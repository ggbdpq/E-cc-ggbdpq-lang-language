//! 四路执行器适配层（specs/runner-contract.md「差分一致性」+ 通用约束 5）。
//!
//! 把 A4 的 AST/IR 双解释器与 A5 的 →Go/→JS 生成+宿主执行接进第一波冻结的
//! [`RunRoute`] 注入面：每路自 `case_dir` 起完整走「目录即包装载 → parse →
//! analyze → 显式 skip 门 → 执行」，产出与 manifest expect 逐字节可比的
//! [`RouteOutput`]。
//!
//! 显式 skip 门（诚实报告纪律，与 lang-interp 的 conformance harness 同口径）：
//! - extern FFI / 宿主 `http.*` 内建（FFI-001）：解释器不提供宿主能力、双后端
//!   产物为 panic 桩——四路统一 Skipped；
//! - 静态诊断非空的 run case（check 语义面）：run 路由只对干净程序负责。
//!
//! 工具链探测（go/node）：环境变量覆盖（`LANG_RS_GO`/`LANG_RS_NODE`，与 A5
//! E2E 同名）→ PATH → 已知路径，逐一以版本子命令探测；缺失 → 该路
//! [`RouteError::Skipped`] 注明原因，不静默跳过、不谎报通过。

use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;

use lang_core::{SourceId, SourceText};
use lang_interp::Interp;
use lang_interp::ir::IrMachine;
use lang_ir::IrProgram;
use lang_loader::{ImportRef, load_program};
use lang_runtime::{Machine, OutputSink};
use lang_semantic::Analysis;
use lang_syntax::{Expr, Stmt};

use crate::compare::{
    ROUTE_GO, ROUTE_INTERP_AST, ROUTE_INTERP_IR, ROUTE_JS, RouteError, RouteOutput, RunRoute,
};

// ———— 捕获 stdout 的内存输出汇 ————

#[derive(Default)]
struct VecSink(String);

impl OutputSink for VecSink {
    fn write(&mut self, s: &str) {
        self.0.push_str(s);
    }
}

// ———— case 装载（目录即包，specs/modules.md；四路与 check 共享） ————

/// 装载 case 目录为合并语句序列：lang-loader 目录即包（文件名序预读 + import
/// 展开在装载序、菱形去重、循环报错、越根拒绝；`*_test.lang-go` 不装载，
/// MODULE-TEST-007）。解析器接线 = 逐文件 `lang_syntax::parse` 抽 import。
///
/// import 是装载指令而非运行时语句：合并序已按依赖先序展开，装载产物里的
/// `Stmt::Import` 在此滤除（与 lang-interp conformance harness 的原位展开
/// 消费语义一致；解释器对 Import 语句报错、lowering 跳过，滤除后两路一致）。
pub fn load_case(dir: &Path) -> Result<Vec<Stmt>, RouteError> {
    let mut imports_of = |rel: &str, src: &str| -> Vec<ImportRef> {
        let text = SourceText::new(rel, src.to_string());
        match lang_syntax::parse(&text, SourceId(0)) {
            Ok(stmts) => stmts
                .iter()
                .filter_map(|s| match s {
                    Stmt::Import { path, span } => Some(ImportRef {
                        path: path.clone(),
                        line: span.start.line,
                    }),
                    _ => None,
                })
                .collect(),
            Err(_) => Vec::new(), // 装载仅抽 import；解析面错误由下方逐文件 parse 报告
        }
    };
    let loaded = load_program(dir, false, &mut imports_of)
        .map_err(|e| RouteError::Failed(format!("装载失败: {}", e.message)))?;
    let mut merged = Vec::new();
    for f in &loaded.files {
        let text = SourceText::new(f.rel_path.clone(), f.source.clone());
        let stmts = lang_syntax::parse(&text, SourceId(0))
            .map_err(|d| RouteError::Failed(format!("{}: {}", f.rel_path, d.message)))?;
        merged.extend(
            stmts
                .into_iter()
                .filter(|s| !matches!(s, Stmt::Import { .. })),
        );
    }
    Ok(merged)
}

/// 静态分析（run 四路与 check 语义共用同一分析面）。
pub fn analyze_case(stmts: &[Stmt]) -> Analysis {
    lang_semantic::analyze_stmts(stmts, SourceId(0))
}

