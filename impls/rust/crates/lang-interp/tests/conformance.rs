//! conformance run 类实测矩阵（本波最大价值）。
//!
//! 管线：scan_suite → 逐 run case「目录即包」装载（specs/modules.md 子集：
//! 文件名序合并、import 语句位置原位展开、菱形去重、循环报错）→ parse →
//! analyze（诊断非空 = check 语义面，显式 skip 计数）→ 解释器执行 →
//! stdout 逐字节比对（`judge_run`，A6 冻结判定面）。
//!
//! 第四波扩为双路差分：
//! - `interp-ast` 路由：AST 参考解释器（第三波）；
//! - `interp-ir` 路由：parse → analyze → `lang_ir::compile`（AST→HIR→IR）→
//!   `IrMachine`。两路各自对 conformance 真值判定（互不对照实现）；
//! - 逐 case 差分：双路 stdout 逐字节一致 + 退出码一致，skip 分类必须同判；
//! - 确定性压力抽测：含 go/chan/closure/map 的 case 各 3 遍双路输出一致。
//!
//! 显式 skip 分类（任务书口径）：宿主 http 内建、extern FFI——当前语料 0 例，
//! 检测保留以防套件扩容后静默误判为失败。真值在 conformance/，禁止改 case.yaml。

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::rc::Rc;

use lang_conformance::{
    Mode, ROUTE_INTERP_AST, ROUTE_INTERP_IR, RouteError, RouteOutput, RunRoute, ScannedCase,
    judge_run, scan_suite,
};
use lang_core::{SourceId, SourceText};
use lang_interp::Interp;
use lang_interp::ir::IrMachine;
use lang_runtime::{Machine, OutputSink};
use lang_semantic::analyze_stmts;
use lang_syntax::{Expr, Stmt};

// ———— 输出汇 ————

/// 收集 stdout 的内存输出汇（测试用；与 StdoutSink 同 trait 面）。
#[derive(Clone, Debug, Default)]
struct CollectSink(Rc<RefCell<String>>);

impl OutputSink for CollectSink {
    fn write(&mut self, s: &str) {
        self.0.borrow_mut().push_str(s);
    }
}

// ———— 目录即包装载器（specs/modules.md 子集，仅测试 harness 用） ————

/// 迷你装载器：目录内全部非测试 `.lang-go` 按文件名序合并；import 语句在
/// 原位置展开（被导入文件先于导入者）；菱形去重、循环报错（MODULE-IMPORT-002
/// /MERGE-003/CYCLE-004）。与行为证据 `impls/typescript/src/loader/loader.ts`
/// 的合并语义同构；真实装载器在 lang-loader，此处不重复其完整错误文本契约。
struct MiniLoader {
    root: PathBuf,
    done: HashSet<PathBuf>,
    in_progress: HashSet<PathBuf>,
}

impl MiniLoader {
    fn load(dir: &Path) -> Result<Vec<Stmt>, String> {
        let mut l = MiniLoader {
            root: dir.canonicalize().map_err(|e| e.to_string())?,
            done: HashSet::new(),
            in_progress: HashSet::new(),
        };
        // 初始装载也走 canonical 根：Windows 下 canonicalize 产生 \\?\ 前缀形态，
        // 与 import 解析的 canonical 路径必须同形，done/去重键才可比。
        let root = l.root.clone();
        l.load_dir(&root)
    }

    fn load_dir(&mut self, dir: &Path) -> Result<Vec<Stmt>, String> {
        let mut raw = Vec::new();
        for f in self.list_lang_go(dir)? {
            let ap = dir.join(&f);
            let stmts = self.parse_file(&ap)?;
            // import 解析键不含扩展名：同目录文件既被根装载又被 import 引用时不得双载
            self.done.insert(ap.clone());
            self.done.insert(ap.with_extension(""));
            raw.extend(stmts);
        }
        self.expand(raw, dir)
    }

