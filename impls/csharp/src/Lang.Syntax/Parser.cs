using System.Globalization;
using Lang.Core;

namespace Lang.Syntax;

/// <summary>解析结果：成功带语句序列，失败带 E1001 诊断（首错即停，无恢复）。</summary>
public sealed record ParseResult(IReadOnlyList<Stmt> Program, Diagnostic? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// 语法器（specs/grammar.md PARSE-001..006）：recursive descent + Pratt。
/// 错误消息与 Go 参考实现逐字一致（经 rust/TS parser 证据复刻），一律 E1001 诊断返回，
/// 禁止 throw 表达用户源码错误；内部以「毒化」状态实现首错即停（等价 Rust Result 传播）。
/// PARSE-006 由 NoStruct 计数器落实：控制头位置禁止裸 struct 字面量，加括号即恢复。
/// </summary>
public static class Parser
{
    /// <summary>解析整个源文件 / REPL 片段（词法 + 语法）。</summary>
    public static ParseResult Parse(SourceText text, SourceId source)
    {
        var lex = Lexer.Lex(text, source);
        if (lex.Error is not null)
            return new ParseResult([], lex.Error);
        return ParseTokens(lex.Tokens, source);
    }

    /// <summary>
    /// 解析已切好的记号流（供上层复用词法结果）。
    /// 契约：toks 末尾应有 lex() 追加的 EOF 记号；空流按空文件处理（补一个合成 EOF，
    /// 保证游标不变量，绝不 throw）。
    /// </summary>
    public static ParseResult ParseTokens(IReadOnlyList<Token> toks, SourceId source)
    {
        if (toks.Count == 0)
        {
            var at = new Position(0, 1, 1);
            toks = [new Token(TokenKind.Eof, "", new Span(source, at, at))];
        }
        var p = new Impl(toks, source);
        return p.ParseProgram();
    }

    private sealed class Impl
    {
        private readonly IReadOnlyList<Token> _toks;
        private readonly SourceId _source;
        private int _i;
        /// <summary>末次消费记号的 span 终点（SpanOf 用；SkipNl 不更新）。</summary>
        private Span _prevEnd;
        /// <summary>&gt;0 时处于控制头位置，禁止裸 struct 字面量（PARSE-006）。</summary>
        private int _noStruct;
        private Diagnostic? _error;

        internal Impl(IReadOnlyList<Token> toks, SourceId source)
        {
            _toks = toks;
            _source = source;
            _prevEnd = Span.Point(source, new Position(0, 1, 1));
        }

        private bool Failed => _error is not null;

        private Token Cur => _toks[_i];

        /// <summary>消费当前记号；停在 EOF 不再前进（与 TS next 一致）。</summary>
        private Token Next()
        {
            var t = _toks[_i];
            _prevEnd = t.Span;
            if (_i < _toks.Count - 1) _i++;
            return t;
        }

        private bool At(TokenKind k) => _toks[_i].Kind == k;

        private void SkipNl()
        {
            while (At(TokenKind.Newline)) _i++;
        }

        private bool Accept(TokenKind k)
        {
            if (!At(k)) return false;
            Next();
            return true;
        }

        /// <summary>`起点记号 → 末次消费记号` 的 span。</summary>
        private Span SpanOf(Token start) => new(_source, start.Span.Start, _prevEnd.End);

        /// <summary>从某起点 Position 到末次消费记号的 span（Arm/EnumCase/TypeEx 等用）。</summary>
        private Span SpanFrom(Span start) => new(_source, start.Start, _prevEnd.End);

        private Diagnostic ErrAt(Token t, string msg) =>
            Diagnostic.New(DiagnosticCode.E1001, msg, Diagnostic.AtLine(_source, t.Span.Start.Line));

        /// <summary>记错误并返回 false（首错锁定：后续错误不覆盖，等价首错即停）。</summary>
        private bool Fail(Token t, string msg)
        {
            _error ??= ErrAt(t, msg);
            return false;
        }

        private Token? Expect(TokenKind k)
        {
            if (At(k)) return Next();
            var t = _toks[_i];
            Fail(t, $"期望 {TokenKinds.KindName(k)}，得到 {TokenKinds.KindName(t.Kind)}");
            return null;
        }

        private string? ExpectName()
        {
            if (At(TokenKind.Ident)) return Next().Lit;
            if (At(TokenKind.Underscore))
            {
                Next();
                return "_";
            }
            var t = _toks[_i];
            Fail(t, $"期望标识符，得到 {TokenKinds.KindName(t.Kind)}");
            return null;
        }

        internal ParseResult ParseProgram()
        {
            var prog = new List<Stmt>();
            while (true)
            {
                SkipNl();
                if (At(TokenKind.Eof))
                    return new ParseResult(prog, _error);
                var s = ParseTop();
                if (Failed)
                    return new ParseResult([], _error);
                prog.Add(s!);
            }
        }

        // —— 顶层 ——

        private Stmt? ParseTop()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Chan:
                    Fail(t, "保留字 chan 尚未启用（规划于 M3+）");
                    return null;
                case TokenKind.Interface:
                    Fail(t, "保留字 interface 尚未启用（规划于 M3+）");
                    return null;
                case TokenKind.Import:
                    {
                        Next();
                        var path = Expect(TokenKind.Str);
                        if (path is null) return null;
                        return new Stmt.Import(SpanOf(t), path.Lit);
                    }
                case TokenKind.Extern:
                    return ParseExtern();
                default:
                    return ParseStmt();
            }
        }

        /// <summary>`extern "host" { fn 签名（无函数体）... }`。</summary>
        private Stmt? ParseExtern()
        {
            var t = Next(); // extern
            if (!At(TokenKind.Str))
            {
                Fail(t, "extern 后须是宿主名字符串，如 extern \"go\"");
                return null;
            }
            string host = Next().Lit;
            if (Expect(TokenKind.LBrace) is null) return null;
            var fns = new List<Stmt>();
            while (true)
            {
                SkipNl();
                if (Accept(TokenKind.RBrace))
                    return new Stmt.Extern(SpanOf(t), host, fns);
                if (At(TokenKind.Eof))
                {
                    Fail(Cur, "extern 块未闭合");
                    return null;
                }
                var fnTok = Expect(TokenKind.Fn);
                if (fnTok is null) return null;
                var name = ExpectName();
                if (name is null) return null;
                var tps = ParseTypeParams();
                if (Failed) return null;
                var ps = ParseParams();
                if (Failed) return null;
                TypeEx? ret = null;
                if (Accept(TokenKind.Colon))
                {
                    ret = ParseType();
                    if (Failed) return null;
                }
                fns.Add(new Stmt.FnDecl(SpanOf(fnTok), name!, null, tps!, ps!, ret, []));
            }
        }

        // —— 语句 ——

        private Stmt? ParseStmt()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Let or TokenKind.Var:
                    {
                        Next();
                        var name = ExpectName();
                        if (name is null) return null;
                        TypeEx? typ = null;
                        if (Accept(TokenKind.Colon))
                        {
                            typ = ParseType();
                            if (Failed) return null;
                        }
                        if (Expect(TokenKind.Eq) is null) return null;
                        var init = ParseExpr(0);
                        if (Failed) return null;
                        return new Stmt.Let(SpanOf(t), name!, t.Kind == TokenKind.Var, typ, init!);
                    }

                case TokenKind.Return:
                    {
                        Next();
                        Expr? value = null;
                        if (!At(TokenKind.Newline) && !At(TokenKind.RBrace) && !At(TokenKind.Eof))
                        {
                            value = ParseExpr(0);
                            if (Failed) return null;
                        }
                        return new Stmt.Return(SpanOf(t), value);
                    }

                case TokenKind.If:
                    return ParseIf();

                case TokenKind.While:
                    {
                        Next();
                        _noStruct++;
                        var cond = ParseExpr(0);
                        _noStruct--;
                        if (Failed) return null;
                        var body = ParseBlock();
                        if (Failed) return null;
                        return new Stmt.While(SpanOf(t), cond!, body!);
                    }

                case TokenKind.For:
                    {
                        Next();
                        var variable = ExpectName();
                        if (variable is null) return null;
                        if (Expect(TokenKind.In) is null) return null;
                        _noStruct++;
                        var iter = ParseExpr(0);
                        _noStruct--;
                        if (Failed) return null;
                        var body = ParseBlock();
                        if (Failed) return null;
                        return new Stmt.ForIn(SpanOf(t), variable!, iter!, body!);
                    }

                case TokenKind.Go:
                    {
                        Next();
                        var call = ParseExpr(0);
                        if (Failed) return null;
                        if (call is not Expr.Call)
                        {
                            Fail(t, "go 的目标须是函数调用");
                            return null;
                        }
                        return new Stmt.Go(SpanOf(t), call);
                    }

                case TokenKind.Break:
                    Next();
                    return new Stmt.Break(SpanOf(t));

                case TokenKind.Continue:
                    Next();
                    return new Stmt.Continue(SpanOf(t));

                case TokenKind.Fn:
                    return ParseFnDecl();

                case TokenKind.Struct:
                    return ParseStruct();

                case TokenKind.Enum:
                    return ParseEnum();

                default:
                    {
                        var e = ParseExpr(0);
                        if (Failed) return null;
                        if (Accept(TokenKind.Eq))
                        {
                            CheckTarget(e!);
                            if (Failed) return null;
                            var v = ParseExpr(0);
                            if (Failed) return null;
                            return new Stmt.Assign(SpanOf(t), e!, AssignOp.Assign, v!);
                        }
                        AssignOp? compound = Cur.Kind switch
                        {
                            TokenKind.PlusEq => AssignOp.AddAssign,
                            TokenKind.MinusEq => AssignOp.SubAssign,
                            TokenKind.StarEq => AssignOp.MulAssign,
                            TokenKind.SlashEq => AssignOp.DivAssign,
                            _ => (AssignOp?)null,
                        };
                        if (compound is { } op)
                        {
                            Next();
                            CheckTarget(e!);
                            if (Failed) return null;
                            var v = ParseExpr(0);
                            if (Failed) return null;
                            return new Stmt.Assign(SpanOf(t), e!, op, v!);
                        }
                        return new Stmt.ExprStmt(SpanOf(t), e!);
                    }
            }
        }

        /// <summary>`if`（含 else if 展开；span 末次重算覆盖 else 分支，与 TS 两次 spanOf 等价）。</summary>
        private Stmt? ParseIf()
        {
            var t = Next(); // if
            _noStruct++;
            var cond = ParseExpr(0);
            _noStruct--;
            if (Failed) return null;
            var thenBody = ParseBlock();
            if (Failed) return null;
            IReadOnlyList<Stmt>? elseBody = null;
            SkipNl();
            if (Accept(TokenKind.Else))
            {
                if (At(TokenKind.If))
                {
                    var nested = ParseIf();
                    if (Failed) return null;
                    elseBody = [nested!];
                }
                else
                {
                    elseBody = ParseBlock();
                    if (Failed) return null;
                }
            }
            return new Stmt.If(SpanOf(t), cond!, thenBody!, elseBody);
        }

        private IReadOnlyList<Stmt>? ParseBlock()
        {
            if (Expect(TokenKind.LBrace) is null) return null;
            var stmts = new List<Stmt>();
            while (true)
            {
                SkipNl();
                if (Accept(TokenKind.RBrace)) return stmts;
                if (At(TokenKind.Eof))
                {
                    Fail(Cur, "块未闭合，缺少 }");
                    return null;
                }
                var s = ParseStmt();
                if (Failed) return null;
                stmts.Add(s!);
            }
        }

        // —— 声明 ——

        private Stmt? ParseFnDecl()
        {
            var t = Next(); // fn
            Param? recv = null;
            if (Accept(TokenKind.LParen))
            {
                var namePos = Cur.Span;
                var rname = ExpectName();
                if (rname is null) return null;
                if (At(TokenKind.And))
                {
                    Fail(Cur, "v0 暂不支持指针接收者");
                    return null;
                }
                var rtyp = ParseType();
                if (Failed) return null;
                if (Expect(TokenKind.RParen) is null) return null;
                if (Expect(TokenKind.Dot) is null) return null;
                recv = new Param(SpanFrom(namePos), rname!, rtyp);
            }
            var name = ExpectName();
            if (name is null) return null;
            var parts = ParseFuncParts();
            if (Failed) return null;
            var (tps, ps, ret, body) = parts!.Value;
            return new Stmt.FnDecl(SpanOf(t), name!, recv, tps, ps, ret, body);
        }

        /// <summary>`fn` 关键字之后的公共部分：类型参数、参数表、返回注解、函数体。</summary>
        private (IReadOnlyList<string> TypeParams, IReadOnlyList<Param> Params, TypeEx? Ret,
            IReadOnlyList<Stmt> Body)? ParseFuncParts()
        {
            var tps = ParseTypeParams();
            if (Failed) return null;
            var ps = ParseParams();
            if (Failed) return null;
            TypeEx? ret = null;
            if (Accept(TokenKind.Colon))
            {
                ret = ParseType();
                if (Failed) return null;
            }
            var body = ParseBlock();
            if (Failed) return null;
            return (tps!, ps!, ret, body!);
        }

        private IReadOnlyList<string>? ParseTypeParams()
        {
            if (!Accept(TokenKind.LBracket)) return [];
            var tps = new List<string>();
            while (true)
            {
                var n = ExpectName();
                if (n is null) return null;
                tps.Add(n!);
                if (!Accept(TokenKind.Comma)) break;
            }
            if (Expect(TokenKind.RBracket) is null) return null;
            return tps;
        }

        private IReadOnlyList<Param>? ParseParams()
        {
            if (Expect(TokenKind.LParen) is null) return null;
            var ps = new List<Param>();
            while (!At(TokenKind.RParen))
            {
                var namePos = Cur.Span;
                var name = ExpectName();
                if (name is null) return null;
                TypeEx? typ = null;
                if (Accept(TokenKind.Colon))
                {
                    typ = ParseType();
                    if (Failed) return null;
                }
                ps.Add(new Param(SpanFrom(namePos), name!, typ));
                if (!Accept(TokenKind.Comma)) break;
            }
            if (Expect(TokenKind.RParen) is null) return null;
            return ps;
        }

        private Stmt? ParseStruct()
        {
            var t = Next(); // struct
            var name = ExpectName();
            if (name is null) return null;
            var tps = ParseTypeParams();
            if (Failed) return null;
            if (Expect(TokenKind.LBrace) is null) return null;
            var fields = new List<Param>();
            while (true)
            {
                SkipNl();
                if (Accept(TokenKind.RBrace)) break;
                if (At(TokenKind.Eof))
                {
                    Fail(Cur, "struct 未闭合");
                    return null;
                }
                var namePos = Cur.Span;
                var fname = ExpectName();
                if (fname is null) return null;
                if (Expect(TokenKind.Colon) is null) return null;
                var typ = ParseType();
                if (Failed) return null;
                fields.Add(new Param(SpanFrom(namePos), fname!, typ));
                SkipNl();
                Accept(TokenKind.Comma);
            }
            return new Stmt.StructDecl(SpanOf(t), name!, tps!, fields);
        }

        private Stmt? ParseEnum()
        {
            var t = Next(); // enum
            var name = ExpectName();
            if (name is null) return null;
            var tps = ParseTypeParams();
            if (Failed) return null;
            if (Expect(TokenKind.LBrace) is null) return null;
            var cases = new List<EnumCase>();
            while (true)
            {
                SkipNl();
                if (Accept(TokenKind.RBrace)) break;
                if (At(TokenKind.Eof))
                {
                    Fail(Cur, "enum 未闭合");
                    return null;
                }
                var casePos = Cur.Span;
                var cn = ExpectName();
                if (cn is null) return null;
                var payloads = new List<TypeEx>();
                if (Accept(TokenKind.LParen))
                {
                    while (!At(TokenKind.RParen))
                    {
                        var p = ParseType();
                        if (Failed) return null;
                        payloads.Add(p!);
                        if (!Accept(TokenKind.Comma)) break;
                    }
                    if (Expect(TokenKind.RParen) is null) return null;
                }
                cases.Add(new EnumCase(SpanFrom(casePos), cn!, payloads));
                SkipNl();
                Accept(TokenKind.Comma);
            }
            return new Stmt.EnumDecl(SpanOf(t), name!, tps!, cases);
        }

        // —— 类型注解 ——

        private TypeEx? ParseType()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Chan:
                    {
                        Next();
                        if (!Accept(TokenKind.LBracket))
                        {
                            Fail(t, "chan 需要 elem 类型参数，如 chan[int]");
                            return null;
                        }
                        var elem = ParseType();
                        if (Failed) return null;
                        if (Expect(TokenKind.RBracket) is null) return null;
                        return new TypeEx.Named(SpanOf(t), "chan", [elem!]);
                    }
                case TokenKind.And:
                    {
                        Next();
                        var elem = ParseType();
                        if (Failed) return null;
                        return new TypeEx.Ptr(SpanOf(t), elem!);
                    }
                case TokenKind.Fn:
                    {
                        Next();
                        if (Expect(TokenKind.LParen) is null) return null;
                        var ps = new List<TypeEx>();
                        while (!At(TokenKind.RParen))
                        {
                            var p = ParseType();
                            if (Failed) return null;
                            ps.Add(p!);
                            if (!Accept(TokenKind.Comma)) break;
                        }
                        if (Expect(TokenKind.RParen) is null) return null;
                        TypeEx? ret = null;
                        if (Accept(TokenKind.Colon))
                        {
                            ret = ParseType();
                            if (Failed) return null;
                        }
                        return new TypeEx.Fn(SpanOf(t), ps, ret);
                    }
                case TokenKind.LBracket:
                    {
                        Next();
                        var elem = ParseType();
                        if (Failed) return null;
                        if (Expect(TokenKind.RBracket) is null) return null;
                        return new TypeEx.Arr(SpanOf(t), elem!);
                    }
                case TokenKind.Ident:
                    {
                        Next();
                        if (t.Lit == "map" && At(TokenKind.LBracket))
                        {
                            Next();
                            var k = ParseType();
                            if (Failed) return null;
                            if (Expect(TokenKind.RBracket) is null) return null;
                            var v = ParseType();
                            if (Failed) return null;
                            return new TypeEx.Map(SpanOf(t), k!, v!);
                        }
                        var args = new List<TypeEx>();
                        if (Accept(TokenKind.LBracket))
                        {
                            while (true)
                            {
                                var a = ParseType();
                                if (Failed) return null;
                                args.Add(a!);
                                if (!Accept(TokenKind.Comma)) break;
                            }
                            if (Expect(TokenKind.RBracket) is null) return null;
                        }
                        return new TypeEx.Named(SpanOf(t), t.Lit, args);
                    }
                default:
                    Fail(t, $"期望类型，得到 {TokenKinds.KindName(t.Kind)}");
                    return null;
            }
        }

        // —— 表达式（Pratt，PARSE-004） ——

        /// <summary>Pratt 优先级（PARSE-004，低 → 高；0 = 非二元运算符）。</summary>
        private static byte PrecOf(TokenKind k) => k switch
        {
            TokenKind.OrOr => 1,
            TokenKind.AndAnd => 2,
            TokenKind.EqEq or TokenKind.Ne => 3,
            TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge => 4,
            TokenKind.DotDot => 5,
            TokenKind.Plus or TokenKind.Minus or TokenKind.Or or TokenKind.Xor => 6,
            TokenKind.Star or TokenKind.Slash or TokenKind.Percent
                or TokenKind.And or TokenKind.Shl or TokenKind.Shr => 7,
            _ => 0,
        };

        private static BinaryOp? BinaryOpOf(TokenKind k) => k switch
        {
            TokenKind.EqEq => BinaryOp.Eq,
            TokenKind.Ne => BinaryOp.Ne,
            TokenKind.Lt => BinaryOp.Lt,
            TokenKind.Le => BinaryOp.Le,
            TokenKind.Gt => BinaryOp.Gt,
            TokenKind.Ge => BinaryOp.Ge,
            TokenKind.Plus => BinaryOp.Add,
            TokenKind.Minus => BinaryOp.Sub,
            TokenKind.Star => BinaryOp.Mul,
            TokenKind.Slash => BinaryOp.Div,
            TokenKind.Percent => BinaryOp.Rem,
            TokenKind.And => BinaryOp.BitAnd,
            TokenKind.Or => BinaryOp.BitOr,
            TokenKind.Xor => BinaryOp.BitXor,
            TokenKind.Shl => BinaryOp.Shl,
            TokenKind.Shr => BinaryOp.Shr,
            TokenKind.AndAnd => BinaryOp.AndAnd,
            TokenKind.OrOr => BinaryOp.OrOr,
            _ => null,
        };

        private Expr? ParseExpr(byte minPrec)
        {
            var left = ParseUnary();
            if (Failed) return null;
            while (true)
            {
                var t = Cur;
                byte prec = PrecOf(t.Kind);
                if (prec == 0 || prec < minPrec) return left;
                Next();
                var right = ParseExpr((byte)(prec + 1));
                if (Failed) return null;
                var span = SpanOf(t);
                left = t.Kind == TokenKind.DotDot
                    ? new Expr.Range(span, left!, right!)
                    : BinaryOpOf(t.Kind) is { } op
                        ? new Expr.Binary(span, op, left!, right!)
                        : FailExpr(t); // 优先级表与二元运算符表不一致属内部错误，不静默
            }
        }

        private Expr FailExpr(Token t)
        {
            Fail(t, "内部错误：优先级表与二元运算符表不一致");
            return null!;
        }

        private Expr? ParseUnary()
        {
            var t = Cur;
            UnaryOp? op = t.Kind switch
            {
                TokenKind.Minus => UnaryOp.Neg,
                TokenKind.Not => UnaryOp.Not,
                TokenKind.And => UnaryOp.Ref,
                _ => (UnaryOp?)null,
            };
            if (op is null) return ParsePostfix();
            Next();
            var x = ParseUnary();
            if (Failed) return null;
            return new Expr.Unary(SpanOf(t), op.Value, x!);
        }

        private Expr? ParsePostfix()
        {
            var x = ParsePrimary();
            if (Failed) return null;
            while (true)
            {
                var t = Cur;
                switch (t.Kind)
                {
                    case TokenKind.LParen:
                        {
                            Next();
                            var args = new List<Expr>();
                            while (true)
                            {
                                SkipNl();
                                if (At(TokenKind.RParen)) break;
                                var a = ParseExpr(0);
                                if (Failed) return null;
                                args.Add(a!);
                                SkipNl();
                                if (!Accept(TokenKind.Comma)) break;
                            }
                            SkipNl();
                            if (Expect(TokenKind.RParen) is null) return null;
                            x = new Expr.Call(SpanOf(t), x!, args);
                            break;
                        }
                    case TokenKind.LBracket:
                        {
                            Next();
                            SkipNl();
                            var lo = ParseExpr(0);
                            if (Failed) return null;
                            if (Accept(TokenKind.Colon))
                            {
                                if (At(TokenKind.RBracket))
                                {
                                    Fail(Cur, "切片需要上界（v0 不支持开区间切片）");
                                    return null;
                                }
                                var hi = ParseExpr(0);
                                if (Failed) return null;
                                SkipNl();
                                if (Expect(TokenKind.RBracket) is null) return null;
                                x = new Expr.Slice(SpanOf(t), x!, lo!, hi!);
                            }
                            else
                            {
                                SkipNl();
                                if (Expect(TokenKind.RBracket) is null) return null;
                                x = new Expr.Index(SpanOf(t), x!, lo!);
                            }
                            break;
                        }
                    case TokenKind.Dot:
                        {
                            Next();
                            var name = Expect(TokenKind.Ident);
                            if (name is null) return null;
                            x = new Expr.Field(SpanOf(t), x!, name.Lit);
                            break;
                        }
                    case TokenKind.Question:
                        Next();
                        x = new Expr.Try(SpanOf(t), x!);
                        break;
                    default:
                        return x;
                }
            }
        }

        private Expr? ParsePrimary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Int:
                    {
                        Next();
                        // Go ParseInt(s, 10, 64)：语法非法与溢出统一报「整数溢出」
                        if (!TryParseIntLit(t.Lit, out var v))
                        {
                            Fail(t, $"整数溢出: {t.Lit}");
                            return null;
                        }
                        return new Expr.IntLit(SpanOf(t), v);
                    }
                case TokenKind.Float:
                    {
                        Next();
                        if (!IsPlainFloat(t.Lit))
                        {
                            Fail(t, $"非法浮点数: {t.Lit}");
                            return null;
                        }
                        if (!double.TryParse(t.Lit, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                        {
                            Fail(t, $"非法浮点数: {t.Lit}");
                            return null;
                        }
                        return new Expr.FloatLit(SpanOf(t), f);
                    }
                case TokenKind.Str:
                    {
                        Next();
                        return new Expr.StrLit(SpanOf(t), t.Lit);
                    }
                case TokenKind.True or TokenKind.False:
                    {
                        Next();
                        return new Expr.BoolLit(SpanOf(t), t.Kind == TokenKind.True);
                    }
                case TokenKind.Ident:
                    {
                        Next();
                        if (At(TokenKind.LBrace) && _noStruct == 0)
                            return ParseStructLit(t);
                        return new Expr.Ident(SpanOf(t), t.Lit);
                    }
                case TokenKind.LParen:
                    {
                        Next();
                        var saved = _noStruct; // 括号内恢复 struct 字面量
                        _noStruct = 0;
                        var x = ParseExpr(0);
                        _noStruct = saved;
                        if (Failed) return null;
                        SkipNl();
                        if (Expect(TokenKind.RParen) is null) return null;
                        return x;
                    }
                case TokenKind.LBracket:
                    {
                        Next();
                        var items = new List<Expr>();
                        while (true)
                        {
                            SkipNl();
                            if (At(TokenKind.RBracket)) break;
                            var item = ParseExpr(0);
                            if (Failed) return null;
                            items.Add(item!);
                            SkipNl();
                            if (!Accept(TokenKind.Comma)) break;
                        }
                        SkipNl();
                        if (Expect(TokenKind.RBracket) is null) return null;
                        return new Expr.ArrLit(SpanOf(t), items);
                    }
                case TokenKind.LBrace:
                    {
                        Next();
                        var items = new List<MapItem>();
                        while (true)
                        {
                            SkipNl();
                            if (Accept(TokenKind.RBrace)) break;
                            if (At(TokenKind.Eof))
                            {
                                Fail(Cur, "映射字面量未闭合");
                                return null;
                            }
                            var keyPos = Cur.Span;
                            var key = ParseExpr(0);
                            if (Failed) return null;
                            if (Expect(TokenKind.Colon) is null) return null;
                            var value = ParseExpr(0);
                            if (Failed) return null;
                            items.Add(new MapItem(SpanFrom(keyPos), key!, value!));
                            SkipNl();
                            Accept(TokenKind.Comma);
                        }
                        return new Expr.MapLit(SpanOf(t), items);
                    }
                case TokenKind.Fn:
                    {
                        Next();
                        var parts = ParseFuncParts();
                        if (Failed) return null;
                        var (tps, ps, ret, body) = parts!.Value;
                        return new Expr.FnLit(SpanOf(t), tps, ps, ret, body);
                    }
                case TokenKind.Match:
                    return ParseMatch();
                case TokenKind.Eof:
                    Fail(t, "表达式意外结束");
                    return null;
                default:
                    Fail(t, $"意外的记号 {TokenKinds.KindName(t.Kind)}");
                    return null;
            }
        }

        private Expr? ParseStructLit(Token startTok)
        {
            string name = startTok.Lit;
            Next(); // {
            var fields = new List<FieldLit>();
            while (true)
            {
                SkipNl();
                if (Accept(TokenKind.RBrace)) break;
                if (At(TokenKind.Eof))
                {
                    Fail(Cur, "struct 字面量未闭合");
                    return null;
                }
                var fname = Expect(TokenKind.Ident);
                if (fname is null) return null;
                if (Expect(TokenKind.Colon) is null) return null;
                var value = ParseExpr(0);
                if (Failed) return null;
                fields.Add(new FieldLit(
                    new Span(_source, fname.Span.Start, _prevEnd.End), fname.Lit, value!));
                SkipNl();
                Accept(TokenKind.Comma);
            }
            return new Expr.StructLit(SpanOf(startTok), name, fields);
        }

        private Expr? ParseMatch()
        {
            var t = Next(); // match
            _noStruct++;
            var scrut = ParseExpr(0);
            _noStruct--;
            if (Failed) return null;
            if (Expect(TokenKind.LBrace) is null) return null;
            var arms = new List<Arm>();
            while (true)
            {
                SkipNl();
                if (Accept(TokenKind.RBrace)) break;
                if (At(TokenKind.Eof))
                {
                    Fail(Cur, "match 未闭合");
                    return null;
                }
                var armPos = Cur.Span;
                var pat = ParsePattern();
                if (Failed) return null;
                if (Expect(TokenKind.FatArrow) is null) return null;
                var body = ParseExpr(0);
                if (Failed) return null;
                arms.Add(new Arm(SpanFrom(armPos), pat!, body!));
                if (At(TokenKind.Newline) || At(TokenKind.RBrace)) continue;
                Fail(Cur, "match 分支之间需要换行");
                return null;
            }
            return new Expr.Match(SpanOf(t), scrut!, arms);
        }

        private Pat? ParsePattern()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.Underscore:
                    Next();
                    return new Pat.Wild(SpanOf(t));

                case TokenKind.Int or TokenKind.Float or TokenKind.Str
                    or TokenKind.True or TokenKind.False:
                    {
                        var e = ParsePrimary();
                        if (Failed) return null;
                        return new Pat.Lit(SpanOf(t), e!);
                    }

                case TokenKind.Ident:
                    {
                        Next();
                        if (Accept(TokenKind.LParen))
                        {
                            var subs = new List<Pat>();
                            while (!At(TokenKind.RParen))
                            {
                                var sub = ParsePattern();
                                if (Failed) return null;
                                subs.Add(sub!);
                                if (!Accept(TokenKind.Comma)) break;
                            }
                            if (Expect(TokenKind.RParen) is null) return null;
                            return new Pat.Case(SpanOf(t), t.Lit, subs);
                        }
                        // 大写开头按 case 引用，小写为绑定（PARSE-005）
                        return FirstByteIsUpper(t.Lit)
                            ? new Pat.Case(SpanOf(t), t.Lit, [])
                            : new Pat.Bind(SpanOf(t), t.Lit);
                    }

                default:
                    Fail(t, $"非法模式，得到 {TokenKinds.KindName(t.Kind)}");
                    return null;
            }
        }

        /// <summary>赋值目标只能是变量、字段链或索引。</summary>
        private void CheckTarget(Expr e)
        {
            switch (e)
            {
                case Expr.Ident:
                    return;
                case Expr.Field f:
                    CheckTarget(f.Obj);
                    return;
                case Expr.Index ix:
                    CheckTarget(ix.Obj);
                    return;
                default:
                    _error ??= Diagnostic.New(DiagnosticCode.E1001, "赋值目标不合法",
                        Diagnostic.AtLine(e.Span.Source, e.Span.Start.Line));
                    return;
            }
        }

        /// <summary>复刻 Go `unicode.IsUpper(rune(utf8 首字节))`：按 UTF-8 首字节判大写
        /// （TS parser firstByteIsUpper 同构；astral 首字节 0xF0..0xF4 均判 false）。</summary>
        private static bool FirstByteIsUpper(string lit)
        {
            if (lit.Length == 0) return false;
            int b = lit[0];
            if (b >= 0x80)
            {
                if (char.IsHighSurrogate(lit[0]))
                {
                    b = 0xF0; // astral → UTF-8 首字节 F0..F4，Latin-1 对应均非大写
                }
                else
                {
                    b = b < 0x800 ? 0xC0 | (b >> 6) : 0xE0 | (b >> 12);
                }
            }
            if (b is >= 0x41 and <= 0x5A) return true;
            return b >= 0x80 && char.IsUpper((char)b);
        }

        /// <summary>Go ParseInt 同源：只认 ASCII 数字（全角 Nd 等交由溢出统一口径拒绝）。</summary>
        private static bool TryParseIntLit(string lit, out long value)
        {
            value = 0;
            foreach (var ch in lit)
            {
                if (ch is < '0' or > '9') return false;
            }
            return long.TryParse(lit, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>浮点字面量形状校验：`[0-9]+\.[0-9]+`（TS parser 正则同构）。</summary>
        private static bool IsPlainFloat(string lit)
        {
            int dot = lit.IndexOf('.');
            if (dot < 0) return false;
            string ip = lit[..dot];
            string fp = lit[(dot + 1)..];
            return ip.Length > 0 && fp.Length > 0
                && ip.All(c => c is >= '0' and <= '9')
                && fp.All(c => c is >= '0' and <= '9');
        }
    }
}
