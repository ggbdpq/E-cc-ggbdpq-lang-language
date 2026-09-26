//! 无损 trivia 层（v0.5 Lossless，对照 impls/typescript/src/syntax/comments.ts）。
//!
//! 词法态扫描：跳过字符串字面量（含转义），把源码切成 注释 / 空白 / 换行 三类 trivia，
//! 按出现序输出，span 与原文齐备。Formatter 的 100% 注释保留与安全编辑（A6）据此工作。
//! 注释 `text` 含 `//` 前缀，不含换行与行尾 `\r`（CRLF 源）；span 终点仍抵 `\n` 之前。

use lang_core::{SourceId, SourceText, Span};

/// trivia 类别。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum TriviaKind {
    /// `//` 行注释（无块注释，LEX-002）。
    Comment,
    /// 空白串（空格 / 制表 / 回车）。
    Whitespace,
    /// 单个换行 `\n`。
    Newline,
}

/// 一段 trivia：类别 + 码点 span + 原文。
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub struct Trivia {
    pub kind: TriviaKind,
    pub span: Span,
    pub text: String,
}

/// 扫描全部 trivia（依出现序；字符串字面量内的 `//`、空白不算）。
#[must_use]
pub fn scan_trivia(text: &SourceText, source: SourceId) -> Vec<Trivia> {
    let cps: Vec<char> = text.text().chars().collect();
    let pos = |cp: usize| {
        let (line, column) = text.line_col(cp as u32);
        lang_core::Position::new(cp as u32, line, column)
    };
    let mut out = Vec::new();
    let mut i = 0usize;
    while i < cps.len() {
        let c = cps[i];
        match c {
            '\n' => {
                let start = i;
                i += 1;
                out.push(Trivia {
                    kind: TriviaKind::Newline,
                    span: Span::new(source, pos(start), pos(i)),
                    text: "\n".to_string(),
                });
            }
            ' ' | '\t' | '\r' => {
                let start = i;
                while i < cps.len() && matches!(cps[i], ' ' | '\t' | '\r') && cps[i] != '\n' {
                    i += 1;
                }
                let text_raw: String = cps[start..i].iter().collect();
                out.push(Trivia {
                    kind: TriviaKind::Whitespace,
                    span: Span::new(source, pos(start), pos(i)),
                    text: text_raw,
                });
            }
            '"' => {
                // 跳过字符串字面量（含转义）；未闭合串到 `\n`/文末为止（与 comments.ts 同构）
                i += 1;
                while i < cps.len() {
                    if cps[i] == '\\' {
                        i += 2;
                        continue;
                    }
                    if cps[i] == '"' || cps[i] == '\n' {
                        i += 1;
                        break;
                    }
                    i += 1;
                }
            }
            '/' if i + 1 < cps.len() && cps[i + 1] == '/' => {
                let start = i;
                while i < cps.len() && cps[i] != '\n' {
                    i += 1;
                }
                let raw: String = cps[start..i].iter().collect();
                let trimmed = raw.trim_end_matches('\r');
                let mut text_out = String::with_capacity(trimmed.len());
                text_out.push_str(trimmed);
                out.push(Trivia {
                    kind: TriviaKind::Comment,
                    span: Span::new(source, pos(start), pos(i)),
                    text: text_out,
                });
            }
            _ => {
                i += 1;
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use lang_core::Position;

    fn scan(src: &str) -> Vec<Trivia> {
        scan_trivia(&SourceText::new("t.lang-go", src.to_string()), SourceId(0))
    }

    #[test]
    fn comments_are_kept_verbatim() {
        let ts = scan("// 行注释\nlet x // 尾注释\n");
        let comments: Vec<&str> = ts
            .iter()
            .filter(|t| t.kind == TriviaKind::Comment)
            .map(|t| t.text.as_str())
            .collect();
        assert_eq!(comments, vec!["// 行注释", "// 尾注释"]);
    }

    #[test]
    fn comment_inside_string_is_not_comment() {
        let ts = scan("let s = \"http://x //not\" // real\n");
        let comments: Vec<&str> = ts
            .iter()
            .filter(|t| t.kind == TriviaKind::Comment)
            .map(|t| t.text.as_str())
            .collect();
        assert_eq!(comments, vec!["// real"]);
    }

    #[test]
    fn crlf_comment_trailing_cr_trimmed_but_span_reaches_newline() {
        // "// c\r\n"：/(0)/(1) (2)c(3)\r(4)\n(5)
        let ts = scan("// c\r\n");
        let c = &ts[0];
        assert_eq!(c.kind, TriviaKind::Comment);
        assert_eq!(c.text, "// c");
        assert_eq!(c.span.start, Position::new(0, 1, 1));
        assert_eq!(c.span.end, Position::new(5, 1, 6)); // 终点在 \n 之前（含 \r）
        assert_eq!(ts[1].kind, TriviaKind::Newline);
    }

    #[test]
    fn whitespace_runs_and_newlines_are_trivia() {
        let ts = scan("  \t \n  x");
        assert_eq!(ts[0].kind, TriviaKind::Whitespace);
        assert_eq!(ts[0].text, "  \t ");
        assert_eq!(ts[1].kind, TriviaKind::Newline);
        assert_eq!(ts[2].kind, TriviaKind::Whitespace);
        assert_eq!(ts[2].text, "  ");
        assert_eq!(ts.len(), 3);
    }

    #[test]
    fn escaped_quote_does_not_end_string_scan() {
        // \" 不结束字符串，串后的 // 才是注释
        let ts = scan("println(\"a\\\"//b\") // tail\n");
        let comments: Vec<&str> = ts
            .iter()
            .filter(|t| t.kind == TriviaKind::Comment)
            .map(|t| t.text.as_str())
            .collect();
        assert_eq!(comments, vec!["// tail"]);
    }
}
