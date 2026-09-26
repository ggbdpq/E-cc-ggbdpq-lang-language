using Lang.Core;
using Lang.Syntax;

namespace Lang.Formatter.Tests;

/// <summary>
/// 幂等矩阵（母本计划 Phase R23 硬契约）：conformance 16 分类 151 个 main.lang-go +
/// dogfood/langalgo 全部 .lang-go，逐文件断言
/// ① 首次解析零错误；② format → reparse → format 二次输出 == 首次（幂等）；
/// ③ AST 形态（span 无关）相等（语义等价）；④ 同输入两次逐字节相等（确定性）；
/// ⑤ 重解析零错误；⑥ 输出零 \r、末尾单换行（稳定换行）。
/// </summary>
public class IdempotencyMatrixTests
{
    private static string? FindRepoDir(string marker)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, marker);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>矩阵主体：六条断言逐文件全跑。</summary>
    private static void Matrix(string src, string label)
    {
        // ① 首次解析零错误
        var r1 = Formatter.FormatSource(src);
        Assert.True(r1.Error is null, $"{label} 首次解析失败: {r1.Error?.Message}");

        // ⑤+② format → reparse → format，二次输出 == 首次
        var r2 = Formatter.FormatSource(r1.Output);
        Assert.True(r2.Error is null, $"{label} 重解析失败: {r2.Error?.Message}");
        Assert.True(r1.Output == r2.Output,
            $"{label} 幂等破坏\n--- 一次 ---\n{r1.Output}\n--- 二次 ---\n{r2.Output}");

        // ③ AST 形态（span 无关）相等
        var text1 = new SourceText($"{label}.orig", src);
        var text2 = new SourceText($"{label}.fmt", r1.Output);
        var p1 = Parser.Parse(text1, new SourceId(0));
        var p2 = Parser.Parse(text2, new SourceId(0));
        Assert.Null(p1.Error);
        Assert.Null(p2.Error);
        Assert.True(AstDump.Program(p1.Program) == AstDump.Program(p2.Program),
            $"{label} AST 形态漂移\n--- 源 ---\n{AstDump.Program(p1.Program)}\n--- 格式化后 ---\n{AstDump.Program(p2.Program)}");

        // ④ 同输入两次逐字节相等
        var t1 = Formatter.Format(p1.Program, TriviaScanner.Scan(text1, new SourceId(0)), text1);
        var t2 = Formatter.Format(p1.Program, TriviaScanner.Scan(text1, new SourceId(0)), text1);
        Assert.True(t1 == t2, $"{label} 确定性破坏");

        // ⑥ 稳定换行：零 \r、末尾恰一换行或空串
        Assert.DoesNotContain("\r", r1.Output);
        Assert.True(r1.Output.Length == 0 || (r1.Output.EndsWith('\n') && !r1.Output.EndsWith("\n\n")),
            $"{label} 末尾换行不稳定");
    }

    // —— conformance：16 分类 151 个 main.lang-go ——

    public static IEnumerable<object[]> ConformanceCases()
    {
        var root = FindRepoDir("conformance");
        if (root is null) return [];
        // 显式排序：目录枚举序不作为契约
        return Directory.GetFiles(root, "main.lang-go", SearchOption.AllDirectories)
            .Order().Select(p => new object[] { p });
    }

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void ConformanceFileIsIdempotent(string path) => Matrix(File.ReadAllText(path), path);

    [Fact]
    public void ConformanceCaseCountIs151()
    {
        Assert.Equal(151, ConformanceCases().Count());
    }

    // —— dogfood：langalgo 全部 .lang-go ——

    public static IEnumerable<object[]> DogfoodCases()
    {
        var root = FindRepoDir("dogfood");
        if (root is null) return [];
        return Directory.GetFiles(root, "*.lang-go", SearchOption.AllDirectories)
            .Order().Select(p => new object[] { p });
    }

    [Theory]
    [MemberData(nameof(DogfoodCases))]
    public void DogfoodFileIsIdempotent(string path) => Matrix(File.ReadAllText(path), path);

    [Fact]
    public void DogfoodCaseCountMatchesDisk()
    {
        var cases = DogfoodCases().Count();
        Assert.True(cases > 0, "dogfood 目录未找到（从 BaseDirectory 上溯）");
        Assert.Equal(66, cases); // 当前盘面 66 个；新增文件时同步更新此锚
    }

    // —— UTF-16 码点世界（契约 §0 astral 用例） ——

    [Fact]
    public void AstralSyntheticSourcePassesMatrix()
    {
        Matrix("let 𝕏 = \"🦀𠀀\" // 🦀 注释\nfn main() {\n    println(𝕏, len(𝕏))\n}\n", "astral-synthetic");
    }
}
