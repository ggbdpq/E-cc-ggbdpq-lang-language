//! Analysis → Graph 填充器：把一次语义分析的符号/引用事实落成语义图。
//!
//! 边语义（与冻结 [`EdgeKind`] 一一对应，证据：impls/go/internal/graph/build.go
//! 的引用/写入/类型分类 + 本仓 specs/agent-protocol.md 的 inspect 面）：
//! - `defines`：模块 → 顶层声明（fn/method/绑定/struct/enum/extern）；
//! - `contains`：struct → 方法、fn → 参数/内部绑定/嵌套 fn、enum → 构造器；
//! - `calls`：Call 的 callee Ident（go 语句除外）；`spawn`：go 语句目标；
//! - `writes`：赋值目标 Ident；`reads`：读位置 Ident；
//! - `references`：StructLit 类型引用 + match 构造模式引用；
//! - `imports`：模块 → 被导入文件节点（`NodeKind::File`）；
//! - `returns`：fn/method → 返回注解指向的用户 struct/enum 符号。
//!
//! 全部事实取自 [`Analysis`] 的符号表与引用映射（SymbolId 事实，禁文本搜索）；
//! 指向预载符号（println/Some/None/Ok/Err…）的引用不落边（预载符号不建节点）。
//!
//! 节点身份：arena 内符号沿用 SymbolId；模块/File 节点为合成 id，自
//! `symbols.len()` 起连续分配（与 arena 编号空间不撞，确定性）。
//! 已知缺口（本波登记）：方法调用边（`recv.method()`）依赖接收者静态类型，
//! 等检查器类型面在图上开放后补——直接调用/go/读/写/类型引用已全覆盖。

use lang_core::{SourceId, SymbolId};
use lang_semantic::{Analysis, SymbolKind};
use lang_syntax::{Expr, Pat, Stmt, TypeEx};

use crate::{EdgeKind, Graph, GraphEdge, GraphError, NodeKind};

/// 引用位置分类（决定 Ident 落哪类边）。
#[derive(Clone, Copy, PartialEq, Eq)]
enum Pos {
    Read,
    Call,
    Write,
    Spawn,
}

