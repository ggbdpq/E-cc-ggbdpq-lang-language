//! ConformanceReport —— 全套件判定总账（分类×路由矩阵 + 逐 case 明细）。
//!
//! 消费面（冻结登记 A6-verify.md）：lang-cli 的 conformance 输出与
//! parity-report 都读本结构。诚实纪律落在类型里：[`RouteOutcome::Skipped`]
//! 与 [`RouteOutcome::Passed`] 是不同变体、分别计数（[`Cell`] 三格独立），
//! skipped 不计入通过也不谎报失败（runner-contract 通用约束 5）。
//!
//! 矩阵口径：run 模式每 case 对四条路由（[`crate::compare::RUN_ROUTES`]）各
//! 一个判定；check 模式每 case 一个语义判定（诊断多重集），无路由维度——
//! 汇总时 check 作为独立列呈现（`check_cell`/`check_total`）。

use serde::Serialize;

use crate::compare::RUN_ROUTES;
use crate::manifest::Mode;

/// 路由/语义判定的三态（Skipped ≠ 通过 ≠ 失败）。
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub enum RouteOutcome {
    Passed,
    /// 真实失败（比较差异 / 执行失败），note 带证据。
    Failed(String),
    /// 环境性跳过（工具链缺失 / extern·http 语料 / check 语义面的 run case），
    /// note 注明原因。
    Skipped(String),
}

/// run 模式单路判定。
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct RouteVerdict {
    /// 路由名（RUN_ROUTES 冻结名之一）。
    pub route: &'static str,
    pub outcome: RouteOutcome,
}

/// check 模式判定（诊断多重集比对结果）。
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct CheckVerdict {
    pub ok: bool,
    /// 失败时为「缺/多」清点（`code|rule|severity|line×n`），通过为空。
    pub note: String,
}

/// 单 case 判定明细。
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct CaseVerdict {
    pub category: String,
    pub name: String,
    pub mode: Mode,
    /// run 模式：与 `RUN_ROUTES` 顺序平行的四路判定；check 模式恒空。
    pub routes: Vec<RouteVerdict>,
    /// check 模式判定；run 模式恒 `None`。
    pub check: Option<CheckVerdict>,
}

/// 聚合计数格（passed/failed/skipped 三格独立；skipped 不并入任何一侧）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize)]
pub struct Cell {
    pub passed: u32,
    pub failed: u32,
    pub skipped: u32,
}

impl Cell {
    pub fn total(&self) -> u32 {
        self.passed + self.failed + self.skipped
    }

    fn count(outcome: &RouteOutcome) -> Cell {
        let mut c = Cell::default();
        match outcome {
            RouteOutcome::Passed => c.passed += 1,
            RouteOutcome::Failed(_) => c.failed += 1,
            RouteOutcome::Skipped(_) => c.skipped += 1,
        }
        c
    }

    fn add(&mut self, other: Cell) {
        self.passed += other.passed;
        self.failed += other.failed;
        self.skipped += other.skipped;
    }
}

/// 全套件判定总账。cases 保持扫描序 = (category, name) 确定序。
#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct ConformanceReport {
    pub cases: Vec<CaseVerdict>,
}

impl ConformanceReport {
    /// 空报告。
    pub fn new() -> Self {
        ConformanceReport { cases: Vec::new() }
    }

    /// 套件出现的分类清单（字典序，去重）。
    pub fn categories(&self) -> Vec<&str> {
        let mut cats: Vec<&str> = self.cases.iter().map(|c| c.category.as_str()).collect();
        cats.sort_unstable();
        cats.dedup();
        cats
    }

    fn cell_where(&self, route: Option<&str>, category: Option<&str>, mode: Mode) -> Cell {
        let mut cell = Cell::default();
        for c in &self.cases {
            if c.mode != mode {
                continue;
            }
            if let Some(cat) = category {
                if c.category != cat {
                    continue;
                }
            }
            match (route, mode) {
                (Some(r), Mode::Run) => {
                    if let Some(v) = c.routes.iter().find(|v| v.route == r) {
                        cell.add(Cell::count(&v.outcome));
                    }
                }
                (None, Mode::Check) => {
                    if let Some(v) = &c.check {
                        if v.ok {
                            cell.passed += 1;
                        } else {
                            cell.failed += 1;
                        }
                    }
                }
                _ => {}
            }
        }
        cell
    }

    /// run 模式某分类 × 某路由的计数格（check 模式不计入路由格）。
    pub fn cell(&self, category: &str, route: &str) -> Cell {
        self.cell_where(Some(route), Some(category), Mode::Run)
    }

    /// 某路由跨全分类汇总（run 模式）。
    pub fn route_total(&self, route: &str) -> Cell {
        self.cell_where(Some(route), None, Mode::Run)
    }

    /// check 语义某分类的计数格（ok → passed，否则 failed；check 无 skipped）。
    pub fn check_cell(&self, category: &str) -> Cell {
        self.cell_where(None, Some(category), Mode::Check)
    }

