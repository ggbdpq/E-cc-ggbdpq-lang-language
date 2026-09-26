using Lang.Core;

namespace Lang.Conformance;

/// <summary>
/// conformance manifest 解析（YAML 受限子集，conformance/README.md「manifest」节；手写解析器，
/// 零外部依赖）。支持的形态穷举：
/// - 顶层标量键 <c>name/category/mode</c>、列表键 <c>ruleIds</c>、嵌套键 <c>expect</c>（恰一层）；
/// - <c>expect</c> 下标量键 <c>exitCode/stderr</c>、块标量键 <c>stdout: |</c>、列表键 <c>diagnostics</c>；
/// - <c>diagnostics</c> 列表项 = <c>- code: …</c> 起头 + 对齐延续字段（<c>ruleId/severity/line</c>）；
/// - <c>#</c> 注释（独立行）；缩进只认空格（Tab 缩进拒绝）；不支持转义/锚点/多级嵌套/流式标量
///   （引号形态仅接受空串 <c>""</c>）。
/// 任何超集形态一律报错——套件拒绝模糊输入。
/// 语义校验（README mode 节落硬）：run 必须 <c>exitCode: 0</c>、stderr 空串、stdout 为块标量
/// （末尾恰一换行、无空行）且不得声明 diagnostics；check 至少一条诊断且不得声明
/// exitCode/stdout/stderr。诊断项 code 必须是已知 E 码、severity ∈ error|warning；
/// ruleId 可省（省 = 由 <see cref="DiagnosticCodeText.Rule"/> 回填），声明了必须与映射一致。
/// </summary>

/// <summary>case 模式（JSON 序列化为 "run"/"check" 字符串）。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CaseMode
{
    /// <summary>执行比对：stdout 逐字节、exitCode 0、stderr 空。</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("run")]
    Run,

    /// <summary>静态诊断：code+ruleId+severity+line 多重集相等。</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("check")]
    Check,
}

/// <summary>期望诊断（多重集的一项）。RuleId 已回填：manifest 声明了用声明的，否则由 code 推导。</summary>
public sealed record DiagExpect(string Code, string RuleId, string Severity, uint Line);

/// <summary><c>expect:</c> 块的语义模型（run 模式 diagnostics 恒空；check 模式 stdout/stderr 恒空串）。</summary>
public sealed record CaseExpect(long ExitCode, string Stdout, string Stderr, IReadOnlyList<DiagExpect> Diagnostics);

/// <summary>一个 conformance case 的解析结果。</summary>
public sealed record ConformanceCase(
    string Name,
    string Category,
    CaseMode Mode,
    IReadOnlyList<string> RuleIds,
    CaseExpect Expect);

/// <summary>manifest 结构/语义解析失败（README manifest 节的硬约束）。</summary>
public sealed class ManifestException(string message) : Exception(message);

public static class ManifestParser
{
    public static ConformanceCase Parse(string yaml)
    {
        var raw = new Parser(yaml).ParseManifest();
        return Validate(raw);
    }

    // —— 结构解析 → 中间模型（区分「缺席」与「空」） ——

    internal sealed class RawManifest
    {
        public string? Name;
        public string? Category;
        public string? Mode;
        public List<string>? RuleIds;
        public RawExpect? Expect;
    }

    internal sealed class RawExpect
    {
        public long? ExitCode;
        public string? Stdout;
        public string? Stderr;
        public List<RawDiag>? Diagnostics;
    }

    internal sealed class RawDiag
    {
        public string? Code;
        public string? RuleId;
        public string? Severity;
        public uint? Line;
    }

    private sealed class Parser
    {
        private readonly string[] _lines;
        private int _pos;

        public Parser(string yaml)
        {
            var normalized = yaml.Replace("\r\n", "\n").Replace("\r", "\n");
            if (normalized.EndsWith('\n'))
            {
                normalized = normalized[..^1];
            }
            _lines = normalized.Length == 0 ? [] : normalized.Split('\n');
        }

