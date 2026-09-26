//! 套件 runner：scan_suite → run 四路逐 case 比对 + check 诊断多重集比对 →
//! [`ConformanceReport`]（specs/runner-contract.md + conformance/README.md）。
//!
//! run 模式：四条路由（[`RUN_ROUTES`]）各自执行并经冻结的 [`judge_run`] 判定，
//! 路由名取自执行器本体。check 模式：装载 → `lang_semantic::analyze_stmts`
//! 直接产诊断 → 投影为 [`DiagActual`]（code/ruleId/severity/line）→ [`judge_check`]
//! 多重集比对——line 取 `primarySpan.start.line`，无位置诊断按 0（套件真值
//! `line: 0` 的发射形态，SPEC-GAP-RS-A6-03 口径）。

use std::path::{Path, PathBuf};

use crate::compare::{DiagActual, RunRoute, judge_check, judge_run};
use crate::manifest::{Case, Mode};
use crate::report::{CaseVerdict, CheckVerdict, ConformanceReport, RouteOutcome, RouteVerdict};
use crate::routes::{GoRoute, InterpAstRoute, InterpIrRoute, JsRoute, analyze_case, load_case};
use crate::scan::{ScanFailure, ScannedCase, scan_suite};

/// 全套件判定（内建四路执行器；套件扫描失败一次性返回）。
pub fn run_suite(root: &Path) -> Result<ConformanceReport, Vec<ScanFailure>> {
    let cases = scan_suite(root)?;
    Ok(run_cases(&cases))
}

/// 对已扫描 case 集判定（内建四路：AST/IR 解释器 + go/js 宿主执行）。
pub fn run_cases(cases: &[ScannedCase]) -> ConformanceReport {
    run_with_routes(
        cases,
        &InterpAstRoute,
        &InterpIrRoute,
        &GoRoute::new(),
        &JsRoute::new(),
    )
}

/// 路由注入形态：宿主可替换执行器（测试桩/定制工具链）。路由名须与
/// `RUN_ROUTES` 顺序平行——报告矩阵按位对齐，错位即总账失真。
pub fn run_with_routes(
    cases: &[ScannedCase],
    ast: &dyn RunRoute,
    ir: &dyn RunRoute,
    go: &dyn RunRoute,
    js: &dyn RunRoute,
) -> ConformanceReport {
    let routes: [&dyn RunRoute; 4] = [ast, ir, go, js];
    let mut out = Vec::with_capacity(cases.len());
    for sc in cases {
        match sc.case.mode {
            Mode::Run => {
                let mut verdicts = Vec::with_capacity(routes.len());
                for r in routes {
                    let res = judge_run(&sc.case, r.name(), &r.run(&sc.dir));
                    let outcome = if res.skipped {
                        RouteOutcome::Skipped(res.note)
                    } else if res.ok {
                        RouteOutcome::Passed
                    } else {
                        RouteOutcome::Failed(res.note)
                    };
                    verdicts.push(RouteVerdict {
                        route: r.name(),
                        outcome,
                    });
                }
                out.push(CaseVerdict {
                    category: sc.case.category.clone(),
                    name: sc.case.name.clone(),
                    mode: Mode::Run,
                    routes: verdicts,
                    check: None,
                });
            }
            Mode::Check => {
                let check = check_verdict(&sc.dir, &sc.case);
                out.push(CaseVerdict {
                    category: sc.case.category.clone(),
                    name: sc.case.name.clone(),
                    mode: Mode::Check,
                    routes: Vec::new(),
                    check: Some(check),
                });
            }
        }
    }
    ConformanceReport { cases: out }
}

/// check 语义判定：装载 → analyze → 诊断投影 → 多重集比对。
///
/// 装载/解析失败即 Failed（check case 必须可解析；根因在 parser/loader）。
fn check_verdict(dir: &Path, case: &Case) -> CheckVerdict {
    let stmts = match load_case(dir) {
        Ok(s) => s,
        Err(e) => {
            return CheckVerdict {
                ok: false,
                note: format!("装载失败: {e:?}"),
            };
        }
    };
    let analysis = analyze_case(&stmts);
    let actuals: Vec<DiagActual> = analysis
        .diags
        .iter()
        .map(|d| DiagActual {
            code: d.code.as_str().to_string(),
            rule_id: d.rule_id.as_str().to_string(),
            severity: d.severity.as_str().to_string(),
            // 无位置诊断 = 0（套件以 line: 0 声明，SPEC-GAP-RS-A6-03）
            line: d.primary_span.map(|s| s.start.line).unwrap_or(0),
        })
        .collect();
    let res = judge_check(case, &actuals);
    CheckVerdict {
        ok: res.ok,
        note: res.note,
    }
}

