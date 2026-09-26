//! lang-verify E2E：真实夹具目录上的九段管线（红→绿矩阵）。
//!
//! 矩阵：
//! 1. 全绿夹具 + DefaultExec（A3 lowering + A4 双解释器 + A5 生成/宿主执行）
//!    → 前八段全过；差分段对可用执行路（ast/ir/js，go 视宿主工具链）逐字节
//!    比对；TestDriver 记录每路产物，独立断言一致性（不依赖差分段消息）。
//! 2. 前端失败夹具（parse/resolve/typecheck）→ 失败段 + 短路 Skipped 可见。
//! 3. 差分失配（TestDriver 篡改 js 路输出）→ Differential Failed 带证据。
//! 4. StubExec → 执行段全 Skipped 显式呈现（编排对桩的可见性）。
//!
//! 临时夹具目录用 Drop 清理（系统临时目录，不落仓库）。

use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use lang_ir::IrProgram;
use lang_verify::{
    DefaultExec, ExecContext, ExecDriver, ExecError, RunOutput, Stage, StageOutcome, StubExec,
    VerifyInput, run_pipeline,
};

// ———— 夹具 ————

/// 全绿夹具：跨函数调用 + 字符串绑定 + 两次输出（ast/ir/js 逐字节可比）。
const GREEN: &str = "\
fn add(a: int, b: int): int {
    return a + b
}
fn main() {
    println(add(1, 2))
    let msg = \"ok\"
    println(msg)
}
";

const EXPECTED_STDOUT: &str = "3\nok\n";

/// 包装 DefaultExec：记录各路真跑 stdout（差分逐字节断言用），可篡改 js 路
/// （差分失配用例）。lowering 与全部执行段委托内建驱动——A3/A4/A5 的真实接线
/// 即被本矩阵覆盖。
struct TestDriver {
    inner: DefaultExec,
    outputs: Arc<Mutex<Vec<(&'static str, String)>>>,
    tamper_js: bool,
}

impl TestDriver {
    fn new(tamper_js: bool) -> Self {
        TestDriver {
            inner: DefaultExec::new(),
            outputs: Arc::new(Mutex::new(Vec::new())),
            tamper_js,
        }
    }
}

impl ExecDriver for TestDriver {
    fn run_ast(&mut self, ctx: &ExecContext<'_>) -> Result<RunOutput, ExecError> {
        let out = self.inner.run_ast(ctx)?;
        self.outputs
            .lock()
            .unwrap()
            .push(("ast", out.stdout.clone()));
        Ok(out)
    }
    fn lower_ir(
        &mut self,
        hir: &lang_hir::HirProg,
        analysis: &lang_semantic::Analysis,
    ) -> Result<IrProgram, ExecError> {
        self.inner.lower_ir(hir, analysis)
    }
    fn run_ir(&mut self, prog: &IrProgram) -> Result<RunOutput, ExecError> {
        let out = self.inner.run_ir(prog)?;
        self.outputs
            .lock()
            .unwrap()
            .push(("ir", out.stdout.clone()));
        Ok(out)
    }
    fn run_go(&mut self, prog: &IrProgram) -> Result<RunOutput, ExecError> {
        let out = self.inner.run_go(prog)?;
        self.outputs
            .lock()
            .unwrap()
            .push(("go", out.stdout.clone()));
        Ok(out)
    }
    fn run_js(&mut self, prog: &IrProgram) -> Result<RunOutput, ExecError> {
        let mut out = self.inner.run_js(prog)?;
        if self.tamper_js {
            out.stdout = "TAMPERED\n".into();
        }
        self.outputs
            .lock()
            .unwrap()
            .push(("js", out.stdout.clone()));
        Ok(out)
    }
}

// ———— 临时夹具目录 ————

struct FixtureDir {
    path: PathBuf,
}

impl FixtureDir {
    fn new(tag: &str, files: &[(&str, &str)]) -> Self {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        let path = std::env::temp_dir().join(format!(
            "lang-verify-e2e-{tag}-{}-{nanos}",
            std::process::id()
        ));
        std::fs::create_dir_all(&path).expect("建夹具目录");
        for (name, src) in files {
            std::fs::write(path.join(name), src).expect("写夹具文件");
        }
        FixtureDir { path }
    }
}

impl Drop for FixtureDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

// ———— 断言辅助 ————

fn assert_stage(
    report: &lang_verify::VerifyReport,
    s: Stage,
    check: impl Fn(&StageOutcome) -> bool,
) {
    let o = report
        .stages
        .iter()
        .find(|(st, _)| *st == s)
        .map(|(_, o)| o)
        .unwrap_or_else(|| panic!("报告缺少 {s:?} 段"));
    assert!(check(o), "{s:?} 实际为 {o:?}");
}

fn is_passed(o: &StageOutcome) -> bool {
    matches!(o, StageOutcome::Passed)
}

fn is_skipped_with(m: &str) -> impl Fn(&StageOutcome) -> bool + '_ {
    move |o| matches!(o, StageOutcome::Skipped(got) if got.contains(m))
}

