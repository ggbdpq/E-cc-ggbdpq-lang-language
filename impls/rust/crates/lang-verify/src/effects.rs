//! Effect 段事实提取：合并 AST + 语义分析 → [`CallGraph`] → 不动点。
//!
//! 事实源（SymbolId 面，禁文本搜索）：
//! - 节点 = 全部非预载 fn/method 符号（extern 含 FFI 直接效果）；
//! - 直接效果 = 内建调用名（`builtin_direct_effect`：println/print→IO、
//!   makeChan/send/recv→CHANNEL）+ go 语句→SPAWN + extern→FFI；
//! - 边 = 直接调用（`symbol_of_ref` 解析到 fn/method 符号）+ go 目标 +
//!   方法调用（接收者静态类型 → struct.methods 表；类型未知即保守缺边）；
//! - 函数字面量效果归属定义所在函数（保守近似，与契约推导规则一致）；
//! - 顶层非声明语句的调用效果归属合成 `init` 节点（懒登记）。
//!
//! HIR→IR lowering 未接入前，本提取是直接效果的保守超集：IR 结构形状
//! （go/extern）在 AST 上同样可得，内建名面与 IR 接线方用同一
//! [`builtin_direct_effect`] 单一事实源。

use lang_contract::{CallGraph, Effect};
use lang_core::{FunctionId, SymbolId};
use lang_semantic::{Analysis, NominalRef, SymbolKind};
use lang_syntax::{Expr, Stmt};
use lang_types::TypeData;

/// 合成 init 节点的函数身份（与符号编号空间不撞：符号 id 为顺序小整数）。
const INIT_FID: u32 = u32::MAX;

/// 从一次分析构建调用图并求解。失败 = 引擎良构性被破坏（内部不变量）。
pub fn solve_effects(
    stmts: &[Stmt],
    analysis: &Analysis,
) -> Result<Vec<lang_contract::EffectEntry>, String> {
    let mut cg = CallGraph::new();
    // 1) 节点：全部非预载 fn/method；extern 挂 FFI
    for s in analysis.symbols.iter() {
        if s.is_builtin || !matches!(s.kind, SymbolKind::Fn | SymbolKind::Method) {
            continue;
        }
        let fid = FunctionId(s.id.0);
        cg.add_fn(fid, s.name.clone()).map_err(|e| e.to_string())?;
        if s.is_extern {
            cg.set_direct(fid, Effect::Ffi).map_err(|e| e.to_string())?;
        }
    }

    // 2) 走查：直接效果 + 调用/go 边
    let mut w = Walk {
        analysis,
        cg: &mut cg,
        init: None,
    };
    for s in stmts {
        match s {
            Stmt::FnDecl { body, .. } => {
                let owner = analysis.symbol_of_decl(s).map(|sym| FunctionId(sym.0));
                for b in body {
                    w.stmt(b, owner);
                }
            }
            Stmt::StructDecl { .. } | Stmt::EnumDecl { .. } | Stmt::Import { .. } => {}
            Stmt::Extern { .. } => {} // FFI 直接效果已在节点登记时挂
            other => {
                // 顶层副作用归属合成 init（懒登记）
                let init = w.ensure_init();
                w.stmt(other, Some(init));
            }
        }
    }
    Ok(cg.solve())
}

struct Walk<'a> {
    analysis: &'a Analysis,
    cg: &'a mut CallGraph,
    init: Option<FunctionId>,
}

