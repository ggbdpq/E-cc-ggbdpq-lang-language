using Lang.BackendGo;

namespace Lang.BackendGo.Tests;

// —— jsonStringify（RUNTIME-JSON-002 硬契约；期望值手算并经本机 go 1.27 encoding/json 对拍；
//    唯一口径分歧：-0.0 → "0" 取解释器语义，Go json 出 "-0"，沿 SPEC-GAP-RS-002 已裁决）——
public class JsonScalarTests
{
    [Fact]
    public void Null_forms()
    {
        Assert.True(Json.TryStringify(FmtNode.Unit.Instance, out string json, out JsonError? err));
        Assert.Null(err);
        Assert.Equal("null", json);
        Assert.True(Json.TryStringify(new FmtNode.Option(null), out json, out err));
        Assert.Null(err);
        Assert.Equal("null", json);
    }

    [Fact]
    public void Scalars()
    {
        Assert.True(Json.TryStringify(new FmtNode.Bool(true), out string json, out _));
        Assert.Equal("true", json);
        Assert.True(Json.TryStringify(new FmtNode.Bool(false), out json, out _));
        Assert.Equal("false", json);
        Assert.True(Json.TryStringify(new FmtNode.Str("hi"), out json, out _));
        Assert.Equal("\"hi\"", json);
    }

    [Fact]
    public void Int64_full_precision()
    {
        // 2^53+1 若走 f64 会丢成 ...992；int 直出十进制不经浮点
        Assert.True(Json.TryStringify(new FmtNode.Int(9007199254740993), out string json, out _));
        Assert.Equal("9007199254740993", json);
        Assert.True(Json.TryStringify(new FmtNode.Int(long.MinValue), out json, out _));
        Assert.Equal("-9223372036854775808", json);
        Assert.True(Json.TryStringify(new FmtNode.Int(long.MaxValue), out json, out _));
        Assert.Equal("9223372036854775807", json);
    }

    [Fact]
    public void Float_encoding_thresholds()
    {
        // Go encoding/json：abs ∈ [1e-6, 1e21) 定点，否则 e 形（负指数去前导零）
        Assert.Equal("123.456", StringifyOk(new FmtNode.Float(123.456)));
        Assert.Equal("100000000", StringifyOk(new FmtNode.Float(1e8)));
        Assert.Equal("100000000000000000000", StringifyOk(new FmtNode.Float(1e20)));
        Assert.Equal("1e+21", StringifyOk(new FmtNode.Float(1e21)));
        Assert.Equal("1e+100", StringifyOk(new FmtNode.Float(1e100)));
        Assert.Equal("0.000001", StringifyOk(new FmtNode.Float(1e-6))); // 端点归定点
        Assert.Equal("9.9e-7", StringifyOk(new FmtNode.Float(9.9e-7))); // 负指数去前导零
        Assert.Equal("1e-7", StringifyOk(new FmtNode.Float(1e-7)));
        Assert.Equal("0", StringifyOk(new FmtNode.Float(0.0)));
        Assert.Equal("0", StringifyOk(new FmtNode.Float(-0.0))); // interp 语义（非 Go json 的 -0）
        Assert.Equal("-2.5", StringifyOk(new FmtNode.Float(-2.5)));
    }

    [Fact]
    public void Float_specials_rejected()
    {
        Assert.False(Json.TryStringify(new FmtNode.Float(double.NaN), out _, out JsonError? err));
        Assert.Equal(new JsonError("JSON 序列化失败: json: unsupported value: NaN"), err);
        Assert.False(Json.TryStringify(new FmtNode.Float(double.PositiveInfinity), out _, out err));
        Assert.Equal(new JsonError("JSON 序列化失败: json: unsupported value: +Inf"), err);
        Assert.False(Json.TryStringify(new FmtNode.Float(double.NegativeInfinity), out _, out err));
        Assert.Equal(new JsonError("JSON 序列化失败: json: unsupported value: -Inf"), err);
    }

    [Fact]
    public void String_html_escaping_and_controls()
    {
        // HTML 转义开启（Go json 默认）：&lt; &gt; &amp;
        Assert.Equal("\"a\\u003cb\\u003e\\u0026c\"", StringifyOk(new FmtNode.Str("a<b>&c")));
        Assert.Equal("\"q\\\"q\\\\s\"", StringifyOk(new FmtNode.Str("q\"q\\s")));
        Assert.Equal("\"\\n\\t\"", StringifyOk(new FmtNode.Str("\n\t")));
        Assert.Equal("\"\\u0001\"", StringifyOk(new FmtNode.Str("\u0001")));
        // DEL 与 U+2028 直出（Go json 不转义；区别于 strconv.Quote）
        Assert.Equal("\"\u007f\"", StringifyOk(new FmtNode.Str("\u007f")));
        Assert.Equal("\"\u2028\"", StringifyOk(new FmtNode.Str("\u2028")));
        // astral 直出
        Assert.Equal("\"𝕏🦀\"", StringifyOk(new FmtNode.Str("𝕏🦀")));
    }