fn is_failed_with(m: &str) -> impl Fn(&StageOutcome) -> bool + '_ {
    move |o| matches!(o, StageOutcome::Failed(got) if got.contains(m))
}

/// node 可用性（js 路断言的前提）。
fn node_available() -> bool {
    std::process::Command::new("node")
        .arg("--version")
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
}

// ———— E2E ————

/// 矩阵 1：全绿夹具 + DefaultExec 全链（A3 lowering → A4 双解释器 →
/// A5 生成/宿主执行）→ 前八段全过；差分对全部可用执行路成立。
#[test]
fn all_green_fixture_full_matrix() {
    let dir = FixtureDir::new("green", &[("main.lang-go", GREEN)]);
    let input = VerifyInput::from_dir(&dir.path).expect("装载夹具目录");
    let mut driver = TestDriver::new(false);
    let r = run_pipeline(&input, &mut driver);

    for s in [
        Stage::Parse,
        Stage::Resolve,
        Stage::Typecheck,
        Stage::Hir,
        Stage::Ir,
        Stage::Effect,
        Stage::Compile,
        Stage::Execute,
    ] {
        assert_stage(&r, s, is_passed);
    }
    // 差分段：go 缺失 → Skipped（诚实呈现）；go 可用 → Passed。绝不 Failed。
    let diff = r
        .stages
        .iter()
        .find(|(s, _)| s == &Stage::Differential)
        .map(|(_, o)| o)
        .expect("报告含 differential");
    match diff {
        StageOutcome::Passed => {}
        StageOutcome::Skipped(m) => assert!(m.contains("go"), "缺失路应点名 go: {m}"),
        StageOutcome::Failed(m) => panic!("差分失败: {m}"),
    }
    assert!(r.passed());
    assert_eq!(r.failed_stage(), None);

    // 真跑产物逐字节断言（不依赖差分段消息）：ast/ir/js 全部等于期望输出
    let outs = driver.outputs.lock().unwrap();
    let ast = outs
        .iter()
        .find(|(n, _)| *n == "ast")
        .expect("ast 路应真跑");
    assert_eq!(ast.1, EXPECTED_STDOUT, "AST 参考路输出");
    assert!(
        outs.iter().any(|(n, s)| *n == "ir" && s == EXPECTED_STDOUT),
        "IR 解释器路输出应与期望一致: {outs:?}"
    );
    if node_available() {
        assert!(
            outs.iter().any(|(n, s)| *n == "js" && s == EXPECTED_STDOUT),
            "node 宿主路输出应与期望一致: {outs:?}"
        );
    }
}

/// 矩阵 2：DefaultExec 直跑 `verify(dir)`（runner-contract 的 verify 动词形）：
/// 与矩阵 1 同链路，ir/compile/execute 全过、差分不失败。
#[test]
fn verify_verb_default_exec_end_to_end() {
    let dir = FixtureDir::new("verb", &[("main.lang-go", GREEN)]);
    let report = lang_verify::verify(&dir.path);
    for s in [
        Stage::Parse,
        Stage::Resolve,
        Stage::Typecheck,
        Stage::Hir,
        Stage::Ir,
        Stage::Effect,
        Stage::Compile,
        Stage::Execute,
    ] {
        assert_stage(&report, s, is_passed);
    }
    match report
        .stages
        .iter()
        .find(|(s, _)| s == &Stage::Differential)
        .map(|(_, o)| o)
    {
        Some(StageOutcome::Passed) => {}
        Some(StageOutcome::Skipped(m)) => {
            assert!(m.contains("go"), "缺失路应点名 go: {m}")
        }
        other => panic!("差分段异常: {other:?}"),
    }
    assert!(report.passed());
    assert_eq!(report.failed_stage(), None);
}

