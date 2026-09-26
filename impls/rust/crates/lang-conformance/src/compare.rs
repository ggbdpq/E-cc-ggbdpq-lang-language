//! Run/Check case 的比较策略（specs/runner-contract.md「run/check/通用约束」节）。
//!
//! run 模式 = 四条执行路径（AST 解释 / IR 解释 / →Go 产物 / →JS 产物）各自
//! stdout 与期望**逐字节**一致、exitCode 0、stderr 空；check 模式 = 静态诊断的
//! `code|ruleId|severity|line` **多重集**相等（message 与列号允许漂移）。
//!
//! 执行器是注入点：[`RunRoute`] 的实现方在未来接入解释器与双后端（本波不接解释器，
//! 这里只定死比较骨架与判定语义，测试用假执行器驱动）。

use std::collections::BTreeMap;
use std::path::Path;

use crate::manifest::{Case, Expect, Mode};

/// 四条 run 路径的规范名（runner-contract：解释器 / →Go / →JS；Rust 侧解释器
/// 分 AST/IR 两路，对应复刻计划的差分矩阵）。
pub const ROUTE_INTERP_AST: &str = "interp-ast";
pub const ROUTE_INTERP_IR: &str = "interp-ir";
pub const ROUTE_GO: &str = "go";
pub const ROUTE_JS: &str = "js";

/// run 模式的全部路径（顺序即判定顺序；缺工具链的路径按 skipped 处理，不谎报通过）。
pub const RUN_ROUTES: [&str; 4] = [ROUTE_INTERP_AST, ROUTE_INTERP_IR, ROUTE_GO, ROUTE_JS];

/// 单路执行输出。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RouteOutput {
    pub stdout: String,
    pub stderr: String,
    pub exit_code: i32,
}

/// 单路执行的失败形态：环境性跳过（宿主缺工具链）与真实执行失败必须区分。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RouteError {
    /// 工具链缺失等环境原因——skipped，不计入通过（runner-contract 通用约束 5）。
    Skipped(String),
    /// 执行/编译失败——直接判败。
    Failed(String),
}

/// 一条执行路径（解释器或后端执行器实现本 trait 后即可接入判定）。
pub trait RunRoute {
    fn name(&self) -> &'static str;
    fn run(&self, case_dir: &Path) -> Result<RouteOutput, RouteError>;
}

/// 单路 run 比对差异（逐字节语义）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RunDiff {
    ExitCode { expected: i64, actual: i32 },
    Stdout { expected: String, actual: String },
    Stderr { expected: String, actual: String },
}

/// 单路比对：返回全部差异，空 = 通过。
pub fn compare_run(expect: &Expect, got: &RouteOutput) -> Vec<RunDiff> {
    let mut diffs = Vec::new();
    if got.exit_code != expect.exit_code as i32 {
        diffs.push(RunDiff::ExitCode {
            expected: expect.exit_code,
            actual: got.exit_code,
        });
    }
    if got.stdout != expect.stdout {
        diffs.push(RunDiff::Stdout {
            expected: expect.stdout.clone(),
            actual: got.stdout.clone(),
        });
    }
    if got.stderr != expect.stderr {
        diffs.push(RunDiff::Stderr {
            expected: expect.stderr.clone(),
            actual: got.stderr.clone(),
        });
    }
    diffs
}

/// 一条诊断的比较键投影（期望侧由 manifest `DiagExpect` 转换而来）。
/// key 不含 message 与列号——那是 diagnostics spec 既定的漂移面。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DiagActual {
    pub code: String,
    pub rule_id: String,
    pub severity: String,
    pub line: u32,
}

impl DiagActual {
    fn key(&self) -> String {
        format!(
            "{}|{}|{}|{}",
            self.code, self.rule_id, self.severity, self.line
        )
    }
}

impl From<&crate::manifest::DiagExpect> for DiagActual {
    fn from(d: &crate::manifest::DiagExpect) -> Self {
        Self {
            code: d.code.clone(),
            rule_id: d.rule_id.clone(),
            severity: d.severity.clone(),
            line: d.line,
        }
    }
}

/// check 多重集比较结果：缺失/多出各按 `code|rule|severity|line×n` 报告。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CheckDiff {
    pub missing: Vec<String>,
    pub extra: Vec<String>,
}

impl CheckDiff {
    pub fn is_empty(&self) -> bool {
        self.missing.is_empty() && self.extra.is_empty()
    }
}

