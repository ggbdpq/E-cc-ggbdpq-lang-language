using Lang.Core;
using Lang.Syntax;

namespace Lang.Formatter.Tests;

/// <summary>
/// Formatter 行为面（期望值对照 rust lang-formatter 测试 / impls/go format_test 的 Go 证据，
/// 逐条手算核对，不拿本实现当期望）。覆盖缩进、末尾单换行、优先级括号、字面量规范化、
/// 控制流、声明、go_format_float 表、确定性、CRLF 归一、astral 码点。
/// </summary>
public class FormatterBehaviorTests
{
    private static string Fmt(string src)
    {
        var r = Formatter.FormatSource(src);
        Assert.Null(r.Error);
        return r.Output;
    }

    [Fact]
    public void IndentNormalizationMatchesGoEvidence()
    {
        // 期望取自 impls/go/internal/format/format_test.go Test缩进规范化
        var out1 = Fmt("fn main() {\nprintln(1)\nif true {\nprintln(2)\n}\n}\n");
        Assert.Equal("fn main() {\n    println(1)\n    if true {\n        println(2)\n    }\n}\n", out1);
    }

    [Fact]
    public void EmptyInputIsEmptyOutput()
    {
        Assert.Equal("", Fmt(""));
        Assert.Equal("", Fmt("\n\n  \t \n"));
    }

    [Fact]
    public void TrailingSingleNewlineAlways()
    {
        Assert.Equal("let x = 1\n", Fmt("let x = 1"));
        Assert.Equal("let x = 1\n", Fmt("let x = 1\n\n\n"));
    }

    [Fact]
    public void ElseIfChainStaysFlattened()
    {
        const string src = "if a > 0 {\n    println(1)\n} else if a < 0 {\n    println(2)\n} else {\n    println(3)\n}\n";
        Assert.Equal(src, Fmt(src));
    }

    [Fact]
    public void BareReturnAndWhileForBreakContinueGo()
    {
        const string src = """
            fn f(n: int): int {
                while n > 0 {
                    n -= 1
                    if n == 3 {
                        continue
                    }
                    if n == 1 {
                        break
                    }
                }
                for x in [1, 2] {
                    println(x)
                }
                go worker(1)
                return
            }
            """;
        var expected = src.ReplaceLineEndings("\n") + "\n";
        Assert.Equal(expected, Fmt(src.ReplaceLineEndings("\n")));
    }

    [Fact]
    public void MethodAndGenericFnAndStructEnum()
    {
        const string src = """
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
            """;
        Assert.Equal(src.ReplaceLineEndings("\n") + "\n", Fmt(src.ReplaceLineEndings("\n")));
    }

    [Fact]
    public void ImportAndExternKept()
    {
        // 期望取自 impls/go/internal/format/format_test.go TestImport与Extern保留
        const string src = "import \"./lib\"\nextern \"go\" {\n    fn hostFn(x: int): int\n}\nfn main() {\n    println(1)\n}\n";
        var out1 = Fmt(src);
        Assert.Contains("import \"./lib\"", out1);
        Assert.Contains("extern \"go\" {", out1);
        Assert.Contains("    fn hostFn(x: int): int", out1);
        Assert.Equal(out1, Fmt(out1));
    }

    [Fact]
    public void ChanAnnotationPassthrough()
    {
        // 期望取自 impls/go/internal/format/format_test.go TestChan注解输出
        var out1 = Fmt("fn prod(ch: chan[int], n: int) {\nsend(ch, n)\n}\n");
        Assert.Contains("fn prod(ch: chan[int], n: int)", out1);
        Assert.Equal(out1, Fmt(out1));
    }