/// 矩阵 3a：parse 失败 → 失败段 + 短路 Skipped 全可见。
#[test]
fn parse_failure_matrix() {
    let dir = FixtureDir::new("parse", &[("main.lang-go", "fn main( {")]);
    let report = lang_verify::verify(&dir.path);
    assert_stage(&report, Stage::Parse, is_failed_with("main.lang-go"));
    for s in [Stage::Resolve, Stage::Hir, Stage::Ir, Stage::Differential] {
        assert_stage(&report, s, is_skipped_with("上游 parse"));
    }
    assert_eq!(report.failed_stage(), Some(Stage::Parse));
    assert!(!report.passed());
}

/// 矩阵 3b：resolve 失败（E2002）→ typecheck 起短路。
#[test]
fn resolve_failure_matrix() {
    let dir = FixtureDir::new(
        "resolve",
        &[("main.lang-go", "fn main() {\n    println(ghost)\n}\n")],
    );
    let report = lang_verify::verify(&dir.path);
    assert_stage(&report, Stage::Parse, is_passed);
    assert_stage(&report, Stage::Resolve, is_failed_with("E2002"));
    assert_stage(&report, Stage::Typecheck, is_skipped_with("上游 resolve"));
    assert_eq!(report.failed_stage(), Some(Stage::Resolve));
}

/// 矩阵 3c：typecheck 失败 → hir 起短路。
#[test]
fn typecheck_failure_matrix() {
    let dir = FixtureDir::new(
        "typecheck",
        &[(
            "main.lang-go",
            "fn main() {\n    let x: int = \"s\"\n    println(x)\n}\n",
        )],
    );
    let report = lang_verify::verify(&dir.path);
    assert_stage(
        &report,
        Stage::Typecheck,
        |o| matches!(o, StageOutcome::Failed(m) if m.contains("E30")),
    );
    assert_stage(&report, Stage::Hir, is_skipped_with("上游 typecheck"));
    assert_eq!(report.failed_stage(), Some(Stage::Typecheck));
}

/// 矩阵 4：差分失配（js 路被篡改）→ Differential Failed 带证据，
/// 失败阶段按执行序取 differential。
#[test]
fn differential_mismatch_is_failed_with_evidence() {
    let dir = FixtureDir::new("tamper", &[("main.lang-go", GREEN)]);
    let input = VerifyInput::from_dir(&dir.path).expect("装载夹具目录");
    let mut driver = TestDriver::new(true);
    let r = run_pipeline(&input, &mut driver);
    assert_stage(&r, Stage::Execute, is_passed);
    assert_stage(&r, Stage::Differential, is_failed_with("js"));
    assert!(!r.passed());
    assert_eq!(r.failed_stage(), Some(Stage::Differential));
}

/// 矩阵 5：StubExec → 前端四段 + effect 真跑，执行段全部 Skipped 且报告整体
/// 仍标记绿（Skipped ≠ 失败）——桩可见性即编排验收。
#[test]
fn stub_exec_front_stages_pass_exec_visible_skipped() {
    let dir = FixtureDir::new("stub", &[("main.lang-go", GREEN)]);
    let input = VerifyInput::from_dir(&dir.path).expect("装载夹具目录");
    let r = run_pipeline(&input, &mut StubExec);
    assert_eq!(r.stages.len(), 9, "九段全部在册");
    for s in [
        Stage::Parse,
        Stage::Resolve,
        Stage::Typecheck,
        Stage::Hir,
        Stage::Effect,
    ] {
        assert_stage(&r, s, is_passed);
    }
    assert_stage(&r, Stage::Ir, is_skipped_with("桩"));
    assert_stage(&r, Stage::Compile, is_skipped_with("ir"));
    assert_stage(&r, Stage::Execute, is_skipped_with("AST 参考路不可用"));
    assert!(r.passed());
    assert_eq!(r.failed_stage(), None);
}

/// verify_files（内存文件集）与 from_dir 同矩阵：前端四段 + effect 全过。
#[test]
fn verify_files_memory_input() {
    let report = lang_verify::verify_files(vec![("main.lang-go".into(), GREEN.into())]);
    for s in [
        Stage::Parse,
        Stage::Resolve,
        Stage::Typecheck,
        Stage::Hir,
        Stage::Effect,
    ] {
        assert_stage(&report, s, is_passed);
    }
}
