using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;

/// <summary>
/// conformance/diagnostics 全部 check 类 case 的夹具测试。
/// 期望值直接读 case.yaml（code/severity/line 逐项），与 Analyze 的实际产出做
/// 多重集合比对——期望来自 conformance 契约面，禁止拿实现当期望。
/// 诊断无锚（line 0）对应 PrimarySpan = null。夹具文件只读。
/// </summary>
public class ConformanceDiagnosticsTests
{
    private sealed record DiagExpect(string Code, string? RuleId, string Severity, uint Line);

    private sealed record CaseYaml(string Name, string Mode, List<DiagExpect> Diagnostics);

    private static string? Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "conformance", "diagnostics");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>case.yaml 子集的最小读取器（缩进两格的固定形状，字段 name/mode/
    /// expect.diagnostics[code/ruleId/severity/line]）。不引入 YAML 依赖。</summary>
    private static CaseYaml ReadCase(string dir, string name)
    {
        var lines = File.ReadAllLines(Path.Combine(dir, "case.yaml"));
        string mode = "";
        var diags = new List<DiagExpect>();
        DiagExpect? cur = null;
        var inDiags = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("mode:"))
            {
                mode = line["mode:".Length..].Trim();
            }
            else if (line.Trim() == "diagnostics:")
            {
                inDiags = true;
            }
            else if (inDiags && raw.StartsWith("    - "))
            {
                cur = null;
                var body = raw["    - ".Length..].Trim();
                if (body.StartsWith("code:"))
                {
                    cur = new DiagExpect(body["code:".Length..].Trim(), null, "error", 0);
                    diags.Add(cur);
                }
            }
            else if (inDiags && cur is not null && raw.StartsWith("      "))
            {
                var body = line.Trim();
                if (body.StartsWith("ruleId:"))
                {
                    var rule = body["ruleId:".Length..].Trim();
                    cur = diags[^1] with { RuleId = rule };
                    diags[^1] = cur;
                }
                else if (body.StartsWith("severity:"))
                {
                    var sev = body["severity:".Length..].Trim();
                    cur = diags[^1] with { Severity = sev };
                    diags[^1] = cur;
                }
                else if (body.StartsWith("line:"))
                {
                    var ln = uint.Parse(body["line:".Length..].Trim());
                    cur = diags[^1] with { Line = ln };
                    diags[^1] = cur;
                }
            }
        }
        return new CaseYaml(name, mode, diags);
    }

    private static (List<(string, uint)> Got, List<(string, uint)> Want, IReadOnlyList<Diagnostic> AllDiags) RunCase(
        string dir, string name)
    {
        var caseYaml = ReadCase(dir, name);
        Assert.Equal("check", caseYaml.Mode);
        var src = File.ReadAllText(Path.Combine(dir, "main.lang-go"));
        var source = new SourceId(0);
        var text = new SourceText($"{name}/main.lang-go", src);
        var parsed = Parser.Parse(text, source);
        Assert.Null(parsed.Error);
        var analysis = LangSemantic.Analyze(parsed.Program, source);

        var got = analysis.Diags
            .Select(d => (d.Code.CodeText(), d.PrimarySpan?.Start.Line ?? 0))
            .OrderBy(x => x)
            .ToList();
        var want = caseYaml.Diagnostics
            .Select(d => (d.Code, d.Line))
            .OrderBy(x => x)
            .ToList();
        return (got, want, analysis.Diags);
    }

    public static IEnumerable<object[]> CaseNames()
    {
        var root = Root();
        if (root is null) return [];
        // 显式排序：目录枚举序不作为契约
        return Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "case.yaml")))
            .Select(Path.GetFileName)
            .Order()
            .Select(n => new object[] { n! });
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void CheckCaseConforms(string caseName)
    {
        var root = Root();
        Assert.NotNull(root);
        var dir = Path.Combine(root, caseName);
        var caseYaml = ReadCase(dir, caseName);
        var (got, want, allDiags) = RunCase(dir, caseName);
        Assert.True(
            got.SequenceEqual(want),
            $"{caseName}:\n  期望 [{string.Join(", ", want)}]\n  实际 [{string.Join(", ", got)}]");
        // ruleId 逐项核对（case.yaml 指定处）
        foreach (var d in caseYaml.Diagnostics.Where(d => d.RuleId is not null))
        {
            var found = allDiags.Any(g => g.Code.CodeText() == d.Code && g.RuleId.RuleText() == d.RuleId);
            Assert.True(found, $"{caseName}: 诊断 {d.Code} 的 ruleId 应为 {d.RuleId}");
        }
    }

    [Fact]
    public void CaseCountIsThirty()
    {
        Assert.Equal(30, CaseNames().Count());
    }
}
