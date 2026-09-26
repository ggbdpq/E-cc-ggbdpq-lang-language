using Lang.Conformance;
using Xunit;
using Xunit.Abstractions;

namespace Lang.Conformance.Tests;

/// <summary>
/// 151×4 总账矩阵 + <c>strict_all_routes_green</c> 收官门禁（runner-contract.md run/check 动词；
/// 对齐 lang-rs 第四波 conformance_matrix.rs 的测试结构）。
///
/// <para>主测试常跑：套件形状（120 run + 31 check = 151）、覆盖自检（防路由漏挂静默漏跑）、
/// AST 参考路 120/120（参考路失败即实现坏，不在登记域）、check 31/31、「run 失败格 ⊆
/// <see cref="RegisteredBlockers"/>」回归守卫 + 台账格数守卫（上游修复落地后守卫红，
/// 提示收缩台账；全绿时台账为空集）。</para>
///
/// <para><see cref="Strict_all_routes_green"/>：四路 120p/0f/0s + check 31p/0f 的 511 格
/// 全绿判定——三格独立计数、Skipped ≠ 通过（工具缺失导致任何一格 Skipped 即红——L6 门禁
/// 要求零 Skipped、零 Failed），是 conformance 收官判据。本次交付四路实测全绿，作为常跑
/// 门禁注册；若未来出现新红格，按 lang-rs 同款纪律改挂 Skip 并登记阻塞台账。</para>
/// </summary>
public class ConformanceMatrixTests(ITestOutputHelper output)
{
    /// <summary>
    /// 已登记阻塞台账（(category, name, route) 键集；格式 <c>category/name|route</c>）。
    /// 只收「根因在别的 crate、已定性登记 status 阻塞区」的失败格；A6 crate 自身缺陷修复后
    /// 不得进台账。全绿时 = 空集（本次交付实测 511 格全绿，台账从空开始）。
    /// </summary>
    private static readonly IReadOnlySet<string> RegisteredBlockers = new HashSet<string>();

    private static ConformanceReport Report => MatrixSuite.Report;

    [Fact]
    public void Suite_shape_is_120_run_plus_31_check_and_fully_covered()
    {
        var report = Report;
        Assert.Equal(151, report.Cases.Count);
        Assert.Equal(120, report.RunCaseTotal());
        Assert.Equal(31, report.CheckCaseTotal());
        Assert.True(report.IsFullyCovered(), "覆盖自检失败：run case 的路由判定与 RunRoutes 不平行或 check 缺判定");
    }

    /// <summary>AST 参考路 120/120（复现 A4 基线；参考路是差分的锚，红即实现坏，不设台账）。</summary>
    [Fact]
    public void Ast_reference_route_is_120_green()
    {
        var ast = Report.RouteTotal(Routes.InterpAst);
        Assert.Equal(120, ast.Passed);
        Assert.Equal(0, ast.Failed);
        Assert.Equal(0, ast.Skipped);
    }

    /// <summary>check 31/31：LangSemantic 直产诊断与套件真值多重集零漂移。</summary>
    [Fact]
    public void Check_suite_is_31_green()
    {
        var check = Report.CheckTotal();
        Assert.Equal(31, check.Passed);
        Assert.Equal(0, check.Failed);
    }

    /// <summary>回归守卫：run 失败格 ⊆ 已登记阻塞台账，且格数与台账一致（防台账腐化：
    /// 上游修复落地 → 实际失败减少 → 本断言红提示收缩台账；新红格 → 未登记断言红）。</summary>
    [Fact]
    public void Run_failures_stay_within_registered_blockers()
    {
        var report = Report;
        var failures = MatrixSuite.RunFailures(report);
        var skips = MatrixSuite.RunSkips(report);
        foreach (var (category, name, route) in failures)
        {
            Assert.True(RegisteredBlockers.Contains($"{category}/{name}|{route}"),
                $"未登记的失败格: {category}/{name} [{route}]（先定性归属再决定修复或登记）");
        }

        Assert.Equal(RegisteredBlockers.Count, failures.Count);
        // skip 门（extern FFI / 宿主 http.* / 静态诊断非空）与工具缺失在当前语料 + 本机
        // 均应为 0；出现即先定性再处置，不允许静默溜过
        Assert.Empty(skips);
        output.WriteLine(MatrixSuite.Render(report));
        output.WriteLine(MatrixSuite.RenderFailures(report));
    }

