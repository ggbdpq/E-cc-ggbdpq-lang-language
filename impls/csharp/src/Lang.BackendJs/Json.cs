using System.Globalization;
using System.Text;

namespace Lang.BackendJs;

/// <summary>序列化失败（运行时面：RUNTIME-FAIL-001）。受控错误类型，不抛异常。</summary>
public sealed record JsonError(string Message);

/// <summary>
/// jsonStringify 序列化规则（RUNTIME-JSON-002 跨实现硬契约）。
/// 语义与 →Go 后端逐字节一致（键码点序、HTML 转义、int64 全精度、Go
/// encoding/json 浮点形、-0.0 → <c>0</c> 取解释器语义）——这是硬契约的全文。
/// 行为证据：<c>impls/typescript/src/interp/value.ts</c> 的
/// <c>goJSONString</c>/<c>goJSONFloat</c> + <c>builtin.ts</c> 的 <c>marshalJSON</c>。
/// 期望值全部手算并以本机 Go 1.27 encoding/json 对拍。
/// </summary>
public static class Json
{
    /// <summary>lang 值 → JSON 文本（规则清单见 Lang.BackendGo.Json.TryStringify，两后端同文）。</summary>
    public static bool TryStringify(FmtNode v, out string json, out JsonError? error)
    {
        var (body, err) = Stringify(v);
        if (err is not null)
        {
            json = "";
            error = err;
            return false;
        }

        json = body;
        error = null;
        return true;
    }

    /// <summary>Go encoding/json 字符串编码镜像（HTML 转义开启；与 <c>Fmt.QuoteJson</c>
    /// 的差异是 <c>&lt; &gt; &amp;</c> 三字符）。</summary>
    public static string JsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '<': sb.Append("\\u003c"); break;
                case '>': sb.Append("\\u003e"); break;
                case '&': sb.Append("\\u0026"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Go encoding/json 浮点编码镜像：abs ∈ [1e-6, 1e21) 定点，否则 <c>e</c> 形
    /// （负指数去前导零）；NaN/Inf 非法；<c>-0.0</c> → <c>0</c>。</summary>
    public static bool TryJsonFloat(double f, out string json, out JsonError? error)
    {
        if (double.IsNaN(f))
        {
            json = "";
            error = new JsonError("JSON 序列化失败: json: unsupported value: NaN");
            return false;
        }

        if (double.IsPositiveInfinity(f) || double.IsNegativeInfinity(f))
        {
            string name = double.IsPositiveInfinity(f) ? "+Inf" : "-Inf";
            json = "";
            error = new JsonError($"JSON 序列化失败: json: unsupported value: {name}");
            return false;
        }

        double a = Math.Abs(f);
        (string digits, int dp) = Fmt.ShortestDigits(a);
        string body;
        if (a != 0.0 && (a < 1e-6 || a >= 1e21))
        {
            int exp = dp - 1;
            string mant = digits[..1];
            if (digits.Length > 1)
            {
                mant += "." + digits[1..];
            }

            string sign = exp < 0 ? "-" : "+";
            body = $"{mant}e{sign}{Math.Abs(exp).ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            body = Fmt.FixedFormat(digits, dp);
        }

        json = f < 0 ? "-" + body : body;
        error = null;
        return true;
    }

    private static (string Json, JsonError? Error) Stringify(FmtNode v)
    {
        switch (v)
        {
            case FmtNode.Unit:
            case FmtNode.Option { Inner: null }:
                return ("null", null);
            case FmtNode.Bool b:
                return (b.Value ? "true" : "false", null);
            case FmtNode.Int i:
                return (i.Value.ToString(CultureInfo.InvariantCulture), null);
            case FmtNode.Float f:
                return TryJsonFloat(f.Value, out string fj, out JsonError? fe)
                    ? (fj, null)
                    : ("", fe);
            case FmtNode.Str s:
                return (JsonString(s.Value), null);
            case FmtNode.Arr a:
                {
                    var sb = new StringBuilder("[");
                    for (int i = 0; i < a.Items.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }

                        var (inner, err) = Stringify(a.Items[i]);
                        if (err is not null)
                        {
                            return ("", err);
                        }

                        sb.Append(inner);
                    }

                    sb.Append(']');
                    return (sb.ToString(), null);
                }
            case FmtNode.Map m:
                {
                    var keys = new List<(string Key, FmtNode Value)>(m.Entries.Count);
                    foreach (var (k, val) in m.Entries)
                    {
                        if (k is not FmtNode.Str sk)
                        {
                            return ("", new JsonError($"JSON 对象键须为 string，得到 {TypeName(k)}"));
                        }

                        keys.Add((sk.Value, val));
                    }

                    // 契约要求码点序（C# 默认序是 UTF-16 序，astral 键会错位）
                    keys.Sort((x, y) => Fmt.CodePointCompare(x.Key, y.Key));
                    var sb = new StringBuilder("{");
                    for (int i = 0; i < keys.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }

                        sb.Append(JsonString(keys[i].Key)).Append(':');
                        var (inner, err) = Stringify(keys[i].Value);
                        if (err is not null)
                        {
                            return ("", err);
                        }

                        sb.Append(inner);
                    }

                    sb.Append('}');
                    return (sb.ToString(), null);
                }
            default:
                return ("", new JsonError($"{NodeTag(v)} 不可 JSON 序列化"));
        }
    }

    /// <summary>错误消息用的类型名（对齐解释器 typeName）。</summary>
    private static string TypeName(FmtNode v) => v switch
    {
        FmtNode.Unit => "()",
        FmtNode.Int => "int",
        FmtNode.Float => "float",
        FmtNode.Bool => "bool",
        FmtNode.Str => "string",
        FmtNode.Fn => "函数",
        FmtNode.Range => "区间",
        FmtNode.Ptr => "引用",
        FmtNode.Arr => "数组",
        FmtNode.Map => "映射",
        FmtNode.Struct st => st.Name,
        FmtNode.EnumValue => "enum",
        FmtNode.Option => "Option",
        FmtNode.Ok or FmtNode.Err => "Result",
        FmtNode.Error => "error",
        _ => throw new InvalidOperationException("不可达：FmtNode 全部变体已穷尽（内部不变量）"),
    };

    /// <summary>序列化拒绝消息里的值名：enum 用 case 名，其余用类型名。</summary>
    private static string NodeTag(FmtNode v) => v switch
    {
        FmtNode.EnumValue e => e.Tag,
        FmtNode.Option { Inner: not null } => "Some",
        FmtNode.Ok => "Ok",
        FmtNode.Err => "Err",
        _ => TypeName(v),
    };
}