/// 诊断多重集比对（与 Python 证据同口径：key 计数，缺/多分别清点后排序）。
pub fn compare_check(expected: &[DiagActual], actual: &[DiagActual]) -> CheckDiff {
    let mut want: BTreeMap<String, i64> = BTreeMap::new();
    for d in expected {
        *want.entry(d.key()).or_insert(0) += 1;
    }
    let mut got: BTreeMap<String, i64> = BTreeMap::new();
    for d in actual {
        *got.entry(d.key()).or_insert(0) += 1;
    }
    let mut missing = Vec::new();
    let mut extra = Vec::new();
    for (k, n) in &want {
        let g = got.get(k).copied().unwrap_or(0);
        if g < *n {
            missing.push(format!("{k}×{}", n - g));
        }
    }
    for (k, n) in &got {
        let w = want.get(k).copied().unwrap_or(0);
        if w < *n {
            extra.push(format!("{k}×{}", n - w));
        }
    }
    missing.sort();
    extra.sort();
    CheckDiff { missing, extra }
}

/// 一个 case 的判定结果（与三实现证据同构：ok/skipped/note）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CaseResult {
    pub name: String,
    pub category: String,
    pub mode: Mode,
    pub ok: bool,
    /// 工具链缺失等环境性跳过：不计入通过，也不谎报失败。
    pub skipped: bool,
    pub note: String,
}

impl CaseResult {
    fn of(case: &Case) -> Self {
        Self {
            name: case.name.clone(),
            category: case.category.clone(),
            mode: case.mode,
            ok: true,
            skipped: false,
            note: String::new(),
        }
    }
}

/// 判定 run 模式的单路结果。多路 case 由调用方对每条 [`RUN_ROUTES`] 各调一次，
/// 任一路失败即整体失败（三方/四路输出本就要求逐字节一致）。
pub fn judge_run(
    case: &Case,
    route: &str,
    outcome: &Result<RouteOutput, RouteError>,
) -> CaseResult {
    let mut res = CaseResult::of(case);
    match outcome {
        Err(RouteError::Skipped(reason)) => {
            res.ok = false;
            res.skipped = true;
            res.note = format!("{route}: {reason}");
            res
        }
        Err(RouteError::Failed(reason)) => {
            res.ok = false;
            res.note = format!("{route}: {reason}");
            res
        }
        Ok(out) => {
            let diffs = compare_run(&case.expect, out);
            if diffs.is_empty() {
                res
            } else {
                res.ok = false;
                res.note = format!("{route}: {}", render_diffs(&diffs));
                res
            }
        }
    }
}

/// 判定 check 模式的诊断多重集（诊断来自注入的检查器）。
pub fn judge_check(case: &Case, actual: &[DiagActual]) -> CaseResult {
    let mut res = CaseResult::of(case);
    let expected: Vec<DiagActual> = case
        .expect
        .diagnostics
        .iter()
        .map(DiagActual::from)
        .collect();
    let diff = compare_check(&expected, actual);
    if diff.is_empty() {
        res
    } else {
        res.ok = false;
        res.note = format!(
            "诊断不匹配 缺{} 多{}",
            diff.missing.join(","),
            diff.extra.join(",")
        );
        res
    }
}

