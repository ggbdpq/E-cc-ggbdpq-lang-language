//! lang-verify —— `verify` 动词的全管线验证（specs/runner-contract.md 五动词表）。
//!
//! 九段编排（执行序 = [`Stage::ALL`]，specs 冻结）：
//! parse → resolve → typecheck → hir → ir → effect → compile → execute →
//! differential；失败输出「失败阶段 + 已完成阶段」，后续段以 Skipped 显式
//! 短路呈现；工具链缺失输出 skipped（不静默、不谎报通过）。
//!
//! 分工：
//! - [`pipeline`]：编排 + 短路 + 差分汇总（[`pipeline::run_pipeline`]）；
//! - [`exec`]：执行段注入面 [`exec::ExecDriver`]——AST/IR 解释（A4）、
//!   HIR→IR lowering（A3，尚未接入 → DefaultExec 如实 Skipped）、
//!   →Go/→JS 生成 + 宿主执行（A5）；[`exec::DefaultExec`] 已接 A4/A5，
//!   [`exec::StubExec`] 为编排测试桩；
//! - [`effects`]：Effect 段事实提取（AST + SymbolId → [`lang_contract::CallGraph`]
//!   → 不动点）；
//! - [`input`]：模块目录（runner-contract：输入一律为程序目录）或内存文件集。
//!
//! 冻结入口（首波登记）：
//! - [`pipeline::verify`](crate::pipeline::verify)(dir) -> VerifyReport
//!   （`--json` 由 lang-cli 经 lang-contract 信封序列化：kind = "verify"，
//!   字段只加不改不删）。
#![forbid(unsafe_code)]

pub mod effects;
pub mod exec;
pub mod input;
pub mod pipeline;

pub use exec::{DefaultExec, ExecContext, ExecDriver, ExecError, RunOutput, StubExec};
pub use input::VerifyInput;
pub use pipeline::{run_pipeline, verify, verify_files};

/// 管线阶段（声明序 = 执行序；[`Stage::ALL`] 是唯一执行序事实源）。
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug)]
pub enum Stage {
    Parse,
    Resolve,
    Typecheck,
    Hir,
    Ir,
    Effect,
    Compile,
    Execute,
    Differential,
}

impl Stage {
    pub const ALL: [Stage; 9] = [
        Stage::Parse,
        Stage::Resolve,
        Stage::Typecheck,
        Stage::Hir,
        Stage::Ir,
        Stage::Effect,
        Stage::Compile,
        Stage::Execute,
        Stage::Differential,
    ];

    pub fn as_str(&self) -> &'static str {
        match self {
            Stage::Parse => "parse",
            Stage::Resolve => "resolve",
            Stage::Typecheck => "typecheck",
            Stage::Hir => "hir",
            Stage::Ir => "ir",
            Stage::Effect => "effect",
            Stage::Compile => "compile",
            Stage::Execute => "execute",
            Stage::Differential => "differential",
        }
    }
}

/// 单阶段结果。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StageOutcome {
    Passed,
    Failed(String),
    /// 工具链缺失等环境性跳过（如宿主缺 go/node）——显式呈现，不算失败也不算通过。
    Skipped(String),
}

/// verify 报告（契约形态：失败阶段 + 逐阶段结果；阶段一律按执行序排列）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct VerifyReport {
    /// 每阶段结果，按 [`Stage::ALL`] 执行序；未执行到的阶段不存在于此表。
    pub stages: Vec<(Stage, StageOutcome)>,
}

impl VerifyReport {
    pub fn passed(&self) -> bool {
        self.stages
            .iter()
            .all(|(_, o)| matches!(o, StageOutcome::Passed | StageOutcome::Skipped(_)))
    }

    /// 失败阶段（runner-contract：失败输出失败阶段；多个失败取执行序第一个）。
    pub fn failed_stage(&self) -> Option<Stage> {
        self.stages
            .iter()
            .find(|(_, o)| matches!(o, StageOutcome::Failed(_)))
            .map(|(s, _)| *s)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 阶段名与执行序冻结（契约面：verify --json 会输出）。
    #[test]
    fn stage_order_and_names_frozen() {
        assert_eq!(
            Stage::ALL.iter().map(Stage::as_str).collect::<Vec<_>>(),
            [
                "parse",
                "resolve",
                "typecheck",
                "hir",
                "ir",
                "effect",
                "compile",
                "execute",
                "differential"
            ]
        );
    }

    /// 报告语义：全过为绿；skipped 不算失败；首个失败阶段按执行序。
    #[test]
    fn report_semantics() {
        let mut r = VerifyReport {
            stages: vec![(Stage::Parse, StageOutcome::Passed)],
        };
        assert!(r.passed());
        assert_eq!(r.failed_stage(), None);

        r.stages = vec![
            (Stage::Parse, StageOutcome::Passed),
            (Stage::Resolve, StageOutcome::Failed("未定义 x".into())),
            (Stage::Typecheck, StageOutcome::Failed("类型不匹配".into())),
        ];
        assert!(!r.passed());
        assert_eq!(r.failed_stage(), Some(Stage::Resolve));

        r.stages = vec![
            (Stage::Parse, StageOutcome::Passed),
            (Stage::Compile, StageOutcome::Skipped("宿主缺 go".into())),
        ];
        assert!(r.passed());
    }
}
