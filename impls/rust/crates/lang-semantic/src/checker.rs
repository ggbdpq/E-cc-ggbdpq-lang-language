//! 类型检查器：对 AST 做语义类型检查，产出每表达式/绑定的类型（HIR/IR/后端共用）。
//!
//! 遍历结构与 resolver 同构（顶层 let → fn 体 → 方法体 → 游离语句）；名字解析结果
//! 复用 [`ResolveOutput`]，名义 def 表由 resolver 移交本检查器续用（单一实例）。
//! 本检查器收编转译器的全部前置约束：参数注解（E3007）、`?` 位置与约束
//! （E3005/E3008）、Result 函数 return 形态（E3006/E3001）、match 分类与穷尽性
//! （E4001/E4002/E4003）。
//!
//! 与证据的两处刻意分歧（均按 spec 修正，登记 status）：
//! 1. 赋值/表达式语句顶层 `?` 走 `infer_try`（RESULT-TRY-003 允许，ADR-0005 及其
//!    2026-09-27 扩展；证据实现误报 E3005，
//!    但 Go interp 的 `?` 是通用表达式求值，spec 与运行时一致）；
//! 2. 块内 let/循环变量/臂绑定写入当前块作用域而非全局层
//!    （NAME-SCOPE-001/MATCH-BIND-003 的静态作用域语义；证据写全局层属历史行为）。

use std::collections::{BTreeMap, HashMap};

use lang_core::{Diagnostic, DiagnosticCode, Span, SymbolId, TypeId};
use lang_syntax::{AssignOp, BinaryOp, Expr, Pat, Stmt, TypeEx, UnaryOp};
use lang_types::{
    ERROR_MSG_FIELD, StructDefId, TypeData, TypeTables, assignable, deref, equal, is_concrete,
    is_numeric, option_elem, result_err, result_ok, type_str,
};

use crate::anno::{TypeCtx, resolve_anno_emit, resolve_anno_quiet};
use crate::builtin::{builtin_return, is_builtin_fn};
use crate::resolver::{ResolveOutput, expr_key, pat_key, stmt_key};
use crate::symbols::{NominalRef, SymbolArena, SymbolKind};

/// match 的语义分类（checker 判定，HIR/IR/后端直接消费结论）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum MatchKind {
    /// 字面量 match（int/string/bool）。
    Literal,
    /// enum match（编译期穷尽检查）。
    Enum,
    /// Result match（两臂齐全检查）。
    Result,
    /// Option match（两臂齐全检查）。
    Option,
}

/// 检查产物（对外经 [`crate::Analysis`] 查询视图暴露）。
pub(crate) struct CheckOutput {
    pub expr_ty: HashMap<*const Expr, TypeId>,
    pub pat_ty: HashMap<*const Pat, TypeId>,
    pub sym_ty: HashMap<SymbolId, TypeId>,
    pub fn_ty: HashMap<SymbolId, TypeId>,
    pub match_kind: HashMap<*const Expr, MatchKind>,
    pub tables: TypeTables,
    pub diags: Vec<Diagnostic>,
}

pub(crate) struct Checker<'a> {
    res: &'a ResolveOutput,
    tables: TypeTables,
    expr_ty: HashMap<*const Expr, TypeId>,
    pat_ty: HashMap<*const Pat, TypeId>,
    sym_ty: HashMap<SymbolId, TypeId>,
    fn_ty: HashMap<SymbolId, TypeId>,
    match_kind: HashMap<*const Expr, MatchKind>,
    diags: Vec<Diagnostic>,
    /// 绑定类型作用域栈（名字 → 类型）。
    scopes: Vec<BTreeMap<String, TypeId>>,
    /// 当前函数返回注解（None = 无注解）。
    fn_ret: Option<TypeId>,
    /// 当前函数的泛型参数名集（注解解析用）。
    tps: Vec<String>,
    in_fn: bool,
}

impl<'a> TypeCtx for Checker<'a> {
    fn tables_mut(&mut self) -> &mut TypeTables {
        &mut self.tables
    }

    fn arena(&self) -> &SymbolArena {
        &self.res.arena
    }

    fn lookup_named(&self, name: &str) -> Option<SymbolId> {
        self.lookup_type_sym(name)
    }

    fn emit(&mut self, code: DiagnosticCode, msg: String) {
        self.diags.push(Diagnostic::new(code, msg, None));
    }
}

/// 复合赋值/二元运算的诊断名（证据 opName：仅认识赋值系，其余一律 "?"）。
fn op_name(op: &str) -> &str {
    match op {
        "+=" | "-=" | "*=" | "/=" | "=" => op,
        _ => "?",
    }
}

impl<'a> Checker<'a> {
    /// 接管 resolver 的 def 表（名义身份的单一实例随 Checker 流转到产物）。
    pub(crate) fn new(res: &'a mut ResolveOutput) -> Self {
        let tables = std::mem::replace(&mut res.tables, TypeTables::new());
        Self {
            res,
            tables,
            expr_ty: HashMap::new(),
            pat_ty: HashMap::new(),
            sym_ty: HashMap::new(),
            fn_ty: HashMap::new(),
            match_kind: HashMap::new(),
            diags: Vec::new(),
            scopes: Vec::new(),
            fn_ret: None,
            tps: Vec::new(),
            in_fn: false,
        }
    }

    fn err(&mut self, span: Span, code: DiagnosticCode, msg: impl Into<String>) {
        self.diags.push(Diagnostic::new(
            code,
            msg,
            Some(Diagnostic::at_line(span.source, span.start.line)),
        ));
    }

    fn lookup_type_sym(&self, name: &str) -> Option<SymbolId> {
        if let Some(&id) = self.res.builtin.get(name) {
            if self.res.arena.get(id).kind == SymbolKind::Struct {
                return Some(id);
            }
        }
        if let Some(&id) = self.res.top_types.get(name) {
            return Some(id);
        }
        self.res.arena.first_type_named(name)
    }

    fn push_scope(&mut self) {
        self.scopes.push(BTreeMap::new());
    }

    fn pop_scope(&mut self) {
        self.scopes.pop();
    }

    fn define_type(&mut self, name: &str, ty: TypeId) {
        if let Some(top) = self.scopes.last_mut() {
            top.insert(name.to_string(), ty);
        }
    }

    fn lookup_var(&self, name: &str) -> Option<TypeId> {
        for scope in self.scopes.iter().rev() {
            if let Some(&t) = scope.get(name) {
                return Some(t);
            }
        }
        None
    }

    // ———— 主驱动（与 resolver 同构的遍历）————

    pub(crate) fn check(mut self, prog: &[Stmt]) -> CheckOutput {
        self.walk(prog);
        CheckOutput {
            expr_ty: self.expr_ty,
            pat_ty: self.pat_ty,
            sym_ty: self.sym_ty,
            fn_ty: self.fn_ty,
            match_kind: self.match_kind,
            tables: self.tables,
            diags: self.diags,
        }
    }

    fn walk(&mut self, prog: &[Stmt]) {
        self.push_scope();
        // 1. 顶层绑定依序（前向引用已被 resolver 拦）
        for s in prog {
            if matches!(s, Stmt::Let { .. }) {
                self.let_stmt(s);
            }
        }
        // 2. fn 体
        for s in prog {
            if let Stmt::FnDecl { recv: None, .. } = s {
                let sym = self.res.decl_syms.get(&stmt_key(s)).copied();
                self.fn_body(s, sym, None);
            }
        }
        // 3. 方法体
        for s in prog {
            if let Stmt::FnDecl { recv: Some(_), .. } = s {
                let sym = self.method_sym(s);
                let recv_ty = self.method_struct_type(s);
                self.fn_body(s, sym, Some(recv_ty));
            }
        }
        // 4. 顶层游离语句（脚本模式；interp 可执行，转译不支持——只做类型走查）
        for s in prog {
            match s {
                Stmt::Let { .. }
                | Stmt::FnDecl { .. }
                | Stmt::StructDecl { .. }
                | Stmt::EnumDecl { .. }
                | Stmt::Extern { .. }
                | Stmt::Import { .. } => {}
                other => self.stmt(other),
            }
        }
        self.pop_scope();
    }

    fn method_sym(&self, d: &Stmt) -> Option<SymbolId> {
        let Stmt::FnDecl {
            name,
            recv: Some(recv),
            ..
        } = d
        else {
            return None;
        };
        let Some(TypeEx::Named { name: rt, .. }) = &recv.typ else {
            return None;
        };
        let st = self.lookup_type_sym(rt)?;
        self.res.arena.get(st).methods.get(name).copied()
    }

