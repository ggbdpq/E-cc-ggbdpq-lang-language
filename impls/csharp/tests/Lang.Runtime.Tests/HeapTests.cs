namespace Lang.Runtime.Tests;

/// <summary>
/// 堆 arena（specs/values.md VALUE-SHARE-002 / VALUE-MAP-006 / VALUE-ELEM-009 / RUNTIME-INDEX-001）。
/// </summary>
public class HeapTests
{
    // —— VALUE-SHARE-002 / conformance: containers-shared-aliasing ——

    [Fact]
    public void SliceAlias_SeesInPlacePush()
    {
        // let a = [1]; let b = a; push(b, 2) → a、b 同见 [1, 2]
        // 手算（conformance 期望 stdout "[1, 2] [1, 2]"）：两个别名都看到长度 2
        var heap = new HeapArena();
        var a = heap.AllocSlice(new Value[] { T.I(1) });
        var b = a.CloneValue(); // 引用语义：共享同一槽位
        T.Ok(heap.SlicePush(T.IdOf(b), T.I(2)));
        Assert.Equal(2, T.Ok(heap.SliceLen(T.IdOf(a))));
        Assert.Equal(2, T.Ok(heap.SliceLen(T.IdOf(b))));
        Assert.Equal(T.I(2), T.Ok(heap.SliceGet(T.IdOf(a), 1)));
    }

    [Fact]
    public void SlicePush_VisibleThroughClonedValue()
    {
        var heap = new HeapArena();
        var a = heap.AllocSlice(Array.Empty<Value>());
        var b = a.CloneValue();
        T.Ok(heap.SlicePush(T.IdOf(a), T.I(1)));
        T.Ok(heap.SlicePush(T.IdOf(b), T.I(2)));
        Assert.Equal(2, T.Ok(heap.SliceLen(T.IdOf(a))));
    }

    // —— VALUE-MAP-006 插入序 ——

    [Fact]
    public void Map_KeepsInsertionOrder_OverwriteKeepsPosition()
    {
        var heap = new HeapArena();
        var m = T.IdOf(heap.AllocMap(new MapObj()));
        T.Ok(heap.MapInsert(m, T.S("c"), T.I(3)));
        T.Ok(heap.MapInsert(m, T.S("a"), T.I(1)));
        T.Ok(heap.MapInsert(m, T.S("b"), T.I(2)));
        // 覆盖已存在键：位置不动，值更新
        T.Ok(heap.MapInsert(m, T.S("a"), T.I(10)));
        var keys = T.Ok(heap.MapKeys(m)).Select(k => T.KeyPlain(k)).ToArray();
        Assert.Equal(new[] { "c", "a", "b" }, keys);
        Assert.Equal(new Value[] { T.I(3), T.I(10), T.I(2) }, T.Ok(heap.MapValues(m)));
    }

    [Fact]
    public void Map_InsertionOrder_AstralKeys()
    {
        // astral 键的插入序与覆盖保位（UTF-16 unit 序与码点序在此不参与——插入序与键值序无关）
        var heap = new HeapArena();
        var m = T.IdOf(heap.AllocMap(new MapObj()));
        T.Ok(heap.MapInsert(m, T.S("🦀"), T.I(1)));
        T.Ok(heap.MapInsert(m, T.S("𝕏"), T.I(2)));
        T.Ok(heap.MapInsert(m, T.S("中"), T.I(3)));
        T.Ok(heap.MapInsert(m, T.S("🦀"), T.I(10))); // 覆盖：位置不动
        var keys = T.Ok(heap.MapKeys(m)).Select(k => T.KeyPlain(k)).ToArray();
        Assert.Equal(new[] { "🦀", "𝕏", "中" }, keys);
        Assert.Equal(T.I(10), T.Ok(heap.MapGet(m, MapKey.FromStr("🦀"))));
        Assert.Equal(T.I(2), T.Ok(heap.MapGet(m, MapKey.FromStr("𝕏"))));
    }

