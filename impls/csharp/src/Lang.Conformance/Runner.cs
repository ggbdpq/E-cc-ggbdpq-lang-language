namespace Lang.Conformance;

/// <summary>
/// 套件 runner（specs/runner-contract.md 五动词中 run/check 的判定面）。
/// 本波为<b>注入骨架</b>：路由实现方（AST/IR 解释器、→Go/→JS 产物执行）在未来波次接入
/// <see cref="IRunRoute"/>；本文件只冻结「扫描 → 逐 case 判定 → 汇总 ConformanceReport」
/// 的管线与三态判定语义。check 侧诊断源以委托注入（未来接语义检查器）。
/// </summary>
public static class SuiteRunner
{
    /// <summary>check 模式诊断源：输入 case 目录，输出实际诊断投影（缺注入时 check 判败并注明）。</summary>
    public delegate IReadOnlyList<DiagActual> CheckDiagnostics(string caseDir);

    /// <summary>跑全套件：路由表须与 <see cref="Routes.RunRoutes"/> 名字逐位平行。</summary>
    public static ConformanceReport RunWithRoutes(
        IReadOnlyList<ScannedCase> cases,
        IReadOnlyList<IRunRoute> routes,
        CheckDiagnostics? checkDiagnostics = null)
    {
        if (routes.Count != Routes.RunRoutes.Count
            || routes.Zip(Routes.RunRoutes, (r, name) => r.Name == name).Any(match => !match))
        {
            throw new ArgumentException(
                $"路由表须与 RunRoutes 逐位平行：期望 [{string.Join(", ", Routes.RunRoutes)}]");
        }

        var verdicts = new List<CaseVerdict>(cases.Count);
        foreach (var scanned in cases)
        {
            var c = scanned.Case;
            switch (c.Mode)
            {
                case CaseMode.Run:
                    var routesVerdict = new List<RouteVerdict>(routes.Count);
                    foreach (var route in routes)
                    {
                        var outcome = route.Run(scanned.Dir);
                        var result = Compare.JudgeRun(c, route.Name, outcome);
                        routesVerdict.Add(new RouteVerdict
                        {
                            Route = route.Name,
                            Outcome = result.Skipped ? RouteOutcomeKind.Skipped
                                : result.Ok ? RouteOutcomeKind.Passed
                                : RouteOutcomeKind.Failed,
                            Note = result.Note,
                        });
                    }
                    verdicts.Add(new CaseVerdict
                    {
                        Category = c.Category,
                        Name = c.Name,
                        Mode = c.Mode,
                        Routes = routesVerdict,
                    });
                    break;
                case CaseMode.Check:
                    var diags = checkDiagnostics?.Invoke(scanned.Dir);
                    var checkResult = diags is null
                        ? new CaseResult(c.Name, c.Category, c.Mode, Ok: false, Skipped: false, Note: "check 诊断源未注入")
                        : Compare.JudgeCheck(c, diags);
                    verdicts.Add(new CaseVerdict
                    {
                        Category = c.Category,
                        Name = c.Name,
                        Mode = c.Mode,
                        Check = new CheckVerdict { Ok = checkResult.Ok, Note = checkResult.Note },
                    });
                    break;
                default:
                    throw new InvalidOperationException("CaseMode 变体未穷尽");
            }
        }
        return new ConformanceReport { Cases = verdicts };
    }
}
