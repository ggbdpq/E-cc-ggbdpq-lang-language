using Lang.Core;
using Lang.Syntax;

public class ParserTests
{
    private static IReadOnlyList<Stmt> Ok(string src)
    {
        var r = Parser.Parse(new SourceText("t.lang-go", src), new SourceId(0));
        Assert.Null(r.Error);
        return r.Program;
    }

    private static Diagnostic Fail(string src)
    {
        var r = Parser.Parse(new SourceText("t.lang-go", src), new SourceId(0));
        Assert.NotNull(r.Error);
        Assert.Equal(DiagnosticCode.E1001, r.Error!.Code);
        return r.Error;
    }

    private static Expr FirstExpr(string src)
    {
        var s = Assert.IsType<Stmt.ExprStmt>(Ok(src)[0]);
        return s.Expr;
    }

    [Fact]
    public void PrecedenceMulOverAdd()
    {
        // 2 + 3 * 4 → Binary(+, 2, Binary(*, 3, 4))
        var e = FirstExpr("2 + 3 * 4");
        var b = Assert.IsType<Expr.Binary>(e);
        Assert.Equal(BinaryOp.Add, b.Op);
        Assert.Equal(2, Assert.IsType<Expr.IntLit>(b.Lhs).Value);
        Assert.Equal(BinaryOp.Mul, Assert.IsType<Expr.Binary>(b.Rhs).Op);
    }

    [Fact]
    public void PrecedenceBitOrAddIsLeftAssoc()
    {
        // PARSE-004：| 与 + 同在第 6 级，左结合 → (1 | 2) + 3（conformance：precedence-bit-vs-arith）
        var e = Assert.IsType<Expr.Binary>(FirstExpr("1 | 2 + 3"));
        Assert.Equal(BinaryOp.Add, e.Op);
        Assert.Equal(BinaryOp.BitOr, Assert.IsType<Expr.Binary>(e.Lhs).Op);
        Assert.Equal(3, Assert.IsType<Expr.IntLit>(e.Rhs).Value);
    }

    [Fact]
    public void PrecedenceShlAboveAdd()
    {
        // PARSE-004：<< 在第 7 级（高于 + 第 6 级）→ (1 << 2) + 1
        var e = Assert.IsType<Expr.Binary>(FirstExpr("1 << 2 + 1"));
        Assert.Equal(BinaryOp.Add, e.Op);
        Assert.Equal(BinaryOp.Shl, Assert.IsType<Expr.Binary>(e.Lhs).Op);
        Assert.Equal(1, Assert.IsType<Expr.IntLit>(e.Rhs).Value);
    }

    [Fact]
    public void ParensOverridePrecedenceAndLogicChain()
    {
        var e = Assert.IsType<Expr.Binary>(FirstExpr("(2 + 3) * 4"));
        Assert.Equal(BinaryOp.Mul, e.Op);
        Assert.Equal(BinaryOp.Add, Assert.IsType<Expr.Binary>(e.Lhs).Op);

        // a || b && c → ||(a, &&(b, c))
        var or = Assert.IsType<Expr.Binary>(FirstExpr("a || b && c"));
        Assert.Equal(BinaryOp.OrOr, or.Op);
        Assert.Equal(BinaryOp.AndAnd, Assert.IsType<Expr.Binary>(or.Rhs).Op);
    }

    [Fact]
    public void BinaryOpsAreLeftAssociative()
    {
        // 10 - 3 - 2 → (- (- 10 3) 2)
        var e = Assert.IsType<Expr.Binary>(FirstExpr("10 - 3 - 2"));
        Assert.Equal(BinaryOp.Sub, Assert.IsType<Expr.Binary>(e.Lhs).Op);
    }

    [Fact]
    public void RangeIsLevelFiveNotFloat()
    {
        var e = Assert.IsType<Expr.Range>(FirstExpr("1..3"));
        Assert.Equal(1, Assert.IsType<Expr.IntLit>(e.Lo).Value);
        Assert.Equal(3, Assert.IsType<Expr.IntLit>(e.Hi).Value);
    }

