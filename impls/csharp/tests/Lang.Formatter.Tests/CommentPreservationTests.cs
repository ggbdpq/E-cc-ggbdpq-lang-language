using Lang.Core;
using Lang.Syntax;

namespace Lang.Formatter.Tests;

/// <summary>
/// 注释保留逐类断言（100% 零丢失）：行尾 / 独立 / 文件头 / 块尾 / 字符串内伪注释 /
/// match 臂 / struct 字段 / 函数字面量体 / 空块+尾注释幂等陷阱。零丢失统一用
/// 「源码 trivia 流的每条注释文本都在输出中出现」硬检查 + 幂等回归。
/// </summary>
public class CommentPreservationTests
{
    private static string Fmt(string src)
    {
        var r = Formatter.FormatSource(src);
        Assert.Null(r.Error);
        return r.Output;
    }

    /// <summary>零丢失硬检查：trivia 流里每条 Comment 的文本逐条出现在输出中；输出幂等。</summary>
    private static void AssertAllCommentsSurvive(string src)
    {
        var out1 = Fmt(src);
        var comments = TriviaScanner.Scan(new SourceText("z.lang-go", src), new SourceId(0))
            .Where(t => t.Kind == TriviaKind.Comment)
            .Select(t => t.Text)
            .ToArray();
        Assert.NotEmpty(comments);
        foreach (var c in comments)
            Assert.True(out1.Contains(c), $"注释丢失 {c}\n--- 输出 ---\n{out1}");
        Assert.Equal(out1, Fmt(out1));
    }

    [Fact]
    public void TrailingCommentKeepsTwoSpaceGapOnSameLine()
    {
        var out1 = Fmt("fn main() {\n    let x = 1 // 行尾注释\n    println(x)\n}\n");
        Assert.Contains("let x = 1  // 行尾注释", out1);
        Assert.Equal(out1, Fmt(out1));
    }

    [Fact]
    public void LeadingAndBlockTailCommentsKeptInPlace()
    {
        const string src = "// 文件头注释\nfn add(a: int, b: int): int {\n    // 求和\n    return a + b\n}\n\n// 主入口\nfn main() {\n    println(add(1, 2))\n    // 块尾注释\n}\n";
        var out1 = Fmt(src);
        foreach (var want in new[] { "// 文件头注释", "// 求和", "// 主入口", "// 块尾注释" })
            Assert.True(out1.Contains(want), $"注释丢失 {want}\n{out1}");
        Assert.Equal(out1, Fmt(out1));
        AssertAllCommentsSurvive(src);
    }

    [Fact]
    public void CommentGroupsKeepBlankLineGrouping()
    {
        const string src = "fn main() {\n    println(1)\n}\n\n// 尾注释组 A\n\n// 尾注释组 B\n";
        var out1 = Fmt(src);
        Assert.Contains("\n    // 尾注释组 A\n\n    // 尾注释组 B\n", out1);
        Assert.Equal(out1, Fmt(out1));
    }

    [Fact]
    public void BlankLineGroupingInsideBody()
    {
        const string src = "fn f() {\n    let a = 1\n\n    // 分组说明\n    let b = 2\n    println(a, b)\n}\n";
        var out1 = Fmt(src);
        Assert.Contains("let a = 1\n\n    // 分组说明\n    let b = 2\n", out1);
        Assert.Equal(out1, Fmt(out1));
    }

    [Fact]
    public void PseudoCommentInsideStringIsNotTrivia()
    {
        const string src = "let s = \"http://x //not\"\nlet u = \"//也不是注释\"\n";
        var out1 = Fmt(src);
        Assert.Equal(src, out1);
        // trivia 流零注释：字符串内的 // 不算（TriviaScanner 词法态保证）
        var comments = TriviaScanner.Scan(new SourceText("z.lang-go", src), new SourceId(0))
            .Where(t => t.Kind == TriviaKind.Comment).ToArray();
        Assert.Empty(comments);
    }

