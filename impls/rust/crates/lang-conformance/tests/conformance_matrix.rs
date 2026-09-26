//! 全量 conformance 实测（第四波核心产出）：151 case × 4 路总账。
//!
//! run 类（120）：四路（interp-ast/interp-ir/go/js）逐 case 比对，stdout 逐字节、
//! exitCode 与 stderr 全比（冻结的 `judge_run`）；go/js 路工具链缺失时整路
//! Skipped 并显式点名，不计失败也不谎报通过。check 类（31）：`lang_semantic::
//! analyze_stmts` 直接产诊断，`code|ruleId|severity|line` 多重集比对（冻结的
//! `judge_check`）。
//!
//! 真值在 conformance/，禁改 case.yaml。失败处置（任务书口径）：根因在本
//! crate 的已修（module 装载 import 滤除）；根因在 lang-ir/lang-backend 的
//! 经最小复现定性后登记 [`REGISTERED_BLOCKERS`] 台账（A6-verify.md「阻塞」区
//! 同源），主测试断言「失败 ⊆ 台账」作回归守卫；[`strict_all_routes_green`]
//! （`#[ignore]`）是阻塞清零后的 L6 收官门禁——修复落地后从台账移除条目并
//! 解禁 strict。矩阵打印到 stdout 供 L6 验收留档。

use lang_conformance::{
    ConformanceReport, ROUTE_GO, ROUTE_INTERP_AST, ROUTE_INTERP_IR, ROUTE_JS, RUN_ROUTES,
    RouteOutcome, default_suite_root, run_suite,
};

/// 套件规模（conformance/README 真值；扩容时同步）。
const TOTAL_CASES: usize = 151;

/// run 类规模（151 - check 31）。
const RUN_CASES: u32 = 120;

/// 已登记阻塞台账：(category, case)。根因归属见 A6-verify.md「阻塞」区。
///
/// **集成轮清零（2026-09-25）**：B1（lang-ir 赋值降形压栈序 + 地址临时槽
/// 拷贝）/ B2（双后端 ArrayPush 拼接）/ B3（Go RangeNew int64 cast）已全部
/// 修复，台账清空、格数守卫归零、strict 门禁解禁。历史条目（B1 11 case
/// 33 格 / B2 eval-compound-string-concat go+js / B3 range-literal go）见
/// A6-verify.md「阻塞」区与下方注释留存。
///
/// （历史 B0——has_ret=false 的 CallBuiltin push 语句发射 `(no value) used
/// as value`——已由 A5 在第四波窗口内修复（langGoPush 改返回 any）。
const REGISTERED_BLOCKERS: &[(&str, &str)] = &[];

#[test]
fn full_suite_151x4_matrix() {
    let report: ConformanceReport = run_suite(&default_suite_root())
        .unwrap_or_else(|f| panic!("套件扫描应零失败，实际 {} 条：{f:?}", f.len()));

    assert_eq!(report.cases.len(), TOTAL_CASES, "套件规模");
    assert!(
        report.is_fully_covered(),
        "每路由格数 = run case 数、check 列 = check case 数（防漏挂静默漏跑）"
    );
    print_matrix(&report);

    // —— 全绿硬断言（本 crate 适配层 + 已交付基线）——
    let ast = report.route_total(ROUTE_INTERP_AST);
    assert_eq!(
        (ast.passed, ast.failed, ast.skipped),
        (RUN_CASES, 0, 0),
        "AST 路由应为 120/120 全过（A4 已知基线）"
    );
    let chk = report.check_total();
    assert_eq!(
        (chk.passed, chk.failed),
        (31, 0),
        "check 类 31 例诊断多重集应全过（lang-semantic 直产诊断）"
    );
    // go/js：工具链可用（本机实测可用）→ 不接受 Skipped；失败必须在台账内。
    // 工具链缺失的宿主 → 整路 Skipped 且原因点名工具（诚实呈现，不谎报）。
    for (route, tool) in [(ROUTE_GO, "go"), (ROUTE_JS, "node")] {
        let c = report.route_total(route);
        if c.passed == 0 && c.skipped == c.total() {
            for cv in &report.cases {
                if let Some(v) = cv.routes.iter().find(|v| v.route == route) {
                    if let RouteOutcome::Skipped(m) = &v.outcome {
                        assert!(
                            m.contains(tool),
                            "{route}/{}/{} skip 原因应点名 {tool}: {m}",
                            cv.category,
                            cv.name
                        );
                    }
                }
            }
            eprintln!(
                "SKIP：宿主缺 {tool}，{route} 路整路跳过（{} 例）",
                c.skipped
            );
        }
    }

    // —— 回归守卫：run 路由的每一格失败都必须在登记台账内 ——
    let mut unexpected: Vec<String> = Vec::new();
    let mut blocked_cells = 0u32;
    for c in &report.cases {
        for v in &c.routes {
            if let RouteOutcome::Failed(m) = &v.outcome {
                if REGISTERED_BLOCKERS
                    .iter()
                    .any(|(cc, nn)| *cc == c.category && *nn == c.name)
                {
                    blocked_cells += 1;
                } else {
                    unexpected.push(format!("[{}] {}/{}: {}", v.route, c.category, c.name, m));
                }
            }
        }
    }
    assert!(
        unexpected.is_empty(),
        "出现台账外的 run 失败 {} 例（回归或新问题；定性后更新 REGISTERED_BLOCKERS 与 A6 台账）：\n{}",
        unexpected.len(),
        unexpected.join("\n")
    );
    // 台账格数守卫（集成轮清零 2026-09-25）：0 = 阻塞全部清零（历史 37→36
    // →0，两次收缩均由本守卫捕获后同步）；任何 run 失败都会先在上面的
    // 「失败 ⊆ 台账」守卫红。
    assert_eq!(
        blocked_cells, 0,
        "阻塞应全部清零：新失败须定性后登记台账或修实现"
    );
}

