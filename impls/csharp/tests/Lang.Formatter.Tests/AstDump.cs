using Lang.Syntax;

namespace Lang.Formatter.Tests;

/// <summary>
/// span 无关的 AST 结构 dump：语义等价断言（format 前后重解析的 AST 形态相等）用。
/// 输出规范 S 表达式，只含语义字段（所有 Span 一律不进入），字符串/名字经转义防歧义。
/// 期望口径独立于 formatter 渲染（不是"拿实现当期望"：它只复述 AST，不由输出文本构成）。
/// </summary>
internal static class AstDump
{
    internal static string Program(IReadOnlyList<Stmt> stmts) =>
        "[" + string.Join("; ", stmts.Select(DStmt)) + "]";

    internal static string DStmt(Stmt s) => s switch
    {
        Stmt.Let x =>
            $"let{(x.Mutable ? "!var" : "")} {Quote(x.Name)}{(x.Typ is { } t ? ":" + DType(t) : "")}={DExpr(x.Init)}",
        Stmt.Assign x => $"assign {DExpr(x.Target)} {x.Op} {DExpr(x.Value)}",
        Stmt.ExprStmt x => $"expr! {DExpr(x.Expr)}",
        Stmt.Return x => x.Value is { } v ? $"return {DExpr(v)}" : "return",
        Stmt.If x =>
            $"if {DExpr(x.Cond)}{{{Program(x.ThenBody)}}}|else{{{(x.ElseBody is { } e ? Program(e) : "-")}}}",
        Stmt.While x => $"while {DExpr(x.Cond)}{{{Program(x.Body)}}}",
        Stmt.ForIn x => $"for {Quote(x.Variable)} in {DExpr(x.Iter)}{{{Program(x.Body)}}}",
        Stmt.Break => "break",
        Stmt.Continue => "continue",
        Stmt.FnDecl x =>
            $"fn {Quote(x.Name)} recv={(x.Recv is { } r ? Quote(r.Name) : "-")}"
            + $"<{string.Join(",", x.TypeParams)}>({string.Join(",", x.Params.Select(DParam))})"
            + $"{(x.Ret is { } rt ? ":" + DType(rt) : "")}{{{Program(x.Body)}}}",
        Stmt.StructDecl x =>
            $"struct {Quote(x.Name)}<{string.Join(",", x.TypeParams)}>"
            + $"{{{string.Join(",", x.Fields.Select(DParam))}}}",
        Stmt.EnumDecl x =>
            $"enum {Quote(x.Name)}<{string.Join(",", x.TypeParams)}>"
            + $"{{{string.Join(",", x.Cases.Select(c => $"{Quote(c.Name)}({string.Join(",", c.Payloads.Select(DType))})"))}}}",
        Stmt.Go x => $"go {DExpr(x.Call)}",
        Stmt.Import x => $"import {Quote(x.Path)}",
        Stmt.Extern x =>
            $"extern {Quote(x.Host)}{{{string.Join(",", x.Fns.Select(DStmt))}}}",
        _ => throw new InvalidOperationException($"未知 Stmt 变体: {s.GetType().Name}"),
    };

    private static string DParam(Param p) =>
        $"{Quote(p.Name)}{(p.Typ is { } t ? ":" + DType(t) : "?")}";

    internal static string DExpr(Expr e) => e switch
    {
        Expr.IntLit x => $"i:{x.Value}",
        Expr.FloatLit x => $"f:{x.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
        Expr.StrLit x => $"s:{Quote(x.Value)}",
        Expr.BoolLit x => $"b:{x.Value}",
        Expr.Ident x => $"id:{Quote(x.Name)}",
        Expr.Unary x => $"({x.Op} {DExpr(x.Operand)})",
        Expr.Binary x => $"({x.Op} {DExpr(x.Lhs)} {DExpr(x.Rhs)})",
        Expr.Range x => $"(.. {DExpr(x.Lo)} {DExpr(x.Hi)})",
        Expr.Call x => $"call {DExpr(x.Callee)}({string.Join(", ", x.Args.Select(DExpr))})",
        Expr.Index x => $"idx {DExpr(x.Obj)}[{DExpr(x.Idx)}]",
        Expr.Slice x => $"slice {DExpr(x.Obj)}[{DExpr(x.Lo)}:{DExpr(x.Hi)}]",
        Expr.Field x => $"fld {DExpr(x.Obj)}.{Quote(x.Name)}",
        Expr.Try x => $"try {DExpr(x.Operand)}",
        Expr.StructLit x =>
            $"slit {Quote(x.TypeName)}{{{string.Join(", ", x.Fields.Select(fl => $"{Quote(fl.Name)}: {DExpr(fl.Value)}"))}}}",
        Expr.MapLit x =>
            $"mlit{{{string.Join(", ", x.Items.Select(it => $"{DExpr(it.Key)}: {DExpr(it.Value)}"))}}}",
        Expr.ArrLit x => $"alit[{string.Join(", ", x.Items.Select(DExpr))}]",
        Expr.Match x =>
            $"match {DExpr(x.Scrutinee)}{{{string.Join(" | ", x.Arms.Select(a => $"{DPat(a.Pat)}=>{DExpr(a.Body)}"))}}}",
        Expr.FnLit x =>
            $"fnlit<{string.Join(",", x.TypeParams)}>({string.Join(",", x.Params.Select(DParam))})"
            + $"{(x.Ret is { } r ? ":" + DType(r) : "")}{{{Program(x.Body)}}}",
        _ => throw new InvalidOperationException($"未知 Expr 变体: {e.GetType().Name}"),
    };

    private static string DPat(Pat p) => p switch
    {
        Pat.Wild => "_",
        Pat.Bind x => $"bind:{Quote(x.Name)}",
        Pat.Lit x => $"lit({DExpr(x.Value)})",
        Pat.Case x => $"case:{Quote(x.Name)}({string.Join(",", x.Subs.Select(DPat))})",
        _ => throw new InvalidOperationException($"未知 Pat 变体: {p.GetType().Name}"),
    };

    internal static string DType(TypeEx t) => t switch
    {
        TypeEx.Named x =>
            x.Args.Count == 0 ? Quote(x.Name) : $"{Quote(x.Name)}[{string.Join(",", x.Args.Select(DType))}]",
        TypeEx.Arr x => $"[{DType(x.Elem)}]",
        TypeEx.Map x => $"map[{DType(x.Key)}]{DType(x.Val)}",
        TypeEx.Ptr x => $"&{DType(x.Elem)}",
        TypeEx.Fn x =>
            $"fn({string.Join(",", x.Params.Select(DType))}){(x.Ret is { } r ? ":" + DType(r) : "")}",
        _ => throw new InvalidOperationException($"未知 TypeEx 变体: {t.GetType().Name}"),
    };

    /// <summary>名字 / 字符串进 dump 前转义（防串接歧义），astral 码点原样保留。</summary>
    private static string Quote(string s)
    {
        var b = new System.Text.StringBuilder(s.Length + 2);
        b.Append('\'');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\'': b.Append("\\'"); break;
                case '\\': b.Append("\\\\"); break;
                case '{': b.Append("\\{"); break;
                case '}': b.Append("\\}"); break;
                case '[': b.Append("\\["); break;
                case ']': b.Append("\\]"); break;
                case '(': b.Append("\\("); break;
                case ')': b.Append("\\)"); break;
                case ':': b.Append("\\:"); break;
                case ',': b.Append("\\,"); break;
                case '|': b.Append("\\|"); break;
                case ';': b.Append("\\;"); break;
                default: b.Append(ch); break;
            }
        }
        b.Append('\'');
        return b.ToString();
    }
}
