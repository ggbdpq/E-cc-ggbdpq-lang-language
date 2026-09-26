//! verify 九段管线编排（specs/runner-contract.md verify 动词）。
//!
//! 执行序 = 冻结 [`Stage::ALL`]：parse → resolve → typecheck → hir → ir →
//! effect → compile → execute → differential。语义分段：
//! - Parse/Resolve/Typecheck/Hir = 前端四段（lang-syntax + lang-semantic +
//!   lang-hir，全部已落地）；
//! - Ir/执行四段（AST Exec / IR Exec / →Go 生成+执行 / →JS 生成+执行）=
//!   [`ExecDriver`] 注入面（A3 lowering 未接入 → DefaultExec 如实 Skipped）；
//! - Effect = lang_contract 不动点引擎，事实取自 AST + SymbolId（effects.rs）；
//! - Differential = 可比执行路的 stdout 逐字节 + exitCode + stderr 汇总比对。
//!
//! 短路纪律：任一段 Failed → 后续段全部 Skipped（"上游 <段> 失败"）且在
//! 报告中显式可见；Skipped = 环境性跳过，不算失败也不算通过——不静默、
//! 不谎报。

use std::path::Path;

use lang_hir::LowerHir;
use lang_semantic::analyze_stmts;

use crate::effects::solve_effects;
use crate::exec::{ExecContext, ExecDriver, ExecError, RunOutput};
use crate::input::VerifyInput;
use crate::{Stage, StageOutcome, VerifyReport};

/// 对模块目录跑全管线（DefaultExec；runner-contract 的 `verify <dir>` 语义）。
pub fn verify(dir: &Path) -> VerifyReport {
    match VerifyInput::from_dir(dir) {
        Ok(input) => run_pipeline(&input, &mut crate::exec::DefaultExec::new()),
        Err(msg) => {
            // 装载失败归 Parse 段报告（目录不可读 / import 展开/循环错误）
            let mut r = VerifyReport {
                stages: vec![(Stage::Parse, StageOutcome::Failed(msg))],
            };
            fill_skipped(&mut r, Stage::Parse);
            r
        }
    }
}

/// 对内存文件集跑全管线（宿主直供；测试主路径）。
pub fn verify_files(files: Vec<(String, String)>) -> VerifyReport {
    run_pipeline(
        &VerifyInput::from_files(files),
        &mut crate::exec::DefaultExec::new(),
    )
}

