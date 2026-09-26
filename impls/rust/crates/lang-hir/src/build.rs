//! AST + 语义产物 → Typed HIR 构建（lowering 第一段）。
//!
//! 前置条件：`analysis` 无诊断（resolver/checker 已通过；与 TS 证据 `hir/build.ts`
//! 的构建前提一致）。构建只**搬运**语义结论（符号/类型），不重新推断。
//!
//! 决策依据（证据）：
//! - 节点分配次序 = TS `hir/build.ts` 的 `n()` 调用时机逐一复刻（NodeId 稳定性契约）；
//!   NodeId 从 1 起先序递增。
//! - 顶层收集序与 TS 相同：struct/enum（声明序交错）→ 顶层 let → 非 method fn
//!   （首个 `main` 记为 main）+ extern → method。
//! - 闭包捕获集（[`HirExpr::FnLit`] 的 `captures`）在本层计算：遍历 fnlit 体，
//!   收集「解析到外层函数局部绑定（Var/Param）的自由名」，排除全局与 fn 名；
//!   嵌套 fnlit 的捕获向外层传播（传递闭包需要外层环境携带）。嵌套**具名** fn
//!   是独立函数边界（与 TS `lower` 的 fresh scope 链一致），捕获不穿越。
//! - 类型事实来源：表达式用 `Analysis::type_of`（checker 逐节点结论）；let/for-in
//!   用符号类型；参数/返回注解经 `lang_semantic::resolve_annotation`（A2 为 HIR
//!   构建暴露的公共入口，需 `&mut Analysis`）；struct 字段/enum 载荷取 TypeTables
//!   def 表（resolver 已静默解析）。

use std::collections::HashSet;

use lang_core::{NodeId, Span, SymbolId, TypeId};
use lang_semantic::{
    Analysis,
    symbols::{NominalRef, SymbolKind},
};
use lang_syntax::{
    AssignOp as AstAssignOp, BinaryOp as AstBinaryOp, Expr, Param, Pat, Stmt, TypeEx,
    UnaryOp as AstUnaryOp,
};
use lang_types::TypeTables;

use crate::hir::{
    AssignOp, BinaryOp, HirAssign, HirEnumCase, HirEnumDecl, HirExpr, HirFieldDecl, HirFn, HirLet,
    HirLit, HirMapItem, HirMatchArm, HirParam, HirPat, HirProg, HirStmt, HirStructDecl, LitKind,
    MatchKind, NodeInfo, UnaryOp,
};
use crate::lower::LowerHir;

/// HIR 构建失败（内部不变量破坏：语义已通过却出现不可能的 AST 形状）。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct BuildError(pub String);

impl std::fmt::Display for BuildError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "HIR 构建内部错误: {}", self.0)
    }
}

impl std::error::Error for BuildError {}

/// AST + 语义产物 → Typed HIR（主入口）。
///
/// 前置条件：`analysis` 由**同一棵** `stmts` 分析而来且无诊断（跨树查询返回 None、
/// 带诊断时事实缺口——两者都只产生占位 HIR，不做补救）。
/// 构建失败即内部不变量破坏（`expect` 崩溃并带定位信息），不是用户错误。
#[must_use]
pub fn build_hir(stmts: &[Stmt], analysis: &mut Analysis) -> HirProg {
    match Builder::new(analysis).build(stmts) {
        Ok(prog) => prog,
        Err(e) => panic!("build_hir: {e}"),
    }
}

/// [`LowerHir`] 的语义驱动实现：`Ast = Vec<Stmt>`（装载器合并后的语句序列）。
pub struct HirBuilder<'a> {
    analysis: &'a mut Analysis,
}

impl<'a> HirBuilder<'a> {
    #[must_use]
    pub fn new(analysis: &'a mut Analysis) -> Self {
        Self { analysis }
    }
}

impl LowerHir for HirBuilder<'_> {
    type Ast = Vec<Stmt>;
    type Error = BuildError;

    fn lower(&mut self, ast: &Self::Ast) -> Result<HirProg, Self::Error> {
        Builder::new(self.analysis).build(ast)
    }
}

impl HirBuilder<'_> {
    /// 切片入口：与 [`LowerHir::lower`] 同一构建管线，免去调用方为
    /// `&[Stmt]` 克隆整棵 AST（compile 便利入口消费）。
    pub fn lower_slice(&mut self, stmts: &[Stmt]) -> Result<HirProg, BuildError> {
        Builder::new(self.analysis).build(stmts)
    }
}

struct Builder<'a> {
    a: &'a mut Analysis,
    /// NodeId 先序递增（从 1 起；与 TS 证据一致）。
    next: u32,
    /// 顶层绑定名（fnlit 捕获排除全局——闭包体经全局槽访问）。
    global_names: HashSet<String>,
    /// 当前函数（fn/fnlit）内的绑定名作用域栈。
    scopes: Vec<Vec<String>>,
    /// 当前函数体内收集的自由名（fnlit 捕获；fn/全局层收集后丢弃）。
    free: Vec<String>,
}

