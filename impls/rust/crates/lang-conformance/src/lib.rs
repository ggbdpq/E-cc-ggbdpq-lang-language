//! lang-conformance —— conformance 套件的 Rust 消费层（specs/runner-contract.md +
//! conformance/README.md）。套件目录 `conformance/` 是跨实现共同真值，独立于实现目录。
//!
//! - [`manifest`]：case.yaml 受限子集解析（serde_yaml + 语义校验，拒绝模糊输入）
//! - [`scan`]：套件目录扫描器（缺 case.yaml 即套件错误；输出 (category, name) 确定序）
//! - [`compare`]：run 四路逐字节比对 / check 诊断多重集比对（执行器注入面）
//! - [`routes`]：四路执行器适配层（AST/IR 解释器 + →Go/→JS 生成+宿主执行；
//!   工具链探测，缺失如实 Skipped）
//! - [`report`]：ConformanceReport 总账（分类×路由矩阵 + 逐 case 明细；CLI 与
//!   parity-report 消费面）
//! - [`runner`]：套件 runner（scan → 四路 run + check 诊断 → 总账）
#![forbid(unsafe_code)]

pub mod compare;
pub mod manifest;
pub mod report;
pub mod routes;
pub mod runner;
pub mod scan;

pub use compare::{
    CaseResult, CheckDiff, DiagActual, ROUTE_GO, ROUTE_INTERP_AST, ROUTE_INTERP_IR, ROUTE_JS,
    RUN_ROUTES, RouteError, RouteOutput, RunDiff, RunRoute, compare_check, compare_run,
    judge_check, judge_run,
};
pub use manifest::{Case, DiagExpect, Expect, ManifestError, Mode, parse_manifest};
pub use report::{CaseVerdict, Cell, CheckVerdict, ConformanceReport, RouteOutcome, RouteVerdict};
pub use routes::{
    GoRoute, InterpAstRoute, InterpIrRoute, JsRoute, analyze_case, detect_go, detect_node,
    load_case, needs_host_or_ffi,
};
pub use runner::{default_suite_root, run_cases, run_suite, run_with_routes};
pub use scan::{ScanFailure, ScannedCase, scan_suite};
