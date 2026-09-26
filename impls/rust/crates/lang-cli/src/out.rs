//! 诊断渲染与 JSON 信封（specs/agent-protocol.md §1）。
//!
//! 信封契约：`schemaVersion` 恒字符串 `"1"`（spec 明文 + Go/TS 证据同文）；
//! `toolVersion` 与 `lang-rs version` 同源。诊断负载一律走
//! lang-contract `DiagnosticDto`（字段序即 schema 序，禁二次加工）。

use lang_contract::{DiagnosticDto, FileResolver};
use lang_core::Diagnostic;
use serde_json::{Value, json};

use crate::VERSION;

/// 统一信封头部。
pub fn envelope(kind: &str) -> Value {
    json!({
        "schemaVersion": "1",
        "toolVersion": VERSION,
        "kind": kind,
    })
}

/// 诊断数组 → DTO JSON 数组。
pub fn diagnostics_json(diags: &[Diagnostic], files: &dyn FileResolver) -> Vec<Value> {
    diags
        .iter()
        .map(|d| {
            // DiagnosticDto 全字段为 String/数字/嵌套同构 DTO（无非字符串键映射、
            // 无浮点），serde_json 序列化总性成立，Err 不可达。
            serde_json::to_value(DiagnosticDto::of(d, files)).expect("DiagnosticDto 序列化总性")
        })
        .collect()
}

/// check --json 的负载（契约 §2：success + diagnostics[]）。
pub fn check_envelope(diags: &[Diagnostic], files: &dyn FileResolver) -> Value {
    let mut v = envelope("check");
    v["success"] = json!(diags.is_empty());
    v["diagnostics"] = json!(diagnostics_json(diags, files));
    v
}

/// 错误信封（契约：error 负载配非零退出码）。
pub fn error_envelope(kind: &str, message: &str) -> Value {
    let mut v = envelope(kind);
    v["error"] = json!(message);
    v
}

/// 诊断的人类可读形（TS formatDiag 同构，文件名前缀多文件模块消歧）：
/// `{file}:第{line}行: [{code}] {message}`；无锚诊断 file 为空、行 0。
pub fn format_diag(diag: &Diagnostic, files: &dyn FileResolver) -> String {
    let line = diag
        .primary_span
        .as_ref()
        .map(|s| s.start.line)
        .unwrap_or(0);
    let file = diag
        .primary_span
        .as_ref()
        .map(|s| files.file_of(s.source))
        .unwrap_or_default();
    let prefix = if file.is_empty() {
        String::new()
    } else {
        format!("{file}:")
    };
    format!(
        "{}第{line}行: [{}] {}",
        prefix,
        diag.code.as_str(),
        diag.message
    )
}

/// 诊断块渲染（多行，TS formatDiags 同构）。
pub fn format_diags(diags: &[Diagnostic], files: &dyn FileResolver) -> String {
    diags
        .iter()
        .map(|d| format_diag(d, files))
        .collect::<Vec<_>>()
        .join("\n")
}

/// 契约 JSON 统一打印（两空格缩进 + 换行，Go printJSON 同构）。
pub fn print_json(v: &Value) {
    println!(
        "{}",
        serde_json::to_string_pretty(v).unwrap_or_else(|_| "<JSON 编码失败>".to_string())
    );
}