    [Fact]
    public void ParensOnlyWhereNeeded()
    {
        Assert.Equal("let a = (1 + 2) * 3\n", Fmt("let a = (1 + 2) * 3"));
        Assert.Equal("let a = 1 + 2 * 3\n", Fmt("let a = 1 + 2 * 3"));
        // 结合性需要的括号保留
        Assert.Equal("let a = 1 - (2 - 3)\n", Fmt("let a = 1 - (2 - 3)"));
        Assert.Equal("let a = 1 - 2 - 3\n", Fmt("let a = 1 - 2 - 3"));
        // 冗余括号规范化掉（解析即弃）
        Assert.Equal("let a = 1\n", Fmt("let a = (((1)))"));
        // 后缀链优先级高于一元
        Assert.Equal("let a = -p.x\n", Fmt("let a = -p.x"));
        Assert.Equal("let a = (-p).x\n", Fmt("let a = (-p).x"));
    }

    [Fact]
    public void HexAndFloatLiteralsNormalize()
    {
        Assert.Equal("let a = 255\n", Fmt("let a = 0xFF"));
        // 保 float 身份：整数值浮点补 .0
        Assert.Equal("let a = 2.0\n", Fmt("let a = 2.0"));
        Assert.Equal("let a = 2.5\n", Fmt("let a = 2.50"));
        Assert.Equal("let a = 123456.0\n", Fmt("let a = 123456.0"));
        Assert.Equal("let a = 0.5\n", Fmt("let a = 0.5"));
    }

    [Fact]
    public void StringEscapesRoundTrip()
    {
        const string src = "let s = \"a\\n\\t\\r\\\"\\\\b\"\n";
        Assert.Equal(src, Fmt(src));
        // 字符串内的伪注释不是注释，原样保留在字面量里
        Assert.Equal("let s = \"http://x //not\"\n", Fmt("let s = \"http://x //not\"\n"));
    }

    [Fact]
    public void MatchAndFnLitShapes()
    {
        const string src = """
            let m = match v {
                1 => "一"
                _ => "多"
            }
            let f = fn(x: int): int {
                return x + 1
            }
            """;
        Assert.Equal(src.ReplaceLineEndings("\n") + "\n", Fmt(src.ReplaceLineEndings("\n")));
    }

    [Fact]
    public void FnLitKeepsTypeParams()
    {
        // 语义保留偏差修正：证据实现丢弃 fnlit 的 type_params，这里保留
        Assert.Equal("let f = fn[T](x: T): T {\n    return x\n}\n",
            Fmt("let f = fn[T](x: T): T {\n    return x\n}\n"));
    }

    [Fact]
    public void StructLitMapLitArrLitOneLine()
    {
        Assert.Equal("let p = Point{x: 1, y: 2}\n", Fmt("let p = Point{x: 1, y: 2}\n"));
        Assert.Equal("let m = {\"a\": 1, \"b\": 2}\n", Fmt("let m = {\"a\": 1, \"b\": 2}\n"));
        Assert.Equal("let a = [1, 2, 3]\n", Fmt("let a = [1, 2, 3]\n"));
        Assert.Equal("let s = a[1:2].b(x)?\n", Fmt("let s = a[1:2].b(x)?\n"));
    }

    [Fact]
    public void RangeAndOperatorSpacing()
    {
        Assert.Equal("for i in 1..n {\n    println(i)\n}\n", Fmt("for i in 1..n {\n    println(i)\n}\n"));
        Assert.Equal("let a = 1 << 2 + 1\n", Fmt("let a = 1 << 2 + 1\n"));
        Assert.Equal("let a = x == 1 && y != 2\n", Fmt("let a = x == 1 && y != 2\n"));
    }

