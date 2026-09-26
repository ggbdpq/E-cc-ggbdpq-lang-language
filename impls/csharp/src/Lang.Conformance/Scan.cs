namespace Lang.Conformance;

/// <summary>
/// conformance 套件目录扫描器（conformance/README.md「布局」节）。
/// 布局：<c>&lt;root&gt;/&lt;category&gt;/&lt;case-name&gt;/case.yaml</c>（入口 main.lang-go 同目录）。
/// 扫描规则：root 下每个目录是一个 category，category 下每个目录必须含 case.yaml
/// （缺文件视为套件错误，不是静默跳过）；解析失败收集全部失败一次性报告；
/// manifest 声明的 category/name 必须与目录位置一致（防 case 搬家后失配）。
/// 输出按 (category, case 目录名) 排序——确定性，禁文件系统序外泄。
/// </summary>

/// <summary>扫描出的一个 case：解析结果 + 所在目录（执行器用目录装载程序）。</summary>
public sealed record ScannedCase(ConformanceCase Case, string Dir);

/// <summary>一条扫描失败（目录缺 case.yaml / manifest 解析失败 / IO）。</summary>
public sealed record ScanFailure(string Path, string Reason);

public static class SuiteScanner
{
    /// <summary>扫描整个套件。任一失败则返回 false（收集全部失败，不只报第一个）。</summary>
    public static bool TryScan(
        string root,
        out IReadOnlyList<ScannedCase> cases,
        out IReadOnlyList<ScanFailure> failures)
    {
        var fail = new List<ScanFailure>();
        var found = new List<ScannedCase>();
        cases = found;
        failures = fail;

        if (!Directory.Exists(root))
        {
            fail.Add(new ScanFailure(root, "读取目录失败：套件根目录不存在"));
            return false;
        }

        foreach (var cat in OrderedDirs(root))
        {
            var catName = NameOf(cat);
            var caseDirs = OrderedDirs(cat).ToList();
            if (caseDirs.Count == 0)
            {
                fail.Add(new ScanFailure(cat, $"category {catName} 下没有任何 case 目录"));
                continue;
            }
            foreach (var caseDir in caseDirs)
            {
                var manifest = Path.Combine(caseDir, "case.yaml");
                if (!File.Exists(manifest))
                {
                    fail.Add(new ScanFailure(caseDir, "缺 case.yaml"));
                    continue;
                }
                string text;
                try
                {
                    text = File.ReadAllText(manifest);
                }
                catch (Exception e)
                {
                    fail.Add(new ScanFailure(manifest, e.Message));
                    continue;
                }
                ConformanceCase parsed;
                try
                {
                    parsed = ManifestParser.Parse(text);
                }
                catch (ManifestException e)
                {
                    fail.Add(new ScanFailure(manifest, e.Message));
                    continue;
                }
                if (parsed.Category != catName)
                {
                    fail.Add(new ScanFailure(manifest, $"manifest category {parsed.Category} 与目录名 {catName} 不一致"));
                    continue;
                }
                if (parsed.Name != NameOf(caseDir))
                {
                    fail.Add(new ScanFailure(manifest, $"manifest name {parsed.Name} 与目录名 {NameOf(caseDir)} 不一致"));
                    continue;
                }
                found.Add(new ScannedCase(parsed, caseDir));
            }
        }

        return fail.Count == 0;
    }

    /// <summary>仓库真值套件根（从本程序集位置向上找含 specs/agent-protocol.md 的目录 → conformance/）。</summary>
    public static string DefaultSuiteRoot()
    {
        for (string? dir = AppContext.BaseDirectory;
             dir is not null;
             dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)))
        {
            var candidate = Path.Combine(dir, "conformance");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir, "specs", "agent-protocol.md")))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("未定位到仓库 conformance/ 目录");
    }

    private static IEnumerable<string> OrderedDirs(string dir) =>
        Directory.GetDirectories(dir).OrderBy(p => p, StringComparer.Ordinal);

    private static string NameOf(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
}
