using System.Globalization;
using Lang.Core;

namespace Lang.Runtime;

/// <summary>struct 实例（内联值；CloneValue = VALUE-COPY-001 深拷贝）。Value 对象按不可变纪律使用：写路径一律构造新对象。</summary>
public sealed class StructVal : IEquatable<StructVal>
{
    public StructDefId Def { get; }

    public IReadOnlyList<Value> Fields { get; }

    public StructVal(StructDefId def, IReadOnlyList<Value> fields)
    {
        Def = def;
        Fields = fields;
    }

    public bool Equals(StructVal? other) =>
        other is not null && Def == other.Def && Fields.SequenceEqual(other.Fields);

    public override bool Equals(object? obj) => Equals(obj as StructVal);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Def);
        foreach (var f in Fields)
        {
            h.Add(f);
        }

        return h.ToHashCode();
    }
}

/// <summary>
/// 语言运行时值（specs/values.md）。
///
/// 值语义铁律的模型化（Runtime Model 裁定，与 lang-rs A4 1:1）：
/// - **struct/enum 内联存储**：<see cref="CloneValue"/> 即 VALUE-COPY-001——顶层字段逐字段
///   复制、嵌套 struct 递归拷贝、字段中的容器（<see cref="ValueId"/>）保持共享。绑定/传参/
///   返回/写入容器一律走 CloneValue（VALUE-ELEM-009 读出即副本），「容器内 struct 共享
///   指针」的别名偏差在结构上不可表示。
/// - **slice/map 堆分配**：<see cref="Value.Slice"/> / <see cref="Value.Map"/> 只持堆索引，
///   共享底层数据（VALUE-SHARE-002 引用语义），原位变更对全部别名可见。
/// - **字符串不可变**（VALUE-IMMUT-005）。
/// - Option/Result 是预载 enum 实例（specs/option-result.md），不是独立变体。
///
/// record 结构相等仅供内部/测试做结构比较；语言级 == 必须走 <see cref="EqualsLang"/>
/// （RUNTIME-EQ-003：仅标量与 enum 可比）。
/// </summary>
public abstract record Value
{
    /// <summary>私有构造封闭层次：只允许嵌套变体继承（新增变体必须列进所有穷尽 switch）。</summary>
    private Value()
    {
    }

    /// <summary>int（i64，值域与 Go int64 对齐）。</summary>
    public sealed record Int(long V) : Value;

    /// <summary>float（f64）。</summary>
    public sealed record Float(double V) : Value;

    /// <summary>bool。</summary>
    public sealed record Bool(bool V) : Value;

    /// <summary>string，不可变（VALUE-IMMUT-005）。</summary>
    public sealed record Str(string V) : Value;

    /// <summary>()（VALUE-UNIT-008）。</summary>
    public sealed record Unit : Value
    {
        /// <summary>唯一 unit 值。</summary>
        public static readonly Unit Default = new();
    }

    /// <summary>区间 lo..hi，半开 [lo, hi)；仅 for-in/len 消费（运行时证据 RangeVal）。</summary>
    public sealed record Range(long Lo, long Hi) : Value;

    /// <summary>[T] 容器——语言唯一线性数组形态，引用语义（VALUE-SHARE-002）。</summary>
    public sealed record Slice(ValueId Id) : Value;

    /// <summary>map[K]V，插入序（VALUE-MAP-006），引用语义。</summary>
    public sealed record Map(ValueId Id) : Value;

    /// <summary>struct 实例，值语义（VALUE-COPY-001）。</summary>
    public sealed record Struct(StructVal Val) : Value;

