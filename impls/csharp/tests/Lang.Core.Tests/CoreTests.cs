using Lang.Core;
using Xunit;

public class SourceTextTests
{
    [Fact]
    public void AsciiPassthrough()
    {
        var s = new SourceText("main.lang-go", "fn main() {}\n");
        Assert.Equal(s.Text.Length, s.LengthCp);
        Assert.Equal(3, s.CharIndexToCp(3));
        Assert.Equal(3, s.CpToCharIndex(3));
        Assert.Equal(2, s.LineCount); // 行尾 \n 开启空行（与 lang-rs 同口径）
    }

    [Fact]
    public void CjkCountedInCodePoints()
    {
        // "中文ab"：4 码点 / 8 UTF-8 字节 / 4 char
        var s = new SourceText("m.lang-go", "中文ab");
        Assert.Equal(4, s.LengthCp);
        Assert.Equal(4, s.LengthChars);
        Assert.Equal(2, s.CharIndexToCp(2));
        Assert.Equal(1, s.CpToCharIndex(1));
        Assert.Equal((1, 3), s.LineCol(2));
    }

    [Fact]
    public void AstralEmojiIsOneCodePoint()
    {
        // "🦀x"：🦀 = 1 码点 = 2 char（代理对）——UTF-16 宿主的对抗核心
        var s = new SourceText("m.lang-go", "🦀x");
        Assert.Equal(2, s.LengthCp);
        Assert.Equal(3, s.LengthChars);
        Assert.Equal(1, s.CharIndexToCp(2)); // x 的 char index → x 是 cp 1
        Assert.Equal(0, s.CharIndexToCp(1)); // 代理对中间向下取整到 🦀
        Assert.Equal(2, s.CpToCharIndex(1)); // x 的 char index
        Assert.Equal((1, 1), s.LineCol(0));
        Assert.Equal((1, 2), s.LineCol(1));
    }

    [Fact]
    public void MultipleAstralInSequence()
    {
        // "🦀𝕏中"：3 码点 / 5 char
        var s = new SourceText("m.lang-go", "🦀𝕏中");
        Assert.Equal(3, s.LengthCp);
        Assert.Equal(5, s.LengthChars);
        Assert.Equal(1, s.CharIndexToCp(3)); // 𝕏 中间向下取整到 𝕏 本身
        Assert.Equal(2, s.CharIndexToCp(4)); // 中 的 char index
        Assert.Equal((1, 3), s.LineCol(2));
    }

    [Fact]
    public void CrlfAndMultiline()
    {
        // "a\r\n中\n"：3 行；第 2 行首 cp 3，行尾 \n 属于所在行
        var s = new SourceText("m.lang-go", "a\r\n中\n");
        Assert.Equal(3, s.LineCount);
        Assert.Equal((1, 1), s.LineCol(0));
        Assert.Equal((1, 2), s.LineCol(1));
        Assert.Equal((2, 1), s.LineCol(3));
        Assert.Equal((2, 2), s.LineCol(4));
    }

    [Fact]
    public void OutOfRangeFloors()
    {
        var s = new SourceText("m.lang-go", "中");
        Assert.Equal(1, s.CharIndexToCp(99)); // ≥ 文末一律钳到 LengthCp（EOF 语义）
        Assert.Equal(1, s.CpToCharIndex(99)); // 码点越界回到文末 char 数
    }

    [Fact]
    public void EndOfTextReturnsLengthCp()
    {
        // 文末边界（A1 词法器 EOF 定位依赖）：== LengthChars 必须给 LengthCp 而非最后一个码点
        var s = new SourceText("m.lang-go", "中x");
        Assert.Equal(2, s.CharIndexToCp(3)); // == LengthChars
        Assert.Equal(1, s.CharIndexToCp(1)); // x 的 char index → cp 1
        var empty = new SourceText("m.lang-go", "");
        Assert.Equal(0, empty.CharIndexToCp(0));
        Assert.Equal((1, 1), empty.LineCol(0));
    }
}

public class DiagnosticTests
{
    [Fact]
    public void CodeStringsVerbatim()
    {
        Assert.Equal("E1001", DiagnosticCode.E1001.CodeText());
        Assert.Equal("E2010", DiagnosticCode.E2010.CodeText());
        Assert.Equal("E3014", DiagnosticCode.E3014.CodeText());
        Assert.Equal("E4003", DiagnosticCode.E4003.CodeText());
    }

    [Fact]
    public void CodeToRuleMappingSamples()
    {
        Assert.Equal(RuleId.LexAll, DiagnosticCode.E1001.Rule());
        Assert.Equal(RuleId.NameUndef004, DiagnosticCode.E2002.Rule());
        Assert.Equal(RuleId.NameCtor005, DiagnosticCode.E2006.Rule());
        Assert.Equal(RuleId.NamePlace008, DiagnosticCode.E2007.Rule());
        Assert.Equal(RuleId.NamePlace008, DiagnosticCode.E2008.Rule());
        Assert.Equal(RuleId.GenSubst003, DiagnosticCode.E3009.Rule());
        Assert.Equal(RuleId.ValueRef003, DiagnosticCode.E3012.Rule());
        Assert.Equal(RuleId.TypeIndex008, DiagnosticCode.E3014.Rule());
        Assert.Equal(RuleId.MatchExhaustive002, DiagnosticCode.E4001.Rule());
        Assert.Equal(RuleId.MatchKind001, DiagnosticCode.E4003.Rule());
    }

    [Fact]
    public void NewDerivesRuleAndPinsError()
    {
        var d = Diagnostic.New(DiagnosticCode.E2002, "未定义 nope");
        Assert.Equal(RuleId.NameUndef004, d.RuleId);
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Empty(d.RelatedSpans);
    }

    [Fact]
    public void LineAnchorIsEmptyPoint()
    {
        var s = Diagnostic.AtLine(new SourceId(0), 7);
        Assert.Equal(new Position(0, 7, 1), s.Start);
        Assert.Equal(s.Start, s.End);
    }

    [Fact]
    public void RuleCanonicalStrings()
    {
        Assert.Equal("NAME-SHADOW-002", RuleId.NameShadow002.RuleText());
        Assert.Equal("VALUE-TASKSHARE-007", RuleId.ValueTaskshare007.RuleText());
        Assert.Equal("FFI-001", RuleId.Ffi001.RuleText());
        Assert.Equal("LEX-005", RuleId.Lex005.RuleText());
    }

    [Fact]
    public void SpanKeepsFields()
    {
        var s = new Span(new SourceId(2), new Position(0, 1, 1), new Position(12, 1, 13));
        Assert.Equal((uint)12, s.End.Offset);
        Assert.False(s.IsEmpty);
        Assert.True(Span.Point(new SourceId(0), new Position(0, 1, 1)).IsEmpty);
    }
}