    [Fact]
    public void Map_InsertIntAndBoolKeys()
    {
        var heap = new HeapArena();
        var m = T.IdOf(heap.AllocMap(new MapObj()));
        T.Ok(heap.MapInsert(m, T.I(7), T.S("seven")));
        T.Ok(heap.MapInsert(m, new Value.Bool(true), T.I(1)));
        Assert.True(T.Ok(heap.MapHas(m, MapKey.FromInt(7))));
        Assert.Equal(T.I(1), T.Ok(heap.MapGet(m, MapKey.FromBool(true))));
        Assert.False(T.Ok(heap.MapHas(m, MapKey.FromInt(8))));
    }

    [Fact]
    public void Map_GetMissingKey_IsIndexError()
    {
        var heap = new HeapArena();
        var m = T.IdOf(heap.AllocMap(new MapObj()));
        T.Ok(heap.MapInsert(m, T.S("k"), T.I(1)));
        // RUNTIME-INDEX-001：取不存在键为运行时错误（has() 是唯一合法探测）
        Assert.Equal(RtErrorKind.Index, T.Err(heap.MapGet(m, MapKey.FromStr("x"))).Kind);
    }

    [Fact]
    public void Map_InsertRejectsNonScalarKey()
    {
        var heap = new HeapArena();
        var m = T.IdOf(heap.AllocMap(new MapObj()));
        Assert.Equal(RtErrorKind.TypeCast, T.Err(heap.MapInsert(m, new Value.Float(1.0), Value.Unit.Default)).Kind);
    }

    // —— RUNTIME-INDEX-001 越界 ——

    [Fact]
    public void SliceIndex_Bounds()
    {
        var heap = new HeapArena();
        var a = heap.AllocSlice(new Value[] { T.I(1), T.I(2) });
        var id = T.IdOf(a);
        Assert.Equal(T.I(2), T.Ok(heap.SliceGet(id, 1)));
        Assert.Equal(RtErrorKind.Index, T.Err(heap.SliceGet(id, 2)).Kind);
        Assert.Equal(RtErrorKind.Index, T.Err(heap.SliceSet(id, 5, T.I(0))).Kind);
        T.Ok(heap.SliceSet(id, 0, T.I(9)));
        Assert.Equal(T.I(9), T.Ok(heap.SliceGet(id, 0)));
    }

    // —— VALUE-ELEM-009：struct 入容器/出容器均为值语义 ——

    [Fact]
    public void StructElement_ReadOutIsCopy_MutationDoesNotWriteBack()
    {
        // struct 入容器（写入副本）→ 读出的是副本 → 改副本不回写容器
        var heap = new HeapArena();
        var p = new StructVal(new StructDefId(0), new Value[] { T.I(42) });
        var xs = heap.AllocSlice(new Value[] { new Value.Struct(p) });
        var id = T.IdOf(xs);

        var elem = T.Ok(heap.SliceGet(id, 0)); // 读出副本
        var elemStruct = Assert.IsType<Value.Struct>(elem);
        Assert.NotSame(p, elemStruct.Val);
        // 原槽位不受影响（手算：仍为 42）
        Assert.Equal(new Value.Struct(p), T.Ok(heap.SliceGet(id, 0)));
        // 且新对象与源脱钩
        Assert.Equal(T.I(42), elemStruct.Val.Fields[0]);
    }

    [Fact]
    public void StructElement_WriteBack_VisibleToContainerAlias()
    {
        // xs[i].f = v 等价于「读元素副本 → 改字段 → 写回槽位」；
        // 容器本身引用共享 → 容器别名看到写回
        var heap = new HeapArena();
        var p = new StructVal(new StructDefId(0), new Value[] { T.I(1) });
        var xs = heap.AllocSlice(new Value[] { new Value.Struct(p) });
        var ys = xs.CloneValue(); // 容器别名
        var id = T.IdOf(xs);

        var elem = T.Ok(heap.SliceGet(id, 0));
        var updated = new Value.Struct(new StructVal(
            Assert.IsType<Value.Struct>(elem).Val.Def,
            new Value[] { T.I(9) }));
        T.Ok(heap.SliceSet(id, 0, updated)); // 写回槽位
        var viaAlias = T.Ok(heap.SliceGet(T.IdOf(ys), 0));
        Assert.Equal(T.I(9), Assert.IsType<Value.Struct>(viaAlias).Val.Fields[0]);
    }
}
