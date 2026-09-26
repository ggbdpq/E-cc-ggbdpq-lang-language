using Lang.Core;
using Lang.Syntax;
using Lang.Types;

namespace Lang.Semantic;

/// <summary>符号类别（诊断名与 Go/TS 证据 kindName 逐字一致）。</summary>
public enum SymbolKind
{
    Fn,
    Method,
    Var,
    Param,
    Struct,
    Enum,
    Ctor,
}

public static class SymbolKindText
{
    /// <summary>类别的诊断名（「绑定」「参数」等中文串为消息契约面）。</summary>
    public static string KindName(this SymbolKind k) => k switch
    {
        SymbolKind.Fn => "fn",
        SymbolKind.Method => "方法",
        SymbolKind.Var => "绑定",
        SymbolKind.Param => "参数",
        SymbolKind.Struct => "struct",
        SymbolKind.Enum => "enum",
        SymbolKind.Ctor => "构造器",
        _ => throw new InvalidOperationException($"未登记的 SymbolKind: {k}"),
    };
}

/// <summary>名义类型的 def 身份（符号 → TypeTables def 表项）。</summary>
public abstract record NominalRef
{
    public sealed record Struct(StructDefId Def) : NominalRef;

    public sealed record Enum(EnumDefId Def) : NominalRef;
}

/// <summary>
/// 静态符号（NAME-* 的承载结构）：自带声明注解的 owned 快照（TypeEx 引用即不可变树），
/// 检查器不回读 AST 声明节点。每个符号拥有编译内稳定的 <see cref="SymbolId"/>
/// （按声明/绑定顺序编号，含预载符号）。
/// </summary>
public sealed class Symbol
{
    public SymbolId Id { get; internal set; }
    public required SymbolKind Kind { get; init; }
    public required string Name { get; init; }
    public required Span Span { get; init; }

    /// <summary>var：是否可重绑（var）；其余类别恒 false。</summary>
    public bool Mutable { get; init; }

    /// <summary>fn/method 返回注解、let 绑定注解。</summary>
    public TypeEx? TypeEx { get; init; }

    /// <summary>fn/method 参数注解（保序）。</summary>
    public IReadOnlyList<TypeEx?> ParamAnnos { get; init; } = [];

    /// <summary>fn/method 参数名（保序，与 ParamAnnos 对齐；诊断消息用）。</summary>
    public IReadOnlyList<string> ParamNames { get; init; } = [];

    /// <summary>method：接收者 struct 名。</summary>
    public string? RecvName { get; init; }

    /// <summary>fn/method 参数个数；ctor 载荷个数。</summary>
    public int Arity { get; init; }

    /// <summary>声明的泛型参数名（用户泛型 fn；用户泛型 struct/enum 属冻结不支持面，
    /// 类型参数按擦除占位处理——GEN-DECL-001）。</summary>
    public IReadOnlyList<string> TypeParams { get; init; } = [];

    /// <summary>ctor：所属 enum 名（预载为 "Option"/"Result"）。</summary>
    public string OwnerName { get; init; } = "";

    /// <summary>ctor：case 序号（Option: Some=0/None=1；Result: Ok=0/Err=1）。</summary>
    public uint CaseIndex { get; init; }

    /// <summary>ctor：载荷类型注解（保序）。</summary>
    public IReadOnlyList<TypeEx?> PayloadAnnos { get; init; } = [];

    /// <summary>struct：字段名（保序）。</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>struct：字段注解（与 Fields 对齐）。</summary>
    public IReadOnlyList<TypeEx?> FieldAnnos { get; init; } = [];

    /// <summary>struct：方法表（方法名 → 方法符号；键序确定——SortedDictionary）。</summary>
    public SortedDictionary<string, SymbolId> Methods { get; } = new();

    /// <summary>struct/enum：def 表身份。</summary>
    public NominalRef? Nominal { get; init; }

    /// <summary>extern 声明（无函数体）。</summary>
    public bool IsExtern { get; init; }

    /// <summary>预载符号（内建函数/预载 struct/预载构造器）。</summary>
    public bool IsBuiltin { get; init; }
}

/// <summary>符号 arena（只增不减，<see cref="SymbolId"/> 即下标）。</summary>
public sealed class SymbolArena
{
    private readonly List<Symbol> _items = [];

    internal SymbolId Push(Symbol s)
    {
        var id = new SymbolId((uint)_items.Count);
        s.Id = id;
        _items.Add(s);
        return id;
    }

    /// <summary>按身份取符号（arena 只增不减，id 恒有效）。</summary>
    public Symbol Get(SymbolId id) => _items[(int)id.Value];

    internal Symbol GetMut(SymbolId id) => _items[(int)id.Value];

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    /// <summary>按声明顺序遍历（含预载符号；插入序，非契约排序面）。</summary>
    public IReadOnlyList<Symbol> Items => _items;

    /// <summary>首个同名 struct/enum 符号（顶层先于嵌套创建；与证据 arena 扫描序一致）。</summary>
    public SymbolId? FirstTypeNamed(string name)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var s = _items[i];
            if ((s.Kind == SymbolKind.Struct || s.Kind == SymbolKind.Enum) && s.Name == name)
            {
                return new SymbolId((uint)i);
            }
        }
        return null;
    }
}
