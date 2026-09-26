using System.Text.Json.Serialization;

namespace Lang.Conformance;

/// <summary>
/// ConformanceReport —— 全套件判定总账（分类×路由矩阵 + 逐 case 明细；System.Text.Json 可序列化）。
/// 消费面：lang-cli 的 conformance 输出与 parity-report 都读本结构。诚实纪律落在类型里：
/// <see cref="RouteOutcomeKind.Skipped"/> 与 <see cref="RouteOutcomeKind.Passed"/> 是不同变体、
/// 分别计数（<see cref="Cell"/> 三格独立），skipped 不计入通过也不谎报失败
/// （runner-contract 通用约束 5）。
/// 矩阵口径：run 模式每 case 对四条路由（<see cref="Routes.RunRoutes"/>）各一个判定；
/// check 模式每 case 一个语义判定（诊断多重集），无路由维度——汇总时 check 作为独立列呈现。
/// </summary>

/// <summary>路由/语义判定的三态（Skipped ≠ 通过 ≠ 失败；JSON 序列化为小写字符串）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RouteOutcomeKind
{
    [JsonStringEnumMemberName("passed")]
    Passed,

    /// <summary>真实失败（比较差异 / 执行失败），Note 带证据。</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>环境性跳过（工具链缺失等），Note 注明原因。</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

/// <summary>run 模式单路判定。</summary>
public sealed record RouteVerdict
{
    [JsonPropertyName("route")]
    public required string Route { get; init; }

    [JsonPropertyName("outcome")]
    public required RouteOutcomeKind Outcome { get; init; }

    [JsonPropertyName("note")]
    public string Note { get; init; } = "";
}

/// <summary>check 模式判定（诊断多重集比对结果）。</summary>
public sealed record CheckVerdict
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    /// <summary>失败时为「缺/多」清点（<c>code|rule|severity|line×n</c>），通过为空。</summary>
    [JsonPropertyName("note")]
    public string Note { get; init; } = "";
}

/// <summary>单 case 判定明细。</summary>
public sealed record CaseVerdict
{
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("mode")]
    public required CaseMode Mode { get; init; }

    /// <summary>run 模式：与 <see cref="Routes.RunRoutes"/> 顺序平行的四路判定；check 模式恒空。</summary>
    [JsonPropertyName("routes")]
    public IReadOnlyList<RouteVerdict> Routes { get; init; } = [];

    /// <summary>check 模式判定；run 模式恒 null。</summary>
    [JsonPropertyName("check")]
    public CheckVerdict? Check { get; init; }
}

/// <summary>聚合计数格（passed/failed/skipped 三格独立；skipped 不并入任何一侧）。</summary>
public sealed record Cell
{
    [JsonPropertyName("passed")]
    public int Passed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; init; }

    public int Total => Passed + Failed + Skipped;

    public static Cell Of(RouteOutcomeKind kind) => kind switch
    {
        RouteOutcomeKind.Passed => new Cell { Passed = 1 },
        RouteOutcomeKind.Failed => new Cell { Failed = 1 },
        RouteOutcomeKind.Skipped => new Cell { Skipped = 1 },
        _ => throw new InvalidOperationException("RouteOutcomeKind 变体未穷尽"),
    };

    public static Cell operator +(Cell a, Cell b) => new()
    {
        Passed = a.Passed + b.Passed,
        Failed = a.Failed + b.Failed,
        Skipped = a.Skipped + b.Skipped,
    };
}

/// <summary>全套件判定总账。cases 保持扫描序 = (category, name) 确定序。</summary>
public sealed record ConformanceReport
{
    [JsonPropertyName("cases")]
    public required IReadOnlyList<CaseVerdict> Cases { get; init; }

    /// <summary>套件出现的分类清单（字典序，去重）。</summary>
    public IReadOnlyList<string> Categories() =>
        [.. Cases.Select(c => c.Category).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal)];

    /// <summary>run 模式某分类 × 某路由的计数格（check 模式不计入路由格）。
    /// 命名注：C# 中与 <see cref="Cell"/> 类型同名的方法不可行，查询面叫 CellFor（对齐 rust 的 cell）。</summary>
    public Cell CellFor(string category, string route) =>
        Aggregate(c => c.Category == category && c.Mode == CaseMode.Run, v => v.Route == route);

    /// <summary>某路由跨全分类汇总（run 模式）。</summary>
    public Cell RouteTotal(string route) =>
        Aggregate(c => c.Mode == CaseMode.Run, v => v.Route == route);

    /// <summary>check 语义某分类的计数格（ok → passed，否则 failed；check 无 skipped）。</summary>
    public Cell CheckCell(string category) =>
        Aggregate(c => c.Category == category && c.Mode == CaseMode.Check, null, check: true);

    /// <summary>check 语义跨全分类汇总。</summary>
    public Cell CheckTotal() =>
        Aggregate(c => c.Mode == CaseMode.Check, null, check: true);

    /// <summary>run 模式总 case 数。</summary>
    public int RunCaseTotal() => Cases.Count(c => c.Mode == CaseMode.Run);

    /// <summary>check 模式总 case 数。</summary>
    public int CheckCaseTotal() => Cases.Count(c => c.Mode == CaseMode.Check);

    /// <summary>
    /// 覆盖自检：run case 的路由判定与 <see cref="Routes.RunRoutes"/> 逐位平行、
    /// check case 恒有判定（防路由漏挂静默漏跑——漏挂的路由不满，此处即 false）。
    /// </summary>
    public bool IsFullyCovered() =>
        Cases.Where(c => c.Mode == CaseMode.Run)
            .All(c => c.Routes.Select(v => v.Route).SequenceEqual(Routes.RunRoutes))
        && Cases.Where(c => c.Mode == CaseMode.Check).All(c => c.Check is not null);

    private Cell Aggregate(Func<CaseVerdict, bool> casePred, Func<RouteVerdict, bool>? routeMatch, bool check = false)
    {
        var total = new Cell();
        foreach (var c in Cases.Where(casePred))
        {
            if (check)
            {
                if (c.Check is { } cv)
                {
                    total += new Cell { Passed = cv.Ok ? 1 : 0, Failed = cv.Ok ? 0 : 1 };
                }
            }
            else if (routeMatch is not null)
            {
                foreach (var v in c.Routes.Where(routeMatch))
                {
                    total += Cell.Of(v.Outcome);
                }
            }
        }
        return total;
    }
}