    /// <summary>enum 实例（含预载 Option/Result）：Tag(载荷...)。相等 = 同 def 同 case + 载荷逐个相等（结构相等）。</summary>
    public sealed record Enum(EnumDefId Def, uint Case, IReadOnlyList<Value> Payloads) : Value
    {
        // 自定义 Equals(Enum?)：合成 Equals(Value?) 对 IReadOnlyList 做引用相等，必须改成逐元素结构相等；
        // 合成 Equals(object?) 会转发到本方法。GetHashCode 同步改为逐元素哈希。
        public bool Equals(Enum? other) =>
            other is not null && Def == other.Def && Case == other.Case && Payloads.SequenceEqual(other.Payloads);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(Def);
            h.Add(Case);
            foreach (var p in Payloads)
            {
                h.Add(p);
            }

            return h.ToHashCode();
        }
    }

    /// <summary>chan[T]，channel 表索引（CHAN-CREATE-001）。</summary>
    public sealed record Chan(ChannelId Id) : Value;

    /// <summary>闭包/函数值：函数身份 + 定义处环境（VALUE-CAPTURE-004 按引用捕获）。</summary>
    public sealed record Closure(FunctionId Func, EnvId Env) : Value;

    /// <summary>内建函数值。</summary>
    public sealed record Builtin(BuiltinId Id) : Value;

    /// <summary>引用（&x 的结果），显式建模——绝不映射为 C# ref/out/ref struct（VALUE-REF-003）。</summary>
    public sealed record Ref(Place Place) : Value;

    /// <summary>类型名（输出与错误消息用；struct/enum 取定义名，需定义表）。</summary>
    public string TypeName(DefTables defs) => this switch
    {
        Int => "int",
        Float => "float",
        Bool => "bool",
        Str => "string",
        Unit => "()",
        Range => "区间",
        Slice => "数组",
        Map => "映射",
        Struct s => DefName(defs.StructDef(s.Val.Def), d => d.Name),
        Enum e => DefName(defs.EnumDef(e.Def), d => d.Name),
        Chan => "chan",
        Closure => "函数",
        Builtin => "内建函数",
        Ref => "引用",
        _ => throw new InvalidOperationException("未知 Value 变体（内部不变量）"),
    };

    /// <summary>不依赖定义表的类型提示（运行时内部错误路径用；带定义名的正规消息由解释器
    /// 经 <see cref="TypeName"/> 构造）。消息文本允许实现差异（RUNTIME-FAIL-001），错误分类是契约。</summary>
    public string TypeHint() => this switch
    {
        Int => "int",
        Float => "float",
        Bool => "bool",
        Str => "string",
        Unit => "()",
        Range => "区间",
        Slice => "数组",
        Map => "映射",
        Struct => "struct 值",
        Enum => "enum 值",
        Chan => "chan",
        Closure => "函数",
        Builtin => "内建函数",
        Ref => "引用",
        _ => throw new InvalidOperationException("未知 Value 变体（内部不变量）"),
    };

    /// <summary>语言级 ==（RUNTIME-EQ-003）：标量同型比较（无隐式转换，int 与 float 不可比）、
    /// enum 同 def 同 case 递归比较；容器/struct/函数等不可比 → <see cref="RtErrorKind.Eq"/>。</summary>
    public static RtResult<bool> EqualsLang(Value a, Value b) => (a, b) switch
    {
        (Int x, Int y) => RtResult<bool>.Ok(x.V == y.V),
        (Float x, Float y) => RtResult<bool>.Ok(x.V == y.V),
        (Bool x, Bool y) => RtResult<bool>.Ok(x.V == y.V),
        (Str x, Str y) => RtResult<bool>.Ok(string.Equals(x.V, y.V, StringComparison.Ordinal)),
        (Enum p, Enum q) => EqualsEnumLang(p, q),
        _ => RtResult<bool>.Fail(new RtError(RtErrorKind.Eq, $"{a.TypeHint()} 不可用 == 比较")),
    };

    private static RtResult<bool> EqualsEnumLang(Enum p, Enum q)
    {
        if (p.Def != q.Def || p.Case != q.Case)
        {
            return RtResult<bool>.Ok(false);
        }

        if (p.Payloads.Count != q.Payloads.Count)
        {
            return RtResult<bool>.Fail(RtError.Internal("enum 载荷数不一致：内部不变量破坏"));
        }

        for (var i = 0; i < p.Payloads.Count; i++)
        {
            var r = EqualsLang(p.Payloads[i], q.Payloads[i]);
            if (!r.IsOk)
            {
                return r;
            }

            if (!r.Value)
            {
                return RtResult<bool>.Ok(false);
            }
        }

        return RtResult<bool>.Ok(true);
    }

    /// <summary>
    /// 同型 int/float/string 大小比较：返回 -1/0/1；异型、不可比类型或 NaN float 返回 null（由解释器消费）。
    /// NaN 无全序（IEEE）：关系运算在 Ops 层对 float 对按 IEEE 运算符直落 false，不经本函数
    /// （TYPE-CONV-005 v1.0.1，ADR-0006/RS-007）；字符串为**码点序**
    /// （Go strings.Compare 的 UTF-8 字节序）——注意 C# string.Ordinal 是 UTF-16 unit 序，
    /// astral 码点与 U+E000..U+FFFF 的相对次序两者相反，必须走 <see cref="CompareCodePoints"/>。
    /// </summary>
    public static int? Compare(Value a, Value b) => (a, b) switch
    {
        (Int x, Int y) => x.V.CompareTo(y.V),
        (Float x, Float y) => double.IsNaN(x.V) || double.IsNaN(y.V) ? null : CompareFloat(x.V, y.V),
        (Str x, Str y) => CompareCodePoints(x.V, y.V),
        _ => null,
    };

    private static int CompareFloat(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return 0;
        }

        return x < y ? -1 : x > y ? 1 : 0;
    }

    /// <summary>条件真值：仅 bool 可作条件（无隐式真值转换）。</summary>
    public RtResult<bool> Truthy() => this is Bool b
        ? RtResult<bool>.Ok(b.V)
        : RtResult<bool>.Fail(new RtError(RtErrorKind.TypeCast, $"条件须为 bool，得到 {TypeHint()}"));

    /// <summary>
    /// 值语义拷贝（VALUE-COPY-001）：struct/enum 递归拷贝（顶层逐字段、嵌套递归），
    /// 容器字段（Slice/Map 堆索引）共享；标量与地址类值（Str/Chan/Closure/Builtin/Ref）
    /// 不可变，直接共享。绑定/传参/容器写入只走 CloneValue。
    /// </summary>
    public Value CloneValue() => this switch
    {
        Int or Float or Bool or Str or Unit or Range or Slice or Map or Chan or Closure or Builtin or Ref => this,
        Struct s => new Struct(new StructVal(s.Val.Def, s.Val.Fields.Select(f => f.CloneValue()).ToArray())),
        Enum e => new Enum(e.Def, e.Case, e.Payloads.Select(p => p.CloneValue()).ToArray()),
        _ => throw new InvalidOperationException("未知 Value 变体（内部不变量）"),
    };

    private static string DefName<T>(RtResult<T> def, Func<T, string> name) where T : class =>
        def.IsOk ? name(def.Value) : "<?>";

    /// <summary>码点序比较（= UTF-8 字节序 = Go strings.Compare）。C# string.Ordinal 是 UTF-16
    /// unit 序，与码点序在 astral vs U+E000..U+FFFF 上方向相反，语言比较必须走本函数。</summary>
    internal static int CompareCodePoints(string x, string y)
    {
        using var xe = x.EnumerateRunes().GetEnumerator();
        using var ye = y.EnumerateRunes().GetEnumerator();
        while (xe.MoveNext())
        {
            if (!ye.MoveNext())
            {
                return 1;
            }

            var a = (uint)xe.Current.Value;
            var b = (uint)ye.Current.Value;
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return ye.MoveNext() ? -1 : 0;
    }

    /// <summary>码点数（astral 码点计 1，不是 string.Length 的 char 数；语言字符串来自合法源码文本）。</summary>
    internal static int CodePointCount(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes())
        {
            n++;
        }

        return n;
    }
}