    [Fact]
    public void UnaryNestedNegation()
    {
        var e = Assert.IsType<Expr.Unary>(FirstExpr("-(-5)"));
        Assert.IsType<Expr.Unary>(e.Operand);

        var sub = Assert.IsType<Expr.Binary>(FirstExpr("-5 - -3"));
        Assert.Equal(BinaryOp.Sub, sub.Op);
        Assert.IsType<Expr.Unary>(sub.Lhs);
        Assert.IsType<Expr.Unary>(sub.Rhs);
    }

    [Fact]
    public void PostfixChainsCallIndexSliceFieldTry()
    {
        // a[1:2].b(x)? → Try(Call(Field(Slice(Ident a), 1, 2), [x]))
        var tryE = Assert.IsType<Expr.Try>(FirstExpr("a[1:2].b(x)?"));
        var call = Assert.IsType<Expr.Call>(tryE.Operand);
        Assert.Single(call.Args);
        var field = Assert.IsType<Expr.Field>(call.Callee);
        Assert.Equal("b", field.Name);
        var slice = Assert.IsType<Expr.Slice>(field.Obj);
        Assert.Equal(1, Assert.IsType<Expr.IntLit>(slice.Lo).Value);
        Assert.Equal(2, Assert.IsType<Expr.IntLit>(slice.Hi).Value);
        Assert.Equal("a", Assert.IsType<Expr.Ident>(slice.Obj).Name);
    }

    [Fact]
    public void LiteralsAndCollections()
    {
        Assert.Equal(255, Assert.IsType<Expr.IntLit>(FirstExpr("0xFF")).Value);

        var arr = Assert.IsType<Expr.ArrLit>(FirstExpr("[1, 2, 3]"));
        Assert.Equal(3, arr.Items.Count);

        var map = Assert.IsType<Expr.MapLit>(FirstExpr("{\"a\": 1, \"b\": 2}"));
        Assert.Equal(2, map.Items.Count);

        Assert.Equal("中\n文", Assert.IsType<Expr.StrLit>(FirstExpr("\"中\\n文\"")).Value);
    }

    [Fact]
    public void StructLiteralParsesFields()
    {
        var e = Assert.IsType<Expr.StructLit>(FirstExpr("Point{x: 1, y: 2}"));
        Assert.Equal("Point", e.TypeName);
        Assert.Equal(2, e.Fields.Count);
        Assert.Equal("x", e.Fields[0].Name);
        Assert.Equal(1, Assert.IsType<Expr.IntLit>(e.Fields[0].Value).Value);
    }

    [Fact]
    public void MatchExpressionArmsAndPatterns()
    {
        var e = Assert.IsType<Expr.Match>(FirstExpr("match v {\n    1 => \"一\"\n    _ => \"多\"\n}"));
        Assert.Equal("v", Assert.IsType<Expr.Ident>(e.Scrutinee).Name);
        Assert.Equal(2, e.Arms.Count);
        Assert.IsType<Pat.Lit>(e.Arms[0].Pat);
        Assert.IsType<Pat.Wild>(e.Arms[1].Pat);

        // 绑定（小写）vs 构造（大写）vs 构造带子模式
        var m = Assert.IsType<Expr.Match>(FirstExpr(
            "match r {\n    Ok(v) => v\n    Err(e) => 0\n    Nil => 0\n    other => 1\n}"));
        var ok = Assert.IsType<Pat.Case>(m.Arms[0].Pat);
        Assert.Equal("Ok", ok.Name);
        Assert.Single(ok.Subs);
        Assert.Equal("Err", Assert.IsType<Pat.Case>(m.Arms[1].Pat).Name);
        Assert.Equal("Nil", Assert.IsType<Pat.Case>(m.Arms[2].Pat).Name);
        Assert.Equal("other", Assert.IsType<Pat.Bind>(m.Arms[3].Pat).Name);
    }

    [Fact]
    public void FnLiteralWithCaptureBody()
    {
        // fn 字面量须处于表达式位置（语句首的 fn 走函数声明）
        var prog = Ok("let f = fn(x: int): int {\n    return x + 1\n}");
        var let = Assert.IsType<Stmt.Let>(prog[0]);
        var fn = Assert.IsType<Expr.FnLit>(let.Init);
        Assert.Single(fn.Params);
        Assert.NotNull(fn.Ret);
        Assert.Single(fn.Body);
    }

