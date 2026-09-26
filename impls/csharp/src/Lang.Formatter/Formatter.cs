using System.Globalization;
using Lang.Core;
using Lang.Syntax;

namespace Lang.Formatter;

/// <summary>
/// langGo 规范化打印器（母本计划 Phase R23，对照 impls/rust/crates/lang-formatter 与
/// impls/typescript/src/format/format.ts 证据）。
///
/// 输入 = 已解析的 AST 语句序列 + 无损 trivia（TriviaScanner）+ 源文本；输出 = 规范形态源码。
/// 硬契约（测试锁定）：`format(format(src)) == format(src)` 幂等、注释 100% 保留、
/// 确定性输出、语义等价（span 无关的 AST 形态不变）。
///
/// 渲染为行缓冲；注释按 trivia 流插回：
/// - 行首注释：独立行输出，保留注释间空行分组（最多一空行）；
/// - 行尾注释：追加到语句渲染输出的最后一行（两空格间隔）；
/// - 表达式内注释：语句边界冲刷（位置后移，文本零丢失）；
/// - 块尾注释：块内最后冲刷，原位保留在 `}` 之前。
///
/// 与 TS 证据的偏差（均在 docs/lang-cs/status/A1-syntax.md 登记）：
/// 1. 批内首条注释前不补空行——证据实现跨 `}` 的 lastSrcLine 跳变会在
///    「空块 + 块尾注释」场景破坏幂等，此修正使空行分组决策上下文无关；
/// 2. 函数字面量签名保留 type_params（`fn[T](x: T)`）——证据实现丢弃它导致
///    重解析 AST 不等。
/// </summary>
public static class Formatter
{
    /// <summary>缩进单位：4 空格（对照 TS 证据 "    ".repeat(indent)）。</summary>
    private const string IndentUnit = "    ";

    /// <summary>便捷入口的合成源名（仅用于 SourceText 构造，不影响输出）。</summary>
    private const string SourceName = "format.lang-go";

    /// <summary>
    /// 把源码格式化为规范形态（解析 + trivia 扫描 + 规范打印）。
    /// 解析失败以 E1001 诊断返回（不 throw 表达用户源码错误）；formatter 自身不产生诊断。
    /// </summary>
    public static FormatResult FormatSource(string src)
    {
        var text = new SourceText(SourceName, src);
        var parsed = Parser.Parse(text, new SourceId(0));
        if (parsed.Error is not null)
            return new FormatResult("", parsed.Error);
        var trivia = TriviaScanner.Scan(text, new SourceId(0));
        return new FormatResult(Format(parsed.Program, trivia, text), null);
    }

    /// <summary>
    /// 把已解析的语句序列 + 无损 trivia 规范化为规范形态源码文本。
    /// <paramref name="source"/> 是码点 → 行号折算的权威：注释贴附所需的行号一律由
    /// span 的码点 offset 经 <see cref="SourceText.LineCol"/> 派生（trivia / AST span
    /// 的 offset 均为 0 基码点）。末尾恒补单换行；空程序输出空串。
    /// </summary>
    public static string Format(IReadOnlyList<Stmt> stmts, IReadOnlyList<Trivia> trivia, SourceText source)
    {
        // 仅 Comment 类 trivia，依出现序（Where 保序，无散列表迭代序）
        var comments = trivia.Where(t => t.Kind == TriviaKind.Comment).ToArray();
        var f = new Printer(source, comments, 0);
        f.Stmts(stmts, 0);
        return f.Out();
    }

    /// <summary>格式化器状态：输出行缓冲 + 共享注释流游标。</summary>
    private sealed class Printer(SourceText text, Trivia[] comments, int pos)
    {
        private readonly List<string> _lines = [];
        private readonly SourceText _text = text;
        private readonly Trivia[] _comments = comments;
        private int _pos = pos;
        /// <summary>最近消费的源码行（空行分组判定；1 基，0 = 尚无）。</summary>
        private uint _lastSrcLine;

        /// <summary>码点偏移 → 1 基行号（权威折算走 SourceText）。</summary>
        private uint LineOf(uint offset) => (uint)_text.LineCol((int)offset).Line;

        /// <summary>无符号饱和减（对齐 rust saturating_sub）：行号倒挂时按 0 处理，不补空行。</summary>
        private static uint SatSub(uint a, uint b) => a > b ? a - b : 0;