impl<'a> Builder<'a> {
    fn new(analysis: &'a mut Analysis) -> Self {
        Self {
            a: analysis,
            next: 0,
            global_names: HashSet::new(),
            scopes: vec![Vec::new()],
            free: Vec::new(),
        }
    }

    fn node(&mut self, span: Span) -> NodeInfo {
        self.next += 1;
        NodeInfo::new(NodeId(self.next), span)
    }

    fn build(mut self, prog: &[Stmt]) -> Result<HirProg, BuildError> {
        for s in prog {
            if let Stmt::Let { name, .. } = s {
                if name != "_" {
                    self.global_names.insert(name.clone());
                }
            }
        }
        let mut structs = Vec::new();
        let mut enums = Vec::new();
        // 1. struct/enum（声明序交错，与 TS collectDecls 同一循环）
        for s in prog {
            match s {
                Stmt::StructDecl { .. } => structs.push(self.struct_decl(s)?),
                Stmt::EnumDecl { .. } => enums.push(self.enum_decl(s)?),
                _ => {}
            }
        }
        // 2. 顶层绑定
        let mut globals = Vec::new();
        for s in prog {
            if let Stmt::Let { .. } = s {
                globals.push(self.let_stmt(s)?);
            }
        }
        // 3. 顶层 fn（首个 main）+ extern
        let mut fns = Vec::new();
        let mut main: Option<HirFn> = None;
        for s in prog {
            match s {
                Stmt::FnDecl { recv: None, .. } => {
                    let f = self.fn_decl(s)?;
                    if f.name == "main" && main.is_none() {
                        main = Some(f.clone());
                    }
                    fns.push(f);
                }
                Stmt::Extern {
                    host,
                    fns: decls,
                    span: _,
                } => {
                    for d in decls {
                        let mut f = self.fn_decl(d)?;
                        f.is_extern = true;
                        f.extern_host = host.clone();
                        f.body = Vec::new();
                        // resolver 对 extern 内层 FnDecl 不入 decl_syms（仅顶层
                        // 作用域持名）——按名回查 Fn 符号，保住「HIR 携带符号
                        // 结论」契约（extern fn 名顶层唯一，首个命中确定）。
                        if f.meta.sym.is_none() {
                            if let Some(sym) = self
                                .a
                                .symbols
                                .iter()
                                .find(|s| s.kind == SymbolKind::Fn && s.name == f.name)
                                .map(|s| s.id)
                            {
                                f.meta = f.meta.with_sym(sym);
                            }
                        }
                        fns.push(f);
                    }
                }
                _ => {}
            }
        }
        // 4. 方法
        for s in prog {
            if let Stmt::FnDecl { recv: Some(_), .. } = s {
                fns.push(self.fn_decl(s)?);
            }
        }
        Ok(HirProg {
            structs,
            enums,
            globals,
            fns,
            main: main.map(Box::new),
        })
    }

    // —— 查表助手 ——

    fn type_sym_by_name(&self, name: &str) -> Option<SymbolId> {
        self.a.symbols.first_type_named(name)
    }

    fn sym_nominal(&self, sym: SymbolId) -> Option<NominalRef> {
        self.a.symbols.get(sym).nominal
    }

    fn anno(&mut self, t: Option<&TypeEx>, tparams: &[String]) -> Option<TypeId> {
        t.map(|t| lang_semantic::resolve_annotation(self.a, t, tparams))
    }

    // —— 声明 ——

    fn struct_decl(&mut self, s: &Stmt) -> Result<HirStructDecl, BuildError> {
        let Stmt::StructDecl {
            span,
            name,
            type_params,
            fields,
            ..
        } = s
        else {
            return Err(BuildError(format!("struct_decl 期望，得到 {s:?}")));
        };
        let sym = self.type_sym_by_name(name);
        let mut meta = self.node(*span);
        if let Some(sym) = sym {
            meta = meta.with_sym(sym);
        }
        // 字段类型取 def 表（resolver 已按声明序静默解析）
        let def_fields: Vec<(String, Option<TypeId>)> = sym
            .and_then(|sym| self.sym_nominal(sym))
            .map(|n| match n {
                NominalRef::Struct(def) => self
                    .a
                    .types
                    .struct_def(def)
                    .fields
                    .iter()
                    .map(|f| (f.name.clone(), Some(f.ty)))
                    .collect(),
                NominalRef::Enum(_) => Vec::new(),
            })
            .unwrap_or_default();
        let fdecls: Vec<HirFieldDecl> = fields
            .iter()
            .enumerate()
            .map(|(i, f)| HirFieldDecl {
                name: f.name.clone(),
                type_id: def_fields
                    .get(i)
                    .map(|(_, t)| *t)
                    .unwrap_or(None)
                    .or(self.anno(f.typ.as_ref(), type_params)),
            })
            .collect();
        Ok(HirStructDecl {
            meta,
            name: name.clone(),
            type_params: type_params.clone(),
            fields: fdecls,
        })
    }