    /// 展开语句序列里的 ImportStmt：相对 from_dir 解析 "./x" → x.lang-go 或 x/ 目录包。
    fn expand(&mut self, stmts: Vec<Stmt>, from_dir: &Path) -> Result<Vec<Stmt>, String> {
        let mut out = Vec::with_capacity(stmts.len());
        for s in stmts {
            let Stmt::Import { path, .. } = &s else {
                out.push(s);
                continue;
            };
            let path = path.clone();
            if !path.starts_with("./") {
                return Err(format!("import 仅支持 \"./相对路径\"（得到 {path:?}）"));
            }
            let target = from_dir.join(&path[2..]);
            // 目标为目录（x/）或文件（x.lang-go）：先按原名，再按补扩展名回退
            let canonical = target
                .canonicalize()
                .ok()
                .or_else(|| target.with_extension("lang-go").canonicalize().ok())
                .ok_or_else(|| format!("import {path}: 目标不存在"))?;
            if !canonical.starts_with(&self.root) {
                return Err(format!("import {path} 逃出模块根目录"));
            }
            if self.is_done(&canonical) {
                continue; // 菱形去重：已装载
            }
            if self.in_progress.contains(&canonical) {
                return Err(format!("循环 import {path}"));
            }
            self.in_progress.insert(canonical.clone());
            let is_dir = canonical.is_dir();
            let sub = if is_dir {
                self.load_dir(&canonical)?
            } else {
                let stmts = self.parse_file(&canonical)?;
                self.expand(stmts, canonical.parent().unwrap_or(Path::new(".")))?
            };
            self.in_progress.remove(&canonical);
            self.done.insert(canonical.clone());
            self.done.insert(canonical.with_extension(""));
            out.extend(sub);
        }
        Ok(out)
    }

    fn is_done(&self, target: &Path) -> bool {
        let target = target.to_path_buf();
        self.done.contains(&target) || self.done.contains(&target.with_extension(""))
    }

    /// 非测试 .lang-go 按文件名序（MODULE-TEST-007：run 不装载 *_test.lang-go）。
    fn list_lang_go(&self, dir: &Path) -> Result<Vec<String>, String> {
        let mut out = Vec::new();
        let entries = std::fs::read_dir(dir).map_err(|e| format!("读取目录失败: {e}"))?;
        for e in entries {
            let e = e.map_err(|e| e.to_string())?;
            let name = e.file_name().to_string_lossy().into_owned();
            if e.path().is_file() && name.ends_with(".lang-go") && !name.ends_with("_test.lang-go")
            {
                out.push(name);
            }
        }
        out.sort();
        Ok(out)
    }

    fn parse_file(&self, ap: &Path) -> Result<Vec<Stmt>, String> {
        let text = std::fs::read_to_string(ap).map_err(|e| format!("{}: {e}", ap.display()))?;
        let src = SourceText::new(ap.to_string_lossy(), &text);
        lang_syntax::parse(&src, SourceId(0)).map_err(|d| format!("{}: {d:?}", ap.display()))
    }
}

// ———— AST 参考解释器路由（RunRoute 实现） ————

