using System.Text.Json;
using Lang.BackendGo;
using Lang.Core;
using Lang.Runtime;

// A5 冻结纯函数的别名（本类自身有 Fmt 方法，裸名 Fmt/Json 会被成员遮蔽）
using GoFmt = Lang.BackendGo.Fmt;
using GoJson = Lang.BackendGo.Json;

namespace Lang.Interp;

/// <summary>
/// 解释器输出面格式化器（ValueFormatter 桥 + 宿主内建）。
/// 打印与 JSON 规则<b>零漂移复用</b> A5 已冻结的纯函数
/// （<see cref="Fmt"/>：float = Go strconv.FormatFloat('g',-1,64) 镜像、嵌套字符串 =
/// strconv.Quote 镜像；<see cref="Json"/>：RUNTIME-JSON-002 字节契约）。
/// 本模块只负责 <see cref="Value"/> → <see cref="FmtNode"/> 的桥接与宿主内建路由。
///
/// Node 形状之外的可打印值（chan / 内建函数值）在进入桥之前截获：
/// chan 打印 <c>?</c>、内建打印 <c>&lt;内建 名&gt;</c>（TS 证据口径；conformance 语料
/// 不打印这两类值，嵌套形态差异不可达）。
/// </summary>
public sealed class InterpFmt : ValueFormatter
{
    /// <summary>共享实例（无状态；堆/定义表经参数 <see cref="Machine"/> 读取）。</summary>
    public static readonly InterpFmt Instance = new();

    private InterpFmt()
    {
    }

    /// <inheritdoc />
    public string Fmt(Machine machine, Value v, bool top) => v switch
    {
        Value.Chan => "?",
        Value.Builtin b => $"<内建 {machine.Builtins.Name(b.Id)}>",
        _ => GoFmt.FmtValue(ToNode(machine, v), top),
    };

    /// <summary>
    /// <see cref="Value"/> → <see cref="FmtNode"/>（A5 抽象打印值树）：struct/enum 字段名
    /// 与 case 名取自定义表；容器按插入序展开；引用打印为穿透后的 <c>&amp;嵌套形</c>；
    /// Option/Result 映射到预载定义身份（def id 相等判定，非名字判定）。
    /// </summary>
    public static FmtNode ToNode(Machine m, Value v) => v switch
    {
        Value.Int i => new FmtNode.Int(i.V),
        Value.Float f => new FmtNode.Float(f.V),
        Value.Bool b => new FmtNode.Bool(b.V),
        Value.Str s => new FmtNode.Str(s.V),
        Value.Unit => FmtNode.Unit.Instance,
        Value.Range r => new FmtNode.Range(r.Lo, r.Hi),
        Value.Slice id => new FmtNode.Arr(SliceItems(m, id.Id)),
        Value.Map id => new FmtNode.Map(MapEntries(m, id.Id)),
        Value.Struct sv => StructNode(m, sv),
        Value.Enum e => EnumNode(m, e),
        Value.Closure c => FnNode(m, c.Func),
        Value.Ref p => RefNode(m, p.Place),
        // Chan/Builtin 在 Fmt 入口已截获；此处仅为完备性兜底
        Value.Chan or Value.Builtin => new FmtNode.Str("?"),
        _ => throw new InvalidOperationException("未知 Value 变体（内部不变量）"),
    };

    private static IReadOnlyList<FmtNode> SliceItems(Machine m, ValueId id)
    {
        var r = m.Heap.Slice(id);
        return r.IsOk ? r.Value.Select(it => ToNode(m, it)).ToList() : [];
    }

    private static IReadOnlyList<(FmtNode, FmtNode)> MapEntries(Machine m, ValueId id)
    {
        var r = m.Heap.Map(id);
        return r.IsOk ? r.Value.Slots.Select(s => (ToNode(m, s.Key), ToNode(m, s.Val))).ToList() : [];
    }

    private static FmtNode StructNode(Machine m, Value.Struct sv)
    {
        var def = m.Defs.StructDef(sv.Val.Def);
        if (!def.IsOk)
        {
            return FmtNode.Unit.Instance;
        }

        var fields = def.Value.Fields
            .Zip(sv.Val.Fields, (n, v) => (n, ToNode(m, v)))
            .ToList();
        return new FmtNode.Struct(def.Value.Name, fields);
    }

