using Lang.Core;
using Lang.Syntax;

public class LexerTests
{
    private static LexResult Lex(string src) =>
        Lexer.Lex(new SourceText("t.lang-go", src), new SourceId(0));

    private static List<Token> Toks(string src)
    {
        var r = Lex(src);
        Assert.Null(r.Error);
        return r.Tokens.ToList();
    }

    private static string Err(string src)
    {
        var r = Lex(src);
        Assert.NotNull(r.Error);
        Assert.Equal(DiagnosticCode.E1001, r.Error!.Code);
        return r.Error.Message;
    }

    [Fact]
    public void AsciiBasicStream()
    {
        var toks = Toks("let x = 1\n");
        Assert.Equal(
        [
            TokenKind.Let, TokenKind.Ident, TokenKind.Eq, TokenKind.Int,
            TokenKind.Newline, TokenKind.Eof,
        ], toks.Select(t => t.Kind));
        Assert.Equal("let", toks[0].Lit);
        Assert.Equal("x", toks[1].Lit);
        Assert.Equal("1", toks[3].Lit);
        Assert.Equal("\n", toks[4].Lit);
        Assert.Equal("", toks[5].Lit);
    }

    [Fact]
    public void AsciiTokenSpansAreCodePointHalfOpen()
    {
        // let(0..3) sp x(4..5) sp =(6..7) sp 1(8..9) \n(9..10)
        var toks = Toks("let x = 1\n");
        Assert.Equal(0u, toks[0].Span.Start.Offset);
        Assert.Equal(3u, toks[0].Span.End.Offset);
        Assert.Equal((1u, 1u), (toks[0].Span.Start.Line, toks[0].Span.Start.Column));
        Assert.Equal(4u, toks[1].Span.Start.Offset);
        Assert.Equal(8u, toks[3].Span.Start.Offset);
        Assert.Equal(9u, toks[4].Span.Start.Offset); // 换行记号本身
        Assert.Equal(10u, toks[5].Span.Start.Offset); // EOF 单点
        Assert.Equal(10u, toks[5].Span.End.Offset);
    }

    [Fact]
    public void CjkIdentifiersSpanByCodePoints()
    {
        // let(0..3) sp 消(4)息(5) sp =(6) sp 1(8)
        var toks = Toks("let 消息 = 1");
        var id = toks[1];
        Assert.Equal(TokenKind.Ident, id.Kind);
        Assert.Equal("消息", id.Lit);
        Assert.Equal(4u, id.Span.Start.Offset);
        Assert.Equal(6u, id.Span.End.Offset); // 2 码点，而非 4 字节
        Assert.Equal((1u, 5u), (id.Span.Start.Line, id.Span.Start.Column));
    }

    [Fact]
    public void AstralStringSpanByCodePoints()
    {
        // l(0)e(1)t(2) sp(3) s(4) sp(5) =(6) sp(7) "(8) 🦀(9) x(10) "(11)
        // 4 字节 emoji 只占 1 码点（2 char），span 用码点度量
        var toks = Toks("let s = \"🦀x\"");
        var st = toks[3];
        Assert.Equal(TokenKind.Str, st.Kind);
        Assert.Equal("🦀x", st.Lit);
        Assert.Equal(8u, st.Span.Start.Offset);
        Assert.Equal(12u, st.Span.End.Offset);
    }

    [Fact]
    public void AstralIdentSpanAndLineCol()
    {
        // 𝕏（U+1D54F，Lu 类）是合法标识符首字符：char index 4..6 折算为码点 4..5
        var toks = Toks("let 𝕏 = 1");
        var id = toks[1];
        Assert.Equal(TokenKind.Ident, id.Kind);
        Assert.Equal("𝕏", id.Lit);
        Assert.Equal(4u, id.Span.Start.Offset);
        Assert.Equal(5u, id.Span.End.Offset);
        Assert.Equal((1u, 5u), (id.Span.Start.Line, id.Span.Start.Column));
        Assert.Equal((1u, 6u), (id.Span.End.Line, id.Span.End.Column));
        Assert.Equal(9u, toks[^1].Span.Start.Offset); // EOF：文末码点
    }

    [Fact]
    public void AstralEmojiIsNotAnIdentChar()
    {
        // 🦀 是 So 类（非字母非数字）：与 Go IsLetter / TS \p{L} 一致判非法
        Assert.Equal("非法字符 🦀", Err("let 🦀 = 1"));
    }

    [Fact]
    public void CrlfLinesAndColumns()
    {
        // a(0) \r(1) \n(2) b(3) \n(4)
        var toks = Toks("a\r\nb\n");
        var a = toks[0];
        Assert.Equal(new Position(0, 1, 1), a.Span.Start);
        var nl = toks[1];
        Assert.Equal(TokenKind.Newline, nl.Kind);
        Assert.Equal(2u, nl.Span.Start.Offset); // \r 被跳过，记号覆盖 \n
        var b = toks[2];
        Assert.Equal((2u, 1u), (b.Span.Start.Line, b.Span.Start.Column));
        Assert.Equal(3u, b.Span.Start.Offset);
    }

    [Fact]
    public void HexLiteralsNormalizedToDecimal()
    {
        Assert.Equal("16", Toks("0x10")[0].Lit);
        Assert.Equal("255", Toks("0xFF")[0].Lit);
        Assert.Equal("16", Toks("0X10")[0].Lit);
        Assert.Equal("9223372036854775807", Toks("0x7FFFFFFFFFFFFFFF")[0].Lit);
    }

