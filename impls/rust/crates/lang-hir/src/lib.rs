//! lang-hir —— Typed HIR（语义事实层）。
//!
//! 分层：AST → **HIR** → Core IR。HIR 携带 NodeId/Span/SymbolId/TypeId 四元结论，
//! 后端与 IR 禁止重新推断语义。接口契约见 docs/lang-rs/interface-contract.md。
#![forbid(unsafe_code)]

pub mod build;
pub mod hir;
pub mod lower;

#[cfg(test)]
mod hir_tests;

pub use build::{BuildError, HirBuilder, build_hir};
pub use hir::{
    AssignOp, BinaryOp, HirAssign, HirEnumCase, HirEnumDecl, HirExpr, HirFieldDecl, HirFn, HirLet,
    HirLit, HirMapItem, HirMatchArm, HirParam, HirPat, HirProg, HirStmt, HirStructDecl, LitKind,
    MatchKind, NodeInfo, UnaryOp, type_of,
};
pub use lower::LowerHir;
