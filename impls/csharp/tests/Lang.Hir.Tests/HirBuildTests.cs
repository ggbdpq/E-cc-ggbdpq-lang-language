using Lang.Core;
using Lang.Hir;
using Lang.Semantic;
using Lang.Syntax;
using Lang.Types;
using Xunit;

namespace Lang.Hir.Tests;

/// <summary>
/// AST + Analysis → Typed HIR 构建测试（第二波）：确定性（同输入两次逐字段相等）、
/// 符号/类型事实落节点、`?` 顶层的类型事实恢复（A2 记录缺口 SG-1 的搬运面补全）、
/// extern 符号按名回查（SG-2）、闭包捕获集计算。
/// </summary>
public class HirBuildTests
{
    private static (IReadOnlyList<Stmt>, Analysis) Analyze(string src)
    {
        var text = new SourceText("test.lang-go", src);
        var source = new SourceId(7);
        var parsed = Parser.Parse(text, source);
        Assert.Null(parsed.Error);
        var analysis = LangSemantic.Analyze(parsed.Program, source);
        Assert.True(analysis.Diags.Count == 0,
            "测试源码应无语义诊断: " + string.Join("; ", analysis.Diags.Select(d => d.Code.CodeText())));
        return (parsed.Program, analysis);
    }

    private static HirProg BuildHir(string src)
    {
        var (stmts, analysis) = Analyze(src);
        var res = HirBuild.BuildHir(stmts, analysis);
        Assert.True(res.IsOk, "build 应通过: " + res.Error);
        return res.Value!;
    }

    [Fact]
    public void Same_source_builds_field_identical_twice()
    {
        const string src = """
let g = 10
struct P {
    v: int
}
enum E {
    A(int)
    B
}
fn (p P).get(): int {
    return p.v
}
fn f(n: int): int {
    return match n {
        0 => 1
        _ => n * 2
    }
}
fn main() {
    let xs = [1, 2]
    for x in xs {
        println(f(x), A(x), P{v: x}.get(), g)
    }
}
""";
        Assert.Equal(BuildHir(src), BuildHir(src));
    }

    [Fact]
    public void Facts_land_on_nodes_and_method_recv_desugars()
    {
        const string src = """
struct Acc {
    total: int
}
fn (a Acc).add(n: int): Acc {
    return Acc{total: a.total + n}
}
fn main() {
    let x = Acc{total: 1}
    println(x.add(2).total)
}
""";
        var prog = BuildHir(src);
        // 方法：Recv 非空（解糖为显式接收者参数），带符号结论。
        var add = prog.Fns.Single(f => f.Name == "add");
        Assert.NotNull(add.Recv);
        Assert.Equal("a", add.Recv.Name);
        Assert.NotNull(add.Meta.Sym);
        Assert.NotNull(add.Meta.TypeId);
        // main：方法调用节点带 method 符号；struct 字面量带 struct 符号与类型。
        var main = prog.Main!;
        var eval = Assert.IsType<HirStmt.Eval>(main.Body[1]);
        // println(...) = 内建 Call，实参为 Field(MethodCall)。
        var println = Assert.IsType<HirExpr.Call>(eval.Expr);
        Assert.Equal("println", println.Builtin);
        var field = Assert.IsType<HirExpr.Field>(Assert.Single(println.Args));
        var methodCall = Assert.IsType<HirExpr.MethodCall>(field.Obj);
        Assert.NotNull(methodCall.Method);
        // 接收者 = 局部引用 x；struct 字面量在 let 语句（带 struct 符号与类型结论）。
        var recv = Assert.IsType<HirExpr.LocalRef>(methodCall.Recv);
        Assert.Equal("x", recv.Name);
        var letX = Assert.IsType<HirStmt.Let>(main.Body[0]);
        var lit = Assert.IsType<HirExpr.StructLit>(letX.Value.Init);
        Assert.NotNull(lit.StructSym);
        Assert.True(lit.Meta.TypeId is not null, "struct 字面量应有类型结论");
    }

    [Fact]
    public void Try_type_fact_recovered_from_operand_result()
    {
        const string src = """
fn step(x: int): Result[int, error] {
    if x < 0 {
        return Err(err("neg"))
    }
    return Ok(x + 1)
}
fn run(x: int): Result[int, error] {
    let a = step(x)?
    return Ok(a)
}
""";
        var prog = BuildHir(src);
        var run = prog.Fns.Single(f => f.Name == "run");
        // let a = step(x)? —— Try 节点在 checker 走 infer_try 直呼后无类型结论，
        // build 按同一规则从操作数类型恢复 ok 侧（int = TypeId(1)）。
        var let = Assert.IsType<HirStmt.Let>(run.Body[0]);
        var tryExpr = Assert.IsType<HirExpr.Try>(let.Value.Init);
        Assert.Equal(TypeTables.Int, tryExpr.Meta.TypeId);
    }

    [Fact]
    public void Extern_fn_symbol_recovered_by_name()
    {
        const string src = """
extern "host" {
    fn hostAdd(a: int, b: int): int
}
fn main() {
    println(hostAdd(1, 2))
}
""";
        var prog = BuildHir(src);
        var ext = prog.Fns.Single(f => f.Name == "hostAdd");
        Assert.True(ext.IsExtern);
        Assert.Equal("host", ext.ExternHost);
        Assert.Empty(ext.Body);
        // resolver 对 extern 内层 FnDecl 不入 decl_syms —— build 按名回查回填（SG-2）。
        // 回查的符号应与 analysis arena 内同名 Fn 符号一致。
        Assert.NotNull(ext.Meta.Sym);
    }

    [Fact]
    public void FnLit_captures_free_locals_not_globals()
    {
        const string src = """
let g = 5
fn counter(): fn(): int {
    var n = 0
    return fn(): int {
        n = n + 1
        return n
    }
}
fn main() {
    let base = g
    let mk = fn(): int { return base }
    println(mk(), counter())
}
""";
        var prog = BuildHir(src);
        var counter = prog.Fns.Single(f => f.Name == "counter");
        var ret = Assert.IsType<HirStmt.Return>(counter.Body[1]);
        var fnlit = Assert.IsType<HirExpr.FnLit>(ret.Val!);
        Assert.Equal(["n"], fnlit.Captures);
        // main 的 fnlit 捕获 base（局部）而非 g（全局）。
        var main = prog.Main!;
        var letMk = Assert.IsType<HirStmt.Let>(main.Body[1]);
        var mk = Assert.IsType<HirExpr.FnLit>(letMk.Value.Init);
        Assert.Equal(["base"], mk.Captures);
    }
}