    fn method_struct_type(&mut self, d: &Stmt) -> TypeId {
        let Stmt::FnDecl {
            recv: Some(recv), ..
        } = d
        else {
            return TypeTables::UNKNOWN;
        };
        let Some(TypeEx::Named { name: rt, .. }) = &recv.typ else {
            return TypeTables::UNKNOWN;
        };
        match self.lookup_type_sym(rt) {
            Some(st) => match self.res.arena.get(st).nominal {
                Some(NominalRef::Struct(def)) => self.tables.intern(TypeData::Struct(def)),
                _ => TypeTables::UNKNOWN,
            },
            None => TypeTables::UNKNOWN,
        }
    }

    /// 函数体检查：建立返回上下文；参数注解缺失 → E3007。
    fn fn_body(&mut self, d: &Stmt, sym: Option<SymbolId>, recv_ty: Option<TypeId>) {
        let Stmt::FnDecl {
            recv,
            type_params,
            ret,
            body,
            ..
        } = d
        else {
            return;
        };
        let saved_scopes = std::mem::replace(&mut self.scopes, vec![BTreeMap::new()]);
        let saved_tps = std::mem::replace(&mut self.tps, type_params.clone());
        let saved_ret = self.fn_ret;
        let saved_in_fn = self.in_fn;
        let sig = self.fn_signature_of_decl(d);
        let sig_ret = match self.tables.data(sig) {
            TypeData::Fn { ret, .. } => *ret,
            _ => TypeTables::UNKNOWN,
        };
        self.fn_ret = ret.as_ref().map(|_| sig_ret); // 注解只解析一次，错误不重复报
        self.in_fn = true;

        if let Some(recv) = recv {
            self.define_type(&recv.name, recv_ty.unwrap_or(TypeTables::UNKNOWN));
        }
        self.declare_params(d, sym, recv_ty);
        if let Some(sym) = sym {
            self.fn_ty.insert(sym, sig);
        }
        for s in body {
            self.stmt(s);
        }
        self.scopes = saved_scopes;
        self.tps = saved_tps;
        self.fn_ret = saved_ret;
        self.in_fn = saved_in_fn;
    }

    /// 解析参数注解（缺注解 → E3007 + Unknown）入作用域，并把类型按序落到参数符号。
    fn declare_params(&mut self, d: &Stmt, sym: Option<SymbolId>, recv_ty: Option<TypeId>) {
        let Stmt::FnDecl {
            type_params,
            recv,
            params,
            ..
        } = d
        else {
            return;
        };
        let psyms: Vec<SymbolId> = sym
            .and_then(|s| self.res.fn_params.get(&s).cloned())
            .unwrap_or_default();
        let mut pi = 0;
        if recv.is_some() && pi < psyms.len() {
            self.sym_ty
                .insert(psyms[pi], recv_ty.unwrap_or(TypeTables::UNKNOWN));
            pi += 1;
        }
        let tps = type_params.clone();
        for p in params {
            let pt = match &p.typ {
                None => {
                    self.err(
                        d.span(),
                        DiagnosticCode::E3007,
                        format!("参数 {} 缺少类型注解", p.name),
                    );
                    TypeTables::UNKNOWN
                }
                Some(t) => resolve_anno_emit(self, t, &tps),
            };
            self.define_type(&p.name, pt);
            if p.name != "_" && pi < psyms.len() {
                self.sym_ty.insert(psyms[pi], pt);
                pi += 1;
            }
        }
    }

    /// fn/方法的函数类型（参数注解缺失 → Unknown 占位）。
    fn fn_signature_of_decl(&mut self, d: &Stmt) -> TypeId {
        let Stmt::FnDecl {
            type_params,
            params,
            ret,
            ..
        } = d
        else {
            return TypeTables::UNKNOWN;
        };
        let tps = type_params.clone();
        let mut ps = Vec::new();
        for p in params {
            ps.push(match &p.typ {
                None => TypeTables::UNKNOWN,
                Some(t) => resolve_anno_emit(self, t, &tps),
            });
        }
        let r = match ret {
            None => TypeTables::UNKNOWN,
            Some(t) => resolve_anno_emit(self, t, &tps),
        };
        self.tables.intern(TypeData::Fn { params: ps, ret: r })
    }

    /// 符号侧的函数类型（extern/未走查体的 fn 用）。
    fn fn_signature_of_symbol(&mut self, sym: SymbolId) -> TypeId {
        let s = self.res.arena.get(sym);
        let annos = s.param_annos.clone();
        let ret = s.type_ex.clone();
        let tps = s.type_params.clone();
        let mut ps = Vec::new();
        for a in &annos {
            ps.push(match a {
                None => TypeTables::UNKNOWN,
                Some(t) => resolve_anno_emit(self, t, &tps),
            });
        }
        let r = match &ret {
            None => TypeTables::UNKNOWN,
            Some(t) => resolve_anno_emit(self, t, &tps),
        };
        self.tables.intern(TypeData::Fn { params: ps, ret: r })
    }

    // ———— 语句 ————

    fn stmt(&mut self, s: &Stmt) {
        match s {
            Stmt::Let { .. } => self.let_stmt(s),

            Stmt::Assign { .. } => self.assign(s),

            Stmt::ExprStmt { expr, .. } => {
                // ADR-0005 扩展（2026-09-27）：表达式语句位置的顶层 ? 合法，
                // 走与 let/return/赋值相同的传播检查
                match expr {
                    Expr::Try { .. } => {
                        self.infer_try(expr);
                    }
                    _ => {
                        self.infer(expr);
                    }
                }
            }

            Stmt::Return { .. } => self.return_stmt(s),

            Stmt::If {
                cond,
                then_body,
                else_body,
                ..
            } => {
                self.require_bool(cond, "if 条件");
                self.block(then_body);
                if let Some(els) = else_body {
                    self.block(els);
                }
            }

            Stmt::While { cond, body, .. } => {
                self.require_bool(cond, "while 条件");
                self.block(body);
            }

            Stmt::ForIn {
                span,
                variable,
                iter,
                body,
                ..
            } => {
                let ty = self.infer(iter);
                let elem = self.for_in_elem(ty, *span);
                if let Some(&sym) = self.res.decl_syms.get(&stmt_key(s)) {
                    self.sym_ty.insert(sym, elem);
                }
                self.push_scope();
                self.define_type(variable, elem);
                for b in body {
                    self.stmt(b);
                }
                self.pop_scope();
            }

            Stmt::Go { call, .. } => {
                self.infer_call(call);
            }

            Stmt::FnDecl { recv: None, .. } => {
                let sym = self.res.decl_syms.get(&stmt_key(s)).copied();
                self.fn_body(s, sym, None);
            }

            // 嵌套类型声明由 resolver 报 E2010；break/continue 位置由 resolver 管
            Stmt::FnDecl { recv: Some(_), .. }
            | Stmt::StructDecl { .. }
            | Stmt::EnumDecl { .. }
            | Stmt::Break { .. }
            | Stmt::Continue { .. }
            | Stmt::Import { .. }
            | Stmt::Extern { .. } => {}
        }
    }

    /// for-in 迭代协议（CTRL-FORIN-003）：不可迭代 → E3010。
    fn for_in_elem(&mut self, ty: TypeId, span: Span) -> TypeId {
        match self.tables.data(ty) {
            TypeData::Slice(elem) => *elem,
            TypeData::Map { key, .. } => *key,
            TypeData::Str => TypeTables::STR,
            TypeData::Range => TypeTables::INT,
            _ if !is_concrete(&self.tables, ty) => TypeTables::UNKNOWN,
            _ => {
                let ts = type_str(&self.tables, ty);
                self.err(span, DiagnosticCode::E3010, format!("{ts} 不可迭代"));
                TypeTables::UNKNOWN
            }
        }
    }

    fn block(&mut self, list: &[Stmt]) {
        self.push_scope();
        for s in list {
            self.stmt(s);
        }
        self.pop_scope();
    }

    fn let_stmt(&mut self, s: &Stmt) {
        let Stmt::Let {
            span,
            name,
            typ,
            init,
            ..
        } = s
        else {
            return;
        };
        let mut ty = match init {
            Expr::Try { .. } => self.infer_try(init),
            _ => self.infer(init),
        };
        if let Some(anno_ex) = typ {
            let tps = self.tps.clone();
            let anno = resolve_anno_emit(self, anno_ex, &tps);
            if !assignable(&self.tables, anno, ty) {
                let vt = type_str(&self.tables, ty);
                let at = type_str(&self.tables, anno);
                self.err(
                    *span,
                    DiagnosticCode::E3001,
                    format!("不能把 {vt} 绑定为注解类型 {at}"),
                );
            }
            ty = anno;
        }
        if let Some(&sym) = self.res.decl_syms.get(&stmt_key(s)) {
            self.sym_ty.insert(sym, ty);
        }
        self.define_type(name, ty);
    }

