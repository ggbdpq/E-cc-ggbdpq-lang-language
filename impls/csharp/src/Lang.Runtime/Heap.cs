namespace Lang.Runtime;

/// <summary>堆槽位身份（arena 下标 newtype）。</summary>
public readonly record struct ValueId(uint Value);

/// <summary>映射槽位：插入序键 + 原键值（输出 {k: v} 与 keys() 需要原始键形态；后续覆盖不改写原键）。</summary>
public readonly record struct MapSlot(Value Key, Value Val);

/// <summary>
/// 保插入序的映射体（VALUE-MAP-006）。
/// Dictionary 只做键 → 下标索引，**迭代序一律来自插入序列表**——禁 Dictionary 迭代序外泄。
/// </summary>
public sealed class MapObj
{
    private readonly List<MapSlot> _slots = new();
    private readonly Dictionary<MapKey, int> _index = new();

    /// <summary>条目数。</summary>
    public int Len => _slots.Count;

    public bool IsEmpty => _slots.Count == 0;

    /// <summary>是否含键（has() 探测，RUNTIME-INDEX-001 的唯一合法探测方式）。</summary>
    public bool Contains(MapKey key) => _index.ContainsKey(key);

    /// <summary>取值（不含原键）。</summary>
    public bool TryGet(MapKey key, out Value value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            value = _slots[i].Val;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>插入/覆盖：已存在的键保持原插入位与原键形态（VALUE-MAP-006），新键追加到末尾。
    /// 载荷按值语义快照（struct 拷贝、容器共享索引）。</summary>
    public void Insert(MapKey key, Value original, Value value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            // 覆盖：保插入位与原键形态，只换值（VALUE-MAP-006）
            var slot = _slots[i];
            _slots[i] = new MapSlot(slot.Key, value.CloneValue());
            return;
        }

        _index[key] = _slots.Count;
        _slots.Add(new MapSlot(original.CloneValue(), value.CloneValue()));
    }

    /// <summary>插入序键值对（只读面；顺序 = 插入序）。</summary>
    public IReadOnlyList<MapSlot> Slots => _slots;
}

/// <summary>堆槽位对象：当前仅引用语义容器；struct/enum 内联于 <see cref="Value"/>。</summary>
public abstract record HeapObj
{
    private HeapObj()
    {
    }

    /// <summary>[T] 容器体（List 可变性仅供运行时内部写路径，公开读面为 IReadOnlyList）。</summary>
    public sealed record Slice(List<Value> Items) : HeapObj;

    /// <summary>map[K]V 容器体（插入序）。</summary>
    public sealed record Map(MapObj Obj) : HeapObj;
}

/// <summary>
/// 堆 arena：引用语义容器的槽位分配（VALUE-SHARE-002 / VALUE-MAP-006）。
/// slice 与 map 是语言中仅有的引用语义容器，以 arena 索引（<see cref="ValueId"/>）承载：
/// 值只持索引，共享即别名；原位变更对全部别名可见。struct/enum 为内联值，不入堆；
/// channel/env 各有专属表（类型不混用）。解释器进程生命周期 = 程序生命周期，只增不减（GC 委托宿主）。
/// </summary>
public sealed class HeapArena
{
    private readonly List<HeapObj> _slots = new();

    /// <summary>分配 slice 槽位，返回持索引的值。</summary>
    public Value AllocSlice(IReadOnlyList<Value> items)
    {
        var id = new ValueId((uint)_slots.Count);
        _slots.Add(new HeapObj.Slice(items.ToList()));
        return new Value.Slice(id);
    }

    /// <summary>分配 map 槽位，返回持索引的值。</summary>
    public Value AllocMap(MapObj obj)
    {
        var id = new ValueId((uint)_slots.Count);
        _slots.Add(new HeapObj.Map(obj));
        return new Value.Map(id);
    }

    private RtResult<HeapObj> Slot(ValueId id) =>
        id.Value < (uint)_slots.Count
            ? RtResult<HeapObj>.Ok(_slots[(int)id.Value])
            : RtResult<HeapObj>.Fail(RtError.Internal($"ValueId({id.Value}) 越界"));

    private static string KindOf(HeapObj obj) => obj is HeapObj.Slice ? "数组" : "映射";

    private static RtResult<T> Mismatch<T>(ValueId id, string want, HeapObj got) =>
        RtResult<T>.Fail(RtError.Internal($"ValueId({id.Value}) 期望 {want}，实际 {KindOf(got)}"));

    /// <summary>slice 槽位可变体（内部写路径）。</summary>
    private RtResult<List<Value>> SliceMut(ValueId id) =>
        Slot(id).Then(o => o is HeapObj.Slice s
            ? RtResult<List<Value>>.Ok(s.Items)
            : Mismatch<List<Value>>(id, "数组", o));

    /// <summary>slice 槽位只读面。</summary>
    public RtResult<IReadOnlyList<Value>> Slice(ValueId id) =>
        SliceMut(id).Then(items => RtResult<IReadOnlyList<Value>>.Ok(items));