/// 判定执行是否属于显式 skip 分类：extern FFI 或宿主 http 内建（FFI-001）。
/// 保守全树遍历：表达式递归覆盖全部求值位置。
fn needs_host_or_ffi(stmts: &[Stmt]) -> bool {
    fn expr_has(e: &Expr) -> bool {
        match e {
            Expr::Call { callee, args, .. } => {
                let http = matches!(callee.as_ref(), Expr::Ident { name, .. } if name.starts_with("http."));
                http || expr_has(callee) || args.iter().any(expr_has)
            }
            Expr::Unary { operand, .. } => expr_has(operand),
            Expr::Binary { lhs, rhs, .. } => expr_has(lhs) || expr_has(rhs),
            Expr::Range { lo, hi, .. } => expr_has(lo) || expr_has(hi),
            Expr::Index { obj, idx, .. } => expr_has(obj) || expr_has(idx),
            Expr::Slice { obj, lo, hi, .. } => expr_has(obj) || expr_has(lo) || expr_has(hi),
            Expr::Field { obj, .. } => expr_has(obj),
            Expr::Try { operand, .. } => expr_has(operand),
            Expr::StructLit { fields, .. } => fields.iter().any(|f| expr_has(&f.value)),
            Expr::MapLit { items, .. } => items
                .iter()
                .any(|it| expr_has(&it.key) || expr_has(&it.value)),
            Expr::ArrLit { items, .. } => items.iter().any(expr_has),
            Expr::Match {
                scrutinee, arms, ..
            } => expr_has(scrutinee) || arms.iter().any(|a| expr_has(&a.body)),
            // 闭包体也是求值位置：体内 extern/http 同样触发 skip（保守全树遍历）
            Expr::FnLit { body, .. } => body.iter().any(stmt_has),
            _ => false,
        }
    }
    fn stmt_has(s: &Stmt) -> bool {
        match s {
            Stmt::Extern { .. } => true,
            Stmt::Let { init, .. } => expr_has(init),
            Stmt::Assign { target, value, .. } => expr_has(target) || expr_has(value),
            Stmt::ExprStmt { expr, .. } => expr_has(expr),
            Stmt::Return { value: Some(e), .. } => expr_has(e),
            Stmt::If {
                cond,
                then_body,
                else_body,
                ..
            } => {
                expr_has(cond)
                    || then_body.iter().any(stmt_has)
                    || else_body.as_ref().is_some_and(|b| b.iter().any(stmt_has))
            }
            Stmt::While { cond, body, .. } => expr_has(cond) || body.iter().any(stmt_has),
            Stmt::ForIn { iter, body, .. } => expr_has(iter) || body.iter().any(stmt_has),
            Stmt::FnDecl { body, .. } => body.iter().any(stmt_has),
            Stmt::Go { call, .. } => expr_has(call),
            _ => false,
        }
    }
    stmts.iter().any(stmt_has)
}

/// AST 参考解释器执行一个 case 目录：装载 → parse → analyze → 执行。
fn run_ast_case(case_dir: &Path) -> Result<RouteOutput, RouteError> {
    let stmts = MiniLoader::load(case_dir).map_err(RouteError::Failed)?;
    if needs_host_or_ffi(&stmts) {
        return Err(RouteError::Skipped(
            "extern FFI / 宿主 http 内建（解释器不提供宿主能力）".into(),
        ));
    }
    let analysis = analyze_stmts(&stmts, SourceId(0));
    if analysis.has_errors() {
        // 静态诊断是 check 语义的真值；run 路由只对干净程序负责
        return Err(RouteError::Skipped(format!(
            "静态诊断 {} 条（check 语义面，run 不执行）",
            analysis.diags.len()
        )));
    }
    let mut sink = CollectSink::default();
    let mut interp = Interp::new(Machine::new());
    match interp.run(&stmts, &analysis, &mut sink) {
        Ok(()) => Ok(RouteOutput {
            stdout: sink.0.borrow().clone(),
            stderr: String::new(),
            exit_code: 0,
        }),
        Err(e) => Err(RouteError::Failed(format!("运行时失败: {e:?}"))),
    }
}

struct AstRoute;

impl RunRoute for AstRoute {
    fn name(&self) -> &'static str {
        ROUTE_INTERP_AST
    }
    fn run(&self, case_dir: &Path) -> Result<RouteOutput, RouteError> {
        run_ast_case(case_dir)
    }
}

// ———— IR 解释器路由（差分第二路，第四波） ————