    fn assign(&mut self, s: &Stmt) {
        let Stmt::Assign {
            span,
            target,
            op,
            value,
        } = s
        else {
            return;
        };
        // SPEC 修正（RESULT-TRY-003）：赋值顶层 `?` 合法（证据误报 E3005）
        let val_ty = match value {
            Expr::Try { .. } => self.infer_try(value),
            _ => self.infer(value),
        };
        match target {
            Expr::Ident { name, .. } => {
                let tgt_ty = self.lookup_var(name).unwrap_or(TypeTables::UNKNOWN);
                if *op != AssignOp::Assign {
                    self.check_compound(*span, *op, tgt_ty, val_ty);
                    return;
                }
                if !assignable(&self.tables, tgt_ty, val_ty) {
                    let vt = type_str(&self.tables, val_ty);
                    let tt = type_str(&self.tables, tgt_ty);
                    self.err(
                        *span,
                        DiagnosticCode::E3001,
                        format!("不能把 {vt} 赋给 {tt}"),
                    );
                }
            }
            Expr::Field { obj, name, .. } => {
                let obj_ty = self.infer(obj);
                let f_ty = self.field_type(obj_ty, name, target.span());
                if *op != AssignOp::Assign {
                    self.check_compound(*span, *op, f_ty, val_ty);
                    return;
                }
                if !assignable(&self.tables, f_ty, val_ty) {
                    let vt = type_str(&self.tables, val_ty);
                    let ft = type_str(&self.tables, f_ty);
                    self.err(
                        *span,
                        DiagnosticCode::E3001,
                        format!("不能把 {vt} 赋给字段 {name}（{ft}）"),
                    );
                }
            }
            Expr::Index { obj, idx, .. } => {
                let obj_ty = self.infer(obj);
                let idx_ty = self.infer(idx);
                self.check_index_key(obj_ty, idx_ty, target.span());
                let elem_ty = self.index_elem_ty(obj_ty, target.span());
                if *op != AssignOp::Assign {
                    self.check_compound(*span, *op, elem_ty, val_ty);
                    return;
                }
                if !assignable(&self.tables, elem_ty, val_ty) {
                    let vt = type_str(&self.tables, val_ty);
                    let et = type_str(&self.tables, elem_ty);
                    self.err(
                        *span,
                        DiagnosticCode::E3001,
                        format!("不能把 {vt} 赋给容器元素（{et}）"),
                    );
                }
            }
            _ => {}
        }
    }

    /// 复合赋值（+= -= *= /=）：同型数值/字符串；数组 += 数组。
    fn check_compound(&mut self, span: Span, op: AssignOp, tgt_ty: TypeId, val_ty: TypeId) {
        if !is_concrete(&self.tables, tgt_ty) || !is_concrete(&self.tables, val_ty) {
            return;
        }
        let op_s = match op {
            AssignOp::Assign => "=",
            AssignOp::AddAssign => "+=",
            AssignOp::SubAssign => "-=",
            AssignOp::MulAssign => "*=",
            AssignOp::DivAssign => "/=",
        };
        if matches!(self.tables.data(tgt_ty), TypeData::Slice(_)) {
            if op == AssignOp::AddAssign && matches!(self.tables.data(val_ty), TypeData::Slice(_)) {
                return;
            }
            let vt = type_str(&self.tables, val_ty);
            self.err(
                span,
                DiagnosticCode::E3001,
                format!("数组 {op_s} 需要数组右值，得到 {vt}"),
            );
            return;
        }
        let same_kind = std::mem::discriminant(self.tables.data(tgt_ty))
            == std::mem::discriminant(self.tables.data(val_ty));
        if same_kind
            && (is_numeric(&self.tables, tgt_ty)
                || matches!(self.tables.data(tgt_ty), TypeData::Str))
        {
            return;
        }
        let tt = type_str(&self.tables, tgt_ty);
        let vt = type_str(&self.tables, val_ty);
        self.err(
            span,
            DiagnosticCode::E3002,
            format!("{tt} 与 {vt} 不可做 {}（无隐式转换）", op_name(op_s)),
        );
    }

    fn return_stmt(&mut self, s: &Stmt) {
        let Stmt::Return { span, value } = s else {
            return;
        };
        if !self.in_fn {
            return; // resolver 已报 E2008
        }
        let Some(val) = value else {
            match self.fn_ret {
                Some(r) if matches!(self.tables.data(r), TypeData::Result { .. }) => {
                    self.err(
                        *span,
                        DiagnosticCode::E3006,
                        "Result 函数的 return 须为 Ok(...) 或 Err(...)",
                    );
                }
                Some(r) if is_concrete(&self.tables, r) => {
                    let ts = type_str(&self.tables, r);
                    self.err(
                        *span,
                        DiagnosticCode::E3001,
                        format!("注解为 {ts} 的函数不能裸 return"),
                    );
                }
                _ => {}
            }
            return;
        };
        if matches!(val, Expr::Try { .. }) {
            self.infer_try(val);
            return;
        }
        let ret = self.fn_ret;
        if let Some(r) = ret {
            if matches!(self.tables.data(r), TypeData::Result { .. }) {
                self.infer_result_return(*span, val, r);
                return;
            }
        }
        let val_ty = self.infer(val);
        if let Some(r) = ret {
            if is_concrete(&self.tables, r) && !assignable(&self.tables, r, val_ty) {
                let vt = type_str(&self.tables, val_ty);
                let rt = type_str(&self.tables, r);
                self.err(
                    *span,
                    DiagnosticCode::E3001,
                    format!("返回 {vt} 与注解 {rt} 不匹配"),
                );
            }
        }
    }

    fn infer_result_return(&mut self, span: Span, call: &Expr, ret: TypeId) {
        let Expr::Call { callee, args, .. } = call else {
            self.err(
                span,
                DiagnosticCode::E3006,
                "Result 函数的 return 须为 Ok(...) 或 Err(...)",
            );
            self.infer(call); // 证据同款：非 Call 值仍走推断（内嵌 ? 的 E3005 在此发出）
            return;
        };
        let Expr::Ident { name, .. } = callee.as_ref() else {
            self.err(
                span,
                DiagnosticCode::E3006,
                "Result 函数的 return 须为 Ok(...) 或 Err(...)",
            );
            self.infer(call);
            return;
        };
        if (name != "Ok" && name != "Err") || args.len() != 1 {
            self.err(
                span,
                DiagnosticCode::E3006,
                "Result 函数的 return 须为 Ok(...) 或 Err(...)",
            );
            self.infer(call);
            return;
        }
        let arg_ty = self.infer(&args[0]);
        if name == "Ok" {
            if !assignable(&self.tables, result_ok(&self.tables, ret), arg_ty) {
                let at = type_str(&self.tables, arg_ty);
                let rt = type_str(&self.tables, ret);
                self.err(
                    span,
                    DiagnosticCode::E3001,
                    format!("Ok({at}) 与返回类型 {rt} 不匹配"),
                );
            }
        } else if !assignable(&self.tables, result_err(&self.tables, ret), arg_ty) {
            let at = type_str(&self.tables, arg_ty);
            let rt = type_str(&self.tables, ret);
            self.err(
                span,
                DiagnosticCode::E3001,
                format!("Err({at}) 与返回类型 {rt} 不匹配"),
            );
        }
        self.expr_ty.insert(expr_key(call), ret);
    }

    // ———— 推断主入口 ————

    /// 推断并记录表达式类型（memo 化；同节点诊断不重复发射）。
    fn infer(&mut self, e: &Expr) -> TypeId {
        let key = expr_key(e);
        if let Some(&t) = self.expr_ty.get(&key) {
            return t;
        }
        let t = self.compute(e);
        self.expr_ty.insert(key, t);
        t
    }

