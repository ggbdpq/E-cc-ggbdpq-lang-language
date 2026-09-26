using Lang.Core;
using Lang.Syntax;

/// <summary>
/// conformance/syntax/ 13 个夹具的语法面测试：逐 case 解析必须零错误（真值来自 case.yaml
/// 的执行期望属解释器域，这里锁定语法结构）。夹具文件只读。
/// </summary>
public class ConformanceSyntaxTests
{
    private static string? Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "conformance", "syntax");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string MainSrc(string caseName)
    {
        var root = Root();
        Assert.True(root is not null, $"未找到 conformance/syntax（从 {AppContext.BaseDirectory} 上溯）");
        return File.ReadAllText(Path.Combine(root, caseName, "main.lang-go"));
    }

    public static IEnumerable<object[]> CaseNames()
    {
        var root = Root();
        if (root is null) return [];
        // 显式排序：目录枚举序不作为契约
        return Directory.GetDirectories(root).Select(Path.GetFileName).Order().Select(n => new object[] { n! });
    }

    private static IReadOnlyList<Stmt> Parsed(string caseName)
    {
        var r = Parser.Parse(new SourceText($"{caseName}/main.lang-go", MainSrc(caseName)), new SourceId(0));
        Assert.Null(r.Error);
        return r.Program;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ParsesClean(string caseName)
    {
        var prog = Parsed(caseName);
        Assert.NotEmpty(prog);
    }

    [Fact]
    public void CaseCountIsThirteen()
    {
        Assert.Equal(13, CaseNames().Count());
    }

    [Fact]
    public void ArithmeticPrecedenceShape()
    {
        // 2 + 3 * 4 → + 在顶
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("arithmetic-precedence")[0]);
        var call = Assert.IsType<Expr.Call>(Assert.IsType<Stmt.ExprStmt>(main.Body[0]).Expr);
        var first = Assert.IsType<Expr.Binary>(call.Args[0]);
        Assert.Equal(BinaryOp.Add, first.Op);
        Assert.Equal(BinaryOp.Mul, Assert.IsType<Expr.Binary>(first.Rhs).Op);
        // 2.5 * 2.0 → 浮点二元
        var last = Assert.IsType<Expr.Binary>(call.Args[^1]);
        Assert.Equal(BinaryOp.Mul, last.Op);
        Assert.IsType<Expr.FloatLit>(last.Lhs);
    }

    [Fact]
    public void CommentTriviaIgnoredAndRecorded()
    {
        var src = MainSrc("comment-trivia-ignored");
        var r = Parser.Parse(new SourceText("comment/main.lang-go", src), new SourceId(0));
        Assert.Null(r.Error);
        var comments = TriviaScanner.Scan(new SourceText("comment/main.lang-go", src), new SourceId(0))
            .Where(t => t.Kind == TriviaKind.Comment).Select(t => t.Text).ToArray();
        Assert.Equal(["// 行注释", "// 注释不影响语义", "// 尾注释"], comments);
    }

    [Fact]
    public void HexAndArithNormalizes()
    {
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("hex-and-arith")[0]);
        var call = Assert.IsType<Expr.Call>(Assert.IsType<Stmt.ExprStmt>(main.Body[0]).Expr);
        Assert.Equal(16, Assert.IsType<Expr.IntLit>(call.Args[0]).Value);
        Assert.Equal(255, Assert.IsType<Expr.IntLit>(call.Args[1]).Value);
        Assert.Equal(255, Assert.IsType<Expr.IntLit>(call.Args[2]).Value);
    }

    [Fact]
    public void LineContinuationShape()
    {
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("line-continuation")[0]);
        // let x = 1 +\n 2 → Binary(+, 1, 2)
        var let = Assert.IsType<Stmt.Let>(main.Body[0]);
        var add = Assert.IsType<Expr.Binary>(let.Init);
        Assert.Equal(BinaryOp.Add, add.Op);
        Assert.Equal(1, Assert.IsType<Expr.IntLit>(add.Lhs).Value);
        Assert.Equal(2, Assert.IsType<Expr.IntLit>(add.Rhs).Value);
    }

    [Fact]
    public void MultilineCallArgsCount()
    {
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("multiline-call-args")[1]);
        var println = Assert.IsType<Expr.Call>(Assert.IsType<Stmt.ExprStmt>(main.Body[0]).Expr);
        var add = Assert.IsType<Expr.Call>(Assert.Single(println.Args));
        Assert.Equal(3, add.Args.Count); // 行尾逗号续行：1, 2, 3,
    }

    [Fact]
    public void NestedCollectionsShape()
    {
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("nested-collections-literal")[0]);
        var let = Assert.IsType<Stmt.Let>(main.Body[0]);
        var map = Assert.IsType<Expr.MapLit>(let.Init);
        Assert.Equal(2, map.Items.Count);
        var inner = Assert.IsType<Expr.ArrLit>(map.Items[0].Value);
        Assert.IsType<Expr.ArrLit>(inner.Items[1]); // [1, [2, 3]]
    }

    [Fact]
    public void NestedFnLiteralShape()
    {
        // make(): fn(): int —— 返回类型是 fn 类型（fnlit type_params 必须保留，契约教训）
        var make = Assert.IsType<Stmt.FnDecl>(Parsed("nested-fn-literal-return")[0]);
        var ret = Assert.IsType<TypeEx.Fn>(make.Ret!);
        Assert.NotNull(ret.Ret);
        var retStmt = Assert.IsType<Stmt.Return>(make.Body[1]);
        var fnlit = Assert.IsType<Expr.FnLit>(retStmt.Value!);
        Assert.Empty(fnlit.TypeParams);
        Assert.NotNull(fnlit.Ret);
    }

    [Fact]
    public void PrecedenceBitVsArithShapes()
    {
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("precedence-bit-vs-arith")[0]);
        var call = Assert.IsType<Expr.Call>(Assert.IsType<Stmt.ExprStmt>(main.Body[0]).Expr);
        // 1 | 2 + 3 → (1 | 2) + 3
        var add = Assert.IsType<Expr.Binary>(call.Args[0]);
        Assert.Equal(BinaryOp.Add, add.Op);
        Assert.Equal(BinaryOp.BitOr, Assert.IsType<Expr.Binary>(add.Lhs).Op);
        // 1 << 2 + 1 → (1 << 2) + 1
        var stmt2 = Assert.IsType<Stmt.ExprStmt>(main.Body[1]);
        var add2 = Assert.IsType<Expr.Binary>(Assert.IsType<Expr.Call>(stmt2.Expr).Args[0]);
        Assert.Equal(BinaryOp.Add, add2.Op);
        Assert.Equal(BinaryOp.Shl, Assert.IsType<Expr.Binary>(add2.Lhs).Op);
    }

    [Fact]
    public void StringEscapesFullDecodes()
    {
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("string-escapes-full")[0]);
        static string Arg0(Stmt s)
        {
            var call = Assert.IsType<Expr.Call>(Assert.IsType<Stmt.ExprStmt>(s).Expr);
            return Assert.IsType<Expr.StrLit>(call.Args[0]).Value;
        }
        Assert.Equal("tab\there", Arg0(main.Body[0]));
        Assert.Equal("quote\"q", Arg0(main.Body[1]));
        Assert.Equal("backslash\\", Arg0(main.Body[2]));
    }

    [Fact]
    public void StringOpsShape()
    {
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("string-ops")[0]);
        var call = Assert.IsType<Expr.Call>(Assert.IsType<Stmt.ExprStmt>(main.Body[0]).Expr);
        Assert.Equal(4, call.Args.Count);
        // len("héllo") —— 首参是调用，其参为字符串字面量
        var lenCall = Assert.IsType<Expr.Call>(call.Args[0]);
        Assert.Equal("héllo", Assert.IsType<Expr.StrLit>(lenCall.Args[0]).Value);
        // "a" + "b"
        var concat = Assert.IsType<Expr.Binary>(call.Args[2]);
        Assert.Equal(BinaryOp.Add, concat.Op);
    }

    [Fact]
    public void UnaryNegationNestedShape()
    {
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("unary-negation-nested")[0]);
        var call = Assert.IsType<Expr.Call>(Assert.IsType<Stmt.ExprStmt>(main.Body[0]).Expr);
        var neg = Assert.IsType<Expr.Unary>(call.Args[0]); // -(-5)
        Assert.Equal(UnaryOp.Neg, neg.Op);
        Assert.IsType<Expr.Unary>(neg.Operand);
    }

    [Fact]
    public void UnicodeIdentifiersAndStrings()
    {
        var main = Assert.IsType<Stmt.FnDecl>(Parsed("unicode-identifiers-and-strings")[0]);
        var let = Assert.IsType<Stmt.Let>(main.Body[0]);
        Assert.Equal("消息", let.Name);
        Assert.Equal("中文测试", Assert.IsType<Expr.StrLit>(let.Init).Value);
    }

    [Fact]
    public void AstralSnippetParsesCleanWithCodePointSpans()
    {
        // 契约 §0：conformance 域的 astral 用例（🦀 / 𝕏 / 𠀀）
        const string src = """
            let 𝕏 = "🦀𠀀"
            fn main() {
                println(𝕏, len(𝕏))
            }
            """;
        var r = Parser.Parse(new SourceText("astral/main.lang-go", src), new SourceId(0));
        Assert.Null(r.Error);
        var let = Assert.IsType<Stmt.Let>(r.Program[0]);
        var str = Assert.IsType<Expr.StrLit>(let.Init);
        // l(0)e(1)t(2) sp(3) 𝕏(4) sp(5) =(6) sp(7) "(8)🦀(9)𠀀(10)"(11)：字符串 span [8, 12)，全长 12 码点
        Assert.Equal(8u, str.Span.Start.Offset);
        Assert.Equal(12u, str.Span.End.Offset);
        Assert.Equal("🦀𠀀", str.Value);
        Assert.Equal(12u, let.Span.End.Offset);
    }
}