/// 显式 skip 门：extern FFI 或宿主 `http.*` 内建（FFI-001）。保守全树遍历，
/// 表达式递归覆盖全部求值位置（与 lang-interp conformance harness 同构）。
pub fn needs_host_or_ffi(stmts: &[Stmt]) -> bool {
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

/// run 路由的前置门：装载 + skip 门 + 静态诊断门。通过 = 语句序列 + 干净 analysis。
fn prepare_run(case_dir: &Path) -> Result<(Vec<Stmt>, Analysis), RouteError> {
    let stmts = load_case(case_dir)?;
    if needs_host_or_ffi(&stmts) {
        return Err(RouteError::Skipped(
            "extern FFI / 宿主 http 内建（本路不提供宿主能力）".into(),
        ));
    }
    let analysis = analyze_case(&stmts);
    if analysis.has_errors() {
        // 静态诊断是 check 语义的真值；run 路由只对干净程序负责
        return Err(RouteError::Skipped(format!(
            "静态诊断 {} 条（check 语义面，run 不执行）",
            analysis.diags.len()
        )));
    }
    Ok((stmts, analysis))
}

/// 下沉 A3 全链：语句序列 + 干净 analysis → 已过 validate 的 IrProgram。
fn lower_to_ir(analysis: &mut Analysis, stmts: &[Stmt]) -> Result<IrProgram, RouteError> {
    lang_ir::compile(stmts, analysis)
        .map_err(|e| RouteError::Failed(format!("HIR→IR lowering: {e}")))
}

// ———— 路由一：AST 参考解释器 ————

/// AST 参考解释器路（A4 [`lang_interp::Interp`]）。
#[derive(Default)]
pub struct InterpAstRoute;

impl RunRoute for InterpAstRoute {
    fn name(&self) -> &'static str {
        ROUTE_INTERP_AST
    }
    fn run(&self, case_dir: &Path) -> Result<RouteOutput, RouteError> {
        let (stmts, analysis) = prepare_run(case_dir)?;
        let mut interp = Interp::new(Machine::new());
        let mut sink = VecSink::default();
        match interp.run(&stmts, &analysis, &mut sink) {
            Ok(()) => Ok(RouteOutput {
                stdout: sink.0,
                stderr: String::new(),
                exit_code: 0,
            }),
            Err(e) => Err(RouteError::Failed(format!("AST 解释器: {}", e.message()))),
        }
    }
}

// ———— 路由二：IR 解释器 ————

/// IR 解释器路（A3 lowering → A4 [`lang_interp::ir::IrMachine`]）。
#[derive(Default)]
pub struct InterpIrRoute;

impl RunRoute for InterpIrRoute {
    fn name(&self) -> &'static str {
        ROUTE_INTERP_IR
    }
    fn run(&self, case_dir: &Path) -> Result<RouteOutput, RouteError> {
        let (stmts, mut analysis) = prepare_run(case_dir)?;
        let prog = lower_to_ir(&mut analysis, &stmts)?;
        let mut m = IrMachine::new(&prog);
        let mut sink = VecSink::default();
        match m.run(&mut sink) {
            Ok(()) => Ok(RouteOutput {
                stdout: sink.0,
                stderr: String::new(),
                exit_code: 0,
            }),
            Err(e) => Err(RouteError::Failed(format!("IR 解释器: {}", e.message()))),
        }
    }
}

// ———— 工具链探测（go/node）————

/// 以 `<exe> <flag>` 子进程探测工具可用性。
fn probe_tool(exe: &Path, flag: &str) -> bool {
    Command::new(exe)
        .arg(flag)
        .output()
        .is_ok_and(|o| o.status.success())
}

/// 工具解析：环境变量覆盖 → PATH → 已知路径，逐一探测。
///
/// 环境变量指向不可用二进制时按 A5 E2E 同口径落回后续候选（探测是可用性
/// 判断而非配置校验）；全部候选不可用 → None（调用方如实 Skipped）。
fn resolve_tool(
    env_value: Option<&str>,
    name: &str,
    flag: &str,
    known: &[&str],
) -> Option<PathBuf> {
    if let Some(p) = env_value {
        let path = PathBuf::from(p);
        if probe_tool(&path, flag) {
            return Some(path);
        }
    }
    if probe_tool(Path::new(name), flag) {
        return Some(PathBuf::from(name));
    }
    known
        .iter()
        .map(PathBuf::from)
        .find(|p| p.exists() && probe_tool(p, flag))
}

/// go 探测（env：`LANG_RS_GO`；known 路径与 A5 E2E 同清单）。
pub fn detect_go() -> Option<PathBuf> {
    resolve_tool(
        std::env::var("LANG_RS_GO").ok().as_deref(),
        "go",
        "version",
        &[
            "D:\\DevTools\\Languages\\Backend\\Golang\\Go\\bin\\go.exe",
            "/usr/local/go/bin/go",
            "/usr/bin/go",
        ],
    )
}

