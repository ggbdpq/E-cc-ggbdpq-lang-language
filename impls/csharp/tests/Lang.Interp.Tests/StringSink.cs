using System.Text;
using Lang.Core;
using Lang.Ir;
using Lang.Runtime;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Interp.Tests;

/// <summary>测试用输出汇：内存缓冲（进程 stdout 的替身）。</summary>
public sealed class StringSink : OutputSink
{
    private readonly StringBuilder _sb = new();

    public void Write(string s) => _sb.Append(s);

    public override string ToString() => _sb.ToString();
}

/// <summary>源码 → 分析 → 双路解释的便捷入口（测试夹具共享）。</summary>
public static class RunHelper
{
    /// <summary>解析单文件源码（失败返回 null 与诊断消息）。</summary>
    public static (IReadOnlyList<Stmt>? Program, string? Error) Parse(string text, uint sourceId)
    {
        var parsed = Parser.Parse(new SourceText("probe.lang-go", text), new SourceId(sourceId));
        return parsed.Error is null ? (parsed.Program, null) : (null, parsed.Error.Message);
    }

    /// <summary>源码 → 分析（期望零诊断）。</summary>
    public static Analysis Analyze(IReadOnlyList<Stmt> stmts) =>
        LangSemantic.Analyze(stmts, new SourceId(0));

    /// <summary>AST 参考解释器路径。</summary>
    public static (bool Ok, string Stdout, string? Error) RunAst(IReadOnlyList<Stmt> stmts, Analysis analysis)
    {
        var sink = new StringSink();
        var interp = new Interp(new Machine());
        var r = interp.Run(stmts, analysis, sink);
        return r.IsOk ? (true, sink.ToString(), null) : (false, sink.ToString(), r.Error!.Message);
    }

    /// <summary>IR 解释器路径（Compiler.Compile 全链 + 栈式基本块执行）。</summary>
    public static (bool Ok, string Stdout, string? Error) RunIr(IReadOnlyList<Stmt> stmts, Analysis analysis)
    {
        var compiled = Compiler.Compile(stmts, analysis);
        if (!compiled.IsOk)
        {
            return (false, string.Empty, $"compile: {compiled.Error}");
        }

        var sink = new StringSink();
        var machine = new IrMachine(compiled.Value!);
        var r = machine.Run(sink);
        return r.IsOk ? (true, sink.ToString(), null) : (false, sink.ToString(), r.Error!.Message);
    }
}