/// IR 解释器执行一个 case 目录：装载 → parse → analyze → `lang_ir::compile`
/// （AST→HIR→IR，A3 领地）→ `IrMachine` 执行。skip 门与 AST 路由逐字相同
/// （extern/http、静态诊断），保证双路 skip 分类可比。
fn run_ir_case(case_dir: &Path) -> Result<RouteOutput, RouteError> {
    let stmts = MiniLoader::load(case_dir).map_err(RouteError::Failed)?;
    if needs_host_or_ffi(&stmts) {
        return Err(RouteError::Skipped(
            "extern FFI / 宿主 http 内建（解释器不提供宿主能力）".into(),
        ));
    }
    let mut analysis = analyze_stmts(&stmts, SourceId(0));
    if analysis.has_errors() {
        // 与 AST 路由同判：静态诊断是 check 语义的真值；run 路由只对干净程序负责
        return Err(RouteError::Skipped(format!(
            "静态诊断 {} 条（check 语义面，run 不执行）",
            analysis.diags.len()
        )));
    }
    // compile 需要 &mut：HIR 构建经 resolve_annotation 静默通道回填注解类型（A2 冻结入口）
    let prog = lang_ir::compile(&stmts, &mut analysis)
        .map_err(|e| RouteError::Failed(format!("IR 编译失败（lowering 面）: {e}")))?;
    let mut sink = CollectSink::default();
    let mut machine = IrMachine::new(&prog);
    match machine.run(&mut sink) {
        Ok(()) => Ok(RouteOutput {
            stdout: sink.0.borrow().clone(),
            stderr: String::new(),
            exit_code: 0,
        }),
        Err(e) => Err(RouteError::Failed(format!("IR 运行时失败: {e:?}"))),
    }
}

struct IrRoute;

impl RunRoute for IrRoute {
    fn name(&self) -> &'static str {
        ROUTE_INTERP_IR
    }
    fn run(&self, case_dir: &Path) -> Result<RouteOutput, RouteError> {
        run_ir_case(case_dir)
    }
}

// ———— 实测矩阵 ————

/// 全套件 run 类实测：除显式 skip（extern/http/静态诊断面）外必须全部通过。
/// 失败即 interp 或 runtime 的 bug——修实现，绝不改 case.yaml。
#[test]
fn conformance_run_matrix_ast_interp() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("..")
        .join("..")
        .join("conformance");
    let cases: Vec<ScannedCase> = scan_suite(&root)
        .unwrap_or_else(|f| panic!("套件扫描应零失败，实际 {} 条：{f:?}", f.len()));
    let route = AstRoute;

    let mut run_total = 0usize;
    let mut passed: Vec<String> = Vec::new();
    let mut skipped: Vec<String> = Vec::new();
    let mut failed: Vec<String> = Vec::new();
    let mut check_total = 0usize;

    for sc in &cases {
        match sc.case.mode {
            Mode::Check => {
                check_total += 1;
                continue; // check 语义（诊断比对）是 A6 路由，不属本波解释器矩阵
            }
            Mode::Run => {}
        }
        run_total += 1;
        let outcome = route.run(&sc.dir);
        let res = judge_run(&sc.case, route.name(), &outcome);
        let label = format!("{}/{}", sc.case.category, sc.case.name);
        if res.skipped {
            skipped.push(format!("{label}（{}）", res.note));
        } else if res.ok {
            passed.push(label);
        } else {
            failed.push(format!("{label}: {}", res.note));
        }
    }

    println!("conformance run 类实测矩阵（路由 {}）：", route.name());
    println!("  run 总数: {run_total}");
    println!("  通过: {}", passed.len());
    println!("  跳过: {} → {skipped:?}", skipped.len());
    println!("  失败: {}", failed.len());
    println!("  （check 类 {} 例不在本路由）", check_total);
    for f in &failed {
        println!("  FAIL {f}");
    }
    assert!(
        failed.is_empty(),
        "run 类失败 {} 例（修实现，禁改 case.yaml）：\n{}",
        failed.len(),
        failed.join("\n")
    );
    assert_eq!(
        run_total,
        passed.len() + skipped.len(),
        "通过 + 跳过应覆盖全部 run 类"
    );
}

// ———— 第四波：IR 路由全量矩阵 + 双解释器差分 + 确定性压力 ————

