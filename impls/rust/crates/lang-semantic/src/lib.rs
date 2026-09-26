//! lang-semantic —— 名字解析与类型检查（接口契约见 docs/lang-rs/interface-contract.md）。
//!
//! 分层：
//! - [`resolver`]：NAME-* 十条规则全覆盖（E2001..E2010），多遍扫描与 interp 顺序语义一致；
//! - [`anno`]：注解 → 语义类型（静默/检查器双通道）；
//! - [`checker`]：E3001..E3014、E4001..E4003 全量发射 + 每表达式类型（HIR/IR 消费）；
//! - [`builtin`]：20 内建名面 + 返回类型特化（与 lang-runtime 冻结名单一致）；
//! - [`Analysis`]：语义分析入口产物（符号表/类型表/诊断集/查询视图）。
//!
//! 入口（冻结）：[`analyze_stmts`]（模块合并后的语句序列）/ [`analyze_source`]（直吃源码）。
//! 行为真值：specs/ 优先于实现证据；两处 spec 修正见 checker 模块注。
#![forbid(unsafe_code)]

pub mod anno;
pub mod builtin;
pub mod checker;
pub mod resolver;
pub mod symbols;

use std::collections::HashMap;

use lang_core::{Diagnostic, DiagnosticCode, SourceId, SymbolId, TypeId};
use lang_syntax::{Expr, Pat, Stmt};
use lang_types::TypeTables;

use checker::CheckOutput;
use symbols::SymbolArena;

pub use checker::MatchKind;
pub use symbols::{NominalRef, Symbol, SymbolKind};

/// 语义分析产物。
///
/// 除诊断与表之外，提供按 AST 节点查询的视图（内部以节点指针为键；查询时
/// 必须传入**同一次分析所消费的那棵 AST** 的引用，跨树查询返回 None）。
pub struct Analysis {
    /// 全部诊断（resolver 在前；resolver 有诊断时 checker 不再运行，`checked=false`）。
    pub diags: Vec<Diagnostic>,
    /// 类型 arena（含名义 def 表与预载面）。
    pub types: TypeTables,
    /// 符号表（含预载符号；id 即创建序）。
    pub symbols: SymbolArena,
    /// 分析入口的源身份。
    pub source: SourceId,
    /// checker 是否已运行（resolver 无诊断时为 true）。
    pub checked: bool,
    expr_ty: HashMap<*const Expr, TypeId>,
    pat_ty: HashMap<*const Pat, TypeId>,
    sym_ty: HashMap<SymbolId, TypeId>,
    fn_ty: HashMap<SymbolId, TypeId>,
    decl_syms: HashMap<*const Stmt, SymbolId>,
    refs: HashMap<*const Expr, SymbolId>,
    pat_refs: HashMap<*const Pat, SymbolId>,
    match_kind: HashMap<*const Expr, MatchKind>,
}

impl Analysis {
    /// 是否存在任何诊断（v0.4 全为 error 级）。
    #[must_use]
    pub fn has_errors(&self) -> bool {
        !self.diags.is_empty()
    }

    /// 表达式静态类型（checker 未运行时为 None）。
    #[must_use]
    pub fn type_of(&self, e: &Expr) -> Option<TypeId> {
        self.expr_ty.get(&(e as *const Expr)).copied()
    }

    /// 模式绑定的捕获类型。
    #[must_use]
    pub fn pat_type(&self, p: &Pat) -> Option<TypeId> {
        self.pat_ty.get(&(p as *const Pat)).copied()
    }

    /// 绑定/参数符号的类型。
    #[must_use]
    pub fn symbol_type(&self, id: SymbolId) -> Option<TypeId> {
        self.sym_ty.get(&id).copied()
    }

    /// fn/方法符号的函数类型。
    #[must_use]
    pub fn fn_type(&self, id: SymbolId) -> Option<TypeId> {
        self.fn_ty.get(&id).copied()
    }

    /// match 表达式的语义分类。
    #[must_use]
    pub fn match_kind(&self, e: &Expr) -> Option<MatchKind> {
        self.match_kind.get(&(e as *const Expr)).copied()
    }

    /// Ident/StructLit 引用解析到的符号。
    #[must_use]
    pub fn symbol_of_ref(&self, e: &Expr) -> Option<SymbolId> {
        self.refs.get(&(e as *const Expr)).copied()
    }

    /// 模式（绑定/构造）解析到的符号。
    #[must_use]
    pub fn symbol_of_pat(&self, p: &Pat) -> Option<SymbolId> {
        self.pat_refs.get(&(p as *const Pat)).copied()
    }

