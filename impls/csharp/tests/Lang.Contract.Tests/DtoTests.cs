using System.Text.Json;
using Lang.Contract;
using Lang.Core;
using Xunit;

namespace Lang.Contract.Tests;

/// <summary>
/// Diagnostic → JSON DTO golden 测试（specs/diagnostics.md §2 + schemas/agent-protocol.schema.json；
/// 期望全部手抄自 spec/schema，禁拿实现当期望）。
/// </summary>
public class DtoTests
{
    [Fact]
    public void Golden_spec_example_empty_file()
    {
        // specs/diagnostics.md §2 的样例逐字段手抄（含 file ""）。
        var diag = Diagnostic.New(DiagnosticCode.E2002, "未定义 nope", Diagnostic.AtLine(new SourceId(0), 2));
        var files = SourceNames.FromPairs((new SourceId(0), ""));

        var actual = ContractJson.Serialize(DiagnosticDto.Of(diag, files));

        Assert.Equal(
            """{"code":"E2002","ruleId":"NAME-UNDEF-004","severity":"error","message":"未定义 nope","primarySpan":{"file":"","start":{"offset":0,"line":2,"column":1},"end":{"offset":0,"line":2,"column":1}},"relatedSpans":[],"help":""}""",
            actual);
    }

    [Fact]
    public void Golden_rich_diagnostic_with_related_and_help()
    {
        // agent-protocol §3 的 SourceSpan 形态（offset 0→12、column 1→13）+ related/help 非空。
        var diag = Diagnostic.New(DiagnosticCode.E3001, "类型不匹配",
            new Span(new SourceId(1), new Position(0, 1, 1), new Position(12, 1, 13))) with
        {
            RelatedSpans =
            [
                new Span(new SourceId(1), new Position(20, 3, 5), new Position(26, 3, 11)),
            ],
            Help = "期望 int",
        };
        var files = SourceNames.FromPairs((new SourceId(1), "main.lang-go"));

        var actual = ContractJson.Serialize(DiagnosticDto.Of(diag, files));

        Assert.Equal(
            """{"code":"E3001","ruleId":"TYPE-ASSIGN-001","severity":"error","message":"类型不匹配","primarySpan":{"file":"main.lang-go","start":{"offset":0,"line":1,"column":1},"end":{"offset":12,"line":1,"column":13}},"relatedSpans":[{"file":"main.lang-go","start":{"offset":20,"line":3,"column":5},"end":{"offset":26,"line":3,"column":11}}],"help":"期望 int"}""",
            actual);
    }

    [Fact]
    public void Missing_primary_span_serializes_null()
    {
        // primarySpan 缺席 → 字段仍必出、值为 null（schema required 集合）。
        var diag = Diagnostic.New(DiagnosticCode.E1001, "解析错误");

        var actual = ContractJson.Serialize(DiagnosticDto.Of(diag, new SourceNames()));

        Assert.Equal(
            """{"code":"E1001","ruleId":"LEX-ALL","severity":"error","message":"解析错误","primarySpan":null,"relatedSpans":[],"help":""}""",
            actual);
    }

    [Fact]
    public void Astral_offsets_are_code_points_not_chars()
    {
        // 接口契约 §0 对抗轴：astral 码点（🦀 = U+1F980，UTF-16 占 2 char）的 offset/column
        // 一律码点度量。("let 🦀 = 1" 中 🦀 的码点 offset = 4、char index = 4..5。)
        var src = new SourceText("🦀.lang-go", "let 🦀 = 1\n");
        Assert.Equal((1, 5), src.LineCol(4));
        Assert.Equal(4, src.CpToCharIndex(4));
        Assert.Equal(6, src.CpToCharIndex(5)); // 🦀 之后的码点跨过了代理对

        var diag = Diagnostic.New(DiagnosticCode.E2002, "未定义 🦀",
            new Span(new SourceId(3), new Position(4, 1, 5), new Position(5, 1, 6)));
        var files = SourceNames.FromPairs((new SourceId(3), "🦀.lang-go"));

        var actual = ContractJson.Serialize(DiagnosticDto.Of(diag, files));

        Assert.Equal(
            """{"code":"E2002","ruleId":"NAME-UNDEF-004","severity":"error","message":"未定义 🦀","primarySpan":{"file":"🦀.lang-go","start":{"offset":4,"line":1,"column":5},"end":{"offset":5,"line":1,"column":6}},"relatedSpans":[],"help":""}""",
            actual);
    }

    [Fact]
    public void Emitted_json_matches_schema_shape()
    {
        // schema 形态校验：required 七字段齐、severity 枚举、code/ruleId 模式（手工核，不引 regex）。
        var diag = Diagnostic.New(DiagnosticCode.E4001, "match 不穷尽", Diagnostic.AtLine(new SourceId(0), 6));

        using var doc = JsonDocument.Parse(ContractJson.Serialize(DiagnosticDto.Of(diag, new SourceNames())));
        var root = doc.RootElement;

        foreach (var key in new[] { "code", "ruleId", "severity", "message", "primarySpan", "relatedSpans", "help" })
        {
            Assert.True(root.TryGetProperty(key, out _), $"schema required 缺字段 {key}");
        }
        // 属性序 = schema 序（枚举属性名逐一比对出现顺序）
        Assert.Equal(
            new[] { "code", "ruleId", "severity", "message", "primarySpan", "relatedSpans", "help" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("error", root.GetProperty("severity").GetString());
        var code = root.GetProperty("code").GetString()!;
        Assert.Equal(5, code.Length);
        Assert.True(code[0] == 'E' && code[1..].All(char.IsAsciiDigit));
        var rule = root.GetProperty("ruleId").GetString()!;
        Assert.All(rule, c => Assert.True(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '-'));
        // 行级形态：column 恒 1、offset 恒 0
        Assert.Equal(1, root.GetProperty("primarySpan").GetProperty("start").GetProperty("column").GetInt32());
        Assert.Equal(0, root.GetProperty("primarySpan").GetProperty("start").GetProperty("offset").GetInt32());
    }

    [Fact]
    public void Dto_roundtrips_through_schema_json()
    {
        // 契约可读：协议消费方按同一 schema 反序列化必须成功且保形。
        var diag = Diagnostic.New(DiagnosticCode.E2010, "嵌套类型声明", Diagnostic.AtLine(new SourceId(2), 4));
        var files = SourceNames.FromPairs((new SourceId(2), "lib.lang-go"));

        var dto = DiagnosticDto.Of(diag, files);
        var text = ContractJson.Serialize(dto);
        var back = ContractJson.Deserialize<DiagnosticDto>(text);

        Assert.Equal(dto, back);
    }

    [Fact]
    public void Unknown_source_resolves_empty_file()
    {
        // 未登记 SourceId → file ""（与 TS 证据的未盖章行为一致）。
        var files = new SourceNames();
        Assert.Equal("", files.FileOf(new SourceId(9)));
        var span = Span.Point(new SourceId(9), new Position(0, 1, 1));
        Assert.Equal("", SpanDto.Of(span, files).File);
    }
}