        /// <summary>冲刷 start 之前的整行注释（行尾注释留在流里给 FlushTrailing）。</summary>
        private void FlushLeading(uint start, int indent)
        {
            while (_pos < _comments.Length && _comments[_pos].Span.Start.Offset < start)
            {
                var c = _comments[_pos++];
                uint line = LineOf(c.Span.Start.Offset);
                if (SatSub(line, _lastSrcLine) >= 2) _lines.Add("");
                _lines.Add(Indent(indent) + c.Text);
                _lastSrcLine = line;
            }
        }

        /// <summary>冲刷语句末行上的行尾注释（追加到最近输出行，两空格间隔）。</summary>
        private void FlushTrailing(uint endOffset, uint endLine)
        {
            while (_pos < _comments.Length)
            {
                var c = _comments[_pos];
                uint line = LineOf(c.Span.Start.Offset);
                if (line != endLine || c.Span.Start.Offset < endOffset) return;
                _pos++;
                if (_lines.Count == 0) _lines.Add("");
                _lines[^1] += "  " + c.Text;
                _lastSrcLine = line;
            }
        }

        /// <summary>
        /// 冲刷全部挂起注释（块尾 / 文件尾 / 函数字面量体尾），整行输出。
        /// 幂等修正：批内第一条注释前不补空行。证据实现用 lastSrcLine（可能指向
        /// `}` 之后）给首条注释做空行分组，注释被收进块内后块尾行号反转，空块场景
        /// 二次格式化会移位；首条不分组使该决策上下文无关，批内相邻注释的分组保留。
        /// </summary>
        private void FlushRemaining(int indent)
        {
            bool first = true;
            while (_pos < _comments.Length)
            {
                var c = _comments[_pos++];
                uint line = LineOf(c.Span.Start.Offset);
                if (!first && SatSub(line, _lastSrcLine) >= 2) _lines.Add("");
                first = false;
                _lines.Add(Indent(indent) + c.Text);
                _lastSrcLine = line;
            }
        }

        /// <summary>输出一行（多行文本仅首行带缩进，其余行已内嵌绝对缩进）。</summary>
        private void W(int indent, string s)
        {
            bool first = true;
            foreach (var part in s.Split('\n'))
            {
                _lines.Add(first ? Indent(indent) + part : part);
                first = false;
            }
        }

        internal string Out() => _lines.Count == 0 ? "" : string.Join("\n", _lines) + "\n";

        /// <summary>语句序列 + 注释贴附（每条语句边界做前导 / 行尾冲刷，末尾冲刷余量）。</summary>
        internal void Stmts(IReadOnlyList<Stmt> list, int indent)
        {
            foreach (var s in list)
            {
                var sp = s.Span;
                FlushLeading(sp.Start.Offset, indent);
                uint endLine = LineOf(sp.End.Offset);
                _lastSrcLine = endLine;
                EmitStmt(s, indent);
                FlushTrailing(sp.End.Offset, endLine);
            }
            FlushRemaining(indent);
        }