/// 差分标记收集：go 语句 / chan 内建（makeChan/send/recv）/ 闭包字面量 /
/// 映射字面量。确定性压力抽测据此选 case（全树遍历，闭包体内同样计入）。
fn diff_markers(stmts: &[Stmt]) -> Vec<&'static str> {
    fn push(out: &mut Vec<&'static str>, m: &'static str) {
        if !out.contains(&m) {
            out.push(m);
        }
    }
    fn expr_markers(e: &Expr, out: &mut Vec<&'static str>) {
        match e {
            Expr::Call { callee, args, .. } => {
                if matches!(callee.as_ref(), Expr::Ident { name, .. }
                    if matches!(name.as_str(), "makeChan" | "send" | "recv"))
                {
                    push(out, "chan");
                }
                expr_markers(callee, out);
                args.iter().for_each(|a| expr_markers(a, out));
            }
            Expr::Unary { operand, .. } => expr_markers(operand, out),
            Expr::Binary { lhs, rhs, .. } => {
                expr_markers(lhs, out);
                expr_markers(rhs, out);
            }
            Expr::Range { lo, hi, .. } => {
                expr_markers(lo, out);
                expr_markers(hi, out);
            }
            Expr::Index { obj, idx, .. } => {
                expr_markers(obj, out);
                expr_markers(idx, out);
            }
            Expr::Slice { obj, lo, hi, .. } => {
                expr_markers(obj, out);
                expr_markers(lo, out);
                expr_markers(hi, out);
            }
            Expr::Field { obj, .. } => expr_markers(obj, out),
            Expr::Try { operand, .. } => expr_markers(operand, out),
            Expr::StructLit { fields, .. } => {
                fields.iter().for_each(|f| expr_markers(&f.value, out));
            }
            Expr::MapLit { items, .. } => {
                push(out, "map");
                // 键值里还可能埋闭包/chan，继续下钻
                items.iter().for_each(|it| {
                    expr_markers(&it.key, out);
                    expr_markers(&it.value, out);
                });
            }
            Expr::ArrLit { items, .. } => items.iter().for_each(|e| expr_markers(e, out)),
            Expr::Match {
                scrutinee, arms, ..
            } => {
                expr_markers(scrutinee, out);
                arms.iter().for_each(|a| expr_markers(&a.body, out));
            }
            Expr::FnLit { body, .. } => {
                push(out, "closure");
                body.iter().for_each(|s| stmt_markers(s, out));
            }
            _ => {}
        }
    }
    fn stmt_markers(s: &Stmt, out: &mut Vec<&'static str>) {
        match s {
            Stmt::Go { .. } => push(out, "go"),
            Stmt::Let { init, .. } => expr_markers(init, out),
            Stmt::Assign { target, value, .. } => {
                expr_markers(target, out);
                expr_markers(value, out);
            }
            Stmt::ExprStmt { expr, .. } => expr_markers(expr, out),
            Stmt::Return { value: Some(e), .. } => expr_markers(e, out),
            Stmt::If {
                cond,
                then_body,
                else_body,
                ..
            } => {
                expr_markers(cond, out);
                then_body.iter().for_each(|s| stmt_markers(s, out));
                if let Some(b) = else_body.as_ref() {
                    b.iter().for_each(|s| stmt_markers(s, out));
                }
            }
            Stmt::While { cond, body, .. } => {
                expr_markers(cond, out);
                body.iter().for_each(|s| stmt_markers(s, out));
            }
            Stmt::ForIn { iter, body, .. } => {
                expr_markers(iter, out);
                body.iter().for_each(|s| stmt_markers(s, out));
            }
            Stmt::FnDecl { body, .. } => body.iter().for_each(|s| stmt_markers(s, out)),
            _ => {}
        }
    }
    let mut out = Vec::new();
    stmts.iter().for_each(|s| stmt_markers(s, &mut out));
    out
}

