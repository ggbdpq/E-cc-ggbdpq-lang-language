using Lang.BackendJs;

namespace Lang.BackendJs.Tests;

// —— float（langGoFloatStr 目标语义 = Go strconv 'g'；期望值手算并经 go 1.27 标准库对拍，
//    与 Lang.BackendGo.Tests 同矩阵）——
public class FmtFloatTests
{
    [Fact]
    public void Float_no_trailing_zero_and_safe_zone()
    {
        Assert.Equal("1", Fmt.FmtFloat(1.0));
        Assert.Equal("0.1", Fmt.FmtFloat(0.1));
        Assert.Equal("-2.5", Fmt.FmtFloat(-2.5));
        Assert.Equal("0.0001", Fmt.FmtFloat(1e-4));
        Assert.Equal("999999", Fmt.FmtFloat(999999.0));
        Assert.Equal("1e+06", Fmt.FmtFloat(1e6));
    }

    [Fact]
    public void Float_scientific_switch()
    {
        Assert.Equal("1e-05", Fmt.FmtFloat(1e-5));
        Assert.Equal("1.5e-07", Fmt.FmtFloat(1.5e-7));
        Assert.Equal("1e+21", Fmt.FmtFloat(1e21));
        Assert.Equal("1e+100", Fmt.FmtFloat(1e100));
        Assert.Equal("1.2345678e+06", Fmt.FmtFloat(1234567.8));
        Assert.Equal("123456.789", Fmt.FmtFloat(123456.789));
    }

    [Fact]
    public void Float_specials()
    {
        Assert.Equal("0", Fmt.FmtFloat(0.0));
        Assert.Equal("-0", Fmt.FmtFloat(-0.0));
        Assert.Equal("NaN", Fmt.FmtFloat(double.NaN));
        Assert.Equal("+Inf", Fmt.FmtFloat(double.PositiveInfinity));
        Assert.Equal("-Inf", Fmt.FmtFloat(double.NegativeInfinity));
        Assert.Equal("5e-324", Fmt.FmtFloat(5e-324));
        Assert.Equal("1.7976931348623157e+308", Fmt.FmtFloat(double.MaxValue));
        Assert.Equal("0.3333333333333333", Fmt.FmtFloat(1.0 / 3.0));
    }
}

// —— 嵌套字符串 quote（JSON.stringify 语义；与 →Go 后端的分歧面逐码点手算锁定，SPEC-GAP-RS-001）——
public class QuoteJsonTests
{
    [Fact]
    public void Basic_escapes()
    {
        Assert.Equal("\"hi\"", Fmt.QuoteJson("hi"));
        Assert.Equal("\"a\\\"b\\\\c\"", Fmt.QuoteJson("a\"b\\c"));
        Assert.Equal("\"d\\u0001e\"", Fmt.QuoteJson("d\u0001e"));
        Assert.Equal("\"x\\ny\\tz\\r\"", Fmt.QuoteJson("x\ny\tz\r"));
        // < 0x20 的非短形控制符走 \u00XX 小写
        Assert.Equal("\"\\u001f\"", Fmt.QuoteJson("\u001f"));
    }

    [Fact]
    public void Unicode_raw_policy()
    {
        // 与 strconv.Quote 的分歧面：以下码点 JSON.stringify 直出（→Go 后端转义）
        Assert.Equal("\"\u2028\"", Fmt.QuoteJson("\u2028")); // Zl：go 形 \u2028
        Assert.Equal("\"\u007f\"", Fmt.QuoteJson("\u007f")); // DEL：go 形 \x7f
        Assert.Equal("\"\u00a0\"", Fmt.QuoteJson("\u00a0")); // NBSP：go 形 \u00a0
        Assert.Equal("\"\uE000\"", Fmt.QuoteJson("\uE000")); // 私用区：go 形 \ue000
        // 打印 Unicode 直出（两后端一致）
        Assert.Equal("\"中文\"", Fmt.QuoteJson("中文"));
        Assert.Equal("\"\U0001f600\"", Fmt.QuoteJson("\U0001f600"));
        // astral 码点按码点迭代直出，不按代理半拆
        Assert.Equal("\"𝕏🦀\"", Fmt.QuoteJson("𝕏🦀"));
    }
}

