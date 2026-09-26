//! 名字解析（specs/name-resolution.md NAME-SCOPE/SHADOW/DUP/UNDEF/CTOR/RECV/MUT/PLACE/NEST/MERGE）。
//!
//! 多遍扫描，结构与 TS/Go 证据同构：预载 → 收集 struct/enum 符号 → 顶层类型 def 表
//! 注册（字段/载荷注解静默解析，前向类型引用可见）→ 收集 fn/extern/method 符号 →
//! 顶层绑定依序解析（与 interp 顺序执行一致，前向引用全局即 E2002）→ 函数体最后走
//! （函数体对全局的可见性与声明顺序无关）。
//!
//! 命名空间三分：值（fn/绑定/参数，作用域栈）、类型（struct/enum，作用域栈）、
//! 构造器（全局唯一，含预载 Some/None/Ok/Err）。
//! 查名顺序与解释器运行时一致：作用域栈 → 构造器 → 内建。

use std::collections::{BTreeMap, HashMap};

use lang_core::{Diagnostic, DiagnosticCode, Position, SourceId, Span, SymbolId};
use lang_syntax::{Expr, Param, Pat, Stmt, TypeEx};
use lang_types::{ERROR_DEF, EnumCaseDef, EnumDef, FieldDef, StructDef, TypeId, TypeTables};

use crate::anno::{TypeCtx, resolve_anno_quiet};
use crate::builtin::{BUILTIN_FNS, PRELUDE_CTORS, PRELUDE_STRUCTS};
use crate::symbols::{NominalRef, Symbol, SymbolArena, SymbolKind};

/// 节点键：同一分析内 AST 稳定借用，指针即身份（不 deref、不外泄）。
pub(crate) fn expr_key(e: &Expr) -> *const Expr {
    e
}

pub(crate) fn pat_key(p: &Pat) -> *const Pat {
    p
}

pub(crate) fn stmt_key(s: &Stmt) -> *const Stmt {
    s
}

/// 解析产物（checker 消费；对外经 [`crate::Analysis`] 查询视图暴露）。
pub(crate) struct ResolveOutput {
    pub arena: SymbolArena,
    pub refs: HashMap<*const Expr, SymbolId>,
    pub pat_refs: HashMap<*const Pat, SymbolId>,
    pub decl_syms: HashMap<*const Stmt, SymbolId>,
    pub fn_params: HashMap<SymbolId, Vec<SymbolId>>,
    pub builtin: BTreeMap<String, SymbolId>,
    pub top_types: BTreeMap<String, SymbolId>,
    pub tables: TypeTables,
    pub diags: Vec<Diagnostic>,
}

pub(crate) struct Resolver {
    pub(crate) arena: SymbolArena,
    pub(crate) diags: Vec<Diagnostic>,
    pub(crate) builtin: BTreeMap<String, SymbolId>,
    pub(crate) top_types: BTreeMap<String, SymbolId>,
    refs: HashMap<*const Expr, SymbolId>,
    pat_refs: HashMap<*const Pat, SymbolId>,
    decl_syms: HashMap<*const Stmt, SymbolId>,
    fn_params: HashMap<SymbolId, Vec<SymbolId>>,
    /// 值命名空间：fn/绑定/参数。
    scopes: Vec<BTreeMap<String, SymbolId>>,
    /// 类型命名空间：struct/enum。
    type_scopes: Vec<BTreeMap<String, SymbolId>>,
    /// 构造器全局命名空间（含预载）。
    ctor_scope: BTreeMap<String, SymbolId>,
    loop_depth: u32,
    fn_depth: u32,
    pub(crate) tables: TypeTables,
}

impl TypeCtx for Resolver {
    fn tables_mut(&mut self) -> &mut TypeTables {
        &mut self.tables
    }

    fn arena(&self) -> &SymbolArena {
        &self.arena
    }

    fn lookup_named(&self, name: &str) -> Option<SymbolId> {
        self.lookup_type_sym(name)
    }

    fn emit(&mut self, code: DiagnosticCode, msg: String) {
        // 注解解析的诊断无节点锚（line 0）
        self.diags.push(Diagnostic::new(code, msg, None));
    }
}

/// 零值 span（预载符号用；line 0）。
fn builtin_span() -> Span {
    Span::point(SourceId(0), Position::new(0, 0, 0))
}

impl Resolver {
    pub(crate) fn new() -> Self {
        Self {
            arena: SymbolArena::new(),
            diags: Vec::new(),
            builtin: BTreeMap::new(),
            top_types: BTreeMap::new(),
            refs: HashMap::new(),
            pat_refs: HashMap::new(),
            decl_syms: HashMap::new(),
            fn_params: HashMap::new(),
            scopes: Vec::new(),
            type_scopes: Vec::new(),
            ctor_scope: BTreeMap::new(),
            loop_depth: 0,
            fn_depth: 0,
            tables: TypeTables::new(),
        }
    }