    fn enum_decl(&mut self, s: &Stmt) -> Result<HirEnumDecl, BuildError> {
        let Stmt::EnumDecl {
            span,
            name,
            type_params,
            cases,
            ..
        } = s
        else {
            return Err(BuildError(format!("enum_decl 期望，得到 {s:?}")));
        };
        let sym = self.type_sym_by_name(name);
        let mut meta = self.node(*span);
        if let Some(sym) = sym {
            meta = meta.with_sym(sym);
        }
        // case 载荷类型取 def 表；case 符号取同 owner 同名构造器（末个命中，与 TS 一致）
        let def_cases: Vec<Vec<TypeId>> = sym
            .and_then(|sym| self.sym_nominal(sym))
            .map(|n| match n {
                NominalRef::Enum(def) => self
                    .a
                    .types
                    .enum_def(def)
                    .cases
                    .iter()
                    .map(|c| c.payloads.clone())
                    .collect(),
                NominalRef::Struct(_) => Vec::new(),
            })
            .unwrap_or_default();
        let cdecls: Vec<HirEnumCase> = cases
            .iter()
            .enumerate()
            .map(|(i, c)| {
                let csym = self
                    .a
                    .symbols
                    .iter()
                    .filter(|s| {
                        s.kind == SymbolKind::Ctor && s.owner_name == *name && s.name == c.name
                    })
                    .map(|s| s.id)
                    .last();
                HirEnumCase {
                    sym: csym,
                    name: c.name.clone(),
                    payloads: def_cases
                        .get(i)
                        .map(|ps| ps.iter().map(|t| Some(*t)).collect())
                        .unwrap_or_else(|| {
                            c.payloads
                                .iter()
                                .map(|p| self.anno(Some(p), type_params))
                                .collect()
                        }),
                }
            })
            .collect();
        Ok(HirEnumDecl {
            meta,
            name: name.clone(),
            type_params: type_params.clone(),
            cases: cdecls,
        })
    }

    fn let_stmt(&mut self, s: &Stmt) -> Result<HirLet, BuildError> {
        let Stmt::Let {
            span,
            name,
            mutable,
            init,
            ..
        } = s
        else {
            return Err(BuildError(format!("let 期望，得到 {s:?}")));
        };
        let sym = self.a.symbol_of_decl(s);
        let mut meta = self.node(*span);
        if let Some(sym) = sym {
            meta = meta.with_sym(sym);
            if let Some(t) = self.a.symbol_type(sym) {
                meta = meta.with_type(t);
            }
        }
        let init = self.expr(init)?;
        Ok(HirLet {
            meta,
            name: name.clone(),
            mutable: *mutable,
            init: Box::new(init),
        })
    }

    fn fn_decl(&mut self, s: &Stmt) -> Result<HirFn, BuildError> {
        let Stmt::FnDecl {
            span,
            name,
            recv,
            type_params,
            params,
            ret,
            body,
        } = s
        else {
            return Err(BuildError(format!("fn_decl 期望，得到 {s:?}")));
        };
        let sym = self.a.symbol_of_decl(s);
        let ret_ty = self.anno(ret.as_ref(), type_params);
        let mut meta = self.node(*span);
        if let Some(sym) = sym {
            meta = meta.with_sym(sym);
        }
        if let Some(t) = ret_ty {
            meta = meta.with_type(t);
        }
        // 函数边界：新作用域栈，自由名丢弃（具名 fn 不捕获）
        let saved_scopes = std::mem::replace(&mut self.scopes, vec![Vec::new()]);
        let saved_free = std::mem::take(&mut self.free);
        let recv_param = recv.as_ref().map(|r| self.param(r, &[]));
        if let Some(rp) = &recv_param {
            self.bind_name(&rp.name);
        }
        let mut fparams = Vec::new();
        for p in params {
            let hp = self.param(p, type_params);
            self.bind_name(&hp.name);
            fparams.push(hp);
        }
        let mut hbody = Vec::new();
        for st in body {
            hbody.push(self.stmt(st)?);
        }
        self.scopes = saved_scopes;
        self.free = saved_free;
        Ok(HirFn {
            meta,
            name: name.clone(),
            recv: recv_param,
            type_params: type_params.clone(),
            params: fparams,
            ret: ret_ty,
            body: hbody,
            is_extern: false,
            extern_host: String::new(),
        })
    }

