using Lang.Core;
using Lang.Syntax;

namespace Lang.Refactor;

/// <summary>
/// 声明名字 token 定位：在声明 span 的词元流内找名字 token 的精确区间。
/// 行为证据：impls/rust/crates/lang-refactor/src/locate.rs（与 TS 证据的
/// spanTokens/scanNameAfterFn 同构）。
///
/// <para>AST span 给搜索范围，词元流给精确 token 区间；<b>名字身份仍以 SymbolId 为准</b>，
/// 这里只做「声明里名字写在哪儿」的定位。码点度量全程（SourceText/token span）。</para>
/// </summary>
public static class Locate
{
    /// <summary>文件内某 span 区间的非换行词元（绝对码点 span，已按文件词元流过滤）。</summary>
    public static List<Token> TokensIn(RefactorFile file, Span span)
    {
        var toks = new List<Token>();
        foreach (var t in file.Tokens)
        {
            if (t.Span.Start.Offset >= span.Start.Offset && t.Span.End.Offset <= span.End.Offset
                && t.Kind is not (TokenKind.Eof or TokenKind.Newline))
            {
                toks.Add(t);
            }
        }
        return toks;
    }

    /// <summary>按符号在文件中的定位：文件词元流 + 符号声明 span → 名字 token span。</summary>
    public static Span? DeclNameSpan(RefactorInput input, Span targetSpan, bool hasRecv)
    {
        var file = input.FileOf(targetSpan);
        if (file is null)
        {
            return null;
        }
        var toks = TokensIn(file, targetSpan);
        var i = toks.FindIndex(t => t.Kind == TokenKind.Fn);
        if (i < 0)
        {
            return null;
        }
        i++;
        if (hasRecv)
        {
            // fn (r Recv).name：跳过平衡的接收者括号组，再越过 `.` 链
            while (i < toks.Count && toks[i].Kind != TokenKind.LParen)
            {
                i++;
            }
            if (i >= toks.Count)
            {
                return null;
            }
            var depth = 1;
            i++;
            while (i < toks.Count && depth > 0)
            {
                switch (toks[i].Kind)
                {
                    case TokenKind.LParen or TokenKind.LBracket:
                        depth++;
                        break;
                    case TokenKind.RParen or TokenKind.RBracket:
                        depth--;
                        break;
                }
                if (depth > 0)
                {
                    i++;
                }
            }
            i++; // 越过收括号
            while (i < toks.Count && toks[i].Kind == TokenKind.Dot)
            {
                i++;
            }
        }
        if (i >= toks.Count || toks[i].Kind != TokenKind.Ident)
        {
            return null;
        }
        return toks[i].Span;
    }

    /// <summary>Let 语句的绑定名 span（首个标识符词元）。</summary>
    public static Span? FirstIdentSpan(RefactorInput input, Span span) =>
        input.FileOf(span) is not { } file
            ? null
            : TokensIn(file, span).Where(t => t.Kind == TokenKind.Ident)
                .Select(t => (Span?)t.Span).FirstOrDefault();

    /// <summary>ForIn 语句的循环变量名 span（`for` 后的标识符）。</summary>
    public static Span? ForinVarSpan(RefactorInput input, Span span)
    {
        if (input.FileOf(span) is not { } file)
        {
            return null;
        }
        var toks = TokensIn(file, span);
        var i = toks.FindIndex(t => t.Kind == TokenKind.For) + 1;
        if (i <= 0 || i >= toks.Count || toks[i].Kind != TokenKind.Ident)
        {
            return null;
        }
        return toks[i].Span;
    }

    /// <summary>参数组的名字 token span：decl span 内第 seg 个参数段的首个标识符。
    ///
    /// 顶层括号组按源码序编号：接收者组在前（若有），形参组随后；`_` 段照计
    /// （调用方传入的 seg 已按含 `_` 的位序计算）。返回类型里的 fn(...) 注解
    /// 组位于形参组之后，不影响取组。</summary>
    public static Span? ParamIdentSpan(RefactorInput input, Span declSpan, bool hasRecv, int seg)
    {
        if (input.FileOf(declSpan) is not { } file)
        {
            return null;
        }
        var toks = TokensIn(file, declSpan);
        // 收集顶层括号组（depth 1 的括号对在词元流中的下标区间）
        var groups = new List<(int Lp, int Rp)>();
        var i = 0;
        while (i < toks.Count)
        {
            if (toks[i].Kind == TokenKind.LParen)
            {
                var depth = 1;
                var j = i + 1;
                while (j < toks.Count && depth > 0)
                {
                    switch (toks[j].Kind)
                    {
                        case TokenKind.LParen or TokenKind.LBracket:
                            depth++;
                            break;
                        case TokenKind.RParen or TokenKind.RBracket:
                            depth--;
                            break;
                    }
                    j++;
                }
                groups.Add((i, j - 1));
                i = j;
            }
            else
            {
                i++;
            }
        }
        var gi = hasRecv ? 1 : 0;
        if (gi >= groups.Count)
        {
            return null;
        }
        var (lp, rp) = groups[gi];
        return SegIdent(toks, lp, rp, seg);
    }

    /// <summary>在第 lp 个词元（LParen）起的括号组（止于 rp）内找第 seg 个逗号段的首个标识符。</summary>
    private static Span? SegIdent(List<Token> toks, int lp, int rp, int seg)
    {
        var depth = 1;
        var i = lp + 1;
        var segIdx = 0;
        var start = i;
        while (i <= rp)
        {
            switch (toks[i].Kind)
            {
                case TokenKind.LParen or TokenKind.LBracket:
                    depth++;
                    break;
                case TokenKind.RParen or TokenKind.RBracket:
                    depth--;
                    if (depth == 0)
                    {
                        goto done;
                    }
                    break;
                case TokenKind.Comma when depth == 1:
                    if (segIdx == seg)
                    {
                        return FirstIdent(toks, start, i);
                    }
                    segIdx++;
                    start = i + 1;
                    break;
            }
            i++;
        }
    done:
        if (segIdx == seg && start <= i)
        {
            return FirstIdent(toks, start, i);
        }
        return null;
    }

    private static Span? FirstIdent(List<Token> toks, int from, int to)
    {
        for (var i = from; i < to && i < toks.Count; i++)
        {
            if (toks[i].Kind == TokenKind.Ident)
            {
                return toks[i].Span;
            }
        }
        return null;
    }

    /// <summary>字段/方法访问的名字 token span：span 内最后一个标识符词元（obj.name）。</summary>
    public static Span? LastIdentSpan(RefactorInput input, Span span) =>
        input.FileOf(span) is not { } file
            ? null
            : TokensIn(file, span).Where(t => t.Kind == TokenKind.Ident)
                .Select(t => (Span?)t.Span).LastOrDefault();
}
