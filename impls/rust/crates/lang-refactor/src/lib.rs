//! lang-refactor —— 语义重构（specs/agent-protocol.md §6/§7）。
//!
//! 三个入口共享同一纪律：**语义事实只来自 SymbolId + 引用集**
//! （[`lang_semantic::Analysis`] 的 refs/pat_refs/decl_syms 查询视图），
//! 禁止文本搜索改名；默认 dry-run 只产出 [`lang_edit::WorkspaceEdit`]，
//! 落盘走 [`apply::apply_and_verify`] 闭环
//! （precondition → apply → format → parse → resolve → typecheck，
//! 诊断零回归断言，失败报 `stage: apply|verify`）。
//!
//! 入口（本波实现，替代首批骨架的占位签名——骨架签名不含 span 事实，
//! 无法产出编辑；入口名与拒绝语义保持登记面）：
//! - [`rename::rename`]：fn/method/变量/参数；struct/enum/构造器显式拒绝（P1）；
//!   同名新名即保守冲突拒绝；方法调用点经检查器接收者类型解析到
//!   `struct.methods` 表（SymbolId 事实），接收者类型未知即拒绝（不冒险）。
//! - [`imports::organize_imports`]：单文件 import 块排序 + 去重，注释零丢失；
//!   区间内夹代码即保守拒绝。
//! - [`remove::safe_remove`]：仅顶层 fn/全局变量、引用计数 0 才给删除编辑；
//!   有引用返回 `NotRemovable { reason, ref_sites }` 证据。
//!
//! [`graph_fill` 文档见 lang_graph::fill]：Analysis → Graph 的语义图填充
//! 在 lang-graph crate（本 crate 的 callers/impact 查询消费方按需落边）。
#![forbid(unsafe_code)]

pub mod apply;
pub mod imports;
pub mod input;
pub mod locate;
pub mod remove;
pub mod rename;
pub mod walk;

pub use apply::{RefactorError, apply_and_verify};
pub use imports::{OrganizeRefusal, organize_imports};
pub use input::{ModuleSources, RefactorInput, SourceFile};
pub use remove::{NotRemovable, safe_remove};
pub use rename::{RenameRefusal, rename, validate_new_name};