/// 全管线编排：逐段推进，任一失败即短路。
pub fn run_pipeline(input: &VerifyInput, driver: &mut dyn ExecDriver) -> VerifyReport {
    let mut report = VerifyReport { stages: Vec::new() };

    // —— 1) Parse ——
    let files = match input.parse_all() {
        Ok(f) => f,
        Err((path, d)) => {
            fail_short_circuit(
                &mut report,
                Stage::Parse,
                format!(
                    "{path} 第 {} 行: {}",
                    d.primary_span.map(|s| s.start.line).unwrap_or(0),
                    d.message
                ),
            );
            return report;
        }
    };
    report.stages.push((Stage::Parse, StageOutcome::Passed));

    let mut merged: Vec<lang_syntax::Stmt> = Vec::new();
    for (_, stmts) in &files {
        // import 是装载指令非运行时语句：装载器为文件级合并、产物保留 Import
        // 原句，AST 解释器对其报错而 lowering 静默跳过——在合并点统一滤除
        // （下游 effect/execute 消费同一 merged；与 lang-conformance load_case
        // 同一消费方义务）。
        merged.extend(
            stmts
                .iter()
                .filter(|s| !matches!(s, lang_syntax::Stmt::Import { .. }))
                .cloned(),
        );
    }

    // —— 2) Resolve / 3) Typecheck（一次 analyze；resolver 失败时 checker 不运行）——
    let mut analysis = analyze_stmts(&merged, lang_core::SourceId(0));
    if !analysis.checked && analysis.has_errors() {
        fail_short_circuit(&mut report, Stage::Resolve, diag_summary(&analysis));
        return report;
    }
    report.stages.push((Stage::Resolve, StageOutcome::Passed));
    if analysis.has_errors() {
        fail_short_circuit(&mut report, Stage::Typecheck, diag_summary(&analysis));
        return report;
    }
    report.stages.push((Stage::Typecheck, StageOutcome::Passed));

    // —— 4) Hir ——
    let hir = match lang_hir::HirBuilder::new(&mut analysis).lower(&merged) {
        Ok(h) => h,
        Err(e) => {
            fail_short_circuit(&mut report, Stage::Hir, format!("HIR 构建: {}", e.0));
            return report;
        }
    };
    report.stages.push((Stage::Hir, StageOutcome::Passed));

    // —— 5) Ir ——
    let ir = match driver.lower_ir(&hir, &analysis) {
        Ok(prog) => match lang_ir::validate(&prog) {
            Ok(()) => {
                report.stages.push((Stage::Ir, StageOutcome::Passed));
                Some(prog)
            }
            Err(e) => {
                fail_short_circuit(&mut report, Stage::Ir, format!("IR 校验: {e}"));
                return report;
            }
        },
        Err(ExecError::Skipped(m)) => {
            report.stages.push((Stage::Ir, StageOutcome::Skipped(m)));
            None
        }
        Err(ExecError::Failed(m)) => {
            fail_short_circuit(&mut report, Stage::Ir, m);
            return report;
        }
    };

    // —— 6) Effect ——
    match solve_effects(&merged, &analysis) {
        Ok(entries) => {
            report.stages.push((Stage::Effect, StageOutcome::Passed));
            let _ = entries; // 效果面由 inspect.effects 消费；本段判据 = 求解成功
        }
        Err(m) => {
            fail_short_circuit(&mut report, Stage::Effect, m);
            return report;
        }
    }

    // —— 7) Compile（→Go / →JS 生成；产物可生成性，产物本体由执行段重新生成）——
    match &ir {
        Some(prog) => {
            if let Err(e) = lang_backend_go::codegen::generate(prog) {
                fail_short_circuit(&mut report, Stage::Compile, format!("→Go 生成: {e}"));
                return report;
            }
            if let Err(e) = lang_backend_js::codegen::generate(prog) {
                fail_short_circuit(&mut report, Stage::Compile, format!("→JS 生成: {e}"));
                return report;
            }
            report.stages.push((Stage::Compile, StageOutcome::Passed));
        }
        None => {
            report.stages.push((
                Stage::Compile,
                StageOutcome::Skipped("上游 ir 阶段未产出 IR".to_string()),
            ));
        }
    };

    // —— 8) Execute（AST 参考路必须成功；IR/宿主路按可用性参与）——
    let mut outs: Vec<(&'static str, RunOutput)> = Vec::new();
    let mut skips: Vec<String> = Vec::new();
    let mut execute_failed: Option<String> = None;
    let ctx = ExecContext {
        stmts: &merged,
        analysis: &analysis,
    };
    match driver.run_ast(&ctx) {
        Ok(o) => outs.push(("ast", o)),
        Err(ExecError::Skipped(m)) => {
            report.stages.push((
                Stage::Execute,
                StageOutcome::Skipped(format!("AST 参考路不可用: {m}")),
            ));
            fill_skipped(&mut report, Stage::Execute);
            return report;
        }
        Err(ExecError::Failed(m)) => execute_failed = Some(format!("ast: {m}")),
    }
    if execute_failed.is_none() {
        if let Some(prog) = &ir {
            match driver.run_ir(prog) {
                Ok(o) => outs.push(("ir", o)),
                Err(ExecError::Skipped(m)) => skips.push(skip_note("ir", &m)),
                Err(ExecError::Failed(m)) => execute_failed = Some(format!("ir: {m}")),
            }
        }
    }
    if execute_failed.is_none() && ir.is_some() {
        match driver.run_go(prog_of(&ir)) {
            Ok(o) => outs.push(("go", o)),
            Err(ExecError::Skipped(m)) => skips.push(skip_note("go", &m)),
            Err(ExecError::Failed(m)) => execute_failed = Some(format!("go: {m}")),
        }
    }
    if execute_failed.is_none() && ir.is_some() {
        match driver.run_js(prog_of(&ir)) {
            Ok(o) => outs.push(("js", o)),
            Err(ExecError::Skipped(m)) => skips.push(skip_note("js", &m)),
            Err(ExecError::Failed(m)) => execute_failed = Some(format!("js: {m}")),
        }
    }
    if let Some(m) = execute_failed {
        fail_short_circuit(&mut report, Stage::Execute, m);
        return report;
    }
    if outs.is_empty() {
        report.stages.push((
            Stage::Execute,
            StageOutcome::Skipped(format!(
                "无可用执行路（{} 段跳过: {}）",
                skips.len(),
                skips.join(", ")
            )),
        ));
        fill_skipped(&mut report, Stage::Execute);
        return report;
    }
    if let Some((name, o)) = outs
        .iter()
        .find(|(_, o)| o.exit_code != 0 || !o.stderr.is_empty())
    {
        fail_short_circuit(
            &mut report,
            Stage::Execute,
            format!(
                "执行路 {name} 非零退出（code={}）: {}",
                o.exit_code, o.stderr
            ),
        );
        return report;
    }
    report.stages.push((Stage::Execute, StageOutcome::Passed));

    // —— 9) Differential（stdout 逐字节 + exitCode + stderr 全比对；
    //         缺失路显式呈现，不谎报全路一致）——
    if outs.len() < 2 {
        report.stages.push((
            Stage::Differential,
            StageOutcome::Skipped(format!(
                "可比执行路不足（{} 路）: {}",
                outs.len(),
                skips.join(", ")
            )),
        ));
        return report;
    }
    let (base_name, base) = &outs[0];
    for (name, o) in &outs[1..] {
        if o.stdout != base.stdout || o.exit_code != base.exit_code || !o.stderr.is_empty() {
            fail_short_circuit(
                &mut report,
                Stage::Differential,
                format!(
                    "执行路 {name} 与 {base_name} 不一致: stdout {:?} vs {:?}，exit {} vs {}",
                    o.stdout, base.stdout, o.exit_code, base.exit_code
                ),
            );
            return report;
        }
    }
    if !skips.is_empty() {
        report.stages.push((
            Stage::Differential,
            StageOutcome::Skipped(format!(
                "已比对 {} 路逐字节一致；缺失路: {}",
                outs.len(),
                skips.join(", ")
            )),
        ));
        return report;
    }
    report
        .stages
        .push((Stage::Differential, StageOutcome::Passed));
    report
}

/// ir 选项的引用拆包（Execute 段内部保证非 None 才走到）。
fn prog_of(ir: &Option<lang_ir::IrProgram>) -> &lang_ir::IrProgram {
    ir.as_ref().expect("Execute 段仅在 ir 就绪时执行宿主路")
}

/// 跳过路记录（`路名: 原因`，差分段的显式呈现素材）。
fn skip_note(name: &str, reason: &str) -> String {
    format!("{name}({reason})")
}

/// 失败段入报告并短路后续段（Skipped 显式可见）。
fn fail_short_circuit(report: &mut VerifyReport, failed: Stage, msg: String) {
    report.stages.push((failed, StageOutcome::Failed(msg)));
    fill_skipped(report, failed);
}

/// 把 failed 之后的全部阶段补成 Skipped（短路可见性）。
fn fill_skipped(report: &mut VerifyReport, failed: Stage) {
    let seen = report.stages.iter().map(|(s, _)| *s).collect::<Vec<_>>();
    for s in Stage::ALL {
        if s > failed && !seen.contains(&s) {
            report.stages.push((
                s,
                StageOutcome::Skipped(format!("上游 {} 未通过，短路", failed.as_str())),
            ));
        }
    }
}

/// 诊断摘要（resolve/typecheck 失败消息；码面 + 首条消息）。
fn diag_summary(analysis: &lang_semantic::Analysis) -> String {
    let codes: Vec<String> = analysis
        .diags
        .iter()
        .map(|d| d.code.as_str().to_string())
        .collect();
    let first = analysis
        .diags
        .first()
        .map(|d| {
            format!(
                "第 {} 行: {}",
                d.primary_span.map(|s| s.start.line).unwrap_or(0),
                d.message
            )
        })
        .unwrap_or_default();
    format!("{} 条诊断 [{codes:?}]: {first}", analysis.diags.len())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::StageOutcome::*;
    use crate::exec::{DefaultExec, StubExec};

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

    fn stage(report: &VerifyReport, s: Stage) -> Option<&StageOutcome> {
        report
            .stages
            .iter()
            .find(|(st, _)| *st == s)
            .map(|(_, o)| o)
    }

    /// 桩驱动 + 全绿夹具：前端四段全过，执行段全部 Skipped 且显式可见。
    #[test]
    fn stub_pipeline_front_stages_pass_exec_visible_skipped() {
        let input = VerifyInput::from_files(vec![("main.lang-go".into(), GREEN.into())]);
        let r = run_pipeline(&input, &mut StubExec);
        assert_eq!(r.stages.len(), 9, "九段全部在报告里（含短路 Skipped）");
        assert!(matches!(stage(&r, Stage::Parse), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Resolve), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Typecheck), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Hir), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Ir), Some(Skipped(m)) if m.contains("桩")));
        assert!(matches!(stage(&r, Stage::Effect), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Compile), Some(Skipped(_))));
        assert!(
            matches!(stage(&r, Stage::Execute), Some(Skipped(m)) if m.contains("AST 参考路不可用"))
        );
        assert!(matches!(stage(&r, Stage::Differential), Some(Skipped(_))));
        // Skipped ≠ 失败：报告整体为绿
        assert!(r.passed());
        assert_eq!(r.failed_stage(), None);
    }

    /// Parse 失败 → 短路：后续段 Skipped 可见。
    #[test]
    fn parse_failure_short_circuits_with_visible_skips() {
        let input = VerifyInput::from_files(vec![("main.lang-go".into(), "fn main( {".into())]);
        let r = run_pipeline(&input, &mut StubExec);
        assert!(matches!(stage(&r, Stage::Parse), Some(Failed(m)) if m.contains("main.lang-go")));
        for s in [
            Stage::Resolve,
            Stage::Typecheck,
            Stage::Hir,
            Stage::Ir,
            Stage::Effect,
            Stage::Compile,
            Stage::Execute,
            Stage::Differential,
        ] {
            assert!(
                matches!(stage(&r, s), Some(Skipped(m)) if m.contains("上游 parse")),
                "{s:?} 应为短路 Skipped"
            );
        }
        assert!(!r.passed());
        assert_eq!(r.failed_stage(), Some(Stage::Parse));
    }

    /// Resolve 失败（E2002 未定义）→ typecheck 起短路。
    #[test]
    fn resolve_failure_short_circuits() {
        let src = "fn main() {\n    println(ghost)\n}\n";
        let input = VerifyInput::from_files(vec![("main.lang-go".into(), src.into())]);
        let r = run_pipeline(&input, &mut StubExec);
        assert!(matches!(stage(&r, Stage::Parse), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Resolve), Some(Failed(m)) if m.contains("E2002")));
        assert!(
            matches!(stage(&r, Stage::Typecheck), Some(Skipped(m)) if m.contains("上游 resolve"))
        );
        assert_eq!(r.failed_stage(), Some(Stage::Resolve));
    }

    /// Typecheck 失败（E3011 类型不匹配）→ hir 起短路。
    #[test]
    fn typecheck_failure_short_circuits() {
        let src = "fn main() {\n    let x: int = \"s\"\n    println(x)\n}\n";
        let input = VerifyInput::from_files(vec![("main.lang-go".into(), src.into())]);
        let r = run_pipeline(&input, &mut StubExec);
        assert!(matches!(stage(&r, Stage::Parse), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Resolve), Some(Passed)));
        let tc = stage(&r, Stage::Typecheck);
        assert!(
            matches!(tc, Some(Failed(m)) if m.contains("E30")),
            "实际: {tc:?}"
        );
        assert!(matches!(stage(&r, Stage::Hir), Some(Skipped(_))));
        assert_eq!(r.failed_stage(), Some(Stage::Typecheck));
    }

    /// DefaultExec 五段全接（A3 lowering + A4 双解释器 + A5 生成）：内存文件集
    /// 上 ir/compile/execute 全过；差分仅有 ast/ir 两路（宿主工具链路按环境
    /// 跳过）——两路一致即差分对可用路成立，缺失路显式 Skipped。
    #[test]
    fn default_exec_full_chain_through_lowering() {
        let input = VerifyInput::from_files(vec![("main.lang-go".into(), GREEN.into())]);
        let r = run_pipeline(&input, &mut DefaultExec::new());
        assert!(matches!(stage(&r, Stage::Ir), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Effect), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Compile), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Execute), Some(Passed)));
        // 差分：ast/ir 两路真跑且一致；go/js 视宿主工具链（缺失显式点名）
        let diff = stage(&r, Stage::Differential);
        assert!(
            matches!(diff, Some(Passed))
                || matches!(diff, Some(Skipped(m)) if m.contains("已比对")),
            "差分实际: {diff:?}"
        );
        assert!(r.passed());
        assert_eq!(r.failed_stage(), None);
    }

    /// 多文件输入：按路径字典序合并分析（跨文件命名空间合并后全绿）。
    #[test]
    fn multi_file_merge_passes_front_stages() {
        let input = VerifyInput::from_files(vec![
            (
                "main.lang-go".into(),
                "import \"./util\"\nfn main() {\n    println(twice(21))\n}\n".into(),
            ),
            (
                "util.lang-go".into(),
                "fn twice(x: int): int {\n    return x * 2\n}\n".into(),
            ),
        ]);
        let r = run_pipeline(&input, &mut StubExec);
        assert!(matches!(stage(&r, Stage::Parse), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Resolve), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Typecheck), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Hir), Some(Passed)));
    }

    /// 带 import 语句的内存文件集走 DefaultExec 全链：Import 在合并点滤除后
    /// AST 参考路可执行（对 lang-conformance load_case 同坑的管线侧修复锁定）。
    #[test]
    fn import_statements_filtered_for_execution() {
        let input = VerifyInput::from_files(vec![
            (
                "main.lang-go".into(),
                "import \"./util\"\nfn main() {\n    println(twice(21))\n}\n".into(),
            ),
            (
                "util.lang-go".into(),
                "fn twice(x: int): int {\n    return x * 2\n}\n".into(),
            ),
        ]);
        let r = run_pipeline(&input, &mut DefaultExec::new());
        assert!(matches!(stage(&r, Stage::Ir), Some(Passed)));
        assert!(matches!(stage(&r, Stage::Execute), Some(Passed)));
        assert!(r.passed());
        assert_eq!(r.failed_stage(), None);
    }
}