    /// <summary>strict 收官门禁：四路 120p/0f/0s + check 31p/0f = 511 格全绿（三格独立、
    /// Skipped ≠ 通过、覆盖自检防漏挂）。L6 验收判据。</summary>
    [Fact]
    public void Strict_all_routes_green()
    {
        var report = Report;
        Assert.True(report.IsFullyCovered());
        foreach (var route in Routes.RunRoutes)
        {
            var cell = report.RouteTotal(route);
            Assert.True(cell.Failed == 0 && cell.Skipped == 0,
                $"{route}: {cell.Failed} failed / {cell.Skipped} skipped（门禁要求 120p/0f/0s）");
            Assert.Equal(120, cell.Passed);
        }

        var check = report.CheckTotal();
        Assert.Equal((31, 0), (check.Passed, check.Failed));
    }

    [Fact]
    public void Print_matrix_for_the_record()
    {
        var report = Report;
        output.WriteLine(MatrixSuite.Render(report));
        output.WriteLine($"skipped 格总数: {MatrixSuite.RunSkips(report).Count}");
    }
}

/// <summary>矩阵运行共享夹具：全套件一次执行、多测试共享（480 路执行不重复付费）。
/// Lazy 默认线程安全；xunit 同类内串行、跨类共享此单例。</summary>
public static class MatrixSuite
{
    private static readonly Lazy<ConformanceReport> LazyReport = new(RunOnce);

    public static ConformanceReport Report => LazyReport.Value;

    private static ConformanceReport RunOnce()
    {
        var root = SuiteScanner.DefaultSuiteRoot();
        var ok = SuiteScanner.TryScan(root, out var cases, out var failures);
        Assert.True(ok, string.Join("; ", failures.Select(f => $"{f.Path}: {f.Reason}")));
        return SuiteRunner.RunWithRoutes(
            cases,
            new IRunRoute[] { new InterpAstRoute(), new InterpIrRoute(), new GoRoute(), new JsRoute() },
            CheckSources.FromAnalysis);
    }

    /// <summary>分类×路由矩阵文本（留档用；分类字典序、路由 = RunRoutes 冻结序 + check 列）。</summary>
    public static string Render(ConformanceReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"| category | {Routes.InterpAst} | {Routes.InterpIr} | {Routes.Go} | {Routes.Js} | check |");
        sb.AppendLine("|---");
        foreach (var cat in report.Categories())
        {
            var cells = Routes.RunRoutes.Select(r => CellText(report.CellFor(cat, r)));
            sb.AppendLine($"| {cat} | {string.Join(" | ", cells)} | {CellText(report.CheckCell(cat))} |");
        }

        var totals = Routes.RunRoutes.Select(r => CellText(report.RouteTotal(r)));
        sb.AppendLine($"| 合计 | {string.Join(" | ", totals)} | {CellText(report.CheckTotal())} |");
        return sb.ToString();
    }

    /// <summary>失败/skip 格明细（定性证据：Note 带期望 vs 实际）。</summary>
    public static string RenderFailures(ConformanceReport report)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in report.Cases)
        {
            foreach (var v in c.Routes.Where(v => v.Outcome != RouteOutcomeKind.Passed))
            {
                sb.AppendLine($"{c.Category}/{c.Name} [{v.Route}] {v.Outcome}: {v.Note}");
            }

            if (c.Check is { Ok: false } chk)
            {
                sb.AppendLine($"{c.Category}/{c.Name} [check] {chk.Note}");
            }
        }

        return sb.ToString();
    }

    public static string CellText(Cell c) => c.Failed == 0 && c.Skipped == 0
        ? $"{c.Passed}p"
        : c.Skipped == 0 ? $"{c.Passed}p/{c.Failed}f" : $"{c.Passed}p/{c.Failed}f/{c.Skipped}s";

    /// <summary>run 模式全部失败格键（category, name, route）。</summary>
    public static List<(string Category, string Name, string Route)> RunFailures(ConformanceReport report) =>
        [.. report.Cases
            .Where(c => c.Mode == CaseMode.Run)
            .SelectMany(c => c.Routes.Where(v => v.Outcome == RouteOutcomeKind.Failed)
                .Select(v => (c.Category, c.Name, v.Route)))];

    /// <summary>run 模式全部 skip 格键。</summary>
    public static List<(string Category, string Name, string Route)> RunSkips(ConformanceReport report) =>
        [.. report.Cases
            .Where(c => c.Mode == CaseMode.Run)
            .SelectMany(c => c.Routes.Where(v => v.Outcome == RouteOutcomeKind.Skipped)
                .Select(v => (c.Category, c.Name, v.Route)))];
}