    fn param(&mut self, p: &Param, tparams: &[String]) -> HirParam {
        HirParam {
            name: p.name.clone(),
            type_id: self.anno(p.typ.as_ref(), tparams),
        }
    }

    // —— 语句 ——

    fn stmt(&mut self, s: &Stmt) -> Result<HirStmt, BuildError> {
        match s {
            Stmt::Let { .. } => Ok(HirStmt::Let(self.let_stmt(s)?)),
            Stmt::Assign {
                span,
                target,
                op,
                value,
            } => {
                let meta = self.node(*span);
                let t = self.assign_target(target)?;
                let rhs = self.expr(value)?;
                Ok(HirStmt::Assign(HirAssign {
                    meta,
                    target: Box::new(t),
                    op: map_assign_op(*op),
                    rhs: Box::new(rhs),
                }))
            }
            Stmt::ExprStmt { span, expr } => {
                let e = self.expr(expr)?;
                let meta = self.node(*span);
                Ok(HirStmt::Eval {
                    meta,
                    expr: Box::new(e),
                })
            }
            Stmt::Return { span, value } => {
                // 与 TS/Go 证据一致：node 先于 val
                let meta = self.node(*span);
                let val = match value {
                    Some(v) => Some(Box::new(self.expr(v)?)),
                    None => None,
                };
                Ok(HirStmt::Return { meta, val })
            }
            Stmt::If {
                span,
                cond,
                then_body,
                else_body,
            } => {
                let meta = self.node(*span);
                let c = self.expr(cond)?;
                let then = self.block(then_body)?;
                let els = match else_body {
                    Some(list) => self.block(list)?,
                    None => Vec::new(),
                };
                Ok(HirStmt::If {
                    meta,
                    cond: Box::new(c),
                    then,
                    els,
                })
            }
            Stmt::While { span, cond, body } => {
                let meta = self.node(*span);
                let c = self.expr(cond)?;
                let b = self.block(body)?;
                Ok(HirStmt::While {
                    meta,
                    cond: Box::new(c),
                    body: b,
                })
            }
            Stmt::ForIn {
                span,
                variable,
                iter,
                body,
            } => {
                let sym = self.a.symbol_of_decl(s);
                let mut meta = self.node(*span);
                if let Some(sym) = sym {
                    meta = meta.with_sym(sym);
                    if let Some(t) = self.a.symbol_type(sym) {
                        meta = meta.with_type(t);
                    }
                }
                let it = self.expr(iter)?;
                self.scopes.push(Vec::new());
                if variable != "_" {
                    self.bind_name(variable);
                }
                let b = self.stmts(body)?;
                self.scopes.pop();
                let var_type = meta.type_id;
                Ok(HirStmt::ForIn {
                    meta,
                    variable: variable.clone(),
                    var_type,
                    iter: Box::new(it),
                    body: b,
                })
            }
            Stmt::Break { span } => Ok(HirStmt::Break {
                meta: self.node(*span),
            }),
            Stmt::Continue { span } => Ok(HirStmt::Continue {
                meta: self.node(*span),
            }),
            Stmt::Go { span, call } => {
                let meta = self.node(*span);
                let Expr::Call { callee, args, .. } = call else {
                    return Err(BuildError("go 目标须为调用（resolver 已约束）".into()));
                };
                let func = match callee.as_ref() {
                    Expr::Ident { .. } => self.a.symbol_of_ref(callee),
                    _ => None,
                };
                let mut hargs = Vec::new();
                for a in args {
                    hargs.push(self.expr(a)?);
                }
                Ok(HirStmt::Spawn {
                    meta,
                    func,
                    args: hargs,
                })
            }
            Stmt::FnDecl { .. } => {
                let meta = self.node(s.span());
                let f = self.fn_decl(s)?;
                Ok(HirStmt::FnStmt {
                    meta,
                    func: Box::new(f),
                })
            }
            // 不可达：结构/枚举/extern/import 由顶层收集处理（函数体内出现已由
            // resolver E2010 拒绝），构建不消费带诊断的程序
            Stmt::StructDecl { span, .. }
            | Stmt::EnumDecl { span, .. }
            | Stmt::Extern { span, .. }
            | Stmt::Import { span, .. } => Err(BuildError(format!(
                "函数体内出现声明语句（span {span:?}）——语义期应已拒绝"
            ))),
        }
    }

    fn stmts(&mut self, list: &[Stmt]) -> Result<Vec<HirStmt>, BuildError> {
        let mut out = Vec::new();
        for s in list {
            out.push(self.stmt(s)?);
        }
        Ok(out)
    }

    fn block(&mut self, list: &[Stmt]) -> Result<Vec<HirStmt>, BuildError> {
        self.scopes.push(Vec::new());
        let out = self.stmts(list)?;
        self.scopes.pop();
        Ok(out)
    }

