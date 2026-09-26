using Lang.BackendJs;

namespace Lang.BackendJs.Tests;

// —— jsonStringify（RUNTIME-JSON-002 硬契约，与 →Go 后端同文；期望值手算 + go 1.27 对拍；
//    -0.0 → "0" 为解释器语义口径）——
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
        Assert.Equal("true", StringifyOk(new FmtNode.Bool(true)));
        Assert.Equal("false", StringifyOk(new FmtNode.Bool(false)));
        Assert.Equal("\"hi\"", StringifyOk(new FmtNode.Str("hi")));
    }

    [Fact]
    public void Int64_full_precision()
    {
        // 2^53+1 走 f64 会丢精度；int 直出十进制
        Assert.Equal("9007199254740993", StringifyOk(new FmtNode.Int(9007199254740993)));
        Assert.Equal("-9223372036854775808", StringifyOk(new FmtNode.Int(long.MinValue)));
        Assert.Equal("9223372036854775807", StringifyOk(new FmtNode.Int(long.MaxValue)));
    }

    [Fact]
    public void Float_encoding_thresholds()
    {
        Assert.Equal("123.456", StringifyOk(new FmtNode.Float(123.456)));
        // jsprelude 旧形在此处错发 1e+08（SPEC-GAP-RS-002）；契约值是定点
        Assert.Equal("100000000", StringifyOk(new FmtNode.Float(1e8)));
        Assert.Equal("100000000000000000000", StringifyOk(new FmtNode.Float(1e20)));
        Assert.Equal("1e+21", StringifyOk(new FmtNode.Float(1e21)));
        Assert.Equal("1e+100", StringifyOk(new FmtNode.Float(1e100)));
        Assert.Equal("0.000001", StringifyOk(new FmtNode.Float(1e-6)));
        Assert.Equal("9.9e-7", StringifyOk(new FmtNode.Float(9.9e-7)));
        Assert.Equal("1e-7", StringifyOk(new FmtNode.Float(1e-7)));
        Assert.Equal("0", StringifyOk(new FmtNode.Float(0.0)));
        Assert.Equal("0", StringifyOk(new FmtNode.Float(-0.0))); // interp 语义
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
        Assert.Equal("\"a\\u003cb\\u003e\\u0026c\"", StringifyOk(new FmtNode.Str("a<b>&c")));
        Assert.Equal("\"q\\\"q\\\\s\"", StringifyOk(new FmtNode.Str("q\"q\\s")));
        Assert.Equal("\"\\n\\t\"", StringifyOk(new FmtNode.Str("\n\t")));
        Assert.Equal("\"\\u0001\"", StringifyOk(new FmtNode.Str("\u0001")));
        // DEL 与 U+2028 直出（Go json 不转义）
        Assert.Equal("\"\u007f\"", StringifyOk(new FmtNode.Str("\u007f")));
        Assert.Equal("\"\u2028\"", StringifyOk(new FmtNode.Str("\u2028")));
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
        // JS 默认 sort 是 UTF-16 序（\U{10000} 的代理项 0xD800 会排在 "Z" 前）；
        // 契约要求码点序：Z(5A) < a(61) < z(7A) < ~(7E) < \U{10000}(10000)
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
        // 码点序：U+FFFF (65535) < U+10000 (65536)；UTF-16 序会颠倒
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
        AssertError(new FmtNode.EnumValue("Red", []), "Red 不可 JSON 序列化");
        AssertError(new FmtNode.Range(1, 2), "区间 不可 JSON 序列化");
        AssertError(new FmtNode.Struct("Point", [("x", new FmtNode.Int(1))]), "Point 不可 JSON 序列化");

        static void AssertError(FmtNode v, string message)
        {
            Assert.False(Json.TryStringify(v, out _, out JsonError? err));
            Assert.Equal(new JsonError(message), err);
        }
    }
}
