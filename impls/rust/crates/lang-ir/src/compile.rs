//! 便利入口：语义分析后的语句序列 → IrProgram（「源码 → IR」全链）。
//!
//! 前置：`analysis` 由**同一棵** `stmts` 分析而来且**无诊断**——用户诊断
//! 已由 lang-semantic 发射，本入口只可能失败于内部不变量破坏
//! （[`CompileError`]），不含用户诊断面。

use lang_hir::{BuildError, HirBuilder};
use lang_semantic::Analysis;
use lang_syntax::Stmt;

use crate::lower::{IrLowerer, LowerError, LowerIr};
use crate::model::IrProgram;

/// 编译失败（全为内部错误形态；语义诊断在 `analyze_stmts` 阶段发射）。
#[derive(Clone, PartialEq, Eq, Debug)]
pub enum CompileError {
    /// HIR 构建失败（语义已通过却出现不可能的 AST 形状）。
    Build(BuildError),
    /// IR lowering 失败或产物未过 `validate`（事实缺口/内部序错）。
    Lower(LowerError),
}

impl std::fmt::Display for CompileError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            CompileError::Build(e) => write!(f, "{e}"),
            CompileError::Lower(e) => write!(f, "{e}"),
        }
    }
}

impl std::error::Error for CompileError {}

/// 语句序列 + 语义产物 → [`IrProgram`]（AST→HIR→IR，产物已过 `validate`）。
///
/// `analysis` 需 `&mut`：HIR 构建经 `lang_semantic::resolve_annotation`
/// 的静默通道回填注解类型（A2 冻结入口）。
pub fn compile(stmts: &[Stmt], analysis: &mut Analysis) -> Result<IrProgram, CompileError> {
    let hir = HirBuilder::new(analysis)
        .lower_slice(stmts)
        .map_err(CompileError::Build)?;
    let mut lowerer = IrLowerer::new(&analysis.types, &analysis.symbols);
    lowerer.lower(&hir).map_err(CompileError::Lower)
}