/// L6 收官门禁（集成轮解禁 2026-09-25，B1–B3 清零）：四路 + check 全绿。
#[test]
fn strict_all_routes_green() {
    let report = run_suite(&default_suite_root())
        .unwrap_or_else(|f| panic!("套件扫描应零失败，实际 {} 条：{f:?}", f.len()));
    let mut red: Vec<String> = Vec::new();
    for r in RUN_ROUTES {
        let c = report.route_total(r);
        if c.failed > 0 || c.passed != RUN_CASES {
            red.push(format!(
                "{r}: {}p/{}f/{}s（期望 120p/0f/0s）",
                c.passed, c.failed, c.skipped
            ));
        }
    }
    let chk = report.check_total();
    if (chk.passed, chk.failed) != (31, 0) {
        red.push(format!(
            "check: {}p/{}f（期望 31p/0f）",
            chk.passed, chk.failed
        ));
    }
    assert!(red.is_empty(), "strict 门禁未过：{}", red.join("; "));
}

/// 打印 151×4 总账矩阵（每分类 × 每路由 通过/失败/skip + 逐格明细留档）。
fn print_matrix(report: &ConformanceReport) {
    println!("\n===== conformance 151×4 总账矩阵 =====");
    println!(
        "{:<14} {:>13} {:>13} {:>13} {:>13} {:>9} {:>9}",
        "category", "interp-ast", "interp-ir", "go", "js", "check", "run×check"
    );
    for cat in report.categories() {
        let run_n = RUN_ROUTES
            .iter()
            .map(|r| report.cell(cat, r).total())
            .max()
            .unwrap_or(0);
        let fmt = |r: &str| {
            let c = report.cell(cat, r);
            format!("{}p/{}f/{}s", c.passed, c.failed, c.skipped)
        };
        let chk = report.check_cell(cat);
        println!(
            "{:<14} {:>13} {:>13} {:>13} {:>13} {:>9} {:>9}",
            cat,
            fmt(ROUTE_INTERP_AST),
            fmt(ROUTE_INTERP_IR),
            fmt(ROUTE_GO),
            fmt(ROUTE_JS),
            format!("{}p/{}f", chk.passed, chk.failed),
            format!("{run_n}×{}", chk.total()),
        );
    }
    println!("{:-<86}", "");
    for r in RUN_ROUTES {
        let c = report.route_total(r);
        println!(
            "{:<14} {:>3} 通过 / {:>3} 失败 / {:>3} skip（run {} 例）",
            r,
            c.passed,
            c.failed,
            c.skipped,
            c.total()
        );
    }
    let chk = report.check_total();
    println!(
        "{:<14} {:>3} 通过 / {:>3} 失败（check {} 例）",
        "check",
        chk.passed,
        chk.failed,
        chk.total()
    );

    println!("----- 明细（Skipped/Failed 逐格）-----");
    for c in &report.cases {
        for v in &c.routes {
            match &v.outcome {
                RouteOutcome::Skipped(m) => {
                    println!("SKIP [{}] {}/{}: {}", v.route, c.category, c.name, m)
                }
                RouteOutcome::Failed(m) => {
                    println!("FAIL [{}] {}/{}: {}", v.route, c.category, c.name, m)
                }
                RouteOutcome::Passed => {}
            }
        }
        if let Some(ck) = &c.check {
            if !ck.ok {
                println!("FAIL [check] {}/{}: {}", c.category, c.name, ck.note);
            }
        }
    }
}
