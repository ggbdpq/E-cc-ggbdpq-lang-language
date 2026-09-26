//! 编辑闭环（specs §6/§7）：precondition → apply → format → parse →
//! resolve → typecheck，诊断零回归断言。
//!
//! 与 lang-cli 的分工：本模块提供**内存版**闭环，返回编辑 + 格式化后的
//! 全部文件内容（`file → 新文本`）；落盘由 CLI/Host 显式执行
//! （或直接走 `lang_edit::apply` 的文件系统事务）。
//! 任一环节失败报 `stage: apply | verify`，绝不半应用。

use std::collections::BTreeMap;

use lang_core::{DiagnosticCode, SourceId, SourceText};
use lang_edit::{ApplyError, apply_in_memory};
use lang_syntax::parse;

use crate::input::RefactorInput;

/// 闭环失败（specs §6：`stage: apply|verify`）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RefactorError {
    /// 前置校验/区间/哈希失败（零写入语义由 lang-edit 保证）。
    Apply(ApplyError),
    /// 应用后 verify 阶段失败（格式化/解析/语义诊断回归）。
    Verify(String),
}

impl std::fmt::Display for RefactorError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RefactorError::Apply(e) => write!(f, "stage apply: {e}"),
            RefactorError::Verify(m) => write!(f, "stage verify: {m}"),
        }
    }
}

impl std::error::Error for RefactorError {}

impl From<ApplyError> for RefactorError {
    fn from(e: ApplyError) -> Self {
        RefactorError::Apply(e)
    }
}

/// 应用事务并对结果做闭环验证，成功返回全部文件的新内容
/// （被编辑文件 = 应用 + 格式化后的文本；未涉及文件 = 原文）。
///
/// 零回归断言：重新 parse + re-analyze 后的诊断码多重集必须与基线
/// 完全一致（基线为空则结果必须零诊断）。
pub fn apply_and_verify(
    ws: &lang_edit::WorkspaceEdit,
    baseline: &RefactorInput,
) -> Result<BTreeMap<String, String>, RefactorError> {
    // 1) apply（内存事务：校验 + 拼接，失败即整体失败）
    let edited = apply_in_memory(ws, &baseline.current())?;

    // 2) format（被编辑文件规范化；解析失败 = verify 失败）
    let mut contents = baseline.current();
    for (file, text) in &edited {
        let formatted = lang_formatter::format_source(text)
            .map_err(|d| RefactorError::Verify(format!("格式化 {file} 失败: {}", d.message)))?;
        contents.insert(file.clone(), formatted);
    }

    // 3) reparse + re-analyze（诊断零回归断言）
    let files: Vec<(String, String)> = contents
        .iter()
        .map(|(f, t)| (f.clone(), t.clone()))
        .collect();
    let got = rebuild(&files)?;
    let base = baseline.baseline_codes();
    if got != base {
        let codes: Vec<String> = got.iter().map(|c| c.as_str().to_string()).collect();
        let base_str: Vec<String> = base.iter().map(|c| c.as_str().to_string()).collect();
        return Err(RefactorError::Verify(format!(
            "诊断零回归断言失败：基线 {base_str:?}，编辑后 {codes:?}"
        )));
    }
    Ok(contents)
}

/// 重建语义视图：逐文件 parse → 合并 → analyze_stmts。
/// 返回诊断码多重集（按码字符串升序）。
fn rebuild(files: &[(String, String)]) -> Result<Vec<DiagnosticCode>, RefactorError> {
    let mut merged = Vec::new();
    for (i, (path, text)) in files.iter().enumerate() {
        let st = SourceText::new(path.clone(), text.clone());
        match parse(&st, SourceId(i as u32)) {
            Ok(stmts) => merged.extend(stmts),
            Err(d) => {
                return Err(RefactorError::Verify(format!(
                    "重新解析 {path} 失败（第 {} 行）: {}",
                    d.primary_span.map(|s| s.start.line).unwrap_or(0),
                    d.message
                )));
            }
        }
    }
    let analysis = lang_semantic::analyze_stmts(&merged, SourceId(0));
    let mut codes: Vec<DiagnosticCode> = analysis.diags.iter().map(|d| d.code).collect();
    codes.sort_by_key(|c| c.as_str());
    Ok(codes)
}