    fn err(&mut self, span: Span, code: DiagnosticCode, msg: impl Into<String>) {
        // v0.4 行级锚：line 取节点首行，column 恒 1
        self.diags.push(Diagnostic::new(
            code,
            msg,
            Some(Diagnostic::at_line(span.source, span.start.line)),
        ));
    }

    fn push_scope(&mut self) {
        self.scopes.push(BTreeMap::new());
        self.type_scopes.push(BTreeMap::new());
    }

    fn pop_scope(&mut self) {
        self.scopes.pop();
        self.type_scopes.pop();
    }

    /// 符号入 arena（id 即创建序；重复定义的符号也在 arena，但不进作用域——
    /// fn_params 等按位映射依赖这一点，与证据一致）。
    fn new_sym(&mut self, s: Symbol) -> SymbolId {
        self.arena.push(s)
    }

    /// 当前作用域定义；重名即 E2001 且**保留先者**（遮蔽跨作用域合法，不在此列）。
    fn define(&mut self, name: &str, span: Span, kind: SymbolKind, id: SymbolId) {
        let dup = self.scopes.last().is_some_and(|top| top.contains_key(name));
        if dup {
            self.err(
                span,
                DiagnosticCode::E2001,
                format!("{} {name} 在此作用域重复定义", kind.kind_name()),
            );
            return;
        }
        if let Some(top) = self.scopes.last_mut() {
            top.insert(name.to_string(), id);
        }
    }

    /// 构造器全局命名空间定义；重名即 E2003（保留先者）。
    fn define_ctor(&mut self, name: &str, span: Span, id: SymbolId) {
        if let Some(&old) = self.ctor_scope.get(name) {
            let owner = if self.arena.get(old).is_builtin {
                "内建".to_string()
            } else {
                self.arena.get(old).owner_name.to_string()
            };
            self.err(
                span,
                DiagnosticCode::E2003,
                format!("构造器 {name} 重名（已被 {owner} 使用）"),
            );
            return;
        }
        self.ctor_scope.insert(name.to_string(), id);
    }

    fn lookup(&self, name: &str) -> Option<SymbolId> {
        for scope in self.scopes.iter().rev() {
            if let Some(&id) = scope.get(name) {
                return Some(id);
            }
        }
        None
    }

    fn lookup_ctor(&self, name: &str) -> Option<SymbolId> {
        self.ctor_scope.get(name).copied()
    }

    fn lookup_top(&self, name: &str) -> Option<SymbolId> {
        self.scopes.first().and_then(|s| s.get(name)).copied()
    }

    /// 类型命名空间查名（struct/enum；struct_lit 引用用，最内作用域优先）。
    fn lookup_type(&self, name: &str) -> Option<SymbolId> {
        for scope in self.type_scopes.iter().rev() {
            if let Some(&id) = scope.get(name) {
                return Some(id);
            }
        }
        None
    }

    /// 注解解析用的类型查名：预载 error → 顶层 → arena 首个同名（嵌套兜底）。
    pub(crate) fn lookup_type_sym(&self, name: &str) -> Option<SymbolId> {
        if let Some(&id) = self.builtin.get(name) {
            if self.arena.get(id).kind == SymbolKind::Struct {
                return Some(id);
            }
        }
        if let Some(&id) = self.top_types.get(name) {
            return Some(id);
        }
        self.arena.first_type_named(name)
    }

    // ———— 预载 ————

    pub(crate) fn prelude(&mut self) {
        for n in BUILTIN_FNS {
            let id = self.new_sym(Symbol {
                id: SymbolId(0),
                kind: SymbolKind::Fn,
                name: n.to_string(),
                span: builtin_span(),
                mutable: false,
                type_ex: None,
                param_annos: Vec::new(),
                param_names: Vec::new(),
                recv_name: None,
                arity: 0,
                type_params: Vec::new(),
                owner_name: String::new(),
                case_index: 0,
                payload_annos: Vec::new(),
                fields: Vec::new(),
                field_annos: Vec::new(),
                methods: BTreeMap::new(),
                nominal: None,
                is_extern: false,
                is_builtin: true,
            });
            self.builtin.insert(n.to_string(), id);
        }
        for n in PRELUDE_STRUCTS {
            let id = self.new_sym(Symbol {
                id: SymbolId(0),
                kind: SymbolKind::Struct,
                name: n.to_string(),
                span: builtin_span(),
                mutable: false,
                type_ex: None,
                param_annos: Vec::new(),
                param_names: Vec::new(),
                recv_name: None,
                arity: 0,
                type_params: Vec::new(),
                owner_name: String::new(),
                case_index: 0,
                payload_annos: Vec::new(),
                fields: vec!["msg".to_string()],
                field_annos: Vec::new(),
                methods: BTreeMap::new(),
                nominal: Some(NominalRef::Struct(ERROR_DEF)),
                is_extern: false,
                is_builtin: true,
            });
            self.builtin.insert(n.to_string(), id);
        }
        for (name, owner, idx, arity) in PRELUDE_CTORS {
            let id = self.new_sym(Symbol {
                id: SymbolId(0),
                kind: SymbolKind::Ctor,
                name: name.to_string(),
                span: builtin_span(),
                mutable: false,
                type_ex: None,
                param_annos: Vec::new(),
                param_names: Vec::new(),
                recv_name: None,
                arity,
                type_params: Vec::new(),
                owner_name: owner.to_string(),
                case_index: idx,
                payload_annos: Vec::new(),
                fields: Vec::new(),
                field_annos: Vec::new(),
                methods: BTreeMap::new(),
                nominal: None,
                is_extern: false,
                is_builtin: true,
            });
            self.ctor_scope.insert(name.to_string(), id);
        }
    }

