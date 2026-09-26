//! 记号类别与记号（specs/lexical-grammar.md LEX-001..008，接口契约 §3）。
//!
//! TokenKind 全列表 = eof/newline/ident/int/float/string/underscore（7）
//! 加关键字 21 加运算符/标点 36，共 64 类，与 `impls/typescript/src/syntax/token.ts`
//! 逐一对应。契约文本写「56 类」为笔误（21+7+36=64），以全列表为准（已在 status 登记）。
//!
//! `kind_name()` 是错误消息契约面（逐字对照 TS token.ts 的 kindName，文本源自 Go 参考实现）。

use lang_core::Span;

/// 记号类别（64 类全列表，契约 §3）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum TokenKind {
    Eof,
    Newline,
    Ident,
    Int,
    Float,
    Str,
    Underscore,
    Let,
    Var,
    Fn,
    Return,
    If,
    Else,
    While,
    For,
    In,
    Match,
    Struct,
    Enum,
    True,
    False,
    Break,
    Continue,
    Go,
    Chan,
    Import,
    Extern,
    Interface,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Eq,
    EqEq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    AndAnd,
    OrOr,
    Not,
    And,
    Or,
    Xor,
    Shl,
    Shr,
    PlusEq,
    MinusEq,
    StarEq,
    SlashEq,
    Question,
    Dot,
    DotDot,
    Comma,
    Colon,
    FatArrow,
    LParen,
    RParen,
    LBrace,
    RBrace,
    LBracket,
    RBracket,
}

/// 展示名（错误消息契约面；逐字对照 TS token.ts kindName）。
#[must_use]
pub fn kind_name(k: TokenKind) -> &'static str {
    match k {
        TokenKind::Eof => "文件尾",
        TokenKind::Newline => "换行",
        TokenKind::Ident => "标识符",
        TokenKind::Int => "整数",
        TokenKind::Float => "浮点数",
        TokenKind::Str => "字符串",
        TokenKind::Underscore => "_",
        TokenKind::Let => "let",
        TokenKind::Var => "var",
        TokenKind::Fn => "fn",
        TokenKind::Return => "return",
        TokenKind::If => "if",
        TokenKind::Else => "else",
        TokenKind::While => "while",
        TokenKind::For => "for",
        TokenKind::In => "in",
        TokenKind::Match => "match",
        TokenKind::Struct => "struct",
        TokenKind::Enum => "enum",
        TokenKind::True => "true",
        TokenKind::False => "false",
        TokenKind::Break => "break",
        TokenKind::Continue => "continue",
        TokenKind::Go => "go",
        TokenKind::Chan => "chan",
        TokenKind::Import => "import",
        TokenKind::Extern => "extern",
        TokenKind::Interface => "interface",
        TokenKind::Plus => "+",
        TokenKind::Minus => "-",
        TokenKind::Star => "*",
        TokenKind::Slash => "/",
        TokenKind::Percent => "%",
        TokenKind::Eq => "=",
        TokenKind::EqEq => "==",
        TokenKind::Ne => "!=",
        TokenKind::Lt => "<",
        TokenKind::Le => "<=",
        TokenKind::Gt => ">",
        TokenKind::Ge => ">=",
        TokenKind::AndAnd => "&&",
        TokenKind::OrOr => "||",
        TokenKind::Not => "!",
        TokenKind::And => "&",
        TokenKind::Or => "|",
        TokenKind::Xor => "^",
        TokenKind::Shl => "<<",
        TokenKind::Shr => ">>",
        TokenKind::PlusEq => "+=",
        TokenKind::MinusEq => "-=",
        TokenKind::StarEq => "*=",
        TokenKind::SlashEq => "/=",
        TokenKind::Question => "?",
        TokenKind::Dot => ".",
        TokenKind::DotDot => "..",
        TokenKind::Comma => ",",
        TokenKind::Colon => ":",
        TokenKind::FatArrow => "=>",
        TokenKind::LParen => "(",
        TokenKind::RParen => ")",
        TokenKind::LBrace => "{",
        TokenKind::RBrace => "}",
        TokenKind::LBracket => "[",
        TokenKind::RBracket => "]",
    }
}

/// 关键字表（LEX-004：关键字 15 + 保留字 5，全部按关键字记号处理）。
#[must_use]
pub fn keyword(name: &str) -> Option<TokenKind> {
    match name {
        "let" => Some(TokenKind::Let),
        "var" => Some(TokenKind::Var),
        "fn" => Some(TokenKind::Fn),
        "return" => Some(TokenKind::Return),
        "if" => Some(TokenKind::If),
        "else" => Some(TokenKind::Else),
        "while" => Some(TokenKind::While),
        "for" => Some(TokenKind::For),
        "in" => Some(TokenKind::In),
        "match" => Some(TokenKind::Match),
        "struct" => Some(TokenKind::Struct),
        "enum" => Some(TokenKind::Enum),
        "true" => Some(TokenKind::True),
        "false" => Some(TokenKind::False),
        "break" => Some(TokenKind::Break),
        "continue" => Some(TokenKind::Continue),
        "go" => Some(TokenKind::Go),
        "chan" => Some(TokenKind::Chan),
        "import" => Some(TokenKind::Import),
        "extern" => Some(TokenKind::Extern),
        "interface" => Some(TokenKind::Interface),
        _ => None,
    }
}