    [Fact]
    public void HexErrorsAreE1001WithContractText()
    {
        var r = Lex("0x");
        Assert.NotNull(r.Error);
        Assert.Equal(DiagnosticCode.E1001, r.Error.Code);
        Assert.Equal("十六进制字面量缺少数字（如 0xFF）", r.Error.Message);
        Assert.Equal(Severity.Error, r.Error.Severity);
        Assert.Equal("LEX-ALL", r.Error.RuleId.RuleText());
        Assert.Equal("十六进制字面量超出 int 范围: 0x1FFFFFFFFFFFFFFFF", Err("0x1FFFFFFFFFFFFFFFF"));
    }

    [Fact]
    public void FloatVsRangeDot()
    {
        // LEX-006：1..3 是整数夹区间，不是浮点
        var toks = Toks("1..3");
        Assert.Equal([TokenKind.Int, TokenKind.DotDot, TokenKind.Int, TokenKind.Eof],
            toks.Select(t => t.Kind));
        Assert.Equal("1", toks[0].Lit);

        var f = Toks("2.5");
        Assert.Equal(TokenKind.Float, f[0].Kind);
        Assert.Equal("2.5", f[0].Lit);

        // "1." —— '.' 后无数字，浮点不成立
        var dot = Toks("1.");
        Assert.Equal([TokenKind.Int, TokenKind.Dot, TokenKind.Eof], dot.Select(t => t.Kind));
    }

    [Fact]
    public void StringEscapesDecode()
    {
        // 源码："a\n\t\r\"\\b" → 解码 a + \n + \t + \r + " + \ + b
        var toks = Toks("\"a\\n\\t\\r\\\"\\\\b\"");
        Assert.Equal(TokenKind.Str, toks[0].Kind);
        Assert.Equal("a\n\t\r\"\\b", toks[0].Lit);
    }

    [Fact]
    public void StringErrors()
    {
        Assert.Equal("字符串未闭合", Err("\"abc"));
        Assert.Equal("字符串未闭合", Err("\"ab\nc\""));
        Assert.Equal("不支持的转义 \\q", Err("\"a\\q\""));
    }

    [Fact]
    public void AstralEscapeIsUnsupported()
    {
        // 转义表只有五种；astral 码点转义同样报契约文本
        Assert.Equal("不支持的转义 \\🦀", Err("\"\\🦀\""));
    }

    [Fact]
    public void LineContinuationAfterContToken()
    {
        // LEX-008：`+` 后的换行被忽略；`2` 后的换行产生记号
        var toks = Toks("let x = 1 +\n 2\n");
        Assert.Equal(
        [
            TokenKind.Let, TokenKind.Ident, TokenKind.Eq, TokenKind.Int,
            TokenKind.Plus, TokenKind.Int, TokenKind.Newline, TokenKind.Eof,
        ], toks.Select(t => t.Kind));
    }

    [Fact]
    public void CommentsProduceNoTokensButNewlinesSurvive()
    {
        var toks = Toks("// 整行注释\nlet x // 尾注释\n");
        Assert.Equal([TokenKind.Let, TokenKind.Ident, TokenKind.Newline, TokenKind.Eof],
            toks.Select(t => t.Kind));
    }

    [Fact]
    public void IllegalCharIsE1001()
    {
        Assert.Equal("非法字符 @", Err("let @ = 1"));
    }

    [Fact]
    public void SuperscriptTwoIsNotADigitOrLetter()
    {
        // Nd 精确性：² 是 No 类，不是数字也不是字母 → 非法字符
        Assert.Equal("非法字符 ²", Err("1²"));
    }

    [Fact]
    public void FullwidthDigitsAreNd()
    {
        // １（U+FF11）是 Nd：词法收进 int 记号，合法性留给 parser（与 TS 证据一致）
        var toks = Toks("１");
        Assert.Equal(TokenKind.Int, toks[0].Kind);
        Assert.Equal("１", toks[0].Lit);
    }

    [Fact]
    public void OperatorsLongestMatch()
    {
        var toks = Toks("a <<= 1 >> 2");
        Assert.Equal(
        [
            TokenKind.Ident, TokenKind.Shl, TokenKind.Eq, TokenKind.Int,
            TokenKind.Shr, TokenKind.Int, TokenKind.Eof,
        ], toks.Select(t => t.Kind));
    }

    [Fact]
    public void UnderscoreAndKeywords()
    {
        var toks = Toks("_ match");
        Assert.Equal(TokenKind.Underscore, toks[0].Kind);
        Assert.Equal("_", toks[0].Lit);
        Assert.Equal(TokenKind.Match, toks[1].Kind);
    }

    [Fact]
    public void EmptySourceYieldsSingleEof()
    {
        var toks = Toks("");
        Assert.Single(toks);
        Assert.Equal(TokenKind.Eof, toks[0].Kind);
        Assert.Equal(new Position(0, 1, 1), toks[0].Span.Start);
        Assert.Equal(toks[0].Span.End, toks[0].Span.Start);
    }

    [Fact]
    public void AstralOnlySourceYieldsSingleEofAtCodePointOne()
    {
        // 契约 §0：EOF 码点定位在 astral 源的文末（"=0 🦀=1 "=2 → EOF cp 3；裸 astral 字符仍是非法字符）
        var toks = Toks("\"🦀\"");
        Assert.Equal(2, toks.Count);
        Assert.Equal(TokenKind.Str, toks[0].Kind);
        Assert.Equal("🦀", toks[0].Lit);
        Assert.Equal(0u, toks[0].Span.Start.Offset); // "=0 🦀=1 "=2 → 串 span [0, 3)
        Assert.Equal(3u, toks[0].Span.End.Offset);
        Assert.Equal(TokenKind.Eof, toks[1].Kind);
        Assert.Equal(3u, toks[1].Span.Start.Offset);
        Assert.Equal((1u, 4u), (toks[1].Span.Start.Line, toks[1].Span.Start.Column));
    }
}
