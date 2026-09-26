using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Interp.Tests;

/// <summary>conformance run case 的 manifest（YAML 受限子集，conformance/README.md 形态）。</summary>
public sealed record CaseManifest(
    string Name,
    string Category,
    /// <summary>run | check（缺省 = run：151 个 case 仅 31 个显式 check）。</summary>
    string Mode,
    int ExitCode,
    /// <summary>块标量原文（含末尾恰一个换行）。</summary>
    string Stdout)
{
    public bool IsRun => Mode != "check";
}

/// <summary>case 目录 + manifest。</summary>
public sealed record ConformanceCase(string Dir, CaseManifest Manifest)
{
    public string Category => Manifest.Category;

    public string Name => Manifest.Name;
}

/// <summary>
/// conformance 迷你装载器与双路运行夹具：
/// - manifest 解析（README 承诺的 YAML 受限子集：顶层标量、一层 expect、<c>|</c> 块标量、
///   diagnostics 列表项；超集形态一律报错）；
/// - 目录合并装载：目录内 .lang-go 文件名序收集 → import 指令 post-order DFS 依赖先序展开
///   （菱形去重 = done 集；真环 = in-progress 链显式报错）→ 合并语句流剥除 import；
/// - AST 参考解释器与 IR 解释器两条执行路径（stdout 逐字节捕获）。
/// </summary>
public static class ConformanceHarness
{
    /// <summary>run case 总量（漏扫/静默跳过先红）。</summary>
    public const int ExpectedRunCaseCount = 120;

    // ———— 目录定位 ————

    public static string? FindRoot()
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

    /// <summary>全量 case 发现（16 类目录名序 × case 目录名序）。</summary>
    public static List<ConformanceCase> Discover(string root)
    {
        var cases = new List<ConformanceCase>();
        foreach (var catDir in Directory.GetDirectories(root).Order())
        {
            var category = Path.GetFileName(catDir);
            foreach (var caseDir in Directory.GetDirectories(catDir).Order())
            {
                var yamlPath = Path.Combine(caseDir, "case.yaml");
                if (!File.Exists(yamlPath))
                {
                    continue;
                }

                var manifest = ParseManifest(File.ReadAllText(yamlPath), category);
                cases.Add(new ConformanceCase(caseDir, manifest));
            }
        }

        return cases;
    }

    // ———— manifest 解析（受限子集，超集报错） ————

    public static CaseManifest ParseManifest(string yaml, string category)
    {
        string? name = null;
        var mode = "run";
        var exitCode = -1;
        var stdout = string.Empty;
        var inExpect = false;
        string? blockKey = null;
        var blockIndent = 0;
        var blockLines = new List<string>();

        void EndBlock()
        {
            if (blockKey != "stdout")
            {
                blockKey = null;
                return;
            }

            // 块标量：strip 最小内容缩进，逐行连接；末尾恰一个换行
            var minIndent = int.MaxValue;
            foreach (var line in blockLines)
            {
                if (line.Length > 0)
                {
                    minIndent = Math.Min(minIndent, LeadingSpaces(line));
                }
            }

            if (minIndent == int.MaxValue)
            {
                minIndent = 0;
            }

            stdout = string.Join("\n", blockLines.Select(l => l.Length > 0 ? l[minIndent..] : string.Empty)) + "\n";
            blockKey = null;
        }

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // 块标量内容行（或空行）
            if (blockKey is not null)
            {
                if (line.Length == 0 || LeadingSpaces(line) > blockIndent)
                {
                    blockLines.Add(line);
                    continue;
                }

                EndBlock();
            }

            var indent = LeadingSpaces(line);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            // 列表条目（ruleIds/diagnostics 的 `- xxx` 行）：受限子集内无需取值
            if (trimmed.StartsWith("- "))
            {
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon < 0)
            {
                throw new InvalidDataException($"case.yaml 含不支持的行形态: {trimmed}");
            }

            if (indent == 0)
            {
                inExpect = false;
            }

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();

            if (indent == 0)
            {
                if (key == "name")
                {
                    name = value;
                }
                else if (key == "mode")
                {
                    mode = value;
                }
                else if (key == "category")
                {
                    // 分类以目录名为准（value 仅为文档；两者在真值套件中一致）
                }
                else if (key == "expect" && value == string.Empty)
                {
                    inExpect = true;
                }
                else if (key == "ruleIds" && value == string.Empty)
                {
                    // 列表键：条目行 `- xxx` 在下方 default 分支被忽略
                }
                else if (value != string.Empty)
                {
                    throw new InvalidDataException($"case.yaml 顶层键 {key} 不在受支持子集内");
                }

                continue;
            }

            if (inExpect && indent == 2)
            {
                switch (key)
                {
                    case "exitCode":
                        exitCode = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "stdout" when value == "|":
                        blockKey = "stdout";
                        blockIndent = indent;
                        blockLines = [];
                        break;
                    case "stdout":
                        stdout = value == "\"\"" ? string.Empty : value.Trim('"');
                        break;
                    case "stderr" or "diagnostics":
                        // run 模式恒空串；check 模式的诊断比对不属本夹具
                        break;
                    default:
                        throw new InvalidDataException($"expect 键 {key} 不在受支持子集内");
                }
            }
        }

        EndBlock();

