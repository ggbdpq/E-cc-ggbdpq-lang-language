using System.Globalization;
using System.Text;
using System.Text.Json;
using Lang.Ir;
using Lang.Syntax;

namespace Lang.Cli;

/// <summary>
/// <c>dump ast|ir [dir]</c>：编译器内部树的调试输出（lang-rs dump.rs 同口径）。
/// 确定性面：ast 用本文件的结构转储器（同输入逐字节相等），ir 用 Lang.Ir 冻结的
/// <see cref="IrDumper.DumpProgram"/>（同 IrProgram 两次逐字节相等）。
/// 调试视图，非跨版本契约。extern 内联签名与嵌套 fn 均随语句流展开。
/// </summary>
internal static class DumpCmd
{
    internal static int CmdDump(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            Out.PrintJson(Out.ErrorEnvelopeOf("dump", "缺少子命令：ast|ir"));
            return 2;
        }

        var sub = args[0];
        if (sub is not ("ast" or "ir"))
        {
            Out.PrintJson(Out.ErrorEnvelopeOf("dump", $"未知子命令 {sub}"));
            return 2;
        }

        var dir = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? ".";
        var load = Loader.LoadModule(dir, includeTests: false);
        switch (load)
        {
            case Loader.LoadResult.LoadError:
                Console.Error.WriteLine("装载失败");
                return 1;
            case Loader.LoadResult.ParseError parse:
                Console.Error.WriteLine(
                    $"模块存在 {parse.Diagnostics.Count} 条诊断，ir dump 需要干净程序（先 check）");
                return 1;
            case Loader.LoadResult.Ok ok:
                return sub == "ast" ? DumpAst(ok.Module.Merged) : DumpIr(ok.Module);
            default:
                throw new InvalidOperationException("LoadResult 变体未穷尽（内部不变量）");
        }
    }

    private static int DumpAst(IReadOnlyList<Stmt> merged)
    {
        Console.Out.Write(AstDump.Dump(merged));
        return 0;
    }

    private static int DumpIr(Loader.CliModule module)
    {
        if (module.Analysis.HasErrors())
        {
            Console.Error.WriteLine(
                $"模块存在 {module.Analysis.Diags.Count} 条诊断，ir dump 需要干净程序（先 check）");
            return 1;
        }

        var compiled = Compiler.Compile(module.Merged, module.Analysis);
        if (!compiled.IsOk)
        {
            Console.Error.WriteLine($"IR 编译失败: {compiled.Error}");
            return 1;
        }

        Console.Out.Write(IrDumper.DumpProgram(compiled.Value!));
        return 0;
    }
}

/// <summary>AST 结构转储（确定性调试视图；同输入逐字节相等；不含 span）。
/// 形态：每节点一行、两空格缩进，表达式为紧凑 s 形（单行）；字符串以 JSON 转义形
/// （确定性转义单一事实源），浮点最短往返（"R"）。</summary>
internal static class AstDump
{
    internal static string Dump(IReadOnlyList<Stmt> stmts)
    {
        var sb = new StringBuilder();
        sb.Append("program\n");
        foreach (var s in stmts)
        {
            StmtLine(sb, s, 1);
        }

        return sb.ToString();
    }

    private static void Indent(StringBuilder sb, int depth)
    {
        for (var i = 0; i < depth; i++)
        {
            sb.Append("  ");
        }
    }

