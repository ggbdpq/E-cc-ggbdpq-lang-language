//! lang-contract —— Agent Contract 契约层：编译器内部模型 → 稳定 JSON 协议的唯一转换边界。
//!
//! 契约事实源：specs/agent-protocol.md（schemaVersion "1"）、specs/diagnostics.md §2、
//! schemas/agent-protocol.schema.json。规则：字段只加不改不删；输出确定性（显式排序，
//! 禁 HashMap 迭代序外泄）；DTO 与内部 struct 分离。
//!
//! - [`dto`]：Diagnostic → JSON DTO（serde，golden 测试对照 specs/diagnostics.md §2 手抄）
//! - [`effects`]：效果元数据 fixpoint 引擎（EFFECT-001，泛型调用图输入，本波不接语义核）
#![forbid(unsafe_code)]

pub mod dto;
pub mod effects;

pub use dto::{DiagnosticDto, FileResolver, PositionDto, SourceNames, SpanDto};
pub use effects::{CallGraph, Effect, EffectEntry, EffectsError, builtin_direct_effect};
