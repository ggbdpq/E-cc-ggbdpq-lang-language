using Lang.Conformance;
using Xunit;

namespace Lang.Conformance.Tests;

/// <summary>run/check 比较与三态判定专项（假执行器驱动 RunRoute 注入点）。</summary>
public class CompareTests
{
    private static CaseExpect RunExpect() => new(0, "ok\n", "", []);

    private static CaseExpect CheckExpect() =>
        new(0, "", "", [new DiagExpect("E3001", "TYPE-ASSIGN-001", "error", 2)]);

    private static ConformanceCase Case(CaseMode mode) => new(
        "demo", "types", mode, ["TYPE-ASSIGN-001"],
        mode == CaseMode.Run ? RunExpect() : CheckExpect());

    private static RouteOutput OkOutput(string stdout = "ok\n", string stderr = "", int exit = 0) =>
        new(stdout, stderr, exit);

    /// <summary>四路路径名冻结（协议面：路由名会出现在 note/报告里）。</summary>
    [Fact]
    public void Run_routes_are_fixed() =>
        Assert.Equal(["interp-ast", "interp-ir", "go", "js"], Routes.RunRoutes);

    /// <summary>stdout 逐字节：差一个换行都算失败。</summary>
    [Fact]
    public void Run_stdout_is_byte_exact()
    {
        var c = Case(CaseMode.Run);
        var ok = Compare.JudgeRun(c, Routes.InterpAst, RouteResult.Ok(OkOutput()));
        Assert.True(ok.Ok);

        var offByNewline = Compare.JudgeRun(c, Routes.InterpAst, RouteResult.Ok(OkOutput("ok")));
        Assert.False(offByNewline.Ok);
        Assert.Contains("stdout", offByNewline.Note);
    }

    /// <summary>退出码与 stderr 一致纳入判定；astral 内容逐字节比对（接口契约 §0 对抗轴）。</summary>
    [Fact]
    public void Run_checks_exit_code_stderr_and_astral_bytes()
    {
        var c = Case(CaseMode.Run);
        var bad = Compare.JudgeRun(c, Routes.Go, RouteResult.Ok(OkOutput(stderr: "panic\n", exit: 1)));
        Assert.False(bad.Ok);
        Assert.Contains("exitCode", bad.Note);
        Assert.Contains("stderr", bad.Note);

        var astral = Compare.JudgeRun(
            c, Routes.Js, RouteResult.Ok(OkOutput("ok 🦀\n")));
        Assert.False(astral.Ok);
        Assert.Contains("🦀", astral.Note);
    }

    /// <summary>skipped ≠ 通过（不谎报绿）；执行失败直接判败。</summary>
    [Fact]
    public void Run_route_skipped_is_not_pass()
    {
        var c = Case(CaseMode.Run);
        var skipped = Compare.JudgeRun(c, Routes.Js, RouteResult.Skip("宿主缺 node"));
        Assert.False(skipped.Ok);
        Assert.True(skipped.Skipped);

        var failed = Compare.JudgeRun(c, Routes.Go, RouteResult.Fail("go build 失败"));
        Assert.False(failed.Ok);
        Assert.False(failed.Skipped);
    }

    /// <summary>check 多重集：数量差异被精确清点（期望 1 条来 2 条 = 多 1；行号不同 = 缺 + 多）。</summary>
    [Fact]
    public void Check_multiset_counts()
    {
        var c = Case(CaseMode.Check);
        static DiagActual Diag() => new("E3001", "TYPE-ASSIGN-001", "error", 2);

        var extra = Compare.JudgeCheck(c, [Diag(), Diag()]);
        Assert.False(extra.Ok);
        Assert.Contains("多E3001|TYPE-ASSIGN-001|error|2×1", extra.Note);

        var shifted = Compare.JudgeCheck(c, [Diag() with { Line = 3 }]);
        Assert.False(shifted.Ok);
        Assert.Contains("缺E3001|TYPE-ASSIGN-001|error|2×1", shifted.Note);
        Assert.Contains("多E3001|TYPE-ASSIGN-001|error|3×1", shifted.Note);

        Assert.True(Compare.JudgeCheck(c, [Diag()]).Ok);
        // message 与列号允许漂移：比较键不含它们（DiagActual 无此字段即证）
    }

    /// <summary>假执行器驱动 IRunRoute 注入点：trait 面向未来解释器/后端。</summary>
    [Fact]
    public void Run_route_interface_injects_executor()
    {
        var fake = new FakeRoute(Routes.InterpIr, OkOutput());
        var c = Case(CaseMode.Run);

        var outp = fake.Run(".");
        Assert.Equal(OkOutput(), outp.Output);
        Assert.True(Compare.JudgeRun(c, fake.Name, fake.Run(".")).Ok);
    }

    private sealed class FakeRoute(string name, RouteOutput output) : IRunRoute
    {
        public string Name { get; } = name;

        public RouteResult Run(string caseDir) => RouteResult.Ok(output);
    }
}