    private static FmtNode EnumNode(Machine m, Value.Enum e)
    {
        if (e.Def == m.Defs.OptionDef())
        {
            return new FmtNode.Option(e.Payloads.Count > 0 ? ToNode(m, e.Payloads[0]) : null);
        }

        if (e.Def == m.Defs.ResultDef())
        {
            var inner = e.Payloads.Count > 0 ? ToNode(m, e.Payloads[0]) : FmtNode.Unit.Instance;
            return e.Case == 0 ? new FmtNode.Ok(inner) : new FmtNode.Err(inner);
        }

        var tag = m.EnumCaseName(e.Def, e.Case);
        var payloads = e.Payloads.Select(p => ToNode(m, p)).ToList();
        return new FmtNode.EnumValue(tag.IsOk ? tag.Value : string.Empty, payloads);
    }

    private static FmtNode FnNode(Machine m, FunctionId func)
    {
        var name = m.FnName(func);
        return name.Length == 0 ? new FmtNode.Fn(null) : new FmtNode.Fn(name);
    }

    private static FmtNode RefNode(Machine m, Place p)
    {
        var r = m.PlaceLoadResolved(p);
        return r.IsOk ? new FmtNode.Ptr(ToNode(m, r.Value)) : new FmtNode.Str("?");
    }

    // ———— 宿主内建（Host 类别在 Machine.CallBuiltin 路由前拦截） ————

    /// <summary>宿主内建分发：jsonParse / jsonStringify（RUNTIME-JSON-001/002）。
    /// 其余 Host 名（如 http.get）报「宿主未提供」。</summary>
    public static RtResult<Value> HostBuiltin(Machine machine, string name, IReadOnlyList<Value> args) =>
        name switch
        {
            "jsonParse" => JsonParse(machine, args),
            "jsonStringify" => JsonStringify(machine, args),
            _ => RtResult<Value>.Fail(new RtError(
                RtErrorKind.UnsupportedHost, $"内建 {name} 由宿主实现（当前解释器未提供）")),
        };

    private static RtError ArityErr(string name, IReadOnlyList<Value> args) =>
        new(RtErrorKind.TypeCast, $"{name} 需要 1 个参数，得到 {args.Count} 个");

    /// <summary><c>jsonParse(s)</c>：两阶段——System.Text.Json 校验合法性，再按
    /// json.Number 语义提取（整数优先 int64；对象键按码点序载入；null → None；
    /// 对象 → 映射；数组 → 数组）。</summary>
    private static RtResult<Value> JsonParse(Machine machine, IReadOnlyList<Value> args)
    {
        if (args.Count != 1)
        {
            return RtResult<Value>.Fail(ArityErr("jsonParse", args));
        }

        if (args[0] is not Value.Str s)
        {
            return RtResult<Value>.Fail(new RtError(
                RtErrorKind.TypeCast, $"jsonParse 需要字符串，得到 {args[0].TypeHint()}"));
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(s.V);
        }
        catch (JsonException)
        {
            // 错误分类是契约（RUNTIME-JSON-001）；detail 与证据同形（EOF / 首字符非法二分）
            var detail = string.IsNullOrWhiteSpace(s.V)
                ? "unexpected end of JSON input"
                : "invalid character looking for beginning of value";
            return RtResult<Value>.Fail(new RtError(
                RtErrorKind.JsonParse, $"JSON 解析失败: {detail}"));
        }

        using (doc)
        {
            return JsonToValue(machine, doc.RootElement);
        }
    }

