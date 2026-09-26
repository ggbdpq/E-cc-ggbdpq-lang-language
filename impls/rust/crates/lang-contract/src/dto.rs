//! Diagnostic → JSON DTO（specs/agent-protocol.md schemaVersion "1" + specs/diagnostics.md §2）。
//!
//! DTO 与 lang-core 的内部 struct 分离：内部结构是编译器所有物，本模块的 serde 形态
//! 才是对外契约（字段只加不改不删）。字段顺序 = schema 结构体序 =
//! `code, ruleId, severity, message, primarySpan, relatedSpans, help`，
//! 位置序 = `offset, line, column`，区间序 = `file, start, end`。
//!
//! v0.4 行级定位：primarySpan 的 line 正确、column 恒 1、offset 恒 0
//! （形态由 lang-core `Diagnostic::at_line` 保证，本层逐字映射，不做二次加工）。

use std::collections::BTreeMap;

use lang_core::{Diagnostic, SourceId, Span};
use serde::{Deserialize, Serialize};

/// SourceId → 契约 `file` 字段（loader 侧提供的只读反查）。
pub trait FileResolver {
    /// 未登记的 SourceId 返回空串（与 TS 证据一致：未盖章 span → file ""）。
    fn file_of(&self, source: SourceId) -> String;
}

/// 简单反查表：由 (SourceId, 文件相对路径) 对构建；BTreeMap 保证确定性。
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct SourceNames {
    names: BTreeMap<u32, String>,
}

impl SourceNames {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn insert(&mut self, source: SourceId, file: impl Into<String>) {
        self.names.insert(source.0, file.into());
    }

    pub fn from_pairs<I, S>(pairs: I) -> Self
    where
        I: IntoIterator<Item = (SourceId, S)>,
        S: Into<String>,
    {
        let mut map = Self::new();
        for (id, file) in pairs {
            map.insert(id, file);
        }
        map
    }
}

impl FileResolver for SourceNames {
    fn file_of(&self, source: SourceId) -> String {
        self.names.get(&source.0).cloned().unwrap_or_default()
    }
}

/// 契约单点（0 基码点 offset；1 基 line/column）。
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct PositionDto {
    pub offset: u32,
    pub line: u32,
    pub column: u32,
}

impl PositionDto {
    pub fn of(pos: &lang_core::Position) -> Self {
        Self {
            offset: pos.offset,
            line: pos.line,
            column: pos.column,
        }
    }
}

/// 契约区间。
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct SpanDto {
    pub file: String,
    pub start: PositionDto,
    pub end: PositionDto,
}

impl SpanDto {
    pub fn of(span: &Span, files: &dyn FileResolver) -> Self {
        Self {
            file: files.file_of(span.source),
            start: PositionDto::of(&span.start),
            end: PositionDto::of(&span.end),
        }
    }
}

/// 诊断 JSON 契约（schemas/agent-protocol.schema.json `definitions.diagnostic`）。
///
/// 全字段必出：`primarySpan` 缺席时序列化为 `null`（schema required 集合
/// 不含 ruleId，但 v0.9 起 ruleId 由 code 推导恒存在，照常输出）。
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticDto {
    pub code: String,
    pub rule_id: String,
    pub severity: String,
    pub message: String,
    pub primary_span: Option<SpanDto>,
    pub related_spans: Vec<SpanDto>,
    pub help: String,
}

