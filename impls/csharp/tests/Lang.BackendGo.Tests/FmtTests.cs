using Lang.BackendGo;

namespace Lang.BackendGo.Tests;

// —— float（对照 Go strconv.FormatFloat('g',-1,64)；期望值手算并经本机 go 1.27 标准库对拍）——
public class FmtFloatTests
{
    [Fact]
    public void Float_no_trailing_zero_and_safe_zone()
    {
        // RUNTIME-OUTPUT-004 安全区 |x| ∈ [1e-4, 1e6)：定点无尾零
        Assert.Equal("1", Fmt.FmtFloat(1.0));
        Assert.Equal("3", Fmt.FmtFloat(3.0));
        Assert.Equal("100", Fmt.FmtFloat(100.0));
        Assert.Equal("0.1", Fmt.FmtFloat(0.1));
        Assert.Equal("-2.5", Fmt.FmtFloat(-2.5));
        Assert.Equal("0.25", Fmt.FmtFloat(0.25));
        Assert.Equal("0.5", Fmt.FmtFloat(0.5));
        // 安全区端点：1e-4 定点（含），1e6 科学计数（开区间右端）
        Assert.Equal("0.0001", Fmt.FmtFloat(1e-4));
        Assert.Equal("999999", Fmt.FmtFloat(999999.0));
        Assert.Equal("1e+06", Fmt.FmtFloat(1e6));
    }

    [Fact]
    public void Float_scientific_switch()
    {
        // 指数 < -4 或 >= 6 走 %e：d.ddde±XX（指数至少两位、恒带符号）
        Assert.Equal("1e-05", Fmt.FmtFloat(1e-5));
        Assert.Equal("1.5e-07", Fmt.FmtFloat(1.5e-7));
        Assert.Equal("1e+21", Fmt.FmtFloat(1e21));
        Assert.Equal("1e+100", Fmt.FmtFloat(1e100));
        Assert.Equal("1.2345678e+06", Fmt.FmtFloat(1234567.8));
        // 边界内保持定点
        Assert.Equal("123456.789", Fmt.FmtFloat(123456.789));
    }

    [Fact]
    public void Float_specials()
    {
        Assert.Equal("0", Fmt.FmtFloat(0.0));
        Assert.Equal("-0", Fmt.FmtFloat(-0.0)); // IEEE 负零保留符号
        Assert.Equal("NaN", Fmt.FmtFloat(double.NaN));
        Assert.Equal("+Inf", Fmt.FmtFloat(double.PositiveInfinity));
        Assert.Equal("-Inf", Fmt.FmtFloat(double.NegativeInfinity));
    }

    [Fact]
    public void Float_shortest_round_trip_digits()
    {
        // 手算：double(1/3) 最短往返小数 = 0.3333333333333333（Go 对拍一致）
        Assert.Equal("0.3333333333333333", Fmt.FmtFloat(1.0 / 3.0));
        // 运行时 0.1+0.2 的最短往返（C# 运行时求值 ≠ Go 编译期常量折叠的 0.3）
        Assert.Equal("0.30000000000000004", Fmt.FmtFloat(0.1 + 0.2));
        Assert.Equal("5e-324", Fmt.FmtFloat(5e-324)); // 最小子正规
        Assert.Equal("1.7976931348623157e+308", Fmt.FmtFloat(double.MaxValue));
    }
}

// —— 嵌套字符串 quote（Go strconv.Quote 语义；期望值手算并经 go 1.27 对拍）——
public class QuoteGoTests
{
    [Fact]
    public void Plain_and_simple_escapes()
    {
        Assert.Equal("\"hi\"", Fmt.QuoteGo("hi"));
        Assert.Equal("\"a\\\"b\"", Fmt.QuoteGo("a\"b"));
        Assert.Equal("\"a\\\\b\"", Fmt.QuoteGo("a\\b"));
        Assert.Equal("\"l\\nm\\tr \"", Fmt.QuoteGo("l\nm\tr "));
    }

    [Fact]
    public void Control_chars()
    {
        Assert.Equal("\"\\a\"", Fmt.QuoteGo("\a"));
        Assert.Equal("\"\\b\"", Fmt.QuoteGo("\b"));
        Assert.Equal("\"\\v\"", Fmt.QuoteGo("\v"));
        Assert.Equal("\"\\f\"", Fmt.QuoteGo("\f"));
        Assert.Equal("\"\\x01\"", Fmt.QuoteGo("\u0001"));
        Assert.Equal("\"\\x1f\"", Fmt.QuoteGo("\u001f"));
        Assert.Equal("\"\\x7f\"", Fmt.QuoteGo("\u007f")); // DEL → \xNN
    }