impl Walk<'_> {
    /// 懒登记合成 init 节点（顶层副作用的效果归属单位）。
    fn ensure_init(&mut self) -> FunctionId {
        if let Some(f) = self.init {
            return f;
        }
        let f = FunctionId(INIT_FID);
        if !self.cg.has_fn(f) {
            let _ = self.cg.add_fn(f, "init");
        }
        self.init = Some(f);
        f
    }

    fn stmt(&mut self, s: &Stmt, owner: Option<FunctionId>) {
        match s {
            Stmt::Let { init, .. } => {
                self.expr(init, owner);
            }
            Stmt::Assign { target, value, .. } => {
                self.expr(target, owner);
                self.expr(value, owner);
            }
            Stmt::ExprStmt { expr, .. } => self.expr(expr, owner),
            Stmt::Return { value, .. } => {
                if let Some(e) = value {
                    self.expr(e, owner);
                }
            }
            Stmt::If {
                cond,
                then_body,
                else_body,
                ..
            } => {
                self.expr(cond, owner);
                for b in then_body {
                    self.stmt(b, owner);
                }
                if let Some(els) = else_body {
                    for b in els {
                        self.stmt(b, owner);
                    }
                }
            }
            Stmt::While { cond, body, .. } => {
                self.expr(cond, owner);
                for b in body {
                    self.stmt(b, owner);
                }
            }
            Stmt::ForIn { iter, body, .. } => {
                self.expr(iter, owner);
                for b in body {
                    self.stmt(b, owner);
                }
            }
            Stmt::Break { .. } | Stmt::Continue { .. } => {}
            // 嵌套 fn：自有节点为效果归属单位
            Stmt::FnDecl { body, .. } => {
                let nested = self.analysis.symbol_of_decl(s).map(|sym| FunctionId(sym.0));
                for b in body {
                    self.stmt(b, nested);
                }
            }
            Stmt::StructDecl { .. } | Stmt::EnumDecl { .. } | Stmt::Import { .. } => {}
            Stmt::Extern { .. } => {}
            // go 语句：SPAWN 直接效果 + go 目标照常沿边传播（契约推导规则）
            Stmt::Go { call, .. } => {
                if let Some(owner) = owner {
                    let _ = self.cg.set_direct(owner, Effect::Spawn);
                    if let Expr::Call { callee, args, .. } = call {
                        let owner = Some(owner);
                        self.callee(callee, owner);
                        for a in args {
                            self.expr(a, owner);
                        }
                    }
                }
            }
        }
    }

    fn expr(&mut self, e: &Expr, owner: Option<FunctionId>) {
        match e {
            Expr::Call { callee, args, .. } => {
                self.callee(callee, owner);
                for a in args {
                    self.expr(a, owner);
                }
            }
            Expr::Unary { operand, .. } => self.expr(operand, owner),
            Expr::Binary { lhs, rhs, .. } => {
                self.expr(lhs, owner);
                self.expr(rhs, owner);
            }
            Expr::Range { lo, hi, .. } => {
                self.expr(lo, owner);
                self.expr(hi, owner);
            }
            Expr::Index { obj, idx, .. } => {
                self.expr(obj, owner);
                self.expr(idx, owner);
            }
            Expr::Slice { obj, lo, hi, .. } => {
                self.expr(obj, owner);
                self.expr(lo, owner);
                self.expr(hi, owner);
            }
            Expr::Field { obj, .. } => self.expr(obj, owner),
            Expr::Try { operand, .. } => self.expr(operand, owner),
            Expr::StructLit { fields, .. } => {
                for f in fields {
                    self.expr(&f.value, owner);
                }
            }
            Expr::MapLit { items, .. } => {
                for it in items {
                    self.expr(&it.key, owner);
                    self.expr(&it.value, owner);
                }
            }
            Expr::ArrLit { items, .. } => {
                for it in items {
                    self.expr(it, owner);
                }
            }
            Expr::Match {
                scrutinee, arms, ..
            } => {
                self.expr(scrutinee, owner);
                for arm in arms {
                    // 模式（绑定/字面量/构造）不含调用，无效果事实，只走臂体
                    self.expr(&arm.body, owner);
                }
            }
            // 函数字面量：体内效果归属定义所在函数（保守近似）
            Expr::FnLit { body, .. } => {
                for b in body {
                    self.stmt(b, owner);
                }
            }
            Expr::IntLit { .. }
            | Expr::FloatLit { .. }
            | Expr::StrLit { .. }
            | Expr::BoolLit { .. }
            | Expr::Ident { .. } => {}
        }
    }

    /// 调用目标解析：直接调用（fn/method 符号）或内建直接效果；
    /// 方法调用经接收者静态类型 → struct.methods（未知类型保守缺边）。
    fn callee(&mut self, callee: &Expr, owner: Option<FunctionId>) {
        let Some(owner) = owner else { return };
        match callee {
            Expr::Ident { name, .. } => {
                if let Some(sym) = self.analysis.symbol_of_ref(callee) {
                    let s = self.analysis.symbols.get(sym);
                    if !s.is_builtin && matches!(s.kind, SymbolKind::Fn | SymbolKind::Method) {
                        let _ = self.cg.add_call(owner, FunctionId(sym.0));
                        return;
                    }
                }
                let _ = self.cg.set_builtin_call(owner, name);
            }
            Expr::Field { obj, name, .. } => {
                if let Some(target) = self.method_owner(obj, name) {
                    let _ = self.cg.add_call(owner, FunctionId(target.0));
                }
            }
            // 间接调用（索引/闭包值）：效果不可静态解析，保守缺边
            _ => {}
        }
    }

    /// 接收者类型 + 方法名 → 方法符号（TypeId → struct def → struct 符号 methods 表）。
    fn method_owner(&self, obj: &Expr, method: &str) -> Option<SymbolId> {
        let t = self.analysis.type_of(obj)?;
        let TypeData::Struct(def) = self.analysis.types.data(t) else {
            return None;
        };
        let sid = self
            .analysis
            .symbols
            .iter()
            .find(|s| {
                !s.is_builtin
                    && s.kind == SymbolKind::Struct
                    && s.nominal == Some(NominalRef::Struct(*def))
            })
            .map(|s| s.id)?;
        self.analysis.symbols.get(sid).methods.get(method).copied()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use lang_core::{SourceId, SourceText};
    use lang_semantic::analyze_stmts;

    fn solve(src: &str) -> Vec<lang_contract::EffectEntry> {
        let text = SourceText::new("main.lang-go", src.to_string());
        let stmts = lang_syntax::parse(&text, SourceId(1)).unwrap_or_default();
        let analysis = analyze_stmts(&stmts, SourceId(1));
        solve_effects(&stmts, &analysis).unwrap()
    }

    fn effects_of<'a>(entries: &'a [lang_contract::EffectEntry], name: &str) -> Vec<&'a str> {
        entries
            .iter()
            .find(|e| e.name == name)
            .map(|e| e.effects.iter().map(Effect::as_str).collect())
            .unwrap_or_else(|| panic!("节点 {name} 应在效果表里"))
    }

    /// 直接效果：println→IO；纯函数 = [PURE]。
    #[test]
    fn direct_io_and_pure() {
        let src = "fn log() {\n    println(\"x\")\n}\nfn pure(): int {\n    return 1\n}\n";
        let entries = solve(src);
        assert_eq!(effects_of(&entries, "log"), vec!["IO"]);
        assert_eq!(effects_of(&entries, "pure"), vec!["PURE"]);
    }

    /// 1 级传播：main → f → println ⇒ main IO。
    #[test]
    fn one_level_propagation() {
        let src = "fn f(): int {\n    println(\"x\")\n    return 1\n}\nfn main() {\n    println(f())\n}\n";
        let entries = solve(src);
        assert_eq!(effects_of(&entries, "main"), vec!["IO"]);
    }

    /// 3 级传播 + 递归收敛（互递归环上 IO 全员可达）。
    #[test]
    fn three_level_and_mutual_recursion() {
        let src = "\
fn a(n: int): int {
    if n <= 0 {
        return b(n)
    }
    return a(n - 1)
}
fn b(n: int): int {
    return c(n)
}
fn c(n: int): int {
    println(n)
    return n
}
fn main() {
    println(a(3))
}
";
        let entries = solve(src);
        assert_eq!(effects_of(&entries, "c"), vec!["IO"]);
        assert_eq!(effects_of(&entries, "b"), vec!["IO"]);
        assert_eq!(effects_of(&entries, "a"), vec!["IO"]);
        assert_eq!(effects_of(&entries, "main"), vec!["IO"]);
    }

    /// go 语句 → SPAWN 直接效果；go 目标效果沿边传播。
    #[test]
    fn spawn_marks_direct_and_propagates_target() {
        let src = "fn work(): int {\n    makeChan(1)\n    return 1\n}\nfn main() {\n    go work()\n    return\n}\n";
        let entries = solve(src);
        assert_eq!(
            effects_of(&entries, "work"),
            vec!["CHANNEL"],
            "makeChan→CHANNEL 直接效果"
        );
        assert_eq!(
            effects_of(&entries, "main"),
            vec!["SPAWN", "CHANNEL"],
            "go 语句→SPAWN 直接效果 + work 的 CHANNEL 沿 go 边传播"
        );
    }

    /// channel 内建面（makeChan/send/recv → CHANNEL）。
    #[test]
    fn channel_builtins() {
        let src =
            "fn main() {\n    let ch = makeChan(1)\n    send(ch, 1)\n    println(recv(ch))\n}\n";
        let entries = solve(src);
        assert_eq!(effects_of(&entries, "main"), vec!["IO", "CHANNEL"]);
    }

    /// extern → FFI 直接效果，并沿调用边传播到 caller。
    #[test]
    fn extern_ffi_and_caller_propagation() {
        let src = "extern \"host\" {\n    fn hostfn(n: int): int\n}\nfn caller(): int {\n    return hostfn(1)\n}\n";
        let entries = solve(src);
        assert_eq!(effects_of(&entries, "hostfn"), vec!["FFI"]);
        assert_eq!(effects_of(&entries, "caller"), vec!["FFI"]);
    }

    /// 方法调用边：接收者静态类型 → struct.methods（效果照常传播）。
    #[test]
    fn method_call_edge_via_receiver_type() {
        let src = "\
struct P {
    x: int
}
fn (p P).loud(): int {
    println(p.x)
    return p.x
}
fn main() {
    let q = P { x: 3 }
    println(q.loud())
}
";
        let entries = solve(src);
        assert_eq!(effects_of(&entries, "loud"), vec!["IO"]);
        assert_eq!(effects_of(&entries, "main"), vec!["IO"]);
    }

    /// 顶层全局初始化的调用效果归属合成 init 节点。
    #[test]
    fn top_level_init_node() {
        let src = "fn mk(): int {\n    println(\"init\")\n    return 1\n}\nlet g = mk()\n";
        let entries = solve(src);
        assert_eq!(effects_of(&entries, "init"), vec!["IO"]);
    }

    /// 确定性：同一输入两次求解逐项相等。
    #[test]
    fn deterministic() {
        let src = "fn f() {\n    println(1)\n}\nfn main() {\n    go f()\n    println(2)\n}\n";
        let e1 = solve(src);
        let e2 = solve(src);
        assert_eq!(e1, e2);
    }
}
