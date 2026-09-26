using Lang.Core;

namespace Lang.Runtime.Tests;

/// <summary>
/// 内建注册表与调用口（specs/runtime.md RUNTIME-OUTPUT-003 输出面 / CHAN 面）：
/// 20 名字面锁定、元数检查、Host 路由、recv Result 语义、astral 码点行为。
/// </summary>
public class BuiltinTests
{
    /// <summary>核心 20 名字面（specs + 运行时证据 CORE_BUILTINS + stdlib）。</summary>
    private static readonly string[] CoreNames =
    [
        "println", "print", "len", "str", "int", "float", "keys", "values", "has", "push",
        "assert", "err", "bytes", "char", "makeChan", "send", "recv", "isNone", "jsonParse", "jsonStringify",
    ];

    private static (Machine M, VecSink Sink) Mach()
    {
        var m = new Machine();
        return (m, new VecSink());
    }

    private static Value Call(Machine m, OutputSink sink, string name, params Value[] args)
    {
        var id = m.Builtins.Lookup(name) ?? throw new InvalidOperationException($"内建 {name} 未注册");
        return T.Ok(m.CallBuiltin(sink, new TestFmt(), id, args));
    }

    private static RtError CallErr(Machine m, OutputSink sink, string name, params Value[] args)
    {
        var id = m.Builtins.Lookup(name) ?? throw new InvalidOperationException($"内建 {name} 未注册");
        return T.Err(m.CallBuiltin(sink, new TestFmt(), id, args));
    }

    // —— 注册表名字面锁定 ——

    [Fact]
    public void Registry_LocksExactlyTwentyCoreNames()
    {
        var reg = BuiltinRegistry.Core();
        Assert.Equal(20, reg.Count);
        foreach (var name in CoreNames)
        {
            Assert.True(reg.Lookup(name) is not null, $"缺少内建 {name}");
        }

        // 名字面恰好等于 20 个：无多余、无缺失
        Assert.Equal(CoreNames.Length, reg.Count);
        // 首个登记 println、Host 类别仅 jsonParse/jsonStringify
        Assert.Equal(BuiltinKind.Println, reg.Kind(reg.Lookup("println")!.Value));
        Assert.Equal(BuiltinKind.Host, reg.Kind(reg.Lookup("jsonParse")!.Value));
        Assert.Equal(BuiltinKind.Host, reg.Kind(reg.Lookup("jsonStringify")!.Value));
        Assert.Equal(BuiltinKind.Send, reg.Kind(reg.Lookup("send")!.Value));
    }

    [Fact]
    public void Registry_RegisterHost_ExtendsAndLookup()
    {
        var reg = BuiltinRegistry.Core();
        var before = reg.Count;
        var id = reg.RegisterHost("http.get");
        Assert.Equal(before + 1, reg.Count);
        Assert.Equal(BuiltinKind.Host, reg.Kind(id));
        Assert.Equal("http.get", reg.Name(id));
        Assert.Null(reg.Lookup("nope"));
    }

    // —— 输出面 RUNTIME-OUTPUT-003 ——

    [Fact]
    public void PrintlnPrint_OutputContract()
    {
        var (m, sink) = Mach();
        // 空格分隔 + 换行
        Call(m, sink, "println", T.I(1), T.S("a"));
        Assert.Equal("1 a\n", sink.Text);
        // print 无换行
        var (_, sink2) = Mach();
        Call(m, sink2, "print", T.I(1));
        Assert.Equal("1", sink2.Text);
        // astral 原样透传（经 ValueFormatter 钩子，运行时不二次编码）
        var (_, sink3) = Mach();
        Call(m, sink3, "println", T.S("🦀𝕏"));
        Assert.Equal("🦀𝕏\n", sink3.Text);
    }

    [Fact]
    public void Str_ReturnsFormatterTopForm()
    {
        var (m, sink) = Mach();
        var v = Call(m, sink, "str", T.S("𝕏"));
        Assert.Equal(T.S("𝕏"), v);
    }

    // —— 元数与路由 ——

