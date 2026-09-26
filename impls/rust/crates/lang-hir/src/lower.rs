//! AST → HIR lowering 入口（本波只冻结签名，实现等 A1 语法面 + A2 语义核就绪）。
//!
//! 冻结约定：
//! - 入口是 [`LowerHir`] trait；`Ast` 关联类型由实现方绑定到 lang-syntax 的程序节点，
//!   避免本 crate 反向锁死 A1 的具体形状。
//! - NodeId 分配为**先序递增**（与 Go/TS 参考实现一致），同一输入两次 lowering
//!   必须产出逐字段相等的 [`HirProg`]（确定性契约）。
//! - Span 在此层盖章（列级）；符号与类型结论来自 resolver/checker，lowering 只搬运。

use crate::hir::HirProg;

/// AST 程序 → Typed HIR。
pub trait LowerHir {
    /// A1 语法面的程序节点类型（实现方绑定，如 `lang_syntax::ast::Program`）。
    type Ast;
    /// lowering 失败形态（内部不变量破坏或 A1/A2 结论缺口）。
    type Error;

    /// 下降整个程序。要求确定性：同输入同输出（逐字段相等）。
    fn lower(&mut self, ast: &Self::Ast) -> Result<HirProg, Self::Error>;
}