/// 从一次分析填充语义图。`stmts` 必须与 `analysis` 消费的是同一棵 AST
/// （查询视图按节点指针取事实，跨树返回 None）。
///
/// 失败形态即 [`GraphError`]（节点/边良构性）；指向预载符号或未登记节点的
/// 边跳过（保守缺边，不虚构事实）。
pub fn from_analysis(
    stmts: &[Stmt],
    analysis: &Analysis,
    module_name: &str,
) -> Result<Graph, GraphError> {
    let mut g = Graph::new();
    // 1) 符号节点：全部非预载符号入图
    for s in analysis.symbols.iter() {
        if s.is_builtin {
            continue;
        }
        let kind = match s.kind {
            SymbolKind::Fn => NodeKind::Function,
            SymbolKind::Method => NodeKind::Method,
            SymbolKind::Var => NodeKind::Variable,
            SymbolKind::Param => NodeKind::Parameter,
            SymbolKind::Struct => NodeKind::Struct,
            SymbolKind::Enum => NodeKind::Enum,
            SymbolKind::Ctor => NodeKind::Variant,
        };
        g.add_node(s.id, kind, s.name.clone())?;
    }
    // 2) 合成节点：模块 + 被导入文件
    let module_id = SymbolId(analysis.symbols.len() as u32);
    g.add_node(module_id, NodeKind::Module, module_name)?;
    let mut file_seq = analysis.symbols.len() as u32 + 1;
    let mut file_ids: Vec<(String, SymbolId)> = Vec::new();

    // 3) 顶层 defines/imports/returns/contains（struct→method、enum→ctor）
    for s in stmts {
        match s {
            Stmt::FnDecl { recv, .. } => {
                let Some(&sym) = analysis.symbol_of_decl(s).as_ref() else {
                    continue;
                };
                connect(&mut g, module_id, EdgeKind::Defines, sym)?;
                // returns：返回注解指向的用户 struct/enum
                let ret_name = analysis
                    .symbols
                    .get(sym)
                    .type_ex
                    .as_ref()
                    .and_then(named_root);
                if let Some(name) = ret_name {
                    if let Some(target) = find_type_named(analysis, &name) {
                        connect(&mut g, sym, EdgeKind::Returns, target)?;
                    }
                }
                // 方法挂 struct（struct 符号的 methods 表是唯一事实源）
                if recv.is_some() {
                    for st in analysis.symbols.iter() {
                        if st.kind != SymbolKind::Struct || st.is_builtin {
                            continue;
                        }
                        if st.methods.values().any(|&m| m == sym) {
                            connect(&mut g, st.id, EdgeKind::Contains, sym)?;
                            break;
                        }
                    }
                }
                bind_params(&mut g, analysis, s, sym)?;
            }
            Stmt::Let { .. } => {
                if let Some(sym) = analysis.symbol_of_decl(s) {
                    connect(&mut g, module_id, EdgeKind::Defines, sym)?;
                }
            }
            Stmt::StructDecl { span, .. } => {
                if let Some(sym) =
                    symbol_at(analysis, SymbolKind::Struct, span.source, span.start.offset)
                {
                    connect(&mut g, module_id, EdgeKind::Defines, sym)?;
                }
            }
            Stmt::EnumDecl { cases, span, .. } => {
                let Some(sym) =
                    symbol_at(analysis, SymbolKind::Enum, span.source, span.start.offset)
                else {
                    continue;
                };
                connect(&mut g, module_id, EdgeKind::Defines, sym)?;
                for c in cases {
                    if let Some(ctor) = symbol_at(
                        analysis,
                        SymbolKind::Ctor,
                        c.span.source,
                        c.span.start.offset,
                    ) {
                        connect(&mut g, sym, EdgeKind::Contains, ctor)?;
                    }
                }
            }
            Stmt::Extern { fns, .. } => {
                for f in fns {
                    let Stmt::FnDecl { span, .. } = f else {
                        continue;
                    };
                    if let Some(sym) =
                        symbol_at(analysis, SymbolKind::Fn, span.source, span.start.offset)
                    {
                        connect(&mut g, module_id, EdgeKind::Defines, sym)?;
                    }
                }
            }
            Stmt::Import { path, .. } => {
                let fid = match file_ids.iter().find(|(p, _)| p == path) {
                    Some((_, id)) => *id,
                    None => {
                        let id = SymbolId(file_seq);
                        file_seq += 1;
                        g.add_node(id, NodeKind::File, path.clone())?;
                        file_ids.push((path.clone(), id));
                        id
                    }
                };
                connect(&mut g, module_id, EdgeKind::Imports, fid)?;
            }
            _ => {}
        }
    }

    // 4) 引用/调用/写/go 边（携带所属函数上下文的深度走查）
    let mut ctx = Ctx {
        g: &mut g,
        analysis,
        owner: None,
        module_id,
    };
    for s in stmts {
        match s {
            // 顶层 FnDecl 的 defines 已落；这里只补体内的引用边
            Stmt::FnDecl { body, .. } => {
                let sym = analysis.symbol_of_decl(s);
                let saved = ctx.owner;
                ctx.owner = sym;
                for b in body {
                    ctx.stmt(b);
                }
                ctx.owner = saved;
            }
            other => ctx.stmt(other),
        }
    }
    Ok(g)
}

/// fn → 参数的 contains 边（参数符号以「span=声明 span + 名字」对位，
/// 与 resolver 的建号事实一致；同名参数本身即 E2001，不产生干净程序）。
fn bind_params(
    g: &mut Graph,
    analysis: &Analysis,
    decl: &Stmt,
    fn_sym: SymbolId,
) -> Result<(), GraphError> {
    let Stmt::FnDecl {
        span, recv, params, ..
    } = decl
    else {
        return Ok(());
    };
    let mut named: Vec<&str> = Vec::new();
    if let Some(r) = recv {
        if r.name != "_" {
            named.push(&r.name);
        }
    }
    for p in params {
        if p.name != "_" {
            named.push(&p.name);
        }
    }
    for name in named {
        let found = analysis.symbols.iter().find(|s| {
            s.kind == SymbolKind::Param
                && s.span.source == span.source
                && s.span.start.offset == span.start.offset
                && s.name == name
        });
        if let Some(p) = found {
            connect(g, fn_sym, EdgeKind::Contains, p.id)?;
        }
    }
    Ok(())
}

struct Ctx<'a> {
    g: &'a mut Graph,
    analysis: &'a Analysis,
    /// 当前所属函数符号（None = 模块作用域，边从模块节点出）。
    owner: Option<SymbolId>,
    module_id: SymbolId,
}

