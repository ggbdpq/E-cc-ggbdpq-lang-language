using System.Globalization;
using System.Text;

namespace Lang.BackendGo;

/// <summary>
/// →Go 后端的输出格式化规则层（RUNTIME-OUTPUT-003 / RUNTIME-OUTPUT-004）。
/// 行为证据（只读）：<c>impls/typescript/src/interp/value.ts</c> 的
/// <c>fmtVal</c>/<c>goFormatFloat</c>/<c>goQuote</c>。期望值全部手算并以本机
/// Go 1.27 标准库（strconv.FormatFloat('g',-1,64) / strconv.Quote）对拍确认，
/// 不回读自身实现输出（防同义反复）。
///
/// 与 →JS 后端唯一的语义分歧点：嵌套字符串转义（本层用 Go strconv.Quote 语义，
/// 见 <see cref="QuoteGo"/>；→JS 用 JSON.stringify 语义，跨后端分歧按 lang-rs
/// SPEC-GAP-RS-001 口径如实分别实现）。
/// </summary>
public static class Fmt
{
    /// <summary>
    /// 值格式化（解释器 <c>fmtVal</c> 镜像）。<c>top=true</c> 为顶层（println/str()）：
    /// 字符串不带引号；<c>top=false</c> 为嵌套：字符串带引号。
    /// </summary>
    public static string FmtValue(FmtNode v, bool top) => v switch
    {
        FmtNode.Unit => "()",
        FmtNode.Int i => i.Value.ToString(CultureInfo.InvariantCulture),
        FmtNode.Float f => FmtFloat(f.Value),
        FmtNode.Bool b => b.Value ? "true" : "false",
        FmtNode.Str s => top ? s.Value : QuoteGo(s.Value),
        FmtNode.Fn fn => fn.Name is null ? "<fn 匿名>" : $"<fn {fn.Name}>",
        FmtNode.Range r => $"{r.Lo.ToString(CultureInfo.InvariantCulture)}..{r.Hi.ToString(CultureInfo.InvariantCulture)}",
        FmtNode.Ptr p => "&" + FmtValue(p.Inner, false),
        FmtNode.Arr a => "[" + string.Join(", ", a.Items.Select(it => FmtValue(it, false))) + "]",
        FmtNode.Map m => "{" + string.Join(", ", m.Entries.Select(e => $"{FmtValue(e.Key, false)}: {FmtValue(e.Value, false)}")) + "}",
        FmtNode.Struct st => st.Name + "{" + string.Join(", ", st.Fields.Select(f => $"{f.Field}: {FmtValue(f.Value, false)}")) + "}",
        FmtNode.EnumValue e => e.Payloads.Count == 0
            ? e.Tag
            : e.Tag + "(" + string.Join(", ", e.Payloads.Select(p => FmtValue(p, false))) + ")",
        FmtNode.Option o => o.Inner is null ? "None" : $"Some({FmtValue(o.Inner, false)})",
        FmtNode.Ok ok => $"Ok({FmtValue(ok.Value, false)})",
        FmtNode.Err err => $"Err({FmtValue(err.Value, false)})",
        FmtNode.Error e => $"error{{msg: {QuoteGo(e.Msg)}}}",
        _ => throw new InvalidOperationException("不可达：FmtNode 全部变体已穷尽（内部不变量）"),
    };

    /// <summary>println/print 的实参格式化：顶层形 + 空格分隔（RUNTIME-OUTPUT-003）。</summary>
    public static string FmtArgs(IReadOnlyList<FmtNode> args) =>
        string.Join(" ", args.Select(a => FmtValue(a, true)));