    // ———— 第一遍：收集顶层 struct/enum 符号（def 表随后统一注册）———

    fn collect_types(&mut self, prog: &[Stmt]) {
        for s in prog {
            match s {
                Stmt::StructDecl {
                    span, name, fields, ..
                } => {
                    if self.type_scopes[0].contains_key(name) {
                        self.err(
                            *span,
                            DiagnosticCode::E2001,
                            format!("struct {name} 重复定义"),
                        );
                        continue;
                    }
                    let nominal = NominalRef::Struct(self.tables.register_struct(StructDef {
                        name: name.clone(),
                        fields: Vec::new(),
                    }));
                    let id =
                        self.new_sym(self.struct_symbol(name, span, fields, nominal, Vec::new()));
                    self.type_scopes[0].insert(name.clone(), id);
                }
                Stmt::EnumDecl {
                    span,
                    name,
                    cases,
                    type_params,
                } => {
                    if self.type_scopes[0].contains_key(name) {
                        self.err(
                            *span,
                            DiagnosticCode::E2001,
                            format!("enum {name} 重复定义"),
                        );
                        continue;
                    }
                    let nominal = NominalRef::Enum(self.tables.register_enum(EnumDef {
                        name: name.clone(),
                        cases: Vec::new(),
                    }));
                    let id = self.new_sym(Symbol {
                        id: SymbolId(0),
                        kind: SymbolKind::Enum,
                        name: name.clone(),
                        span: *span,
                        mutable: false,
                        type_ex: None,
                        param_annos: Vec::new(),
                        param_names: Vec::new(),
                        recv_name: None,
                        arity: 0,
                        type_params: type_params.clone(),
                        owner_name: String::new(),
                        case_index: 0,
                        payload_annos: Vec::new(),
                        fields: Vec::new(),
                        field_annos: Vec::new(),
                        methods: BTreeMap::new(),
                        nominal: Some(nominal),
                        is_extern: false,
                        is_builtin: false,
                    });
                    self.type_scopes[0].insert(name.clone(), id);
                    for (i, c) in cases.iter().enumerate() {
                        if self.builtin.contains_key(&c.name) {
                            self.err(
                                c.span,
                                DiagnosticCode::E2006,
                                format!("case {} 与内建名冲突", c.name),
                            );
                            continue;
                        }
                        let cs = Symbol {
                            id: SymbolId(0),
                            kind: SymbolKind::Ctor,
                            name: c.name.clone(),
                            span: c.span,
                            mutable: false,
                            type_ex: None,
                            param_annos: Vec::new(),
                            param_names: Vec::new(),
                            recv_name: None,
                            arity: c.payloads.len(),
                            type_params: Vec::new(),
                            owner_name: name.clone(),
                            case_index: i as u32,
                            payload_annos: c.payloads.iter().map(|p| Some(p.clone())).collect(),
                            fields: Vec::new(),
                            field_annos: Vec::new(),
                            methods: BTreeMap::new(),
                            nominal: None,
                            is_extern: false,
                            is_builtin: false,
                        };
                        let cid = self.new_sym(cs);
                        self.define_ctor(&c.name.clone(), c.span, cid);
                    }
                }
                _ => {}
            }
        }
    }

    fn struct_symbol(
        &self,
        name: &str,
        span: &Span,
        fields: &[Param],
        nominal: NominalRef,
        type_params: Vec<String>,
    ) -> Symbol {
        Symbol {
            id: SymbolId(0),
            kind: SymbolKind::Struct,
            name: name.to_string(),
            span: *span,
            mutable: false,
            type_ex: None,
            param_annos: Vec::new(),
            param_names: Vec::new(),
            recv_name: None,
            arity: 0,
            type_params,
            owner_name: String::new(),
            case_index: 0,
            payload_annos: Vec::new(),
            fields: fields.iter().map(|f| f.name.clone()).collect(),
            field_annos: fields.iter().map(|f| f.typ.clone()).collect(),
            methods: BTreeMap::new(),
            nominal: Some(nominal),
            is_extern: false,
            is_builtin: false,
        }
    }