    fn compute(&mut self, e: &Expr) -> TypeId {
        match e {
            Expr::IntLit { .. } => TypeTables::INT,
            Expr::FloatLit { .. } => TypeTables::FLOAT,
            Expr::StrLit { .. } => TypeTables::STR,
            Expr::BoolLit { .. } => TypeTables::BOOL,

            Expr::Ident { .. } => self.ident_type(e),

            Expr::Unary { .. } => self.infer_unary(e),

            Expr::Binary { .. } => self.infer_binary(e),

            Expr::Range { lo, hi, span, .. } => {
                let lo_ty = self.infer(lo);
                let hi_ty = self.infer(hi);
                let bad = (is_concrete(&self.tables, lo_ty)
                    && self.tables.data(lo_ty) != &TypeData::Int)
                    || (is_concrete(&self.tables, hi_ty)
                        && self.tables.data(hi_ty) != &TypeData::Int);
                if bad {
                    let ls = type_str(&self.tables, lo_ty);
                    let hs = type_str(&self.tables, hi_ty);
                    self.err(
                        *span,
                        DiagnosticCode::E3001,
                        format!("区间两端须为 int，得到 {ls}..{hs}"),
                    );
                    return TypeTables::INVALID;
                }
                TypeTables::RANGE
            }

            Expr::Call { .. } => self.infer_call(e),

            Expr::Index { obj, idx, span, .. } => {
                let obj_ty = self.infer(obj);
                let idx_ty = self.infer(idx);
                self.check_index_key(obj_ty, idx_ty, *span);
                self.index_elem_ty(obj_ty, *span)
            }

            Expr::Slice {
                obj, lo, hi, span, ..
            } => {
                let raw_obj = self.infer(obj);
                let obj_ty = deref(&self.tables, raw_obj);
                let lo_ty = self.infer(lo);
                let hi_ty = self.infer(hi);
                for t in [lo_ty, hi_ty] {
                    if is_concrete(&self.tables, t) && self.tables.data(t) != &TypeData::Int {
                        let ts = type_str(&self.tables, t);
                        self.err(
                            *span,
                            DiagnosticCode::E3013,
                            format!("切片端点须为 int，得到 {ts}"),
                        );
                    }
                }
                match self.tables.data(obj_ty) {
                    TypeData::Slice(_) => obj_ty,
                    TypeData::Str => TypeTables::STR,
                    TypeData::Any | TypeData::Unknown | TypeData::Invalid => TypeTables::UNKNOWN,
                    _ => {
                        let ts = type_str(&self.tables, obj_ty);
                        self.err(*span, DiagnosticCode::E3001, format!("{ts} 不可切片"));
                        TypeTables::INVALID
                    }
                }
            }

            Expr::Field { obj, name, .. } => {
                let obj_ty = self.infer(obj);
                self.field_type(obj_ty, name, e.span())
            }

            Expr::StructLit { .. } => self.infer_struct_lit(e),

            Expr::MapLit { .. } => self.infer_map_lit(e),

            Expr::ArrLit { .. } => self.infer_arr_lit(e),

            Expr::Match { .. } => self.infer_match(e),

            Expr::FnLit { .. } => self.infer_fn_lit(e),

            Expr::Try { span, .. } => {
                // 非语句顶层位置（let/return/赋值/表达式语句已在语句层处理）
                self.err(
                    *span,
                    DiagnosticCode::E3005,
                    "? 只能出现在 let/return/赋值/表达式语句的顶层",
                );
                self.infer_try_operand(e)
            }
        }
    }

    /// Ident 类型：作用域栈（参数/绑定/臂捕获）优先，再走符号路径。
    fn ident_type(&mut self, e: &Expr) -> TypeId {
        let Expr::Ident { span, name } = e else {
            return TypeTables::UNKNOWN;
        };
        if let Some(t) = self.lookup_var(name) {
            if t != TypeTables::INVALID {
                return t;
            }
        }
        if let Some(&sym) = self.res.refs.get(&expr_key(e)) {
            match self.res.arena.get(sym).kind {
                SymbolKind::Fn => {
                    if let Some(&t) = self.fn_ty.get(&sym) {
                        return t;
                    }
                    let t = self.fn_signature_of_symbol(sym);
                    self.fn_ty.insert(sym, t);
                    return t;
                }
                SymbolKind::Ctor => return self.ctor_value_type(sym, *span),
                _ => {
                    return self
                        .sym_ty
                        .get(&sym)
                        .copied()
                        .unwrap_or(TypeTables::UNKNOWN);
                }
            }
        }
        TypeTables::UNKNOWN // 内建裸引用 / 未定义名（已报）→ 保守
    }

    /// 裸构造器引用（零参才合法，如 None）。
    fn ctor_value_type(&mut self, sym: SymbolId, span: Span) -> TypeId {
        let (name, arity) = {
            let s = self.res.arena.get(sym);
            (s.name.clone(), s.arity)
        };
        if arity != 0 {
            self.err(
                span,
                DiagnosticCode::E3003,
                format!("构造器 {name} 须直接调用"),
            );
            return TypeTables::INVALID;
        }
        self.ctor_result_type(sym, &[])
    }

    /// 构造器调用的结果类型。
    fn ctor_result_type(&mut self, sym: SymbolId, args: &[TypeId]) -> TypeId {
        let owner = self.res.arena.get(sym).owner_name.clone();
        match owner.as_str() {
            "Option" => {
                let elem = args.first().copied().unwrap_or(TypeTables::UNKNOWN);
                self.tables.intern(TypeData::Option(elem))
            }
            "Result" => {
                let ok = args.first().copied().unwrap_or(TypeTables::UNKNOWN);
                self.tables.intern(TypeData::Result {
                    ok,
                    err: TypeTables::ERROR,
                })
            }
            _ => match self.lookup_type_sym(&owner) {
                Some(sid) => match self.res.arena.get(sid).nominal {
                    Some(NominalRef::Enum(def)) => self.tables.intern(TypeData::Enum(def)),
                    _ => TypeTables::UNKNOWN,
                },
                None => TypeTables::UNKNOWN,
            },
        }
    }

    fn infer_unary(&mut self, e: &Expr) -> TypeId {
        let Expr::Unary { span, op, operand } = e else {
            return TypeTables::UNKNOWN;
        };
        let t = self.infer(operand);
        match op {
            UnaryOp::Neg => {
                if !is_concrete(&self.tables, t) {
                    return t;
                }
                if is_numeric(&self.tables, t) {
                    return t;
                }
                let ts = type_str(&self.tables, t);
                self.err(*span, DiagnosticCode::E3011, format!("- 不能用于 {ts}"));
                TypeTables::INVALID
            }
            UnaryOp::Not => {
                if is_concrete(&self.tables, t) && self.tables.data(t) != &TypeData::Bool {
                    let ts = type_str(&self.tables, t);
                    self.err(
                        *span,
                        DiagnosticCode::E3011,
                        format!("! 不能用于 {ts}（须为 bool）"),
                    );
                    return TypeTables::INVALID;
                }
                TypeTables::BOOL
            }
            UnaryOp::Ref => {
                if !matches!(operand.as_ref(), Expr::Ident { .. }) {
                    self.err(*span, DiagnosticCode::E3012, "v0 只能对变量取引用");
                    return TypeTables::INVALID;
                }
                self.tables.intern(TypeData::Ref(t))
            }
        }
    }

    fn infer_binary(&mut self, e: &Expr) -> TypeId {
        let Expr::Binary { span, op, lhs, rhs } = e else {
            return TypeTables::UNKNOWN;
        };
        // 短路逻辑：操作数须 bool（与 interp truthy 一致）
        if matches!(op, BinaryOp::AndAnd | BinaryOp::OrOr) {
            self.require_bool(lhs, "逻辑运算");
            self.require_bool(rhs, "逻辑运算");
            return TypeTables::BOOL;
        }
        let l = self.infer(lhs);
        let r = self.infer(rhs);

        match op {
            BinaryOp::Eq | BinaryOp::Ne => {
                if is_concrete(&self.tables, l) && is_concrete(&self.tables, r) {
                    let same_scalar = std::mem::discriminant(self.tables.data(l))
                        == std::mem::discriminant(self.tables.data(r))
                        && (is_numeric(&self.tables, l)
                            || matches!(self.tables.data(l), TypeData::Str | TypeData::Bool));
                    let same_enum = match (self.tables.data(l), self.tables.data(r)) {
                        (TypeData::Enum(x), TypeData::Enum(y)) => x == y,
                        _ => false,
                    };
                    if !same_scalar && !same_enum {
                        let ls = type_str(&self.tables, l);
                        let rs = type_str(&self.tables, r);
                        self.err(
                            *span,
                            DiagnosticCode::E3001,
                            format!("{ls} 与 {rs} 不可用 == 比较"),
                        );
                        return TypeTables::INVALID;
                    }
                }
                TypeTables::BOOL
            }
            BinaryOp::Lt | BinaryOp::Le | BinaryOp::Gt | BinaryOp::Ge => {
                if is_concrete(&self.tables, l) && is_concrete(&self.tables, r) {
                    let ok = std::mem::discriminant(self.tables.data(l))
                        == std::mem::discriminant(self.tables.data(r))
                        && (is_numeric(&self.tables, l)
                            || matches!(self.tables.data(l), TypeData::Str));
                    if !ok {
                        let ls = type_str(&self.tables, l);
                        let rs = type_str(&self.tables, r);
                        self.err(
                            *span,
                            DiagnosticCode::E3001,
                            format!("{ls} 与 {rs} 不可比较大小（无隐式转换）"),
                        );
                        return TypeTables::INVALID;
                    }
                }
                TypeTables::BOOL
            }
            BinaryOp::BitAnd
            | BinaryOp::BitOr
            | BinaryOp::BitXor
            | BinaryOp::Shl
            | BinaryOp::Shr => {
                if is_concrete(&self.tables, l) && is_concrete(&self.tables, r) {
                    if self.tables.data(l) != &TypeData::Int
                        || self.tables.data(r) != &TypeData::Int
                    {
                        let ls = type_str(&self.tables, l);
                        let rs = type_str(&self.tables, r);
                        self.err(
                            *span,
                            DiagnosticCode::E3001,
                            format!("{ls} 与 {rs} 不可位运算（须为 int）"),
                        );
                        return TypeTables::INVALID;
                    }
                    return TypeTables::INT;
                }
                TypeTables::ANY
            }

            // 算术
            BinaryOp::Add => {
                if is_concrete(&self.tables, l)
                    && matches!(self.tables.data(l), TypeData::Str)
                    && is_concrete(&self.tables, r)
                    && matches!(self.tables.data(r), TypeData::Str)
                {
                    return TypeTables::STR;
                }
                self.arith_type(*span, l, r)
            }
            BinaryOp::Sub | BinaryOp::Mul | BinaryOp::Div => self.arith_type(*span, l, r),
            BinaryOp::Rem => {
                if is_concrete(&self.tables, l) && matches!(self.tables.data(l), TypeData::Float) {
                    self.err(*span, DiagnosticCode::E3001, "% 不能用于 float");
                    return TypeTables::INVALID;
                }
                self.arith_type(*span, l, r)
            }
            // AndAnd/OrOr 已在入口短路处理
            BinaryOp::AndAnd | BinaryOp::OrOr => TypeTables::BOOL,
        }
    }

