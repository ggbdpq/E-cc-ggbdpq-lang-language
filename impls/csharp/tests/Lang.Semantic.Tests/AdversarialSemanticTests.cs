using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;
using Lang.Types;

/// <summary>
/// 对抗专项（docs/lang-rs/2026-09-25-lang-rs-对抗验证重点.md 的 Scope/Generic 节
/// + checker 单元真值 + ADR-0005/RS-012 spec 修正面 + Analysis 查询视图）。
/// 含 astral 码点用例（🦀𝕏）。
/// </summary>
public class AdversarialSemanticTests
{
    // ———— Scope：for 绑定遮蔽———

    [Fact]
    public void ForVarShadowsOuterAndRestores()
    {
        // 循环变量在内层遮蔽同名绑定；循环结束后外层类型恢复（NAME-SHADOW-002）
        var src = """
fn f(): string {
    let x: string = "a"
    for x in [1, 2] {
        let y: int = x
    }
    return x
}
fn main() {
    println(f())
}
""";
        Sem.AssertDiags(src);
    }

    // ———— Scope：match 臂绑定限本臂（MATCH-BIND-003）———

    [Fact]
    public void MatchBindScopedToArm()
    {
        // v 只在 Some 臂可见；臂外引用即未定义
        var src = """
fn f(o: Option[int]): int {
    return match o {
        Some(v) => v
        None => 0
    }
}
fn g(o: Option[int]): int {
    let before = f(o)
    return before + v
}
fn main() {
    println(g(Some(1)))
}
""";
        // g 里的 v 是未定义名（臂绑定不外泄）；行号 = `return before + v` 所在行
        Sem.AssertDiags(src, ("E2002", 9));
    }

    // ———— Scope：嵌套 let 遮蔽（块作用域类型不外泄，RS-012）———

    [Fact]
    public void NestedLetShadowingInBlock()
    {
        var src = """
fn f(): int {
    let x = 1
    if true {
        let x: string = "s"
        let y: string = x
    }
    let z: int = x
    return z
}
fn main() {
    println(f())
}
""";
        Sem.AssertDiags(src);
    }

    // ———— Scope：接收者/参数遮蔽（同作用域重名即 E2001）———

    [Fact]
    public void ReceiverShadowedByParamIsDup()
    {
        var src = """
struct P {
    v: int
}
fn (p P).dist(p: int): int {
    return p
}
fn main() {
    let p = P{v: 1}
    println(p.dist(2))
}
""";
        // 接收者 p 与参数 p 同作用域 → E2001（锚在 fn 声明行 5）
        Sem.AssertDiags(src, ("E2001", 4));
    }

    [Fact]
    public void DuplicateParamsIsDup()
    {
        var src = """
fn f(a: int, a: int): int {
    return a
}
fn main() {
    println(f(1, 2))
}
""";
        Sem.AssertDiags(src, ("E2001", 1));
    }

    // ———— prelude 冲突———

    [Fact]
    public void UserCaseConflictsWithPreludeCtor()
    {
        // 与预载构造器 Some 重名 → E2003（NAME-CTOR-005）
        var src = """
enum Bad {
    Some
}
fn main() {
    println(1)
}
""";
        Sem.AssertDiags(src, ("E2003", 2));
    }

    [Fact]
    public void UserFnShadowsBuiltinOk()
    {
        // 用户 fn 遮蔽内建名合法（内建不占值命名空间）
        var src = """
fn len(x: string): int {
    return 0
}
fn main() {
    println(len("ab"))
}
""";
        Sem.AssertDiags(src);
    }

    // ———— 跨文件合并命名空间（NAME-MERGE-010）———

    [Fact]
    public void CrossFileReferenceAfterMerge()
    {
        var a = """
fn usePoint(p: Point): int {
    return p.x
}
fn main() {
    println(usePoint(Point{x: 1, y: 2}))
}
""";
        var b = """
struct Point {
    x: int
    y: int
}
""";
        var got = Sem.DiagsMerged(a, b);
        Assert.Empty(got);
    }