    private static RtResult<Value> JsonToValue(Machine machine, JsonElement raw)
    {
        switch (raw.ValueKind)
        {
            case JsonValueKind.Null:
                return RtResult<Value>.Ok(machine.NoneValue());
            case JsonValueKind.True or JsonValueKind.False:
                return RtResult<Value>.Ok(new Value.Bool(raw.GetBoolean()));
            case JsonValueKind.Number:
                return JsonNumber(raw);
            case JsonValueKind.String:
                return RtResult<Value>.Ok(new Value.Str(raw.GetString()!));
            case JsonValueKind.Array:
                {
                    var vals = new List<Value>();
                    foreach (var it in raw.EnumerateArray())
                    {
                        var r = JsonToValue(machine, it);
                        if (!r.IsOk)
                        {
                            return r;
                        }

                        vals.Add(r.Value);
                    }

                    return RtResult<Value>.Ok(machine.AllocSliceValue(vals));
                }

            case JsonValueKind.Object:
                {
                    // 键按码点序载入（RUNTIME-JSON-002；文档序不外泄，显式排序保证确定）
                    var keys = new List<string>();
                    foreach (var prop in raw.EnumerateObject())
                    {
                        keys.Add(prop.Name);
                    }

                    keys.Sort(CodePointCompare);
                    var entries = new List<(Value, Value)>(keys.Count);
                    foreach (var k in keys)
                    {
                        var r = JsonToValue(machine, raw.GetProperty(k));
                        if (!r.IsOk)
                        {
                            return r;
                        }

                        entries.Add((new Value.Str(k), r.Value));
                    }

                    var map = machine.AllocMapValue();
                    if (map is not Value.Map mv)
                    {
                        return RtResult<Value>.Fail(RtError.Internal("新分配映射缺索引"));
                    }

                    foreach (var (k, v) in entries)
                    {
                        var ins = machine.Heap.MapInsert(mv.Id, k, v);
                        if (!ins.IsOk)
                        {
                            return RtResult<Value>.Fail(ins.Error!);
                        }
                    }

                    return RtResult<Value>.Ok(map);
                }

            default:
                return RtResult<Value>.Fail(RtError.Internal("JSON 值种类未知（内部不变量）"));
        }
    }

    /// <summary>json.Number 语义：整数文本优先 int64；越 i64 域按浮点（证据口径）。</summary>
    private static RtResult<Value> JsonNumber(JsonElement raw)
    {
        var text = raw.GetRawText();
        if (text.IndexOfAny(['.', 'e', 'E']) < 0)
        {
            if (long.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var i))
            {
                return RtResult<Value>.Ok(new Value.Int(i));
            }

            if (ulong.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var u))
            {
                return RtResult<Value>.Ok(new Value.Float(u));
            }
        }

        return RtResult<Value>.Ok(new Value.Float(raw.GetDouble()));
    }

    /// <summary><c>jsonStringify(v)</c>：<see cref="Value"/> → <see cref="FmtNode"/> 后走
    /// A5 冻结的字节契约（键码点序、HTML 转义、int64 直出、None/unit → null）。</summary>
    private static RtResult<Value> JsonStringify(Machine machine, IReadOnlyList<Value> args)
    {
        if (args.Count != 1)
        {
            return RtResult<Value>.Fail(ArityErr("jsonStringify", args));
        }

        var node = ToNode(machine, args[0]);
        return GoJson.TryStringify(node, out var text, out var error)
            ? RtResult<Value>.Ok(new Value.Str(text))
            : RtResult<Value>.Fail(new RtError(RtErrorKind.TypeCast, error!.Message));
    }

    /// <summary>码点序比较（= UTF-8 字节序；C# string.Ordinal 是 UTF-16 序，astral
    /// 与 U+E000..U+FFFF 相对次序相反，JSON 键排序必须逐码点）。</summary>
    private static int CodePointCompare(string x, string y)
    {
        var xi = 0;
        var yi = 0;
        while (xi < x.Length && yi < y.Length)
        {
            var ca = CodePointAt(x, xi, out var sx);
            var cb = CodePointAt(y, yi, out var sy);
            if (ca != cb)
            {
                return ca < cb ? -1 : 1;
            }

            xi += sx;
            yi += sy;
        }

        return (x.Length - xi).CompareTo(y.Length - yi);
    }

    /// <summary>s[i] 起的码点：代理对合成 astral 码点，否则取单个 char（孤代理按 char 计）。</summary>
    private static int CodePointAt(string s, int i, out int size)
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
}
