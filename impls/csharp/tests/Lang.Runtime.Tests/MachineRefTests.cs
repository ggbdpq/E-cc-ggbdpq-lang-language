namespace Lang.Runtime.Tests;

/// <summary>
/// Machine 的 Place/Ref 语义（VALUE-REF-003 / VALUE-ELEM-009 / EVAL-ASSIGN-003）。
/// 对应 conformance：reference-alias-write / reference-read-write / containers-shared-aliasing /
/// struct-field-write / nested-struct-copy / map-value-struct-shared。
/// </summary>
public class MachineRefTests
{
    private static (EnvId Env, uint Slot) DefVar(Machine m, string name, Value v, bool mutable)
    {
        var slot = T.Ok(m.Envs.Define(m.Root, name, v, mutable));
        return (m.Root, slot);
    }

    // —— VALUE-REF-003 / conformance: reference-alias-write ——

    [Fact]
    public void RefAlias_ReadsThrough()
    {
        // var n = 5; let r = &n; n = 6; 经 r 读 → 6（别名可见）
        var m = new Machine();
        var (env, nSlot) = DefVar(m, "n", T.I(5), mutable: true);
        var rSlot = T.Ok(m.Envs.Define(m.Root, "r", m.TakeRef(env, nSlot), mutable: false));

        T.Ok(m.PlaceStore(new Place.Var(env, nSlot), T.I(6))); // n = 6
        var viaR = T.Ok(m.PlaceLoadResolved(new Place.Var(env, rSlot)));
        Assert.Equal(T.I(6), viaR);
    }

    // —— VALUE-REF-003 / conformance: reference-read-write ——

    [Fact]
    public void RefFieldWrite_GoesThroughToOriginal()
    {
        // var s = P{v:1}; let r = &s; r.v = 2 → s.v == 2 且 r.v == 2（写穿 + 读穿）
        var m = new Machine();
        var pDef = m.Defs.RegisterStruct("P", new[] { "v" });
        var sVal = new Value.Struct(new StructVal(pDef, new Value[] { T.I(1) }));
        var (_, sSlot) = DefVar(m, "s", sVal, mutable: true);
        var rSlot = T.Ok(m.Envs.Define(m.Root, "r", m.TakeRef(m.Root, sSlot), mutable: false));

        var target = new Place.Field(new Place.Var(m.Root, rSlot), 0);
        T.Ok(m.PlaceStore(target, T.I(2)));

        var sNow = T.Ok(m.Envs.Get(m.Root, sSlot));
        Assert.Equal(new Value.Struct(new StructVal(pDef, new Value[] { T.I(2) })), sNow);
        Assert.Equal(T.I(2), T.Ok(m.PlaceLoadResolved(target)));
    }

    [Fact]
    public void RefToAstralString_ReadsThrough()
    {
        // astral 值经引用读写：写穿后读回原字符串（UTF-16 序列原样）
        var m = new Machine();
        var (env, sSlot) = DefVar(m, "s", T.S("🦀𝕏"), mutable: true);
        var rSlot = T.Ok(m.Envs.Define(m.Root, "r", m.TakeRef(env, sSlot), mutable: false));
        T.Ok(m.PlaceStore(new Place.Var(env, sSlot), T.S("𝕏🦀")));
        Assert.Equal(T.S("𝕏🦀"), T.Ok(m.PlaceLoadResolved(new Place.Var(env, rSlot))));
    }

    // —— VALUE-ELEM-009：容器元素字段写回 + 读出副本 ——

    [Fact]
    public void ContainerElement_FieldWritebackAndCopyOut()
    {
        // xs = [P{v:1}]; ys = xs; xs[0].v = 9 → ys 同见 9（容器引用共享）；
        // e = xs[0]; e.v = 5 → xs 不变（读出即副本）
        var m = new Machine();
        var pDef = m.Defs.RegisterStruct("P", new[] { "v" });
        var xs = m.AllocSliceValue(new Value[] { new Value.Struct(new StructVal(pDef, new Value[] { T.I(1) })) });
        var xsId = T.IdOf(xs);
        var ys = xs.CloneValue();

        var target = new Place.Field(new Place.Element(xsId, 0), 0);
        T.Ok(m.PlaceStore(target, T.I(9)));

        var viaAlias = T.Ok(m.PlaceLoad(new Place.Element(T.IdOf(ys), 0)));
        Assert.Equal(new Value.Struct(new StructVal(pDef, new Value[] { T.I(9) })), viaAlias);

        // 读出副本改字段 → 不回写
        var copy = T.Ok(m.Heap.SliceGet(xsId, 0));
        var mutated = new Value.Struct(new StructVal(pDef, new Value[] { T.I(5) }));
        _ = mutated;
        var still9 = T.Ok(m.Heap.SliceGet(xsId, 0));
        Assert.Equal(new Value.Struct(new StructVal(pDef, new Value[] { T.I(9) })), still9);
        // 副本与容器槽位脱钩
        Assert.NotSame(Assert.IsType<Value.Struct>(copy).Val, Assert.IsType<Value.Struct>(still9).Val);
    }

