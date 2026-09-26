using Lang.Core;
using Lang.Graph;
using Lang.Semantic;
using Lang.Syntax;
using Xunit;

namespace Lang.Graph.Tests;

/// <summary>
/// Analysis → Graph 填充器（行为证据：impls/rust/crates/lang-graph/src/fill.rs 测试，1:1 对齐）。
/// 全边清点：defines/contains/calls/spawn/reads/writes/references/imports/returns。
/// </summary>
public class FillTests
{
    private const string Src = """
        import "./util"
        struct Point {
            x: int
        }
        enum Color {
            Red
        }
        fn (p Point).len2(): Point {
            return Point { x: p.x }
        }
        fn mk(x: int): Point {
            return Point { x: x }
        }
        fn main() {
            var n = mk(3)
            go mk(4)
            for i in 1..3 {
                n = n + i
            }
            let m = match Color {
                Red => n
            }
            let b = n
            println(b)
        }
        """;

    private static (Graph Graph, Analysis Analysis) Fill()
    {
        var text = new SourceText("main.lang-go", Src);
        var parsed = Parser.Parse(text, new SourceId(1));
        Assert.Null(parsed.Error);
        var analysis = LangSemantic.Analyze(parsed.Program, new SourceId(1));
        return (GraphFill.FromAnalysis(parsed.Program, analysis, "main"), analysis);
    }

    private static bool HasEdge(Graph g, string from, EdgeKind kind, string to)
    {
        var f = g.Nodes().FirstOrDefault(n => n.Name == from && n.Kind != NodeKind.Module);
        var t = g.Nodes().FirstOrDefault(n => n.Name == to && n.Kind != NodeKind.Module);
        if (f is null || t is null)
        {
            return false;
        }
        return g.Edges().Any(e => e.From == f.Id && e.Kind == kind && e.To == t.Id);
    }

    /// <summary>以模块节点为起点的边（模块名与 fn main 同名，按 kind 消歧）。</summary>
    private static bool ModEdge(Graph g, EdgeKind kind, string to)
    {
        var f = g.Nodes().First(n => n.Kind == NodeKind.Module);
        var t = g.Nodes().FirstOrDefault(n => n.Name == to && n.Kind != NodeKind.Module);
        if (t is null)
        {
            return false;
        }
        return g.Edges().Any(e => e.From == f.Id && e.Kind == kind && e.To == t.Id);
    }

    /// <summary>全边清点：9 类边语义逐项落地；预载 println 不落边也不落节点。</summary>
    [Fact]
    public void Edges_land_per_semantics()
    {
        var (g, _) = Fill();
        Assert.True(ModEdge(g, EdgeKind.Defines, "mk"));
        Assert.True(ModEdge(g, EdgeKind.Defines, "len2"));
        Assert.True(ModEdge(g, EdgeKind.Defines, "Point"));
        Assert.True(ModEdge(g, EdgeKind.Defines, "Color"));
        Assert.True(ModEdge(g, EdgeKind.Defines, "main"));

        // contains：struct→方法、enum→ctor、fn→参数
        Assert.True(HasEdge(g, "Point", EdgeKind.Contains, "len2"));
        Assert.True(HasEdge(g, "Color", EdgeKind.Contains, "Red"));
        Assert.True(HasEdge(g, "mk", EdgeKind.Contains, "x"));
        Assert.True(HasEdge(g, "len2", EdgeKind.Contains, "p"));

        // returns：mk/len2 的返回注解指向 Point
        Assert.True(HasEdge(g, "mk", EdgeKind.Returns, "Point"));
        Assert.True(HasEdge(g, "len2", EdgeKind.Returns, "Point"));

        // calls / spawn / 读写 / 类型引用
        Assert.True(HasEdge(g, "main", EdgeKind.Calls, "mk"));
        Assert.True(HasEdge(g, "main", EdgeKind.Spawn, "mk"));
        Assert.True(HasEdge(g, "mk", EdgeKind.Reads, "x"));
        Assert.True(HasEdge(g, "len2", EdgeKind.Reads, "p"));
        Assert.True(HasEdge(g, "mk", EdgeKind.References, "Point"));
        Assert.True(HasEdge(g, "len2", EdgeKind.References, "Point"));
        Assert.True(HasEdge(g, "main", EdgeKind.References, "Red"));

        // imports：模块 → File 节点
        Assert.True(ModEdge(g, EdgeKind.Imports, "./util"));

        // 预载 println 不落边也不落节点
        Assert.False(HasEdge(g, "main", EdgeKind.Calls, "println"));
        Assert.True(g.Nodes().All(n => n.Name != "println"));

        // n 在循环里被写
        Assert.True(HasEdge(g, "main", EdgeKind.Writes, "n"));
    }

    /// <summary>节点身份与确定性：同一 AST 两次填充逐项相等；File/Module 为合成 id。</summary>
    [Fact]
    public void Deterministic_and_synthetic_ids()
    {
        var (g1, a) = Fill();
        var (g2, _) = Fill();
        var n1 = g1.Nodes().Select(n => (n.Id, n.Kind, n.Name)).ToList();
        var n2 = g2.Nodes().Select(n => (n.Id, n.Kind, n.Name)).ToList();
        Assert.Equal(n1, n2);
        var e1 = g1.Edges().ToList();
        var e2 = g2.Edges().ToList();
        Assert.Equal(e1, e2);

        // 合成 id：模块 = symbols.Count（arena 编号空间之后）
        var module = g1.Nodes().First(n => n.Kind == NodeKind.Module);
        Assert.Equal(new SymbolId((uint)a.Symbols.Count), module.Id);
        Assert.Equal("main", module.Name);
        var file = g1.Nodes().First(n => n.Kind == NodeKind.File);
        Assert.Equal(new SymbolId((uint)(a.Symbols.Count + 1)), file.Id);
        Assert.Equal("./util", file.Name);
    }

    /// <summary>结构缺口与脏程序的保守缺边：引用未登记节点（预载符号）不落边、不炸。</summary>
    [Fact]
    public void Preload_reference_skipped_not_crash()
    {
        var text = new SourceText("m.lang-go", "fn main() {\n    println(1)\n    let s = Some(1)\n}\n");
        var parsed = Parser.Parse(text, new SourceId(0));
        Assert.Null(parsed.Error);
        var analysis = LangSemantic.Analyze(parsed.Program, new SourceId(0));
        var g = GraphFill.FromAnalysis(parsed.Program, analysis, "m");
        // println/Some 均无节点：calls/references 边全部保守缺席
        Assert.True(g.Nodes().All(n => n.Name != "println" && n.Name != "Some"));
        Assert.Equal(3, g.NodeCount); // module + main + 绑定 s（Some 调用不建节点）
        var moduleId = g.Nodes().First(n => n.Kind == NodeKind.Module).Id;
        Assert.DoesNotContain(g.EdgesTo(moduleId), e => e.Kind == EdgeKind.Calls);
    }

    /// <summary>extern 顶层签名：defines 落边（FFI 面由 verify effects 消费）。</summary>
    [Fact]
    public void Extern_fn_gets_defines_edge()
    {
        var text = new SourceText("m.lang-go", "extern \"host\" {\n    fn hostfn(n: int): int\n}\n");
        var parsed = Parser.Parse(text, new SourceId(0));
        Assert.Null(parsed.Error);
        var analysis = LangSemantic.Analyze(parsed.Program, new SourceId(0));
        var g = GraphFill.FromAnalysis(parsed.Program, analysis, "m");
        Assert.True(ModEdge(g, EdgeKind.Defines, "hostfn"));
    }
}