    [Fact]
    public void Arity_Checked()
    {
        var (m, sink) = Mach();
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "len").Kind);
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "makeChan", T.I(1)).Kind);
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "assert", new Value.Bool(true), T.S("x"), T.I(3)).Kind);
    }

    [Fact]
    public void HostBuiltin_RoutedAsUnsupportedHost()
    {
        var (m, sink) = Mach();
        Assert.Equal(BuiltinKind.Host, m.Builtins.Kind(m.Builtins.Lookup("jsonParse")!.Value));
        Assert.Equal(RtErrorKind.UnsupportedHost, CallErr(m, sink, "jsonParse", T.S("{}")).Kind);
        Assert.Equal(RtErrorKind.UnsupportedHost, CallErr(m, sink, "jsonStringify", T.S("🦀")).Kind);
    }

    // —— len（码点数）/ 区间 ——

    [Fact]
    public void Len_CountsCodePoints()
    {
        var (m, sink) = Mach();
        // lexical-grammar.md：len() 返回码点数（"hél" = 3）
        Assert.Equal(T.I(3), Call(m, sink, "len", T.S("hél")));
        // astral：🦀𝕏 = 2 码点（string.Length 会给 4 个 char——这正是对抗轴）
        Assert.Equal(T.I(2), Call(m, sink, "len", T.S("🦀𝕏")));
        Assert.Equal(T.I(1), Call(m, sink, "len", T.S("🦀")));
        // 区间：半开；hi <= lo → 0
        Assert.Equal(T.I(3), Call(m, sink, "len", new Value.Range(2, 5)));
        Assert.Equal(T.I(0), Call(m, sink, "len", new Value.Range(5, 2)));
        // 数组/映射
        var arr = m.AllocSliceValue(new Value[] { T.I(1), T.I(2), T.I(3) });
        Assert.Equal(T.I(3), Call(m, sink, "len", arr));
        var map = m.AllocMapValue();
        Assert.Equal(T.I(0), Call(m, sink, "len", map));
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "len", T.I(1)).Kind);
    }

    // —— chan 三件套 ——

    [Fact]
    public void Recv_ReturnsResultValues()
    {
        // CHAN-RECV-003：有值 Ok(v)；空 → Err("channel 空")（载荷文本逐字冻结）
        var (m, sink) = Mach();
        var ch = Call(m, sink, "makeChan");
        Call(m, sink, "send", ch, T.I(5));
        var ok = Call(m, sink, "recv", ch);
        var okEnum = Assert.IsType<Value.Enum>(ok);
        Assert.Equal(m.Defs.ResultDef(), okEnum.Def);
        Assert.Equal(0u, okEnum.Case);
        Assert.Equal(new Value[] { T.I(5) }, okEnum.Payloads);

        var errV = Call(m, sink, "recv", ch);
        var errEnum = Assert.IsType<Value.Enum>(errV);
        Assert.Equal(m.Defs.ResultDef(), errEnum.Def);
        Assert.Equal(1u, errEnum.Case);
        Assert.Equal(new Value[] { T.S("channel 空") }, errEnum.Payloads);
    }

    [Fact]
    public void Send_RejectsNonChan_AndFullBuffer()
    {
        var (m, sink) = Mach();
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "send", T.I(1), Value.Unit.Default).Kind);
        var ch = m.MakeChanValue();
        var chId = Assert.IsType<Value.Chan>(ch).Id;
        for (long i = 0; i < ChannelTable.ChanCap; i++)
        {
            T.Ok(m.Chans.Send(chId, T.I(i)));
        }

        Assert.Equal(RtErrorKind.ChanFull, CallErr(m, sink, "send", ch, T.I(-1)).Kind);
        // astral 载荷经 send 入队快照
        var ch2 = Call(m, sink, "makeChan");
        Call(m, sink, "send", ch2, T.S("𝕏"));
        Call(m, sink, "send", ch2, T.S("🦀"));
        Assert.Equal(T.S("𝕏"), Call(m, sink, "recv", ch2).As<Value.Enum>().Payloads[0]);
        Assert.Equal(T.S("🦀"), Call(m, sink, "recv", ch2).As<Value.Enum>().Payloads[0]);
    }

    [Fact]
    public void Send_ReturnsUnit()
    {
        var (m, sink) = Mach();
        var ch = Call(m, sink, "makeChan");
        Assert.IsType<Value.Chan>(ch); // makeChan 返回 chan 值
        Assert.Equal(Value.Unit.Default, Call(m, sink, "send", ch, T.I(1))); // send 恒返回 ()
    }

    // —— 纯值内建 ——

    [Fact]
    public void KeysValues_InsertionOrder()
    {
        var (m, sink) = Mach();
        var map = m.AllocMapValue();
        var mapId = T.IdOf(map);
        T.Ok(m.Heap.MapInsert(mapId, T.S("b"), T.I(2)));
        T.Ok(m.Heap.MapInsert(mapId, T.S("a"), T.I(1)));
        var keys = Call(m, sink, "keys", map);
        var keyItems = T.Ok(m.Heap.Slice(T.IdOf(keys)));
        Assert.Equal(new Value[] { T.S("b"), T.S("a") }, keyItems);
        var values = Call(m, sink, "values", map);
        Assert.Equal(new Value[] { T.I(2), T.I(1) }, T.Ok(m.Heap.Slice(T.IdOf(values))));
        // astral 键插入序
        T.Ok(m.Heap.MapInsert(mapId, T.S("🦀"), T.I(3)));
        Assert.Equal(new Value[] { T.S("b"), T.S("a"), T.S("🦀") },
            T.Ok(m.Heap.Slice(T.IdOf(Call(m, sink, "keys", map)))));
    }

    [Fact]
    public void Has_ProbesOnly()
    {
        var (m, sink) = Mach();
        var map = m.AllocMapValue();
        T.Ok(m.Heap.MapInsert(T.IdOf(map), T.S("k"), T.I(1)));
        Assert.Equal(new Value.Bool(true), Call(m, sink, "has", map, T.S("k")));
        Assert.Equal(new Value.Bool(false), Call(m, sink, "has", map, T.S("🦀")));
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "has", map, new Value.Float(1)).Kind);
    }

    [Fact]
    public void Push_InPlaceVisibleThroughAlias()
    {
        // VALUE-SHARE-002：push 原位追加对所有别名可见
        var (m, sink) = Mach();
        var a = m.AllocSliceValue(new Value[] { T.I(1) });
        var b = a.CloneValue();
        var ret = Call(m, sink, "push", a, T.I(2));
        Assert.Equal(a, ret); // push 返回数组本身
        Assert.Equal(2, T.Ok(m.Heap.SliceLen(T.IdOf(b))));
        // 变长：一次 push 多个
        Call(m, sink, "push", a, T.I(3), T.I(4));
        Assert.Equal(4, T.Ok(m.Heap.SliceLen(T.IdOf(b))));
    }

    [Fact]
    public void Assert_PassAndFail()
    {
        var (m, sink) = Mach();
        Assert.Equal(Value.Unit.Default, Call(m, sink, "assert", new Value.Bool(true)));
        var err = CallErr(m, sink, "assert", new Value.Bool(false), T.S("oops"));
        Assert.Equal(RtErrorKind.Assert, err.Kind);
        Assert.Contains("oops", err.Message);
        Assert.Equal(RtErrorKind.Assert, CallErr(m, sink, "assert", new Value.Bool(false)).Kind);
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "assert", T.I(1)).Kind);
    }

    [Fact]
    public void Err_BuildsErrorStruct()
    {
        var (m, sink) = Mach();
        var v = Call(m, sink, "err", T.S("boom"));
        var sv = Assert.IsType<Value.Struct>(v);
        Assert.Equal(m.Defs.ErrorDef(), sv.Val.Def);
        Assert.Equal(new Value[] { T.S("boom") }, sv.Val.Fields);
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "err", T.I(1)).Kind);
    }

    [Fact]
    public void Bytes_SplitsUtf8()
    {
        var (m, sink) = Mach();
        // 手算："h" = 0x68；"é" = U+00E9 = UTF-8 C3 A9
        var v = Call(m, sink, "bytes", T.S("hé"));
        Assert.Equal(new Value[] { T.I(0x68), T.I(0xC3), T.I(0xA9) }, T.Ok(m.Heap.Slice(T.IdOf(v))));
        // astral：🦀 = U+1F980 = UTF-8 F0 9F A6 80（4 字节）
        var v2 = Call(m, sink, "bytes", T.S("🦀"));
        Assert.Equal(new Value[] { T.I(0xF0), T.I(0x9F), T.I(0xA6), T.I(0x80) }, T.Ok(m.Heap.Slice(T.IdOf(v2))));
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "bytes", T.I(1)).Kind);
    }

    [Fact]
    public void Char_ValidAndInvalidCodePoints()
    {
        var (m, sink) = Mach();
        // 证据口径：代理区/越界/负数 → U+FFFD
        Assert.Equal(T.S("\uFFFD"), Call(m, sink, "char", T.I(0xD800)));
        Assert.Equal(T.S("\uFFFD"), Call(m, sink, "char", T.I(-1)));
        Assert.Equal(T.S("\uFFFD"), Call(m, sink, "char", T.I(0x110000)));
        Assert.Equal(T.S("中"), Call(m, sink, "char", T.I(0x4E2D)));
        // astral：char(0x1F980) = 🦀（单码点 → 2 char 的 UTF-16 对）
        Assert.Equal(T.S("🦀"), Call(m, sink, "char", T.I(0x1F980)));
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "char", T.S("x")).Kind);
    }

    [Fact]
    public void Int_Casts()
    {
        var (m, sink) = Mach();
        // Go int64()：向零截断（手算：-2.7 → -2；2.9 → 2）
        Assert.Equal(T.I(-2), Call(m, sink, "int", new Value.Float(-2.7)));
        Assert.Equal(T.I(2), Call(m, sink, "int", new Value.Float(2.9)));
        Assert.Equal(T.I(42), Call(m, sink, "int", T.S("42")));
        Assert.Equal(T.I(42), Call(m, sink, "int", T.I(42)));
        // 越界/非有限 → i64::MIN（Go amd64 口径，SPEC-GAP 登记）
        Assert.Equal(T.I(long.MinValue), Call(m, sink, "int", new Value.Float(9.3e18)));
        Assert.Equal(T.I(long.MinValue), Call(m, sink, "int", new Value.Float(double.NaN)));
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "int", T.S("x")).Kind);
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "int", new Value.Bool(true)).Kind);
    }

    [Fact]
    public void Float_Casts()
    {
        var (m, sink) = Mach();
        Assert.Equal(new Value.Float(1.5), Call(m, sink, "float", new Value.Float(1.5)));
        Assert.Equal(new Value.Float(2.0), Call(m, sink, "float", T.I(2)));
        Assert.Equal(new Value.Float(0.5), Call(m, sink, "float", T.S("0.5")));
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "float", T.S("x")).Kind);
        Assert.Equal(RtErrorKind.TypeCast, CallErr(m, sink, "float", T.S("🦀")).Kind);
    }

    [Fact]
    public void IsNone_OnlyOptionNoneCase()
    {
        var (m, sink) = Mach();
        Assert.Equal(new Value.Bool(true), Call(m, sink, "isNone", m.NoneValue()));
        Assert.Equal(new Value.Bool(false), Call(m, sink, "isNone", m.SomeValue(T.I(1))));
        Assert.Equal(new Value.Bool(false), Call(m, sink, "isNone", m.ErrValue(Value.Unit.Default)));
        Assert.Equal(new Value.Bool(false), Call(m, sink, "isNone", T.I(0)));
        Assert.Equal(new Value.Bool(false), Call(m, sink, "isNone", T.S("🦀")));
    }

    // —— 函数命名 ——

    [Fact]
    public void FnName_RegisteredAndMissing()
    {
        var m = new Machine();
        var f = m.RegisterFunction("work");
        Assert.Equal("work", m.FnName(f));
        Assert.Equal(string.Empty, m.FnName(new FunctionId(99)));
        // 既存 id 补登记（含空洞）
        T.Ok(m.SetFnName(new FunctionId(5), "late"));
        Assert.Equal("late", m.FnName(new FunctionId(5)));
        Assert.Equal("work", m.FnName(f));
    }
}

/// <summary>Assert.IsType 链的缩写。</summary>
internal static class ValueCastExt
{
    public static T As<T>(this Value v) where T : Value => Assert.IsType<T>(v);
}