    [Fact]
    public void CrossFileDuplicateFnIsDup()
    {
        var a = """
fn f(): int {
    return 1
}
""";
        var b = """
fn f(): int {
    return 2
}
fn main() {
    println(f())
}
""";
        var got = Sem.DiagsMerged(a, b);
        _ = got.Single(x => x == ("E2001", 1));
        Assert.Single(got);
    }

    // ———— Generic：擦除同一性———

    [Fact]
    public void GenericFnErasedIdentity()
    {
        // 同一泛型 fn 以 int/string 两种实参实例化；T 端双向放行（GEN-INFER-002 擦除面）
        var src = """
fn id[T](x: T): T {
    return x
}
fn main() {
    let a: int = id(1)
    let s: string = id("x")
    println(a)
    println(s)
}
""";
        Sem.AssertDiags(src);
    }

    [Fact]
    public void GenericFnArityStillChecked()
    {
        var src = """
fn id[T](x: T): T {
    return x
}
fn main() {
    println(id(1, 2))
}
""";
        Sem.AssertDiags(src, ("E3003", 5));
    }

    // ———— Generic：Option 链———

    [Fact]
    public void NestedOptionChain()
    {
        var src = """
fn f(o: Option[Option[int]]): int {
    return match o {
        Some(inner) => match inner {
            Some(v) => v
            None => 0
        }
        None => 0
    }
}
fn main() {
    println(f(Some(Some(1))))
}
""";
        Sem.AssertDiags(src);
    }

    // ———— Generic：Result 第二参数———

    [Fact]
    public void ResultSecondParamMustBeError()
    {
        // Result[int, string] → E3009（无节点锚，line 0）；仍按 Result 形检查 return
        var src = """
fn f(): Result[int, string] {
    return Ok(1)
}
fn main() {
    println(f())
}
""";
        Sem.AssertDiags(src, ("E3009", 0));
    }

    [Fact]
    public void ResultMissingSecondParam()
    {
        var src = """
fn f(): Result[int] {
    return Ok(1)
}
fn main() {
    println(f())
}
""";
        // fnRet 回退 Invalid → return Ok(1) 走普通路径：E3006（Ok/Err 位置）连带，证据同款
        Sem.AssertDiags(src, ("E3001", 0), ("E3006", 2));
    }

    // ———— spec 修正面 1：赋值顶层 ? 合法（RESULT-TRY-003 + ADR-0005）———

    [Fact]
    public void TryAtAssignTopLevelIsAllowed()
    {
        var src = """
fn mayFail(): Result[int, error] {
    return Ok(1)
}
fn f(): Result[int, error] {
    var x = 0
    x = mayFail()?
    return Ok(x)
}
fn main() {
    println(f())
}
""";
        Sem.AssertDiags(src);
    }

    [Fact]
    public void TryOutsideStatementTopLevelRejected()
    {
        // ? 混入表达式内部 → E3005（位置非法）
        var src = """
fn mayFail(): Result[int, error] {
    return Ok(1)
}
fn f(): Result[int, error] {
    return (mayFail()?) + 1
}
fn main() {
    println(f())
}
""";
        // Result fn return 非 Ok/Err → E3006；推断内嵌值时 ? 的位置非法 E3005 也发出
        Sem.AssertDiags(src, ("E3005", 5), ("E3006", 5));
    }

    // ———— spec 修正面 2：块作用域类型不外泄（NAME-SCOPE-001 / RS-012）———

    [Fact]
    public void BlockLetTypeDoesNotLeak()
    {
        var src = """
fn f(): int {
    if true {
        let q: string = "s"
    }
    let q: int = 1
    return q
}
fn main() {
    println(f())
}
""";
        Sem.AssertDiags(src);
    }

    // ———— NAME-PLACE-008 补充：continue 同 break———

    [Fact]
    public void ContinueAndBreakInsideLoopOk()
    {
        var src = """
fn main() {
    for x in [1, 2, 3] {
        if x == 2 {
            continue
        }
        if x == 3 {
            break
        }
        println(x)
    }
}
""";
        Sem.AssertDiags(src);
    }

    // ———— checker 单元真值（E3007/E4001/E3011/E3004/E3005+E3008 锚定）———