    /// 第二步：顶层类型 def 表注册（字段/载荷注解静默解析；此时全部顶层类型名可见，
    /// 前向类型引用与证据的惰性解析同效）。
    fn register_top_defs(&mut self, prog: &[Stmt]) {
        for s in prog {
            match s {
                Stmt::StructDecl {
                    name,
                    fields,
                    type_params,
                    ..
                } => {
                    let Some(&sid) = self.type_scopes[0].get(name) else {
                        continue;
                    };
                    let tps = type_params.clone();
                    let mut defs = Vec::new();
                    for f in fields {
                        let ty = match &f.typ {
                            Some(t) => resolve_anno_quiet(self, t, &tps),
                            None => TypeTables::UNKNOWN,
                        };
                        defs.push(FieldDef {
                            name: f.name.clone(),
                            ty,
                        });
                    }
                    if let Some(NominalRef::Struct(def_id)) = self.arena.get(sid).nominal {
                        self.tables.struct_def_mut(def_id).fields = defs;
                    }
                }
                Stmt::EnumDecl {
                    name,
                    cases,
                    type_params,
                    ..
                } => {
                    let Some(&sid) = self.type_scopes[0].get(name) else {
                        continue;
                    };
                    let tps = type_params.clone();
                    let mut defs = Vec::new();
                    for c in cases {
                        let payloads: Vec<TypeId> = c
                            .payloads
                            .iter()
                            .map(|p| resolve_anno_quiet(self, p, &tps))
                            .collect();
                        defs.push(EnumCaseDef {
                            name: c.name.clone(),
                            payloads,
                        });
                    }
                    if let Some(NominalRef::Enum(def_id)) = self.arena.get(sid).nominal {
                        self.tables.enum_def_mut(def_id).cases = defs;
                    }
                }
                _ => {}
            }
        }
    }

    // ———— 第二遍：收集 fn/extern/method 符号（体在第四遍走）———

    fn collect_fns(&mut self, prog: &[Stmt]) {
        for s in prog {
            match s {
                Stmt::FnDecl {
                    span,
                    name,
                    recv: Some(recv),
                    params,
                    ret,
                    ..
                } => {
                    self.collect_method(span, name, recv, params, ret.as_ref());
                }
                Stmt::FnDecl { span, name, .. } => {
                    if let Some(old) = self.lookup_top(name) {
                        let old_line = self.arena.get(old).span.start.line;
                        self.err(
                            *span,
                            DiagnosticCode::E2001,
                            format!("fn {name} 重复定义（先前定义于第 {old_line} 行）"),
                        );
                        continue;
                    }
                    let sym = self.make_fn_symbol(s, false);
                    let id = self.new_sym(sym);
                    self.scopes[0].insert(name.clone(), id);
                }
                Stmt::Extern { span, fns, .. } => {
                    for f in fns {
                        let Stmt::FnDecl { name, .. } = f else {
                            continue;
                        };
                        if let Some(old) = self.lookup_top(name) {
                            let old_line = self.arena.get(old).span.start.line;
                            self.err(
                                *span,
                                DiagnosticCode::E2001,
                                format!("fn {name} 重复定义（先前定义于第 {old_line} 行）"),
                            );
                            continue;
                        }
                        let sym = self.make_fn_symbol(f, true);
                        let id = self.new_sym(sym);
                        self.scopes[0].insert(name.clone(), id);
                    }
                }
                _ => {}
            }
        }
    }

    fn make_fn_symbol(&self, d: &Stmt, is_extern: bool) -> Symbol {
        let Stmt::FnDecl {
            span,
            name,
            type_params,
            params,
            ret,
            ..
        } = d
        else {
            unreachable!("make_fn_symbol 只接受 FnDecl（解析器保证的内部不变量）");
        };
        Symbol {
            id: SymbolId(0),
            kind: SymbolKind::Fn,
            name: name.clone(),
            span: *span,
            mutable: false,
            type_ex: ret.clone(),
            param_annos: params.iter().map(|p| p.typ.clone()).collect(),
            param_names: params.iter().map(|p| p.name.clone()).collect(),
            recv_name: None,
            arity: params.len(),
            type_params: type_params.clone(),
            owner_name: String::new(),
            case_index: 0,
            payload_annos: Vec::new(),
            fields: Vec::new(),
            field_annos: Vec::new(),
            methods: BTreeMap::new(),
            nominal: None,
            is_extern,
            is_builtin: false,
        }
    }

    fn collect_method(
        &mut self,
        span: &Span,
        name: &str,
        recv: &Param,
        params: &[Param],
        ret: Option<&TypeEx>,
    ) {
        let Some(TypeEx::Named { name: rt_name, .. }) = &recv.typ else {
            self.err(
                *span,
                DiagnosticCode::E2004,
                "方法接收者须为具名 struct 类型",
            );
            return;
        };
        let st = self.type_scopes[0].get(rt_name).copied();
        let Some(st) = st.filter(|id| self.arena.get(*id).kind == SymbolKind::Struct) else {
            self.err(
                *span,
                DiagnosticCode::E2004,
                format!("为未知 struct {rt_name} 定义方法"),
            );
            return;
        };
        if self.arena.get(st).methods.contains_key(name) {
            self.err(
                *span,
                DiagnosticCode::E2001,
                format!("方法 {name} 在 struct {rt_name} 上重复定义"),
            );
            return;
        }
        let sym = Symbol {
            id: SymbolId(0),
            kind: SymbolKind::Method,
            name: name.to_string(),
            span: *span,
            mutable: false,
            type_ex: ret.cloned(),
            param_annos: params.iter().map(|p| p.typ.clone()).collect(),
            param_names: params.iter().map(|p| p.name.clone()).collect(),
            recv_name: Some(rt_name.clone()),
            arity: params.len(),
            type_params: Vec::new(),
            owner_name: rt_name.clone(),
            case_index: 0,
            payload_annos: Vec::new(),
            fields: Vec::new(),
            field_annos: Vec::new(),
            methods: BTreeMap::new(),
            nominal: None,
            is_extern: false,
            is_builtin: false,
        };
        let id = self.new_sym(sym);
        self.arena.get_mut(st).methods.insert(name.to_string(), id);
    }

