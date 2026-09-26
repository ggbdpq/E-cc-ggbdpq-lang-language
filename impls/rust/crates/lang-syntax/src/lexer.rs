//! 词法器（specs/lexical-grammar.md LEX-001..008；对照 impls/typescript/src/syntax/lexer.ts）。
//!
//! 在码点序列上扫描，Token.span 一律 0 基码点（经 `SourceText` 折算 line/column）。
//! 换行即语句结束；续行只看上一记号（`token::is_cont`，LEX-008）。
//! 词法错误 = `lang_core::Diagnostic` E1001（message 逐字对照 Go 参考实现文本），
//! 定位为行级锚（v0.4 契约：column 恒 1、offset 0）。错误消息文本属契约面。

use lang_core::{Diagnostic, DiagnosticCode, Position, SourceId, SourceText, Span};

use crate::token::{Token, TokenKind, is_cont, keyword};
use crate::unicode_tables::{LETTER_RANGES, ND_RANGES};

/// 一般类别 L*（与 Go unicode.IsLetter、TS `\p{L}` 同源）。
fn is_letter(c: char) -> bool {
    in_ranges(LETTER_RANGES, c as u32)
}

/// 十进制数字类别 Nd（与 Go unicode.IsDigit、TS `\p{Nd}` 同源；不含 Nl/No）。
fn is_digit(c: char) -> bool {
    in_ranges(ND_RANGES, c as u32)
}

/// 闭区间表二分查找（表按起点有序且互不重叠）。
fn in_ranges(table: &[(u32, u32)], cp: u32) -> bool {
    let idx = table.partition_point(|&(_, hi)| hi < cp);
    table.get(idx).is_some_and(|&(lo, _)| lo <= cp)
}

fn is_ident_start(c: char) -> bool {
    c == '_' || is_letter(c)
}

fn is_ident_part(c: char) -> bool {
    c == '_' || is_letter(c) || is_digit(c)
}

/// 词法器状态：码点游标 + 行号 + 已产出记号。
struct Lexer<'a> {
    cps: Vec<char>,
    text: &'a SourceText,
    source: SourceId,
    i: usize,
    line: u32,
    /// 上一记号类别（续行判定用；`None` = 尚无记号）。
    last: Option<TokenKind>,
    toks: Vec<Token>,
}

impl<'a> Lexer<'a> {
    fn new(text: &'a SourceText, source: SourceId) -> Self {
        Self {
            cps: text.text().chars().collect(),
            text,
            source,
            i: 0,
            line: 1,
            last: None,
            toks: Vec::new(),
        }
    }

    fn peek(&self, n: usize) -> char {
        self.cps.get(self.i + n).copied().unwrap_or('\0')
    }

    /// 码点偏移 → Position（1 基 line/column，经 SourceText）。
    fn pos(&self, cp: usize) -> Position {
        let (line, column) = self.text.line_col(cp as u32);
        Position::new(cp as u32, line, column)
    }

    fn span_from(&self, start: usize) -> Span {
        Span::new(self.source, self.pos(start), self.pos(self.i))
    }

    fn emit(&mut self, kind: TokenKind, lit: impl Into<String>, start: usize) {
        let span = self.span_from(start);
        self.toks.push(Token {
            kind,
            lit: lit.into(),
            span,
        });
        self.last = Some(kind);
    }

    /// E1001 行级诊断（当前行）。
    fn err(&self, msg: impl Into<String>) -> Diagnostic {
        Diagnostic::new(
            DiagnosticCode::E1001,
            msg,
            Some(Diagnostic::at_line(self.source, self.line)),
        )
    }

    /// 落实续行规则：续行记号之后、行首的换行不产记号（LEX-008）。
    fn emit_newline(&mut self, nl_cp: usize) {
        if self.toks.is_empty() || self.last.is_some_and(is_cont) {
            return;
        }
        if self.last == Some(TokenKind::Newline) {
            return;
        }
        let span = self.span_from(nl_cp);
        self.toks.push(Token {
            kind: TokenKind::Newline,
            lit: "\n".to_string(),
            span,
        });
        self.last = Some(TokenKind::Newline);
    }