    fn assign_target(&mut self, t: &Expr) -> Result<HirExpr, BuildError> {
        match t {
            Expr::Ident { span, name } => {
                let sym = self.a.symbol_of_ref(t);
                let mut meta = self.node(*span);
                if let Some(sym) = sym {
                    meta = meta.with_sym(sym);
                    if let Some(ty) = self.a.symbol_type(sym) {
                        meta = meta.with_type(ty);
                    }
                }
                self.note_name(name, sym);
                Ok(HirExpr::LocalRef {
                    meta,
                    name: name.clone(),
                })
            }
            Expr::Field { span, obj, name } => {
                let meta = self.node(*span);
                let o = self.expr(obj)?;
                Ok(HirExpr::Field {
                    meta,
                    obj: Box::new(o),
                    name: name.clone(),
                })
            }
            Expr::Index { span, obj, idx } => {
                let meta = self.node(*span);
                let o = self.expr(obj)?;
                let i = self.expr(idx)?;
                Ok(HirExpr::Index {
                    meta,
                    obj: Box::new(o),
                    idx: Box::new(i),
                })
            }
            other => Err(BuildError(format!(
                "未预期的赋值目标 {other:?}（解析器已约束为 ident/field/index）"
            ))),
        }
    }

    // —— 表达式 ——