/// 该类记号之后换行视为续行（LEX-008；对照 TS token.ts isCont）。
#[must_use]
pub fn is_cont(k: TokenKind) -> bool {
    matches!(
        k,
        TokenKind::Plus
            | TokenKind::Minus
            | TokenKind::Star
            | TokenKind::Slash
            | TokenKind::Percent
            | TokenKind::Eq
            | TokenKind::EqEq
            | TokenKind::Ne
            | TokenKind::Lt
            | TokenKind::Le
            | TokenKind::Gt
            | TokenKind::Ge
            | TokenKind::AndAnd
            | TokenKind::OrOr
            | TokenKind::Comma
            | TokenKind::Colon
            | TokenKind::FatArrow
            | TokenKind::Dot
            | TokenKind::DotDot
            | TokenKind::LParen
            | TokenKind::LBracket
            | TokenKind::LBrace
    )
}

/// 记号：类别 + 字面文本 + 码点 span（契约 §3 冻结形状）。
///
/// `lit`：int 存十进制规范文本（十六进制已折算）；string 存解码后的值；
/// 其余记号存原文。span 为 0 基码点、半开 [start, end)。
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub struct Token {
    pub kind: TokenKind,
    pub lit: String,
    pub span: Span,
}

#[cfg(test)]
mod tests {
    use super::*;
    use lang_core::{Position, SourceId};

    #[test]
    fn kind_name_contract_strings() {
        // 逐字对照 TS token.ts kindName（含全部非平凡展示名）
        assert_eq!(kind_name(TokenKind::Eof), "文件尾");
        assert_eq!(kind_name(TokenKind::Newline), "换行");
        assert_eq!(kind_name(TokenKind::Ident), "标识符");
        assert_eq!(kind_name(TokenKind::Int), "整数");
        assert_eq!(kind_name(TokenKind::Float), "浮点数");
        assert_eq!(kind_name(TokenKind::Str), "字符串");
        assert_eq!(kind_name(TokenKind::Underscore), "_");
        assert_eq!(kind_name(TokenKind::Interface), "interface");
        assert_eq!(kind_name(TokenKind::FatArrow), "=>");
        assert_eq!(kind_name(TokenKind::RBracket), "]");
        assert_eq!(kind_name(TokenKind::Shl), "<<");
    }

    #[test]
    fn keyword_table_matches_lex004() {
        // LEX-004：15 关键字 + 5 保留字
        for kw in [
            "let",
            "var",
            "fn",
            "return",
            "if",
            "else",
            "while",
            "for",
            "in",
            "match",
            "struct",
            "enum",
            "true",
            "false",
            "break",
            "continue",
            "go",
            "chan",
            "import",
            "extern",
            "interface",
        ] {
            assert!(keyword(kw).is_some(), "{kw} 应是关键字");
            assert_eq!(kind_name(keyword(kw).unwrap_or(TokenKind::Eof)), kw);
        }
        assert_eq!(keyword("x"), None);
        assert_eq!(keyword("_"), None);
    }

    #[test]
    fn cont_table_matches_lex008() {
        // 续行：运算符、逗号、冒号、=>、.、..、开括号；闭括号与记号不续行
        for k in [
            TokenKind::Plus,
            TokenKind::Minus,
            TokenKind::Star,
            TokenKind::Slash,
            TokenKind::Percent,
            TokenKind::Eq,
            TokenKind::EqEq,
            TokenKind::Ne,
            TokenKind::Lt,
            TokenKind::Le,
            TokenKind::Gt,
            TokenKind::Ge,
            TokenKind::AndAnd,
            TokenKind::OrOr,
            TokenKind::Comma,
            TokenKind::Colon,
            TokenKind::FatArrow,
            TokenKind::Dot,
            TokenKind::DotDot,
            TokenKind::LParen,
            TokenKind::LBracket,
            TokenKind::LBrace,
        ] {
            assert!(is_cont(k), "{} 应续行", kind_name(k));
        }
        for k in [
            TokenKind::RParen,
            TokenKind::RBracket,
            TokenKind::RBrace,
            TokenKind::Ident,
            TokenKind::Int,
            TokenKind::Str,
            TokenKind::Eof,
            TokenKind::Newline,
            TokenKind::Not,
            TokenKind::Question,
        ] {
            assert!(!is_cont(k), "{} 不应续行", kind_name(k));
        }
    }

    #[test]
    fn token_holds_kind_lit_span() {
        let t = Token {
            kind: TokenKind::Int,
            lit: "255".to_string(),
            span: Span::new(SourceId(0), Position::new(0, 1, 1), Position::new(3, 1, 4)),
        };
        assert_eq!(t.kind, TokenKind::Int);
        assert_eq!(t.lit, "255");
        assert_eq!(t.span.start.offset, 0);
        assert_eq!(t.span.end.offset, 3);
    }
}