/// IR 路由全量实测矩阵：与 AST 矩阵同构，执行器换成
/// parse → analyze → compile → IrMachine。失败即 IrMachine 或 lowering 面
/// 的 bug（lowering 归 A3，只登记）；绝不改 case.yaml。
///
/// 显式 blocked 名单：集成轮（2026-09-25）已修复 lang-ir lowering 普通赋值
/// 两缺陷（压栈序 + 地址临时槽拷贝，见 status/A4-runtime.md 阻塞区），名单
/// 清空 = 120 例全量双路守卫。名单与实测失败集做**全等断言**：名单外新
/// 失败照常红；后续若再现 lowering 阻断面，在此登记并保持双向全等。
#[test]
fn conformance_run_matrix_ir_interp() {
    /// lowering 阻断面（category/name 全名；集成轮修复后清空）
    const LOWERING_BLOCKED: [&str; 0] = [];
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("..")
        .join("..")
        .join("conformance");
    let cases: Vec<ScannedCase> = scan_suite(&root)
        .unwrap_or_else(|f| panic!("套件扫描应零失败，实际 {} 条：{f:?}", f.len()));
    let route = IrRoute;

    let mut run_total = 0usize;
    let mut passed: Vec<String> = Vec::new();
    let mut skipped: Vec<String> = Vec::new();
    let mut failed: Vec<String> = Vec::new();
    let mut blocked: Vec<String> = Vec::new();
    let mut check_total = 0usize;

    for sc in &cases {
        match sc.case.mode {
            Mode::Check => {
                check_total += 1;
                continue; // check 语义（诊断比对）是 A6 路由，不属本波解释器矩阵
            }
            Mode::Run => {}
        }
        run_total += 1;
        let outcome = route.run(&sc.dir);
        let res = judge_run(&sc.case, route.name(), &outcome);
        let label = format!("{}/{}", sc.case.category, sc.case.name);
        if res.skipped {
            skipped.push(format!("{label}（{}）", res.note));
        } else if res.ok {
            passed.push(label);
        } else if LOWERING_BLOCKED.contains(&label.as_str()) {
            blocked.push(label); // 已登记的 lowering 阻断面，不与真实失败混流
        } else {
            failed.push(format!("{label}: {}", res.note));
        }
    }

    println!("conformance run 类实测矩阵（路由 {}）：", route.name());
    println!("  run 总数: {run_total}");
    println!("  通过: {}", passed.len());
    println!("  跳过: {} → {skipped:?}", skipped.len());
    println!("  lowering 阻断（已登记）: {}", blocked.len());
    println!("  失败: {}", failed.len());
    println!("  （check 类 {} 例不在本路由）", check_total);
    for f in &failed {
        println!("  FAIL {f}");
    }
    // blocked 名单全等断言（双向）：名单内必须确实失败（A3 修复后名单须收缩），
    // 失败也不得超出名单（名单外新失败照常红）
    let mut blocked_sorted = blocked.clone();
    blocked_sorted.sort();
    let mut expected: Vec<String> = LOWERING_BLOCKED.iter().map(|s| (*s).to_string()).collect();
    expected.sort();
    assert_eq!(
        blocked_sorted,
        expected,
        "lowering 阻断面漂移（名单 {} 条 vs 实际阻断 {} 条）：A3 修复后收缩名单，名单外失败须另定性",
        expected.len(),
        blocked_sorted.len()
    );
    assert!(
        failed.is_empty(),
        "run 类失败 {} 例（修实现，禁改 case.yaml）：\n{}",
        failed.len(),
        failed.join("\n")
    );
    assert_eq!(
        run_total,
        passed.len() + skipped.len() + blocked.len(),
        "通过 + 跳过 + 已登记阻断应覆盖全部 run 类"
    );
}