impl Ctx<'_> {
    fn from(&self) -> SymbolId {
        self.owner.unwrap_or(self.module_id)
    }

    fn ref_edge(&mut self, e: &Expr, pos: Pos) {
        let Some(sym) = self.analysis.symbol_of_ref(e) else {
            return;
        };
        let kind = match pos {
            Pos::Read => EdgeKind::Reads,
            Pos::Call => EdgeKind::Calls,
            Pos::Write => EdgeKind::Writes,
            Pos::Spawn => EdgeKind::Spawn,
        };
        let from = self.from();
        let _ = connect(self.g, from, kind, sym);
    }

    fn stmt(&mut self, s: &Stmt) {
        match s {
            Stmt::Let { init, .. } => {
                self.expr(init, Pos::Read);
                if let Some(sym) = self.analysis.symbol_of_decl(s) {
                    if let Some(owner) = self.owner {
                        let _ = connect(self.g, owner, EdgeKind::Contains, sym);
                    }
                }
            }
            Stmt::Assign { target, value, .. } => {
                self.assign_target(target);
                self.expr(value, Pos::Read);
            }
            Stmt::ExprStmt { expr, .. } => self.expr(expr, Pos::Read),
            Stmt::Return { value, .. } => {
                if let Some(v) = value {
                    self.expr(v, Pos::Read);
                }
            }
            Stmt::If {
                cond,
                then_body,
                else_body,
                ..
            } => {
                self.expr(cond, Pos::Read);
                for b in then_body {
                    self.stmt(b);
                }
                if let Some(els) = else_body {
                    for b in els {
                        self.stmt(b);
                    }
                }
            }
            Stmt::While { cond, body, .. } => {
                self.expr(cond, Pos::Read);
                for b in body {
                    self.stmt(b);
                }
            }
            Stmt::ForIn { iter, body, .. } => {
                self.expr(iter, Pos::Read);
                if let Some(sym) = self.analysis.symbol_of_decl(s) {
                    if let Some(owner) = self.owner {
                        let _ = connect(self.g, owner, EdgeKind::Contains, sym);
                    }
                }
                for b in body {
                    self.stmt(b);
                }
            }
            Stmt::Break { .. } | Stmt::Continue { .. } => {}
            Stmt::FnDecl { body, .. } => {
                // 嵌套 fn：定义/contains + 切换所属上下文
                let sym = self.analysis.symbol_of_decl(s);
                if let (Some(owner), Some(sym)) = (self.owner, sym) {
                    let _ = connect(self.g, owner, EdgeKind::Contains, sym);
                }
                if let Some(sym) = sym {
                    let _ = bind_params(self.g, self.analysis, s, sym);
                }
                let saved = self.owner;
                self.owner = sym;
                for b in body {
                    self.stmt(b);
                }
                self.owner = saved;
            }
            Stmt::StructDecl { .. } | Stmt::EnumDecl { .. } | Stmt::Import { .. } => {}
            Stmt::Extern { .. } => {} // extern 无函数体，无引用事实
            Stmt::Go { call, .. } => {
                if let Expr::Call { callee, args, .. } = call {
                    if let Expr::Ident { .. } = callee.as_ref() {
                        self.expr(callee, Pos::Spawn);
                    }
                    for a in args {
                        self.expr(a, Pos::Read);
                    }
                }
            }
        }
    }

    fn assign_target(&mut self, t: &Expr) {
        match t {
            Expr::Ident { .. } => self.expr(t, Pos::Write),
            Expr::Field { obj, .. } => self.assign_target(obj),
            Expr::Index { obj, idx, .. } => {
                self.assign_target(obj);
                self.expr(idx, Pos::Read);
            }
            other => self.expr(other, Pos::Read),
        }
    }

    fn expr(&mut self, e: &Expr, pos: Pos) {
        match e {
            Expr::Ident { .. } => self.ref_edge(e, pos),
            Expr::Unary { operand, .. } => self.expr(operand, Pos::Read),
            Expr::Binary { lhs, rhs, .. } => {
                self.expr(lhs, Pos::Read);
                self.expr(rhs, Pos::Read);
            }
            Expr::Range { lo, hi, .. } => {
                self.expr(lo, Pos::Read);
                self.expr(hi, Pos::Read);
            }
            Expr::Call { callee, args, .. } => {
                self.expr(callee, Pos::Call);
                for a in args {
                    self.expr(a, Pos::Read);
                }
            }
            Expr::Index { obj, idx, .. } => {
                self.expr(obj, Pos::Read);
                self.expr(idx, Pos::Read);
            }
            Expr::Slice { obj, lo, hi, .. } => {
                self.expr(obj, Pos::Read);
                self.expr(lo, Pos::Read);
                self.expr(hi, Pos::Read);
            }
            Expr::Field { obj, .. } => self.expr(obj, Pos::Read),
            Expr::Try { operand, .. } => self.expr(operand, Pos::Read),
            Expr::StructLit { fields, .. } => {
                // 类型位置引用
                if let Some(sym) = self.analysis.symbol_of_ref(e) {
                    let from = self.from();
                    let _ = connect(self.g, from, EdgeKind::References, sym);
                }
                for f in fields {
                    self.expr(&f.value, Pos::Read);
                }
            }
            Expr::MapLit { items, .. } => {
                for it in items {
                    self.expr(&it.key, Pos::Read);
                    self.expr(&it.value, Pos::Read);
                }
            }
            Expr::ArrLit { items, .. } => {
                for it in items {
                    self.expr(it, Pos::Read);
                }
            }
            Expr::Match {
                scrutinee, arms, ..
            } => {
                self.expr(scrutinee, Pos::Read);
                for arm in arms {
                    self.pat(&arm.pat);
                    self.expr(&arm.body, Pos::Read);
                }
            }
            Expr::FnLit { body, .. } => {
                // FnLit 自身不是符号；体内语句照常按当前所属函数落边
                for b in body {
                    self.stmt(b);
                }
            }
            Expr::IntLit { .. }
            | Expr::FloatLit { .. }
            | Expr::StrLit { .. }
            | Expr::BoolLit { .. } => {}
        }
    }

    fn pat(&mut self, p: &Pat) {
        match p {
            Pat::Wild { .. } | Pat::Lit { .. } => {}
            Pat::Bind { .. } => {
                if let Some(sym) = self.analysis.symbol_of_pat(p) {
                    if let Some(owner) = self.owner {
                        let _ = connect(self.g, owner, EdgeKind::Contains, sym);
                    }
                }
            }
            Pat::Case { subs, .. } => {
                // 构造模式引用
                if let Some(sym) = self.analysis.symbol_of_pat(p) {
                    let from = self.from();
                    let _ = connect(self.g, from, EdgeKind::References, sym);
                }
                for sp in subs {
                    self.pat(sp);
                }
            }
        }
    }
}