    // ———— 第三遍：顶层绑定依序解析 ————

    fn bind_globals(&mut self, prog: &[Stmt]) {
        for s in prog {
            match s {
                Stmt::Let {
                    span,
                    name,
                    mutable,
                    typ,
                    init,
                } => {
                    self.expr(init);
                    if name == "_" {
                        continue;
                    }
                    if let Some(old) = self.lookup_top(name) {
                        let old_line = self.arena.get(old).span.start.line;
                        self.err(
                            *span,
                            DiagnosticCode::E2001,
                            format!("绑定 {name} 在此作用域重复定义（先前定义于第 {old_line} 行）"),
                        );
                        continue;
                    }
                    let id = self.new_sym(Symbol {
                        id: SymbolId(0),
                        kind: SymbolKind::Var,
                        name: name.clone(),
                        span: *span,
                        mutable: *mutable,
                        type_ex: typ.clone(),
                        param_annos: Vec::new(),
                        param_names: Vec::new(),
                        recv_name: None,
                        arity: 0,
                        type_params: Vec::new(),
                        owner_name: String::new(),
                        case_index: 0,
                        payload_annos: Vec::new(),
                        fields: Vec::new(),
                        field_annos: Vec::new(),
                        methods: BTreeMap::new(),
                        nominal: None,
                        is_extern: false,
                        is_builtin: false,
                    });
                    self.scopes[0].insert(name.clone(), id);
                    self.decl_syms.insert(stmt_key(s), id);
                }
                Stmt::FnDecl { .. }
                | Stmt::StructDecl { .. }
                | Stmt::EnumDecl { .. }
                | Stmt::Extern { .. }
                | Stmt::Import { .. } => {}
                other => self.stmt(other),
            }
        }
    }

    // ———— 第四遍：函数体 ————

    fn bind_fns(&mut self, prog: &[Stmt]) {
        for s in prog {
            if let Stmt::FnDecl {
                name, recv: None, ..
            } = s
            {
                let sym = self.scopes[0].get(name).copied();
                if let Some(sym) = sym {
                    self.decl_syms.insert(stmt_key(s), sym);
                }
                self.fn_scope(s, sym);
            }
        }
        for s in prog {
            if let Stmt::FnDecl {
                recv: Some(recv),
                name,
                ..
            } = s
            {
                let Some(TypeEx::Named { name: rt_name, .. }) = &recv.typ else {
                    continue;
                };
                let Some(&st) = self.type_scopes[0].get(rt_name) else {
                    continue;
                };
                if self.arena.get(st).kind != SymbolKind::Struct {
                    continue;
                }
                let sym = self.arena.get(st).methods.get(name).copied();
                if let Some(sym) = sym {
                    self.decl_syms.insert(stmt_key(s), sym);
                    self.fn_scope(s, Some(sym));
                }
            }
        }
    }

    fn param_symbol(name: &str, span: Span, typ: Option<&TypeEx>) -> Symbol {
        Symbol {
            id: SymbolId(0),
            kind: SymbolKind::Param,
            name: name.to_string(),
            span,
            mutable: false,
            type_ex: typ.cloned(),
            param_annos: Vec::new(),
            param_names: Vec::new(),
            recv_name: None,
            arity: 0,
            type_params: Vec::new(),
            owner_name: String::new(),
            case_index: 0,
            payload_annos: Vec::new(),
            fields: Vec::new(),
            field_annos: Vec::new(),
            methods: BTreeMap::new(),
            nominal: None,
            is_extern: false,
            is_builtin: false,
        }
    }