/// <summary>映射键种类。</summary>
public enum MapKeyKind
{
    Int,
    Str,
    Bool,
}

/// <summary>
/// 映射键：语言层仅允许 int/string/bool（E3014）。字符串比较一律 Ordinal。
/// 派生序为「变体序 + 值序」，仅供内部；JSON 键字母序（RUNTIME-JSON-002）由宿主内建
/// 按 <see cref="Plain"/> 文本排序实现。
/// </summary>
public readonly struct MapKey : IEquatable<MapKey>, IComparable<MapKey>
{
    private readonly long _int;
    private readonly string? _str;
    private readonly bool _bool;

    private MapKey(MapKeyKind kind, long i, string? s, bool b)
    {
        Kind = kind;
        _int = i;
        _str = s;
        _bool = b;
    }

    public MapKeyKind Kind { get; }

    public long IntValue => _int;

    public string StrValue => _str!;

    public bool BoolValue => _bool;

    public static MapKey FromInt(long v) => new(MapKeyKind.Int, v, null, false);

    public static MapKey FromStr(string v) => new(MapKeyKind.Str, 0, v, false);

    public static MapKey FromBool(bool v) => new(MapKeyKind.Bool, 0, null, v);

    /// <summary>从值提取合法映射键；其余类型返回 null（由调用方报 E3014/RUNTIME 错）。</summary>
    public static MapKey? FromValue(Value v) => v switch
    {
        Value.Int i => FromInt(i.V),
        Value.Str s => FromStr(s.V),
        Value.Bool b => FromBool(b.V),
        _ => null,
    };

    /// <summary>规范键形还原为值（映射输出 {k: v} 的原键面）。</summary>
    public Value ToValue() => Kind switch
    {
        MapKeyKind.Int => new Value.Int(_int),
        MapKeyKind.Str => new Value.Str(_str!),
        MapKeyKind.Bool => new Value.Bool(_bool),
        _ => throw new InvalidOperationException("未知 MapKey 种类（内部不变量）"),
    };

    /// <summary>键的纯文本形（int 十进制 / bool true|false / string 原文）。
    /// 映射输出 {k: v} 与 JSON 键字母序共用此形。</summary>
    public string Plain() => Kind switch
    {
        MapKeyKind.Int => _int.ToString(CultureInfo.InvariantCulture),
        MapKeyKind.Str => _str!,
        MapKeyKind.Bool => _bool ? "true" : "false",
        _ => throw new InvalidOperationException("未知 MapKey 种类（内部不变量）"),
    };

    public bool Equals(MapKey other) => Kind == other.Kind && Kind switch
    {
        MapKeyKind.Int => _int == other._int,
        MapKeyKind.Str => string.Equals(_str, other._str, StringComparison.Ordinal),
        MapKeyKind.Bool => _bool == other._bool,
        _ => false,
    };

    public override bool Equals(object? obj) => obj is MapKey k && Equals(k);

    public override int GetHashCode() => Kind switch
    {
        MapKeyKind.Int => HashCode.Combine(Kind, _int),
        MapKeyKind.Str => HashCode.Combine(Kind, _str),
        MapKeyKind.Bool => HashCode.Combine(Kind, _bool),
        _ => 0,
    };

    public int CompareTo(MapKey other)
    {
        if (Kind != other.Kind)
        {
            return Kind < other.Kind ? -1 : 1;
        }

        return Kind switch
        {
            MapKeyKind.Int => _int.CompareTo(other._int),
            MapKeyKind.Str => Value.CompareCodePoints(_str!, other._str!),
            MapKeyKind.Bool => _bool.CompareTo(other._bool),
            _ => 0,
        };
    }

    public static bool operator ==(MapKey a, MapKey b) => a.Equals(b);

    public static bool operator !=(MapKey a, MapKey b) => !a.Equals(b);
}