/// 双解释器逐 case 差分（本波核心交付）：全部 run 类 case 各走
/// interp-ast / interp-ir 两路，断言 stdout 逐字节一致 + 退出码一致；
/// skip 分类必须同判（同一套 extern/http + 静态诊断门）。
/// 本测试只比双路互等——各自对 conformance 真值的判定由两个全量矩阵测试负责。
///
/// **#[ignore] 已解除（集成轮 2026-09-25）**：此前的 11 例 lowering 阻断
/// （普通赋值压栈序违契 + 地址临时槽拷贝破坏 place 身份）已在 lang-ir
/// 修复（见 status/A4-runtime.md 阻塞区与集成轮记录），恢复 120 例全量守卫。
#[test]
fn diff_matrix_ast_vs_ir() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("..")
        .join("..")
        .join("conformance");
    let cases: Vec<ScannedCase> = scan_suite(&root)
        .unwrap_or_else(|f| panic!("套件扫描应零失败，实际 {} 条：{f:?}", f.len()));
    let ast = AstRoute;
    let ir = IrRoute;

    let mut run_total = 0usize;
    let mut agreed = 0usize;
    let mut both_skipped = 0usize;
    let mut failed: Vec<String> = Vec::new();

    for sc in &cases {
        match sc.case.mode {
            Mode::Check => continue,
            Mode::Run => {}
        }
        run_total += 1;
        let label = format!("{}/{}", sc.case.category, sc.case.name);
        let a = ast.run(&sc.dir);
        let i = ir.run(&sc.dir);
        match (&a, &i) {
            (Err(RouteError::Skipped(_)), Err(RouteError::Skipped(_))) => both_skipped += 1,
            (Ok(oa), Ok(oi)) => {
                if oa.stdout != oi.stdout {
                    failed.push(format!(
                        "{label}: stdout 不一致\n    AST: {oa:?}\n    IR : {oi:?}"
                    ));
                } else if oa.exit_code != oi.exit_code {
                    failed.push(format!(
                        "{label}: 退出码不一致 AST={} IR={}",
                        oa.exit_code, oi.exit_code
                    ));
                } else {
                    agreed += 1;
                }
            }
            (Ok(oa), Err(e)) => failed.push(format!(
                "{label}: AST 成功但 IR 失败（{e:?}）；AST stdout={oa:?}"
            )),
            (Err(e), Ok(oi)) => failed.push(format!(
                "{label}: IR 成功但 AST 失败（{e:?}）；IR stdout={oi:?}"
            )),
            (Err(ea), Err(ei)) => {
                failed.push(format!("{label}: 双路失败 AST={ea:?} IR={ei:?}"));
            }
        }
    }

    println!("双解释器差分矩阵（interp-ast vs interp-ir）：");
    println!("  run 总数: {run_total}");
    println!("  双路一致（stdout 逐字节 + 退出码）: {agreed}");
    println!("  双路同 skip: {both_skipped}");
    println!("  差分失败: {}", failed.len());
    for f in &failed {
        println!("  DIFF {f}");
    }
    assert!(
        failed.is_empty(),
        "差分失败 {} 例（归属判定后修对应实现或登记 lowering 缺口）：\n{}",
        failed.len(),
        failed.join("\n")
    );
    assert_eq!(
        run_total,
        agreed + both_skipped,
        "一致 + 同 skip 应覆盖全部 run 类"
    );
}