/// 加边；任一端点未登记（预载符号不建节点）则跳过。
fn connect(g: &mut Graph, from: SymbolId, kind: EdgeKind, to: SymbolId) -> Result<(), GraphError> {
    if g.node(from).is_none() || g.node(to).is_none() {
        return Ok(());
    }
    g.add_edge(GraphEdge { from, kind, to })
}

/// 按（source, 起点码点）定位声明符号（struct/enum/extern fn 的声明锚；
/// resolver 不为这些语句发 decl_syms，span 同值即同一声明的结构事实）。
fn symbol_at(
    analysis: &Analysis,
    kind: SymbolKind,
    source: SourceId,
    offset: u32,
) -> Option<SymbolId> {
    analysis
        .symbols
        .iter()
        .find(|s| {
            s.kind == kind
                && !s.is_builtin
                && s.span.source == source
                && s.span.start.offset == offset
        })
        .map(|s| s.id)
}

/// 用户 struct/enum 符号（按名；类型命名空间顶层唯一，重名即 E2001 脏程序）。
fn find_type_named(analysis: &Analysis, name: &str) -> Option<SymbolId> {
    analysis
        .symbols
        .iter()
        .find(|s| {
            !s.is_builtin
                && (s.kind == SymbolKind::Struct || s.kind == SymbolKind::Enum)
                && s.name == name
        })
        .map(|s| s.id)
}

