using Lang.Core;
using Lang.Interp;
using AstInterp = Lang.Interp.Interp;
using Lang.Runtime;
using Lang.Syntax;

namespace Lang.Cli;

/// <summary>
/// <c>run [dir]</c> 与 <c>test [dir]</c>（对照 lang-rs commands.rs / Go cli.go 证据）。
/// 执行路 = AST 参考解释器（Lang.Interp）；装载走第二波 <see cref="Loader.LoadModule"/>
/// （Lang.Loader + ParseImports 组合，includeTests=true 追加 *_test.lang-go——不再引入
/// 第二份 mini 装载器，单一装载事实源）。
/// </summary>
internal static class RunTest
{
    /// <summary><c>run [dir]</c>：装载 → analyze（分析诊断非空打 check JSON 信封并 exit 1；
    /// 解析诊断走 check 文本契约）→ AST 解释器 stdout 直出。装载器错误走 stderr。</summary>
    internal static int CmdRun(IReadOnlyList<string> args)
    {
        string dir = Commands.Positional(args);
        switch (Loader.LoadModule(dir, includeTests: false))
        {
            case Loader.LoadResult.LoadError load:
                Console.Error.WriteLine($"装载失败: {load.Message}");
                return 1;
            case Loader.LoadResult.ParseError parse:
                Console.Error.WriteLine($"检查未通过:\n{Out.FormatDiags(parse.Diagnostics, parse.Names)}");
                return 1;
            case Loader.LoadResult.Ok ok:
                if (ok.Module.Analysis.HasErrors())
                {
                    Out.PrintJson(Out.CheckEnvelopeOf(ok.Module.Analysis.Diags, ok.Module.Names));
                    return 1;
                }

                return Execute(ok.Module.Merged, ok.Module.Analysis);
            default:
                throw new InvalidOperationException("LoadResult 变体未穷尽（内部不变量）");
        }
    }

    /// <summary><c>test [dir]</c>：装载（含 *_test.lang-go）→ 顶层 <c>fn test*</c>
    /// （无接收者、无参）依声明序执行。执行序对照 TS/Rust 证据（evalChunk 不跑 main →
    /// 逐个 callByName）：先执行「去掉顶层 fn main」的合并序列，再逐测试以合成的
    /// 单语句调用切片走 <see cref="AstInterp.Run"/>（同一解释器实例共享状态）。</summary>
    internal static int CmdTest(IReadOnlyList<string> args)
    {
        string dir = Commands.Positional(args);
        switch (Loader.LoadModule(dir, includeTests: true))
        {
            case Loader.LoadResult.LoadError load:
                Console.Error.WriteLine($"装载失败: {load.Message}");
                return 1;
            case Loader.LoadResult.ParseError parse:
                Console.Error.WriteLine($"检查未通过:\n{Out.FormatDiags(parse.Diagnostics, parse.Names)}");
                return 1;
            case Loader.LoadResult.Ok ok:
                return RunTests(ok.Module);
            default:
                throw new InvalidOperationException("LoadResult 变体未穷尽（内部不变量）");
        }
    }

    private static int RunTests(Loader.CliModule module)
    {
        if (module.Analysis.HasErrors())
        {
            Console.Error.WriteLine(
                $"检查未通过:\n{Out.FormatDiags(module.Analysis.Diags, module.Names)}");
            return 1;
        }

        var tests = DiscoverTests(module.Merged);
        if (tests.Count == 0)
        {
            Console.WriteLine("未发现 *_test.lang-go");
            return 0;
        }

        // evalChunk 等价：顶层语句全执行、main 不调用（滤除顶层 main 声明）
        var body = module.Merged
            .Where(s => s is not Stmt.FnDecl { Recv: null, Name: "main" })
            .ToList();
        var interp = new AstInterp(new Machine());
        var run = interp.Run(body, module.Analysis, new StdoutSink());
        if (!run.IsOk)
        {
            Console.Error.WriteLine($"装载失败: {run.Error!.Message}");
            return 1;
        }

        int pass = 0;
        int fail = 0;
        foreach (var name in tests)
        {
            var slice = new Stmt[] { TestCallStmt(name) };
            var r = interp.Run(slice, module.Analysis, new StdoutSink());
            if (r.IsOk)
            {
                pass++;
                Console.WriteLine($"PASS {name}");
            }
            else
            {
                fail++;
                Console.WriteLine($"FAIL {name}: {r.Error!.Message}");
            }
        }

        Console.WriteLine($"\n{pass} 通过, {fail} 失败");
        return fail > 0 ? 1 : 0;
    }

    /// <summary>test 函数发现（Go loader 证据同规则）：顶层 <c>fn test*</c>、
    /// 无接收者、无参数；声明序（合并程序出现序）。</summary>
    internal static List<string> DiscoverTests(IReadOnlyList<Stmt> merged)
    {
        var tests = new List<string>();
        foreach (var s in merged)
        {
            if (s is Stmt.FnDecl { Recv: null, Params: { Count: 0 } } d
                && d.Name.StartsWith("test", StringComparison.Ordinal))
            {
                tests.Add(d.Name);
            }
        }

        return tests;
    }

    /// <summary>合成 <c>name()</c> 单语句调用（callByName 等价物；span 为合成锚点，
    /// 不参与语义分析——解释器只消费 HasErrors 门）。</summary>
    internal static Stmt TestCallStmt(string name)
    {
        var span = Span.Point(new SourceId(0), new Position(0, 1, 1));
        return new Stmt.ExprStmt(
            span,
            new Expr.Call(span, new Expr.Ident(span, name), []));
    }

    /// <summary>干净程序的解释执行；错误走 stderr「错误:」+ exit 1。</summary>
    private static int Execute(IReadOnlyList<Stmt> stmts, Lang.Semantic.Analysis analysis)
    {
        var r = new AstInterp(new Machine()).Run(stmts, analysis, new StdoutSink());
        if (r.IsOk)
        {
            return 0;
        }

        Console.Error.WriteLine($"错误: {r.Error!.Message}");
        return 1;
    }
}

/// <summary>收集型输出汇（verify 差分路：stdout 三要素比对用）。</summary>
internal sealed class CollectSink : OutputSink
{
    private readonly System.Text.StringBuilder _sb = new();

    public void Write(string s) => _sb.Append(s);

    public override string ToString() => _sb.ToString();
}