    /// 整数 / 浮点（LEX-005/006）。十六进制折算十进制规范文本。
    fn number(&mut self, start: usize) -> Result<(), Diagnostic> {
        if self.cps[start] == '0' && matches!(self.peek(1), 'x' | 'X') {
            self.i += 2;
            let digits_at = self.i;
            while self.i < self.cps.len() && is_hex_digit(self.cps[self.i]) {
                self.i += 1;
            }
            if self.i == digits_at {
                return Err(self.err("十六进制字面量缺少数字（如 0xFF）"));
            }
            let raw: String = self.cps[start..self.i].iter().collect();
            let digits: String = self.cps[digits_at..self.i].iter().collect();
            let val = u64::from_str_radix(&digits, 16)
                .ok()
                .filter(|&v| v <= i64::MAX as u64);
            match val {
                Some(v) => self.emit(TokenKind::Int, v.to_string(), start),
                None => return Err(self.err(format!("十六进制字面量超出 int 范围: {raw}"))),
            }
            return Ok(());
        }

        while self.i < self.cps.len() && is_digit(self.cps[self.i]) {
            self.i += 1;
        }
        // "1..3" 里第一个 '.' 不属于数字（LEX-006）
        if self.i < self.cps.len() && self.cps[self.i] == '.' && is_digit(self.peek(1)) {
            self.i += 1;
            while self.i < self.cps.len() && is_digit(self.cps[self.i]) {
                self.i += 1;
            }
            let lit: String = self.cps[start..self.i].iter().collect();
            self.emit(TokenKind::Float, lit, start);
        } else {
            let lit: String = self.cps[start..self.i].iter().collect();
            // 原样保留（含全角数字等 Nd 记号），合法性由 parser 阶段校验（与 TS 证据一致）
            self.emit(TokenKind::Int, lit, start);
        }
        Ok(())
    }

    /// 字符串字面量（LEX-007）：转义仅 `\n \t \r \" \\` 五种。
    fn string_lit(&mut self, start: usize) -> Result<(), Diagnostic> {
        self.i += 1;
        let mut value = String::new();
        loop {
            let Some(&c) = self.cps.get(self.i) else {
                return Err(self.err("字符串未闭合"));
            };
            if c == '\n' {
                return Err(self.err("字符串未闭合"));
            }
            if c == '"' {
                self.i += 1;
                break;
            }
            if c == '\\' {
                self.i += 1;
                let Some(&esc) = self.cps.get(self.i) else {
                    return Err(self.err("字符串未闭合"));
                };
                match esc {
                    'n' => value.push('\n'),
                    't' => value.push('\t'),
                    'r' => value.push('\r'),
                    '"' => value.push('"'),
                    '\\' => value.push('\\'),
                    other => return Err(self.err(format!("不支持的转义 \\{other}"))),
                }
                self.i += 1;
                continue;
            }
            value.push(c);
            self.i += 1;
        }
        self.emit(TokenKind::Str, value, start);
        Ok(())
    }

    /// 标识符 / 关键字 / 单独下划线（LEX-003/004）。
    fn ident(&mut self, start: usize) {
        while self.i < self.cps.len() && is_ident_part(self.cps[self.i]) {
            self.i += 1;
        }
        let name: String = self.cps[start..self.i].iter().collect();
        if name == "_" {
            self.emit(TokenKind::Underscore, name, start);
            return;
        }
        match keyword(&name) {
            Some(k) => self.emit(k, name, start),
            None => self.emit(TokenKind::Ident, name, start),
        }
    }