    [Fact]
    public void MissingParamAnnotationIsE3007AtDeclLine()
    {
        // TYPE-ANN-006：参数缺注解 → E3007（锚在 fn 声明行）
        var src = "fn f(a): int {\n    return a\n}\nfn main() {\n    println(f(1))\n}\n";
        Sem.AssertDiags(src, ("E3007", 1));
    }

    [Fact]
    public void ResultMatchMissingErrArmIsE4001()
    {
        // MATCH-EXHAUSTIVE-002 代数型：缺 Err 臂
        var src = """
fn f(r: Result[int, error]): int {
    return match r {
        Ok(v) => v
    }
}
fn main() {
    println(f(Ok(1)))
}
""";
        // main 里 Ok(1) 在 Result-fn return 之外 → E3006 连带（RESULT-RETURN-004 位置约束）
        Sem.AssertDiags(src, ("E4001", 2), ("E3006", 7));
    }

    [Fact]
    public void NegOnStringIsE3011()
    {
        var src = """
fn main() {
    let s = "x"
    let t = -s
    println(t)
}
""";
        Sem.AssertDiags(src, ("E3011", 3));
    }

    [Fact]
    public void ErrorMsgFieldAccessOkOtherMissing()
    {
        // RESULT-PRELUDE-001：error.msg 合法；其他字段 E3004（消息特化为 error 专名）
        var src = """
fn main() {
    let e = err("boom")
    println(e.msg)
    println(e.other)
}
""";
        Sem.AssertDiags(src, ("E3004", 4));
    }

    [Fact]
    public void TryOperandNonResultIsE3008()
    {
        // ? 操作数为 int → E3008（同 line 双码：函数未注解 Result E3005）
        var src = """
fn f(): int {
    let x = 1?
    return x
}
fn main() {
    println(f())
}
""";
        Sem.AssertDiags(src, ("E3005", 2), ("E3008", 2));
    }

    // ———— astral 对抗轴：诊断行锚穿越 astral 内容仍按码点世界计算———

    [Fact]
    public void DiagnosticLineStableThroughAstralContent()
    {
        // 行 2 含 astral 码点（🦀𝕏𠀀，UTF-16 各占 2 char）；错误在行 3——行锚不得因
        // 代理对错位（char index 世界会把行号算歪）
        var src = """
fn main() {
    let s = "🦀𝕏𠀀"
    let x: int = s
    println(x)
}
""";
        Sem.AssertDiags(src, ("E3001", 3));
    }

    [Fact]
    public void MethodOnUnknownStructWithAstralContentIsE2004()
    {
        // 接收者 struct 名为非 ASCII（🦀 位于体内字符串，标识符规则不收 astral）；
        // 行锚穿越 astral 内容后仍指向声明行
        var src = """
fn (p 河蟹).hi(): int {
    let s = "🦀𝕏"
    return 1
}
fn main() {
    println(1)
}
""";
        Sem.AssertDiags(src, ("E2004", 1));
    }

    // ———— Analysis 查询视图———

    [Fact]
    public void AnalysisQueryViews()
    {
        var source = new SourceId(5);
        var text = new SourceText("q.lang-go", "fn main() {\n    let x = 1\n    println(x)\n}\n");
        var parsed = Parser.Parse(text, source);
        Assert.Null(parsed.Error);
        var analysis = LangSemantic.Analyze(parsed.Program, source);
        Assert.False(analysis.HasErrors());
        Assert.True(analysis.Checked);
        // main 体的 let 类型 = int
        foreach (var s in parsed.Program)
        {
            if (s is Stmt.FnDecl { Name: "main" } fn)
            {
                var let = Assert.IsType<Stmt.Let>(fn.Body[0]);
                var t = analysis.TypeOf(let.Init);
                Assert.NotNull(t);
                Assert.Equal(new TypeData.Int(), analysis.Types.Data(t.Value));
                var sym = analysis.SymbolOfDecl(s);
                Assert.NotNull(sym);
                Assert.Equal("main", analysis.Symbols.Get(sym.Value).Name);
            }
        }
    }

