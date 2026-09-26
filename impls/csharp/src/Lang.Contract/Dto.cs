using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lang.Core;

namespace Lang.Contract;

/// <summary>契约 JSON 序列化唯一入口：字段序由声明序 + JsonPropertyName 决定；转义形与
/// Go encoding/json / serde_json 证据一致——非 ASCII 码点字面输出。BCL 编码器对 astral
/// 码点强制代理对转义（且 UnicodeRange 只覆盖 BMP、自定义 Encoder 需 unsafe），故序列化后
/// 做一次左到右扫描，仅把「代理对转义」还原为字面 UTF-16（其余转义保留，`\\u` 字面形态
/// 因成对消费而无歧义）。</summary>
public static class ContractJson
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => RestoreAstralLiterals(JsonSerializer.Serialize(value, s_options));

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, s_options);

    internal static string RestoreAstralLiterals(string json)
    {
        if (!json.Contains("\\u", StringComparison.Ordinal))
        {
            return json;
        }
        var sb = new System.Text.StringBuilder(json.Length);
        var i = 0;
        while (i < json.Length)
        {
            var c = json[i];
            if (c != '\\' || i + 1 >= json.Length)
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (json[i + 1] == 'u' && i + 6 <= json.Length && TryReadHex4(json, i + 2, out var hi))
            {
                // 高代理 + 紧随的低代理转义 → 还原字面码点；其余 \uXXXX 原样保留。
                if (hi is >= 0xD800 and <= 0xDBFF
                    && i + 12 <= json.Length
                    && json[i + 6] == '\\' && json[i + 7] == 'u'
                    && TryReadHex4(json, i + 8, out var lo)
                    && lo is >= 0xDC00 and <= 0xDFFF)
                {
                    sb.Append((char)hi).Append((char)lo);
                    i += 12;
                    continue;
                }
            }
            sb.Append(c).Append(json[i + 1]);
            i += 2;
        }
        return sb.ToString();
    }

    private static bool TryReadHex4(string s, int start, out int value)
    {
        value = 0;
        for (var k = 0; k < 4; k++)
        {
            var d = s[start + k] switch
            {
                >= '0' and <= '9' => s[start + k] - '0',
                >= 'a' and <= 'f' => s[start + k] - 'a' + 10,
                >= 'A' and <= 'F' => s[start + k] - 'A' + 10,
                _ => -1,
            };
            if (d < 0)
            {
                return false;
            }
            value = (value << 4) | d;
        }
        return true;
    }
}

/// <summary>
/// Diagnostic → JSON DTO（specs/agent-protocol.md schemaVersion "1" + specs/diagnostics.md §2）。
///
/// DTO 与 Lang.Core 内部 record 分离：内部结构是编译器所有物，本文件的 System.Text.Json
/// 形态才是对外契约（字段只加不改不删）。字段序 = schema 结构体序 =
/// <c>code, ruleId, severity, message, primarySpan, relatedSpans, help</c>；
/// 位置序 = <c>offset, line, column</c>；区间序 = <c>file, start, end</c>。
/// 每个属性显式 <see cref="JsonPropertyNameAttribute"/>——形状不依赖序列化方的命名策略。
///
/// v0.4 行级定位：primarySpan 的 line 正确、column 恒 1、offset 恒 0
/// （形态由 Lang.Core <see cref="Diagnostic.AtLine"/> 保证，本层逐字映射，不做二次加工）。
/// </summary>

/// <summary>SourceId → 契约 <c>file</c> 字段（loader 侧提供的只读反查）。</summary>
public interface IFileResolver
{
    /// <summary>未登记的 SourceId 返回空串（与 TS 证据一致：未盖章 span → file ""）。</summary>
    string FileOf(SourceId source);
}

/// <summary>简单反查表：由 (SourceId, 文件相对路径) 对构建；只按键查询，从不迭代（禁哈希序外泄）。</summary>
public sealed class SourceNames : IFileResolver
{
    private readonly System.Collections.Generic.Dictionary<uint, string> _names = new();

    public void Add(SourceId source, string file) => _names[source.Value] = file;

    public static SourceNames FromPairs(params (SourceId Source, string File)[] pairs)
    {
        var map = new SourceNames();
        foreach (var (source, file) in pairs)
        {
            map.Add(source, file);
        }
        return map;
    }

    public string FileOf(SourceId source) => _names.TryGetValue(source.Value, out var file) ? file : "";
}

/// <summary>契约单点（0 基码点 offset；1 基 line/column）。</summary>
public sealed record PositionDto
{
    [JsonPropertyName("offset")]
    public required uint Offset { get; init; }

    [JsonPropertyName("line")]
    public required uint Line { get; init; }

    [JsonPropertyName("column")]
    public required uint Column { get; init; }

    public static PositionDto Of(Position pos) => new()
    {
        Offset = pos.Offset,
        Line = pos.Line,
        Column = pos.Column,
    };
}

/// <summary>契约区间。</summary>
public sealed record SpanDto
{
    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("start")]
    public required PositionDto Start { get; init; }

    [JsonPropertyName("end")]
    public required PositionDto End { get; init; }

    public static SpanDto Of(Span span, IFileResolver files) => new()
    {
        File = files.FileOf(span.Source),
        Start = PositionDto.Of(span.Start),
        End = PositionDto.Of(span.End),
    };
}

/// <summary>诊断 JSON 契约（schemas/agent-protocol.schema.json <c>definitions.diagnostic</c>）。</summary>
/// <remarks>
/// 全字段必出：primarySpan 缺席时序列化为 <c>null</c>（schema required 集合照办）；
/// ruleId v0.9 起由 code 推导恒存在，照常输出。
/// </remarks>
public sealed record DiagnosticDto
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("ruleId")]
    public required string RuleId { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("primarySpan")]
    public SpanDto? PrimarySpan { get; init; }

    [JsonPropertyName("relatedSpans")]
    public IReadOnlyList<SpanDto> RelatedSpans { get; init; } = [];

    [JsonPropertyName("help")]
    public string Help { get; init; } = "";

    /// <summary>集合成员按序列结构比较（record 默认按引用比 IReadOnlyList）。</summary>
    public bool Equals(DiagnosticDto? other) =>
        other is not null && Code == other.Code && RuleId == other.RuleId
            && Severity == other.Severity && Message == other.Message
            && (PrimarySpan is null ? other.PrimarySpan is null : PrimarySpan.Equals(other.PrimarySpan))
            && RelatedSpans.SequenceEqual(other.RelatedSpans)
            && Help == other.Help;

    public override int GetHashCode()
    {
        var h = new System.HashCode();
        h.Add(Code);
        h.Add(RuleId);
        h.Add(Severity);
        h.Add(Message);
        h.Add(PrimarySpan);
        foreach (var s in RelatedSpans)
        {
            h.Add(s);
        }
        h.Add(Help);
        return h.ToHashCode();
    }

    /// <summary>内部诊断 → 契约 DTO。severity/ruleId 均由 code 侧事实源推导，不可由调用方改写。</summary>
    public static DiagnosticDto Of(Diagnostic diag, IFileResolver files) => new()
    {
        Code = diag.Code.CodeText(),
        RuleId = diag.RuleId.RuleText(),
        Severity = diag.Severity == Lang.Core.Severity.Error ? "error" : "warning",
        Message = diag.Message,
        PrimarySpan = diag.PrimarySpan is { } span ? SpanDto.Of(span, files) : null,
        RelatedSpans = [.. diag.RelatedSpans.Select(s => SpanDto.Of(s, files))],
        Help = diag.Help,
    };
}
