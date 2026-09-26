using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lang.Contract;
using Lang.Core;

namespace Lang.Cli;

/// <summary>
/// 诊断渲染与 JSON 信封（specs/agent-protocol.md §1；证据 lang-rs out.rs + Go cli.go）。
///
/// <para>信封契约：<c>schemaVersion</c> 恒字符串 <c>"1"</c>（spec 明文 + Go/TS 证据同文）；
/// <c>toolVersion</c> 与 <c>lang-cs version</c> 同源。诊断负载一律走 Lang.Contract
/// <see cref="DiagnosticDto"/>（字段序即 schema 序，禁二次加工）。</para>
///
/// <para>JSON 输出形：两空格缩进（Go json.MarshalIndent / serde_json to_string_pretty
/// 同构）；转义口径与 Lang.Contract 一致——非 ASCII 码点字面输出。</para>
/// </summary>
internal static class Out
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>inspect/dump/refactor 信封用：可选字段缺席即省略（rust serde skip-None
    /// 同构——同一条命令不同分支的字段集不同，契约负载字段在册即输出）。</summary>
    private static readonly JsonSerializerOptions s_softOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>契约 JSON 统一打印（两空格缩进 + 换行，Go printJSON 同构）。</summary>
    internal static void PrintJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, s_options));

    /// <summary>inspect/dump/refactor 信封打印（两空格缩进；null 字段省略）。</summary>
    internal static void PrintJsonSoft<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, s_softOptions));

    /// <summary>check --json 的信封（契约 §2：success + diagnostics[]；字段序即 schema 序）。</summary>
    internal sealed record CheckEnvelope
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion => "1";

        [JsonPropertyName("toolVersion")]
        public string ToolVersion => CliApp.Version;

        [JsonPropertyName("kind")]
        public string Kind => "check";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("diagnostics")]
        public IReadOnlyList<DiagnosticDto> Diagnostics { get; init; } = [];
    }

    /// <summary>错误信封（契约：error 负载配非零退出码）。</summary>
    internal sealed record ErrorEnvelope
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion => "1";

        [JsonPropertyName("toolVersion")]
        public string ToolVersion => CliApp.Version;

        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("error")]
        public required string Error { get; init; }
    }

    /// <summary>verify --json 的信封（kind = "verify"；Skipped 显式在册，不谎报通过）。</summary>
    internal sealed record VerifyEnvelope
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion => "1";

        [JsonPropertyName("toolVersion")]
        public string ToolVersion => CliApp.Version;

        [JsonPropertyName("kind")]
        public string Kind => "verify";

        [JsonPropertyName("passed")]
        public required bool Passed { get; init; }

        [JsonPropertyName("failedStage")]
        public string? FailedStage { get; init; }

        [JsonPropertyName("stages")]
        public IReadOnlyList<VerifyStageDto> Stages { get; init; } = [];
    }

    internal sealed record VerifyStageDto
    {
        [JsonPropertyName("stage")]
        public required string Stage { get; init; }

        [JsonPropertyName("outcome")]
        public required string Outcome { get; init; }

        [JsonPropertyName("note")]
        public string Note { get; init; } = "";
    }

    internal static CheckEnvelope CheckEnvelopeOf(IReadOnlyList<Diagnostic> diags, IFileResolver files) => new()
    {
        Success = diags.Count == 0,
        Diagnostics = [.. diags.Select(d => DiagnosticDto.Of(d, files))],
    };

    internal static ErrorEnvelope ErrorEnvelopeOf(string kind, string message) => new()
    {
        Kind = kind,
        Error = message,
    };

    // ———— inspect / dump / refactor 信封（第三波；字段序 = 契约序） ————

    /// <summary>信封头三字段（schemaVersion/toolVersion/kind）按声明序序列化。</summary>
    internal abstract record EnvelopeBase
    {
        // JsonPropertyOrder：继承 record 的基类属性默认序列化在派生属性之后，
        // 头三字段必须按契约序在前（schemaVersion → toolVersion → kind）
        [JsonPropertyName("schemaVersion")]
        [JsonPropertyOrder(-3)]
        public string SchemaVersion => "1";

        [JsonPropertyName("toolVersion")]
        [JsonPropertyOrder(-2)]
        public string ToolVersion => CliApp.Version;

        [JsonPropertyName("kind")]
        [JsonPropertyOrder(-1)]
        public required string Kind { get; init; }
    }

    internal sealed record SymbolsEnvelope : EnvelopeBase
    {
        [JsonPropertyName("symbols")]
        public IReadOnlyList<SymbolDto> Symbols { get; init; } = [];
    }

    internal sealed record SymbolDto
    {
        [JsonPropertyName("symbolId")]
        public required uint SymbolId { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("definition")]
        public required SpanDto Definition { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("arity")]
        public required int Arity { get; init; }

        [JsonPropertyName("module")]
        public required string Module { get; init; }
    }

    internal sealed record ReferencesEnvelope : EnvelopeBase
    {
        [JsonPropertyName("references")]
        public IReadOnlyList<ReferenceDto> References { get; init; } = [];
    }

    internal sealed record ReferenceDto
    {
        [JsonPropertyName("symbolId")]
        public required uint SymbolId { get; init; }

        [JsonPropertyName("span")]
        public required SpanDto Span { get; init; }

        [JsonPropertyName("referenceKind")]
        public required string ReferenceKind { get; init; }
    }

    internal sealed record TypeNodesEnvelope : EnvelopeBase
    {
        [JsonPropertyName("nodes")]
        public IReadOnlyList<TypeNodeDto> Nodes { get; init; } = [];
    }

    internal sealed record TypeNodeDto
    {
        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("span")]
        public required SpanDto Span { get; init; }
    }

    internal sealed record EffectsEnvelope : EnvelopeBase
    {
        [JsonPropertyName("effects")]
        public IReadOnlyList<EffectDto> Effects { get; init; } = [];
    }

    internal sealed record EffectDto
    {
        [JsonPropertyName("symbolId")]
        public required uint SymbolId { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("effects")]
        public IReadOnlyList<string> Effects { get; init; } = [];
    }

    internal sealed record CallersEnvelope : EnvelopeBase
    {
        [JsonPropertyName("callers")]
        public IReadOnlyList<GraphNodeDto> Callers { get; init; } = [];
    }

    internal sealed record CalleesEnvelope : EnvelopeBase
    {
        [JsonPropertyName("callees")]
        public IReadOnlyList<GraphNodeDto> Callees { get; init; } = [];
    }

    internal sealed record ImpactEnvelope : EnvelopeBase
    {
        [JsonPropertyName("impact")]
        public required ImpactDto Impact { get; init; }
    }

    internal sealed record GraphNodeDto
    {
        [JsonPropertyName("symbolId")]
        public required uint SymbolId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }
    }

    internal sealed record ImpactDto
    {
        [JsonPropertyName("symbol")]
        public required GraphNodeDto Symbol { get; init; }

        [JsonPropertyName("direct")]
        public IReadOnlyList<GraphNodeDto> Direct { get; init; } = [];

        [JsonPropertyName("transitive")]
        public IReadOnlyList<GraphNodeDto> Transitive { get; init; } = [];

        [JsonPropertyName("note")]
        public string Note => "证据集为保守估计，非完整影响证明";
    }

    /// <summary>refactor dry-run 报告（契约 §2：applied/editCount/symbol/workspaceEdit）。</summary>
    internal sealed record RefactorPlanEnvelope : EnvelopeBase
    {
        [JsonPropertyName("applied")]
        public bool Applied => false;

        [JsonPropertyName("editCount")]
        public required int EditCount { get; init; }

        [JsonPropertyName("symbol")]
        public SymbolRefDto? Symbol { get; init; }

        [JsonPropertyName("workspaceEdit")]
        public required WorkspaceEditDto WorkspaceEdit { get; init; }
    }

    /// <summary>refactor --apply 成功报告（writtenFiles 替代 workspaceEdit）。</summary>
    internal sealed record RefactorAppliedEnvelope : EnvelopeBase
    {
        [JsonPropertyName("applied")]
        public bool Applied => true;

        [JsonPropertyName("editCount")]
        public required int EditCount { get; init; }

        [JsonPropertyName("writtenFiles")]
        public required int WrittenFiles { get; init; }
    }

    /// <summary>safe-remove 不可删证据（removable:false + reason + refSites，退出码 1）。</summary>
    internal sealed record RefactorNotRemovableEnvelope : EnvelopeBase
    {
        [JsonPropertyName("removable")]
        public bool Removable => false;

        [JsonPropertyName("reason")]
        public required string Reason { get; init; }

        [JsonPropertyName("refSites")]
        public IReadOnlyList<string> RefSites { get; init; } = [];

        [JsonPropertyName("symbol")]
        public required SymbolRefDto Symbol { get; init; }
    }

    internal sealed record SymbolRefDto
    {
        [JsonPropertyName("id")]
        public required uint Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("kind")]
        public required string Kind { get; init; }
    }

    internal sealed record WorkspaceEditDto
    {
        [JsonPropertyName("edits")]
        public IReadOnlyList<TextEditDto> Edits { get; init; } = [];

        [JsonPropertyName("preconditions")]
        public IReadOnlyList<PreconditionDto> Preconditions { get; init; } = [];
    }

    internal sealed record TextEditDto
    {
        [JsonPropertyName("file")]
        public required string File { get; init; }

        [JsonPropertyName("startOffset")]
        public required int StartOffset { get; init; }

        [JsonPropertyName("endOffset")]
        public required int EndOffset { get; init; }

        [JsonPropertyName("replacement")]
        public required string Replacement { get; init; }
    }

    internal sealed record PreconditionDto
    {
        [JsonPropertyName("file")]
        public required string File { get; init; }

        [JsonPropertyName("fileHash")]
        public required string FileHash { get; init; }
    }

    /// <summary>诊断的人类可读形（TS formatDiag 同构，文件名前缀多文件模块消歧）：
    /// <c>{file}:第{line}行: [{code}] {message}</c>；无锚诊断 file 为空、行 0。</summary>
    internal static string FormatDiag(Diagnostic diag, IFileResolver files)
    {
        uint line = diag.PrimarySpan?.Start.Line ?? 0;
        string file = diag.PrimarySpan is { } span ? files.FileOf(span.Source) : "";
        string prefix = file.Length == 0 ? "" : $"{file}:";
        return $"{prefix}第{line}行: [{diag.Code.CodeText()}] {diag.Message}";
    }

    /// <summary>诊断块渲染（多行，TS formatDiags 同构）。</summary>
    internal static string FormatDiags(IReadOnlyList<Diagnostic> diags, IFileResolver files) =>
        string.Join("\n", diags.Select(d => FormatDiag(d, files)));
}
