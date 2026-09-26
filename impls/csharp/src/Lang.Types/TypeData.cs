using Lang.Core;

namespace Lang.Types;

/// <summary>名义 struct 定义的身份（<see cref="TypeTables"/> 的 struct def 表下标，只增不减）。</summary>
public readonly record struct StructDefId(uint Value);

/// <summary>名义 enum 定义的身份（<see cref="TypeTables"/> 的 enum def 表下标，只增不减）。</summary>
public readonly record struct EnumDefId(uint Value);

/// <summary>struct 字段定义（注册时可先占位、注解解析后回填——def 表可变位）。</summary>
public sealed class FieldDef
{
    public required string Name { get; set; }
    public required TypeId Ty { get; set; }
}

/// <summary>名义 struct 定义（TYPE-STRUCT-003：字段完全相同的两个 struct 亦不兼容）。</summary>
public sealed class StructDef
{
    public required string Name { get; set; }
    public List<FieldDef> Fields { get; set; } = [];
}

/// <summary>enum case 定义（载荷按位置）。</summary>
public sealed class EnumCaseDef
{
    public required string Name { get; set; }
    public List<TypeId> Payloads { get; set; } = [];
}

/// <summary>名义 enum 定义（含预载 Option/Result）。</summary>
public sealed class EnumDef
{
    public required string Name { get; set; }
    public List<EnumCaseDef> Cases { get; set; } = [];
}

/// <summary>
/// 类型的结构描述（specs/type-system.md TYPE-KINDS-001 全量变体；intern 键，
/// <see cref="TypeId"/> 是它的 arena 身份）。泛型类型参数是擦除占位（GEN-DECL-001）。
/// </summary>
public abstract record TypeData
{
    /// <summary>错误传播占位（已有诊断，下游静默）。</summary>
    public sealed record Invalid : TypeData;

    /// <summary><c>int</c>（64 位有符号）。</summary>
    public sealed record Int : TypeData;

    /// <summary><c>float</c>（64 位）。</summary>
    public sealed record Float : TypeData;

    /// <summary><c>bool</c>。</summary>
    public sealed record Bool : TypeData;

    /// <summary><c>string</c>（不可变，按码点索引）。</summary>
    public sealed record Str : TypeData;

    /// <summary>预载 <c>error</c> struct 的静态类型。</summary>
    public sealed record Error : TypeData;

    /// <summary>unit <c>()</c>。</summary>
    public sealed record Unit : TypeData;

    /// <summary>区间 <c>lo..hi</c>（半开，int 端点）。</summary>
    public sealed record Range : TypeData;

    /// <summary><c>any</c>：动态边界（jsonParse/extern），双向放行（TYPE-ANY-002）。</summary>
    public sealed record Any : TypeData;

    /// <summary><c>Unknown</c>：推断不出的内部状态，保守放行，非逃生口（TYPE-ANY-002）。</summary>
    public sealed record Unknown : TypeData;

    /// <summary>不可达/无值类型（保留）。</summary>
    public sealed record Never : TypeData;

    /// <summary><c>[T]</c> 数组（引用语义）。</summary>
    public sealed record Slice(TypeId Elem) : TypeData;

    /// <summary><c>map[K]V</c>（K ∈ int/string/bool，保插入序）。</summary>
    public sealed record Map(TypeId Key, TypeId Val) : TypeData;

    /// <summary><c>&amp;T</c>（仅变量可取）。</summary>
    public sealed record Ref(TypeId Elem) : TypeData;

    /// <summary><c>chan[T]</c>。</summary>
    public sealed record Chan(TypeId Elem) : TypeData;

    /// <summary>
    /// <c>fn(T...): R</c>（结构兼容，TYPE-FN-004）。参数表参与结构相等：
    /// record 默认等值按引用比集合，这里改为逐元素。
    /// </summary>
    public sealed record Fn(IReadOnlyList<TypeId> Params, TypeId Ret) : TypeData
    {
        public bool Equals(Fn? other) =>
            other is not null && Params.SequenceEqual(other.Params) && Ret == other.Ret;

        public override int GetHashCode()
        {
            var h = new System.HashCode();
            foreach (var p in Params) h.Add(p);
            h.Add(Ret);
            return h.ToHashCode();
        }
    }

    /// <summary>名义 struct。</summary>
    public sealed record Struct(StructDefId Def) : TypeData;

    /// <summary>名义 enum。</summary>
    public sealed record Enum(EnumDefId Def) : TypeData;

    /// <summary><c>Option[T]</c>（预载代数类型）。</summary>
    public sealed record Option(TypeId Elem) : TypeData;

    /// <summary><c>Result[T, E]</c>（E 侧冻结为 error，GEN-SUBST-003）。</summary>
    public sealed record Result(TypeId Ok, TypeId Err) : TypeData;

    /// <summary>泛型类型参数（如 <c>fn[T]</c> 里的 <c>T</c>；擦除占位，彼此同一）。</summary>
    public sealed record TypeParam(string Name) : TypeData;
}
