using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Interp.Tests;

/// <summary>
/// AST vs IR 差分（A3 的 Compiler.Compile 已就绪）：同一 case 双路
/// （AST 参考解释器 vs IrMachine）stdout 逐字节一致 + 退出状态一致。
/// 失败按最小化复现定位归属，只修自己领地。
/// 确定性压力：含 go/chan/closure/map 的 case × 3 遍 × 双路，零漂移。
/// </summary>
public class DifferentialTests
{
    private static (string Root, List<ConformanceCase> RunCases) Setup()
    {
        var root = ConformanceHarness.FindRoot();
        Assert.NotNull(root);
        var cases = ConformanceHarness.Discover(root!).Where(c => c.Manifest.IsRun).ToList();
        return (root!, cases);
    }

    /// <summary>装载 + 分析一次，供双路共用（同 stmts 实例分别进 Interp/IrMachine）。</summary>
    private static (IReadOnlyList<Stmt> Stmts, Analysis Analysis)? Load(ConformanceCase c, List<string> failures)
    {
        var (stmts, err) = ConformanceHarness.LoadCase(c.Dir);
        if (err is not null)
        {
            failures.Add($"{c.Category}/{c.Name}: {err}");
            return null;
        }

        var analysis = RunHelper.Analyze(stmts!);
        if (analysis.HasErrors())
        {
            failures.Add($"{c.Category}/{c.Name}: 语义诊断: {string.Join("; ", analysis.Diags.Select(d => d.Code))}");
            return null;
        }

        return (stmts!, analysis);
    }

    [Fact]
    public void RunCaseCountIs120()
    {
        var (_, run) = Setup();
        Assert.Equal(ConformanceHarness.ExpectedRunCaseCount, run.Count);
    }

    /// <summary>全量 run case 双路差分：两路均成功、stdout 逐字节一致、且与 case.yaml 真值一致。</summary>
    [Fact]
    public void AstIrDifferential_AllRunCases()
    {
        var (_, runCases) = Setup();
        var failures = new List<string>();
        var ok = 0;
        foreach (var c in runCases)
        {
            var loaded = Load(c, failures);
            if (loaded is null)
            {
                continue;
            }

            var (stmts, analysis) = loaded.Value;
            var ast = RunHelper.RunAst(stmts, analysis);
            var ir = RunHelper.RunIr(stmts, analysis);

            if (!ast.Ok || !ir.Ok)
            {
                failures.Add(
                    $"{c.Category}/{c.Name}: 状态不一致 ast.Ok={ast.Ok} ir.Ok={ir.Ok} ast 错误={ast.Error} ir 错误={ir.Error}");
                continue;
            }

            if (ast.Stdout != ir.Stdout)
            {
                failures.Add(
                    $"{c.Category}/{c.Name}: 双路 stdout 不一致\n--- AST ---\n{ast.Stdout}--- IR ---\n{ir.Stdout}---");
                continue;
            }

            if (ast.Stdout != c.Manifest.Stdout)
            {
                failures.Add(
                    $"{c.Category}/{c.Name}: 与 case.yaml 真值不一致\n--- 期望 ---\n{c.Manifest.Stdout}--- 实际 ---\n{ast.Stdout}---");
                continue;
            }

            ok++;
        }

        Assert.True(failures.Count == 0,
            $"{ok}/{runCases.Count} 双路一致，{failures.Count} 失败:\n  {string.Join("\n  ", failures)}");
    }

    /// <summary>确定性压力：10 个含 go/chan/closure/map 的 case × 3 遍 × 双路，零漂移。</summary>
    [Fact]
    public void DeterministicStress_TenCasesBy3RunsBothPaths()
    {
        var (_, runCases) = Setup();
        (string Cat, string Name)[] picks =
        [
            ("concurrency", "chan-fifo-single-producer"),
            ("concurrency", "chan-multi-values-fifo"),
            ("concurrency", "chan-send-recv"),
            ("concurrency", "chan-struct-payload"),
            ("concurrency", "spawn-eval-args-immediately"),
            ("concurrency", "spawn-two-tasks-deterministic"),
            ("closure", "counter-closure"),
            ("closure", "closure-loop-capture-var"),
            ("values", "nested-struct-copy"),
            ("collections", "map-iteration-insertion-order"),
        ];
        var selected = new List<ConformanceCase>();
        foreach (var (cat, name) in picks)
        {
            var hit = runCases.FirstOrDefault(c => c.Category == cat && c.Name == name);
            Assert.True(hit is not null, $"压力夹具 {cat}/{name} 不在 run case 集内");
            selected.Add(hit!);
        }

        Assert.Equal(10, selected.Count);

        var failures = new List<string>();
        foreach (var c in selected)
        {
            var loaded = Load(c, failures);
            Assert.True(loaded is not null, $"{c.Category}/{c.Name} 装载失败");
            var (stmts, analysis) = loaded!.Value;

            var ast0 = RunHelper.RunAst(stmts, analysis);
            var ir0 = RunHelper.RunIr(stmts, analysis);
            Assert.True(ast0.Ok && ir0.Ok, $"{c.Category}/{c.Name}: ast.Ok={ast0.Ok} ir.Ok={ir0.Ok}");

            for (var round = 1; round <= 2; round++)
            {
                var ast = RunHelper.RunAst(stmts, analysis);
                var ir = RunHelper.RunIr(stmts, analysis);
                if (ast.Stdout != ast0.Stdout || ir.Stdout != ir0.Stdout || ast.Stdout != ir.Stdout)
                {
                    failures.Add($"{c.Category}/{c.Name}: 第 {round + 1} 遍漂移");
                }
            }
        }

        Assert.True(failures.Count == 0, "确定性压力失败:\n  " + string.Join("\n  ", failures));
    }
}
