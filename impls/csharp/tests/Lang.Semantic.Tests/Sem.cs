using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;

/// <summary>
/// 语义测试公共助手：源码 → Analysis / 诊断 (码, 行) 序列（行 0 = 无锚）。
/// 期望值全部按 specs/conformance/手算书写，禁拿实现当期望。
/// </summary>
internal static class Sem
{
    public static readonly SourceId Src = new(3);

    public static Analysis Analyze(string src)
    {
        var text = new SourceText("a.lang-go", src);
        var parsed = Parser.Parse(text, Src);
        Assert.Null(parsed.Error);
        return LangSemantic.Analyze(parsed.Program, Src);
    }

    public static List<(string Code, uint Line)> Diags(string src)
    {
        var a = Analyze(src);
        return a.Diags
            .Select(d => (d.Code.CodeText(), d.PrimarySpan?.Start.Line ?? 0))
            .ToList();
    }

    /// <summary>两份文件语句合并后分析（模拟装载器 NAME-MERGE-010）。</summary>
    public static List<(string Code, uint Line)> DiagsMerged(string a, string b)
    {
        var ta = new SourceText("a.lang-go", a);
        var tb = new SourceText("b.lang-go", b);
        List<Stmt> stmts = [];
        var pa = Parser.Parse(ta, Src);
        var pb = Parser.Parse(tb, Src);
        Assert.Null(pa.Error);
        Assert.Null(pb.Error);
        stmts.AddRange(pa.Program);
        stmts.AddRange(pb.Program);
        return LangSemantic.Analyze(stmts, Src).Diags
            .Select(d => (d.Code.CodeText(), d.PrimarySpan?.Start.Line ?? 0))
            .ToList();
    }

    public static void AssertDiags(string src, params (string Code, uint Line)[] want)
    {
        var got = Diags(src);
        got.Sort();
        var wantSorted = want.ToList();
        wantSorted.Sort();
        Assert.True(
            got.SequenceEqual(wantSorted),
            $"诊断不符。\n源码:\n{src}\n期望: {Fmt(wantSorted)}\n实际: {Fmt(got)}");
    }

    private static string Fmt(List<(string Code, uint Line)> ds) =>
        "[" + string.Join(", ", ds.Select(d => $"({d.Code}, {d.Line})")) + "]";
}
