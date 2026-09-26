using Lang.Core;

namespace Lang.Syntax;

/// <summary>
/// 记号类别（64 类全列表，接口契约 §3；specs/lexical-grammar.md LEX-001..008）。
/// 与 impls/rust/crates/lang-syntax/src/token.rs、impls/typescript/src/syntax/token.ts 逐一对应：
/// eof/newline/ident/int/float/string/underscore（7）+ 关键字/保留字（21）+ 运算符/标点（36）。
/// </summary>
public enum TokenKind
{
    Eof, Newline, Ident, Int, Float, Str, Underscore,
    Let, Var, Fn, Return, If, Else, While, For, In, Match, Struct, Enum, True, False, Break, Continue,
    Go, Chan, Import, Extern, Interface,
    Plus, Minus, Star, Slash, Percent,
    Eq, EqEq, Ne, Lt, Le, Gt, Ge, AndAnd, OrOr, Not, And, Or, Xor, Shl, Shr,
    PlusEq, MinusEq, StarEq, SlashEq,
    Question, Dot, DotDot, Comma, Colon, FatArrow,
    LParen, RParen, LBrace, RBrace, LBracket, RBracket,
}

/// <summary>记号文本契约面：展示名 / 关键字表 / 续行表（LEX-004、LEX-008）。</summary>
public static class TokenKinds
{
    /// <summary>展示名（错误消息契约面；逐字对照 TS token.ts kindName，源自 Go 参考实现）。</summary>
    public static string KindName(TokenKind k) => k switch
    {
        TokenKind.Eof => "文件尾",
        TokenKind.Newline => "换行",
        TokenKind.Ident => "标识符",
        TokenKind.Int => "整数",
        TokenKind.Float => "浮点数",
        TokenKind.Str => "字符串",
        TokenKind.Underscore => "_",
        TokenKind.Let => "let",
        TokenKind.Var => "var",
        TokenKind.Fn => "fn",
        TokenKind.Return => "return",
        TokenKind.If => "if",
        TokenKind.Else => "else",
        TokenKind.While => "while",
        TokenKind.For => "for",
        TokenKind.In => "in",
        TokenKind.Match => "match",
        TokenKind.Struct => "struct",
        TokenKind.Enum => "enum",
        TokenKind.True => "true",
        TokenKind.False => "false",
        TokenKind.Break => "break",
        TokenKind.Continue => "continue",
        TokenKind.Go => "go",
        TokenKind.Chan => "chan",
        TokenKind.Import => "import",
        TokenKind.Extern => "extern",
        TokenKind.Interface => "interface",
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.Eq => "=",
        TokenKind.EqEq => "==",
        TokenKind.Ne => "!=",
        TokenKind.Lt => "<",
        TokenKind.Le => "<=",
        TokenKind.Gt => ">",
        TokenKind.Ge => ">=",
        TokenKind.AndAnd => "&&",
        TokenKind.OrOr => "||",
        TokenKind.Not => "!",
        TokenKind.And => "&",
        TokenKind.Or => "|",
        TokenKind.Xor => "^",
        TokenKind.Shl => "<<",
        TokenKind.Shr => ">>",
        TokenKind.PlusEq => "+=",
        TokenKind.MinusEq => "-=",
        TokenKind.StarEq => "*=",
        TokenKind.SlashEq => "/=",
        TokenKind.Question => "?",
        TokenKind.Dot => ".",
        TokenKind.DotDot => "..",
        TokenKind.Comma => ",",
        TokenKind.Colon => ":",
        TokenKind.FatArrow => "=>",
        TokenKind.LParen => "(",
        TokenKind.RParen => ")",
        TokenKind.LBrace => "{",
        TokenKind.RBrace => "}",
        TokenKind.LBracket => "[",
        TokenKind.RBracket => "]",
        // 64 类全表锁定；枚举可携带越界值（非法输入）——运行期硬失败而非静默兜底
        _ => throw new InvalidOperationException($"未登记的 TokenKind: {k}"),
    };

    /// <summary>关键字表（LEX-004：关键字 16 + 保留字 5，全部按关键字记号处理）。</summary>
    public static TokenKind? KeywordOf(string name) => name switch
    {
        "let" => TokenKind.Let,
        "var" => TokenKind.Var,
        "fn" => TokenKind.Fn,
        "return" => TokenKind.Return,
        "if" => TokenKind.If,
        "else" => TokenKind.Else,
        "while" => TokenKind.While,
        "for" => TokenKind.For,
        "in" => TokenKind.In,
        "match" => TokenKind.Match,
        "struct" => TokenKind.Struct,
        "enum" => TokenKind.Enum,
        "true" => TokenKind.True,
        "false" => TokenKind.False,
        "break" => TokenKind.Break,
        "continue" => TokenKind.Continue,
        "go" => TokenKind.Go,
        "chan" => TokenKind.Chan,
        "import" => TokenKind.Import,
        "extern" => TokenKind.Extern,
        "interface" => TokenKind.Interface,
        _ => null,
    };

    /// <summary>该类记号之后换行视为续行（LEX-008；对照 TS token.ts isCont）。</summary>
    public static bool IsCont(TokenKind k) => k is
        TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or TokenKind.Percent or
        TokenKind.Eq or TokenKind.EqEq or TokenKind.Ne or TokenKind.Lt or TokenKind.Le or
        TokenKind.Gt or TokenKind.Ge or TokenKind.AndAnd or TokenKind.OrOr or TokenKind.Comma or
        TokenKind.Colon or TokenKind.FatArrow or TokenKind.Dot or TokenKind.DotDot or
        TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace;
}

/// <summary>
/// 记号：类别 + 字面文本 + 码点 span（契约 §3 冻结形状）。
/// lit：int 存十进制规范文本（十六进制已折算）；string 存解码后的值；其余记号存原文。
/// span 为 0 基码点、半开 [start, end)。
/// </summary>
public sealed record Token(TokenKind Kind, string Lit, Span Span);
