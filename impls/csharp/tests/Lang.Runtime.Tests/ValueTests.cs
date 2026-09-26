namespace Lang.Runtime.Tests;

/// <summary>
/// Value 语义（specs/values.md）：类型名、语言级 ==（RUNTIME-EQ-003）、
/// compare（码点序）、truthy、MapKey、CloneValue（VALUE-COPY-001）。
/// 期望值全部手算自 specs/证据，不取自实现。
/// </summary>
public class ValueTests
{
    private static DefTables Defs() => DefTables.WithPrelude();

    // —— type_name（输出面/错误消息真值，来自证据 typeName 表）——

    [Fact]
    public void TypeName_ScalarAndContainers()
    {
        var d = Defs();
        Assert.Equal("int", new Value.Int(1).TypeName(d));
        Assert.Equal("float", new Value.Float(1.0).TypeName(d));
        Assert.Equal("bool", new Value.Bool(true).TypeName(d));
        Assert.Equal("string", T.S("x").TypeName(d));
        Assert.Equal("()", Value.Unit.Default.TypeName(d));
        Assert.Equal("区间", new Value.Range(0, 1).TypeName(d));
        Assert.Equal("数组", new Value.Slice(new ValueId(0)).TypeName(d));
        Assert.Equal("映射", new Value.Map(new ValueId(0)).TypeName(d));
        Assert.Equal("引用", new Value.Ref(new Place.Var(new EnvId(0), 0)).TypeName(d));
        // astral：字符串值的类型名与码点内容无关
        Assert.Equal("string", T.S("🦀𝕏").TypeName(d));
    }

    [Fact]
    public void TypeName_StructAndEnum_UseDefName()
    {
        var d = Defs();
        var s = d.RegisterStruct("P", new[] { "v" });
        var e = d.RegisterEnum("Color", new[] { "Red" });
        Assert.Equal("P", new Value.Struct(new StructVal(s, Array.Empty<Value>())).TypeName(d));
        Assert.Equal("Color", new Value.Enum(e, 0, Array.Empty<Value>()).TypeName(d));
    }

    // —— equals（RUNTIME-EQ-003：仅标量与 enum 可比）——

    [Fact]
    public void EqualsLang_ScalarsSameTypeOnly()
    {
        Assert.True(T.Ok(Value.EqualsLang(T.I(1), T.I(1))));
        Assert.False(T.Ok(Value.EqualsLang(T.I(1), T.I(2))));
        // 无隐式转换：int 与 float 不可比较
        Assert.Equal(RtErrorKind.Eq, T.Err(Value.EqualsLang(T.I(1), new Value.Float(1.0))).Kind);
        Assert.True(T.Ok(Value.EqualsLang(T.S("a"), T.S("a"))));
        Assert.True(T.Ok(Value.EqualsLang(new Value.Bool(true), new Value.Bool(true))));
    }

    [Fact]
    public void EqualsLang_StrIsOrdinal_AstralAndCombining()
    {
        // astral 码点：同 UTF-16 序列相等（含跨对象）
        Assert.True(T.Ok(Value.EqualsLang(T.S("🦀"), T.S("\U0001F980"))));
        // 语言 == 是 Ordinal：分解形（e + U+0301）与预合成 é 不相等（Go 字节相等语义）
        Assert.False(T.Ok(Value.EqualsLang(T.S("e\u0301"), T.S("é"))));
        // 大小写敏感
        Assert.False(T.Ok(Value.EqualsLang(T.S("X"), T.S("x"))));
    }

    [Fact]
    public void EqualsLang_Enum_RequiresSameDefAndCase()
    {
        var d = Defs();
        var e1 = d.RegisterEnum("Color", new[] { "Red", "Pair" });
        var e2 = d.RegisterEnum("Other", new[] { "Red" });
        Value.Enum Red(EnumDefId def) => new(def, 0, Array.Empty<Value>());
        var pair = new Value.Enum(e1, 1, new Value[] { T.I(1), T.I(2) });
        var pairEq = new Value.Enum(e1, 1, new Value[] { T.I(1), T.I(2) });
        var pairNe = new Value.Enum(e1, 1, new Value[] { T.I(1), T.I(3) });
        Assert.True(T.Ok(Value.EqualsLang(Red(e1), Red(e1))));
        Assert.False(T.Ok(Value.EqualsLang(Red(e1), Red(e2)))); // def 身份不同 → false
        Assert.False(T.Ok(Value.EqualsLang(Red(e1), pair))); // case 不同 → false
        Assert.True(T.Ok(Value.EqualsLang(pair, pairEq))); // 载荷递归比较
        Assert.False(T.Ok(Value.EqualsLang(pair, pairNe)));
        // astral 载荷递归相等
        var p1 = new Value.Enum(e1, 0, new Value[] { T.S("🦀") });
        var p2 = new Value.Enum(e1, 0, new Value[] { T.S("🦀") });
        Assert.True(T.Ok(Value.EqualsLang(p1, p2)));
    }

