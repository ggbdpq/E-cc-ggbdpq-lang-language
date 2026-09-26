using Lang.Core;
using Lang.Syntax;

public class TriviaTests
{
    private static IReadOnlyList<Trivia> Scan(string src) =>
        TriviaScanner.Scan(new SourceText("t.lang-go", src), new SourceId(0));

    private static string[] Comments(IReadOnlyList<Trivia> ts) =>
        ts.Where(t => t.Kind == TriviaKind.Comment).Select(t => t.Text).ToArray();

    [Fact]
    public void CommentsAreKeptVerbatim()
    {
        var ts = Scan("// 行注释\nlet x // 尾注释\n");
        Assert.Equal(["// 行注释", "// 尾注释"], Comments(ts));
    }

    [Fact]
    public void CommentInsideStringIsNotComment()
    {
        var ts = Scan("let s = \"http://x //not\" // real\n");
        Assert.Equal(["// real"], Comments(ts));
    }

    [Fact]
    public void CrlfCommentTrailingCrTrimmedButSpanReachesNewline()
    {
        // "// c\r\n"：/(0)/(1) (2)c(3)\r(4)\n(5)
        var ts = Scan("// c\r\n");
        var c = ts[0];
        Assert.Equal(TriviaKind.Comment, c.Kind);
        Assert.Equal("// c", c.Text);
        Assert.Equal(new Position(0, 1, 1), c.Span.Start);
        Assert.Equal(new Position(5, 1, 6), c.Span.End); // 终点在 \n 之前（含 \r）
        Assert.Equal(TriviaKind.Newline, ts[1].Kind);
    }

    [Fact]
    public void WhitespaceRunsAndNewlinesAreTrivia()
    {
        var ts = Scan("  \t \n  x");
        Assert.Equal(TriviaKind.Whitespace, ts[0].Kind);
        Assert.Equal("  \t ", ts[0].Text);
        Assert.Equal(TriviaKind.Newline, ts[1].Kind);
        Assert.Equal(TriviaKind.Whitespace, ts[2].Kind);
        Assert.Equal("  ", ts[2].Text);
        Assert.Equal(3, ts.Count);
    }

    [Fact]
    public void EscapedQuoteDoesNotEndStringScan()
    {
        // \" 不结束字符串，串后的 // 才是注释
        var ts = Scan("println(\"a\\\"//b\") // tail\n");
        Assert.Equal(["// tail"], Comments(ts));
    }

    [Fact]
    public void AstralCommentTextAndSpanInCodePoints()
    {
        // 契约 §0：注释含 astral 时 span 按码点计（/(0)/(1) (2)🦀(3) (4)𝕏(5,6char) → cp 终点 6）
        var ts = Scan("// 🦀 𝕏\n");
        var c = ts[0];
        Assert.Equal(TriviaKind.Comment, c.Kind);
        Assert.Equal("// 🦀 𝕏", c.Text);
        Assert.Equal(0u, c.Span.Start.Offset);
        Assert.Equal(6u, c.Span.End.Offset);
        Assert.Equal(TriviaKind.Newline, ts[1].Kind);
    }

    [Fact]
    public void StringWithAstralDoesNotHideFollowingComment()
    {
        // 串内 astral（2 char）不破坏扫描；后续注释仍被识别
        var ts = Scan("let s = \"🦀//x\" // 真\n");
        Assert.Equal(["// 真"], Comments(ts));
    }
}