    [Fact]
    public void AnalysisPreludeAndMatchKindView()
    {
        var source = new SourceId(1);
        var text = new SourceText("m.lang-go", """
fn f(o: Option[int]): int {
    return match o {
        Some(v) => v
        None => 0
    }
}
fn main() {
    println(f(None))
}
""");
        var parsed = Parser.Parse(text, source);
        Assert.Null(parsed.Error);
        var stmts = parsed.Program;
        var analysis = LangSemantic.Analyze(stmts, source);
        Assert.False(analysis.HasErrors());
        // 预载面（A4 冻结 id）：预载 Option/Result 只有 ctor 符号，enum def 直接在表里
        foreach (var s in analysis.Symbols.Items)
        {
            if (s.Kind == SymbolKind.Ctor && s.Name == "Some")
            {
                Assert.Equal("Option", s.OwnerName);
                Assert.Equal(0u, s.CaseIndex);
                Assert.True(s.IsBuiltin);
            }
        }
        Assert.Equal("Option", analysis.Types.GetEnum(TypeTables.OptionDef).Name);
        Assert.Equal("Some", analysis.Types.GetEnum(TypeTables.OptionDef).Cases[0].Name);
        Assert.Equal("Ok", analysis.Types.GetEnum(TypeTables.ResultDef).Cases[0].Name);
        // match 分类：f 体里的 match 是 Option 型
        var f = stmts.OfType<Stmt.FnDecl>().First(x => x.Name == "f");
        var ret = Assert.IsType<Stmt.Return>(f.Body[0]);
        var matchExpr = Assert.IsType<Expr.Match>(ret.Value!);
        var mk = analysis.MatchKindOf(matchExpr);
        Assert.NotNull(mk);
        Assert.Equal(MatchKind.Option, mk);
        // 被匹配值类型 = Option[int]
        var scrutTy = analysis.TypeOf(matchExpr.Scrutinee);
        Assert.NotNull(scrutTy);
        Assert.Equal(new TypeData.Option(TypeTables.Int), analysis.Types.Data(scrutTy.Value));
        // 裸构造器 None 的类型 = Option[unknown]
        var main = stmts.OfType<Stmt.FnDecl>().First(x => x.Name == "main");
        var call = Assert.IsType<Expr.Call>(Assert.IsType<Stmt.ExprStmt>(main.Body[0]).Expr);
        var inner = Assert.IsType<Expr.Call>(call.Args[0]); // f(None)
        var arg = Assert.IsType<Expr.Ident>(inner.Args[0]); // None
        var noneTy = analysis.TypeOf(arg);
        Assert.NotNull(noneTy);
        Assert.Equal(new TypeData.Option(TypeTables.Unknown), analysis.Types.Data(noneTy.Value));
    }

    [Fact]
    public void ResolveAnnotationQuietChannel()
    {
        // HIR 侧复用面：静默解析注解（无诊断），Option/Result/容器/前向类型引用全通
        var source = new SourceId(9);
        var text = new SourceText("r.lang-go", """
struct P {
    x: int
}
fn f(r: Result[int, error]): Result[P, error] {
    let v = r?
    return Ok(P{x: v})
}
""");
        var parsed = Parser.Parse(text, source);
        Assert.Null(parsed.Error);
        var analysis = LangSemantic.Analyze(parsed.Program, source);
        Assert.False(analysis.HasErrors());
        var fn = parsed.Program.OfType<Stmt.FnDecl>().First(x => x.Name == "f");
        var ret = fn.Ret!;
        var tid = LangSemantic.ResolveAnnotation(analysis, ret, []);
        Assert.Equal("Result[P, error]", Ops.TypeStr(analysis.Types, tid));
        var p = LangSemantic.ResolveAnnotation(
            analysis, new TypeEx.Arr(ret.Span, ret), []);
        Assert.Equal("[Result[P, error]]", Ops.TypeStr(analysis.Types, p));
        // 泛型参数名走擦除占位通道
        var tp = LangSemantic.ResolveAnnotation(
            analysis, new TypeEx.Named(ret.Span, "T", []), ["T"]);
        Assert.Equal("T", Ops.TypeStr(analysis.Types, tp));
    }
}
