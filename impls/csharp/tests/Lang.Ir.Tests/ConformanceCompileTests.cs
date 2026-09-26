using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;
using Xunit;

namespace Lang.Ir.Tests;

/// <summary>
/// conformance 采样测试：run 类 case（expect.exitCode == 0）全量过
/// 「parse → analyze → compile → validate」链。
///
/// 范围 = 任务登记的 13 个分类（io 需宿主服务面、diagnostics 不产 IR，不做）。
/// 多文件 case（module 等）：递归收集源文件，按 import 指令做依赖先序展开
/// （NAME-MERGE-010 的装载序近似；resolver 对 import 语句按「装载器已展开」处理）。
/// compile 内部已 validate；测试再显式调 Validator.Validate 复核（任务判据）。
/// </summary>
public class ConformanceCompileTests
{
    private static string? ConformanceRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "conformance");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>case.yaml 是否为 run 类（期望正常退出）。</summary>
    private static bool IsRunCase(string yaml) => yaml.Contains("exitCode: 0");

    /// <summary>收集 case 的全部源文件（递归子目录）。键 = 相对路径（正斜杠、去扩展名），
    /// 对应 `import "./util/math"` 的 `util/math`。</summary>
    private static void CollectFiles(string dir, string baseUrl, List<(string Key, string Path)> outp)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry is DirectoryInfo sub)
            {
                CollectFiles(sub.FullName, baseUrl, outp);
            }
            else if (entry.Name.EndsWith(".lang-go", StringComparison.Ordinal))
            {
                var rel = Path.GetRelativePath(baseUrl, entry.FullName).Replace('\\', '/');
                var key = rel[..^".lang-go".Length];
                outp.Add((key, entry.FullName));
            }
        }
    }

    /// <summary>源码里的 import 相对路径 → 本 case 内的文件键（语法解析提取，非正则）。</summary>
    private static List<string> ImportKeysOf(string text, IReadOnlyCollection<string> keys)
    {
        var st = new SourceText("probe.lang-go", text);
        var parsed = Parser.Parse(st, new SourceId(999));
        var outp = new List<string>();
        if (parsed.Error is not null)
        {
            return outp;
        }
        foreach (var s in parsed.Program)
        {
            if (s is Stmt.Import { Path: var path })
            {
                var key = path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
                if (key.EndsWith(".lang-go", StringComparison.Ordinal))
                {
                    key = key[..^".lang-go".Length];
                }
                if (keys.Contains(key))
                {
                    outp.Add(key);
                }
            }
        }
        return outp;
    }

    private static void ExpandImports(
        string key,
        Dictionary<string, string> byKey,
        List<string> order,
        HashSet<string> seen)
    {
        if (!seen.Add(key))
        {
            return;
        }
        if (!byKey.TryGetValue(key, out var text))
        {
            return;
        }
        foreach (var dep in ImportKeysOf(text, byKey.Keys.ToList()))
        {
            ExpandImports(dep, byKey, order, seen);
        }
        order.Add(key);
    }

    /// <summary>合并序：依赖先序展开 import（post-order DFS，防环）；根按名字序确定。</summary>
    private static List<(string Key, string Text)> CaseSources(string dir)
    {
        var files = new List<(string Key, string Path)>();
        CollectFiles(dir, dir, files);
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, path) in files)
        {
            byKey[key] = File.ReadAllText(path);
        }
        var imported = new List<string>();
        foreach (var (_, text) in byKey)
        {
            imported.AddRange(ImportKeysOf(text, byKey.Keys.ToList()));
        }
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in byKey.Keys.Where(k => !imported.Contains(k)).Order())
        {
            ExpandImports(key, byKey, order, seen);
        }
        foreach (var key in byKey.Keys)
        {
            if (!seen.Contains(key))
            {
                order.Add(key);
            }
        }
        return order.Select(k => (k, byKey[k])).ToList();
    }

    /// <summary>单 case：合并源码 → analyze → compile → validate。</summary>
    private static void RunCase(string dir, string caseName)
    {
        var sources = CaseSources(dir);
        var stmts = new List<Stmt>();
        var i = 0;
        foreach (var (key, text) in sources)
        {
            var source = new SourceId((uint)i);
            var st = new SourceText($"{key}.lang-go", text);
            var parsed = Parser.Parse(st, source);
            Assert.True(parsed.Error is null, $"{caseName}/{key}: parse 失败: {parsed.Error}");
            stmts.AddRange(parsed.Program);
            i++;
        }
        var analysis = LangSemantic.Analyze(stmts, new SourceId(0));
        Assert.True(analysis.Diags.Count == 0,
            $"{caseName}: run case 不应有语义诊断: {string.Join("; ", analysis.Diags.Select(d => d.Code.CodeText()))}");
        var res = Compiler.Compile(stmts, analysis);
        Assert.True(res.IsOk, $"{caseName}: compile 失败: {res.Error}");
        var err = Validator.Validate(res.Value!);
        Assert.True(err is null, $"{caseName}: validate 失败: {err}");
    }

    /// <summary>每分类一个测试：枚举全部 run case 逐个过链；返回实跑数。</summary>
    private static int CheckCategory(string root, string cat)
    {
        var dir = Path.Combine(root, cat);
        var failures = new List<string>();
        var ran = 0;
        foreach (var caseDir in Directory.GetDirectories(dir).Order())
        {
            var yamlPath = Path.Combine(caseDir, "case.yaml");
            if (!File.Exists(yamlPath))
            {
                continue;
            }
            var yaml = File.ReadAllText(yamlPath);
            if (!IsRunCase(yaml))
            {
                continue;
            }
            var name = Path.GetFileName(caseDir);
            try
            {
                RunCase(caseDir, $"{cat}/{name}");
            }
            catch (Exception ex)
            {
                failures.Add($"{cat}/{name}: {ex.Message}");
            }
            ran++;
        }
        Assert.True(failures.Count == 0,
            $"{ran} 个 run case 中 {failures.Count} 失败:\n  {string.Join("\n  ", failures)}");
        Assert.True(ran > 0, $"分类 {cat} 应至少覆盖一个 run case（实跑 {ran}）");
        return ran;
    }

    private static (string? Root, Dictionary<string, int> Counts) RunAll()
    {
        var root = ConformanceRoot();
        var counts = new Dictionary<string, int>();
        if (root is null)
        {
            return (null, counts);
        }
        foreach (var cat in Categories)
        {
            counts[cat] = CheckCategory(root, cat);
        }
        return (root, counts);
    }

    private static readonly string[] Categories =
    [
        "expressions", "syntax", "collections", "values", "match", "struct",
        "generics", "result", "concurrency", "module", "closure", "enum", "option",
    ];

    public static IEnumerable<object[]> CategoryNames() =>
        Categories.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(CategoryNames))]
    public void ConformanceCategory(string cat)
    {
        var root = ConformanceRoot();
        Assert.NotNull(root);
        CheckCategory(root, cat);
    }

    /// <summary>13 分类 run case 总量锁定 = 100（漏扫/静默跳过在此先红）。</summary>
    [Fact]
    public void RunCaseCountIsHundred()
    {
        var (root, counts) = RunAll();
        Assert.NotNull(root);
        Assert.Equal(100, counts.Values.Sum());
    }
}