    /// 数值运算类型：int⊗int→int、float⊗float→float、混用→E3002、动态→Any。
    fn arith_type(&mut self, span: Span, l: TypeId, r: TypeId) -> TypeId {
        if !is_concrete(&self.tables, l) || !is_concrete(&self.tables, r) {
            return TypeTables::ANY;
        }
        if std::mem::discriminant(self.tables.data(l))
            == std::mem::discriminant(self.tables.data(r))
            && is_numeric(&self.tables, l)
        {
            return l;
        }
        let ls = type_str(&self.tables, l);
        let rs = type_str(&self.tables, r);
        self.err(
            span,
            DiagnosticCode::E3002,
            format!("{ls} 与 {rs} 不可做 {}（无隐式转换）", op_name("?")),
        );
        TypeTables::INVALID
    }

    fn require_bool(&mut self, e: &Expr, what: &str) {
        let t = self.infer(e);
        if is_concrete(&self.tables, t) && self.tables.data(t) != &TypeData::Bool {
            let ts = type_str(&self.tables, t);
            self.err(
                e.span(),
                DiagnosticCode::E3011,
                format!("{what}须为 bool，得到 {ts}"),
            );
        }
    }

    // ———— 调用 ————

    fn infer_call(&mut self, e: &Expr) -> TypeId {
        let Expr::Call { callee, args, .. } = e else {
            return TypeTables::UNKNOWN;
        };
        // 构造器 / 内建 / 用户 fn 的 Ident 调用
        if let Expr::Ident { name, .. } = callee.as_ref() {
            if let Some(&sym) = self.res.refs.get(&expr_key(callee.as_ref())) {
                if self.res.arena.get(sym).kind == SymbolKind::Ctor {
                    let mut arg_tys = Vec::new();
                    for a in args {
                        arg_tys.push(self.infer(a));
                    }
                    let arity = self.res.arena.get(sym).arity;
                    if arg_tys.len() != arity {
                        self.err(
                            e.span(),
                            DiagnosticCode::E3003,
                            format!("{name} 需要 {arity} 个参数，得到 {} 个", arg_tys.len()),
                        );
                        return TypeTables::INVALID;
                    }
                    if name == "Ok" || name == "Err" {
                        // Ok/Err 只能出现在 Result 函数的 return（RESULT-RETURN-004）
                        self.err(
                            e.span(),
                            DiagnosticCode::E3006,
                            "Ok/Err 只能出现在 Result 函数的 return",
                        );
                    }
                    return self.ctor_result_type(sym, &arg_tys);
                }
                // 用户 fn
                if self.res.arena.get(sym).kind == SymbolKind::Fn {
                    self.check_args_vs_symbol(sym, args, e.span());
                    if let Some(&t) = self.fn_ty.get(&sym) {
                        return match self.tables.data(t) {
                            TypeData::Fn { ret, .. } => *ret,
                            _ => TypeTables::UNKNOWN,
                        };
                    }
                    return TypeTables::UNKNOWN;
                }
                // var/param 等持有函数值的 callee：落到下方表达式 callee 路径
            } else if self.res.builtin.contains_key(name) && is_builtin_fn(name) {
                // 内建调用（内建不进 refs，裸引用已由 resolver 放行给调用路径）
                let mut arg_tys = Vec::new();
                for a in args {
                    arg_tys.push(self.infer(a));
                }
                return builtin_return(&mut self.tables, name, &arg_tys);
            }
        }

        // 方法/字段函数值调用：obj.name(args)
        if matches!(callee.as_ref(), Expr::Field { .. }) {
            return self.infer_method_call(callee.as_ref(), e);
        }

        // 表达式 callee（函数字面量/索引）：按函数类型检查
        let callee_ty = self.infer(callee.as_ref());
        let mut arg_tys = Vec::new();
        for a in args {
            arg_tys.push(self.infer(a));
        }
        if matches!(self.tables.data(callee_ty), TypeData::Fn { .. }) {
            self.check_args_vs_fn(callee_ty, &arg_tys, e.span());
            return match self.tables.data(callee_ty) {
                TypeData::Fn { ret, .. } => *ret,
                _ => TypeTables::UNKNOWN,
            };
        }
        TypeTables::UNKNOWN
    }

    /// obj.name(args)：用户方法 / 字段函数值。
    fn infer_method_call(&mut self, fe: &Expr, call: &Expr) -> TypeId {
        let Expr::Field {
            obj,
            name,
            span: fe_span,
            ..
        } = fe
        else {
            return TypeTables::UNKNOWN;
        };
        let Expr::Call { args, .. } = call else {
            return TypeTables::UNKNOWN;
        };
        let raw_obj = self.infer(obj);
        let obj_ty = deref(&self.tables, raw_obj);
        if let TypeData::Struct(def) = self.tables.data(obj_ty) {
            let def = *def;
            // 用户方法（值接收者）
            if let Some(m) = self.struct_method_sym(def, name) {
                self.check_args_vs_symbol(m, args, call.span());
                let s = self.res.arena.get(m);
                let ret = s.type_ex.clone();
                let tps = s.type_params.clone();
                return match &ret {
                    Some(t) => resolve_anno_emit(self, t, &tps),
                    None => TypeTables::UNKNOWN,
                };
            }
            // 字段持有的函数值
            let f_ty = self.field_type(obj_ty, name, *fe_span);
            let mut arg_tys = Vec::new();
            for a in args {
                arg_tys.push(self.infer(a));
            }
            if matches!(self.tables.data(f_ty), TypeData::Fn { .. }) {
                self.check_args_vs_fn(f_ty, &arg_tys, call.span());
                return match self.tables.data(f_ty) {
                    TypeData::Fn { ret, .. } => *ret,
                    _ => TypeTables::UNKNOWN,
                };
            }
            if !is_concrete(&self.tables, f_ty) || f_ty == TypeTables::INVALID {
                return TypeTables::UNKNOWN;
            }
            self.err(
                call.span(),
                DiagnosticCode::E3004,
                format!("{name} 不是方法也不是函数值字段"),
            );
            return TypeTables::INVALID;
        }
        if !is_concrete(&self.tables, obj_ty) {
            for a in args {
                self.infer(a);
            }
            return TypeTables::UNKNOWN;
        }
        let ts = type_str(&self.tables, obj_ty);
        self.err(
            call.span(),
            DiagnosticCode::E3004,
            format!("{ts} 没有方法 {name}"),
        );
        TypeTables::INVALID
    }

    /// struct def → 首个声明符号（arena 创建序；顶层先于嵌套）。
    fn struct_symbol_for(&self, def: StructDefId) -> Option<SymbolId> {
        self.res
            .arena
            .iter()
            .position(|s| s.nominal == Some(NominalRef::Struct(def)))
            .map(|p| SymbolId(p as u32))
    }

    fn struct_method_sym(&self, def: StructDefId, name: &str) -> Option<SymbolId> {
        let sid = self.struct_symbol_for(def)?;
        self.res.arena.get(sid).methods.get(name).copied()
    }