    [Fact]
    public void MatchArmCommentsMigrateToStmtBoundaryWithoutLoss()
    {
        // match 臂注释：语句边界冲刷（位置后移，文本零丢失——证据同构）
        const string src = "fn f(s: int): int {\n    return match s {\n        1 => 10 // 一\n        _ => 0 // 其他\n    }\n}\nfn main() {\n    println(f(1))\n}\n";
        var out1 = Fmt(src);
        Assert.Contains("// 一", out1);
        Assert.Contains("// 其他", out1);
        Assert.Equal(out1, Fmt(out1));
        AssertAllCommentsSurvive(src);
    }

    [Fact]
    public void StructFieldCommentsSurvive()
    {
        const string src = "struct P {\n    x: int // 横坐标\n    y: int\n}\nfn main() {\n    println(P{x: 1, y: 2})\n}\n";
        var out1 = Fmt(src);
        Assert.Contains("// 横坐标", out1);
        Assert.Equal(out1, Fmt(out1));
        AssertAllCommentsSurvive(src);
    }

    [Fact]
    public void FnLitBodyCommentsSurvive()
    {
        const string src = "let f = fn(x: int) {\n    // 参数说明\n    println(x)\n}\n";
        var out1 = Fmt(src);
        Assert.Contains("// 参数说明", out1);
        Assert.Equal(out1, Fmt(out1));
        AssertAllCommentsSurvive(src);
    }

    [Fact]
    public void CommentsAfterEmptyBodyStayIdempotent()
    {
        // 幂等修正的目标场景：注释物理上位于 `}` 之后、被收进空块（lang-rs 已知陷阱）
        const string src = "fn main() {\n}\n\n// A\n\n// B\n";
        var once = Fmt(src);
        var twice = Fmt(once);
        Assert.Equal(once, twice);
        AssertAllCommentsSurvive(src);
    }

    [Fact]
    public void PureCommentFileAndEmptyFile()
    {
        Assert.Equal("// 只有注释\n// 第二行\n", Fmt("// 只有注释\n// 第二行\n"));
        Assert.Equal("", Fmt(""));
        AssertAllCommentsSurvive("// 只有注释\n// 第二行\n");
    }

    [Fact]
    public void CommentBetweenStatementsStaysBetweenThem()
    {
        const string src = "fn main() {\n    let a = 1\n    // 中缝注释\n    let b = 2\n}\n";
        var out1 = Fmt(src);
        Assert.Contains("let a = 1\n    // 中缝注释\n    let b = 2\n", out1);
        Assert.Equal(out1, Fmt(out1));
    }

    [Fact]
    public void TrailingAndIndependentCommentOnEveryStmtKindSurvives()
    {
        // 15 语句变体抽样 × 前导 + 行尾注释：全类目零丢失
        // （extern 是顶层语句，不能进函数体——ParseStmt 不收）
        const string src = """
            import "./lib"
            extern "go" {
                fn host(n: int): int // extern 注
            }
            struct P {
                x: int // 字段注
            }
            enum E {
                A(int) // case 注
            }
            fn (p P).m(): int {
                return p.x // return 注
            }
            fn main() {
                // 前导注
                let x = 1 // let 注
                x += 1 // assign 注
                println(x) // expr 注
                if x > 0 {
                    x -= 1 // if 体注
                }
                while x > 0 {
                    x -= 1 // while 体注
                }
                for i in 1..2 {
                    println(i) // for 体注
                }
                go host(1) // go 注
                // 块尾注
            }
            """;
        AssertAllCommentsSurvive(src.ReplaceLineEndings("\n"));
    }

    // —— UTF-16 码点世界（契约 §0 astral 用例） ——

    [Fact]
    public void AstralCommentTextSurvivesByteExact()
    {
        // 注释正文含 astral 码点 + 贴附行前有 astral 内容：零丢失且逐字保真
        const string src = "let 𝕏 = \"🦀\"\n// 🦀𝕏𠀀 独立注释\nfn main() {\n    println(𝕏) // 🦀 尾注\n}\n";
        var out1 = Fmt(src);
        Assert.Contains("// 🦀𝕏𠀀 独立注释", out1);
        Assert.Contains("println(𝕏)  // 🦀 尾注", out1);
        AssertAllCommentsSurvive(src);
    }
}