        public RawManifest ParseManifest()
        {
            var m = new RawManifest();
            var topIndent = RequireIndent(null);
            while (NextAtIndent(topIndent) is { } line)
            {
                var (key, val) = SplitKeyVal(line.Text, "manifest 顶层");
                switch (key)
                {
                    case "name":
                        Assign(ref m.Name, Scalar(val, "name"), "name");
                        break;
                    case "category":
                        Assign(ref m.Category, Scalar(val, "category"), "category");
                        break;
                    case "mode":
                        Assign(ref m.Mode, Scalar(val, "mode"), "mode");
                        break;
                    case "ruleIds":
                        RejectDup(m.RuleIds is not null, "ruleIds");
                        m.RuleIds = ParseScalarList(topIndent);
                        break;
                    case "expect":
                        RejectDup(m.Expect is not null, "expect");
                        m.Expect = ParseExpect(topIndent);
                        break;
                    default:
                        throw new ManifestException($"manifest 顶层未知键 {key}（支持 name/category/mode/ruleIds/expect）");
                }
            }
            _ = m.Name ?? throw new ManifestException("manifest 缺 name");
            _ = m.Category ?? throw new ManifestException("manifest 缺 category");
            return m.Expect is null ? throw new ManifestException("manifest 缺 expect") : m;
        }

        private RawExpect ParseExpect(int parentIndent)
        {
            var e = new RawExpect();
            var indent = RequireIndent(parentIndent);
            while (NextAtIndent(indent) is { } line)
            {
                var (key, val) = SplitKeyVal(line.Text, "expect 块");
                switch (key)
                {
                    case "exitCode":
                        RejectDup(e.ExitCode is not null, "exitCode");
                        e.ExitCode = ParseLong(Scalar(val, "exitCode"));
                        break;
                    case "stdout":
                        RejectDup(e.Stdout is not null, "stdout");
                        e.Stdout = val == "|" ? ParseBlockScalar(indent) : throw new ManifestException("stdout 只支持 | 块标量");
                        break;
                    case "stderr":
                        RejectDup(e.Stderr is not null, "stderr");
                        e.Stderr = Scalar(val, "stderr");
                        break;
                    case "diagnostics":
                        RejectDup(e.Diagnostics is not null, "diagnostics");
                        e.Diagnostics = ParseDiagList(indent);
                        break;
                    default:
                        throw new ManifestException($"expect 块未知键 {key}（支持 exitCode/stdout/stderr/diagnostics）");
                }
            }
            return e;
        }

        private List<string> ParseScalarList(int keyIndent)
        {
            var items = new List<string>();
            int? itemIndent = null;
            while (NextBeyond(keyIndent) is { } line)
            {
                if (!line.Text.StartsWith("- "))
                {
                    throw new ManifestException($"列表项须以 \"- \" 起头（得到 {line.Text}）");
                }
                RejectMixedIndent(itemIndent, line.Indent);
                itemIndent ??= line.Indent;
                items.Add(Scalar(line.Text[2..].TrimEnd(), "列表项"));
            }
            return items.Count > 0 ? items : throw new ManifestException("列表键下没有任何列表项");
        }

        private List<RawDiag> ParseDiagList(int keyIndent)
        {
            var items = new List<RawDiag>();
            int? itemIndent = null;
            while (NextBeyond(keyIndent) is { } head)
            {
                if (!head.Text.StartsWith("- "))
                {
                    throw new ManifestException($"diagnostics 列表项须以 \"- \" 起头（得到 {head.Text}）");
                }
                RejectMixedIndent(itemIndent, head.Indent);
                itemIndent ??= head.Indent;
                var (firstKey, firstVal) = SplitKeyVal(head.Text[2..], "diagnostics 列表项");
                var diag = new RawDiag();
                AssignRawDiag(diag, firstKey, Scalar(firstVal, firstKey));
                while (NextAtIndent(itemIndent.Value + 2) is { } cont)
                {
                    var (k, v) = SplitKeyVal(cont.Text, "diagnostics 列表项延续字段");
                    AssignRawDiag(diag, k, Scalar(v, k));
                }
                items.Add(diag);
            }
            return items.Count > 0 ? items : throw new ManifestException("diagnostics 下没有任何列表项");
        }

        private static void RejectMixedIndent(int? itemIndent, int indent)
        {
            if (itemIndent is { } first && first != indent)
            {
                throw new ManifestException("列表项缩进不一致");
            }
        }

        private string ParseBlockScalar(int keyIndent)
        {
            var parts = new List<string>();
            int? baseIndent = null;
            while (_pos < _lines.Length)
            {
                var raw = _lines[_pos];
                var indent = raw.TakeWhile(c => c == ' ').Count();
                var content = raw[indent..];
                if (content.Length == 0)
                {
                    throw new ManifestException("块标量含空行（README：末尾恰一个换行、无空行）");
                }
                if (raw[indent] == '\t')
                {
                    throw new ManifestException("块标量缩进不得使用 Tab");
                }
                if (indent <= keyIndent)
                {
                    break;
                }
                baseIndent ??= indent;
                if (indent < baseIndent)
                {
                    throw new ManifestException("块标量缩进不一致");
                }
                parts.Add(raw[baseIndent.Value..]);
                _pos++;
            }
            return parts.Count > 0 ? string.Join("\n", parts) + "\n"
                : throw new ManifestException("块标量没有任何内容行");
        }