    [Fact]
    public void StatementsFullSet()
    {
        var prog = Ok("""
            let a = 1
            var b = 2
            b += 3
            a.b = 4
            c[0] = 5
            if a > 0 {
                return a
            } else if a < 0 {
                return -a
            } else {
                return 0
            }
            while b > 0 {
                b -= 1
                if b == 1 { break }
                continue
            }
            for x in [1, 2] {
                println(x)
            }
            go worker(1)
            """);
        Assert.True(prog[0] is Stmt.Let { Mutable: false });
        Assert.True(prog[1] is Stmt.Let { Mutable: true });
        var add = Assert.IsType<Stmt.Assign>(prog[2]);
        Assert.Equal(AssignOp.AddAssign, add.Op);
        Assert.IsType<Expr.Ident>(add.Target);
        Assert.IsType<Expr.Field>(Assert.IsType<Stmt.Assign>(prog[3]).Target);
        Assert.IsType<Expr.Index>(Assert.IsType<Stmt.Assign>(prog[4]).Target);
        Assert.True(prog[5] is Stmt.If { ElseBody: not null });
        Assert.IsType<Stmt.While>(prog[6]);
        Assert.Equal("x", Assert.IsType<Stmt.ForIn>(prog[7]).Variable);
        Assert.IsType<Stmt.Go>(prog[8]);
    }

    [Fact]
    public void BareReturnIsNone()
    {
        var prog = Ok("fn f() {\n    return\n}");
        var body = Assert.IsType<Stmt.FnDecl>(prog[0]).Body;
        var ret = Assert.IsType<Stmt.Return>(body[0]);
        Assert.Null(ret.Value);
    }

    [Fact]
    public void DeclarationsFnMethodGenericStructEnum()
    {
        var prog = Ok("""
            fn add(a: int, b: int): int {
                return a + b
            }
            fn (r Rect).area(): int {
                return r.w * r.h
            }
            fn id[T](x: T): T {
                return x
            }
            struct Point[T] {
                x: T
                y: T
            }
            enum Shape {
                Circle(int)
                Rect(int, int)
                Unit
            }
            """);
        var add = Assert.IsType<Stmt.FnDecl>(prog[0]);
        Assert.Equal("add", add.Name);
        Assert.Equal(2, add.Params.Count);
        Assert.NotNull(add.Ret);

        var area = Assert.IsType<Stmt.FnDecl>(prog[1]);
        Assert.Equal("area", area.Name);
        Assert.Equal("r", area.Recv!.Name);

        var id = Assert.IsType<Stmt.FnDecl>(prog[2]);
        Assert.Equal("id", id.Name);
        Assert.Equal(["T"], id.TypeParams);

        var point = Assert.IsType<Stmt.StructDecl>(prog[3]);
        Assert.Equal("Point", point.Name);
        Assert.Single(point.TypeParams);
        Assert.Equal(2, point.Fields.Count);

        var shape = Assert.IsType<Stmt.EnumDecl>(prog[4]);
        Assert.Equal("Shape", shape.Name);
        Assert.Equal(3, shape.Cases.Count);
        Assert.Single(shape.Cases[0].Payloads);
        Assert.Equal(2, shape.Cases[1].Payloads.Count);
        Assert.Empty(shape.Cases[2].Payloads);
    }

    [Fact]
    public void ImportAndExtern()
    {
        var prog = Ok("import \"./md5\"\n");
        Assert.Equal("./md5", Assert.IsType<Stmt.Import>(prog[0]).Path);

        var ext = Assert.IsType<Stmt.Extern>(Ok("extern \"go\" {\n    fn abs(x: int): int\n}\n")[0]);
        Assert.Equal("go", ext.Host);
        var fn = Assert.IsType<Stmt.FnDecl>(Assert.Single(ext.Fns));
        Assert.Empty(fn.Body);
    }

    [Fact]
    public void NodesCarryCodePointSpansWithCjk()
    {
        // let(0..3) sp 消(4)息(5) sp =(6) sp "(9)中(10)文(11)"(12)：全长 13 码点
        var prog = Ok("let 消息 = \"中文\"");
        var stmt = prog[0];
        Assert.Equal(0u, stmt.Span.Start.Offset);
        Assert.Equal(13u, stmt.Span.End.Offset);
        var init = Assert.IsType<Expr.StrLit>(Assert.IsType<Stmt.Let>(stmt).Init);
        Assert.Equal("中文", init.Value);
        Assert.Equal(9u, init.Span.Start.Offset);
        Assert.Equal(13u, init.Span.End.Offset); // 引号 2 + 2 码点 = 4 码点（非 6 字节）
    }

