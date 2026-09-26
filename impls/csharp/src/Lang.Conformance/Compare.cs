namespace Lang.Conformance;

/// <summary>
/// Run/Check case 的比较策略（specs/runner-contract.md「run/check/通用约束」节）。
/// run 模式 = 四条执行路径（AST 解释 / IR 解释 / →Go 产物 / →JS 产物）各自 stdout 与期望
/// <b>逐字节</b>一致、exitCode 0、stderr 空；check 模式 = 静态诊断的
/// <c>code|ruleId|severity|line</c> <b>多重集</b>相等（message 与列号允许漂移）。
/// 执行器是注入点：<see cref="IRunRoute"/> 的实现方在未来接入解释器与双后端
/// （本波不接解释器，只定死比较骨架与判定语义，测试用假执行器驱动）。
/// </summary>

/// <summary>四条 run 路径的规范名（runner-contract：解释器×2 / →Go / →JS；冻结，出现在 note/报告里）。</summary>
public static class Routes
{
    public const string InterpAst = "interp-ast";
    public const string InterpIr = "interp-ir";
    public const string Go = "go";
    public const string Js = "js";

    /// <summary>run 模式的全部路径（顺序即判定顺序；缺工具链的路径按 skipped 处理，不谎报通过）。</summary>
    public static readonly IReadOnlyList<string> RunRoutes = [InterpAst, InterpIr, Go, Js];
}

/// <summary>单路执行输出。</summary>
public sealed record RouteOutput(string Stdout, string Stderr, int ExitCode);

/// <summary>单路执行的失败形态类别：环境性跳过与真实执行失败必须区分。</summary>
public enum RouteErrorKind
{
    /// <summary>工具链缺失等环境原因——skipped，不计入通过（runner-contract 通用约束 5）。</summary>
    Skipped,

    /// <summary>执行/编译失败——直接判败。</summary>
    Failed,
}

/// <summary>单路执行的失败（kind + 原因）。</summary>
public sealed record RouteError(RouteErrorKind Kind, string Reason);

/// <summary>单路执行结果（成功输出或受控失败；C# 无和类型，二者恰一非空）。</summary>
public sealed record RouteResult(RouteOutput? Output, RouteError? Error)
{
    public static RouteResult Ok(RouteOutput output) => new(output, null);
    public static RouteResult Skip(string reason) => new(null, new RouteError(RouteErrorKind.Skipped, reason));
    public static RouteResult Fail(string reason) => new(null, new RouteError(RouteErrorKind.Failed, reason));
}

/// <summary>一条执行路径（解释器或后端执行器实现本接口后即可接入判定）。</summary>
public interface IRunRoute
{
    /// <summary>路由名（<see cref="Routes.RunRoutes"/> 冻结名之一）。</summary>
    string Name { get; }

    /// <summary>执行一个 case 目录（目录即包，入口 main.lang-go）。</summary>
    RouteResult Run(string caseDir);
}

/// <summary>单路 run 比对差异（field ∈ exitCode/stdout/stderr；逐字节语义）。</summary>
public sealed record RunDiff(string Field, string Expected, string Actual);

/// <summary>一条诊断的比较键投影（期望侧由 manifest <see cref="DiagExpect"/> 转换而来）。
/// key 不含 message 与列号——那是 diagnostics spec 既定的漂移面。</summary>
public sealed record DiagActual(string Code, string RuleId, string Severity, uint Line)
{
    public string Key() => $"{Code}|{RuleId}|{Severity}|{Line}";
}

/// <summary>check 多重集比较结果：缺失/多出各按 <c>code|rule|severity|line×n</c> 报告。</summary>
public sealed record CheckDiff(IReadOnlyList<string> Missing, IReadOnlyList<string> Extra)
{
    public bool IsEmpty => Missing.Count == 0 && Extra.Count == 0;
}

/// <summary>一个 case 的判定结果（与三实现证据同构：ok/skipped/note）。</summary>
public sealed record CaseResult(string Name, string Category, CaseMode Mode, bool Ok, bool Skipped, string Note);

