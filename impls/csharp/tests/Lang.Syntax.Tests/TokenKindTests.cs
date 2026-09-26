using Lang.Core;
using Lang.Syntax;

public class TokenKindTests
{
    /// <summary>kind 文本契约面（逐字对照 rust lang-syntax token.rs kind_name / TS token.ts kindName）。</summary>
    private static (TokenKind Kind, string Name)[] Table() =>
    [
        (TokenKind.Eof, "文件尾"),
        (TokenKind.Newline, "换行"),
        (TokenKind.Ident, "标识符"),
        (TokenKind.Int, "整数"),
        (TokenKind.Float, "浮点数"),
        (TokenKind.Str, "字符串"),
        (TokenKind.Underscore, "_"),
        (TokenKind.Let, "let"),
        (TokenKind.Var, "var"),
        (TokenKind.Fn, "fn"),
        (TokenKind.Return, "return"),
        (TokenKind.If, "if"),
        (TokenKind.Else, "else"),
        (TokenKind.While, "while"),
        (TokenKind.For, "for"),
        (TokenKind.In, "in"),
        (TokenKind.Match, "match"),
        (TokenKind.Struct, "struct"),
        (TokenKind.Enum, "enum"),
        (TokenKind.True, "true"),
        (TokenKind.False, "false"),
        (TokenKind.Break, "break"),
        (TokenKind.Continue, "continue"),
        (TokenKind.Go, "go"),
        (TokenKind.Chan, "chan"),
        (TokenKind.Import, "import"),
        (TokenKind.Extern, "extern"),
        (TokenKind.Interface, "interface"),
        (TokenKind.Plus, "+"),
        (TokenKind.Minus, "-"),
        (TokenKind.Star, "*"),
        (TokenKind.Slash, "/"),
        (TokenKind.Percent, "%"),
        (TokenKind.Eq, "="),
        (TokenKind.EqEq, "=="),
        (TokenKind.Ne, "!="),
        (TokenKind.Lt, "<"),
        (TokenKind.Le, "<="),
        (TokenKind.Gt, ">"),
        (TokenKind.Ge, ">="),
        (TokenKind.AndAnd, "&&"),
        (TokenKind.OrOr, "||"),
        (TokenKind.Not, "!"),
        (TokenKind.And, "&"),
        (TokenKind.Or, "|"),
        (TokenKind.Xor, "^"),
        (TokenKind.Shl, "<<"),
        (TokenKind.Shr, ">>"),
        (TokenKind.PlusEq, "+="),
        (TokenKind.MinusEq, "-="),
        (TokenKind.StarEq, "*="),
        (TokenKind.SlashEq, "/="),
        (TokenKind.Question, "?"),
        (TokenKind.Dot, "."),
        (TokenKind.DotDot, ".."),
        (TokenKind.Comma, ","),
        (TokenKind.Colon, ":"),
        (TokenKind.FatArrow, "=>"),
        (TokenKind.LParen, "("),
        (TokenKind.RParen, ")"),
        (TokenKind.LBrace, "{"),
        (TokenKind.RBrace, "}"),
        (TokenKind.LBracket, "["),
        (TokenKind.RBracket, "]"),
    ];

    [Fact]
    public void KindNameCoversAll64Kinds()
    {
        var table = Table();
        Assert.Equal(64, table.Length); // 契约 §3：64 类全列表
        foreach (var (kind, name) in table)
            Assert.Equal(name, TokenKinds.KindName(kind));
    }

    [Fact]
    public void KeywordTableMatchesLex004()
    {
        // LEX-004：关键字 16 + 保留字 5（规范枚举列表为准）
        string[] kws =
        [
            "let", "var", "fn", "return", "if", "else", "while", "for", "in", "match",
            "struct", "enum", "true", "false", "break", "continue",
            "go", "chan", "import", "extern", "interface",
        ];
        foreach (var kw in kws)
        {
            var k = TokenKinds.KeywordOf(kw);
            Assert.NotNull(k);
            Assert.Equal(kw, TokenKinds.KindName(k!.Value));
        }
        Assert.Null(TokenKinds.KeywordOf("x"));
        Assert.Null(TokenKinds.KeywordOf("_"));
        Assert.Null(TokenKinds.KeywordOf("𝕏")); // astral 字母属标识符，不是关键字
    }

    [Fact]
    public void ContTableMatchesLex008()
    {
        TokenKind[] conts =
        [
            TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Percent,
            TokenKind.Eq, TokenKind.EqEq, TokenKind.Ne, TokenKind.Lt, TokenKind.Le, TokenKind.Gt, TokenKind.Ge,
            TokenKind.AndAnd, TokenKind.OrOr, TokenKind.Comma, TokenKind.Colon, TokenKind.FatArrow,
            TokenKind.Dot, TokenKind.DotDot, TokenKind.LParen, TokenKind.LBracket, TokenKind.LBrace,
        ];
        foreach (var k in conts)
            Assert.True(TokenKinds.IsCont(k), $"{TokenKinds.KindName(k)} 应续行");

        TokenKind[] nonConts =
        [
            TokenKind.RParen, TokenKind.RBracket, TokenKind.RBrace, TokenKind.Ident, TokenKind.Int,
            TokenKind.Str, TokenKind.Eof, TokenKind.Newline, TokenKind.Not, TokenKind.Question,
        ];
        foreach (var k in nonConts)
            Assert.False(TokenKinds.IsCont(k), $"{TokenKinds.KindName(k)} 不应续行");
    }

    [Fact]
    public void TokenHoldsKindLitSpan()
    {
        var t = new Token(TokenKind.Int, "255",
            new Span(new SourceId(0), new Position(0, 1, 1), new Position(3, 1, 4)));
        Assert.Equal(TokenKind.Int, t.Kind);
        Assert.Equal("255", t.Lit);
        Assert.Equal(0u, t.Span.Start.Offset);
        Assert.Equal(3u, t.Span.End.Offset);
    }

    [Fact]
    public void TokenLitCarriesAstralValue()
    {
        // 契约 §0：astral（🦀𝕏）在记号 Lit 里按码点语义原样承载（2 码点 = 4 char）
        var t = new Token(TokenKind.Str, "🦀𝕏", default);
        Assert.Equal(TokenKind.Str, t.Kind);
        Assert.Equal("🦀𝕏", t.Lit);
        Assert.Equal(4, t.Lit.Length);
    }
}