/// 套件根目录的仓库定位（impls/rust/crates/lang-conformance → 仓库根 conformance/）。
pub fn default_suite_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("..")
        .join("..")
        .join("conformance")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::compare::{
        ROUTE_GO, ROUTE_INTERP_AST, ROUTE_INTERP_IR, ROUTE_JS, RUN_ROUTES, RouteError, RouteOutput,
    };
    use std::path::PathBuf as PB;

    /// 假执行器：固定输出/Skipped（验证 run_with_routes 的注入与判定映射）。
    struct Fake(&'static str, Result<RouteOutput, RouteError>);

    impl RunRoute for Fake {
        fn name(&self) -> &'static str {
            self.0
        }
        fn run(&self, _dir: &Path) -> Result<RouteOutput, RouteError> {
            self.1.clone()
        }
    }

    fn ok_out() -> RouteOutput {
        RouteOutput {
            stdout: "ok\n".into(),
            stderr: String::new(),
            exit_code: 0,
        }
    }

    fn scanned_run(dir: &PB) -> ScannedCase {
        let src = "name: demo\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    ok\n  stderr: \"\"\n";
        ScannedCase {
            case: crate::manifest::parse_manifest(src).unwrap(),
            dir: dir.clone(),
        }
    }

    /// 四路注入：通过/Skipped/Failed 各自正确落入 RouteOutcome 三态。
    #[test]
    fn run_with_routes_maps_outcomes() {
        let case = scanned_run(&PB::from("."));
        let report = run_with_routes(
            std::slice::from_ref(&case),
            &Fake(ROUTE_INTERP_AST, Ok(ok_out())),
            &Fake(ROUTE_INTERP_IR, Err(RouteError::Skipped("缺 X".into()))),
            &Fake(ROUTE_GO, Err(RouteError::Failed("go build 失败".into()))),
            &Fake(
                ROUTE_JS,
                Err(RouteError::Skipped("宿主缺 node 工具链".into())),
            ),
        );
        assert_eq!(report.cases.len(), 1);
        let v = &report.cases[0].routes;
        assert_eq!(v[0].outcome, RouteOutcome::Passed);
        assert!(matches!(v[1].outcome, RouteOutcome::Skipped(ref m) if m.contains("缺 X")));
        assert!(matches!(v[2].outcome, RouteOutcome::Failed(ref m) if m.contains("go build")));
        assert!(matches!(v[3].outcome, RouteOutcome::Skipped(_)));
        // 路由名与 RUN_ROUTES 平行
        for (v, r) in v.iter().zip(RUN_ROUTES) {
            assert_eq!(v.route, r);
        }
        // 聚合：ast 全过、ir 1 skip、go 1 fail、js 1 skip
        assert_eq!(report.route_total(ROUTE_INTERP_AST).passed, 1);
        assert_eq!(report.route_total(ROUTE_INTERP_IR).skipped, 1);
        assert_eq!(report.route_total(ROUTE_GO).failed, 1);
        assert!(report.is_fully_covered());
    }

    /// 判定差异透传：期望与实际不符 → Failed（不是 Skipped）。
    #[test]
    fn run_with_routes_detects_diff() {
        let case = scanned_run(&PB::from("."));
        let bad = RouteOutput {
            stdout: "different\n".into(),
            stderr: String::new(),
            exit_code: 0,
        };
        let report = run_with_routes(
            std::slice::from_ref(&case),
            &Fake(ROUTE_INTERP_AST, Ok(bad)),
            &Fake(ROUTE_INTERP_IR, Ok(ok_out())),
            &Fake(ROUTE_GO, Ok(ok_out())),
            &Fake(ROUTE_JS, Ok(ok_out())),
        );
        let v = &report.cases[0].routes;
        assert!(matches!(v[0].outcome, RouteOutcome::Failed(ref m) if m.contains("stdout")));
        assert_eq!(report.route_total(ROUTE_INTERP_IR).passed, 1);
    }
}