/// node 探测（env：`LANG_RS_NODE`）。
pub fn detect_node() -> Option<PathBuf> {
    resolve_tool(
        std::env::var("LANG_RS_NODE").ok().as_deref(),
        "node",
        "--version",
        &[],
    )
}

// ———— 临时目录（Drop 清理；纪律：codegen 产物不落仓库）————

struct TempDir {
    path: PathBuf,
}

impl TempDir {
    fn new(tag: &str) -> Result<Self, RouteError> {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        let path = std::env::temp_dir().join(format!(
            "lang-conformance-{tag}-{}-{nanos}",
            std::process::id()
        ));
        std::fs::create_dir_all(&path)
            .map_err(|e| RouteError::Failed(format!("建临时目录失败: {e}")))?;
        Ok(TempDir { path })
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

fn write_source(path: &Path, src: &str) -> Result<(), RouteError> {
    std::fs::write(path, src.as_bytes())
        .map_err(|e| RouteError::Failed(format!("写 {} 失败: {e}", path.display())))
}

fn collect_output(out: &std::process::Output) -> RouteOutput {
    RouteOutput {
        stdout: String::from_utf8_lossy(&out.stdout).into_owned(),
        stderr: String::from_utf8_lossy(&out.stderr).into_owned(),
        exit_code: out.status.code().unwrap_or(-1),
    }
}

/// 工具路径持有（探测/注入二选一；[`RunRoute::run`] 只读，故用 `OnceLock`
/// 内部可变性缓存探测结果——每实例至多探测一次）。
struct ToolPath {
    exe: OnceLock<Option<PathBuf>>,
}

impl ToolPath {
    /// 注入固定路径（`None` = 强制 Skipped，skip 逻辑测试面）。
    fn with_exe(exe: Option<PathBuf>) -> Self {
        let t = ToolPath {
            exe: OnceLock::new(),
        };
        let _ = t.exe.set(exe);
        t
    }
    /// 自动探测（惰性：首次 `tool()` 时探测一次并缓存）。
    fn with_detect(detect: fn() -> Option<PathBuf>) -> Self {
        let t = ToolPath {
            exe: OnceLock::new(),
        };
        let _ = t.exe.set(detect());
        t
    }
    fn tool(&self) -> Option<&Path> {
        self.exe.get().and_then(|o| o.as_ref()).map(|p| p.as_path())
    }
}

// ———— 路由三：→Go 生成 + go 工具链执行 ————

/// →Go 生成 + 宿主执行路（A5 [`lang_backend_go::codegen`] + go 工具链）。
pub struct GoRoute {
    tool: ToolPath,
}

impl GoRoute {
    /// 自动探测（`LANG_RS_GO`/PATH/已知路径；每实例至多一次）。
    pub fn new() -> Self {
        GoRoute {
            tool: ToolPath::with_detect(detect_go),
        }
    }
    /// 注入固定工具路径；`None` = 强制 Skipped（skip 逻辑测试/宿主覆盖用）。
    pub fn with_exe(exe: Option<PathBuf>) -> Self {
        GoRoute {
            tool: ToolPath::with_exe(exe),
        }
    }
}

impl Default for GoRoute {
    fn default() -> Self {
        Self::new()
    }
}

impl RunRoute for GoRoute {
    fn name(&self) -> &'static str {
        ROUTE_GO
    }
    fn run(&self, case_dir: &Path) -> Result<RouteOutput, RouteError> {
        let (stmts, mut analysis) = prepare_run(case_dir)?;
        let prog = lower_to_ir(&mut analysis, &stmts)?;
        let Some(exe) = self.tool.tool() else {
            return Err(RouteError::Skipped(
                "宿主缺 go 工具链（LANG_RS_GO/PATH/已知路径均不可用）".into(),
            ));
        };
        let src = lang_backend_go::codegen::generate(&prog)
            .map_err(|e| RouteError::Failed(format!("→Go 生成: {e}")))?;
        let tmp = TempDir::new("go")?;
        let src_path = tmp.path.join("main.go");
        write_source(&src_path, &src)?;
        let bin = tmp.path.join(if cfg!(windows) {
            "main_bin.exe"
        } else {
            "main_bin"
        });
        let build = Command::new(exe)
            .args(["build", "-o"])
            .arg(&bin)
            .arg(&src_path)
            .output()
            .map_err(|e| RouteError::Failed(format!("go build 启动失败: {e}")))?;
        if !build.status.success() {
            return Err(RouteError::Failed(format!(
                "go build 失败: {}",
                String::from_utf8_lossy(&build.stderr)
            )));
        }
        let out = Command::new(&bin)
            .output()
            .map_err(|e| RouteError::Failed(format!("执行产物失败: {e}")))?;
        Ok(collect_output(&out))
    }
}

// ———— 路由四：→JS 生成 + node 工具链执行 ————

/// →JS 生成 + 宿主执行路（A5 [`lang_backend_js::codegen`] + node 工具链）。
pub struct JsRoute {
    tool: ToolPath,
}

impl JsRoute {
    /// 自动探测（`LANG_RS_NODE`/PATH；每实例至多一次）。
    pub fn new() -> Self {
        JsRoute {
            tool: ToolPath::with_detect(detect_node),
        }
    }
    /// 注入固定工具路径；`None` = 强制 Skipped（skip 逻辑测试/宿主覆盖用）。
    pub fn with_exe(exe: Option<PathBuf>) -> Self {
        JsRoute {
            tool: ToolPath::with_exe(exe),
        }
    }
}

impl Default for JsRoute {
    fn default() -> Self {
        Self::new()
    }
}

impl RunRoute for JsRoute {
    fn name(&self) -> &'static str {
        ROUTE_JS
    }
    fn run(&self, case_dir: &Path) -> Result<RouteOutput, RouteError> {
        let (stmts, mut analysis) = prepare_run(case_dir)?;
        let prog = lower_to_ir(&mut analysis, &stmts)?;
        let Some(exe) = self.tool.tool() else {
            return Err(RouteError::Skipped(
                "宿主缺 node 工具链（LANG_RS_NODE/PATH 均不可用）".into(),
            ));
        };
        let src = lang_backend_js::codegen::generate(&prog)
            .map_err(|e| RouteError::Failed(format!("→JS 生成: {e}")))?;
        let tmp = TempDir::new("js")?;
        let src_path = tmp.path.join("main.mjs");
        write_source(&src_path, &src)?;
        // .mjs 不能被 Windows 直接执行（os error 193）——必须经 node 启动
        let out = Command::new(exe)
            .arg(&src_path)
            .output()
            .map_err(|e| RouteError::Failed(format!("node 执行失败: {e}")))?;
        Ok(collect_output(&out))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 夹具目录：持有 TempDir 本体（Drop 清理；codegen 产物不落仓库）。
    struct FixtureDir(TempDir);

    impl FixtureDir {
        fn print_ok() -> Self {
            let dir = TempDir::new("routes-fixture").expect("建夹具目录");
            std::fs::write(
                dir.path.join("main.lang-go"),
                "fn main() {\n    println(\"ok\")\n}\n",
            )
            .expect("写夹具");
            FixtureDir(dir)
        }

        fn path(&self) -> &Path {
            &self.0.path
        }
    }

    /// 工具注入 None → 该路 Skipped（skip 逻辑测试面；不谎报失败或通过）。
    #[test]
    fn missing_tool_is_skipped_not_failed() {
        let fixture = FixtureDir::print_ok();
        let go = GoRoute::with_exe(None);
        let js = JsRoute::with_exe(None);
        for (route, tool) in [(&go as &dyn RunRoute, "go"), (&js as &dyn RunRoute, "node")] {
            match route.run(fixture.path()) {
                Err(RouteError::Skipped(m)) => assert!(m.contains(tool), "{tool}: {m}"),
                other => panic!("{tool} 路应 Skipped，实际 {other:?}"),
            }
        }
    }

    /// AST/IR 两路不需要工具链：同一夹具 stdout 逐字节相等。
    #[test]
    fn interp_routes_agree_on_fixture() {
        let fixture = FixtureDir::print_ok();
        let ast = InterpAstRoute.run(fixture.path()).expect("AST 路");
        let ir = InterpIrRoute.run(fixture.path()).expect("IR 路");
        assert_eq!(ast.stdout, "ok\n");
        assert_eq!(ir.stdout, "ok\n");
        assert_eq!(ast.exit_code, 0);
        assert!(ast.stderr.is_empty());
    }

    /// go/js 宿主路 E2E（工具缺失 → skip 打印原因，不失败）。
    #[test]
    fn host_routes_e2e_when_toolchain_present() {
        let fixture = FixtureDir::print_ok();
        let go = GoRoute::new();
        let js = JsRoute::new();
        match go.run(fixture.path()) {
            Ok(out) => assert_eq!(out.stdout, "ok\n", "go 路输出"),
            Err(RouteError::Skipped(m)) => eprintln!("SKIP go 路：{m}"),
            Err(RouteError::Failed(m)) => panic!("go 路失败: {m}"),
        }
        match js.run(fixture.path()) {
            Ok(out) => assert_eq!(out.stdout, "ok\n", "js 路输出"),
            Err(RouteError::Skipped(m)) => eprintln!("SKIP js 路：{m}"),
            Err(RouteError::Failed(m)) => panic!("js 路失败: {m}"),
        }
    }
}