    /// 运算符 / 标点（最长匹配两级）。
    fn op(&mut self, start: usize) -> Result<(), Diagnostic> {
        let c = self.cps[self.i];
        let two = |lx: &mut Self, next: char, k: TokenKind, lit: &str| -> bool {
            if lx.peek(1) == next {
                lx.i += 2;
                lx.emit(k, lit, start);
                true
            } else {
                false
            }
        };
        let kind = match c {
            '&' => {
                if two(self, '&', TokenKind::AndAnd, "&&") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::And
            }
            '|' => {
                if two(self, '|', TokenKind::OrOr, "||") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Or
            }
            '^' => {
                self.i += 1;
                TokenKind::Xor
            }
            '+' => {
                if two(self, '=', TokenKind::PlusEq, "+=") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Plus
            }
            '-' => {
                if two(self, '=', TokenKind::MinusEq, "-=") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Minus
            }
            '*' => {
                if two(self, '=', TokenKind::StarEq, "*=") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Star
            }
            '/' => {
                if two(self, '=', TokenKind::SlashEq, "/=") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Slash
            }
            '=' => {
                if two(self, '=', TokenKind::EqEq, "==") {
                    return Ok(());
                }
                if two(self, '>', TokenKind::FatArrow, "=>") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Eq
            }
            '!' => {
                if two(self, '=', TokenKind::Ne, "!=") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Not
            }
            '<' => {
                if two(self, '<', TokenKind::Shl, "<<") {
                    return Ok(());
                }
                if two(self, '=', TokenKind::Le, "<=") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Lt
            }
            '>' => {
                if two(self, '>', TokenKind::Shr, ">>") {
                    return Ok(());
                }
                if two(self, '=', TokenKind::Ge, ">=") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Gt
            }
            '.' => {
                if two(self, '.', TokenKind::DotDot, "..") {
                    return Ok(());
                }
                self.i += 1;
                TokenKind::Dot
            }
            '%' => {
                self.i += 1;
                TokenKind::Percent
            }
            '(' => {
                self.i += 1;
                TokenKind::LParen
            }
            ')' => {
                self.i += 1;
                TokenKind::RParen
            }
            '{' => {
                self.i += 1;
                TokenKind::LBrace
            }
            '}' => {
                self.i += 1;
                TokenKind::RBrace
            }
            '[' => {
                self.i += 1;
                TokenKind::LBracket
            }
            ']' => {
                self.i += 1;
                TokenKind::RBracket
            }
            ',' => {
                self.i += 1;
                TokenKind::Comma
            }
            ':' => {
                self.i += 1;
                TokenKind::Colon
            }
            '?' => {
                self.i += 1;
                TokenKind::Question
            }
            other => return Err(self.err(format!("非法字符 {other}"))),
        };
        let lit = kind_name_lit(kind);
        self.emit(kind, lit, start);
        Ok(())
    }

    fn run(mut self) -> Result<Vec<Token>, Diagnostic> {
        while self.i < self.cps.len() {
            let start = self.i;
            let c = self.cps[self.i];
            match c {
                ' ' | '\t' | '\r' => self.i += 1,
                '\n' => {
                    self.i += 1;
                    self.line += 1;
                    self.emit_newline(start);
                }
                '/' if self.peek(1) == '/' => {
                    // 行注释（LEX-002）：token 流里不产记号，trivia 层保留
                    while self.i < self.cps.len() && self.cps[self.i] != '\n' {
                        self.i += 1;
                    }
                }
                c if is_digit(c) => self.number(start)?,
                '"' => self.string_lit(start)?,
                c if is_ident_start(c) => self.ident(start),
                _ => self.op(start)?,
            }
        }
        let at = self.pos(self.i);
        self.toks.push(Token {
            kind: TokenKind::Eof,
            lit: String::new(),
            span: Span::new(self.source, at, at),
        });
        Ok(self.toks)
    }
}

fn is_hex_digit(c: char) -> bool {
    c.is_ascii_hexdigit()
}

/// 运算符/标点的展示名即其字面（kind_name 对非关键字恒返回原文）。
fn kind_name_lit(k: TokenKind) -> String {
    crate::token::kind_name(k).to_string()
}

