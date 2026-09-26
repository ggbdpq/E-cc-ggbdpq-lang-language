using System.Text.Json;
using Lang.Conformance;
using Xunit;

namespace Lang.Conformance.Tests;

/// <summary>ConformanceReport 聚合矩阵 + JSON 形状 + 覆盖自检。</summary>
public class ReportTests
{
    private static CaseVerdict RunCase(string category, string name, params (string Route, RouteOutcomeKind Kind, string Note)[] routes) => new()
    {
        Category = category,
        Name = name,
        Mode = CaseMode.Run,
        Routes = [.. routes.Select(r => new RouteVerdict { Route = r.Route, Outcome = r.Kind, Note = r.Note })],
    };

    private static CaseVerdict CheckCase(string category, string name, bool ok, string note = "") => new()
    {
        Category = category,
        Name = name,
        Mode = CaseMode.Check,
        Check = new CheckVerdict { Ok = ok, Note = note },
    };

    [Fact]
    public void Aggregation_matrix_counts_three_states_independently()
    {
        var report = new ConformanceReport
        {
            Cases =
            [
                RunCase("types", "a",
                    (Routes.InterpAst, RouteOutcomeKind.Passed, ""),
                    (Routes.InterpIr, RouteOutcomeKind.Passed, ""),
                    (Routes.Go, RouteOutcomeKind.Failed, "go build 失败"),
                    (Routes.Js, RouteOutcomeKind.Skipped, "宿主缺 node")),
                RunCase("types", "b",
                    (Routes.InterpAst, RouteOutcomeKind.Passed, ""),
                    (Routes.InterpIr, RouteOutcomeKind.Passed, ""),
                    (Routes.Go, RouteOutcomeKind.Passed, ""),
                    (Routes.Js, RouteOutcomeKind.Passed, "")),
                RunCase("io", "c",
                    (Routes.InterpAst, RouteOutcomeKind.Passed, ""),
                    (Routes.InterpIr, RouteOutcomeKind.Passed, ""),
                    (Routes.Go, RouteOutcomeKind.Passed, ""),
                    (Routes.Js, RouteOutcomeKind.Passed, "")),
                CheckCase("diagnostics", "d", ok: true),
                CheckCase("diagnostics", "e", ok: false, note: "缺E2002|NAME-UNDEF-004|error|2×1"),
                CheckCase("types", "f", ok: true),
            ],
        };

        // 三格独立：skipped 不并入任何一侧
        Assert.Equal(new Cell { Passed = 2 }, report.CellFor("types", Routes.InterpAst));
        Assert.Equal(1, report.CellFor("types", Routes.Go).Failed);
        Assert.Equal(1, report.CellFor("types", Routes.Js).Skipped);
        Assert.Equal(1, report.CellFor("types", Routes.Js).Passed);
        Assert.Equal(0, report.CellFor("types", Routes.Js).Failed);

        Assert.Equal(3, report.RouteTotal(Routes.InterpAst).Passed);
        Assert.Equal(1, report.RouteTotal(Routes.Go).Failed);
        // check 不计入路由格；run 不计入 check 格
        Assert.Equal(0, report.CellFor("diagnostics", Routes.InterpAst).Total);
        Assert.Equal(1, report.CheckCell("diagnostics").Passed);
        Assert.Equal(1, report.CheckCell("diagnostics").Failed);
        Assert.Equal(2, report.CheckTotal().Passed);
        Assert.Equal(1, report.CheckTotal().Failed);
        Assert.Equal(0, report.CheckTotal().Skipped);

        Assert.Equal(["diagnostics", "io", "types"], report.Categories());
        Assert.Equal(3, report.RunCaseTotal());
        Assert.Equal(3, report.CheckCaseTotal());
        Assert.True(report.IsFullyCovered());
    }

    [Fact]
    public void Coverage_self_check_detects_missing_route()
    {
        var report = new ConformanceReport
        {
            Cases =
            [
                RunCase("types", "a",
                    (Routes.InterpAst, RouteOutcomeKind.Passed, ""),
                    (Routes.InterpIr, RouteOutcomeKind.Passed, ""),
                    (Routes.Go, RouteOutcomeKind.Passed, "")), // js 路漏挂
            ],
        };

        Assert.False(report.IsFullyCovered());
    }

    [Fact]
    public void Serializes_to_stable_json_shape()
    {
        // JSON 形状锁定：camelCase 键 + 声明序 + mode/outcome 字符串枚举（CLI/parity-report 消费面）。
        var report = new ConformanceReport
        {
            Cases =
            [
                RunCase("types", "demo", (Routes.Go, RouteOutcomeKind.Skipped, "宿主缺 go")),
                CheckCase("diagnostics", "amp", ok: false, note: "缺E2002×1"),
            ],
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = false,
        });
        using var doc = JsonDocument.Parse(json);
        var case0 = doc.RootElement.GetProperty("cases")[0];

        // 属性序 = category, name, mode, routes, check
        Assert.Equal(["category", "name", "mode", "routes", "check"],
            case0.EnumerateObject().Select(p => p.Name));
        Assert.Equal("types", case0.GetProperty("category").GetString());
        Assert.Equal("run", case0.GetProperty("mode").GetString());
        var route0 = case0.GetProperty("routes")[0];
        Assert.Equal(["route", "outcome", "note"], route0.EnumerateObject().Select(p => p.Name));
        Assert.Equal("go", route0.GetProperty("route").GetString());
        Assert.Equal("skipped", route0.GetProperty("outcome").GetString());
        Assert.Equal("宿主缺 go", route0.GetProperty("note").GetString());
        Assert.Equal(0, case0.GetProperty("check").ValueKind switch
        {
            JsonValueKind.Null => 0,
            _ => throw new InvalidOperationException("run 模式 check 应为 null"),
        });

        var case1 = doc.RootElement.GetProperty("cases")[1];
        Assert.Equal("check", case1.GetProperty("mode").GetString());
        Assert.Empty(case1.GetProperty("routes").EnumerateArray());
        Assert.False(case1.GetProperty("check").GetProperty("ok").GetBoolean());
        Assert.Equal("缺E2002×1", case1.GetProperty("check").GetProperty("note").GetString());
    }
}