        if (name is null)
        {
            throw new InvalidDataException("case.yaml 缺 name");
        }

        return new CaseManifest(name, category, mode, exitCode, stdout);
    }

    private static int LeadingSpaces(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ')
        {
            n++;
        }

        return n;
    }

    // ———— 目录合并装载（import 展开 / 菱形去重 / 环检测） ————

    /// <summary>装载并合并一个 case 目录：返回解析后的合并语句流（import 已剥除）。
    /// 失败（parse/环/未知 import）返回错误消息。</summary>
    public static (IReadOnlyList<Stmt>? Stmts, string? Error) LoadCase(string caseDir)
    {
        // 1) 目录内 .lang-go 递归收集（名字序；键 = 相对路径去扩展名、正斜杠）
        var byKey = new SortedDictionary<string, string>(StringComparer.Ordinal);
        CollectFiles(caseDir, caseDir, byKey);
        if (byKey.Count == 0)
        {
            return (null, "case 目录无 .lang-go 源文件");
        }

        // 2) 逐文件解析（SourceId 按收集序），并提取 import 依赖键
        var parsed = new Dictionary<string, IReadOnlyList<Stmt>>(StringComparer.Ordinal);
        var imports = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sid = 0u;
        foreach (var (key, text) in byKey)
        {
            var result = Parser.Parse(new SourceText($"{key}.lang-go", text), new SourceId(sid++));
            if (result.Error is not null)
            {
                return (null, $"{key}: parse 失败: {result.Error.Message}");
            }

            parsed[key] = result.Program;
            var deps = new List<string>();
            foreach (var s in result.Program)
            {
                if (s is Stmt.Import { Path: var path })
                {
                    var depKey = path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
                    if (depKey.EndsWith(".lang-go", StringComparison.Ordinal))
                    {
                        depKey = depKey[..^".lang-go".Length];
                    }

                    deps.Add(depKey);
                }
            }

            imports[key] = deps;
        }

        // 3) post-order DFS 依赖先序展开（done 去重 + in-progress 环检测）
        var order = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var inProgress = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in byKey.Keys)
        {
            var err = Expand(key);
            if (err is not null)
            {
                return (null, err);
            }
        }

        string? Expand(string key)
        {
            if (done.Contains(key))
            {
                return null;
            }

            if (inProgress.Contains(key))
            {
                return $"循环 import: {key}";
            }

            if (!parsed.ContainsKey(key))
            {
                return $"import 目标不存在: {key}";
            }

            inProgress.Add(key);
            foreach (var dep in imports[key])
            {
                foreach (var target in ResolveDep(dep))
                {
                    var err = Expand(target);
                    if (err is not null)
                    {
                        return err;
                    }
                }
            }

            inProgress.Remove(key);
            done.Add(key);
            order.Add(key);
            return null;
        }

        // MODULE-IMPORT-002：`./x` → 文件 `x.lang-go`（键 x）或目录包 `x/`
        // （目录包 = 目录内全部 .lang-go 文件名序全读，逐文件进入依赖展开）
        List<string> ResolveDep(string dep)
        {
            if (parsed.ContainsKey(dep))
            {
                return [dep];
            }

            var prefix = dep + "/";
            var members = parsed.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !k[prefix.Length..].Contains('/'))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            return members;
        }

        // 4) 合并语句流 + 剥除 import（CLI check 同序）
        var stmts = new List<Stmt>();
        foreach (var key in order)
        {
            foreach (var s in parsed[key])
            {
                if (s is not Stmt.Import)
                {
                    stmts.Add(s);
                }
            }
        }

        return (stmts, null);
    }

    private static void CollectFiles(string dir, string baseUrl, SortedDictionary<string, string> outp)
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
                outp[rel[..^".lang-go".Length]] = File.ReadAllText(entry.FullName);
            }
        }
    }

    // ———— case 执行 ————

    /// <summary>AST 参考解释器路径：装载 → 分析 → Run → stdout。返回 (实际输出, 错误)。</summary>
    public static (string? Stdout, string? Error) RunCaseAst(ConformanceCase c)
    {
        var (stmts, err) = LoadCase(c.Dir);
        if (err is not null)
        {
            return (null, err);
        }

        var analysis = RunHelper.Analyze(stmts!);
        if (analysis.HasErrors())
        {
            return (null, $"语义诊断: {string.Join("; ", analysis.Diags.Select(d => $"{d.Code} {d.Message}"))}");
        }

        var (ok, stdout, error) = RunHelper.RunAst(stmts!, analysis);
        return ok ? (stdout, null) : (null, $"运行错误: {error}");
    }

    /// <summary>IR 解释器路径：装载 → 分析 → Compile → IrMachine → stdout。</summary>
    public static (string? Stdout, string? Error) RunCaseIr(ConformanceCase c)
    {
        var (stmts, err) = LoadCase(c.Dir);
        if (err is not null)
        {
            return (null, err);
        }

        var analysis = RunHelper.Analyze(stmts!);
        if (analysis.HasErrors())
        {
            return (null, $"语义诊断: {string.Join("; ", analysis.Diags.Select(d => $"{d.Code} {d.Message}"))}");
        }

        var (ok, stdout, error) = RunHelper.RunIr(stmts!, analysis);
        return ok ? (stdout, null) : (null, $"运行错误: {error}");
    }
}
