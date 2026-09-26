//! lang-core —— 全 workspace 的地基：稳定身份、SourceSpan 码点契约、诊断契约。
//!
//! 接口契约见 `docs/lang-rs/interface-contract.md`；本 crate 仅协调人可改。
#![forbid(unsafe_code)]

pub mod diagnostic;
pub mod ids;
pub mod source;
pub mod span;

pub use diagnostic::{Diagnostic, DiagnosticCode, RuleId, Severity};
pub use ids::{FunctionId, LocalId, NodeId, SourceId, SymbolId, TypeId};
pub use source::SourceText;
pub use span::{Position, Span};