fn render_diffs(diffs: &[RunDiff]) -> String {
    diffs
        .iter()
        .map(|d| match d {
            RunDiff::ExitCode { expected, actual } => {
                format!("exitCode 期望 {expected} 实际 {actual}")
            }
            RunDiff::Stdout { expected, actual } => {
                format!("stdout 不一致 期望 {expected:?} 实际 {actual:?}")
            }
            RunDiff::Stderr { expected, actual } => {
                format!("stderr 期望 {expected:?} 实际 {actual:?}")
            }
        })
        .collect::<Vec<_>>()
        .join("; ")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn expect_sample() -> Expect {
        Expect {
            exit_code: 0,
            stdout: "ok\n".into(),
            stderr: String::new(),
            diagnostics: Vec::new(),
        }
    }

    fn case_sample(mode: Mode) -> Case {
        Case {
            name: "demo".into(),
            category: "types".into(),
            mode,
            rule_ids: vec!["TYPE-ASSIGN-001".into()],
            expect: match mode {
                Mode::Run => expect_sample(),
                Mode::Check => Expect {
                    exit_code: 0,
                    stdout: String::new(),
                    stderr: String::new(),
                    diagnostics: vec![crate::manifest::DiagExpect {
                        code: "E3001".into(),
                        rule_id: "TYPE-ASSIGN-001".into(),
                        severity: "error".into(),
                        line: 2,
                    }],
                },
            },
        }
    }

    /// 四路路径名冻结（协议面：路由名会出现在 note/报告里）。
    #[test]
    fn run_routes_are_fixed() {
        assert_eq!(RUN_ROUTES, ["interp-ast", "interp-ir", "go", "js"]);
    }

    /// stdout 逐字节：差一个换行都算失败。
    #[test]
    fn run_stdout_is_byte_exact() {
        let case = case_sample(Mode::Run);
        let ok = judge_run(
            &case,
            ROUTE_INTERP_AST,
            &Ok(RouteOutput {
                stdout: "ok\n".into(),
                stderr: String::new(),
                exit_code: 0,
            }),
        );
        assert!(ok.ok);
        let off_by_newline = judge_run(
            &case,
            ROUTE_INTERP_AST,
            &Ok(RouteOutput {
                stdout: "ok".into(),
                stderr: String::new(),
                exit_code: 0,
            }),
        );
        assert!(!off_by_newline.ok);
        assert!(off_by_newline.note.contains("stdout"));
    }

    /// 退出码与 stderr 一致纳入判定。
    #[test]
    fn run_checks_exit_code_and_stderr() {
        let case = case_sample(Mode::Run);
        let bad = judge_run(
            &case,
            ROUTE_GO,
            &Ok(RouteOutput {
                stdout: "ok\n".into(),
                stderr: "panic\n".into(),
                exit_code: 1,
            }),
        );
        assert!(!bad.ok);
        assert!(bad.note.contains("exitCode"));
        assert!(bad.note.contains("stderr"));
    }

    /// skipped ≠ 通过（不谎报绿）；执行失败直接判败。
    #[test]
    fn run_route_skipped_is_not_pass() {
        let case = case_sample(Mode::Run);
        let skipped = judge_run(
            &case,
            ROUTE_JS,
            &Err(RouteError::Skipped("宿主缺 node".into())),
        );
        assert!(!skipped.ok);
        assert!(skipped.skipped);
        let failed = judge_run(
            &case,
            ROUTE_GO,
            &Err(RouteError::Failed("go build 失败".into())),
        );
        assert!(!failed.ok);
        assert!(!failed.skipped);
    }

    /// check 多重集：数量差异被精确清点（期望 2 条只来 1 条 = 缺 1）。
    #[test]
    fn check_multiset_counts() {
        let case = case_sample(Mode::Check);
        let diag = || DiagActual {
            code: "E3001".into(),
            rule_id: "TYPE-ASSIGN-001".into(),
            severity: "error".into(),
            line: 2,
        };
        // 期望 1 条 → 实际 2 条：多 1
        let r = judge_check(&case, &[diag(), diag()]);
        assert!(!r.ok);
        assert!(
            r.note.contains("多E3001|TYPE-ASSIGN-001|error|2×1"),
            "{}",
            r.note
        );
        // 行号不同 = 不同键：缺 + 多
        let mut shifted = diag();
        shifted.line = 3;
        let r = judge_check(&case, &[shifted]);
        assert!(!r.ok);
        assert!(r.note.contains("缺E3001|TYPE-ASSIGN-001|error|2×1"));
        assert!(r.note.contains("多E3001|TYPE-ASSIGN-001|error|3×1"));
        // 完全一致：通过
        assert!(judge_check(&case, &[diag()]).ok);
        // message 与列号允许漂移：比较键不含它们（DiagActual 无此字段即证）
    }

    /// 假执行器驱动 RunRoute 注入点：trait 面向未来解释器/后端。
    #[test]
    fn run_route_trait_injects_executor() {
        struct Fake;
        impl RunRoute for Fake {
            fn name(&self) -> &'static str {
                ROUTE_INTERP_IR
            }
            fn run(&self, _case_dir: &Path) -> Result<RouteOutput, RouteError> {
                Ok(RouteOutput {
                    stdout: "ok\n".into(),
                    stderr: String::new(),
                    exit_code: 0,
                })
            }
        }
        let case = case_sample(Mode::Run);
        let out = Fake.run(Path::new(".")).unwrap();
        assert!(judge_run(&case, Fake.name(), &Ok(out)).ok);
    }
}
