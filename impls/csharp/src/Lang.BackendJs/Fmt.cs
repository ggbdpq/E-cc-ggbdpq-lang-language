using System.Globalization;
using System.Text;

namespace Lang.BackendJs;

/// <summary>
/// →JS 后端的输出格式化规则层（RUNTIME-OUTPUT-003 / RUNTIME-OUTPUT-004）。
/// 行为证据（只读）：<c>impls/typescript/src/backends/jsprelude.ts</c> 的
/// <c>langGoFmt</c>/<c>langGoFloatStr</c>（带 FIX 注释：按 Go
/// <c>strconv.FormatFloat('g',-1,64)</c> 对齐，不采用 <c>String(v)</c>）。
/// 期望值全部手算并以本机 Go 1.27 标准库对拍。
///
/// 与 →Go 后端的语义分歧点：嵌套字符串转义采用 JSON.stringify 语义（见
/// <see cref="QuoteJson"/>），与 strconv.Quote 在 U+2028/2029、DEL、NBSP、
/// 私用区等码点上有字节级分歧——跨后端分歧按 lang-rs SPEC-GAP-RS-001 口径
/// 如实分别实现，不抹平。
/// </summary>
public static class Fmt
{
    /// <summary>
    /// 值格式化（<c>langGoFmt</c> 镜像）。<c>top=true</c> 为顶层（println/str()）：
    /// 字符串不带引号；<c>top=false</c> 为嵌套：字符串按 JSON.stringify 语义带引号。
    /// </summary>
    public static string FmtValue(FmtNode v, bool top) => v switch
    {
        FmtNode.Unit => "()",
        FmtNode.Int i => i.Value.ToString(CultureInfo.InvariantCulture), // BigInt 全域十进制镜像
        FmtNode.Float f => FmtFloat(f.Value),
        FmtNode.Bool b => b.Value ? "true" : "false",
        FmtNode.Str s => top ? s.Value : QuoteJson(s.Value),
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
        FmtNode.Error e => $"error{{msg: {QuoteJson(e.Msg)}}}",
        _ => throw new InvalidOperationException("不可达：FmtNode 全部变体已穷尽（内部不变量）"),
    };

    /// <summary>println/print 的实参格式化：顶层形 + 空格分隔（RUNTIME-OUTPUT-003）。</summary>
    public static string FmtArgs(IReadOnlyList<FmtNode> args) =>
        string.Join(" ", args.Select(a => FmtValue(a, true)));

    /// <summary>
    /// float 文本形：Go <c>strconv.FormatFloat(f, 'g', -1, 64)</c> 等价
    /// （jsprelude <c>langGoFloatStr</c> 声明的目标语义；非 JS/C# 的 String(v) 默认形）。
    /// 规则与 →Go 后端逐字节一致（同一 Go 1.27 对拍矩阵）。
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
    /// 嵌套字符串引号：JSON.stringify 语义（jsprelude <c>langGoFmt</c> 字符串分支）。
    /// 仅转义 <c>"</c> <c>\</c> 与 &lt; 0x20 控制字符（<c>\b \f \n \r \t</c> 短形，
    /// 其余 <c>\u00XX</c> 小写）；其余码点直出——U+2028/2029、DEL(0x7F)、NBSP 均
    /// <b>不</b>转义。与 →Go 的 <c>strconv.Quote</c> 分歧按 SPEC-GAP-RS-001 各自实现。
    /// 按 UTF-16 码点（char 对）迭代——astral 码点是单个码点，不得按代理半拆。
    /// </summary>
    public static string QuoteJson(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        int i = 0;
        while (i < s.Length)
        {
            int cp = CodePointAt(s, i, out int size);
            i += size;
            switch (cp)
            {
                case 0x22: sb.Append("\\\""); break;
                case 0x5c: sb.Append("\\\\"); break;
                case 0x08: sb.Append("\\b"); break;
                case 0x0c: sb.Append("\\f"); break;
                case 0x0a: sb.Append("\\n"); break;
                case 0x0d: sb.Append("\\r"); break;
                case 0x09: sb.Append("\\t"); break;
                default:
                    if (cp < 0x20)
                    {
                        sb.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (size == 2)
                    {
                        // astral 码点：原样追加代理对两半
                        sb.Append(s[i - 2]).Append(s[i - 1]);
                    }
                    else
                    {
                        sb.Append((char)cp);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>最短舍入数字提取（与 Lang.BackendGo 同法：.NET 默认格式即最短往返表示）。</summary>
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

        int last = digits.Length;
        while (last > 1 && digits[last - 1] == '0')
        {
            last--;
        }

        digits = digits[..last];
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

    /// <summary>%f 形：定点。不补尾零。</summary>
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

    /// <summary>码点序比较（UTF-8 字节序 = 码点序，非 UTF-16 序）。</summary>
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
