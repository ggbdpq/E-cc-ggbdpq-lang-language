//! lang-edit —— 安全编辑事务（specs/agent-protocol.md §6）。
//!
//! 【冻结度量】`TextEdit` 的 start/end offset 一律 **Unicode 码点**（0 基），与
//! SourceSpan 同度量（specs/source-span.md）；UTF-8 字节偏移只允许存在于 parser
//! 内部，任何进入编辑事务的偏移必须已经过 `SourceText::byte_to_cp` 归一。
//! `file` 字段为文件相对路径（与契约 Span.file 同口径）。
//!
//! 事务管线（specs §6）：inspect → plan → precondition check → edit → format →
//! parse → resolve → typecheck。本 crate 负责其中 precondition → edit 两步：
//! - [`hash`]：sha256 hex（precondition.file_hash 的唯一度量）；
//! - [`apply_in_memory`] / [`apply`]：重叠拒绝、越界拒绝、缺前置拒绝、
//!   哈希不匹配拒绝；全部校验通过才产生新内容（全成或全不成）。
//!
//! dry-run = 重构计划产物 [`WorkspaceEdit`] 本身（不落盘）；落盘是显式动作。
//!
//! WorkspaceEdit 多文件事务：每个涉及文件带独立 precondition；有编辑的文件
//! 缺 precondition 即整体拒绝；未涉及编辑的 precondition 项被忽略（与 TS 证据一致）。
#![forbid(unsafe_code)]

pub mod apply;
pub mod hash;

pub use apply::{ApplyError, apply, apply_in_memory};
pub use hash::{sha256, sha256_hex};

/// 单点编辑：把 `[start_offset, end_offset)` 区间的文本替换为 `replacement`。
/// offset 为 0 基码点（见模块头冻结决定）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TextEdit {
    pub file: String,
    pub start_offset: u32,
    pub end_offset: u32,
    pub replacement: String,
}

/// 编辑前置条件：文件当前内容的 sha256 hex。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Precondition {
    pub file: String,
    pub file_hash: String,
}

/// 跨文件编辑事务（specs/agent-protocol.md §6：每个涉及文件带独立 precondition）。
#[derive(Clone, Debug, PartialEq, Eq, Default)]
pub struct WorkspaceEdit {
    pub edits: Vec<TextEdit>,
    pub preconditions: Vec<Precondition>,
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 冻结数据形状：字段与默认值（契约面，序列化由 CLI 波次落）。
    #[test]
    fn shapes_default_and_consistent() {
        let ws = WorkspaceEdit::default();
        assert!(ws.edits.is_empty());
        assert!(ws.preconditions.is_empty());
        let e = TextEdit {
            file: "a.lang-go".into(),
            start_offset: 0,
            end_offset: 1,
            replacement: "x".into(),
        };
        assert_eq!(e.clone(), e);
    }
}
