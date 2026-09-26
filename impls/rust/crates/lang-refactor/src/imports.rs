//! organize-imports：单文件 import 块排序 + 去重，注释零丢失（specs §7）。
//!
//! 结构事实来自 AST（`Stmt::Import` 的 path 与 span），非文本搜索；注释经
//! 无损 trivia 层保留。行为口径（与 TS 证据的差异已登记）：
//! - 去重保首现；排序用码点序（Rust str Ord = UTF-8 字节序 = 码点序）；
//! - 行尾注释随行走；区间内的独立注释行归拢到块首（原相对序），零丢失；
//! - 去重被删行携带的行尾注释随行删除（与 TS 证据同口径）；
//! - 已排序且无重复 → 零编辑（不动文件）；
//! - import 区间内夹非 import 代码 → 保守拒绝（不猜测语义）。

use lang_core::{SourceId, SourceText};
use lang_edit::{Precondition, TextEdit, WorkspaceEdit};
use lang_syntax::{TriviaKind, parse, scan_trivia};

/// organize 拒绝。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum OrganizeRefusal {
    /// 源码解析失败。
    Parse(String),
    /// import 区间内夹有非 import 代码，语义不安全。
    Scattered(String),
}

impl std::fmt::Display for OrganizeRefusal {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            OrganizeRefusal::Parse(m) | OrganizeRefusal::Scattered(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for OrganizeRefusal {}

/// 单条 import 的编排事实：路径 + 行尾注释（如有）。
struct ImportLine {
    path: String,
    comment: Option<String>,
}

/// 计划一次 organize-imports（dry-run）。`file` 为相对路径（precondition 用）。
///
/// 无 import 或至多一条 → 零编辑；否则返回整个 import 区间的单点替换编辑。
pub fn organize_imports(file: &str, source: &str) -> Result<WorkspaceEdit, OrganizeRefusal> {
    let text = SourceText::new(file, source.to_string());
    let stmts = parse(&text, SourceId(0)).map_err(|d| {
        OrganizeRefusal::Parse(format!(
            "第 {} 行: {}",
            d.primary_span.map(|s| s.start.line).unwrap_or(0),
            d.message
        ))
    })?;
    let imports: Vec<(String, lang_core::Span)> = stmts
        .iter()
        .filter_map(|s| match s {
            lang_syntax::Stmt::Import { path, span } => Some((path.clone(), *span)),
            _ => None,
        })
        .collect();
    plan(file, source, &text, &imports)
}

fn plan(
    file: &str,
    source: &str,
    text: &SourceText,
    imports: &[(String, lang_core::Span)],
) -> Result<WorkspaceEdit, OrganizeRefusal> {
    // 零/单条 import：无序可排、无重可去
    if imports.len() <= 1 {
        return Ok(WorkspaceEdit::default());
    }
    // 区间：首个 import 的行首 → 末个 import 的行尾（不含换行；行尾 \r 含在区间内）
    let start = line_start(
        source,
        imports
            .iter()
            .map(|(_, s)| s.start.offset)
            .min()
            .unwrap_or(0),
    );
    let end = line_end(
        source,
        imports.iter().map(|(_, s)| s.end.offset).max().unwrap_or(0),
    );

    // 注释 trivia（行尾注释归属用）
    let trivia = scan_trivia(text, SourceId(0));

    // 区间内逐行分类：import 行 / 独立注释行 / 空行 / 其他（拒绝）
    let region = crate::locate::cp_slice(source, start, end);
    let mut import_lines: Vec<usize> = Vec::new();
    for (i, line) in region.split('\n').enumerate() {
        let ls = start + prefix_cps(region, i);
        let le = ls + line.chars().count() as u32;
        let is_import = imports
            .iter()
            .any(|(_, s)| s.start.offset >= ls && s.end.offset <= le);
        let trimmed = line.trim();
        if is_import {
            import_lines.push(i);
        } else if trimmed.is_empty() || trimmed.starts_with("//") {
            // 独立注释 / 空行：允许在区间内（归拢保序）
        } else {
            return Err(OrganizeRefusal::Scattered(format!(
                "{file} 第 {} 行：import 区间内夹有代码，organize-imports 拒绝处理",
                text.line_col(ls).0
            )));
        }
    }

    // 行尾注释：import span 结束后、同行内的首条注释
    let trailing_comment = |imp_end: u32, le: u32| -> Option<String> {
        trivia
            .iter()
            .find(|t| {
                t.kind == TriviaKind::Comment
                    && t.span.start.offset >= imp_end
                    && t.span.start.offset < le
            })
            .map(|t| t.text.clone())
    };

    // 编排事实：去重保首现 + 排序（码点序）
    let mut seen: Vec<&str> = Vec::new();
    let mut kept: Vec<ImportLine> = Vec::new();
    for (path, span) in imports {
        if seen.contains(&path.as_str()) {
            continue;
        }
        seen.push(path);
        let le = line_end(source, span.end.offset);
        kept.push(ImportLine {
            path: path.clone(),
            comment: trailing_comment(span.end.offset, le),
        });
    }
    kept.sort_by(|a, b| a.path.cmp(&b.path));

    // 新块：区间内的独立注释行归拢块首（原相对序），随后排序后的 import 行
    let mut block: Vec<String> = Vec::new();
    for (i, line) in region.split('\n').enumerate() {
        if import_lines.contains(&i) {
            continue;
        }
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        block.push(trimmed.to_string());
    }
    for l in &kept {
        let mut line = format!("import \"{}\"", l.path);
        if let Some(c) = &l.comment {
            line.push(' ');
            line.push_str(c);
        }
        block.push(line);
    }
    let new_block = block.join("\n");
    // 规范化对比：原块行尾 \r 归一（CRLF 源）后与新块比较
    let old_norm = region
        .replace("\r\n", "\n")
        .trim_end_matches('\r')
        .to_string();
    if old_norm == new_block {
        return Ok(WorkspaceEdit::default());
    }

    Ok(WorkspaceEdit {
        edits: vec![TextEdit {
            file: file.to_string(),
            start_offset: start,
            end_offset: end,
            replacement: new_block,
        }],
        preconditions: vec![Precondition {
            file: file.to_string(),
            file_hash: lang_edit::sha256_hex(source.as_bytes()),
        }],
    })
}

/// cp 之前最近的行首（0 或最近 `\n` 之后）。
fn line_start(src: &str, cp: u32) -> u32 {
    let mut line_start = 0u32;
    for (i, ch) in src.chars().enumerate() {
        let i = i as u32;
        if i >= cp {
            break;
        }
        if ch == '\n' {
            line_start = i + 1;
        }
    }
    line_start
}

/// cp 所在行的行尾（`\n` 之前；`\r` 含在行内）。
fn line_end(src: &str, cp: u32) -> u32 {
    let mut i = 0u32;
    for ch in src.chars() {
        if i >= cp && ch == '\n' {
            return i;
        }
        i += 1;
    }
    i
}

/// region 前 i 行的码点数（含换行符）。
fn prefix_cps(region: &str, lines: usize) -> u32 {
    region
        .split('\n')
        .take(lines)
        .map(|l| l.chars().count() as u32 + 1)
        .sum()
}

#[cfg(test)]
mod tests {
    use super::*;
    use lang_edit::apply_in_memory;

    fn organize(src: &str) -> Result<Option<String>, OrganizeRefusal> {
        let ws = organize_imports("main.lang-go", src)?;
        if ws.edits.is_empty() {
            return Ok(None);
        }
        let mut cur = std::collections::BTreeMap::new();
        cur.insert("main.lang-go".to_string(), src.to_string());
        let out = apply_in_memory(&ws, &cur).unwrap();
        Ok(Some(out["main.lang-go"].clone()))
    }

    #[test]
    fn sorts_and_dedupes() {
        let src = "import \"./zeta\"\nimport \"./alpha\"\nimport \"./zeta\"\n\nfn main() {\n    println(\"x\")\n}\n";
        let out = organize(src).unwrap().unwrap();
        assert_eq!(
            out,
            "import \"./alpha\"\nimport \"./zeta\"\n\nfn main() {\n    println(\"x\")\n}\n"
        );
    }

    #[test]
    fn trailing_comments_travel_with_lines() {
        let src = "import \"./b\" // 后注 B\nimport \"./a\" // 后注 A\nfn main() {}\n";
        let out = organize(src).unwrap().unwrap();
        assert_eq!(
            out,
            "import \"./a\" // 后注 A\nimport \"./b\" // 后注 B\nfn main() {}\n"
        );
    }

    #[test]
    fn standalone_comment_preserved() {
        let src = "import \"./b\"\n// 工具组\nimport \"./a\"\nfn main() {}\n";
        let a = organize(src).unwrap().unwrap();
        assert!(a.contains("// 工具组"));
        assert!(a.find("// 工具组").unwrap() < a.find("import \"./a\"").unwrap());
        assert!(a.find("import \"./a\"").unwrap() < a.find("import \"./b\"").unwrap());
    }

    #[test]
    fn already_organized_is_noop() {
        let src = "import \"./a\"\nimport \"./b\"\nfn main() {}\n";
        assert!(organize(src).unwrap().is_none());
        assert!(organize("fn main() {}\n").unwrap().is_none());
        assert!(
            organize("import \"./only\"\nfn main() {}\n")
                .unwrap()
                .is_none()
        );
    }

    #[test]
    fn code_inside_region_refuses() {
        let src = "import \"./b\"\nlet x = 1\nimport \"./a\"\nfn main() {}\n";
        let err = organize(src).unwrap_err();
        assert!(matches!(err, OrganizeRefusal::Scattered(_)));
    }
}
