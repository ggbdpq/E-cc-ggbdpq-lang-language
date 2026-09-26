//! 源码定位（specs/source-span.md，v0.4 冻结结构）。
//!
//! 对外契约一律 **0 基 Unicode 码点 offset、1 基 line/column、半开区间 [start, end)**。
//! Rust 字节偏移只能存在于 parser 内部，出 `lang-syntax` 前必须经 `SourceText` 映射。

use crate::ids::SourceId;

/// 单点定位。`offset` 为 0 基码点偏移；`line`/`column` 为 1 基码点计数。
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug)]
pub struct Position {
    pub offset: u32,
    pub line: u32,
    pub column: u32,
}

impl Position {
    pub fn new(offset: u32, line: u32, column: u32) -> Self {
        Self {
            offset,
            line,
            column,
        }
    }
}

/// 半开区间 Span。空锚点允许 `start == end`（诊断行级锚点即此形态）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub struct Span {
    pub source: SourceId,
    pub start: Position,
    pub end: Position,
}

impl Span {
    /// 由起止点构造。debug 下校验区间方向，防上游把字节序当码点序。
    pub fn new(source: SourceId, start: Position, end: Position) -> Self {
        debug_assert!(
            (start.line, start.column, start.offset) <= (end.line, end.column, end.offset),
            "Span 起点必须不晚于终点"
        );
        Self { source, start, end }
    }

    /// 单点锚（start == end）。
    pub fn point(source: SourceId, pos: Position) -> Self {
        Self {
            source,
            start: pos,
            end: pos,
        }
    }

    pub fn is_empty(&self) -> bool {
        self.start == self.end
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn point_span_allows_empty() {
        let s = Span::point(SourceId(0), Position::new(0, 1, 1));
        assert!(s.is_empty());
    }

    #[test]
    fn span_keeps_fields() {
        let s = Span::new(
            SourceId(2),
            Position::new(0, 1, 1),
            Position::new(12, 1, 13),
        );
        assert_eq!(s.start.line, 1);
        assert_eq!(s.end.offset, 12);
        assert!(!s.is_empty());
    }
}