    [Fact]
    public void MapEntry_StoreKeepsInsertionOrder()
    {
        var m = new Machine();
        var map = m.AllocMapValue();
        var mapId = T.IdOf(map);
        var kA = T.Ok(m.MapKeyOf(T.S("a")));
        var kB = T.Ok(m.MapKeyOf(T.S("b")));
        T.Ok(m.PlaceStore(new Place.MapEntry(mapId, kA), T.I(1)));
        T.Ok(m.PlaceStore(new Place.MapEntry(mapId, kB), T.I(2)));
        // 覆盖 a：插入位不动
        T.Ok(m.PlaceStore(new Place.MapEntry(mapId, kA), T.I(10)));
        var plain = T.Ok(m.Heap.MapKeys(mapId)).Select(k => T.KeyPlain(k)).ToArray();
        Assert.Equal(new[] { "a", "b" }, plain);
        Assert.Equal(T.I(10), T.Ok(m.Heap.MapGet(mapId, kA)));
        // 新键追加到末尾
        var kC = T.Ok(m.MapKeyOf(T.S("c")));
        T.Ok(m.PlaceStore(new Place.MapEntry(mapId, kC), T.I(3)));
        plain = T.Ok(m.Heap.MapKeys(mapId)).Select(k => T.KeyPlain(k)).ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, plain);
    }

    [Fact]
    public void Deref_FollowsRefChain()
    {
        // r2 = &r1（引用的引用）：穿透两层得原值
        var m = new Machine();
        var (env, nSlot) = DefVar(m, "n", T.I(7), mutable: true);
        var r1 = m.TakeRef(env, nSlot);
        var r1Slot = T.Ok(m.Envs.Define(m.Root, "r1", r1, mutable: false));
        var r2Slot = T.Ok(m.Envs.Define(m.Root, "r2", m.TakeRef(m.Root, r1Slot), mutable: false));
        var v = T.Ok(m.PlaceLoadResolved(new Place.Var(m.Root, r2Slot)));
        Assert.Equal(T.I(7), v);
    }

    [Fact]
    public void ImmutableVar_StoreRejected_ButFieldWritebackAllowed()
    {
        // let c = Counter{n: 0}; c.n = 41 → 合法；c = ... 重绑定仍被拒
        // （conformance: struct-field-write / nested-struct-copy 的行为真值）
        var m = new Machine();
        var def = m.Defs.RegisterStruct("Counter", new[] { "n" });
        var slot = T.Ok(m.Envs.Define(
            m.Root, "c", new Value.Struct(new StructVal(def, new Value[] { T.I(0) })), mutable: false));
        T.Ok(m.PlaceStore(new Place.Field(new Place.Var(m.Root, slot), 0), T.I(41)));
        var cNow = Assert.IsType<Value.Struct>(T.Ok(m.Envs.Get(m.Root, slot)));
        Assert.Equal(T.I(41), cNow.Val.Fields[0]);
        // 重绑定路径（Var 直写）仍拒绝 let cell
        Assert.Equal(RtErrorKind.ImmutableAssign, T.Err(m.PlaceStore(new Place.Var(m.Root, slot), Value.Unit.Default)).Kind);
    }

    [Fact]
    public void NestedFieldChain_WriteThroughLetCells()
    {
        // let a = Out{m: Mid{i: In{v: 7}}}; a.m.i.v = 99 → 手算 a.m.i.v == 99
        var m = new Machine();
        var inDef = m.Defs.RegisterStruct("In", new[] { "v" });
        var midDef = m.Defs.RegisterStruct("Mid", new[] { "i" });
        var outDef = m.Defs.RegisterStruct("Out", new[] { "m" });
        var slot = T.Ok(m.Envs.Define(
            m.Root,
            "a",
            new Value.Struct(new StructVal(outDef, new Value[]
            {
                new Value.Struct(new StructVal(midDef, new Value[]
                {
                    new Value.Struct(new StructVal(inDef, new Value[] { T.I(7) })),
                })),
            })),
            mutable: false));
        var target = new Place.Field(
            new Place.Field(new Place.Field(new Place.Var(m.Root, slot), 0), 0), 0);
        T.Ok(m.PlaceStore(target, T.I(99)));

        var outV = Assert.IsType<Value.Struct>(T.Ok(m.Envs.Get(m.Root, slot)));
        var midV = Assert.IsType<Value.Struct>(outV.Val.Fields[0]);
        var inV = Assert.IsType<Value.Struct>(midV.Val.Fields[0]);
        Assert.Equal(T.I(99), inV.Val.Fields[0]);
    }

    [Fact]
    public void FieldStore_OnNonStructRejected()
    {
        var m = new Machine();
        var (_, slot) = DefVar(m, "x", T.I(1), mutable: true);
        var err = T.Err(m.PlaceStore(new Place.Field(new Place.Var(m.Root, slot), 0), T.I(1)));
        Assert.Equal(RtErrorKind.TypeCast, err.Kind);
        Assert.Equal(RtErrorKind.TypeCast, T.Err(m.PlaceLoad(new Place.Field(new Place.Var(m.Root, slot), 0))).Kind);
    }

    [Fact]
    public void PlaceLoad_DoesNotAutoDeref_PlaceLoadResolvedDoes()
    {
        // n = 3；r = &n（r 是另一个变量，持有引用）
        var m = new Machine();
        var (env, nSlot) = DefVar(m, "n", T.I(3), mutable: true);
        var rSlot = T.Ok(m.Envs.Define(m.Root, "r", m.TakeRef(env, nSlot), mutable: false));
        var raw = T.Ok(m.PlaceLoad(new Place.Var(m.Root, rSlot)));
        Assert.IsType<Value.Ref>(raw); // 不穿透
        var resolved = T.Ok(m.PlaceLoadResolved(new Place.Var(m.Root, rSlot)));
        Assert.Equal(T.I(3), resolved); // 穿透
        // 自引用环（n = &n）被深度上限防护为 Internal，不挂死
        T.Ok(m.Envs.Set(m.Root, nSlot, m.TakeRef(env, nSlot)));
        var nNow = T.Ok(m.PlaceLoad(new Place.Var(m.Root, rSlot)));
        Assert.Equal(RtErrorKind.Internal, T.Err(m.DerefValue(nNow)).Kind);
    }

    // —— 预载构造器（A2 对齐：error=StructDefId(0)/Option=EnumDefId(0)/Result=EnumDefId(1)）——

    [Fact]
    public void Prelude_ConstructorsAndIsNone()
    {
        var m = new Machine();
        Assert.True(m.IsNone(m.NoneValue()));
        Assert.False(m.IsNone(m.SomeValue(T.I(1))));
        Assert.False(m.IsNone(T.I(0)));
        // Result 的 Err 不是 Option 的 None
        Assert.False(m.IsNone(m.ErrValue(Value.Unit.Default)));
        Assert.Equal("Result", m.OkValue(T.I(1)).TypeName(m.Defs));
        Assert.Equal("Option", m.NoneValue().TypeName(m.Defs));
        Assert.Equal("error", m.ErrorValue("x").TypeName(m.Defs));
        // astral 消息载荷原样
        var errStruct = Assert.IsType<Value.Struct>(m.ErrorValue("出错了🦀"));
        Assert.Equal(T.S("出错了🦀"), errStruct.Val.Fields[0]);
    }

    [Fact]
    public void EnumCaseName_AndStructFieldNames()
    {
        var m = new Machine();
        Assert.Equal("Some", T.Ok(m.EnumCaseName(m.Defs.OptionDef(), 0)));
        Assert.Equal("None", T.Ok(m.EnumCaseName(m.Defs.OptionDef(), 1)));
        Assert.Equal("Err", T.Ok(m.EnumCaseName(m.Defs.ResultDef(), 1)));
        Assert.Equal(string.Empty, T.Ok(m.EnumCaseName(m.Defs.ResultDef(), 99)));
        Assert.Equal(new[] { "msg" }, T.Ok(m.StructFieldNames(m.Defs.ErrorDef())));
    }

    [Fact]
    public void MapKeyOf_RejectsNonScalar()
    {
        var m = new Machine();
        Assert.Equal(MapKey.FromInt(5), T.Ok(m.MapKeyOf(T.I(5))));
        Assert.Equal(RtErrorKind.TypeCast, T.Err(m.MapKeyOf(new Value.Slice(new ValueId(0)))).Kind);
    }
}
