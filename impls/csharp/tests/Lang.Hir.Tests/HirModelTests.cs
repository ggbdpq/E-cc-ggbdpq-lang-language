using Lang.Core;
using Lang.Hir;
using Xunit;

namespace Lang.Hir.Tests;

/// <summary>
/// HIR 节点模型（语义事实层）：NodeInfo 四元结论、TypeOf 只读事实、确定性（同构造两次逐字段相等）。
/// 语义核就绪前以手构节点锁形状；AST→HIR 的 build 下一波交付（ILowerHir 已冻结签名）。
/// </summary>
public class HirModelTests
{
    private static readonly Span Span =
        new(new SourceId(0), new Position(0, 1, 1), new Position(3, 1, 4));

    [Fact]
    public void NodeInfo_factories_and_type_of()
    {
        var meta = NodeInfo.New(new NodeId(7), Span).WithSym(new SymbolId(3)).WithType(new TypeId(1));
        Assert.Equal(7u, meta.Id.Value);
        Assert.Equal(3u, meta.Sym!.Value.Value);
        Assert.Equal(1u, meta.TypeId!.Value.Value);

        // TypeOf 读取节点类型结论（事实层），不重新推断。
        var lit = new HirExpr.Lit(new HirLit(NodeInfo.New(new NodeId(1), Span).WithType(new TypeId(9)),
            LitKind.Int, 1, 0, string.Empty, false));
        var untyped = new HirExpr.Lit(new HirLit(NodeInfo.New(new NodeId(2), Span), LitKind.Int, 1, 0, string.Empty, false));
        Assert.Equal(9u, HirFacts.TypeOf(lit)!.Value.Value);
        Assert.Null(HirFacts.TypeOf(untyped));
        Assert.Null(HirFacts.TypeOf(new HirExpr.LocalRef(NodeInfo.New(new NodeId(3), Span), "x")));
    }

    [Fact]
    public void Hir_prog_built_twice_is_value_equal()
    {
        static HirProg Build() => new(
            Structs: new[]
            {
                new HirStructDecl(NodeInfo.New(new NodeId(0), Span), "点🦀", Array.Empty<string>(),
                    new HirFieldDecl[] { new("坐标𝕏", new TypeId(2)) }),
            },
            Enums: new[]
            {
                new HirEnumDecl(NodeInfo.New(new NodeId(1), Span), "Shape", Array.Empty<string>(),
                    new HirEnumCase[]
                    {
                        new(new SymbolId(10), "Circle", new TypeId?[] { null }),
                        new(null, "None", Array.Empty<TypeId?>()),
                    }),
            },
            Globals: Array.Empty<HirLet>(),
            Fns: new[]
            {
                new HirFn(NodeInfo.New(new NodeId(2), Span), "main", Recv: null, TypeParams: Array.Empty<string>(),
                    Params: Array.Empty<HirParam>(), Ret: null, IsExtern: false, ExternHost: string.Empty,
                    Body: new HirStmt[]
                    {
                        new HirStmt.Let(new HirLet(NodeInfo.New(new NodeId(3), Span), "变量𝕏", true,
                            new HirExpr.Binary(NodeInfo.New(new NodeId(4), Span), BinaryOp.Add,
                                new HirExpr.Lit(new HirLit(NodeInfo.New(new NodeId(5), Span), LitKind.Int, 1, 0, string.Empty, false)),
                                new HirExpr.Lit(new HirLit(NodeInfo.New(new NodeId(6), Span), LitKind.Int, 2, 0, string.Empty, false))))),
                        new HirStmt.Eval(NodeInfo.New(new NodeId(8), Span),
                            new HirExpr.Call(NodeInfo.New(new NodeId(9), Span), Func: null, Builtin: "println", Callee: null,
                                Args: new HirExpr[] { new HirExpr.LocalRef(NodeInfo.New(new NodeId(10), Span), "变量𝕏") })),
                    }),
            },
            Main: null);
        var a = Build();
        var b = Build();
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // 变异可感知：语句块差异 / enum case 差异 / 符号差异逐项检测。
        var fns = a.Fns.ToArray();
        var body = fns[0].Body.ToArray();
        body[0] = new HirStmt.Break(NodeInfo.New(new NodeId(99), Span));
        fns[0] = fns[0] with { Body = body };
        Assert.NotEqual(Build(), a with { Fns = fns });
    }

    [Fact]
    public void Method_call_and_pattern_shapes()
    {
        // obj.m(args)：method 符号 + name_span（rename 消费面）。
        var mc = new HirExpr.MethodCall(
            NodeInfo.New(new NodeId(1), Span),
            Recv: new HirExpr.LocalRef(NodeInfo.New(new NodeId(2), Span), "obj"),
            Method: new SymbolId(5), FieldName: null,
            Args: Array.Empty<HirExpr>(), NameSpan: Span);
        Assert.Equal(5u, mc.Method!.Value.Value);

        // 构造臂：嵌套 Ctor 子模式按位置解构载荷（MATCH-BIND-003）。
        var arm = new HirMatchArm(
            new HirPat.Ctor(new SymbolId(10), new HirPat[]
            {
                new HirPat.Bind("x", null), new HirPat.Wild(),
            }),
            new HirExpr.LocalRef(NodeInfo.New(new NodeId(3), Span), "x"));
        var ctorPat = Assert.IsType<HirPat.Ctor>(arm.Pat);
        Assert.Equal(2, ctorPat.Subs.Count);
    }

    [Fact]
    public void Lower_result_shape()
    {
        var ok = LowerResult<string>.Ok("v");
        Assert.True(ok.IsOk);
        var fail = LowerResult<string>.Fail("结论缺口");
        Assert.False(fail.IsOk);
        Assert.Equal("结论缺口", fail.Error);
    }
}
