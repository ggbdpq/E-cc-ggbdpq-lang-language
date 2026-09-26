namespace Lang.Interp.Tests;

/// <summary>
/// conformance 实测（本波最大价值）：conformance/ 全部 run 类 case（120 个）——
/// 迷你装载器（目录合并/import 展开/菱形去重/环检测）→ Analyze → AST 参考解释器 →
/// stdout 逐字节比对 case.yaml 的 expect.stdout（exitCode 0、stderr 空）。
/// 每分类一个测试逐 case 断言失败明细；io 类（jsonParse/jsonStringify 宿主内建）在内。
/// </summary>
public class ConformanceRunTests
{
    private static (string Root, List<ConformanceCase> Cases) Setup()
    {
        var root = ConformanceHarness.FindRoot();
        Assert.NotNull(root);
        var cases = ConformanceHarness.Discover(root!);
        var run = cases.Where(c => c.Manifest.IsRun).ToList();
        return (root!, run);
    }

    private static List<string> CheckCategory(List<ConformanceCase> runCases, string category, Func<ConformanceCase, (string?, string?)> run)
    {
        var failures = new List<string>();
        var ran = 0;
        foreach (var c in runCases.Where(c => c.Category == category))
        {
            ran++;
            var (stdout, error) = run(c);
            if (error is not null)
            {
                failures.Add($"{category}/{c.Name}: {error}");
                continue;
            }

            if (stdout != c.Manifest.Stdout)
            {
                failures.Add(
                    $"{category}/{c.Name}: stdout 不一致\n--- 期望 ---\n{c.Manifest.Stdout}--- 实际 ---\n{stdout}---");
            }
        }

        Assert.True(ran > 0, $"分类 {category} 应至少覆盖一个 run case（实跑 {ran}）");
        return failures;
    }

    private static void AssertCategory(List<ConformanceCase> runCases, string category)
    {
        var failures = CheckCategory(runCases, category, ConformanceHarness.RunCaseAst);
        Assert.True(failures.Count == 0,
            $"{category}: {failures.Count} 个 run case 失败:\n  {string.Join("\n  ", failures)}");
    }

    [Fact]
    public void RunCaseCountIs120()
    {
        var (_, run) = Setup();
        Assert.Equal(ConformanceHarness.ExpectedRunCaseCount, run.Count);
    }

    [Theory]
    [InlineData("expressions")]
    [InlineData("syntax")]
    [InlineData("collections")]
    [InlineData("values")]
    [InlineData("match")]
    [InlineData("struct")]
    [InlineData("generics")]
    [InlineData("result")]
    [InlineData("concurrency")]
    [InlineData("module")]
    [InlineData("closure")]
    [InlineData("enum")]
    [InlineData("option")]
    [InlineData("types")]
    [InlineData("io")]
    public void AstInterpreter_Category(string category)
    {
        var (_, run) = Setup();
        AssertCategory(run, category);
    }

    /// <summary>io 类 run case 逐个登记（jsonParse/jsonStringify 宿主内建 + 输出面真值）。</summary>
    [Fact]
    public void AstInterpreter_IoCasesExplicit()
    {
        var (_, run) = Setup();
        var io = run.Where(c => c.Category == "io").Select(c => c.Name).ToList();
        Assert.Equal(
            new HashSet<string> { "json-parse-scalars", "json-roundtrip", "json-stringify-nested", "len-of-string-unicode", "print-mixed-floats", "println-formatting", "string-escapes-and-print" },
            io.ToHashSet());
        Assert.Equal(7, io.Count);
    }
}