        /// <summary>取下一条缩进恰为 indent 的有效行；遇到更小缩进/eof 返回 null，更大缩进报错。</summary>
        private (int Indent, string Text)? NextAtIndent(int indent)
        {
            var next = NextSignificant();
            if (next is null)
            {
                return null; // EOF：全部行已消费，不回退
            }
            if (next.Value.Indent < indent)
            {
                _pos--;
                return null;
            }
            if (next.Value.Indent > indent)
            {
                throw new ManifestException($"意外的多级嵌套缩进：{next.Value.Text}");
            }
            return next;
        }

        /// <summary>取下一条缩进严格大于 beyond 的有效行；否则回退并返回 null。</summary>
        private (int Indent, string Text)? NextBeyond(int beyond)
        {
            var next = NextSignificant();
            if (next is null)
            {
                return null; // EOF：全部行已消费，不回退
            }
            if (next.Value.Indent <= beyond)
            {
                _pos--;
                return null;
            }
            return next;
        }

        private int RequireIndent(int? parentIndent)
        {
            var next = NextSignificant();
            if (next is null)
            {
                throw new ManifestException("嵌套键下没有任何内容行");
            }
            if (parentIndent is { } p && next.Value.Indent <= p)
            {
                _pos--;
                throw new ManifestException("嵌套键下没有任何内容行");
            }
            _pos--;
            return next.Value.Indent;
        }

        private (int Indent, string Text)? NextSignificant()
        {
            while (_pos < _lines.Length)
            {
                var raw = _lines[_pos++];
                var indent = raw.TakeWhile(c => c == ' ').Count();
                if (indent < raw.Length && raw[indent] == '\t')
                {
                    throw new ManifestException($"第 {_pos} 行：缩进不得使用 Tab");
                }
                var text = raw[indent..];
                if (text.Length == 0 || text.StartsWith('#'))
                {
                    continue;
                }
                return (indent, text);
            }
            return null;
        }

        private static (string Key, string Val) SplitKeyVal(string text, string ctx)
        {
            var colon = text.IndexOf(':');
            if (colon <= 0)
            {
                throw new ManifestException($"{ctx}行缺少 \"键:\" 形态：{text}");
            }
            var key = text[..colon];
            if (key.Length == 0 || !char.IsAsciiLetter(key[0]) || key.Any(c => !char.IsAsciiLetterOrDigit(c)))
            {
                throw new ManifestException($"{ctx}键名形态非法：{key}");
            }
            var val = text[(colon + 1)..].Trim();
            return (key, val);
        }

        private static string Scalar(string val, string ctx) => val switch
        {
            "" => throw new ManifestException($"{ctx} 缺少值"),
            "\"\"" => "",
            var v when v.StartsWith('"') || v.StartsWith('\'') =>
                throw new ManifestException($"{ctx}：不支持流式/引号标量（除空串 \"\"）"),
            var v => StripComment(v),
        };

        private static string StripComment(string v)
        {
            var hash = v.IndexOf(" #", StringComparison.Ordinal);
            return hash < 0 ? v : v[..hash].TrimEnd();
        }

        private static long ParseLong(string v) =>
            long.TryParse(v, out var n) ? n : throw new ManifestException($"exitCode 须为整数（得到 {v}）");

        private static void Assign(ref string? slot, string val, string key)
        {
            RejectDup(slot is not null, key);
            slot = val;
        }

        private static void AssignRawDiag(RawDiag diag, string key, string val)
        {
            switch (key)
            {
                case "code":
                    RejectDup(diag.Code is not null, "code");
                    diag.Code = val;
                    break;
                case "ruleId":
                    RejectDup(diag.RuleId is not null, "ruleId");
                    diag.RuleId = val;
                    break;
                case "severity":
                    RejectDup(diag.Severity is not null, "severity");
                    diag.Severity = val;
                    break;
                case "line":
                    RejectDup(diag.Line is not null, "line");
                    diag.Line = uint.TryParse(val, out var line)
                        ? line
                        : throw new ManifestException($"line 须为非负整数（得到 {val}）");
                    break;
                default:
                    throw new ManifestException($"diagnostics 列表项未知键 {key}（支持 code/ruleId/severity/line）");
            }
        }