    [Fact]
    public void GoFloatMatchesGoGFormat()
    {
        // 期望值逐条对照 Go：strconv.FormatFloat(f, 'g', -1, 64)（rust 测试同表）
        Assert.Equal("NaN", Formatter.GoFormatFloat(double.NaN));
        Assert.Equal("+Inf", Formatter.GoFormatFloat(double.PositiveInfinity));
        Assert.Equal("-Inf", Formatter.GoFormatFloat(double.NegativeInfinity));
        Assert.Equal("0", Formatter.GoFormatFloat(0.0));
        Assert.Equal("-0", Formatter.GoFormatFloat(-0.0));
        Assert.Equal("2.5", Formatter.GoFormatFloat(2.5));
        Assert.Equal("0.5", Formatter.GoFormatFloat(0.5));
        Assert.Equal("1.25", Formatter.GoFormatFloat(1.25));
        Assert.Equal("100.25", Formatter.GoFormatFloat(100.25));
        Assert.Equal("1234.5", Formatter.GoFormatFloat(1234.5));
        Assert.Equal("123456", Formatter.GoFormatFloat(123456.0)); // exp 5 < 6 → 十进制
        Assert.Equal("1.234567e+06", Formatter.GoFormatFloat(1234567.0)); // exp 6 → 科学
        Assert.Equal("0.0001", Formatter.GoFormatFloat(0.0001)); // exp -4 → 十进制
        Assert.Equal("1e-05", Formatter.GoFormatFloat(0.00001)); // exp -5 → 科学
        Assert.Equal("1e+21", Formatter.GoFormatFloat(1e21));
        Assert.Equal("-2.75", Formatter.GoFormatFloat(-2.75));
        Assert.Equal("1.005", Formatter.GoFormatFloat(1.005));
    }

    [Fact]
    public void ParseFailureYieldsDiagnosticNotThrow()
    {
        var r = Formatter.FormatSource("fn main() {\nlet x = = 1\n}\n");
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
        Assert.Equal(DiagnosticCode.E1001, r.Error!.Code);
        Assert.Equal("", r.Output);
    }

    [Fact]
    public void ApiFormatTakesStmtsTriviaSource()
    {
        var text = new SourceText("t.lang-go", "let x = 1 // 尾\n");
        var parsed = Parser.Parse(text, new SourceId(0));
        Assert.Null(parsed.Error);
        var trivia = TriviaScanner.Scan(text, new SourceId(0));
        Assert.Equal("let x = 1  // 尾\n", Formatter.Format(parsed.Program, trivia, text));
    }

    [Fact]
    public void DeterminismSameInputSameOutput()
    {
        const string src = "fn main() {\n    let x = 1 // 注\n    println(x)\n}\n";
        Assert.Equal(Fmt(src), Fmt(src));
        var text = new SourceText("t.lang-go", src);
        var parsed = Parser.Parse(text, new SourceId(0));
        var trivia = TriviaScanner.Scan(text, new SourceId(0));
        Assert.Equal(Formatter.Format(parsed.Program, trivia, text),
                     Formatter.Format(parsed.Program, trivia, text));
    }

    [Fact]
    public void CrlfSourceNormalizesToLf()
    {
        var out1 = Fmt("fn main() {\r\n    let x = 1 // CRLF 注释\r\n    println(x)\r\n}\r\n");
        Assert.Contains("let x = 1  // CRLF 注释", out1);
        Assert.DoesNotContain("\r", out1);
        Assert.Equal(out1, Fmt(out1));
    }

    // —— UTF-16 码点世界（契约 §0 astral 用例） ——

    [Fact]
    public void AstralIdentifiersStringsAndLineNumbers()
    {
        // 行号折算权威走 SourceText 码点：astral 内容不挤歪注释贴附与空行分组
        const string src = "let 𝕏 = \"🦀𠀀🦀𠀀🦀𠀀\"\n\n// 独立注释（前有 astral 行）\nlet y = 2 // 𝕏 尾注\n";
        var out1 = Fmt(src);
        // 行尾注释规范化为两空格间隔，其余原样
        Assert.Equal("let 𝕏 = \"🦀𠀀🦀𠀀🦀𠀀\"\n\n// 独立注释（前有 astral 行）\nlet y = 2  // 𝕏 尾注\n", out1);
        Assert.Equal(out1, Fmt(out1));
    }
}