/// 把源码切成记号流（末尾追加 EOF；词法错误以 E1001 诊断返回）。
pub fn lex(text: &SourceText, source: SourceId) -> Result<Vec<Token>, Diagnostic> {
    Lexer::new(text, source).run()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn lex_str(src: &str) -> Result<Vec<Token>, Diagnostic> {
        lex(&SourceText::new("t.lang-go", src.to_string()), SourceId(0))
    }

    fn kinds(toks: &[Token]) -> Vec<TokenKind> {
        toks.iter().map(|t| t.kind).collect()
    }

    fn err_msg(r: Result<Vec<Token>, Diagnostic>) -> String {
        r.expect_err("应为词法错误").message
    }

    #[test]
    fn ascii_basic_stream() {
        let toks = lex_str("let x = 1\n").unwrap_or_default();
        assert_eq!(
            kinds(&toks),
            vec![
                TokenKind::Let,
                TokenKind::Ident,
                TokenKind::Eq,
                TokenKind::Int,
                TokenKind::Newline,
                TokenKind::Eof,
            ]
        );
        assert_eq!(toks[0].lit, "let");
        assert_eq!(toks[1].lit, "x");
        assert_eq!(toks[3].lit, "1");
        assert_eq!(toks[4].lit, "\n");
        assert_eq!(toks[5].lit, "");
    }

    #[test]
    fn ascii_token_spans_are_code_point_half_open() {
        // let(0..3) sp x(4..5) sp =(6..7) sp 1(8..9) \n(9..10)
        let toks = lex_str("let x = 1\n").unwrap_or_default();
        assert_eq!(toks[0].span.start.offset, 0);
        assert_eq!(toks[0].span.end.offset, 3);
        assert_eq!((toks[0].span.start.line, toks[0].span.start.column), (1, 1));
        assert_eq!(toks[1].span.start.offset, 4);
        assert_eq!(toks[3].span.start.offset, 8);
        assert_eq!(toks[4].span.start.offset, 9); // 换行记号本身
        assert_eq!(toks[5].span.start.offset, 10); // EOF 单点
        assert_eq!(toks[5].span.end.offset, 10);
    }

    #[test]
    fn cjk_identifiers_span_by_code_points() {
        // let(0..3) sp 消(4)息(5) sp =(6) sp 1(8)
        let toks = lex_str("let 消息 = 1").unwrap_or_default();
        let id = &toks[1];
        assert_eq!(id.kind, TokenKind::Ident);
        assert_eq!(id.lit, "消息");
        assert_eq!(id.span.start.offset, 4);
        assert_eq!(id.span.end.offset, 6); // 2 码点，而非 4 字节
        assert_eq!((id.span.start.line, id.span.start.column), (1, 5));
    }

    #[test]
    fn emoji_spans_by_code_point_inside_string() {
        // l(0)e(1)t(2) sp(3) s(4) =(6 前 1 空格)(5) =(6) sp(7) "(8) 🦀(9) x(10) "(11)
        // 4 字节 emoji 只占 1 码点，span 用码点度量
        let toks = lex_str("let s = \"🦀x\"").unwrap_or_default();
        let st = &toks[3];
        assert_eq!(st.kind, TokenKind::Str);
        assert_eq!(st.lit, "🦀x");
        assert_eq!(st.span.start.offset, 8);
        assert_eq!(st.span.end.offset, 12);
    }

    #[test]
    fn emoji_is_not_an_ident_char() {
        // 🦀 是 So 类（非字母非数字）：与 Go IsLetter / TS \p{L} 一致判非法
        let e = lex_str("let 🦀 = 1").expect_err("");
        assert_eq!(e.message, "非法字符 🦀");
    }

    #[test]
    fn crlf_lines_and_columns() {
        // a(0) \r(1) \n(2) b(3) \n(4)
        let toks = lex_str("a\r\nb\n").unwrap_or_default();
        let a = &toks[0];
        assert_eq!(a.span.start, Position::new(0, 1, 1));
        let nl = &toks[1];
        assert_eq!(nl.kind, TokenKind::Newline);
        assert_eq!(nl.span.start.offset, 2); // \r 被跳过，记号覆盖 \n
        let b = &toks[2];
        assert_eq!((b.span.start.line, b.span.start.column), (2, 1));
        assert_eq!(b.span.start.offset, 3);
    }

    #[test]
    fn hex_literals_normalized_to_decimal() {
        assert_eq!(lex_str("0x10").unwrap_or_default()[0].lit, "16");
        assert_eq!(lex_str("0xFF").unwrap_or_default()[0].lit, "255");
        assert_eq!(lex_str("0X10").unwrap_or_default()[0].lit, "16");
        assert_eq!(
            lex_str("0x7FFFFFFFFFFFFFFF").unwrap_or_default()[0].lit,
            "9223372036854775807"
        );
    }

    #[test]
    fn hex_errors_are_e1001_with_contract_text() {
        let e = lex_str("0x").expect_err("");
        assert_eq!(e.code, DiagnosticCode::E1001);
        assert_eq!(e.message, "十六进制字面量缺少数字（如 0xFF）");
        assert_eq!(e.severity, lang_core::Severity::Error);
        assert_eq!(e.rule_id.as_str(), "LEX-ALL");
        let e = lex_str("0x1FFFFFFFFFFFFFFFF").expect_err("");
        assert_eq!(
            e.message,
            "十六进制字面量超出 int 范围: 0x1FFFFFFFFFFFFFFFF"
        );
    }

    #[test]
    fn float_vs_range_dot() {
        // LEX-006：1..3 是整数夹区间，不是浮点
        let toks = lex_str("1..3").unwrap_or_default();
        assert_eq!(
            kinds(&toks),
            vec![
                TokenKind::Int,
                TokenKind::DotDot,
                TokenKind::Int,
                TokenKind::Eof
            ]
        );
        assert_eq!(toks[0].lit, "1");
        let toks = lex_str("2.5").unwrap_or_default();
        assert_eq!(toks[0].kind, TokenKind::Float);
        assert_eq!(toks[0].lit, "2.5");
        // "1." —— '.' 后无数字，浮点不成立
        let toks = lex_str("1.").unwrap_or_default();
        assert_eq!(
            kinds(&toks),
            vec![TokenKind::Int, TokenKind::Dot, TokenKind::Eof]
        );
    }

    #[test]
    fn string_escapes_decode() {
        // 源码： "a\n\t\r\"\\b" → 解码 a + \n + \t + \r + " + \ + b
        let src = "\"a\\n\\t\\r\\\"\\\\b\"";
        let toks = lex_str(src).unwrap_or_default();
        assert_eq!(toks[0].kind, TokenKind::Str);
        assert_eq!(toks[0].lit, "a\n\t\r\"\\b");
    }

    #[test]
    fn string_errors() {
        assert_eq!(err_msg(lex_str("\"abc")), "字符串未闭合");
        assert_eq!(err_msg(lex_str("\"ab\nc\"")), "字符串未闭合");
        assert_eq!(err_msg(lex_str("\"a\\q\"")), "不支持的转义 \\q");
    }

    #[test]
    fn line_continuation_after_cont_token() {
        // LEX-008：`+` 后的换行被忽略；`2` 后的换行产生记号
        let toks = lex_str("let x = 1 +\n 2\n").unwrap_or_default();
        assert_eq!(
            kinds(&toks),
            vec![
                TokenKind::Let,
                TokenKind::Ident,
                TokenKind::Eq,
                TokenKind::Int,
                TokenKind::Plus,
                TokenKind::Int,
                TokenKind::Newline,
                TokenKind::Eof,
            ]
        );
    }

    #[test]
    fn comments_produce_no_tokens_but_newlines_survive() {
        let toks = lex_str("// 整行注释\nlet x // 尾注释\n").unwrap_or_default();
        assert_eq!(
            kinds(&toks),
            vec![
                TokenKind::Let,
                TokenKind::Ident,
                TokenKind::Newline,
                TokenKind::Eof
            ]
        );
    }

    #[test]
    fn illegal_char_is_e1001() {
        let e = lex_str("let @ = 1").expect_err("");
        assert_eq!(e.code, DiagnosticCode::E1001);
        assert_eq!(e.message, "非法字符 @");
    }

    #[test]
    fn superscript_two_is_not_a_digit_or_letter() {
        // Nd 精确性：² 是 No 类，不是数字也不是字母 → 非法字符
        let e = lex_str("1²").expect_err("");
        assert_eq!(e.message, "非法字符 ²");
    }

    #[test]
    fn fullwidth_digits_are_nd_digits() {
        // １（U+FF11）是 Nd：词法收进 int 记号，合法性留给 parser（与 TS 证据一致）
        let toks = lex_str("１").unwrap_or_default();
        assert_eq!(toks[0].kind, TokenKind::Int);
        assert_eq!(toks[0].lit, "１");
    }

    #[test]
    fn operators_longest_match() {
        let toks = lex_str("a <<= 1 >> 2").unwrap_or_default();
        assert_eq!(
            kinds(&toks),
            vec![
                TokenKind::Ident,
                TokenKind::Shl,
                TokenKind::Eq,
                TokenKind::Int,
                TokenKind::Shr,
                TokenKind::Int,
                TokenKind::Eof,
            ]
        );
    }

    #[test]
    fn underscore_and_keywords() {
        let toks = lex_str("_ match").unwrap_or_default();
        assert_eq!(toks[0].kind, TokenKind::Underscore);
        assert_eq!(toks[0].lit, "_");
        assert_eq!(toks[1].kind, TokenKind::Match);
    }
}