impl DiagnosticDto {
    /// 内部诊断 → 契约 DTO。severity/ruleId 均由 code 侧事实源推导，不可改写。
    pub fn of(diag: &Diagnostic, files: &dyn FileResolver) -> Self {
        Self {
            code: diag.code.as_str().to_string(),
            rule_id: diag.rule_id.as_str().to_string(),
            severity: diag.severity.as_str().to_string(),
            message: diag.message.clone(),
            primary_span: diag.primary_span.as_ref().map(|s| SpanDto::of(s, files)),
            related_spans: diag
                .related_spans
                .iter()
                .map(|s| SpanDto::of(s, files))
                .collect(),
            help: diag.help.clone(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use lang_core::{DiagnosticCode, Position};

    /// specs/diagnostics.md §2 的样例逐字段手抄（含 file ""），期望来自 spec 而非实现。
    #[test]
    fn golden_spec_example_empty_file() {
        let diag = Diagnostic::new(
            DiagnosticCode::E2002,
            "未定义 nope",
            Some(Diagnostic::at_line(SourceId(0), 2)),
        );
        let files = SourceNames::from_pairs([(SourceId(0), "")]);
        let dto = DiagnosticDto::of(&diag, &files);

        let actual = serde_json::to_string_pretty(&dto).unwrap();
        let expected = r#"{
  "code": "E2002",
  "ruleId": "NAME-UNDEF-004",
  "severity": "error",
  "message": "未定义 nope",
  "primarySpan": {
    "file": "",
    "start": {
      "offset": 0,
      "line": 2,
      "column": 1
    },
    "end": {
      "offset": 0,
      "line": 2,
      "column": 1
    }
  },
  "relatedSpans": [],
  "help": ""
}"#;
        assert_eq!(actual, expected);
    }

    /// agent-protocol §3 的 SourceSpan 形态（offset 0→12、column 1→13）+ related/help 非空。
    #[test]
    fn golden_rich_diagnostic_with_related() {
        let mut diag = Diagnostic::new(
            DiagnosticCode::E3001,
            "类型不匹配",
            Some(Span::new(
                SourceId(1),
                Position::new(0, 1, 1),
                Position::new(12, 1, 13),
            )),
        );
        diag.related_spans = vec![Span::new(
            SourceId(1),
            Position::new(20, 3, 5),
            Position::new(26, 3, 11),
        )];
        diag.help = "期望 int".into();
        let files = SourceNames::from_pairs([(SourceId(1), "main.lang-go")]);
        let dto = DiagnosticDto::of(&diag, &files);

        let actual = serde_json::to_string_pretty(&dto).unwrap();
        let expected = r#"{
  "code": "E3001",
  "ruleId": "TYPE-ASSIGN-001",
  "severity": "error",
  "message": "类型不匹配",
  "primarySpan": {
    "file": "main.lang-go",
    "start": {
      "offset": 0,
      "line": 1,
      "column": 1
    },
    "end": {
      "offset": 12,
      "line": 1,
      "column": 13
    }
  },
  "relatedSpans": [
    {
      "file": "main.lang-go",
      "start": {
        "offset": 20,
        "line": 3,
        "column": 5
      },
      "end": {
        "offset": 26,
        "line": 3,
        "column": 11
      }
    }
  ],
  "help": "期望 int"
}"#;
        assert_eq!(actual, expected);
    }

    /// primarySpan 缺席 → 字段仍必出、值为 null（schema required 集合）。
    #[test]
    fn missing_primary_span_serializes_null() {
        let diag = Diagnostic::new(DiagnosticCode::E1001, "解析错误", None);
        let dto = DiagnosticDto::of(&diag, &SourceNames::new());
        let actual = serde_json::to_string(&dto).unwrap();
        assert_eq!(
            actual,
            r#"{"code":"E1001","ruleId":"LEX-ALL","severity":"error","message":"解析错误","primarySpan":null,"relatedSpans":[],"help":""}"#
        );
    }

    /// schema 形态校验：required 七字段齐、severity 枚举、code/ruleId 模式（手工核，不引 regex）。
    #[test]
    fn emitted_json_matches_schema_shape() {
        let diag = Diagnostic::new(
            DiagnosticCode::E4001,
            "match 不穷尽",
            Some(Diagnostic::at_line(SourceId(0), 6)),
        );
        let dto = DiagnosticDto::of(&diag, &SourceNames::new());
        let v: serde_json::Value = serde_json::to_value(&dto).unwrap();

        for key in [
            "code",
            "ruleId",
            "severity",
            "message",
            "primarySpan",
            "relatedSpans",
            "help",
        ] {
            assert!(v.get(key).is_some(), "schema required 缺字段 {key}");
        }
        assert_eq!(v["severity"], "error");
        let code = v["code"].as_str().unwrap();
        assert_eq!(code.len(), 5);
        assert!(code.starts_with('E') && code[1..].chars().all(|c| c.is_ascii_digit()));
        let rule = v["ruleId"].as_str().unwrap();
        assert!(
            rule.chars()
                .all(|c| c.is_ascii_uppercase() || c.is_ascii_digit() || c == '-')
        );
        // 行级形态：column 恒 1、offset 恒 0
        assert_eq!(v["primarySpan"]["start"]["column"], 1);
        assert_eq!(v["primarySpan"]["start"]["offset"], 0);
    }

    /// 契约可读：协议消费方按同一 schema 反序列化必须成功且保形。
    #[test]
    fn dto_roundtrips_through_schema_json() {
        let diag = Diagnostic::new(
            DiagnosticCode::E2010,
            "嵌套类型声明",
            Some(Diagnostic::at_line(SourceId(2), 4)),
        );
        let files = SourceNames::from_pairs([(SourceId(2), "lib.lang-go")]);
        let dto = DiagnosticDto::of(&diag, &files);
        let text = serde_json::to_string(&dto).unwrap();
        let back: DiagnosticDto = serde_json::from_str(&text).unwrap();
        assert_eq!(back, dto);
    }

    /// 未登记 SourceId → file ""（与 TS 证据的未盖章行为一致）。
    #[test]
    fn unknown_source_resolves_empty_file() {
        let files = SourceNames::new();
        assert_eq!(files.file_of(SourceId(9)), "");
        let span = Span::point(SourceId(9), Position::new(0, 1, 1));
        let dto = SpanDto::of(&span, &files);
        assert_eq!(dto.file, "");
    }
}