    /// 函数体作用域：接收者 + 参数，fn_depth 保护 return。
    /// 参数符号按序记入 fn_params[fn 符号]（接收者在前；`_` 跳过），
    /// 供 checker/HIR 对位取类型。
    fn fn_scope(&mut self, d: &Stmt, sym: Option<SymbolId>) {
        let Stmt::FnDecl {
            span,
            recv,
            params,
            body,
            ..
        } = d
        else {
            return;
        };
        self.push_scope();
        let mut psyms: Vec<SymbolId> = Vec::new();
        if let Some(recv) = recv {
            let id = self.new_sym(Self::param_symbol(&recv.name, *span, recv.typ.as_ref()));
            self.define(&recv.name.clone(), *span, SymbolKind::Param, id);
            psyms.push(id);
        }
        for p in params {
            if p.name == "_" {
                continue;
            }
            let id = self.new_sym(Self::param_symbol(&p.name, *span, p.typ.as_ref()));
            self.define(&p.name.clone(), *span, SymbolKind::Param, id);
            psyms.push(id);
        }
        if let Some(sym) = sym {
            self.fn_params.insert(sym, psyms);
        }
        self.fn_depth += 1;
        for s in body {
            self.stmt(s);
        }
        self.fn_depth -= 1;
        self.pop_scope();
    }

    fn stmt(&mut self, s: &Stmt) {
        match s {
            Stmt::Let {
                span,
                name,
                mutable,
                typ,
                init,
            } => {
                self.expr(init);
                if name == "_" {
                    return;
                }
                let id = self.new_sym(Symbol {
                    id: SymbolId(0),
                    kind: SymbolKind::Var,
                    name: name.clone(),
                    span: *span,
                    mutable: *mutable,
                    type_ex: typ.clone(),
                    param_annos: Vec::new(),
                    param_names: Vec::new(),
                    recv_name: None,
                    arity: 0,
                    type_params: Vec::new(),
                    owner_name: String::new(),
                    case_index: 0,
                    payload_annos: Vec::new(),
                    fields: Vec::new(),
                    field_annos: Vec::new(),
                    methods: BTreeMap::new(),
                    nominal: None,
                    is_extern: false,
                    is_builtin: false,
                });
                self.decl_syms.insert(stmt_key(s), id);
                self.define(name, *span, SymbolKind::Var, id);
            }

            Stmt::Assign {
                span,
                target,
                value,
                ..
            } => {
                if let Expr::Ident {
                    span: id_span,
                    name,
                } = target
                {
                    match self.lookup(name) {
                        None => {
                            self.err(
                                *id_span,
                                DiagnosticCode::E2002,
                                format!("未定义变量 {name}"),
                            );
                        }
                        Some(sym) if !self.arena.get(sym).mutable => {
                            self.err(
                                *span,
                                DiagnosticCode::E2005,
                                format!("{name} 由 let 绑定，不可重新赋值"),
                            );
                        }
                        Some(sym) => {
                            self.refs.insert(expr_key(target), sym);
                        }
                    }
                } else {
                    self.assign_target(target);
                }
                self.expr(value);
            }

            Stmt::ExprStmt { expr, .. } => self.expr(expr),

            Stmt::Return { span, value } => {
                if self.fn_depth == 0 {
                    self.err(*span, DiagnosticCode::E2008, "return 出现在函数之外");
                }
                if let Some(v) = value {
                    self.expr(v);
                }
            }

            Stmt::If {
                cond,
                then_body,
                else_body,
                ..
            } => {
                self.expr(cond);
                self.block(then_body);
                if let Some(els) = else_body {
                    self.block(els);
                }
            }

            Stmt::While { cond, body, .. } => {
                self.expr(cond);
                self.loop_depth += 1;
                self.block(body);
                self.loop_depth -= 1;
            }

            Stmt::ForIn {
                span,
                variable,
                iter,
                body,
            } => {
                self.expr(iter);
                self.push_scope();
                if variable != "_" {
                    let id = self.new_sym(Symbol {
                        id: SymbolId(0),
                        kind: SymbolKind::Var,
                        name: variable.clone(),
                        span: *span,
                        mutable: false,
                        type_ex: None,
                        param_annos: Vec::new(),
                        param_names: Vec::new(),
                        recv_name: None,
                        arity: 0,
                        type_params: Vec::new(),
                        owner_name: String::new(),
                        case_index: 0,
                        payload_annos: Vec::new(),
                        fields: Vec::new(),
                        field_annos: Vec::new(),
                        methods: BTreeMap::new(),
                        nominal: None,
                        is_extern: false,
                        is_builtin: false,
                    });
                    self.decl_syms.insert(stmt_key(s), id);
                    self.define(variable, *span, SymbolKind::Var, id);
                }
                self.loop_depth += 1;
                for b in body {
                    self.stmt(b);
                }
                self.loop_depth -= 1;
                self.pop_scope();
            }

            Stmt::Break { span } => {
                if self.loop_depth == 0 {
                    self.err(*span, DiagnosticCode::E2007, "break 出现在循环之外");
                }
            }
            Stmt::Continue { span } => {
                if self.loop_depth == 0 {
                    self.err(*span, DiagnosticCode::E2007, "continue 出现在循环之外");
                }
            }

            Stmt::FnDecl { recv: Some(_), .. } => {} // 方法体在 bind_fns 统一走

            Stmt::FnDecl { name, .. } => {
                // 嵌套 fn：定义进当前作用域
                let id = self.new_sym(self.make_fn_symbol(s, false));
                self.decl_syms.insert(stmt_key(s), id);
                self.define(name, s.span(), SymbolKind::Fn, id);
                self.fn_scope(s, Some(id));
            }

            Stmt::StructDecl {
                span,
                name,
                fields,
                type_params,
            } => {
                // 嵌套类型声明：E2010（类型集合须平坦，NAME-NEST-009）
                self.err(
                    *span,
                    DiagnosticCode::E2010,
                    format!("嵌套类型声明不支持（struct {name} 须在顶层声明）"),
                );
                let absent = !self
                    .type_scopes
                    .last()
                    .is_some_and(|top| top.contains_key(name));
                if absent {
                    let nominal = NominalRef::Struct(self.tables.register_struct(StructDef {
                        name: name.clone(),
                        fields: Vec::new(),
                    }));
                    // 嵌套类型字段注解就地静默解析
                    let tps = type_params.clone();
                    let mut defs = Vec::new();
                    for f in fields {
                        let ty = match &f.typ {
                            Some(t) => resolve_anno_quiet(self, t, &tps),
                            None => TypeTables::UNKNOWN,
                        };
                        defs.push(FieldDef {
                            name: f.name.clone(),
                            ty,
                        });
                    }
                    if let NominalRef::Struct(def_id) = nominal {
                        self.tables.struct_def_mut(def_id).fields = defs;
                    }
                    let id = self.new_sym(self.struct_symbol(
                        name,
                        span,
                        fields,
                        nominal,
                        type_params.clone(),
                    ));
                    if let Some(top) = self.type_scopes.last_mut() {
                        top.insert(name.clone(), id);
                    }
                }
            }

            Stmt::EnumDecl {
                span,
                name,
                type_params,
                ..
            } => {
                self.err(
                    *span,
                    DiagnosticCode::E2010,
                    format!("嵌套类型声明不支持（enum {name} 须在顶层声明）"),
                );
                let absent = !self
                    .type_scopes
                    .last()
                    .is_some_and(|top| top.contains_key(name));
                if absent {
                    let nominal = NominalRef::Enum(self.tables.register_enum(EnumDef {
                        name: name.clone(),
                        cases: Vec::new(),
                    }));
                    let id = self.new_sym(Symbol {
                        id: SymbolId(0),
                        kind: SymbolKind::Enum,
                        name: name.clone(),
                        span: *span,
                        mutable: false,
                        type_ex: None,
                        param_annos: Vec::new(),
                        param_names: Vec::new(),
                        recv_name: None,
                        arity: 0,
                        type_params: type_params.clone(),
                        owner_name: String::new(),
                        case_index: 0,
                        payload_annos: Vec::new(),
                        fields: Vec::new(),
                        field_annos: Vec::new(),
                        methods: BTreeMap::new(),
                        nominal: Some(nominal),
                        is_extern: false,
                        is_builtin: false,
                    });
                    if let Some(top) = self.type_scopes.last_mut() {
                        top.insert(name.clone(), id);
                    }
                }
            }

            Stmt::Extern { fns, .. } => {
                for f in fns {
                    let Stmt::FnDecl { name, .. } = f else {
                        continue;
                    };
                    if self.lookup(name).is_none() {
                        let id = self.new_sym(self.make_fn_symbol(f, true));
                        self.define(name, f.span(), SymbolKind::Fn, id);
                    }
                }
            }

            Stmt::Go { span, call } => {
                if let Expr::Call { callee, args, .. } = call {
                    if let Expr::Ident {
                        span: id_span,
                        name,
                    } = callee.as_ref()
                    {
                        let ok = self
                            .lookup(name)
                            .map(|sym| self.arena.get(sym).kind == SymbolKind::Fn)
                            .unwrap_or(false);
                        if ok {
                            let sym = self.lookup(name);
                            if let Some(sym) = sym {
                                self.refs.insert(expr_key(callee.as_ref()), sym);
                            }
                        } else {
                            self.err(
                                *id_span,
                                DiagnosticCode::E2002,
                                format!("go 的目标须是顶层函数：{name}"),
                            );
                        }
                    } else {
                        self.err(*span, DiagnosticCode::E2002, "go 的目标须是具名函数调用");
                    }
                    for a in args {
                        self.expr(a);
                    }
                }
            }

            Stmt::Import { .. } => {} // 装载器已展开
        }
    }