    fn expr(&mut self, e: &Expr) -> Result<HirExpr, BuildError> {
        let ty = self.a.type_of(e);
        match e {
            Expr::IntLit { span, value } => Ok(HirExpr::Lit(HirLit {
                meta: with_ty_opt(self.node(*span), ty),
                kind: LitKind::Int,
                i64: *value,
                f64: 0.0,
                s: String::new(),
                b: false,
            })),
            Expr::FloatLit { span, value } => Ok(HirExpr::Lit(HirLit {
                meta: with_ty_opt(self.node(*span), ty),
                kind: LitKind::Float,
                i64: 0,
                f64: *value,
                s: String::new(),
                b: false,
            })),
            Expr::StrLit { span, value } => Ok(HirExpr::Lit(HirLit {
                meta: with_ty_opt(self.node(*span), ty),
                kind: LitKind::Str,
                i64: 0,
                f64: 0.0,
                s: value.clone(),
                b: false,
            })),
            Expr::BoolLit { span, value } => Ok(HirExpr::Lit(HirLit {
                meta: with_ty_opt(self.node(*span), ty),
                kind: LitKind::Bool,
                i64: 0,
                f64: 0.0,
                s: String::new(),
                b: *value,
            })),

            Expr::Ident { span, name } => {
                let mut meta = with_ty_opt(self.node(*span), ty);
                let sym = self.a.symbol_of_ref(e);
                if let Some(sym) = sym {
                    meta = meta.with_sym(sym);
                    match self.a.symbols.get(sym).kind {
                        SymbolKind::Fn => {
                            return Ok(HirExpr::FnRef {
                                meta,
                                name: name.clone(),
                            });
                        }
                        SymbolKind::Ctor => {
                            return Ok(HirExpr::Ctor {
                                meta,
                                ctor_sym: Some(sym),
                                args: Vec::new(),
                            });
                        }
                        _ => {}
                    }
                } else if lang_semantic::builtin::is_builtin_fn(name) {
                    return Ok(HirExpr::BuiltinRef {
                        meta,
                        name: name.clone(),
                    });
                }
                // Var/Param → 局部引用；未定义名（checker 已报）→ LocalRef 占位
                self.note_name(name, sym);
                Ok(HirExpr::LocalRef {
                    meta,
                    name: name.clone(),
                })
            }

            Expr::Unary { span, op, operand } => {
                let meta = with_ty_opt(self.node(*span), ty);
                let x = self.expr(operand)?;
                Ok(HirExpr::Unary {
                    meta,
                    op: map_unary_op(*op),
                    expr: Box::new(x),
                })
            }

            Expr::Binary { span, op, lhs, rhs } => {
                let meta = self.node(*span);
                let l = self.expr(lhs)?;
                let r = self.expr(rhs)?;
                match op {
                    AstBinaryOp::AndAnd => Ok(HirExpr::AndThen {
                        meta: with_ty_opt(meta, Some(TypeTables::BOOL)),
                        lhs: Box::new(l),
                        rhs: Box::new(r),
                    }),
                    AstBinaryOp::OrOr => Ok(HirExpr::OrElse {
                        meta: with_ty_opt(meta, Some(TypeTables::BOOL)),
                        lhs: Box::new(l),
                        rhs: Box::new(r),
                    }),
                    other => Ok(HirExpr::Binary {
                        meta: with_ty_opt(meta, ty),
                        op: map_binary_op(*other),
                        lhs: Box::new(l),
                        rhs: Box::new(r),
                    }),
                }
            }

            Expr::Range { span, lo, hi } => {
                let meta = with_ty_opt(self.node(*span), ty);
                let l = self.expr(lo)?;
                let h = self.expr(hi)?;
                Ok(HirExpr::Range {
                    meta,
                    lo: Box::new(l),
                    hi: Box::new(h),
                })
            }

            Expr::Call { span, callee, args } => self.call(e, *span, callee, args, ty),

            Expr::Index { span, obj, idx } => {
                let meta = with_ty_opt(self.node(*span), ty);
                let o = self.expr(obj)?;
                let i = self.expr(idx)?;
                Ok(HirExpr::Index {
                    meta,
                    obj: Box::new(o),
                    idx: Box::new(i),
                })
            }

            Expr::Slice { span, obj, lo, hi } => {
                let meta = with_ty_opt(self.node(*span), ty);
                let o = self.expr(obj)?;
                let l = self.expr(lo)?;
                let h = self.expr(hi)?;
                Ok(HirExpr::Slice {
                    meta,
                    obj: Box::new(o),
                    lo: Box::new(l),
                    hi: Box::new(h),
                })
            }

            Expr::Field { span, obj, name } => {
                let meta = with_ty_opt(self.node(*span), ty);
                let o = self.expr(obj)?;
                Ok(HirExpr::Field {
                    meta,
                    obj: Box::new(o),
                    name: name.clone(),
                })
            }

            Expr::Try { span, operand } => {
                // 类型事实恢复：checker 对 let/return/赋值顶层的 `?` 走
                // infer_try 直呼（绕过 expr_ty 记录），Try 节点本身无类型结论。
                // 按 infer_try_operand 的同一条规则从操作数的已记录类型恢复
                // Result 的 ok 侧（A2 记录缺口的搬运面补全，非新推断）。
                let ty = ty.or_else(|| {
                    let op_ty = self.a.type_of(operand)?;
                    match self.a.types.data(op_ty) {
                        lang_types::TypeData::Result { ok, .. } => Some(*ok),
                        _ => None,
                    }
                });
                let meta = with_ty_opt(self.node(*span), ty);
                let x = self.expr(operand)?;
                Ok(HirExpr::Try {
                    meta,
                    expr: Box::new(x),
                })
            }

            Expr::StructLit { span, fields, .. } => {
                let mut meta = with_ty_opt(self.node(*span), ty);
                if let Some(sym) = self.a.symbol_of_ref(e) {
                    meta = meta.with_sym(sym);
                }
                let mut fitems = Vec::new();
                for f in fields {
                    let v = self.expr(&f.value)?;
                    fitems.push((f.name.clone(), v));
                }
                let struct_sym = meta.sym;
                Ok(HirExpr::StructLit {
                    meta,
                    struct_sym,
                    fields: fitems,
                })
            }

            Expr::MapLit { span, items } => {
                let meta = with_ty_opt(self.node(*span), ty);
                let mut out = Vec::new();
                for it in items {
                    // 逐对 键先值后（EVAL-LITERAL-004）
                    let k = self.expr(&it.key)?;
                    let v = self.expr(&it.value)?;
                    out.push(HirMapItem { key: k, val: v });
                }
                Ok(HirExpr::MapLit { meta, items: out })
            }

            Expr::ArrLit { span, items } => {
                let meta = with_ty_opt(self.node(*span), ty);
                let mut elems = Vec::new();
                for el in items {
                    elems.push(self.expr(el)?);
                }
                Ok(HirExpr::ArrayLit { meta, elems })
            }

            Expr::Match {
                span,
                scrutinee,
                arms,
            } => {
                let meta = with_ty_opt(self.node(*span), ty);
                let scrut = self.expr(scrutinee)?;
                let kind = self
                    .a
                    .match_kind(e)
                    .unwrap_or(lang_semantic::MatchKind::Literal);
                let kind = match kind {
                    lang_semantic::MatchKind::Literal => MatchKind::Literal,
                    lang_semantic::MatchKind::Enum => MatchKind::Enum,
                    lang_semantic::MatchKind::Result => MatchKind::Result,
                    lang_semantic::MatchKind::Option => MatchKind::Option,
                };
                let mut harms = Vec::new();
                for arm in arms {
                    harms.push(HirMatchArm {
                        pat: self.pat(&arm.pat)?,
                        body: Box::new(self.expr(&arm.body)?),
                    });
                }
                Ok(HirExpr::Match {
                    meta,
                    scrutinee: Box::new(scrut),
                    kind,
                    arms: harms,
                })
            }

            Expr::FnLit {
                span,
                type_params,
                params,
                ret,
                body,
            } => {
                let meta = with_ty_opt(self.node(*span), ty);
                // fnlit 是捕获边界：新作用域栈；自由名即捕获集
                let saved_scopes = std::mem::replace(&mut self.scopes, vec![Vec::new()]);
                let saved_free = std::mem::take(&mut self.free);
                let mut hparams = Vec::new();
                for p in params {
                    let hp = self.param(p, type_params);
                    self.bind_name(&hp.name);
                    hparams.push(hp);
                }
                let hbody = self.stmts(body)?;
                let captures = std::mem::take(&mut self.free);
                self.scopes = saved_scopes;
                self.free = saved_free;
                // 传递捕获：嵌套 fnlit 需要的名字若不在本层绑定，则本层也要捕获
                for n in &captures {
                    if !self.bound(n) && !self.global_names.contains(n) && !self.free.contains(n) {
                        self.free.push(n.clone());
                    }
                }
                let hret = self.anno(ret.as_ref(), type_params);
                Ok(HirExpr::FnLit {
                    meta,
                    type_params: type_params.clone(),
                    params: hparams,
                    ret: hret,
                    body: hbody,
                    captures,
                })
            }
        }
    }