    /// <summary>map 槽位只读面。</summary>
    public RtResult<MapObj> Map(ValueId id) =>
        Slot(id).Then(o => o is HeapObj.Map m
            ? RtResult<MapObj>.Ok(m.Obj)
            : Mismatch<MapObj>(id, "映射", o));

    // —— slice 操作 ——

    /// <summary>长度。</summary>
    public RtResult<int> SliceLen(ValueId id) => Slice(id).Map(items => items.Count);

    /// <summary>原位追加（VALUE-SHARE-002：push(arr, v) 对所有别名可见）。</summary>
    public RtResult SlicePush(ValueId id, Value v)
    {
        var r = SliceMut(id);
        if (!r.IsOk)
        {
            return RtResult.Fail(r.Error!);
        }

        r.Value.Add(v.CloneValue());
        return RtResult.Ok;
    }

    /// <summary>读元素：CloneValue 返回（VALUE-ELEM-009 读出即副本；struct 元素与源槽位脱钩）。
    /// 越界 → <see cref="RtErrorKind.Index"/>（RUNTIME-INDEX-001）。</summary>
    public RtResult<Value> SliceGet(ValueId id, uint index)
    {
        var r = Slice(id);
        if (!r.IsOk)
        {
            return RtResult<Value>.Fail(r.Error!);
        }

        var items = r.Value;
        return index < (uint)items.Count
            ? RtResult<Value>.Ok(items[(int)index].CloneValue())
            : RtResult<Value>.Fail(new RtError(RtErrorKind.Index, $"索引 {index} 越界（长度 {items.Count}）"));
    }

    /// <summary>写槽位。越界 → <see cref="RtErrorKind.Index"/>。</summary>
    public RtResult SliceSet(ValueId id, uint index, Value v)
    {
        var r = SliceMut(id);
        if (!r.IsOk)
        {
            return RtResult.Fail(r.Error!);
        }

        var items = r.Value;
        if (index >= (uint)items.Count)
        {
            return RtResult.Fail(new RtError(RtErrorKind.Index, $"索引 {index} 越界（长度 {items.Count}）"));
        }

        items[(int)index] = v.CloneValue();
        return RtResult.Ok;
    }

    // —— map 操作 ——

    /// <summary>条目数。</summary>
    public RtResult<int> MapLen(ValueId id) => Map(id).Map(m => m.Len);

    /// <summary>取值（m[k]）；键不存在 → <see cref="RtErrorKind.Index"/>（RUNTIME-INDEX-001，合法探测只有 has()）。</summary>
    public RtResult<Value> MapGet(ValueId id, MapKey key)
    {
        var r = Map(id);
        if (!r.IsOk)
        {
            return RtResult<Value>.Fail(r.Error!);
        }

        return r.Value.TryGet(key, out var v)
            ? RtResult<Value>.Ok(v.CloneValue())
            : RtResult<Value>.Fail(new RtError(RtErrorKind.Index, $"映射不存在的键 {key.Plain()}"));
    }

    /// <summary>has(m, k) 探测。</summary>
    public RtResult<bool> MapHas(ValueId id, MapKey key) => Map(id).Map(m => m.Contains(key));

    /// <summary>插入/覆盖（保持插入位，VALUE-MAP-006）。键类型非法 → <see cref="RtErrorKind.TypeCast"/>（E3014 的运行时面）。</summary>
    public RtResult MapInsert(ValueId id, Value keyValue, Value value)
    {
        var k = MapKey.FromValue(keyValue);
        if (k is null)
        {
            return RtResult.Fail(new RtError(RtErrorKind.TypeCast, $"映射键须为 int/string/bool，得到 {keyValue.TypeHint()}"));
        }

        return MapInsertKey(id, k.Value, keyValue, value);
    }

    /// <summary>以规范键形插入/覆盖（place_store 的 MapEntry 路径；键已是 MapKey）。</summary>
    public RtResult MapInsertKey(ValueId id, MapKey key, Value originalKey, Value value)
    {
        var r = Map(id);
        if (!r.IsOk)
        {
            return RtResult.Fail(r.Error!);
        }

        r.Value.Insert(key, originalKey, value);
        return RtResult.Ok;
    }

    /// <summary>插入序键列表（keys()/for-in 用；返回原键值副本）。</summary>
    public RtResult<IReadOnlyList<Value>> MapKeys(ValueId id) =>
        Map(id).Map<IReadOnlyList<Value>>(m => m.Slots.Select(s => s.Key.CloneValue()).ToArray());

    /// <summary>插入序值列表（values()/for-in 用）。</summary>
    public RtResult<IReadOnlyList<Value>> MapValues(ValueId id) =>
        Map(id).Map<IReadOnlyList<Value>>(m => m.Slots.Select(s => s.Val.CloneValue()).ToArray());
}