    [Fact]
    public void AstralNodeSpansByCodePoints()
    {
        // 契约 §0 对抗用例：l(0)e(1)t(2) sp(3) 𝕏(4) sp(5) =(6) sp(7) "(8)🦀(9)中(10)"(11)
        // 𝕏/🦀 各 1 码点 2 char——char index 世界里会错位成 4..6 / 8..12
        var prog = Ok("let 𝕏 = \"🦀中\"");
        var let = Assert.IsType<Stmt.Let>(prog[0]);
        Assert.Equal(0u, let.Span.Start.Offset);
        Assert.Equal(12u, let.Span.End.Offset); // 全长 12 码点（14 char）
        Assert.Equal("𝕏", let.Name);
        var str = Assert.IsType<Expr.StrLit>(let.Init);
        Assert.Equal("🦀中", str.Value);
        Assert.Equal(8u, str.Span.Start.Offset);
        Assert.Equal(12u, str.Span.End.Offset); // 引号 2 + 2 码点 = 4 码点
    }

    [Fact]
    public void AstralMatchArmsCarrySpans()
    {
        // 契约 §0：match 模式位置出现 astral 字符串字面量，臂 span 仍按码点闭合
        var e = Assert.IsType<Expr.Match>(FirstExpr("match 𝕏 {\n    \"🦀\" => 1\n    _ => 0\n}"));
        Assert.Equal(2, e.Arms.Count);
        Assert.True(e.Arms[0].Span.End.Offset > e.Arms[0].Span.Start.Offset);
        var lit = Assert.IsType<Pat.Lit>(e.Arms[0].Pat);
        Assert.Equal("🦀", Assert.IsType<Expr.StrLit>(lit.Value).Value);
    }

    [Fact]
    public void ArmAndCaseSpansAreRealNotZero()
    {
        var e = Assert.IsType<Expr.Match>(FirstExpr("match v {\n    1 => x\n}"));
        var a = e.Arms[0];
        Assert.True(a.Span.Start.Offset > 0);
        Assert.True(a.Span.End.Offset > a.Span.Start.Offset);
        Assert.Equal(2u, a.Pat.Span.Start.Line);
    }

    [Fact]
    public void ControlHeadBansBareStructLiteralParse006()
    {
        // 裸 struct 字面量在 if 头部被拒
        Assert.Equal(DiagnosticCode.E1001,
            Fail("fn main() {\n    if Point{x: 1}.x > 0 {\n        println(1)\n    }\n}").Code);
        // 加括号即合法
        Assert.Single(Ok("fn main() {\n    if (Point{x: 1}).x > 0 {\n        println(1)\n    }\n}"));
    }

    [Fact]
    public void StructLiteralAllowedOutsideControlHead()
    {
        Assert.Equal(2, Ok("let p = Point{x: 1}\nprintln(p.x)").Count);
    }

    [Fact]
    public void ReservedWordsRejectedAtTopLevel()
    {
        Assert.Equal("保留字 interface 尚未启用（规划于 M3+）", Fail("interface = 1").Message);
        Assert.Equal("保留字 chan 尚未启用（规划于 M3+）", Fail("chan = 1").Message);
        // 但 chan[int] 类型注解合法（类型位置）
        var fn = Assert.IsType<Stmt.FnDecl>(Ok("fn f(c: chan[int]) {}")[0]);
        var t = Assert.IsType<TypeEx.Named>(fn.Params[0].Typ);
        Assert.Equal("chan", t.Name);
        Assert.Single(t.Args);
    }

    [Fact]
    public void ErrorMessagesFollowContractText()
    {
        Assert.Equal("期望标识符，得到 =", Fail("let = 1").Message);
        Assert.Equal("块未闭合，缺少 }", Fail("fn main() {").Message);
        Assert.Equal("整数溢出: 999999999999999999999999", Fail("999999999999999999999999").Message);
        Assert.Equal("表达式意外结束", Fail("let x =").Message);
        // `1.` 的 '.' 被 postfix 当字段访问消费，随后在文件尾找字段名
        Assert.Equal("期望 标识符，得到 文件尾", Fail("let x = 1.").Message);
        Assert.Equal("match 分支之间需要换行", Fail("match x { 1 => 1 2 => 2 }").Message);
    }