    [Fact]
    public void EqualsLang_ContainersRejected()
    {
        var err = T.Err(Value.EqualsLang(new Value.Slice(new ValueId(0)), new Value.Slice(new ValueId(0))));
        Assert.Equal(RtErrorKind.Eq, err.Kind);
        var sv = new StructVal(new StructDefId(0), Array.Empty<Value>());
        Assert.Equal(RtErrorKind.Eq, T.Err(Value.EqualsLang(new Value.Struct(sv), new Value.Struct(sv))).Kind);
        Assert.Equal(RtErrorKind.Eq, T.Err(Value.EqualsLang(Value.Unit.Default, Value.Unit.Default)).Kind);
    }

    // —— compare / truthy ——

    [Fact]
    public void Compare_SameTypeOnly()
    {
        Assert.Equal(-1, Value.Compare(T.I(1), T.I(2)));
        Assert.Equal(1, Value.Compare(new Value.Float(2.0), new Value.Float(1.0)));
        Assert.Equal(-1, Value.Compare(T.S("a"), T.S("b")));
        Assert.Null(Value.Compare(T.I(1), T.S("1")));
    }

    [Fact]
    public void Compare_StringIsCodePointOrder_NotUtf16Ordinal()
    {
        // 码点序：U+E000（私用区 BMP）< U+20000（astral 𠀀）。
        // C# string.Ordinal 的 UTF-16 序会把 U+20000 的高代理 0xD840 排在 0xE000 之前——方向相反。
        Assert.Equal(-1, Value.Compare(T.S("\uE000"), T.S("\U00020000")));
        Assert.Equal(1, Value.Compare(T.S("\U00020000"), T.S("\uE000")));
        // astral 与 astral：🦀 U+1F980 < 𝕏 U+1D54F？否——U+1D54F < U+1F980
        Assert.Equal(-1, Value.Compare(T.S("𝕏"), T.S("🦀")));
        // 前缀短的更小
        Assert.Equal(-1, Value.Compare(T.S("ab"), T.S("abc")));
        Assert.Equal(0, Value.Compare(T.S("中"), T.S("中")));
    }

    [Fact]
    public void Compare_FloatNanIsUnordered()
    {
        // IEEE 无序（TYPE-CONV-005 v1.0.1，ADR-0006/RS-007）：NaN → null，
        // 关系运算在 Ops 层按 IEEE 直落 false；== 仍 false、!= 恒 true（含自身）
        Assert.Null(Value.Compare(new Value.Float(double.NaN), new Value.Float(1.0)));
        Assert.Null(Value.Compare(new Value.Float(1.0), new Value.Float(double.NaN)));
        Assert.Null(Value.Compare(new Value.Float(double.NaN), new Value.Float(double.NaN)));
        Assert.False(T.Ok(Value.EqualsLang(new Value.Float(double.NaN), new Value.Float(double.NaN))));
        // == 为 false（含自身对自身）；!= 恒 true（EqualsLang 是 == 面，取反即 !=）
        Assert.False(T.Ok(Value.EqualsLang(new Value.Float(double.NaN), new Value.Float(1.0))));
        Assert.True(!T.Ok(Value.EqualsLang(new Value.Float(double.NaN), new Value.Float(double.NaN))));
        Assert.True(!T.Ok(Value.EqualsLang(new Value.Float(double.NaN), new Value.Float(1.0))));
    }

    [Fact]
    public void Truthy_BoolOnly()
    {
        Assert.True(T.Ok(new Value.Bool(true).Truthy()));
        Assert.False(T.Ok(new Value.Bool(false).Truthy()));
        Assert.Equal(RtErrorKind.TypeCast, T.Err(T.I(0).Truthy()).Kind);
    }

    // —— MapKey（E3014：键须 int/string/bool）——

    [Fact]
    public void MapKey_FromValue()
    {
        Assert.Equal(MapKey.FromInt(-3), MapKey.FromValue(T.I(-3)));
        Assert.Equal(MapKey.FromStr("k"), MapKey.FromValue(T.S("k")));
        Assert.Equal(MapKey.FromBool(true), MapKey.FromValue(new Value.Bool(true)));
        Assert.Null(MapKey.FromValue(new Value.Float(1.0)));
        Assert.Null(MapKey.FromValue(Value.Unit.Default));
        // astral 键形态保留
        Assert.Equal(MapKey.FromStr("🦀"), MapKey.FromValue(T.S("🦀")));
    }

