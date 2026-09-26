using System.Globalization;
using System.Text;
using Lang.Core;

namespace Lang.Syntax;

/// <summary>词法结果：成功带记号流（含末尾 EOF），失败带 E1001 诊断（首错即停）。</summary>
public sealed record LexResult(IReadOnlyList<Token> Tokens, Diagnostic? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// 词法器（specs/lexical-grammar.md LEX-001..008；对照 rust lang-syntax lexer.ts..rs 证据）。
/// 契约 §0：内部以 char index 扫描，Token.Span 一律 0 基码点、半开区间，
/// 经 <see cref="SourceText"/> 折算 offset 与行列——astral 码点（2 char）不得错位。
/// 词法错误 = E1001 诊断（message 逐字对照 Go 参考实现文本），禁止 throw；首错即停。
/// </summary>
public static class Lexer
{
    public static LexResult Lex(SourceText text, SourceId source)
    {
        var lx = new Impl(text, source);
        return lx.Run();
    }

    private sealed class Impl
    {
        private readonly string _s;
        private readonly SourceText _text;
        private readonly SourceId _source;
        private int _i;
        private uint _line = 1;
        private TokenKind? _last;
        private readonly List<Token> _toks = [];
        private Diagnostic? _error;

        internal Impl(SourceText text, SourceId source)
        {
            _text = text;
            _source = source;
            _s = text.Text;
        }

        internal LexResult Run()
        {
            while (_i < _s.Length && _error is null)
            {
                int start = _i;
                char c = _s[_i];
                switch (c)
                {
                    case ' ' or '\t' or '\r':
                        _i++;
                        break;
                    case '\n':
                        _i++;
                        _line++;
                        EmitNewline(start);
                        break;
                    case '/' when PeekAt(start, 1) == '/':
                        // 行注释（LEX-002）：token 流里不产记号，trivia 层保留
                        while (_i < _s.Length && _s[_i] != '\n') _i++;
                        break;
                    case '"':
                        StringLit(start);
                        break;
                    default:
                        if (IsNdAt(start)) Number(start);
                        else if (IsIdentStartAt(start)) Ident(start);
                        else Op(start);
                        break;
                }
            }
            if (_error is not null)
                return new LexResult([], _error);
            var at = Pos(_i);
            _toks.Add(new Token(TokenKind.Eof, "", new Span(_source, at, at)));
            return new LexResult(_toks, null);
        }

        // —— 记号扫描 ——

        /// <summary>整数 / 浮点（LEX-005/006）。十六进制折算十进制规范文本。</summary>
        private void Number(int start)
        {
            if (_s[start] == '0' && PeekAt(start, 1) is 'x' or 'X')
            {
                _i = start + 2;
                int digitsAt = _i;
                while (_i < _s.Length && IsAsciiHex(_s[_i])) _i++;
                if (_i == digitsAt)
                {
                    Fail("十六进制字面量缺少数字（如 0xFF）");
                    return;
                }
                string raw = _s[start.._i];
                string digits = _s[digitsAt.._i];
                // 上限对齐 Go：u64 解析成功且 ≤ i64.Max 才合法
                if (!ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u)
                    || u > (ulong)long.MaxValue)
                {
                    Fail($"十六进制字面量超出 int 范围: {raw}");
                    return;
                }
                Emit(TokenKind.Int, u.ToString(CultureInfo.InvariantCulture), start);
                return;
            }

            while (_i < _s.Length && IsNdAt(_i)) _i += RuneLenAt(_i);
            // "1..3" 里第一个 '.' 不属于数字（LEX-006）
            if (_i < _s.Length && _s[_i] == '.' && IsNdAt(_i + 1))
            {
                _i++;
                while (_i < _s.Length && IsNdAt(_i)) _i += RuneLenAt(_i);
                Emit(TokenKind.Float, _s[start.._i], start);
            }
            else
            {
                // 原样保留（含全角数字等 Nd 记号），合法性由 parser 阶段校验（与 TS 证据一致）
                Emit(TokenKind.Int, _s[start.._i], start);
            }
        }

        /// <summary>字符串字面量（LEX-007）：转义仅 `\n \t \r \" \\` 五种。</summary>
        private void StringLit(int start)
        {
            _i++;
            var value = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length)
                {
                    Fail("字符串未闭合");
                    return;
                }
                char c = _s[_i];
                if (c == '\n')
                {
                    Fail("字符串未闭合");
                    return;
                }
                if (c == '"')
                {
                    _i++;
                    break;
                }
                if (c == '\\')
                {
                    _i++;
                    if (_i >= _s.Length)
                    {
                        Fail("字符串未闭合");
                        return;
                    }
                    switch (_s[_i])
                    {
                        case 'n': value.Append('\n'); break;
                        case 't': value.Append('\t'); break;
                        case 'r': value.Append('\r'); break;
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        default:
                            Fail($"不支持的转义 \\{CodepointText(_i)}");
                            return;
                    }
                    _i++;
                    continue;
                }
                value.Append(CodepointText(_i)); // astral 代理对整段入值
                _i += RuneLenAt(_i);
            }
            Emit(TokenKind.Str, value.ToString(), start);
        }

        /// <summary>标识符 / 关键字 / 单独下划线（LEX-003/004）。</summary>
        private void Ident(int start)
        {
            while (_i < _s.Length && IsIdentPartAt(_i)) _i += RuneLenAt(_i);
            string name = _s[start.._i];
            if (name == "_")
            {
                Emit(TokenKind.Underscore, name, start);
                return;
            }
            var kw = TokenKinds.KeywordOf(name);
            Emit(kw ?? TokenKind.Ident, name, start);
        }

        /// <summary>运算符 / 标点（最长匹配两级）。</summary>
        private void Op(int start)
        {
            char c = _s[_i];
            switch (c)
            {
                case '&':
                    if (Two('&', TokenKind.AndAnd, "&&", start)) return;
                    _i++;
                    Emit(TokenKind.And, start);
                    return;
                case '|':
                    if (Two('|', TokenKind.OrOr, "||", start)) return;
                    _i++;
                    Emit(TokenKind.Or, start);
                    return;
                case '^': One(TokenKind.Xor, start); return;
                case '+':
                    if (Two('=', TokenKind.PlusEq, "+=", start)) return;
                    _i++;
                    Emit(TokenKind.Plus, start);
                    return;
                case '-':
                    if (Two('=', TokenKind.MinusEq, "-=", start)) return;
                    _i++;
                    Emit(TokenKind.Minus, start);
                    return;
                case '*':
                    if (Two('=', TokenKind.StarEq, "*=", start)) return;
                    _i++;
                    Emit(TokenKind.Star, start);
                    return;
                case '/':
                    if (Two('=', TokenKind.SlashEq, "/=", start)) return;
                    _i++;
                    Emit(TokenKind.Slash, start);
                    return;
                case '=':
                    if (Two('=', TokenKind.EqEq, "==", start)) return;
                    if (Two('>', TokenKind.FatArrow, "=>", start)) return;
                    _i++;
                    Emit(TokenKind.Eq, start);
                    return;
                case '!':
                    if (Two('=', TokenKind.Ne, "!=", start)) return;
                    _i++;
                    Emit(TokenKind.Not, start);
                    return;
                case '<':
                    if (Two('<', TokenKind.Shl, "<<", start)) return;
                    if (Two('=', TokenKind.Le, "<=", start)) return;
                    _i++;
                    Emit(TokenKind.Lt, start);
                    return;
                case '>':
                    if (Two('>', TokenKind.Shr, ">>", start)) return;
                    if (Two('=', TokenKind.Ge, ">=", start)) return;
                    _i++;
                    Emit(TokenKind.Gt, start);
                    return;
                case '.':
                    if (Two('.', TokenKind.DotDot, "..", start)) return;
                    _i++;
                    Emit(TokenKind.Dot, start);
                    return;
                case '%': One(TokenKind.Percent, start); return;
                case '(': One(TokenKind.LParen, start); return;
                case ')': One(TokenKind.RParen, start); return;
                case '{': One(TokenKind.LBrace, start); return;
                case '}': One(TokenKind.RBrace, start); return;
                case '[': One(TokenKind.LBracket, start); return;
                case ']': One(TokenKind.RBracket, start); return;
                case ',': One(TokenKind.Comma, start); return;
                case ':': One(TokenKind.Colon, start); return;
                case '?': One(TokenKind.Question, start); return;
                default:
                    Fail($"非法字符 {CodepointText(_i)}");
                    return;
            }
        }

        private void One(TokenKind kind, int start)
        {
            _i++;
            Emit(kind, start);
        }

        private bool Two(char next, TokenKind kind, string lit, int start)
        {
            if (PeekAt(_i, 1) != next) return false;
            _i += 2;
            _toks.Add(new Token(kind, lit, SpanFrom(start)));
            _last = kind;
            return true;
        }

        // —— 换行续行（LEX-008） ——

        /// <summary>落实续行规则：续行记号之后、行首的换行不产记号。</summary>
        private void EmitNewline(int nlChar)
        {
            if (_toks.Count == 0) return;
            if (_last is { } last)
            {
                if (TokenKinds.IsCont(last)) return;
                if (last == TokenKind.Newline) return;
            }
            _toks.Add(new Token(TokenKind.Newline, "\n", SpanFrom(nlChar)));
            _last = TokenKind.Newline;
        }

        // —— 位置 / 分类 / 错误 ——

        /// <summary>char index → Position（码点 offset + 1 基行列，经 SourceText 折算）。</summary>
        private Position Pos(int charIndex)
        {
            // SourceText.CharIndexToCp 在文末边界（charIndex == LengthChars）返回末码点
            // 而非文末码点总数；此处按「文末 = LengthCp」修正（Lang.Core 边界缺陷，status 已登记）
            int cp = charIndex >= _s.Length ? _text.LengthCp : _text.CharIndexToCp(charIndex);
            var (line, column) = _text.LineCol(cp);
            return new Position((uint)cp, (uint)line, (uint)column);
        }

        private Span SpanFrom(int startChar) => new(_source, Pos(startChar), Pos(_i));

        private void Emit(TokenKind kind, int startChar)
        {
            // 运算符/标点的展示名即其字面（kind_name 对非关键字恒返回原文）
            _toks.Add(new Token(kind, TokenKinds.KindName(kind), SpanFrom(startChar)));
            _last = kind;
        }

        private void Emit(TokenKind kind, string lit, int startChar)
        {
            _toks.Add(new Token(kind, lit, SpanFrom(startChar)));
            _last = kind;
        }

        /// <summary>E1001 行级诊断（当前行；首错锁定，后续不再覆盖）。</summary>
        private void Fail(string msg) =>
            _error ??= Diagnostic.New(DiagnosticCode.E1001, msg, Diagnostic.AtLine(_source, _line));

        private char PeekAt(int from, int n) => from + n < _s.Length ? _s[from + n] : '\0';

        /// <summary>位置 i 处码点的 UTF-16 长度（astral 代理对为 2）。</summary>
        private int RuneLenAt(int i)
        {
            char c = _s[i];
            return char.IsHighSurrogate(c) && i + 1 < _s.Length && char.IsLowSurrogate(_s[i + 1]) ? 2 : 1;
        }

        private Rune RuneAt(int i)
        {
            if (i >= _s.Length) return Rune.ReplacementChar; // 前瞻越界（如 '.' 判浮点）按非 Nd 处理
            char c = _s[i];
            if (char.IsHighSurrogate(c) && i + 1 < _s.Length && char.IsLowSurrogate(_s[i + 1]))
                return Rune.TryCreate(c, _s[i + 1], out var pair) ? pair : Rune.ReplacementChar;
            return Rune.TryCreate(c, out var single) ? single : Rune.ReplacementChar;
        }

        /// <summary>位置 i 处码点的文本（astral 为 2 char，整段取出）。</summary>
        private string CodepointText(int i) => _s.Substring(i, RuneLenAt(i));

        private static bool IsAsciiHex(char c) =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        /// <summary>十进制数字类别 Nd（与 Go unicode.IsDigit、TS `\p{Nd}` 同源；不含 Nl/No）。</summary>
        private bool IsNdAt(int i) =>
            Rune.GetUnicodeCategory(RuneAt(i)) == UnicodeCategory.DecimalDigitNumber;

        private bool IsIdentStartAt(int i)
        {
            var r = RuneAt(i);
            return r.Value == '_' || IsLetter(r);
        }

        private bool IsIdentPartAt(int i)
        {
            var r = RuneAt(i);
            return r.Value == '_' || IsLetter(r)
                || Rune.GetUnicodeCategory(r) == UnicodeCategory.DecimalDigitNumber;
        }

        /// <summary>一般类别 L*（与 Go unicode.IsLetter、TS `\p{L}` 同源）。</summary>
        private static bool IsLetter(Rune r) => Rune.GetUnicodeCategory(r) switch
        {
            UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter => true,
            _ => false,
        };
    }
}