    private static string StringifyOk(FmtNode v)
    {
        Assert.True(Json.TryStringify(v, out string json, out JsonError? err));
        Assert.Null(err);
        return json;
    }
}

public class JsonCompositeTests
{
    [Fact]
    public void Map_keys_sorted_by_codepoint()
    {
        // 插入序 z,a,~,Z,\U{10000} → 码点序 Z(5A) < a(61) < z(7A) < ~(7E) < \U{10000}(10000)
        var m = new FmtNode.Map(
        [
            (new FmtNode.Str("z"), new FmtNode.Int(1)),
            (new FmtNode.Str("a"), new FmtNode.Int(2)),
            (new FmtNode.Str("~"), new FmtNode.Int(3)),
            (new FmtNode.Str("Z"), new FmtNode.Int(4)),
            (new FmtNode.Str("\U00010000"), new FmtNode.Int(5)),
        ]);
        Assert.True(Json.TryStringify(m, out string json, out JsonError? err));
        Assert.Null(err);
        Assert.Equal("{\"Z\":4,\"a\":2,\"z\":1,\"~\":3,\"\U00010000\":5}", json);

        // 嵌套映射同样排序
        var inner = new FmtNode.Map(
        [
            (new FmtNode.Str("b"), new FmtNode.Int(1)),
            (new FmtNode.Str("a"), new FmtNode.Int(2)),
        ]);
        var outer = new FmtNode.Map([(new FmtNode.Str("k"), inner)]);
        Assert.True(Json.TryStringify(outer, out json, out err));
        Assert.Null(err);
        Assert.Equal("{\"k\":{\"a\":2,\"b\":1}}", json);
    }

    [Fact]
    public void Map_key_order_is_codepoint_not_utf16()
    {
        // 码点序：U+FFFF (65535) < U+10000 (65536)。
        // 若误用 UTF-16 序（string.CompareOrdinal），\U{10000} 的代理首单元 0xD800 < 0xFFFF 会颠倒
        var m = new FmtNode.Map(
        [
            (new FmtNode.Str("\U00010000"), new FmtNode.Int(1)),
            (new FmtNode.Str("\uffff"), new FmtNode.Int(2)),
        ]);
        Assert.True(Json.TryStringify(m, out string json, out JsonError? err));
        Assert.Null(err);
        Assert.Equal("{\"\uffff\":2,\"\U00010000\":1}", json);
    }

    [Fact]
    public void Map_non_string_key_rejected()
    {
        var m = new FmtNode.Map([(new FmtNode.Int(1), new FmtNode.Str("x"))]);
        Assert.False(Json.TryStringify(m, out _, out JsonError? err));
        Assert.Equal(new JsonError("JSON 对象键须为 string，得到 int"), err);
    }

    [Fact]
    public void Arrays_and_nested_null()
    {
        var a = new FmtNode.Arr([new FmtNode.Int(1), new FmtNode.Str("a"), new FmtNode.Option(null)]);
        Assert.True(Json.TryStringify(a, out string json, out JsonError? err));
        Assert.Null(err);
        Assert.Equal("[1,\"a\",null]", json);
    }

    [Fact]
    public void Non_serializable_rejected_with_tag()
    {
        AssertError(new FmtNode.Option(new FmtNode.Int(1)), "Some 不可 JSON 序列化");
        AssertError(new FmtNode.Err(new FmtNode.Str("e")), "Err 不可 JSON 序列化");
        AssertError(new FmtNode.Ok(new FmtNode.Int(1)), "Ok 不可 JSON 序列化");
        AssertError(new FmtNode.EnumValue("Red", []), "Red 不可 JSON 序列化");
        AssertError(new FmtNode.Range(1, 2), "区间 不可 JSON 序列化");
        AssertError(new FmtNode.Struct("Point", [("x", new FmtNode.Int(1))]), "Point 不可 JSON 序列化");
        AssertError(new FmtNode.Ptr(new FmtNode.Int(1)), "引用 不可 JSON 序列化");
        AssertError(new FmtNode.Fn("f"), "函数 不可 JSON 序列化");

        static void AssertError(FmtNode v, string message)
        {
            Assert.False(Json.TryStringify(v, out _, out JsonError? err));
            Assert.Equal(new JsonError(message), err);
        }
    }
}
