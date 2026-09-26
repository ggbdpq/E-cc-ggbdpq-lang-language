using Lang.Core;
using Lang.Semantic;

/// <summary>
/// NAME-* 十条专项（E2009 空号保留）。逐码覆盖：E2001/E2002/E2003/E2004/E2005/
/// E2006/E2007/E2008/E2010 + 遮蔽合法（不报码）+ 合并命名空间。
/// 期望码与行号按 specs/name-resolution.md 与 rules.md 映射手算。
/// </summary>
public class NameRulesTests
{
    // ———— NAME-DUP-003 → E2001 ————

    [Fact]
    public void E2001_DuplicateBindingInSameScope()
    {
        Sem.AssertDiags("""
fn main() {
    let x = 1
    var x = 2
}
""", ("E2001", 3));
    }

    [Fact]
    public void E2001_ShadowingAcrossScopesIsLegal()
    {
        // NAME-SHADOW-002：遮蔽跨作用域合法，不报码
        Sem.AssertDiags("""
fn main() {
    let x = 1
    if true {
        let x = "s"
    }
    println(x)
}
""");
    }

    [Fact]
    public void E2001_DuplicateStructIsDup()
    {
        Sem.AssertDiags("""
struct P {
    x: int
}
struct P {
    y: int
}
fn main() {
    println(1)
}
""", ("E2001", 4));
    }

    // ———— NAME-UNDEF-004 → E2002 ————

    [Fact]
    public void E2002_UndefinedIdent()
    {
        Sem.AssertDiags("""
fn main() {
    println(nope)
}
""", ("E2002", 2));
    }

    [Fact]
    public void E2002_UnknownStructInLiteral()
    {
        Sem.AssertDiags("""
fn main() {
    let p = Ghost{x: 1}
}
""", ("E2002", 2));
    }

    [Fact]
    public void E2002_GoTargetMustBeTopFn()
    {
        // EVAL-GO-007：go 的目标须为顶层函数；非函数名 → E2002
        Sem.AssertDiags("""
fn main() {
    go notAFn(1)
}
""", ("E2002", 2));
    }

    [Fact]
    public void E2002_ForwardGlobalReferenceIsUndefined()
    {
        // 与 interp 顺序语义一致：顶层绑定按序可见，前向引用全局即 E2002
        //（函数体最后走，函数内引用后置全局是合法的）
        Sem.AssertDiags("""
let b = a
let a = 1
fn main() {
    println(b)
}
""", ("E2002", 1));
    }

    [Fact]
    public void FnBodySeesLaterGlobal()
    {
        Sem.AssertDiags("""
fn main() {
    println(ahead)
}
let ahead = 1
""");
    }

    // ———— NAME-CTOR-005 → E2003 / E2006 ————

    [Fact]
    public void E2003_TwoUserEnumsWithSameCaseName()
    {
        Sem.AssertDiags("""
enum A {
    T
}
enum B {
    T
}
fn main() {
    println(1)
}
""", ("E2003", 5));
    }

    [Fact]
    public void E2006_CaseConflictsBuiltinFn()
    {
        Sem.AssertDiags("""
enum Bad {
    print
}
fn main() {
    println(1)
}
""", ("E2006", 2));
    }

    // ———— NAME-RECV-006 → E2004 ————

    [Fact]
    public void E2004_ReceiverNotNamedType()
    {
        Sem.AssertDiags("""
fn (p int).hi(): int {
    return 1
}
fn main() {
    println(1)
}
""", ("E2004", 1));
    }

    [Fact]
    public void E2004_MethodOnKnownEnumIsRecvError()
    {
        // Option 是已知 enum 而非 struct → E2004（接收者须为已知 struct）
        Sem.AssertDiags("""
fn (o Option).hi(): int {
    return 1
}
fn main() {
    println(1)
}
""", ("E2004", 1));
    }

    // ———— NAME-MUT-007 → E2005 ————

    [Fact]
    public void E2005_AssignToLet()
    {
        Sem.AssertDiags("""
fn main() {
    let x = 1
    x = 2
}
""", ("E2005", 3));
    }

    [Fact]
    public void VarReassignIsLegal()
    {
        Sem.AssertDiags("""
fn main() {
    var x = 1
    x = 2
    println(x)
}
""");
    }

    [Fact]
    public void E2005_FieldWriteNeverTripsMutCheck()
    {
        // 字段与容器内容总是可写（与绑定可变性解耦）
        Sem.AssertDiags("""
struct P {
    v: int
}
fn main() {
    let p = P{v: 1}
    p.v = 2
    println(p.v)
}
""");
    }

    // ———— NAME-PLACE-008 → E2007 / E2008 ————

    [Fact]
    public void E2007_BreakOutsideLoop()
    {
        Sem.AssertDiags("""
fn main() {
    break
}
""", ("E2007", 2));
    }

    [Fact]
    public void E2007_ContinueOutsideLoop()
    {
        Sem.AssertDiags("""
fn main() {
    continue
}
""", ("E2007", 2));
    }

    [Fact]
    public void E2008_ReturnOutsideFn()
    {
        Sem.AssertDiags("""
let x = 1
return x
""", ("E2008", 2));
    }

    // ———— NAME-NEST-009 → E2010 ————

    [Fact]
    public void E2010_NestedStructDecl()
    {
        Sem.AssertDiags("""
fn main() {
    struct Inner {
        v: int
    }
    println(1)
}
""", ("E2010", 2));
    }

    [Fact]
    public void E2010_NestedEnumDecl()
    {
        Sem.AssertDiags("""
fn main() {
    enum Inner {
        A
    }
    println(1)
}
""", ("E2010", 2));
    }

    // ———— 入口面：resolve 有诊断 → checker 不运行（checked=false）———

    [Fact]
    public void ResolverDiagsSuppressChecker()
    {
        var a = Sem.Analyze("""
fn main() {
    let x = undefinedName
}
""");
        Assert.False(a.Checked);
        _ = a.Diags.Single(d => d.Code.CodeText() == "E2002");
        Assert.Single(a.Diags);
    }

    [Fact]
    public void AnalyzeSourceParseFailureYieldsE1001Only()
    {
        var a = LangSemantic.AnalyzeSource(
            new Lang.Core.SourceText("bad.lang-go", "fn main( {{{"),
            new Lang.Core.SourceId(2));
        Assert.False(a.Checked);
        Assert.True(a.HasErrors());
        var d = Assert.Single(a.Diags);
        Assert.Equal("E1001", d.Code.CodeText());
    }
}