        private void EmitStmt(Stmt s, int indent)
        {
            switch (s)
            {
                case Stmt.Let st:
                    {
                        string kw = st.Mutable ? "var" : "let";
                        string anno = st.Typ is { } t ? ": " + TypeStr(t) : "";
                        string init = Expr(st.Init, 0, indent);
                        W(indent, $"{kw} {st.Name}{anno} = {init}");
                        break;
                    }
                case Stmt.Assign st:
                    {
                        string target = Expr(st.Target, 0, indent);
                        string value = Expr(st.Value, 0, indent);
                        W(indent, $"{target} {AssignOpStr(st.Op)} {value}");
                        break;
                    }
                case Stmt.ExprStmt st:
                    W(indent, Expr(st.Expr, 0, indent));
                    break;
                case Stmt.Return st:
                    W(indent, st.Value is { } v ? "return " + Expr(v, 0, indent) : "return");
                    break;
                case Stmt.If st:
                    IfChain(st.Cond, st.ThenBody, st.ElseBody, indent);
                    break;
                case Stmt.While st:
                    W(indent, $"while {Expr(st.Cond, 0, indent)} {{");
                    Stmts(st.Body, indent + 1);
                    W(indent, "}");
                    break;
                case Stmt.ForIn st:
                    W(indent, $"for {st.Variable} in {Expr(st.Iter, 0, indent)} {{");
                    Stmts(st.Body, indent + 1);
                    W(indent, "}");
                    break;
                case Stmt.Break:
                    W(indent, "break");
                    break;
                case Stmt.Continue:
                    W(indent, "continue");
                    break;
                case Stmt.Go st:
                    W(indent, $"go {Expr(st.Call, 0, indent)}");
                    break;
                case Stmt.FnDecl st:
                    EmitFnDecl(st, indent);
                    break;
                case Stmt.StructDecl st:
                    {
                        W(indent, $"struct {st.Name}{GenericParams(st.TypeParams)} {{");
                        foreach (var fd in st.Fields)
                        {
                            // parser 恒填字段类型，缺省走证据的 "?" 占位
                            string anno = fd.Typ is { } t ? ": " + TypeStr(t) : ": ?";
                            W(indent + 1, fd.Name + anno);
                        }
                        W(indent, "}");
                        break;
                    }
                case Stmt.EnumDecl st:
                    {
                        W(indent, $"enum {st.Name}{GenericParams(st.TypeParams)} {{");
                        foreach (var c in st.Cases)
                        {
                            if (c.Payloads.Count == 0)
                                W(indent + 1, c.Name);
                            else
                                W(indent + 1, $"{c.Name}({string.Join(", ", c.Payloads.Select(TypeStr))})");
                        }
                        W(indent, "}");
                        break;
                    }
                case Stmt.Import st:
                    W(indent, $"import {GoQuote(st.Path)}");
                    break;
                case Stmt.Extern st:
                    {
                        W(indent, $"extern {GoQuote(st.Host)} {{");
                        foreach (var f in st.Fns)
                        {
                            // 契约：fns 每项恒 Stmt.FnDecl（body 空），只打印签名
                            if (f is Stmt.FnDecl fd)
                                W(indent + 1, FnSig(fd.Name, fd.TypeParams, fd.Params, fd.Ret));
                        }
                        W(indent, "}");
                        break;
                    }
                default:
                    throw new InvalidOperationException($"未知 Stmt 变体: {s.GetType().Name}");
            }
        }

        private void EmitFnDecl(Stmt.FnDecl st, int indent)
        {
            if (st.Recv is { } r)
            {
                // 接收者无冒号（Go 风格）；parser 恒填 recv.typ，缺省走证据的 "?" 占位
                string rtyp = r.Typ is { } t ? TypeStr(t) : "?";
                string ps = ParamsSig(st.Params);
                string sig = $"fn ({r.Name} {rtyp}).{st.Name}({ps})";
                if (st.Ret is { } ret)
                {
                    sig += ": ";
                    sig += TypeStr(ret);
                }
                W(indent, sig + " {");
            }
            else
            {
                W(indent, FnSig(st.Name, st.TypeParams, st.Params, st.Ret) + " {");
            }
            Stmts(st.Body, indent + 1);
            W(indent, "}");
        }

        /// <summary>if / else if / else 扁平化输出。</summary>
        private void IfChain(Expr cond, IReadOnlyList<Stmt> thenBody,
            IReadOnlyList<Stmt>? elseBody, int indent)
        {
            W(indent, $"if {Expr(cond, 0, indent)} {{");
            Stmts(thenBody, indent + 1);
            ElseTail(elseBody, indent);
        }

        private void ElseTail(IReadOnlyList<Stmt>? elseBody, int indent)
        {
            if (elseBody is { } els)
            {
                // else if 保持同起始行（链式展开）
                if (els is [Stmt.If nested])
                {
                    W(indent, $"}} else if {Expr(nested.Cond, 0, indent)} {{");
                    Stmts(nested.ThenBody, indent + 1);
                    ElseTail(nested.ElseBody, indent);
                    return;
                }
                if (els.Count > 0)
                {
                    W(indent, "} else {");
                    Stmts(els, indent + 1);
                }
            }
            W(indent, "}");
        }

        /// <summary>以 minPrec 门槛输出表达式；低于门槛的子式加括号。</summary>
        private string Expr(Expr e, byte minPrec, int indent)
        {
            string s = ExprRaw(e, indent);
            return ExprPrec(e) < minPrec ? $"({s})" : s;
        }