/// 确定性压力抽测：取前 10 个（扫描序，确定）含 go/chan/closure/map 标记的
/// run 类 case，各跑 3 遍 × 双路，断言 6 份输出逐字节一致（调度确定性：
/// go 为同步确定子集 TASK-SPAWN-002，chan FIFO，map 插入序打印）。
///
/// 选样门控双路可执行：lowering 阻断面（普通赋值压栈序/临时槽拷贝，见
/// status/A4-runtime.md 阻塞区）的 case 由差分与 IR 矩阵登记，不进压力池。
#[test]
fn determinism_stress_go_chan_closure_map() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("..")
        .join("..")
        .join("conformance");
    let cases: Vec<ScannedCase> = scan_suite(&root)
        .unwrap_or_else(|f| panic!("套件扫描应零失败，实际 {} 条：{f:?}", f.len()));
    let ast = AstRoute;
    let ir = IrRoute;

    // 确定选样：先按标记类型轮转（go → chan → closure → map，保证调度面
    // 覆盖），再按扫描序补满 10；仅收双路门放行的 run 类 case
    let mut candidates: Vec<(String, PathBuf, Vec<&'static str>)> = Vec::new();
    for sc in &cases {
        if sc.case.mode != Mode::Run {
            continue;
        }
        let Ok(stmts) = MiniLoader::load(&sc.dir) else {
            continue; // 装载失败由矩阵测试负责，此处不重复定性
        };
        let markers = diff_markers(&stmts);
        if markers.is_empty() || needs_host_or_ffi(&stmts) {
            continue;
        }
        if analyze_stmts(&stmts, SourceId(0)).has_errors() {
            continue; // 静态诊断面是 skip 类，不进压力池
        }
        // 双路可执行门控：lowering 阻断面（登记于 status 阻塞区）不进压力池
        if ir.run(&sc.dir).is_err() {
            continue;
        }
        candidates.push((
            format!(
                "{}/{}（{}）",
                sc.case.category,
                sc.case.name,
                markers.join("+")
            ),
            sc.dir.clone(),
            markers,
        ));
    }
    let mut picked: Vec<(String, PathBuf)> = Vec::new();
    let mut seen: HashSet<String> = HashSet::new();
    // 每类标记至多 4 例、每类目（category/）至多 4 例：go/chan/closure/map
    // 都有代表（go 与 chan 候选常重合，类目上限防单类目吃满配额）
    let mut per_cat: HashMap<String, usize> = HashMap::new();
    for marker in ["go", "chan", "closure", "map"] {
        let mut quota = 4usize;
        for (label, dir, markers) in &candidates {
            if picked.len() >= 10 || quota == 0 {
                break;
            }
            let cat = label.split('/').next().unwrap_or_default().to_string();
            if seen.contains(label)
                || !markers.contains(&marker)
                || per_cat.get(&cat).copied().unwrap_or(0) >= 4
            {
                continue;
            }
            seen.insert(label.clone());
            *per_cat.entry(cat).or_insert(0) += 1;
            picked.push((label.clone(), dir.clone()));
            quota -= 1;
        }
    }
    for (label, dir, _) in &candidates {
        if picked.len() >= 10 {
            break;
        }
        if seen.contains(label) {
            continue;
        }
        seen.insert(label.clone());
        picked.push((label.clone(), dir.clone()));
    }
    assert_eq!(
        picked.len(),
        10,
        "含 go/chan/closure/map 标记的可执行 run 类 case 应不少于 10 个，实际 {}",
        picked.len()
    );

    let mut failed: Vec<String> = Vec::new();
    for (label, dir) in &picked {
        let mut first: Option<(String, i32)> = None;
        for round in 1..=3 {
            for (route_name, outcome) in [("interp-ast", ast.run(dir)), ("interp-ir", ir.run(dir))]
            {
                match outcome {
                    Ok(o) => match &first {
                        None => first = Some((o.stdout.clone(), o.exit_code)),
                        Some((stdout, exit)) => {
                            if *stdout != o.stdout || *exit != o.exit_code {
                                failed.push(format!(
                                        "{label}: 第 {round} 遍 {route_name} 输出漂移\n    基准: ({stdout:?}, {exit})\n    本次: ({:?}, {})",
                                        o.stdout, o.exit_code
                                    ));
                            }
                        }
                    },
                    Err(e) => {
                        failed.push(format!("{label}: 第 {round} 遍 {route_name} 失败: {e:?}"))
                    }
                }
            }
        }
    }

    println!("确定性压力抽测（10 case × 3 遍 × 双路）：");
    for (label, _) in &picked {
        println!("  {label}");
    }
    println!("  漂移/失败: {}", failed.len());
    for f in &failed {
        println!("  DRIFT {f}");
    }
    assert!(
        failed.is_empty(),
        "确定性漂移 {} 处（go 同步子集 + chan FIFO + map 插入序应可复现）：\n{}",
        failed.len(),
        failed.join("\n")
    );
}