// —— 标量与容器规范打印形（langGoFmt 分派镜像；形状与 →Go 后端同构）——
public class FmtValueTests
{
    [Fact]
    public void Int_full_range_decimal()
    {
        Assert.Equal("-9223372036854775808", Fmt.FmtValue(new FmtNode.Int(long.MinValue), true));
        Assert.Equal("9223372036854775807", Fmt.FmtValue(new FmtNode.Int(long.MaxValue), true));
    }

    [Fact]
    public void Top_string_unquoted_nested_quoted()
    {
        var s = new FmtNode.Str("a b");
        Assert.Equal("a b", Fmt.FmtValue(s, true));
        Assert.Equal("\"a b\"", Fmt.FmtValue(s, false));
    }

    [Fact]
    public void Container_shapes()
    {
        var arr = new FmtNode.Arr([new FmtNode.Int(1), new FmtNode.Str("a"), new FmtNode.Float(2.5)]);
        Assert.Equal("[1, \"a\", 2.5]", Fmt.FmtValue(arr, true));

        var st = new FmtNode.Struct("Point", [("x", new FmtNode.Int(1)), ("y", new FmtNode.Float(2.5))]);
        Assert.Equal("Point{x: 1, y: 2.5}", Fmt.FmtValue(st, true));
        Assert.Equal("-3..4", Fmt.FmtValue(new FmtNode.Range(-3, 4), true));
    }

    [Fact]
    public void Map_prints_insertion_order()
    {
        var m = new FmtNode.Map(
        [
            (new FmtNode.Str("b"), new FmtNode.Int(2)),
            (new FmtNode.Int(1), new FmtNode.Str("x")),
            (new FmtNode.Bool(true), FmtNode.Unit.Instance),
        ]);
        Assert.Equal("{\"b\": 2, 1: \"x\", true: ()}", Fmt.FmtValue(m, true));
    }

    [Fact]
    public void Enum_opt_res_error_shapes()
    {
        Assert.Equal("Red", Fmt.FmtValue(new FmtNode.EnumValue("Red", []), true));
        Assert.Equal(
            "Pair(1, \"a\")",
            Fmt.FmtValue(new FmtNode.EnumValue("Pair", [new FmtNode.Int(1), new FmtNode.Str("a")]), true));
        Assert.Equal("Some(1)", Fmt.FmtValue(new FmtNode.Option(new FmtNode.Int(1)), true));
        Assert.Equal("None", Fmt.FmtValue(new FmtNode.Option(null), true));
        Assert.Equal("Ok(\"a\")", Fmt.FmtValue(new FmtNode.Ok(new FmtNode.Str("a")), true));
        Assert.Equal("Err(3)", Fmt.FmtValue(new FmtNode.Err(new FmtNode.Int(3)), true));
        // error 的 msg 用 JSON 引号形（jsprelude langGoFmt 的 __langGoErr 分支）
        Assert.Equal("error{msg: \"boom\\n\"}", Fmt.FmtValue(new FmtNode.Error("boom\n"), true));
        // 分歧面进容器：嵌套 error msg 在 →JS 侧也是 JSON 形
        var nested = new FmtNode.Arr([new FmtNode.Error("a\u2028b")]);
        Assert.Equal("[error{msg: \"a\u2028b\"}]", Fmt.FmtValue(nested, true));
    }

    [Fact]
    public void Fn_ptr_and_nested_composition()
    {
        Assert.Equal("<fn main>", Fmt.FmtValue(new FmtNode.Fn("main"), true));
        Assert.Equal("<fn 匿名>", Fmt.FmtValue(new FmtNode.Fn(null), true));
        Assert.Equal("&5", Fmt.FmtValue(new FmtNode.Ptr(new FmtNode.Int(5)), true));
        var nested = new FmtNode.Arr([new FmtNode.Option(new FmtNode.Str("x"))]);
        Assert.Equal("[Some(\"x\")]", Fmt.FmtValue(nested, true));
    }

    [Fact]
    public void Println_args_space_joined()
    {
        var args = new List<FmtNode> { new FmtNode.Str("a b"), new FmtNode.Int(1), new FmtNode.Bool(false) };
        Assert.Equal("a b 1 false", Fmt.FmtArgs(args));
        Assert.Equal("", Fmt.FmtArgs([]));
    }
}