        private string ExprRaw(Expr e, int indent)
        {
            switch (e)
            {
                case Expr.IntLit x:
                    return x.Value.ToString(CultureInfo.InvariantCulture);
                case Expr.FloatLit x:
                    {
                        string s = GoFormatFloat(x.Value);
                        // 保住 float 身份：整数值的浮点字面量补 ".0"（否则重解析变 int）
                        return s.Contains('.') || s.Contains('e') || s.Contains('E') ? s : s + ".0";
                    }
                case Expr.StrLit x:
                    return GoQuote(x.Value);
                case Expr.BoolLit x:
                    return x.Value ? "true" : "false";
                case Expr.Ident x:
                    return x.Name;
                case Expr.Unary x:
                    return UnaryOpStr(x.Op) + Expr(x.Operand, 8, indent);
                case Expr.Binary x:
                    {
                        byte p = BinaryPrec(x.Op);
                        return $"{Expr(x.Lhs, p, indent)} {BinaryOpStr(x.Op)} {Expr(x.Rhs, (byte)(p + 1), indent)}";
                    }
                case Expr.Range x:
                    return $"{Expr(x.Lo, 5, indent)}..{Expr(x.Hi, 6, indent)}";
                case Expr.Call x:
                    return $"{Expr(x.Callee, 9, indent)}({string.Join(", ", x.Args.Select(a => Expr(a, 0, indent)))})";
                case Expr.Index x:
                    return $"{Expr(x.Obj, 9, indent)}[{Expr(x.Idx, 0, indent)}]";
                case Expr.Slice x:
                    return $"{Expr(x.Obj, 9, indent)}[{Expr(x.Lo, 0, indent)}:{Expr(x.Hi, 0, indent)}]";
                case Expr.Field x:
                    return $"{Expr(x.Obj, 9, indent)}.{x.Name}";
                case Expr.Try x:
                    return $"{Expr(x.Operand, 9, indent)}?";
                case Expr.StructLit x:
                    return $"{x.TypeName}{{{string.Join(", ", x.Fields.Select(fl => $"{fl.Name}: {Expr(fl.Value, 0, indent)}"))}}}";
                case Expr.MapLit x:
                    return $"{{{string.Join(", ", x.Items.Select(it => $"{Expr(it.Key, 0, indent)}: {Expr(it.Value, 0, indent)}"))}}}";
                case Expr.ArrLit x:
                    return $"[{string.Join(", ", x.Items.Select(el => Expr(el, 0, indent)))}]";
                case Expr.Match x:
                    {
                        var b = new System.Text.StringBuilder();
                        b.Append("match ").Append(Expr(x.Scrutinee, 0, indent)).Append(" {\n");
                        foreach (var arm in x.Arms)
                        {
                            b.Append(Indent(indent + 1));
                            b.Append(PatStr(arm.Pat));
                            b.Append(" => ");
                            b.Append(Expr(arm.Body, 0, indent + 1));
                            b.Append('\n');
                        }
                        b.Append(Indent(indent));
                        b.Append('}');
                        return b.ToString();
                    }
                case Expr.FnLit x:
                    {
                        // 语义保留偏差修正：fnlit 的 type_params 必须保留（fn[T](x: T)）
                        string sig = FnLitSig(x.TypeParams, x.Params, x.Ret);
                        var inner = new Printer(_text, _comments, _pos);
                        inner.W(0, sig + " {");
                        inner.Stmts(x.Body, indent + 1);
                        inner.W(indent, "}");
                        // 注释流游标归还外层（体尾注释已由 inner 的 FlushRemaining 冲刷）
                        _pos = inner._pos;
                        return string.Join("\n", inner._lines);
                    }
                default:
                    throw new InvalidOperationException($"未知 Expr 变体: {e.GetType().Name}");
            }
        }

        /// <summary>方法名避开类型名 Pat（方法组 Select(Pat) 会撞类型名）。</summary>
        private string PatStr(Pat p)
        {
            switch (p)
            {
                case Pat.Wild:
                    return "_";
                case Pat.Bind x:
                    return x.Name;
                case Pat.Lit x:
                    return Expr(x.Value, 0, 0);
                case Pat.Case x:
                    return x.Subs.Count == 0
                        ? x.Name
                        : $"{x.Name}({string.Join(", ", x.Subs.Select(PatStr))})";
                default:
                    throw new InvalidOperationException($"未知 Pat 变体: {p.GetType().Name}");
            }
        }
    }