    fn block(&mut self, stmts: &[Stmt]) {
        self.push_scope();
        for s in stmts {
            self.stmt(s);
        }
        self.pop_scope();
    }

    /// 字段/索引赋值目标：只走子表达式（形状合法性由解析器保证）。
    fn assign_target(&mut self, t: &Expr) {
        match t {
            Expr::Field { obj, .. } => self.expr(obj),
            Expr::Index { obj, idx, .. } => {
                self.expr(obj);
                self.expr(idx);
            }
            other => self.expr(other),
        }
    }

    fn expr(&mut self, e: &Expr) {
        match e {
            Expr::Ident { span, name } => {
                if let Some(sym) = self.lookup(name) {
                    self.refs.insert(expr_key(e), sym);
                    return;
                }
                if let Some(ctor) = self.lookup_ctor(name) {
                    self.refs.insert(expr_key(e), ctor);
                    return;
                }
                if self.builtin.contains_key(name) {
                    return; // 裸内建引用是否合法由 checker 判定
                }
                self.err(*span, DiagnosticCode::E2002, format!("未定义 {name}"));
            }

            Expr::Unary { operand, .. } => self.expr(operand),
            Expr::Binary { lhs, rhs, .. } => {
                self.expr(lhs);
                self.expr(rhs);
            }
            Expr::Range { lo, hi, .. } => {
                self.expr(lo);
                self.expr(hi);
            }
            Expr::Call { callee, args, .. } => {
                self.expr(callee);
                for a in args {
                    self.expr(a);
                }
            }
            Expr::Index { obj, idx, .. } => {
                self.expr(obj);
                self.expr(idx);
            }
            Expr::Slice { obj, lo, hi, .. } => {
                self.expr(obj);
                self.expr(lo);
                self.expr(hi);
            }
            Expr::Field { obj, .. } => self.expr(obj),
            Expr::Try { operand, .. } => self.expr(operand),

            Expr::StructLit {
                span,
                type_name,
                fields,
            } => {
                match self.lookup_type(type_name) {
                    Some(sym) => {
                        self.refs.insert(expr_key(e), sym);
                    }
                    None => {
                        self.err(
                            *span,
                            DiagnosticCode::E2002,
                            format!("未知 struct {type_name}"),
                        );
                    }
                }
                for f in fields {
                    self.expr(&f.value);
                }
            }

            Expr::MapLit { items, .. } => {
                for item in items {
                    self.expr(&item.key);
                    self.expr(&item.value);
                }
            }
            Expr::ArrLit { items, .. } => {
                for el in items {
                    self.expr(el);
                }
            }

            Expr::Match {
                scrutinee, arms, ..
            } => {
                self.expr(scrutinee);
                for arm in arms {
                    self.push_scope();
                    self.pat(&arm.pat);
                    self.expr(&arm.body);
                    self.pop_scope();
                }
            }

            Expr::FnLit { params, body, .. } => {
                self.push_scope();
                for p in params {
                    if p.name != "_" {
                        let id = self.new_sym(Self::param_symbol(&p.name, p.span, p.typ.as_ref()));
                        self.define(&p.name.clone(), p.span, SymbolKind::Param, id);
                    }
                }
                self.fn_depth += 1;
                for s in body {
                    self.stmt(s);
                }
                self.fn_depth -= 1;
                self.pop_scope();
            }

            Expr::IntLit { .. }
            | Expr::FloatLit { .. }
            | Expr::StrLit { .. }
            | Expr::BoolLit { .. } => {}
        }
    }

