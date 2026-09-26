//! `verify [dir] [--json]`：lang-verify 九段管线 + 报告打印。
//!
//! 装载缺口绕行（登记于 status）：lang-verify 的管线合并语句流保留 `import`
//! 语句，AST 解释器对 Import 拒执行——`ImportStripExec` 薄包装（ExecDriver
//! 是 lang-verify 文档化的宿主注入点）在 run_ast 前剥除 import 语句，其余
//! 四段直通 DefaultExec。HIR/IR 段不受影响（lowering 不产 import 形态）。

use std::path::Path;

use lang_syntax::Stmt;
use lang_verify::{
    DefaultExec, ExecContext, ExecDriver, ExecError, RunOutput, Stage, StageOutcome, VerifyInput,
    VerifyReport, run_pipeline,
};
use serde_json::json;

use crate::commands::{has_flag, positional};
use crate::out::{envelope, print_json};

/// DefaultExec 薄包装：AST 执行段剥除 import 语句（其余段零改动透传）。
struct ImportStripExec {
    inner: DefaultExec,
}

impl ExecDriver for ImportStripExec {
    fn run_ast(&mut self, ctx: &ExecContext<'_>) -> Result<RunOutput, ExecError> {
        let stripped: Vec<Stmt> = ctx
            .stmts
            .iter()
            .filter(|s| !matches!(s, Stmt::Import { .. }))
            .cloned()
            .collect();
        let inner_ctx = ExecContext {
            stmts: &stripped,
            analysis: ctx.analysis,
        };
        self.inner.run_ast(&inner_ctx)
    }
    fn lower_ir(
        &mut self,
        hir: &lang_hir::HirProg,
        analysis: &lang_semantic::Analysis,
    ) -> Result<lang_ir::IrProgram, ExecError> {
        self.inner.lower_ir(hir, analysis)
    }
    fn run_ir(&mut self, prog: &lang_ir::IrProgram) -> Result<RunOutput, ExecError> {
        self.inner.run_ir(prog)
    }
    fn run_go(&mut self, prog: &lang_ir::IrProgram) -> Result<RunOutput, ExecError> {
        self.inner.run_go(prog)
    }
    fn run_js(&mut self, prog: &lang_ir::IrProgram) -> Result<RunOutput, ExecError> {
        self.inner.run_js(prog)
    }
}

/// `verify [dir] [--json]`。退出码：报告 passed（含 Skipped）= 0，否则 1。
pub fn cmd_verify(args: &[String]) -> i32 {
    let dir = positional(args);
    let report = verify_dir(Path::new(&dir));
    if has_flag(args, "--json") {
        print_json(&report_json(&report));
    } else {
        for (stage, outcome) in &report.stages {
            match outcome {
                StageOutcome::Passed => println!("{} 通过", stage.as_str()),
                StageOutcome::Failed(m) => println!("{} 失败: {m}", stage.as_str()),
                StageOutcome::Skipped(m) => println!("{} 跳过: {m}", stage.as_str()),
            }
        }
        match report.failed_stage() {
            Some(s) => println!("验证失败: {}", s.as_str()),
            None => println!("验证通过"),
        }
    }
    if report.passed() { 0 } else { 1 }
}

/// lang_verify::verify 的包装（装载失败同样归 Parse 段报告）。
fn verify_dir(dir: &Path) -> VerifyReport {
    match VerifyInput::from_dir(dir) {
        Ok(input) => run_pipeline(
            &input,
            &mut ImportStripExec {
                inner: DefaultExec::new(),
            },
        ),
        Err(msg) => VerifyReport {
            stages: vec![(Stage::Parse, StageOutcome::Failed(msg))],
        },
    }
}

/// 报告 → 契约信封（kind = "verify"；Skipped 显式在册，不谎报通过）。
pub fn report_json(report: &VerifyReport) -> serde_json::Value {
    let mut v = envelope("verify");
    v["passed"] = json!(report.passed());
    v["failedStage"] = match report.failed_stage() {
        Some(s) => json!(s.as_str()),
        None => serde_json::Value::Null,
    };
    v["stages"] = json!(
        report
            .stages
            .iter()
            .map(|(stage, outcome)| {
                let (state, note) = match outcome {
                    StageOutcome::Passed => ("passed", String::new()),
                    StageOutcome::Failed(m) => ("failed", m.clone()),
                    StageOutcome::Skipped(m) => ("skipped", m.clone()),
                };
                json!({ "stage": stage.as_str(), "outcome": state, "note": note })
            })
            .collect::<Vec<_>>()
    );
    v
}