        private static void RejectDup(bool dup, string key)
        {
            if (dup)
            {
                throw new ManifestException($"键 {key} 重复声明");
            }
        }
    }

    // —— 语义校验（README manifest 节硬约束） ——

    private static ConformanceCase Validate(RawManifest raw)
    {
        if (raw.Name!.Trim().Length == 0)
        {
            throw new ManifestException("name 不得为空");
        }
        if (raw.Category!.Trim().Length == 0)
        {
            throw new ManifestException("category 不得为空");
        }
        if (raw.RuleIds is { } ids && ids.Any(r => r.Trim().Length == 0))
        {
            throw new ManifestException("ruleIds 列表项不得为空");
        }
        var mode = raw.Mode switch
        {
            null => CaseMode.Run,
            "run" => CaseMode.Run,
            "check" => CaseMode.Check,
            var other => throw new ManifestException($"mode 须为 run|check（得到 {other}）"),
        };
        var e = raw.Expect!;

        var diagnostics = new List<DiagExpect>();
        if (e.Diagnostics is { } list)
        {
            foreach (var d in list)
            {
                diagnostics.Add(ValidateDiag(d));
            }
        }

        switch (mode)
        {
            case CaseMode.Run:
                if (diagnostics.Count > 0)
                {
                    throw new ManifestException("run 模式不得声明 diagnostics");
                }
                var exitCode = e.ExitCode ?? throw new ManifestException("run 模式必须声明 exitCode");
                if (exitCode != 0)
                {
                    throw new ManifestException("run 模式 exitCode 必须为 0（运行时错误文本不在比对面）");
                }
                var stderr = e.Stderr ?? throw new ManifestException("run 模式必须声明 stderr（当前必须为空串）");
                if (stderr.Length > 0)
                {
                    throw new ManifestException("run 模式 stderr 必须为空串");
                }
                var stdout = e.Stdout ?? throw new ManifestException("run 模式必须声明 stdout（| 块标量）");
                if (!stdout.EndsWith('\n') || stdout.Contains("\n\n"))
                {
                    throw new ManifestException("run 模式 stdout 须为 | 块标量：末尾恰一个换行、无空行");
                }
                return Build(mode, exitCode, stdout, stderr, diagnostics);
            case CaseMode.Check:
                if (e.ExitCode is not null || e.Stdout is not null || e.Stderr is not null)
                {
                    throw new ManifestException("check 模式不得声明 exitCode/stdout/stderr（只比对诊断多重集）");
                }
                if (diagnostics.Count == 0)
                {
                    throw new ManifestException("check 模式至少声明一条诊断");
                }
                return Build(mode, 0, "", "", diagnostics);
            default:
                throw new InvalidOperationException("CaseMode 变体未穷尽");
        }

        ConformanceCase Build(CaseMode m, long exit, string stdout, string stderr, List<DiagExpect> diags) =>
            new(raw.Name!, raw.Category!, m, raw.RuleIds ?? [], new CaseExpect(exit, stdout, stderr, diags));
    }

    private static DiagExpect ValidateDiag(RawDiag d)
    {
        var codeText = d.Code ?? throw new ManifestException("诊断项缺 code");
        if (codeText.Length == 0 || codeText[0] != 'E' || !Enum.TryParse<DiagnosticCode>(codeText, out var code))
        {
            throw new ManifestException($"未知诊断码 {codeText}");
        }
        var severity = d.Severity ?? throw new ManifestException("诊断项缺 severity");
        if (severity != "error" && severity != "warning")
        {
            throw new ManifestException($"severity 须为 error|warning（得到 {severity}）");
        }
        // line 允许 0：套件真值中「无位置诊断」（如 E3009 全局形状错误）以 line: 0 声明，
        // runner 按 primarySpan.start.line 精确比对（README manifest 节）。
        var line = d.Line ?? throw new ManifestException("诊断项缺 line");
        var expect = code.Rule().RuleText();
        var ruleId = d.RuleId switch
        {
            null => expect,
            // 声明了 ruleId 就必须是该码的映射真值（Rule ID 与码同为契约）
            var r when r == expect => r,
            var r => throw new ManifestException($"ruleId {r} 与码 {codeText} 的映射 {expect} 不符"),
        };
        return new DiagExpect(codeText, ruleId, severity, line);
    }
}