    [Fact]
    public void IntOverflowAndNonAsciiDigitsUnifiedMessage()
    {
        // Go ParseInt 同源：语法非法与溢出统一报「整数溢出」
        Assert.Equal("整数溢出: １２３", Fail("１２３").Message); // 全角数字（Nd），词法收进、parser 拒绝
    }

    [Fact]
    public void AssignmentTargetCheck()
    {
        Assert.Equal("赋值目标不合法", Fail("1 = 2").Message);
        Assert.Equal("赋值目标不合法", Fail("f() = 2").Message);
        Assert.Single(Ok("a.b[0].c = 1")); // 链式字段/索引目标合法
    }

    [Fact]
    public void SliceNeedsUpperBound()
    {
        Assert.Equal("切片需要上界（v0 不支持开区间切片）", Fail("a[1:]").Message);
    }

    [Fact]
    public void GoRequiresCall()
    {
        Assert.Equal("go 的目标须是函数调用", Fail("go worker").Message);
    }

    [Fact]
    public void MultilineCollectionsAndCallArgsSurvive()
    {
        // LEX-008 续行的端到端：行尾逗号 / 开括号使换行存活
        var prog = Ok("""
            fn add(a: int, b: int, c: int): int {
                return a + b + c
            }
            fn main() {
                println(add(
                    1,
                    2,
                    3,
                ))
                let m = {
                    "k": [
                        1,
                        2,
                    ]
                }
                println(m)
            }
            """);
        Assert.Equal(2, prog.Count);
    }

    [Fact]
    public void PointerReceiverRejected()
    {
        // 名字消费后才检查 &（与 TS 证据一致）
        Assert.Equal("v0 暂不支持指针接收者", Fail("fn (r &Rect).area(): int {\n    return 0\n}").Message);
    }

    [Fact]
    public void DiagnosticShapeIsE1001WithLineAnchor()
    {
        var d = Fail("let = 1");
        Assert.Equal("E1001", d.Code.CodeText());
        Assert.Equal("LEX-ALL", d.RuleId.RuleText());
        Assert.Equal(Severity.Error, d.Severity);
        var s = d.PrimarySpan!.Value;
        Assert.Equal(1u, s.Start.Line);
        Assert.Equal(1u, s.Start.Column); // v0.4 行级契约
        Assert.Equal(0u, s.Start.Offset);
    }

    [Fact]
    public void ErrorLinePointsAtOffendingLine()
    {
        Assert.Equal(2u, Fail("let a = 1\nlet = 2\n").PrimarySpan!.Value.Start.Line);
    }

    [Fact]
    public void EmptyTokenStreamIsTreatedAsEmptyFile()
    {
        // pub API 防御：空记号流不 throw，按空程序处理
        var r = Parser.ParseTokens(Array.Empty<Token>(), new SourceId(0));
        Assert.Null(r.Error);
        Assert.Empty(r.Program);
    }

    [Fact]
    public void ExhaustiveDispatchHasNoDefaultArm()
    {
        // 契约 §3：穷尽分派用 switch expression，禁 default 兜底吞新变体
        var e = FirstExpr("2 + 3 * 4");
        string describe = e switch
        {
            Expr.IntLit => "int",
            Expr.FloatLit => "float",
            Expr.StrLit => "str",
            Expr.BoolLit => "bool",
            Expr.Ident => "ident",
            Expr.Unary => "unary",
            Expr.Binary => "binary",
            Expr.Range => "range",
            Expr.Call => "call",
            Expr.Index => "index",
            Expr.Slice => "slice",
            Expr.Field => "field",
            Expr.Try => "try",
            Expr.StructLit => "structlit",
            Expr.MapLit => "maplit",
            Expr.ArrLit => "arrlit",
            Expr.Match => "match",
            Expr.FnLit => "fnlit",
            // C# 开放类层次 switch 表达式必须有终臂：兜底只硬失败，禁静默吞新变体
            _ => throw new InvalidOperationException($"未登记的 Expr 变体: {e.GetType().Name}"),
        };
        Assert.Equal("binary", describe);
    }
}
