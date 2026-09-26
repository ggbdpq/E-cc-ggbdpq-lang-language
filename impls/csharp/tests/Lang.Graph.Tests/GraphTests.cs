using Lang.Core;
using Lang.Graph;
using Xunit;

namespace Lang.Graph.Tests;

/// <summary>语义图专项：冻结枚举字符串、确定性序、集合语义、直接入出边。</summary>
public class GraphTests
{
    private static SymbolId Sid(uint n) => new(n);

    private static GraphEdge Edge(uint from, EdgeKind kind, uint to) =>
        new(Sid(from), kind, Sid(to));

    [Fact]
    public void Node_edge_strings_frozen()
    {
        // 冻结字符串形 11/10 全枚举（契约面：inspect JSON 会用到）。
        Assert.Equal(
            ["module", "file", "function", "variable", "parameter", "struct", "field", "method", "enum", "variant", "type"],
            Enum.GetValues<NodeKind>().Select(k => k.AsStr()));
        Assert.Equal(11, Enum.GetValues<NodeKind>().Length);

        Assert.Equal(
            ["defines", "references", "calls", "imports", "returns", "reads", "writes", "contains", "depends_on", "spawn"],
            Enum.GetValues<EdgeKind>().Select(k => k.AsStr()));
        Assert.Equal(10, Enum.GetValues<EdgeKind>().Length);
    }

    /// <summary>同一图、不同登记顺序 → 公开迭代逐项相等（禁哈希序外泄）。</summary>
    [Fact]
    public void Iteration_is_insertion_order_independent()
    {
        static (List<(uint Id, string Kind, string Name)> Nodes, List<(uint From, string Kind, uint To)> Edges) Build(bool scrambled)
        {
            var g = new Graph();
            var ids = new[] { (1u, NodeKind.Function, "main"), (2u, NodeKind.Function, "helper"), (3u, NodeKind.Struct, "Point"), (4u, NodeKind.Field, "x"), (5u, NodeKind.Module, "lib") };
            var order = scrambled ? new[] { 3, 0, 4, 1, 2 } : new[] { 0, 1, 2, 3, 4 };
            foreach (var i in order)
            {
                g.AddNode(Sid(ids[i].Item1), ids[i].Item2, ids[i].Item3);
            }
            var es = new[] { Edge(1, EdgeKind.Calls, 2), Edge(1, EdgeKind.References, 3), Edge(3, EdgeKind.Contains, 4), Edge(5, EdgeKind.Contains, 3), Edge(1, EdgeKind.Spawn, 2) };
            var orderE = scrambled ? new[] { 4, 2, 0, 3, 1 } : new[] { 0, 1, 2, 3, 4 };
            foreach (var i in orderE)
            {
                g.AddEdge(es[i]);
            }
            return (
                g.Nodes().Select(n => (n.Id.Value, n.Kind.AsStr(), n.Name)).ToList(),
                g.Edges().Select(e => (e.From.Value, e.Kind.AsStr(), e.To.Value)).ToList());
        }

        var (plainNodes, plainEdges) = Build(false);
        var (scrambledNodes, scrambledEdges) = Build(true);
        Assert.Equal(plainNodes, scrambledNodes);
        Assert.Equal(plainEdges, scrambledEdges);
        // canonical 序的形状：节点 id 升序；边 (from, kind 声明序, to)
        Assert.Equal([1u, 2u, 3u, 4u, 5u], plainNodes.Select(n => n.Id));
        Assert.Equal(
            new[] { (1u, "references", 3u), (1u, "calls", 2u), (1u, "spawn", 2u), (3u, "contains", 4u), (5u, "contains", 3u) },
            plainEdges);
    }

    /// <summary>重复边幂等、重复节点/悬边拒绝。</summary>
    [Fact]
    public void Edge_set_semantics_and_validation()
    {
        var g = new Graph();
        g.AddNode(Sid(1), NodeKind.Function, "a");
        g.AddNode(Sid(2), NodeKind.Function, "b");
        g.AddEdge(Edge(1, EdgeKind.Calls, 2));
        g.AddEdge(Edge(1, EdgeKind.Calls, 2));
        Assert.Equal(1, g.EdgeCount);

        var dup = Assert.Throws<GraphException>(() => g.AddNode(Sid(1), NodeKind.Variable, "dup"));
        Assert.Equal(GraphErrorKind.DuplicateNode, dup.Kind);
        var danglingTo = Assert.Throws<GraphException>(() => g.AddEdge(Edge(1, EdgeKind.Calls, 9)));
        Assert.Equal(GraphErrorKind.UnknownNode, danglingTo.Kind);
        var danglingFrom = Assert.Throws<GraphException>(() => g.AddEdge(Edge(9, EdgeKind.Calls, 1)));
        Assert.Equal(GraphErrorKind.UnknownNode, danglingFrom.Kind);
    }

    /// <summary>callers（edges_to）/ callees（edges_from）直接集，按 canonical 序给出。</summary>
    [Fact]
    public void Direct_in_out_edges()
    {
        var g = new Graph();
        foreach (var (id, name) in new[] { (1u, "main"), (2u, "log"), (3u, "fetch"), (4u, "sink") })
        {
            g.AddNode(Sid(id), NodeKind.Function, name);
        }
        g.AddEdge(Edge(1, EdgeKind.Calls, 2));
        g.AddEdge(Edge(1, EdgeKind.Calls, 3));
        g.AddEdge(Edge(4, EdgeKind.Calls, 2));
        g.AddEdge(Edge(1, EdgeKind.References, 2));

        Assert.Equal(
            new[] { (2u, "references"), (2u, "calls"), (3u, "calls") },
            g.EdgesFrom(Sid(1)).Select(e => (e.To.Value, e.Kind.AsStr())));

        Assert.Equal(
            [1u, 1u, 4u],
            g.EdgesTo(Sid(2)).Select(e => e.From.Value));
    }

    /// <summary>接口契约 §0：astral 标识符（🦀）作为节点名原样保留。</summary>
    [Fact]
    public void Astral_node_name_is_preserved()
    {
        var g = new Graph();
        g.AddNode(Sid(1), NodeKind.Function, "函数🦀");
        g.AddNode(Sid(2), NodeKind.Variable, "𝕏");

        Assert.Equal("函数🦀", g.Node(Sid(1))!.Name);
        Assert.Equal("𝕏", g.Nodes().First(n => n.Id.Value == 2).Name);
    }
}
