namespace Lang.Runtime;

/// <summary>
/// 赋值目标位置（Place）——显式建模的左值地址（EVAL-ASSIGN-003）。
/// Place 是运行时「地址组件」的物化：解释器按规范先求值 target 的地址组件
/// （Ident 绑定；obj.f 的 obj；a[i] 的 obj、i），得到 Place 后再求值右值、
/// 最后经 <see cref="Machine.PlaceStore"/> 写入。
///
/// - <see cref="Var"/>：变量 cell（env + 槽位）。
/// - <see cref="Element"/>：slice 槽位（容器引用共享，直写）。
/// - <see cref="MapEntry"/>：映射条目（覆盖保插入位）。
/// - <see cref="Field"/>：struct 字段，**经 base 读出副本 → 改字段 → 写回 base**
///   （VALUE-ELEM-009：xs[i].f = v 不共享中间副本，写回槽位对容器别名可见）。
///
/// 引用（&x）不在 Place 构造面：E3012 规定 & 仅限变量；写穿引用由
/// <see cref="Machine.PlaceStore"/> 解析 <see cref="Value.Ref"/> 后落到内层 Place 完成
/// （VALUE-REF-003：经引用读写字段/索引即读写原值）。禁用 C# ref/out/ref struct 表达语言引用。
/// </summary>
public abstract record Place
{
    private Place()
    {
    }

    /// <summary>变量 cell。</summary>
    public sealed record Var(EnvId Env, uint Slot) : Place;

    /// <summary>slice 元素槽位。</summary>
    public sealed record Element(ValueId Slice, uint Index) : Place;

    /// <summary>映射条目（key 为规范键形）。</summary>
    public sealed record MapEntry(ValueId Map, MapKey Key) : Place;

    /// <summary>struct 字段：base 落点上的第 index 个字段。</summary>
    public sealed record Field(Place Base, uint Index) : Place;
}