    /// 实参 ↔ 声明参数（个数 + 已注解参数的兼容性）。
    fn check_args_vs_symbol(&mut self, sym: SymbolId, args: &[Expr], span: Span) {
        let (fname, arity, annos, names, tps) = {
            let s = self.res.arena.get(sym);
            (
                s.name.clone(),
                s.arity,
                s.param_annos.clone(),
                s.param_names.clone(),
                s.type_params.clone(),
            )
        };
        if args.len() != arity {
            self.err(
                span,
                DiagnosticCode::E3003,
                format!("{fname} 需要 {arity} 个参数，得到 {} 个", args.len()),
            );
            for a in args {
                self.infer(a);
            }
            return;
        }
        for (i, a) in args.iter().enumerate() {
            let at = self.infer(a);
            let Some(anno) = annos.get(i).cloned().flatten() else {
                continue;
            };
            let pt = resolve_anno_emit(self, &anno, &tps);
            if !assignable(&self.tables, pt, at) {
                let pname = names.get(i).cloned().unwrap_or_default();
                let pts = type_str(&self.tables, pt);
                let ats = type_str(&self.tables, at);
                self.err(
                    a.span(),
                    DiagnosticCode::E3001,
                    format!("参数 {pname} 需要 {pts}，得到 {ats}"),
                );
            }
        }
    }

    fn check_args_vs_fn(&mut self, fn_ty: TypeId, args: &[TypeId], span: Span) {
        let params = match self.tables.data(fn_ty) {
            TypeData::Fn { params, .. } => params.clone(),
            _ => return,
        };
        if args.len() != params.len() {
            self.err(
                span,
                DiagnosticCode::E3003,
                format!("需要 {} 个参数，得到 {} 个", params.len(), args.len()),
            );
            return;
        }
        for (i, (p, a)) in params.iter().zip(args.iter()).enumerate() {
            if !assignable(&self.tables, *p, *a) {
                let pts = type_str(&self.tables, *p);
                let ats = type_str(&self.tables, *a);
                self.err(
                    span,
                    DiagnosticCode::E3001,
                    format!("第 {} 个参数需要 {pts}，得到 {ats}", i + 1),
                );
            }
        }
    }

    // ———— 字面量与容器 ————

    fn infer_struct_lit(&mut self, e: &Expr) -> TypeId {
        let Expr::StructLit { span, fields, .. } = e else {
            return TypeTables::UNKNOWN;
        };
        let Some(&sym) = self.res.refs.get(&expr_key(e)) else {
            // resolver 已报未知 struct；只走子表达式
            for f in fields {
                self.infer(&f.value);
            }
            return TypeTables::UNKNOWN;
        };
        let s = self.res.arena.get(sym);
        let (sname, sfields, sannos, stps, nominal) = (
            s.name.clone(),
            s.fields.clone(),
            s.field_annos.clone(),
            s.type_params.clone(),
            s.nominal,
        );
        if fields.len() != sfields.len() {
            self.err(
                *span,
                DiagnosticCode::E3001,
                format!(
                    "{} 需要 {} 个字段，得到 {} 个",
                    sname,
                    sfields.len(),
                    fields.len()
                ),
            );
        }
        for (i, f) in fields.iter().enumerate() {
            let ft = self.infer(&f.value);
            if i < sfields.len() {
                if f.name != sfields[i] {
                    self.err(
                        *span,
                        DiagnosticCode::E3001,
                        format!(
                            "{} 第 {} 个字段应为 {}，得到 {}",
                            sname,
                            i + 1,
                            sfields[i],
                            f.name
                        ),
                    );
                }
                if let Some(anno) = sannos.get(i).cloned().flatten() {
                    let at = resolve_anno_quiet(self, &anno, &stps);
                    if !assignable(&self.tables, at, ft) {
                        let fts = type_str(&self.tables, ft);
                        let ats = type_str(&self.tables, at);
                        self.err(
                            f.value.span(),
                            DiagnosticCode::E3001,
                            format!("字段 {} 需要 {ats}，得到 {fts}", f.name),
                        );
                    }
                }
            }
        }
        match nominal {
            Some(NominalRef::Struct(def)) => self.tables.intern(TypeData::Struct(def)),
            _ => TypeTables::UNKNOWN,
        }
    }

    fn infer_map_lit(&mut self, e: &Expr) -> TypeId {
        let Expr::MapLit { span, items } = e else {
            return TypeTables::UNKNOWN;
        };
        let mut key_t: Option<TypeId> = None;
        let mut val_t: Option<TypeId> = None;
        for item in items {
            let kt = self.infer(&item.key);
            let vt = self.infer(&item.value);
            key_t = self.mix_concrete(key_t, kt);
            val_t = self.mix_concrete(val_t, vt);
        }
        let key_t = key_t.unwrap_or(TypeTables::UNKNOWN);
        let val_t = val_t.unwrap_or(TypeTables::UNKNOWN);
        let key_bad = is_concrete(&self.tables, key_t)
            && !matches!(
                self.tables.data(key_t),
                TypeData::Int | TypeData::Str | TypeData::Bool
            );
        if key_bad {
            let ks = type_str(&self.tables, key_t);
            self.err(
                *span,
                DiagnosticCode::E3014,
                format!("映射键须为 int/string/bool，得到 {ks}"),
            );
            return self.tables.intern(TypeData::Map {
                key: TypeTables::UNKNOWN,
                val: val_t,
            });
        }
        self.tables.intern(TypeData::Map {
            key: key_t,
            val: val_t,
        })
    }

    /// 混型统一：同型保持；一侧宽松取另一侧；两侧具体异型 → 动态（interp 合法）。
    fn mix_concrete(&self, cur: Option<TypeId>, next: TypeId) -> Option<TypeId> {
        match cur {
            None => Some(next),
            Some(c) => {
                if equal(&self.tables, c, next) || !is_concrete(&self.tables, next) {
                    Some(c)
                } else if !is_concrete(&self.tables, c) {
                    Some(next)
                } else {
                    Some(TypeTables::UNKNOWN)
                }
            }
        }
    }

    fn infer_arr_lit(&mut self, e: &Expr) -> TypeId {
        let Expr::ArrLit { items, .. } = e else {
            return TypeTables::UNKNOWN;
        };
        let mut elem: Option<TypeId> = None;
        for el in items {
            let t = self.infer(el);
            match elem {
                None => elem = Some(t),
                Some(cur) => {
                    if equal(&self.tables, cur, t) {
                        continue;
                    }
                    if !is_concrete(&self.tables, t) {
                        continue;
                    }
                    if !is_concrete(&self.tables, cur) {
                        elem = Some(t);
                        continue;
                    }
                    elem = Some(TypeTables::UNKNOWN); // 混型数组：interp 合法
                }
            }
        }
        self.tables
            .intern(TypeData::Slice(elem.unwrap_or(TypeTables::UNKNOWN)))
    }

    // ———— match ————

    fn infer_match(&mut self, e: &Expr) -> TypeId {
        let Expr::Match {
            span,
            scrutinee,
            arms,
        } = e
        else {
            return TypeTables::UNKNOWN;
        };
        let scrut = self.infer(scrutinee);
        let (kind, ok) = self.classify_match(e);
        self.match_kind.insert(expr_key(e), kind);
        if !ok {
            return TypeTables::UNKNOWN; // 分类已报错（混用/字面量臂），不再叠加穷尽诊断
        }

        if kind == MatchKind::Enum {
            self.check_enum_exhaustive(e, *span);
        } else if matches!(kind, MatchKind::Result | MatchKind::Option) {
            self.check_algebraic_exhaustive(e, *span);
        }

        // 臂绑定类型与臂体类型（绑定限本臂作用域，MATCH-BIND-003）
        let mut body: Option<TypeId> = None;
        for arm in arms {
            let bind_types = self.arm_bind_types(&arm.pat, scrut);
            self.push_scope();
            for (name, t) in &bind_types {
                self.define_type(name, *t);
            }
            let bt = self.infer(&arm.body);
            self.pop_scope();
            match body {
                None => body = Some(bt),
                Some(cur) => {
                    if equal(&self.tables, cur, bt) {
                        // 同型
                    } else if assignable(&self.tables, bt, cur) {
                        // 臂体可归入已定类型 → 保持（如 [] 归入 [Ref]）
                    } else if assignable(&self.tables, cur, bt) {
                        body = Some(bt);
                    } else if is_concrete(&self.tables, cur) && is_concrete(&self.tables, bt) {
                        let cs = type_str(&self.tables, cur);
                        let bs = type_str(&self.tables, bt);
                        self.err(
                            arm.span,
                            DiagnosticCode::E3001,
                            format!("match 臂类型不一致：{cs} 与 {bs}"),
                        );
                    } else if !is_concrete(&self.tables, cur) {
                        body = Some(bt);
                    }
                }
            }
        }
        body.unwrap_or(TypeTables::UNKNOWN)
    }