    [Fact]
    public void MapKey_PlainForm()
    {
        Assert.Equal("42", MapKey.FromInt(42).Plain());
        Assert.Equal("-7", MapKey.FromInt(-7).Plain());
        Assert.Equal("false", MapKey.FromBool(false).Plain());
        Assert.Equal("k", MapKey.FromStr("k").Plain());
        Assert.Equal("𝕏", MapKey.FromStr("𝕏").Plain());
    }

    [Fact]
    public void MapKey_EqualsIsKindAwareAndOrdinal()
    {
        Assert.True(MapKey.FromInt(1) == MapKey.FromInt(1));
        Assert.False(MapKey.FromInt(1) == MapKey.FromBool(true));
        Assert.True(MapKey.FromStr("🦀") == MapKey.FromStr("\U0001F980"));
        Assert.False(MapKey.FromStr("e\u0301") == MapKey.FromStr("é"));
        Assert.Equal(MapKey.FromStr("a").GetHashCode(), MapKey.FromStr("a").GetHashCode());
    }

    // —— CloneValue（VALUE-COPY-001：struct 顶层逐字段 + 嵌套递归 + 容器字段共享）——

    [Fact]
    public void CloneValue_StructDeepCopyButSharesContainerField()
    {
        var heap = new HeapArena();
        var items = heap.AllocSlice(new Value[] { T.I(1) });
        var nested = new StructVal(new StructDefId(1), new Value[] { T.I(5) });
        var outer = new StructVal(
            new StructDefId(0),
            new Value[] { T.I(1), new Value.Struct(nested), new Value.Slice(T.IdOf(items)) });

        var copy = new Value.Struct(outer).CloneValue();
        var copyStruct = Assert.IsType<Value.Struct>(copy);
        // 顶层标量字段独立：改副本不影响源（不可变纪律下表现为对象不同 + 值独立）
        Assert.NotSame(outer, copyStruct.Val);
        Assert.Equal(new Value.Int(100), UpdateField(copyStruct, 0, T.I(100)).Val.Fields[0]);
        Assert.Equal(T.I(1), outer.Fields[0]);
        // 嵌套 struct 递归拷贝：副本嵌套对象与源不同
        Assert.IsType<Value.Struct>(copyStruct.Val.Fields[1]);
        Assert.NotSame(nested, Assert.IsType<Value.Struct>(copyStruct.Val.Fields[1]).Val);
        // 容器字段共享：经副本 push 源可见（VALUE-SHARE-002）
        var sharedId = T.IdOf(Assert.IsType<Value.Slice>(copyStruct.Val.Fields[2]));
        T.Ok(heap.SlicePush(sharedId, T.I(2)));
        Assert.Equal(2, T.Ok(heap.SliceLen(T.IdOf(items))));
    }

    private static Value.Struct UpdateField(Value.Struct sv, uint index, Value v)
    {
        var fields = sv.Val.Fields.ToArray();
        fields[index] = v;
        return new Value.Struct(new StructVal(sv.Val.Def, fields));
    }

    [Fact]
    public void CloneValue_EnumPayloadsRecursivelyCopied()
    {
        var nested = new StructVal(new StructDefId(0), new Value[] { T.I(7) });
        var e = new Value.Enum(new EnumDefId(0), 0, new Value[] { new Value.Struct(nested), T.S("𝕏") });
        var copy = e.CloneValue();
        var copyEnum = Assert.IsType<Value.Enum>(copy);
        Assert.NotSame(e, copyEnum);
        Assert.Equal(e, copyEnum); // 结构相等
        Assert.NotSame(nested, Assert.IsType<Value.Struct>(copyEnum.Payloads[0]).Val);
        // 标量载荷共享（不可变）
        Assert.Same(e.Payloads[1], copyEnum.Payloads[1]);
    }

    [Fact]
    public void CloneValue_ScalarsAndAddressesShareInstance()
    {
        Value v = T.S("🦀");
        Assert.Same(v, v.CloneValue());
        Value c = new Value.Chan(new ChannelId(3));
        Assert.Same(c, c.CloneValue());
        Value r = new Value.Ref(new Place.Var(new EnvId(0), 1));
        Assert.Same(r, r.CloneValue());
        Assert.Same(Value.Unit.Default, Value.Unit.Default.CloneValue());
    }
}