    /// check 语义跨全分类汇总。
    pub fn check_total(&self) -> Cell {
        self.cell_where(None, None, Mode::Check)
    }

    /// run 模式总 case 数（全路由格的行数）。
    pub fn run_case_total(&self) -> u32 {
        self.cases.iter().filter(|c| c.mode == Mode::Run).count() as u32
    }

    /// check 模式总 case 数。
    pub fn check_case_total(&self) -> u32 {
        self.cases.iter().filter(|c| c.mode == Mode::Check).count() as u32
    }

    /// 逐 case 明细检索。
    pub fn case(&self, category: &str, name: &str) -> Option<&CaseVerdict> {
        self.cases
            .iter()
            .find(|c| c.category == category && c.name == name)
    }

    /// 矩阵自检：每条路由的 run 格总数 = run case 总数、check 格总数 =
    /// check case 总数（防路由漏挂/重复判定静默漏跑）。
    pub fn is_fully_covered(&self) -> bool {
        let run = self.run_case_total();
        RUN_ROUTES
            .iter()
            .all(|r| self.route_total(r).total() == run)
            && self.check_total().total() == self.check_case_total()
    }
}

impl Default for ConformanceReport {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run_case(category: &str, name: &str, outcomes: [RouteOutcome; 4]) -> CaseVerdict {
        CaseVerdict {
            category: category.into(),
            name: name.into(),
            mode: Mode::Run,
            routes: RUN_ROUTES
                .iter()
                .zip(outcomes)
                .map(|(r, o)| RouteVerdict {
                    route: r,
                    outcome: o,
                })
                .collect(),
            check: None,
        }
    }

    fn check_case(category: &str, name: &str, ok: bool) -> CaseVerdict {
        CaseVerdict {
            category: category.into(),
            name: name.into(),
            mode: Mode::Check,
            routes: Vec::new(),
            check: Some(CheckVerdict {
                ok,
                note: String::new(),
            }),
        }
    }

    /// 聚合：分类×路由格、路由总计、check 列、覆盖自检。
    #[test]
    fn aggregation_matrix() {
        let report = ConformanceReport {
            cases: vec![
                run_case(
                    "types",
                    "a",
                    [
                        RouteOutcome::Passed,
                        RouteOutcome::Passed,
                        RouteOutcome::Skipped("缺 go".into()),
                        RouteOutcome::Passed,
                    ],
                ),
                run_case(
                    "types",
                    "b",
                    [
                        RouteOutcome::Passed,
                        RouteOutcome::Failed("stdout 不一致".into()),
                        RouteOutcome::Skipped("缺 go".into()),
                        RouteOutcome::Passed,
                    ],
                ),
                run_case(
                    "io",
                    "c",
                    [
                        RouteOutcome::Passed,
                        RouteOutcome::Passed,
                        RouteOutcome::Skipped("缺 go".into()),
                        RouteOutcome::Passed,
                    ],
                ),
                check_case("diagnostics", "d", true),
                check_case("diagnostics", "e", false),
                check_case("types", "f", true),
            ],
        };
        // 分类格：types×go = 2 skip；io×go = 1 skip
        assert_eq!(report.cell("types", "go").skipped, 2);
        assert_eq!(report.cell("io", "go").skipped, 1);
        assert_eq!(report.cell("types", "go").total(), 2);
        assert_eq!(report.cell("io", "interp-ast").passed, 1);
        // 路由总计：ir 1 failed；ast 3 passed
        let ir = report.route_total("interp-ir");
        assert_eq!(
            (ir.passed, ir.failed, ir.skipped),
            (2, 1, 0),
            "ir 总计应 2 过 1 败"
        );
        let ast = report.route_total("interp-ast");
        assert_eq!((ast.passed, ast.failed, ast.skipped), (3, 0, 0));
        // check 列：diagnostics 1 过 1 败；总 2 过 1 败
        assert_eq!(
            (
                report.check_cell("diagnostics").passed,
                report.check_cell("diagnostics").failed
            ),
            (1, 1)
        );
        assert_eq!(report.check_cell("types").passed, 1);
        let ct = report.check_total();
        assert_eq!((ct.passed, ct.failed), (2, 1));
        // 覆盖自检：路由格总数 = run case 数、check = 3
        assert!(report.is_fully_covered());
        assert_eq!(report.run_case_total(), 3);
        assert_eq!(report.check_case_total(), 3);
        assert_eq!(report.categories(), vec!["diagnostics", "io", "types"]);
        // 明细检索
        assert!(report.case("types", "b").is_some());
        assert!(report.case("types", "zz").is_none());
    }

    /// 空报告与缺路覆盖自检（路由漏挂 → is_fully_covered false）。
    #[test]
    fn coverage_detects_missing_route() {
        let mut c = run_case(
            "types",
            "a",
            [
                RouteOutcome::Passed,
                RouteOutcome::Passed,
                RouteOutcome::Passed,
                RouteOutcome::Passed,
            ],
        );
        c.routes.pop(); // js 路漏挂
        let report = ConformanceReport { cases: vec![c] };
        assert!(!report.is_fully_covered(), "缺 js 格应检出");
    }
}