    /// 模式绑定的捕获类型。
    fn arm_bind_types(&mut self, p: &Pat, scrut: TypeId) -> Vec<(String, TypeId)> {
        let mut out = Vec::new();
        self.walk_bind(p, scrut, &mut out);
        out
    }

    fn walk_bind(&mut self, p: &Pat, scrut: TypeId, out: &mut Vec<(String, TypeId)>) {
        match p {
            Pat::Bind { name, .. } => {
                out.push((name.clone(), scrut));
                self.pat_ty.insert(pat_key(p), scrut);
            }
            Pat::Case { subs, .. } => {
                let Some(&sym) = self.res.pat_refs.get(&pat_key(p)) else {
                    return;
                };
                let s = self.res.arena.get(sym);
                let (owner, cname, case_index) =
                    (s.owner_name.clone(), s.name.clone(), s.case_index);
                for (i, sp) in subs.iter().enumerate() {
                    let sub_t = if owner == "Result" {
                        if cname == "Ok" {
                            result_ok(&self.tables, scrut)
                        } else {
                            TypeTables::ERROR
                        }
                    } else if owner == "Option" {
                        option_elem(&self.tables, scrut)
                    } else {
                        self.case_payload_type(&owner, case_index, i)
                    };
                    self.walk_bind(sp, sub_t, out);
                }
            }
            Pat::Lit { .. } | Pat::Wild { .. } => {}
        }
    }

    /// enum case 载荷类型（def 表已静默解析）。
    fn case_payload_type(&mut self, owner: &str, case_index: u32, payload_idx: usize) -> TypeId {
        match self.lookup_type_sym(owner) {
            Some(sid) => match self.res.arena.get(sid).nominal {
                Some(NominalRef::Enum(def)) => {
                    let cases = &self.tables.enum_def(def).cases;
                    match cases.get(case_index as usize) {
                        Some(c) => c
                            .payloads
                            .get(payload_idx)
                            .copied()
                            .unwrap_or(TypeTables::UNKNOWN),
                        None => TypeTables::UNKNOWN,
                    }
                }
                _ => TypeTables::UNKNOWN,
            },
            None => TypeTables::UNKNOWN,
        }
    }

    /// match 分类（唯一判定点）。第二返回值 false 表示分类阶段已报错。
    fn classify_match(&mut self, e: &Expr) -> (MatchKind, bool) {
        let Expr::Match { span, arms, .. } = e else {
            return (MatchKind::Literal, true);
        };
        let mut has_case = false;
        let mut has_lit = false;
        let mut result_ctor = false;
        let mut option_ctor = false;
        let mut enum_name = String::new();
        let mut mixed_enum = false;
        for arm in arms {
            match &arm.pat {
                Pat::Lit { .. } => has_lit = true,
                Pat::Case { .. } => {
                    has_case = true;
                    let Some(&sym) = self.res.pat_refs.get(&pat_key(&arm.pat)) else {
                        continue;
                    };
                    let owner = self.res.arena.get(sym).owner_name.clone();
                    if owner == "Result" {
                        result_ctor = true;
                    } else if owner == "Option" {
                        option_ctor = true;
                    } else if enum_name.is_empty() {
                        enum_name = owner;
                    } else if enum_name != owner {
                        mixed_enum = true;
                    }
                }
                Pat::Bind { .. } | Pat::Wild { .. } => {}
            }
        }
        if result_ctor && option_ctor {
            self.err(*span, DiagnosticCode::E4003, "Result 与 Option 的模式混用");
            return (MatchKind::Literal, false);
        }
        if result_ctor {
            if has_lit {
                self.err(
                    *span,
                    DiagnosticCode::E4002,
                    "代数类型的 match 不能混入字面量模式",
                );
                return (MatchKind::Result, false);
            }
            return (MatchKind::Result, true);
        }
        if option_ctor {
            if has_lit {
                self.err(
                    *span,
                    DiagnosticCode::E4002,
                    "代数类型的 match 不能混入字面量模式",
                );
                return (MatchKind::Option, false);
            }
            return (MatchKind::Option, true);
        }
        if has_case {
            if has_lit {
                self.err(
                    *span,
                    DiagnosticCode::E4002,
                    "enum 的 match 不能混入字面量模式",
                );
                return (MatchKind::Enum, false);
            }
            if mixed_enum {
                self.err(
                    *span,
                    DiagnosticCode::E4003,
                    "match 混入了不同 enum 的构造器",
                );
                return (MatchKind::Enum, false);
            }
            return (MatchKind::Enum, true);
        }
        (MatchKind::Literal, true)
    }

    /// enum match 穷尽性（编译期）。
    fn check_enum_exhaustive(&mut self, e: &Expr, span: Span) {
        let Expr::Match { arms, .. } = e else {
            return;
        };
        let mut covered: Vec<String> = Vec::new();
        let mut has_wild = false;
        let mut enum_sym: Option<SymbolId> = None;
        for arm in arms {
            match &arm.pat {
                Pat::Case { name, .. } => {
                    let Some(&sym) = self.res.pat_refs.get(&pat_key(&arm.pat)) else {
                        continue;
                    };
                    if enum_sym.is_none() {
                        let owner = self.res.arena.get(sym).owner_name.clone();
                        enum_sym = self.lookup_type_sym(&owner);
                    }
                    covered.push(name.clone());
                }
                Pat::Wild { .. } | Pat::Bind { .. } => has_wild = true,
                Pat::Lit { .. } => {}
            }
        }
        if has_wild {
            return;
        }
        let Some(sym) = enum_sym else {
            return;
        };
        let Some(NominalRef::Enum(def)) = self.res.arena.get(sym).nominal else {
            return;
        };
        let missing: Vec<String> = {
            let cases = &self.tables.enum_def(def).cases;
            cases
                .iter()
                .filter(|c| !covered.contains(&c.name))
                .map(|c| c.name.clone())
                .collect()
        };
        if !missing.is_empty() {
            self.err(
                span,
                DiagnosticCode::E4001,
                format!("match 不穷尽，缺少: {}", missing.join(", ")),
            );
        }
    }

    /// Result/Option match 须两臂齐全。
    fn check_algebraic_exhaustive(&mut self, e: &Expr, span: Span) {
        let Expr::Match { arms, .. } = e else {
            return;
        };
        let mut pos = false;
        let mut neg = false;
        for arm in arms {
            match &arm.pat {
                Pat::Case { name, .. } => {
                    if name == "Ok" || name == "Some" {
                        pos = true;
                    } else if name == "Err" || name == "None" {
                        neg = true;
                    }
                }
                Pat::Wild { .. } => {
                    pos = true;
                    neg = true;
                }
                _ => {}
            }
        }
        if !pos || !neg {
            self.err(
                span,
                DiagnosticCode::E4001,
                "match 不穷尽：Result/Option 须覆盖两臂",
            );
        }
    }

    // ———— ? 传播 ————

    /// 语句顶层 ? 的检查：所在函数须注解 Result，操作数须 Result。
    fn infer_try(&mut self, t: &Expr) -> TypeId {
        let ret_is_result = self
            .fn_ret
            .map(|r| matches!(self.tables.data(r), TypeData::Result { .. }))
            .unwrap_or(false);
        if !self.in_fn || !ret_is_result {
            self.err(
                t.span(),
                DiagnosticCode::E3005,
                "带 ? 的函数须注解为 Result[T, error]",
            );
        }
        self.infer_try_operand(t)
    }

    fn infer_try_operand(&mut self, t: &Expr) -> TypeId {
        let Expr::Try { span, operand } = t else {
            return TypeTables::UNKNOWN;
        };
        let op = self.infer(operand);
        match self.tables.data(op) {
            TypeData::Result { ok, .. } => *ok,
            TypeData::Any | TypeData::Unknown | TypeData::Invalid => TypeTables::UNKNOWN,
            TypeData::Option(_) => {
                let ts = type_str(&self.tables, op);
                self.err(
                    *span,
                    DiagnosticCode::E3008,
                    format!("? 的操作数须为 Result，得到 {ts}（Option 不可用 ?）"),
                );
                TypeTables::INVALID
            }
            _ => {
                let ts = type_str(&self.tables, op);
                self.err(
                    *span,
                    DiagnosticCode::E3008,
                    format!("? 的操作数须为 Result，得到 {ts}"),
                );
                TypeTables::INVALID
            }
        }
    }

    // ———— 字段与索引 ————