    fn pat(&mut self, p: &Pat) {
        match p {
            Pat::Wild { .. } => {}
            Pat::Bind { span, name } => {
                let id = self.new_sym(Symbol {
                    id: SymbolId(0),
                    kind: SymbolKind::Var,
                    name: name.clone(),
                    span: *span,
                    mutable: false,
                    type_ex: None,
                    param_annos: Vec::new(),
                    param_names: Vec::new(),
                    recv_name: None,
                    arity: 0,
                    type_params: Vec::new(),
                    owner_name: String::new(),
                    case_index: 0,
                    payload_annos: Vec::new(),
                    fields: Vec::new(),
                    field_annos: Vec::new(),
                    methods: BTreeMap::new(),
                    nominal: None,
                    is_extern: false,
                    is_builtin: false,
                });
                self.pat_refs.insert(pat_key(p), id);
                self.define(name, *span, SymbolKind::Var, id);
            }
            Pat::Lit { value, .. } => self.expr(value),
            Pat::Case { span, name, subs } => {
                match self.lookup_ctor(name) {
                    Some(sym) => {
                        self.pat_refs.insert(pat_key(p), sym);
                    }
                    None => {
                        self.err(*span, DiagnosticCode::E2002, format!("未知构造器 {name}"));
                    }
                }
                for sp in subs {
                    self.pat(sp);
                }
            }
        }
    }
}

/// 借用驱动整个解析（prog 为装载器合并后的语句序列，NAME-MERGE-010）。
pub(crate) fn resolve_program(prog: &[Stmt], _source: SourceId) -> ResolveOutput {
    let mut r = Resolver::new();
    r.prelude();
    r.scopes = vec![BTreeMap::new()];
    r.type_scopes = vec![BTreeMap::new()];
    r.collect_types(prog);
    r.register_top_defs(prog);
    r.collect_fns(prog);
    r.bind_globals(prog);
    r.bind_fns(prog);
    r.top_types = r.scopes.first().cloned().unwrap_or_default();
    ResolveOutput {
        arena: r.arena,
        refs: r.refs,
        pat_refs: r.pat_refs,
        decl_syms: r.decl_syms,
        fn_params: r.fn_params,
        builtin: r.builtin,
        top_types: r.top_types,
        tables: r.tables,
        diags: r.diags,
    }
}