    private static void StmtLine(StringBuilder sb, Stmt s, int depth)
    {
        switch (s)
        {
            case Stmt.Let let:
                Indent(sb, depth);
                sb.Append(let.Mutable ? "var " : "let ").Append(let.Name);
                if (let.Typ is { } t)
                {
                    sb.Append(": ").Append(TypeExText(t));
                }

                sb.Append('\n');
                ExprLine(sb, "init", let.Init, depth + 1);
                break;
            case Stmt.Assign assign:
                Indent(sb, depth);
                sb.Append("assign ").Append(assign.Op.ToString().ToLowerInvariant()).Append('\n');
                ExprLine(sb, "target", assign.Target, depth + 1);
                ExprLine(sb, "value", assign.Value, depth + 1);
                break;
            case Stmt.ExprStmt es:
                Indent(sb, depth);
                sb.Append("expr\n");
                ExprLine(sb, "e", es.Expr, depth + 1);
                break;
            case Stmt.Return ret:
                Indent(sb, depth);
                sb.Append("return\n");
                if (ret.Value is not null)
                {
                    ExprLine(sb, "value", ret.Value, depth + 1);
                }

                break;
            case Stmt.If iff:
                Indent(sb, depth);
                sb.Append("if\n");
                ExprLine(sb, "cond", iff.Cond, depth + 1);
                BlockLines(sb, "then", iff.ThenBody, depth + 1);
                if (iff.ElseBody is not null)
                {
                    BlockLines(sb, "else", iff.ElseBody, depth + 1);
                }

                break;
            case Stmt.While w:
                Indent(sb, depth);
                sb.Append("while\n");
                ExprLine(sb, "cond", w.Cond, depth + 1);
                BlockLines(sb, "body", w.Body, depth + 1);
                break;
            case Stmt.ForIn forIn:
                Indent(sb, depth);
                sb.Append("for ").Append(forIn.Variable).Append('\n');
                ExprLine(sb, "iter", forIn.Iter, depth + 1);
                BlockLines(sb, "body", forIn.Body, depth + 1);
                break;
            case Stmt.Break:
                Indent(sb, depth);
                sb.Append("break\n");
                break;
            case Stmt.Continue:
                Indent(sb, depth);
                sb.Append("continue\n");
                break;
            case Stmt.FnDecl fn:
                Indent(sb, depth);
                sb.Append(fn.Recv is not null ? "method " : "fn ").Append(fn.Name);
                if (fn.TypeParams.Count > 0)
                {
                    sb.Append('[').Append(string.Join(", ", fn.TypeParams)).Append(']');
                }

                if (fn.Recv is { } recv)
                {
                    sb.Append(" recv=").Append(recv.Name).Append(':').Append(TypeExText(recv.Typ!));
                }

                sb.Append('(')
                    .Append(string.Join(", ", fn.Params.Select(p => ParamText(p))))
                    .Append(')');
                if (fn.Ret is { } retTy)
                {
                    sb.Append(": ").Append(TypeExText(retTy));
                }

                sb.Append('\n');
                BlockLines(sb, "body", fn.Body, depth + 1);
                break;
            case Stmt.StructDecl sd:
                Indent(sb, depth);
                sb.Append("struct ").Append(sd.Name);
                if (sd.TypeParams.Count > 0)
                {
                    sb.Append('[').Append(string.Join(", ", sd.TypeParams)).Append(']');
                }

                sb.Append('\n');
                foreach (var f in sd.Fields)
                {
                    Indent(sb, depth + 1);
                    sb.Append("field ").Append(f.Name).Append(": ").Append(TypeExText(f.Typ!)).Append('\n');
                }

                break;
            case Stmt.EnumDecl ed:
                Indent(sb, depth);
                sb.Append("enum ").Append(ed.Name);
                if (ed.TypeParams.Count > 0)
                {
                    sb.Append('[').Append(string.Join(", ", ed.TypeParams)).Append(']');
                }

                sb.Append('\n');
                foreach (var c in ed.Cases)
                {
                    Indent(sb, depth + 1);
                    sb.Append("case ").Append(c.Name)
                        .Append('(').Append(string.Join(", ", c.Payloads.Select(TypeExText))).Append(")\n");
                }

                break;
            case Stmt.Go go:
                Indent(sb, depth);
                sb.Append("go\n");
                ExprLine(sb, "call", go.Call, depth + 1);
                break;
            case Stmt.Import imp:
                Indent(sb, depth);
                sb.Append("import ").Append(StrLit(imp.Path)).Append('\n');
                break;
            case Stmt.Extern externDecl:
                Indent(sb, depth);
                sb.Append("extern ").Append(StrLit(externDecl.Host)).Append('\n');
                foreach (var f in externDecl.Fns)
                {
                    StmtLine(sb, f, depth + 1);
                }

                break;
            default:
                throw new InvalidOperationException("Stmt 变体未穷尽（内部不变量）");
        }
    }

    private static void BlockLines(StringBuilder sb, string label, IReadOnlyList<Stmt> body, int depth)
    {
        Indent(sb, depth);
        sb.Append(label).Append('\n');
        foreach (var s in body)
        {
            StmtLine(sb, s, depth + 1);
        }
    }

    private static void ExprLine(StringBuilder sb, string label, Expr e, int depth)
    {
        Indent(sb, depth);
        sb.Append(label).Append(' ').Append(ExprText(e)).Append('\n');
    }