    /// 字段访问类型检查（`&T` 自动解引用，与 interp PtrVal 语义一致）。
    fn field_type(&mut self, obj_ty: TypeId, name: &str, span: Span) -> TypeId {
        let ty = deref(&self.tables, obj_ty);
        match self.tables.data(ty) {
            TypeData::Struct(def) => {
                let found = self
                    .tables
                    .struct_def(*def)
                    .fields
                    .iter()
                    .find(|f| f.name == name)
                    .map(|f| f.ty);
                if let Some(ft) = found {
                    return ft;
                }
                let sname = self.tables.struct_def(*def).name.clone();
                self.err(
                    span,
                    DiagnosticCode::E3004,
                    format!("{sname} 没有字段 {name}"),
                );
                TypeTables::INVALID
            }
            TypeData::Error => {
                if name == ERROR_MSG_FIELD {
                    return TypeTables::STR;
                }
                self.err(span, DiagnosticCode::E3004, "error 只有 msg 字段");
                TypeTables::INVALID
            }
            _ if !is_concrete(&self.tables, ty) => TypeTables::UNKNOWN,
            _ => {
                let ts = type_str(&self.tables, ty);
                self.err(span, DiagnosticCode::E3004, format!("{ts} 没有字段 {name}"));
                TypeTables::INVALID
            }
        }
    }

    /// 索引读取的元素类型。
    fn index_elem_ty(&mut self, obj_ty: TypeId, span: Span) -> TypeId {
        let ty = deref(&self.tables, obj_ty);
        match self.tables.data(ty) {
            TypeData::Slice(elem) => *elem,
            TypeData::Map { val, .. } => *val,
            TypeData::Str => TypeTables::STR,
            TypeData::Any | TypeData::Unknown | TypeData::Invalid => TypeTables::UNKNOWN,
            _ => {
                let ts = type_str(&self.tables, ty);
                self.err(span, DiagnosticCode::E3001, format!("{ts} 不可索引"));
                TypeTables::INVALID
            }
        }
    }

    /// 索引类型检查：容器须 int 下标；映射键限 int/string/bool。
    fn check_index_key(&mut self, obj_ty: TypeId, idx_ty: TypeId, span: Span) {
        if !is_concrete(&self.tables, idx_ty) {
            return;
        }
        let ty = deref(&self.tables, obj_ty);
        let idx_bad = !matches!(
            self.tables.data(idx_ty),
            TypeData::Int | TypeData::Str | TypeData::Bool
        );
        match self.tables.data(ty) {
            TypeData::Slice(_) | TypeData::Str => {
                if self.tables.data(idx_ty) != &TypeData::Int {
                    let is = type_str(&self.tables, idx_ty);
                    self.err(
                        span,
                        DiagnosticCode::E3013,
                        format!("索引须为 int，得到 {is}"),
                    );
                }
            }
            TypeData::Map { .. } if idx_bad => {
                let is = type_str(&self.tables, idx_ty);
                self.err(
                    span,
                    DiagnosticCode::E3014,
                    format!("映射键须为 int/string/bool，得到 {is}"),
                );
            }
            _ => {}
        }
    }

    // ———— 函数字面量 ————

    fn infer_fn_lit(&mut self, e: &Expr) -> TypeId {
        let Expr::FnLit {
            span,
            type_params,
            params,
            ret,
            body,
        } = e
        else {
            return TypeTables::UNKNOWN;
        };
        let saved_scopes = std::mem::replace(&mut self.scopes, vec![BTreeMap::new()]);
        let saved_ret = self.fn_ret;
        let saved_in_fn = self.in_fn;
        let tps = type_params.clone();
        self.fn_ret = ret.as_ref().map(|r| resolve_anno_emit(self, r, &tps));
        self.in_fn = true;
        let mut ps = Vec::new();
        for p in params {
            match &p.typ {
                None => {
                    self.err(
                        *span,
                        DiagnosticCode::E3007,
                        format!("函数字面量参数 {} 缺少类型注解", p.name),
                    );
                    ps.push(TypeTables::UNKNOWN);
                    self.define_type(&p.name, TypeTables::UNKNOWN);
                }
                Some(t) => {
                    let pt = resolve_anno_emit(self, t, &tps);
                    ps.push(pt);
                    self.define_type(&p.name, pt);
                }
            }
        }
        for s in body {
            self.stmt(s);
        }
        let r = match ret {
            None => TypeTables::UNKNOWN,
            Some(t) => resolve_anno_emit(self, t, &tps),
        };
        self.scopes = saved_scopes;
        self.fn_ret = saved_ret;
        self.in_fn = saved_in_fn;
        self.tables.intern(TypeData::Fn { params: ps, ret: r })
    }
}

#[cfg(test)]
mod tests {
    use crate::check_source;
    use lang_core::DiagnosticCode;

    #[test]
    fn missing_param_annotation_is_e3007_at_decl_line() {
        // TYPE-ANN-006：参数缺注解 → E3007（锚在 fn 声明行）
        let src = "fn f(a): int {\n    return a\n}\nfn main() {\n    println(f(1))\n}\n";
        assert_eq!(check_source(src), vec![(DiagnosticCode::E3007, 1)]);
    }

    #[test]
    fn result_match_missing_err_arm_is_e4001() {
        // MATCH-EXHAUSTIVE-002 代数型：缺 Err 臂
        let src = r#"
fn f(r: Result[int, error]): int {
    return match r {
        Ok(v) => v
    }
}
fn main() {
    println(f(Ok(1)))
}
"#;
        // main 里 Ok(1) 在 Result-fn return 之外 → E3006 连带（RESULT-RETURN-004 位置约束）
        assert_eq!(
            check_source(src),
            vec![(DiagnosticCode::E4001, 3), (DiagnosticCode::E3006, 8)]
        );
    }

    #[test]
    fn neg_on_string_is_e3011() {
        let src = r#"
fn main() {
    let s = "x"
    let t = -s
    println(t)
}
"#;
        assert_eq!(check_source(src), vec![(DiagnosticCode::E3011, 4)]);
    }

    #[test]
    fn error_msg_field_access_ok_other_missing() {
        // RESULT-PRELUDE-001：error.msg 合法；其他字段 E3004（消息特化为 error 专名）
        let src = r#"
fn main() {
    let e = err("boom")
    println(e.msg)
    println(e.other)
}
"#;
        assert_eq!(check_source(src), vec![(DiagnosticCode::E3004, 5)]);
    }

    #[test]
    fn try_operand_non_result_is_e3008() {
        // ? 操作数为 int → E3008（同 line 双码：函数未注解 Result E3005）
        let src = r#"
fn f(): int {
    let x = 1?
    return x
}
fn main() {
    println(f())
}
"#;
        assert_eq!(
            check_source(src),
            vec![(DiagnosticCode::E3005, 3), (DiagnosticCode::E3008, 3)]
        );
    }

    #[test]
    fn analysis_prelude_and_match_kind_view() {
        use lang_core::SourceId;
        use lang_core::SourceText;
        use lang_types::TypeData;

        let source = SourceId(1);
        let text = SourceText::new(
            "m.lang-go",
            r#"
fn f(o: Option[int]): int {
    return match o {
        Some(v) => v
        None => 0
    }
}
fn main() {
    println(f(None))
}
"#
            .to_string(),
        );
        let stmts = lang_syntax::parse(&text, source).unwrap_or_default();
        let analysis = crate::analyze_stmts(&stmts, source);
        assert!(!analysis.has_errors());
        // 预载面（A4 冻结 id）：预载 Option/Result 只有 ctor 符号，enum def 直接在表里
        for s in analysis.symbols.iter() {
            if s.kind == crate::symbols::SymbolKind::Ctor && s.name == "Some" {
                assert_eq!(s.owner_name, "Option");
                assert_eq!(s.case_index, 0);
                assert!(s.is_builtin);
            }
        }
        assert_eq!(
            analysis.types.enum_def(lang_types::OPTION_DEF).name,
            "Option"
        );
        assert_eq!(
            analysis.types.enum_def(lang_types::OPTION_DEF).cases[0].name,
            "Some"
        );
        assert_eq!(
            analysis.types.enum_def(lang_types::RESULT_DEF).cases[0].name,
            "Ok"
        );
        // match 分类：f 体里的 match 是 Option 型
        for s in &stmts {
            if let lang_syntax::Stmt::FnDecl { body, name, .. } = s {
                if name == "f" {
                    if let Some(lang_syntax::Stmt::Return { value: Some(v), .. }) = body.first() {
                        let mk = analysis.match_kind(v).expect("match 已分类");
                        assert_eq!(mk, crate::MatchKind::Option);
                        // 被匹配值类型 = Option[int]
                        if let lang_syntax::Expr::Match { scrutinee, .. } = v {
                            let t = analysis.type_of(scrutinee.as_ref()).expect("scrut 类型");
                            assert_eq!(
                                analysis.types.data(t),
                                &TypeData::Option(lang_types::TypeTables::INT)
                            );
                        }
                    }
                }
            }
        }
    }
}