    // —— 语法面 → 文本（纯函数） ——

    private static string Indent(int n) => string.Concat(Enumerable.Repeat(IndentUnit, n));

    private static string GenericParams(IReadOnlyList<string> tps) =>
        tps.Count == 0 ? "" : $"[{string.Join(", ", tps)}]";

    private static string ParamsSig(IReadOnlyList<Param> ps) => string.Join(", ", ps.Select(p =>
        p.Typ is { } t ? $"{p.Name}: {TypeStr(t)}" : p.Name));

    /// <summary>函数 / extern 签名（`fn name[tps](params): ret`）。</summary>
    private static string FnSig(string name, IReadOnlyList<string> typeParams,
        IReadOnlyList<Param> ps, TypeEx? ret)
    {
        string sig = $"fn {name}{GenericParams(typeParams)}({ParamsSig(ps)})";
        return AppendRet(sig, ret);
    }

    /// <summary>函数字面量签名：无名字、`fn` 后无空格（`fn[tps](params): ret`）。</summary>
    private static string FnLitSig(IReadOnlyList<string> typeParams,
        IReadOnlyList<Param> ps, TypeEx? ret)
    {
        string sig = $"fn{GenericParams(typeParams)}({ParamsSig(ps)})";
        return AppendRet(sig, ret);
    }

    private static string AppendRet(string sig, TypeEx? ret)
    {
        if (ret is not { } t) return sig;
        return sig + ": " + TypeStr(t);
    }

    private static string TypeStr(TypeEx t)
    {
        switch (t)
        {
            case TypeEx.Named x:
                return x.Args.Count == 0
                    ? x.Name
                    : $"{x.Name}[{string.Join(", ", x.Args.Select(TypeStr))}]";
            case TypeEx.Arr x:
                return $"[{TypeStr(x.Elem)}]";
            case TypeEx.Map x:
                return $"map[{TypeStr(x.Key)}]{TypeStr(x.Val)}";
            case TypeEx.Ptr x:
                return $"&{TypeStr(x.Elem)}";
            case TypeEx.Fn x:
                {
                    string sig = $"fn({string.Join(", ", x.Params.Select(TypeStr))})";
                    return AppendRet(sig, x.Ret);
                }
            default:
                throw new InvalidOperationException($"未知 TypeEx 变体: {t.GetType().Name}");
        }
    }

    private static string UnaryOpStr(UnaryOp op) => op switch
    {
        UnaryOp.Neg => "-",
        UnaryOp.Not => "!",
        UnaryOp.Ref => "&",
        _ => throw new InvalidOperationException($"未知 UnaryOp: {op}"),
    };

    private static string BinaryOpStr(BinaryOp op) => op switch
    {
        BinaryOp.Eq => "==",
        BinaryOp.Ne => "!=",
        BinaryOp.Lt => "<",
        BinaryOp.Le => "<=",
        BinaryOp.Gt => ">",
        BinaryOp.Ge => ">=",
        BinaryOp.Add => "+",
        BinaryOp.Sub => "-",
        BinaryOp.Mul => "*",
        BinaryOp.Div => "/",
        BinaryOp.Rem => "%",
        BinaryOp.BitAnd => "&",
        BinaryOp.BitOr => "|",
        BinaryOp.BitXor => "^",
        BinaryOp.Shl => "<<",
        BinaryOp.Shr => ">>",
        BinaryOp.AndAnd => "&&",
        BinaryOp.OrOr => "||",
        _ => throw new InvalidOperationException($"未知 BinaryOp: {op}"),
    };

    private static string AssignOpStr(AssignOp op) => op switch
    {
        AssignOp.Assign => "=",
        AssignOp.AddAssign => "+=",
        AssignOp.SubAssign => "-=",
        AssignOp.MulAssign => "*=",
        AssignOp.DivAssign => "/=",
        _ => throw new InvalidOperationException($"未知 AssignOp: {op}"),
    };

