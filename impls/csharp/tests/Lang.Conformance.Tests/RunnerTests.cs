using Lang.Conformance;
using Xunit;

namespace Lang.Conformance.Tests;

/// <summary>套件 runner 骨架：扫描 → 逐 case 判定 → 汇总（假路由 + 假诊断源注入）。</summary>
public class RunnerTests
{
    private sealed class FakeRoute(string name, bool fail = false, bool skip = false) : IRunRoute
    {
        public string Name { get; } = name;

        public RouteResult Run(string caseDir) => skip
            ? RouteResult.Skip("宿主缺工具链")
            : fail ? RouteResult.Fail("执行失败")
            : RouteResult.Ok(new RouteOutput("ok\n", "", 0));
    }

    private static ScannedCase ScanOf(ConformanceCase c) => new(c, Path.Combine("suite", c.Category, c.Name));

    private static ConformanceCase RunCase(string name) => new(
        name, "types", CaseMode.Run, [],
        new CaseExpect(0, "ok\n", "", []));

    private static ConformanceCase CheckCase(string name) => new(
        name, "diagnostics", CaseMode.Check, [],
        new CaseExpect(0, "", "", [new DiagExpect("E2002", "NAME-UNDEF-004", "error", 2)]));

    [Fact]
    public void Judges_all_cases_and_aggregates()
    {
        var cases = new[] { ScanOf(RunCase("r1")), ScanOf(RunCase("r2")), ScanOf(CheckCase("c1")) };
        var routes = new IRunRoute[]
        {
            new FakeRoute(Routes.InterpAst),
            new FakeRoute(Routes.InterpIr),
            new FakeRoute(Routes.Go),
            new FakeRoute(Routes.Js),
        };

        var report = SuiteRunner.RunWithRoutes(cases, routes, _ => [new DiagActual("E2002", "NAME-UNDEF-004", "error", 2)]);

        Assert.Equal(3, report.Cases.Count);
        Assert.True(report.IsFullyCovered());
        Assert.Equal(2, report.RouteTotal(Routes.InterpAst).Passed);
        Assert.Equal(1, report.CheckTotal().Passed);
        // run case 无 check 判定；check case 无路由判定
        Assert.Null(report.Cases[0].Check);
        Assert.Empty(report.Cases[2].Routes);
    }

    [Fact]
    public void Route_failures_and_skips_land_in_distinct_cells()
    {
        var cases = new[] { ScanOf(RunCase("r1")) };
        var routes = new IRunRoute[]
        {
            new FakeRoute(Routes.InterpAst),
            new FakeRoute(Routes.InterpIr, fail: true),
            new FakeRoute(Routes.Go, skip: true),
            new FakeRoute(Routes.Js),
        };

        var report = SuiteRunner.RunWithRoutes(cases, routes);

        Assert.Equal(1, report.RouteTotal(Routes.InterpAst).Passed);
        Assert.Equal(1, report.RouteTotal(Routes.InterpIr).Failed);
        Assert.Equal(1, report.RouteTotal(Routes.Go).Skipped);
        Assert.Equal(0, report.RouteTotal(Routes.Go).Passed);
        Assert.Contains("宿主缺工具链", report.Cases[0].Routes.Single(v => v.Route == Routes.Go).Note);
    }

    [Fact]
    public void Check_without_injected_source_fails_honestly()
    {
        var cases = new[] { ScanOf(CheckCase("c1")) };
        var routes = new IRunRoute[]
        {
            new FakeRoute(Routes.InterpAst),
            new FakeRoute(Routes.InterpIr),
            new FakeRoute(Routes.Go),
            new FakeRoute(Routes.Js),
        };

        var report = SuiteRunner.RunWithRoutes(cases, routes, checkDiagnostics: null);

        Assert.False(report.Cases[0].Check!.Ok);
        Assert.Contains("未注入", report.Cases[0].Check!.Note);
    }

    [Fact]
    public void Rejects_route_table_not_parallel_to_run_routes()
    {
        var cases = Array.Empty<ScannedCase>();
        var routes = new IRunRoute[]
        {
            new FakeRoute(Routes.InterpAst),
            new FakeRoute(Routes.InterpIr),
            new FakeRoute(Routes.Go),
            new FakeRoute("wrong-js"),
        };

        Assert.Throws<ArgumentException>(
            () => SuiteRunner.RunWithRoutes(cases, routes));
    }
}