    private static string ParamText(Param p) =>
        p.Typ is { } t ? $"{p.Name}: {TypeExText(t)}" : p.Name;

    private static string TypeExText(TypeEx t) => t switch
    {
        TypeEx.Named n => n.Args.Count == 0
            ? n.Name
            : $"{n.Name}[{string.Join(", ", n.Args.Select(TypeExText))}]",
        TypeEx.Arr a => $"[{TypeExText(a.Elem)}]",
        TypeEx.Map m => $"map[{TypeExText(m.Key)}]{TypeExText(m.Val)}",
        TypeEx.Ptr p => $"&{TypeExText(p.Elem)}",
        TypeEx.Fn f => $"fn({string.Join(", ", f.Params.Select(TypeExText))})"
            + (f.Ret is { } r ? $": {TypeExText(r)}" : ""),
        _ => throw new InvalidOperationException("TypeEx 变体未穷尽（内部不变量）"),
    };

    private static string StrLit(string s) => JsonSerializer.Serialize(s);

    private static string FloatText(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string ExprText(Expr e) => e switch
    {
        Expr.IntLit i => $"int {i.Value.ToString(CultureInfo.InvariantCulture)}",
        Expr.FloatLit f => $"float {FloatText(f.Value)}",
        Expr.StrLit s => $"str {StrLit(s.Value)}",
        Expr.BoolLit b => $"bool {(b.Value ? "true" : "false")}",
        Expr.Ident id => $"ident {id.Name}",
        Expr.Unary u => $"({u.Op.ToString().ToLowerInvariant()} {ExprText(u.Operand)})",
        Expr.Binary b => $"({b.Op} {ExprText(b.Lhs)} {ExprText(b.Rhs)})",
        Expr.Range r => $"(range {ExprText(r.Lo)} {ExprText(r.Hi)})",
        Expr.Call c => $"(call {ExprText(c.Callee)}"
            + string.Join("", c.Args.Select(a => " " + ExprText(a))) + ")",
        Expr.Index ix => $"(index {ExprText(ix.Obj)} {ExprText(ix.Idx)})",
        Expr.Slice sl => $"(slice {ExprText(sl.Obj)} {ExprText(sl.Lo)} {ExprText(sl.Hi)})",
        Expr.Field fl => $"(field {ExprText(fl.Obj)} {StrLit(fl.Name)})",
        Expr.Try t => $"(try {ExprText(t.Operand)})",
        Expr.StructLit lit => $"(struct {StrLit(lit.TypeName)}"
            + string.Join("", lit.Fields.Select(f => $" {f.Name} {ExprText(f.Value)}")) + ")",
        Expr.MapLit m => "(map" + string.Join("", m.Items.Select(i => $" ({ExprText(i.Key)} {ExprText(i.Value)})"))
            + ")",
        Expr.ArrLit a => "(array" + string.Join("", a.Items.Select(ExprText).Select(x => " " + x)) + ")",
        Expr.Match m => $"(match {ExprText(m.Scrutinee)}"
            + string.Join("", m.Arms.Select(a => $" ({PatText(a.Pat)} {ExprText(a.Body)})")) + ")",
        Expr.FnLit fn => $"(fn({string.Join(", ", fn.Params.Select(ParamText))})"
            + string.Join("", fn.Body.Select(s => " {" + StmtInline(s) + "}")) + ")",
        _ => throw new InvalidOperationException("Expr 变体未穷尽（内部不变量）"),
    };

    private static string PatText(Pat p) => p switch
    {
        Pat.Wild => "_",
        Pat.Bind b => b.Name,
        Pat.Lit l => ExprText(l.Value),
        Pat.Case c => $"({c.Name}" + string.Join("", c.Subs.Select(s => " " + PatText(s))) + ")",
        _ => throw new InvalidOperationException("Pat 变体未穷尽（内部不变量）"),
    };

    /// <summary>FmLit 体内语句的内联形（调试可读优先，非跨版本契约）。</summary>
    private static string StmtInline(Stmt s) => s switch
    {
        Stmt.Let let => $"let {let.Name} = {ExprText(let.Init)}",
        Stmt.ExprStmt es => ExprText(es.Expr),
        Stmt.Return { Value: null } => "return",
        Stmt.Return { Value: { } v } => $"return {ExprText(v)}",
        _ => "<stmt>",
    };
}