    /// 调用脱糖（TS `call()` 同构）：构造器 / 内建 / 用户 fn / 方法 / 函数值。
    #[allow(clippy::too_many_arguments)]
    fn call(
        &mut self,
        _e: &Expr,
        span: lang_core::Span,
        callee: &Expr,
        args: &[Expr],
        ty: Option<TypeId>,
    ) -> Result<HirExpr, BuildError> {
        if let Expr::Ident { name, .. } = callee {
            let sym = self.a.symbol_of_ref(callee);
            if let Some(sym) = sym {
                let kind = self.a.symbols.get(sym).kind;
                if kind == SymbolKind::Ctor {
                    let meta = self.node(span);
                    let meta = match ty {
                        Some(t) => meta.with_sym(sym).with_type(t),
                        None => meta.with_sym(sym),
                    };
                    let mut hargs = Vec::new();
                    for a in args {
                        hargs.push(self.expr(a)?);
                    }
                    return Ok(HirExpr::Ctor {
                        meta,
                        ctor_sym: Some(sym),
                        args: hargs,
                    });
                }
                if kind == SymbolKind::Fn {
                    let meta = self.node(span);
                    let meta = match ty {
                        Some(t) => meta.with_sym(sym).with_type(t),
                        None => meta.with_sym(sym),
                    };
                    let mut hargs = Vec::new();
                    for a in args {
                        hargs.push(self.expr(a)?);
                    }
                    return Ok(HirExpr::Call {
                        meta,
                        func: Some(sym),
                        builtin: None,
                        callee: None,
                        args: hargs,
                    });
                }
            }
            if sym.is_none() && lang_semantic::builtin::is_builtin_fn(name) {
                let meta = with_ty_opt(self.node(span), ty);
                let mut hargs = Vec::new();
                for a in args {
                    hargs.push(self.expr(a)?);
                }
                return Ok(HirExpr::Call {
                    meta,
                    func: None,
                    builtin: Some(name.clone()),
                    callee: None,
                    args: hargs,
                });
            }
        }
        if let Expr::Field {
            span: fe_span,
            obj,
            name,
        } = callee
        {
            let recv = self.expr(obj)?;
            let recv_ty = crate::hir::type_of(&recv);
            let derefed = recv_ty
                .map(|t| lang_types::ops::deref(&self.a.types, t))
                .unwrap_or(TypeTables::UNKNOWN);
            let mut method = None;
            if let lang_types::TypeData::Struct(def) = self.a.types.data(derefed) {
                // struct def → 首个声明符号（arena 创建序，与 checker 同构）
                let st = self
                    .a
                    .symbols
                    .iter()
                    .find(|s| s.nominal == Some(NominalRef::Struct(*def)))
                    .map(|s| s.id);
                if let Some(st) = st {
                    method = self.a.symbols.get(st).methods.get(name).copied();
                }
            }
            let mut meta = self.node(span);
            if let Some(m) = method {
                meta = meta.with_sym(m);
            }
            if let Some(t) = ty {
                meta = meta.with_type(t);
            }
            let mut hargs = Vec::new();
            for a in args {
                hargs.push(self.expr(a)?);
            }
            return Ok(HirExpr::MethodCall {
                meta,
                recv: Box::new(recv),
                method,
                field: if method.is_none() {
                    Some(name.clone())
                } else {
                    None
                },
                args: hargs,
                name_span: method_name_span(*fe_span, name),
            });
        }
        // 表达式 callee（函数值）
        let meta = with_ty_opt(self.node(span), ty);
        let c = self.expr(callee)?;
        let mut hargs = Vec::new();
        for a in args {
            hargs.push(self.expr(a)?);
        }
        Ok(HirExpr::Call {
            meta,
            func: None,
            builtin: None,
            callee: Some(Box::new(c)),
            args: hargs,
        })
    }