    /// <summary>
    /// float 文本形：Go <c>strconv.FormatFloat(f, 'g', -1, 64)</c> 的镜像。
    /// 规则（已按 Go 1.27 标准库逐值对拍）：NaN → <c>NaN</c>；+Inf/-Inf → <c>+Inf</c>/<c>-Inf</c>；
    /// ±0 → <c>0</c>/<c>-0</c>；最短舍入数字；十进制指数 &lt; -4 或 &gt;= 6 →
    /// <c>d.ddde±XX</c>（指数至少两位、恒带符号），否则定点无尾零（<c>1.0</c> → <c>1</c>）。
    /// C# 陷阱：所有文本转换显式 InvariantCulture，禁用 double.ToString 的默认
    /// 科学计数切换阈值（≥1e15 才切换，与 Go 'g' 的 1e6 阈值不同）。
    /// </summary>
    public static string FmtFloat(double f)
    {
        if (double.IsNaN(f))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(f))
        {
            return "+Inf";
        }

        if (double.IsNegativeInfinity(f))
        {
            return "-Inf";
        }

        if (f == 0.0)
        {
            return double.IsNegative(f) ? "-0" : "0";
        }

        var (digits, dp) = ShortestDigits(Math.Abs(f));
        int exp = dp - 1;
        // 条件按 Go strconv 源码逐字镜像（exp < -4 || exp >= 6 走 %e），不改写为区间形
        string body = exp < -4 || exp >= 6
            ? SciFormat(digits, exp)
            : FixedFormat(digits, dp);
        return double.IsNegative(f) ? "-" + body : body;
    }

    /// <summary>
    /// Go <c>strconv.Quote</c> 的镜像（嵌套字符串引号）。
    /// <c>"</c> → <c>\"</c>，<c>\</c> → <c>\\</c>；ASCII 控制字符走
    /// <c>\a \b \f \n \r \t \v</c>，其余 <c>\xNN</c>（小写十六进制，含 DEL 0x7F）；
    /// U+2028/U+2029 与非打印 Unicode → <c>\uNNNN</c>（&gt;0xFFFF 用 <c>\UNNNNNNNN</c>）；
    /// 打印 Unicode（L/M/N/P/S 类 + ASCII 空格，含中文/emoji/组合记号）直出。
    /// 按 UTF-16 码点（char 对）迭代——astral 码点是单个码点，不得按代理半拆。
    /// </summary>
    public static string QuoteGo(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        int i = 0;
        while (i < s.Length)
        {
            int cp = CodePointAt(s, i, out int size);
            i += size;
            if (cp < 0x80)
            {
                switch (cp)
                {
                    case 0x22: sb.Append("\\\""); break;
                    case 0x5c: sb.Append("\\\\"); break;
                    case 0x07: sb.Append("\\a"); break;
                    case 0x08: sb.Append("\\b"); break;
                    case 0x0c: sb.Append("\\f"); break;
                    case 0x0a: sb.Append("\\n"); break;
                    case 0x0d: sb.Append("\\r"); break;
                    case 0x09: sb.Append("\\t"); break;
                    case 0x0b: sb.Append("\\v"); break;
                    default:
                        if (cp is >= 0x20 and < 0x7f)
                        {
                            sb.Append((char)cp);
                        }
                        else
                        {
                            sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
                        }

                        break;
                }

                continue;
            }

            if (cp == 0x2028 || cp == 0x2029 || !IsPrintUnicode(cp))
            {
                sb.Append(cp > 0xFFFF ? "\\U" : "\\u")
                    .Append(cp.ToString(cp > 0xFFFF ? "x8" : "x4", CultureInfo.InvariantCulture));
                continue;
            }

            if (size == 2)
            {
                // astral 码点：原样追加代理对两半
                sb.Append(s[i - 2]).Append(s[i - 1]);
            }
            else
            {
                sb.Append((char)cp);
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// 最短舍入数字提取：.NET（Core 3.0 起）double 默认格式即最短往返表示，
    /// InvariantCulture 下形如 <c>123.456</c> / <c>1.2E+21</c> / <c>5E-324</c>。
    /// 返回（无符号十进制数字串、小数点位置 dp），满足 <c>value = 0.digits × 10^dp</c>。
    /// </summary>
    internal static (string Digits, int Dp) ShortestDigits(double a)
    {
        string s = a.ToString(CultureInfo.InvariantCulture);
        string digits;
        int dp;
        int eIdx = s.IndexOf('E');
        if (eIdx >= 0)
        {
            // C# E 形尾数恒为「点前恰一位」（如 1.797…E+308 / 5E-324）：
            // value = 0.digits × 10^(exp+1)，与点的位置无关
            string mant = s[..eIdx];
            int exp = int.Parse(s[(eIdx + 1)..], CultureInfo.InvariantCulture);
            int dot = mant.IndexOf('.');
            digits = dot >= 0 ? mant.Remove(dot, 1) : mant;
            dp = exp + 1;
        }
        else
        {
            int dot = s.IndexOf('.');
            if (dot >= 0)
            {
                digits = s.Remove(dot, 1);
                dp = dot;
            }
            else
            {
                digits = s;
                dp = s.Length;
            }
        }

        // 去尾零（如 "100" → "1"，dp 不动：0.1×10^3 = 100）
        int last = digits.Length;
        while (last > 1 && digits[last - 1] == '0')
        {
            last--;
        }

        digits = digits[..last];
        // 去前导零（"0.001" → digits "1"，每去一位 dp 减一）
        int start = 0;
        while (start < digits.Length - 1 && digits[start] == '0')
        {
            start++;
        }

        if (start > 0)
        {
            digits = digits[start..];
            dp -= start;
        }

        return (digits, dp);
    }

    /// <summary>%e 形：<c>d.ddde±XX</c>（指数至少两位、恒带符号）。</summary>
    internal static string SciFormat(string digits, int exp)
    {
        string mant = digits[..1];
        if (digits.Length > 1)
        {
            mant += "." + digits[1..];
        }

        string sign = exp < 0 ? "-" : "+";
        return $"{mant}e{sign}{Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>%f 形：定点。不补尾零——最短数字本身不含多余零。</summary>
    internal static string FixedFormat(string digits, int dp)
    {
        if (dp <= 0)
        {
            return "0." + new string('0', -dp) + digits;
        }

        if (dp >= digits.Length)
        {
            return digits + new string('0', dp - digits.Length);
        }

        return digits[..dp] + "." + digits[dp..];
    }

    /// <summary>s[i] 起的码点：代理对合成 astral 码点，否则取单个 char。</summary>
    internal static int CodePointAt(string s, int i, out int size)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            size = 2;
            return 0x10000 + ((c - 0xD800) << 10) + (s[i + 1] - 0xDC00);
        }

        size = 1;
        return c;
    }

    /// <summary>
    /// Go <c>unicode.IsPrint</c>：L/M/N/P/S 类 + ASCII 空格为打印。C# 直接读
    /// Unicode 类别表（比 lang-rs 在 std 无类别表下的硬编码近似更精确：未分配
    /// 码点 Cn、私用区 Co、分隔符 Z*、格式符 Cf 均非打印，与 Go 一致）。
    /// 代理区（lone surrogate）按非打印处理（Rune API 不接受，Go 侧不可达）。
    /// </summary>
    internal static bool IsPrintUnicode(int cp)
    {
        if (cp is >= 0xD800 and <= 0xDFFF)
        {
            return false;
        }

        return Rune.GetUnicodeCategory(new Rune(cp)) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
                or UnicodeCategory.OtherNumber
                or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol => true,
            _ => cp == 0x20,
        };
    }

    /// <summary>码点序比较（UTF-8 字节序 = 码点序，非 UTF-16 序）。键排序/前缀短者在前。</summary>
    internal static int CodePointCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int ca = CodePointAt(a, i, out int sa);
            int cb = CodePointAt(b, j, out int sb);
            if (ca != cb)
            {
                return ca < cb ? -1 : 1;
            }

            i += sa;
            j += sb;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}
