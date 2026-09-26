using Lang.Core;

namespace Lang.Graph;

/// <summary>
/// lang-graph —— 语义图：inspect.callers/callees/impact/references/dependency 查询面
/// （specs/agent-protocol.md）的公共结构。
///
/// 节点身份 = <see cref="SymbolId"/>（resolver 分配、refactor/inspect 消费）；
/// 模块级节点（Module/File）沿用同一编号空间。
///
/// 确定性纪律（接口契约 §5）：全部容器有序（SortedDictionary/SortedSet），公开迭代序固定——
/// 节点按 SymbolId 升序、边按 (from, kind 声明序, to) 升序，禁哈希迭代序外泄。
/// </summary>

/// <summary>语义图节点类别（冻结枚举 11 变体，追加只能走协调人集成轮；数值序 = 声明序 = 边排序 kind 次序）。</summary>
public enum NodeKind
{
    Module,
    File,
    Function,
    Variable,
    Parameter,
    Struct,
    Field,
    Method,
    Enum,
    Variant,
    Type,
}

public static class NodeKindText
{
    public static string AsStr(this NodeKind k) => k switch
    {
        NodeKind.Module => "module",
        NodeKind.File => "file",
        NodeKind.Function => "function",
        NodeKind.Variable => "variable",
        NodeKind.Parameter => "parameter",
        NodeKind.Struct => "struct",
        NodeKind.Field => "field",
        NodeKind.Method => "method",
        NodeKind.Enum => "enum",
        NodeKind.Variant => "variant",
        NodeKind.Type => "type",
        _ => throw new InvalidOperationException("NodeKind 变体未穷尽"),
    };
}

/// <summary>语义图边类别（冻结枚举 10 变体；数值序 = 声明序 = 边排序的 kind 次序）。</summary>
public enum EdgeKind
{
    Defines,
    References,
    Calls,
    Imports,
    Returns,
    Reads,
    Writes,
    Contains,
    DependsOn,
    Spawn,
}

public static class EdgeKindText
{
    public static string AsStr(this EdgeKind k) => k switch
    {
        EdgeKind.Defines => "defines",
        EdgeKind.References => "references",
        EdgeKind.Calls => "calls",
        EdgeKind.Imports => "imports",
        EdgeKind.Returns => "returns",
        EdgeKind.Reads => "reads",
        EdgeKind.Writes => "writes",
        EdgeKind.Contains => "contains",
        EdgeKind.DependsOn => "depends_on",
        EdgeKind.Spawn => "spawn",
        _ => throw new InvalidOperationException("EdgeKind 变体未穷尽"),
    };
}

/// <summary>图节点：身份 + 类别 + 名称。</summary>
public sealed record GraphNode(SymbolId Id, NodeKind Kind, string Name);

/// <summary>有向边（语义方向：from → to，如 caller --calls→ callee）。</summary>
public readonly record struct GraphEdge(SymbolId From, EdgeKind Kind, SymbolId To)
    : IComparable<GraphEdge>
{
    public int CompareTo(GraphEdge other)
    {
        var byFrom = From.Value.CompareTo(other.From.Value);
        if (byFrom != 0)
        {
            return byFrom;
        }
        var byKind = ((int)Kind).CompareTo((int)other.Kind);
        return byKind != 0 ? byKind : To.Value.CompareTo(other.To.Value);
    }
}

/// <summary>图构建错误类别（节点重复登记 / 边端点未登记——语义图必须良构：先节点后边）。</summary>
public enum GraphErrorKind { DuplicateNode, UnknownNode }

/// <summary>图构建受控异常。</summary>
public sealed class GraphException(GraphErrorKind kind, SymbolId id)
    : Exception(kind switch
    {
        GraphErrorKind.DuplicateNode => $"图节点重复登记: {id.Value}",
        _ => $"边端点未登记: {id.Value}",
    })
{
    public GraphErrorKind Kind { get; } = kind;
    public SymbolId Id { get; } = id;
}

/// <summary>
/// 确定性语义图容器。
/// - 节点存 SortedDictionary&lt;uint, GraphNode&gt;，<see cref="Nodes"/> 按 id 升序；
/// - 边存 SortedSet&lt;GraphEdge&gt;，<see cref="Edges"/> 按 (from, kind, to) 升序且天然去重。
/// </summary>
public sealed class Graph
{
    private readonly SortedDictionary<uint, GraphNode> _nodes = new();
    private readonly SortedSet<GraphEdge> _edges = new();

    /// <summary>登记节点；同 id 重复登记拒绝（防调用方静默改写已发布身份）。</summary>
    public void AddNode(SymbolId id, NodeKind kind, string name)
    {
        var node = new GraphNode(id, kind, name);
        if (!_nodes.TryAdd(id.Value, node))
        {
            throw new GraphException(GraphErrorKind.DuplicateNode, id);
        }
    }

    /// <summary>加边；端点必须已登记，重复边幂等（集合语义）。</summary>
    public void AddEdge(GraphEdge edge)
    {
        Require(edge.From);
        Require(edge.To);
        _edges.Add(edge);
    }

    public GraphNode? Node(SymbolId id) => _nodes.TryGetValue(id.Value, out var node) ? node : null;

    /// <summary>全部节点，SymbolId 升序（canonical 序）。</summary>
    public IEnumerable<GraphNode> Nodes() => _nodes.Values;

    /// <summary>全部边，(from, kind, to) 升序（canonical 序）。</summary>
    public IEnumerable<GraphEdge> Edges() => _edges;

    /// <summary>以 from 为起点的边（kind 声明序 → to 升序；inspect.callees/impact 的直接集）。</summary>
    public IEnumerable<GraphEdge> EdgesFrom(SymbolId from)
    {
        var lower = new GraphEdge(from, EdgeKind.Defines, new SymbolId(uint.MinValue));
        var upper = new GraphEdge(from, EdgeKind.Spawn, new SymbolId(uint.MaxValue));
        return _edges.GetViewBetween(lower, upper);
    }

    /// <summary>以 to 为终点的边（inspect.callers 的直接入边集；canonical 序）。</summary>
    public IEnumerable<GraphEdge> EdgesTo(SymbolId to) => _edges.Where(e => e.To == to);

    public int NodeCount => _nodes.Count;

    public int EdgeCount => _edges.Count;

    private void Require(SymbolId id)
    {
        if (!_nodes.ContainsKey(id.Value))
        {
            throw new GraphException(GraphErrorKind.UnknownNode, id);
        }
    }
}