public static class Compare
{
    /// <summary>单路比对：返回全部差异，空 = 通过。</summary>
    public static IReadOnlyList<RunDiff> Run(CaseExpect expect, RouteOutput got)
    {
        var diffs = new List<RunDiff>();
        if (got.ExitCode != expect.ExitCode)
        {
            diffs.Add(new RunDiff("exitCode", $"{expect.ExitCode}", $"{got.ExitCode}"));
        }
        if (got.Stdout != expect.Stdout)
        {
            diffs.Add(new RunDiff("stdout", expect.Stdout, got.Stdout));
        }
        if (got.Stderr != expect.Stderr)
        {
            diffs.Add(new RunDiff("stderr", expect.Stderr, got.Stderr));
        }
        return diffs;
    }

    /// <summary>诊断多重集比对（与三实现证据同口径：key 计数，缺/多分别清点后按 key 序）。</summary>
    public static CheckDiff Check(IReadOnlyList<DiagActual> expected, IReadOnlyList<DiagActual> actual)
    {
        var want = CountKeys(expected);
        var got = CountKeys(actual);
        var missing = new List<string>();
        var extra = new List<string>();
        foreach (var (key, n) in want)
        {
            var g = got.TryGetValue(key, out var v) ? v : 0;
            if (g < n)
            {
                missing.Add($"{key}×{n - g}");
            }
        }
        foreach (var (key, n) in got)
        {
            var w = want.TryGetValue(key, out var v) ? v : 0;
            if (w < n)
            {
                extra.Add($"{key}×{n - w}");
            }
        }
        return new CheckDiff(missing, extra);
    }

    /// <summary>判定 run 模式的单路结果。多路 case 由调用方对每条 <see cref="Routes.RunRoutes"/>
    /// 各调一次，任一路失败即整体失败（四路输出本就要求逐字节一致）。</summary>
    public static CaseResult JudgeRun(ConformanceCase c, string route, RouteResult outcome) =>
        outcome switch
        {
            { Error: { Kind: RouteErrorKind.Skipped } } e => Fail(c, skipped: true, $"{route}: {e.Error.Reason}"),
            { Error: { } e } => Fail(c, skipped: false, $"{route}: {e.Reason}"),
            { Output: { } outp } => Run(c, route, outp),
            _ => throw new InvalidOperationException("RouteResult 二元形态非法"),
        };

    /// <summary>判定 check 模式的诊断多重集（诊断来自注入的检查器）。</summary>
    public static CaseResult JudgeCheck(ConformanceCase c, IReadOnlyList<DiagActual> actual)
    {
        var expected = c.Expect.Diagnostics
            .Select(d => new DiagActual(d.Code, d.RuleId, d.Severity, d.Line))
            .ToList();
        var diff = Check(expected, actual);
        return diff.IsEmpty
            ? Pass(c)
            : Fail(c, skipped: false, $"诊断不匹配 缺{string.Join(",", diff.Missing)} 多{string.Join(",", diff.Extra)}");
    }

    private static CaseResult Run(ConformanceCase c, string route, RouteOutput outp)
    {
        var diffs = Compare.Run(c.Expect, outp);
        return diffs.Count == 0
            ? Pass(c)
            : Fail(c, skipped: false, $"{route}: {Render(diffs)}");
    }

    private static CaseResult Pass(ConformanceCase c) => new(c.Name, c.Category, c.Mode, Ok: true, Skipped: false, Note: "");

    private static CaseResult Fail(ConformanceCase c, bool skipped, string note) =>
        new(c.Name, c.Category, c.Mode, Ok: false, Skipped: skipped, Note: note);

    private static string Render(IReadOnlyList<RunDiff> diffs) => string.Join("; ", diffs.Select(d => d.Field switch
    {
        "exitCode" => $"exitCode 期望 {d.Expected} 实际 {d.Actual}",
        "stdout" => $"stdout 不一致 期望 {d.Expected} 实际 {d.Actual}",
        _ => $"stderr 期望 {d.Expected} 实际 {d.Actual}",
    }));

    private static SortedDictionary<string, int> CountKeys(IReadOnlyList<DiagActual> diags)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in diags)
        {
            counts[d.Key()] = counts.TryGetValue(d.Key(), out var n) ? n + 1 : 1;
        }
        return counts;
    }
}