    /// <summary>二元运算符优先级（PARSE-004 同表；1 低 → 7 高）。</summary>
    private static byte BinaryPrec(BinaryOp op) => op switch
    {
        BinaryOp.OrOr => 1,
        BinaryOp.AndAnd => 2,
        BinaryOp.Eq or BinaryOp.Ne => 3,
        BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge => 4,
        BinaryOp.Add or BinaryOp.Sub or BinaryOp.BitOr or BinaryOp.BitXor => 6,
        BinaryOp.Mul or BinaryOp.Div or BinaryOp.Rem
            or BinaryOp.BitAnd or BinaryOp.Shl or BinaryOp.Shr => 7,
        _ => throw new InvalidOperationException($"未知 BinaryOp: {op}"),
    };

    /// <summary>节点优先级：二元取运算符级，一元 8，其余（字面量 / 后缀链 / 复合字面量）9。</summary>
    private static byte ExprPrec(Expr e) => e switch
    {
        Expr.Binary x => BinaryPrec(x.Op),
        Expr.Unary => 8,
        _ => 9,
    };

    /// <summary>
    /// langGo 转义规则输出字符串 / import 路径（仅 `\n \t \r \" \\` 五种转义，
    /// 其余码点原样保留；TS quoteStr 与 goQuoteStr 行为一致，合一实现）。
    /// </summary>
    internal static string GoQuote(string s)
    {
        var b = new System.Text.StringBuilder(s.Length + 2);
        b.Append('"');
        foreach (var r in s)
        {
            switch (r)
            {
                case '\n': b.Append("\\n"); break;
                case '\t': b.Append("\\t"); break;
                case '\r': b.Append("\\r"); break;
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                default: b.Append(r); break;
            }
        }
        b.Append('"');
        return b.ToString();
    }

    /// <summary>
    /// Go `strconv.FormatFloat(f, 'g', -1, 64)` 等价（最短往返数字，科学计数门槛
    /// eprec=6；指数至少两位、带符号）。NaN / ±Inf / ±0 与 Go 同文。
    /// .NET "R" 与 Rust `{:e}` 同为最短往返口径，数字串唯一，可直接归一。
    /// </summary>
    internal static string GoFormatFloat(double f)
    {
        if (double.IsNaN(f)) return "NaN";
        if (double.IsPositiveInfinity(f)) return "+Inf";
        if (double.IsNegativeInfinity(f)) return "-Inf";
        if (f == 0.0) return double.IsNegative(f) ? "-0" : "0";
        bool neg = f < 0.0;
        double a = Math.Abs(f);

        // 归一 "R" 输出为 (digits, exp)：value = d.ddd × 10^exp
        string r = a.ToString("R", CultureInfo.InvariantCulture);
        string digits;
        int exp;
        int ePos = r.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            digits = r[..ePos].Replace(".", "");
            exp = int.Parse(r[(ePos + 1)..], CultureInfo.InvariantCulture);
        }
        else
        {
            int dot = r.IndexOf('.');
            if (dot < 0)
            {
                digits = r;
                exp = r.Length - 1;
            }
            else
            {
                digits = r.Remove(dot, 1);
                exp = dot - 1;
            }
            int lead = 0;
            while (lead < digits.Length && digits[lead] == '0') lead++;
            digits = digits[lead..];
            exp -= lead;
        }

        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0"; // 不可达（非零值），防御性对齐 rust 口径
        int len = digits.Length;
        int dp = exp + 1; // 小数点位置（相对数字串起点）
        string body;
        // 科学计数门槛 eprec=6：exp < -4 或 exp >= 6（对照 Go strconv 'g' shortest）
        if (exp < -4 || exp >= 6)
        {
            string head = digits[..1];
            string tail = digits[1..];
            string frac = tail.Length == 0 ? "" : "." + tail;
            char sign = exp < 0 ? '-' : '+';
            body = $"{head}{frac}e{sign}{Math.Abs(exp):00}";
        }
        else if (dp <= 0)
        {
            body = "0." + new string('0', -dp) + digits;
        }
        else if (dp >= len)
        {
            body = digits + new string('0', dp - len);
        }
        else
        {
            body = digits[..dp] + "." + digits[dp..];
        }
        return neg ? "-" + body : body;
    }
}

/// <summary>格式化结果：成功带规范形态源码，失败带透传的 E1001 诊断（Output 为空串）。</summary>
public sealed record FormatResult(string Output, Diagnostic? Error)
{
    public bool Ok => Error is null;
}