/// 命名类型的根名（`Option[int]` → "Option"；用于 returns 边判定）。
fn named_root(t: &TypeEx) -> Option<String> {
    match t {
        TypeEx::Named { name, .. } => Some(name.clone()),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use lang_core::SourceText;
    use lang_semantic::analyze_stmts;

    const SRC: &str = "\
import \"./util\"
struct Point {
    x: int
}
enum Color {
    Red
}
fn (p Point).len2(): Point {
    return Point { x: p.x }
}
fn mk(x: int): Point {
    return Point { x: x }
}
fn main() {
    var n = mk(3)
    go mk(4)
    for i in 1..3 {
        n = n + i
    }
    let m = match Color {
        Red => n
    }
    let b = n
    println(b)
}
";

    fn fill() -> (Graph, Analysis) {
        let text = SourceText::new("main.lang-go", SRC.to_string());
        let stmts = lang_syntax::parse(&text, SourceId(1)).unwrap_or_default();
        let analysis = analyze_stmts(&stmts, SourceId(1));
        let g = from_analysis(&stmts, &analysis, "main").unwrap();
        (g, analysis)
    }

    fn has_edge(g: &Graph, from: &str, kind: EdgeKind, to: &str) -> bool {
        let Some(f) = g
            .nodes()
            .find(|n| n.name == from && n.kind != NodeKind::Module)
        else {
            return false;
        };
        let Some(t) = g
            .nodes()
            .find(|n| n.name == to && n.kind != NodeKind::Module)
        else {
            return false;
        };
        g.edges()
            .any(|e| e.from == f.id && e.kind == kind && e.to == t.id)
    }

    /// 以模块节点为起点的边（模块名与 `fn main` 同名，按 kind 消歧）。
    fn mod_edge(g: &Graph, kind: EdgeKind, to: &str) -> bool {
        let Some(f) = g.nodes().find(|n| n.kind == NodeKind::Module) else {
            return false;
        };
        let Some(t) = g
            .nodes()
            .find(|n| n.name == to && n.kind != NodeKind::Module)
        else {
            return false;
        };
        g.edges()
            .any(|e| e.from == f.id && e.kind == kind && e.to == t.id)
    }

    /// 全边清点：defines/contains/calls/spawn/reads/writes/references/imports/returns。
    #[test]
    fn edges_land_per_semantics() {
        let (g, _a) = fill();
        assert!(mod_edge(&g, EdgeKind::Defines, "mk"));
        assert!(mod_edge(&g, EdgeKind::Defines, "len2"));
        assert!(mod_edge(&g, EdgeKind::Defines, "Point"));
        assert!(mod_edge(&g, EdgeKind::Defines, "Color"));
        assert!(mod_edge(&g, EdgeKind::Defines, "main"));

        // contains：struct→方法、enum→ctor、fn→参数
        assert!(has_edge(&g, "Point", EdgeKind::Contains, "len2"));
        assert!(has_edge(&g, "Color", EdgeKind::Contains, "Red"));
        assert!(has_edge(&g, "mk", EdgeKind::Contains, "x"));
        assert!(has_edge(&g, "len2", EdgeKind::Contains, "p"));

        // returns：mk/len2 的返回注解指向 Point
        assert!(has_edge(&g, "mk", EdgeKind::Returns, "Point"));
        assert!(has_edge(&g, "len2", EdgeKind::Returns, "Point"));

        // calls / spawn / 读写 / 类型引用
        assert!(has_edge(&g, "main", EdgeKind::Calls, "mk"));
        assert!(has_edge(&g, "main", EdgeKind::Spawn, "mk"));
        assert!(has_edge(&g, "mk", EdgeKind::Reads, "x"));
        assert!(has_edge(&g, "len2", EdgeKind::Reads, "p"));
        assert!(has_edge(&g, "mk", EdgeKind::References, "Point"));
        assert!(has_edge(&g, "len2", EdgeKind::References, "Point"));
        assert!(has_edge(&g, "main", EdgeKind::References, "Red"));
        // imports：模块 → File 节点
        assert!(mod_edge(&g, EdgeKind::Imports, "./util"));
        // 预载 println 不落边也不落节点
        assert!(!has_edge(&g, "main", EdgeKind::Calls, "println"));
        assert!(g.nodes().all(|n| n.name != "println"));
        // n 在循环里被写
        assert!(has_edge(&g, "main", EdgeKind::Writes, "n"));
    }

    /// 节点身份与确定性：同一 AST 两次填充逐项相等；File/Module 为合成 id。
    #[test]
    fn deterministic_and_synthetic_ids() {
        let (g1, a) = fill();
        let (g2, _) = fill();
        let n1: Vec<_> = g1.nodes().map(|n| (n.id, n.kind, n.name.clone())).collect();
        let n2: Vec<_> = g2.nodes().map(|n| (n.id, n.kind, n.name.clone())).collect();
        assert_eq!(n1, n2);
        let e1: Vec<_> = g1.edges().copied().collect();
        let e2: Vec<_> = g2.edges().copied().collect();
        assert_eq!(e1, e2);

        // 合成 id：模块 = symbols.len()（arena 编号空间之后）
        let module = g1.nodes().find(|n| n.kind == NodeKind::Module).unwrap();
        assert_eq!(module.id, SymbolId(a.symbols.len() as u32));
        assert_eq!(module.name, "main");
        let file = g1.nodes().find(|n| n.kind == NodeKind::File).unwrap();
        assert_eq!(file.id, SymbolId(a.symbols.len() as u32 + 1));
        assert_eq!(file.name, "./util");
    }
}