    [Fact]
    public void Unicode_passthrough_policy()
    {
        // 打印 Unicode 直出：中文（Lo）、组合记号（Mn）、emoji（So）
        Assert.Equal("\"中文\"", Fmt.QuoteGo("中文"));
        Assert.Equal("\"e\u0301\"", Fmt.QuoteGo("e\u0301"));
        Assert.Equal("\"\U0001f600\"", Fmt.QuoteGo("\U0001f600"));
        // 非打印转义：Zl / Zs / Cf / Co / 非字符
        Assert.Equal("\"\\u2028\"", Fmt.QuoteGo("\u2028"));
        Assert.Equal("\"\\u00a0\"", Fmt.QuoteGo("\u00a0")); // NBSP（Zs）
        Assert.Equal("\"\\u200d\"", Fmt.QuoteGo("\u200d")); // ZWJ（Cf）
        Assert.Equal("\"\\u00ad\"", Fmt.QuoteGo("\u00ad")); // 软连字符（Cf）
        Assert.Equal("\"\\ue000\"", Fmt.QuoteGo("\uE000")); // 私用区（十六进制小写，Go 对拍一致）
        Assert.Equal("\"\\U0010ffff\"", Fmt.QuoteGo("\U0010ffff")); // >0xFFFF 用 \U
        // 未分配码点 Cn → 非打印（Go IsPrint 同判；Rune 类别表精确覆盖）
        Assert.Equal("\"\\u0378\"", Fmt.QuoteGo("\u0378"));
    }

    [Fact]
    public void Astral_code_points_iterate_per_codepoint()
    {
        // astral 码点（𝕏 U+1D54F、🦀 U+1F980）是单个码点：按码点直出，不按代理半拆
        Assert.Equal("\"𝕏🦀\"", Fmt.QuoteGo("𝕏🦀"));
    }
}

// —— 标量与容器规范打印形（RUNTIME-OUTPUT-003，fmtVal 分派镜像）——
public class FmtValueTests
{
    [Fact]
    public void Int_full_range_decimal()
    {
        Assert.Equal("-9223372036854775808", Fmt.FmtValue(new FmtNode.Int(long.MinValue), true));
        Assert.Equal("9223372036854775807", Fmt.FmtValue(new FmtNode.Int(long.MaxValue), true));
        Assert.Equal("0", Fmt.FmtValue(new FmtNode.Int(0), true));
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

        var st = new FmtNode.Struct("Point",
            [("x", new FmtNode.Int(1)), ("y", new FmtNode.Float(2.5)), ("label", new FmtNode.Str("a"))]);
        Assert.Equal("Point{x: 1, y: 2.5, label: \"a\"}", Fmt.FmtValue(st, true));

        Assert.Equal("1..5", Fmt.FmtValue(new FmtNode.Range(1, 5), true));
        Assert.Equal("()", Fmt.FmtValue(FmtNode.Unit.Instance, true));
    }

    [Fact]
    public void Map_prints_insertion_order()
    {
        // 插入序 b, 1, true——与键的任何排序序不同（禁 Dictionary 迭代序外泄）
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
        // error 的 msg 用 strconv.Quote 形（→JS 后端同点位用 JSON 形，分歧面见各后端）
        Assert.Equal("error{msg: \"boom\\n\"}", Fmt.FmtValue(new FmtNode.Error("boom\n"), true));
    }

    [Fact]
    public void Fn_ptr_and_nested_composition()
    {
        Assert.Equal("<fn main>", Fmt.FmtValue(new FmtNode.Fn("main"), true));
        Assert.Equal("<fn 匿名>", Fmt.FmtValue(new FmtNode.Fn(null), true));
        Assert.Equal("&5", Fmt.FmtValue(new FmtNode.Ptr(new FmtNode.Int(5)), true));
        // 嵌套：数组里的 Option 里的字符串 → 嵌套形（带引号）；astral 直出
        var nested = new FmtNode.Arr([new FmtNode.Option(new FmtNode.Str("𝕏🦀"))]);
        Assert.Equal("[Some(\"𝕏🦀\")]", Fmt.FmtValue(nested, true));
    }

    [Fact]
    public void Println_args_space_joined()
    {
        var args = new List<FmtNode> { new FmtNode.Str("a b"), new FmtNode.Int(1), new FmtNode.Bool(false) };
        Assert.Equal("a b 1 false", Fmt.FmtArgs(args));
        Assert.Equal("", Fmt.FmtArgs([]));
    }
}