    // —— 模式 ——

    fn pat(&mut self, p: &Pat) -> Result<HirPat, BuildError> {
        match p {
            Pat::Wild { .. } => Ok(HirPat::Wild),
            Pat::Bind { name, .. } => Ok(HirPat::Bind {
                name: name.clone(),
                type_id: self.a.pat_type(p),
            }),
            Pat::Lit { value, .. } => {
                let e = self.expr(value)?;
                match e {
                    HirExpr::Lit(l) => Ok(HirPat::Lit { lit: Box::new(l) }),
                    other => Err(BuildError(format!(
                        "字面量模式期望字面量，得到 {other:?}（解析器已约束）"
                    ))),
                }
            }
            Pat::Case { subs, .. } => {
                let ctor_sym = self.a.symbol_of_pat(p);
                let mut hsubs = Vec::new();
                for sp in subs {
                    hsubs.push(self.pat(sp)?);
                }
                Ok(HirPat::Ctor {
                    ctor_sym,
                    subs: hsubs,
                })
            }
        }
    }

    // —— 捕获簿记 ——

    fn bound(&self, name: &str) -> bool {
        self.scopes.iter().any(|s| s.iter().any(|n| n == name))
    }

    fn bind_name(&mut self, name: &str) {
        if let Some(top) = self.scopes.last_mut() {
            top.push(name.to_string());
        }
    }

    /// 自由名登记：解析到外层函数局部（Var/Param）且不在当前函数作用域、
    /// 非全局 → 记入当前 fnlit 捕获集（首次出现序）。
    fn note_name(&mut self, name: &str, sym: Option<SymbolId>) {
        let kind = sym.map(|s| self.a.symbols.get(s).kind);
        let local_like = matches!(kind, Some(SymbolKind::Var) | Some(SymbolKind::Param));
        if !local_like || self.bound(name) || self.global_names.contains(name) {
            return;
        }
        if !self.free.contains(&name.to_string()) {
            self.free.push(name.to_string());
        }
    }
}

fn with_ty_opt(meta: NodeInfo, ty: Option<TypeId>) -> NodeInfo {
    match ty {
        Some(t) => meta.with_type(t),
        None => meta,
    }
}

fn map_unary_op(op: AstUnaryOp) -> UnaryOp {
    match op {
        AstUnaryOp::Neg => UnaryOp::Neg,
        AstUnaryOp::Not => UnaryOp::Not,
        AstUnaryOp::Ref => UnaryOp::Ref,
    }
}

fn map_binary_op(op: AstBinaryOp) -> BinaryOp {
    match op {
        AstBinaryOp::Eq => BinaryOp::Eq,
        AstBinaryOp::Ne => BinaryOp::Ne,
        AstBinaryOp::Lt => BinaryOp::Lt,
        AstBinaryOp::Le => BinaryOp::Le,
        AstBinaryOp::Gt => BinaryOp::Gt,
        AstBinaryOp::Ge => BinaryOp::Ge,
        AstBinaryOp::Add => BinaryOp::Add,
        AstBinaryOp::Sub => BinaryOp::Sub,
        AstBinaryOp::Mul => BinaryOp::Mul,
        AstBinaryOp::Div => BinaryOp::Div,
        AstBinaryOp::Rem => BinaryOp::Rem,
        AstBinaryOp::BitAnd => BinaryOp::BitAnd,
        AstBinaryOp::BitOr => BinaryOp::BitOr,
        AstBinaryOp::BitXor => BinaryOp::BitXor,
        AstBinaryOp::Shl => BinaryOp::Shl,
        AstBinaryOp::Shr => BinaryOp::Shr,
        AstBinaryOp::AndAnd | AstBinaryOp::OrOr => {
            unreachable!("AndAnd/OrOr 已在调用点降为 AndThen/OrElse")
        }
    }
}

fn map_assign_op(op: AstAssignOp) -> AssignOp {
    match op {
        AstAssignOp::Assign => AssignOp::Assign,
        AstAssignOp::AddAssign => AssignOp::AddAssign,
        AstAssignOp::SubAssign => AssignOp::SubAssign,
        AstAssignOp::MulAssign => AssignOp::MulAssign,
        AstAssignOp::DivAssign => AssignOp::DivAssign,
    }
}

/// 方法名的 token 区间（fe.span 终点回退名字码点数；rename 消费）。
fn method_name_span(fe_span: lang_core::Span, name: &str) -> lang_core::Span {
    let n = name.chars().count() as u32;
    let end = fe_span.end;
    let start = lang_core::Position::new(
        end.offset.saturating_sub(n),
        end.line,
        end.column.saturating_sub(n),
    );
    lang_core::Span::new(fe_span.source, start, end)
}