    /// 声明语句（Let/FnDecl/ForIn）对应的符号。
    #[must_use]
    pub fn symbol_of_decl(&self, s: &Stmt) -> Option<SymbolId> {
        self.decl_syms.get(&(s as *const Stmt)).copied()
    }
}

/// 语义分析入口（冻结）：输入为**装载器合并后的语句序列**（NAME-MERGE-010，
/// 跨文件同名符号按 NAME-DUP-003 报错）。有诊断时仍返回部分产物。
#[must_use]
pub fn analyze_stmts(stmts: &[Stmt], source: SourceId) -> Analysis {
    let mut res = resolver::resolve_program(stmts, source);
    let resolve_diags = std::mem::take(&mut res.diags);
    let checked = resolve_diags.is_empty();
    if !checked {
        // 与证据管线一致：解析期有诊断则不再跑检查器（避免级联噪声）
        return Analysis {
            diags: resolve_diags,
            types: res.tables,
            symbols: res.arena,
            source,
            checked: false,
            expr_ty: HashMap::new(),
            pat_ty: HashMap::new(),
            sym_ty: HashMap::new(),
            fn_ty: HashMap::new(),
            decl_syms: res.decl_syms,
            refs: res.refs,
            pat_refs: res.pat_refs,
            match_kind: HashMap::new(),
        };
    }
    let checker = checker::Checker::new(&mut res);
    let CheckOutput {
        expr_ty,
        pat_ty,
        sym_ty,
        fn_ty,
        match_kind,
        tables,
        diags,
    } = checker.check(stmts);
    Analysis {
        diags,
        types: tables,
        symbols: res.arena,
        source,
        checked: true,
        expr_ty,
        pat_ty,
        sym_ty,
        fn_ty,
        decl_syms: res.decl_syms,
        refs: res.refs,
        pat_refs: res.pat_refs,
        match_kind,
    }
}

/// 源码直入口（冻结）：parse → analyze_stmts；解析失败时产物仅含 E1001 诊断。
#[must_use]
pub fn analyze_source(text: &lang_core::SourceText, source: SourceId) -> Analysis {
    match lang_syntax::parse(text, source) {
        Ok(stmts) => analyze_stmts(&stmts, source),
        Err(d) => Analysis {
            diags: vec![d],
            types: TypeTables::new(),
            symbols: SymbolArena::new(),
            source,
            checked: false,
            expr_ty: HashMap::new(),
            pat_ty: HashMap::new(),
            sym_ty: HashMap::new(),
            fn_ty: HashMap::new(),
            decl_syms: HashMap::new(),
            refs: HashMap::new(),
            pat_refs: HashMap::new(),
            match_kind: HashMap::new(),
        },
    }
}

/// 注解解析公共入口（HIR 构建阶段复用；静默通道，坏 arity 保守回退不报错）。
/// 前置条件：`analysis` 无诊断（注解合法）。
#[must_use]
pub fn resolve_annotation(
    analysis: &mut Analysis,
    t: &lang_syntax::TypeEx,
    tparams: &[String],
) -> TypeId {
    struct AnnoCtx<'a> {
        tables: &'a mut TypeTables,
        symbols: &'a SymbolArena,
    }
    impl anno::TypeCtx for AnnoCtx<'_> {
        fn tables_mut(&mut self) -> &mut TypeTables {
            self.tables
        }
        fn arena(&self) -> &SymbolArena {
            self.symbols
        }
        fn lookup_named(&self, name: &str) -> Option<SymbolId> {
            // 预载 error → 顶层 → arena 首个同名（与 resolver/checker 同序）
            for s in self.symbols.iter() {
                if s.is_builtin && s.kind == SymbolKind::Struct && s.name == name {
                    return Some(s.id);
                }
            }
            self.symbols.first_type_named(name)
        }
        fn emit(&mut self, _code: DiagnosticCode, _msg: String) {}
    }
    let mut ctx = AnnoCtx {
        tables: &mut analysis.types,
        symbols: &analysis.symbols,
    };
    anno::resolve_anno_quiet(&mut ctx, t, tparams)
}

/// 测试辅助：源码 → (诊断码, 行号) 序列（行 0 = 无锚）。
#[cfg(test)]
pub(crate) fn check_source(src: &str) -> Vec<(DiagnosticCode, u32)> {
    let source = SourceId(7);
    let text = lang_core::SourceText::new("test.lang-go", src.to_string());
    let stmts = lang_syntax::parse(&text, source).unwrap_or_default();
    analyze_stmts(&stmts, source)
        .diags
        .iter()
        .map(|d| (d.code, d.primary_span.map(|s| s.start.line).unwrap_or(0)))
        .collect()
}
