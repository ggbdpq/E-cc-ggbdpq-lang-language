using System.Globalization;
using System.Text;

namespace Lang.Runtime;

/// <summary>内建身份（注册表下标 newtype）。</summary>
public readonly record struct BuiltinId(uint Value);

/// <summary>内建类别（决定调用路由与元数检查）。</summary>
public enum BuiltinKind
{
    /// <summary>println(args...)：空格分隔 + 换行（RUNTIME-OUTPUT-003）。</summary>
    Println,

    /// <summary>print(args...)：空格分隔，无换行。</summary>
    Print,

    /// <summary>len(x)：str 码点数 / 数组 / 映射 / 区间。</summary>
    Len,

    /// <summary>str(v)：值的顶层文本形。</summary>
    Str,

    /// <summary>int(v)。</summary>
    Int,

    /// <summary>float(v)。</summary>
    Float,

    /// <summary>keys(m)：插入序键数组。</summary>
    Keys,

    /// <summary>values(m)：插入序值数组。</summary>
    Values,

    /// <summary>has(m, k)：合法键探测。</summary>
    Has,

    /// <summary>push(arr, v...)：原位追加。</summary>
    Push,

    /// <summary>assert(cond, msg?)。</summary>
    Assert,

    /// <summary>err(msg)：构造 error{msg} struct。</summary>
    Err,

    /// <summary>bytes(s)：UTF-8 字节数组。</summary>
    Bytes,

    /// <summary>char(n)：码点 → 单字符字符串（非法码点 → U+FFFD）。</summary>
    Char,

    /// <summary>makeChan()。</summary>
    MakeChan,

    /// <summary>send(ch, v)。</summary>
    Send,

    /// <summary>recv(ch)。</summary>
    Recv,

    /// <summary>isNone(v)。</summary>
    IsNone,

    /// <summary>宿主实现（jsonParse/jsonStringify/http.get 等）：调用口存在，实现归宿主。</summary>
    Host,
}

/// <summary>输出汇：println/print 的落点（进程 stdout 或测试缓冲）。</summary>
public interface OutputSink
{
    /// <summary>原样写出（不自动换行）。</summary>
    void Write(string s);
}

/// <summary>进程 stdout 输出汇。</summary>
public sealed class StdoutSink : OutputSink
{
    public void Write(string s) => Console.Out.Write(s);
}

/// <summary>
/// 值格式化钩子：输出面细节（float 安全区、%q 引号、struct/enum 形）
/// 由 A5/解释器波次提供实现；运行时只消费结果。
/// top=true 时字符串顶层不带引号（RUNTIME-OUTPUT-003）。
/// </summary>
public interface ValueFormatter
{
    string Fmt(Machine machine, Value v, bool top);
}

/// <summary>
/// 内建注册表：名字面冻结（20 名锁定，与运行时证据 CORE_BUILTINS + stdlib 对齐）。
/// 注册表锁定内建**名字面**（name + 元数 + 类别）；执行分两类：
/// - 运行时自有：println/print、chan 三件套、纯值内建，由 <see cref="Machine.CallBuiltin"/> 实现；
/// - 宿主类（Host）：jsonParse/jsonStringify/http.get 等——具体格式化与 IO 规则由宿主实现，
///   运行时只保证名字注册与调用路由口。
/// </summary>
public sealed class BuiltinRegistry
{
    private sealed record Entry(string Name, BuiltinKind Kind, uint MinArgs, uint? MaxArgs);

    private readonly Dictionary<string, BuiltinId> _byName = new(StringComparer.Ordinal);
    private readonly List<Entry> _entries = new();

    /// <summary>已登记内建总数（注册表面锁定测试用）。</summary>
    public int Count => _entries.Count;

    /// <summary>核心注册表：语言标准内建全量名字面。</summary>
    public static BuiltinRegistry Core()
    {
        var r = new BuiltinRegistry();
        void Add(string name, BuiltinKind kind, uint min, uint? max) => r.Register(new Entry(name, kind, min, max));
        Add("println", BuiltinKind.Println, 0, null);
        Add("print", BuiltinKind.Print, 0, null);
        Add("len", BuiltinKind.Len, 1, 1);
        Add("str", BuiltinKind.Str, 1, 1);
        Add("int", BuiltinKind.Int, 1, 1);
        Add("float", BuiltinKind.Float, 1, 1);
        Add("keys", BuiltinKind.Keys, 1, 1);
        Add("values", BuiltinKind.Values, 1, 1);
        Add("has", BuiltinKind.Has, 2, 2);
        Add("push", BuiltinKind.Push, 2, null);
        Add("assert", BuiltinKind.Assert, 1, 2);
        Add("err", BuiltinKind.Err, 1, 1);
        Add("bytes", BuiltinKind.Bytes, 1, 1);
        Add("char", BuiltinKind.Char, 1, 1);
        Add("makeChan", BuiltinKind.MakeChan, 0, 0);
        Add("send", BuiltinKind.Send, 2, 2);
        Add("recv", BuiltinKind.Recv, 1, 1);
        Add("isNone", BuiltinKind.IsNone, 1, 1);
        Add("jsonParse", BuiltinKind.Host, 1, 1);
        Add("jsonStringify", BuiltinKind.Host, 1, 1);
        return r;
    }

    private BuiltinId Register(Entry entry)
    {
        var id = new BuiltinId((uint)_entries.Count);
        _entries.Add(entry);
        _byName.Add(entry.Name, id);
        return id;
    }

    /// <summary>登记宿主内建（名字锁定，执行由宿主接手）。</summary>
    public BuiltinId RegisterHost(string name) => Register(new Entry(name, BuiltinKind.Host, 0, null));

    /// <summary>名字解析（静态解析结论的运行时余量；CALL-RESOLVE-001）。</summary>
    public BuiltinId? Lookup(string name) => _byName.TryGetValue(name, out var id) ? id : null;

    public BuiltinKind Kind(BuiltinId id) => EntryAt(id).Kind;

    public string Name(BuiltinId id) => EntryAt(id).Name;

    /// <summary>元数检查（内建实参不符为运行时错误；用户函数静态 E3003）。</summary>
    public RtResult CheckArity(BuiltinId id, IReadOnlyList<Value> args)
    {
        var entry = EntryAt(id);
        var n = (uint)args.Count;
        var ok = n >= entry.MinArgs && (entry.MaxArgs is not { } max || n <= max);
        if (ok)
        {
            return RtResult.Ok;
        }

        var want = entry.MaxArgs is { } m
            ? entry.MinArgs == m
                ? $"{m} 个参数"
                : $"{entry.MinArgs}~{m} 个参数"
            : $"至少 {entry.MinArgs} 个参数";
        return RtResult.Fail(new RtError(RtErrorKind.TypeCast, $"{entry.Name} 需要{want}，得到 {n} 个"));
    }

    private Entry EntryAt(BuiltinId id) =>
        id.Value < (uint)_entries.Count
            ? _entries[(int)id.Value]
            : throw new InvalidOperationException($"BuiltinId({id.Value}) 越界（内部不变量）");
}

/// <summary>运行时自有内建的执行分发（<see cref="Machine.CallBuiltin"/> 的主体）。
/// Host 类别返回 UnsupportedHost——宿主应在路由前拦截。</summary>
public static class BuiltinDispatch
{
    /// <summary>把实参格式化为空格分隔的输出行体（println/print 共用）。</summary>
    public static string FmtArgs(Machine machine, ValueFormatter fmt, IReadOnlyList<Value> args) =>
        string.Join(" ", args.Select(a => fmt.Fmt(machine, a, top: true)));

    /// <summary>内建调用错误：类型不符统一走 <see cref="RtErrorKind.TypeCast"/>（消息含期望类型）。</summary>
    internal static RtError TypeErr(string name, string want, Value got) =>
        new(RtErrorKind.TypeCast, $"{name} 需要{want}，得到 {got.TypeHint()}");

    internal static RtResult<Value> Call(
        Machine machine,
        OutputSink sink,
        ValueFormatter fmt,
        BuiltinId id,
        IReadOnlyList<Value> args)
    {
        var kind = machine.Builtins.Kind(id);
        var name = machine.Builtins.Name(id);
        var arity = machine.Builtins.CheckArity(id, args);
        if (!arity.IsOk)
        {
            return RtResult<Value>.Fail(arity.Error!);
        }

        switch (kind)
        {
            case BuiltinKind.Host:
                return RtResult<Value>.Fail(new RtError(RtErrorKind.UnsupportedHost, $"内建 {name} 由宿主实现（当前调用口不承接）"));
            case BuiltinKind.Println:
                sink.Write(FmtArgs(machine, fmt, args));
                sink.Write("\n");
                return RtResult<Value>.Ok(Value.Unit.Default);
            case BuiltinKind.Print:
                sink.Write(FmtArgs(machine, fmt, args));
                return RtResult<Value>.Ok(Value.Unit.Default);
            case BuiltinKind.Len:
                return BuiltinLen(machine, args);
            case BuiltinKind.Str:
                return RtResult<Value>.Ok(new Value.Str(fmt.Fmt(machine, args[0], top: true)));
            case BuiltinKind.Int:
                return BuiltinInt(args[0]);
            case BuiltinKind.Float:
                return BuiltinFloat(args[0]);
            case BuiltinKind.Keys:
                {
                    var m = ExpectMap("keys", args[0]);
                    if (!m.IsOk)
                    {
                        return RtResult<Value>.Fail(m.Error!);
                    }

                    var keys = machine.Heap.MapKeys(m.Value);
                    return keys.IsOk
                        ? RtResult<Value>.Ok(machine.Heap.AllocSlice(keys.Value))
                        : RtResult<Value>.Fail(keys.Error!);
                }

            case BuiltinKind.Values:
                {
                    var m = ExpectMap("values", args[0]);
                    if (!m.IsOk)
                    {
                        return RtResult<Value>.Fail(m.Error!);
                    }

                    var values = machine.Heap.MapValues(m.Value);
                    return values.IsOk
                        ? RtResult<Value>.Ok(machine.Heap.AllocSlice(values.Value))
                        : RtResult<Value>.Fail(values.Error!);
                }

            case BuiltinKind.Has:
                {
                    var m = ExpectMap("has", args[0]);
                    if (!m.IsOk)
                    {
                        return RtResult<Value>.Fail(m.Error!);
                    }

                    var key = MapKey.FromValue(args[1]);
                    return key is null
                        ? RtResult<Value>.Fail(TypeErr("has", "int/string/bool 键", args[1]))
                        : machine.Heap.MapHas(m.Value, key.Value).Map<Value>(b => new Value.Bool(b));
                }

            case BuiltinKind.Push:
                {
                    var m = ExpectSlice("push", args[0]);
                    if (!m.IsOk)
                    {
                        return RtResult<Value>.Fail(m.Error!);
                    }

                    for (var i = 1; i < args.Count; i++)
                    {
                        var pushed = machine.Heap.SlicePush(m.Value, args[i]);
                        if (!pushed.IsOk)
                        {
                            return RtResult<Value>.Fail(pushed.Error!);
                        }
                    }

                    return RtResult<Value>.Ok(args[0]);
                }

            case BuiltinKind.Assert:
                {
                    var cond = args[0].Truthy();
                    if (!cond.IsOk)
                    {
                        return RtResult<Value>.Fail(TypeErr("assert", "bool 条件", args[0]));
                    }

                    if (cond.Value)
                    {
                        return RtResult<Value>.Ok(Value.Unit.Default);
                    }

                    var msg = args.Count == 2
                        ? $"assert 失败: {fmt.Fmt(machine, args[1], top: true)}"
                        : "assert 失败";
                    return RtResult<Value>.Fail(new RtError(RtErrorKind.Assert, msg));
                }

            case BuiltinKind.Err:
                return args[0] is Value.Str errMsg
                    ? RtResult<Value>.Ok(machine.ErrorValue(errMsg.V))
                    : RtResult<Value>.Fail(TypeErr("err", "string", args[0]));

            case BuiltinKind.Bytes:
                return args[0] is Value.Str s
                    ? RtResult<Value>.Ok(machine.Heap.AllocSlice(
                        Encoding.UTF8.GetBytes(s.V).Select(b => new Value.Int(b)).ToArray()))
                    : RtResult<Value>.Fail(TypeErr("bytes", "string", args[0]));

            case BuiltinKind.Char:
                {
                    if (args[0] is not Value.Int n)
                    {
                        return RtResult<Value>.Fail(TypeErr("char", "int", args[0]));
                    }

                    // Go string(rune(n))：非法码点/代理区 → U+FFFD（证据行为）
                    var cp = n.V;
                    var text = cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)
                        ? "\uFFFD"
                        : new Rune((int)cp).ToString();
                    return RtResult<Value>.Ok(new Value.Str(text));
                }

            case BuiltinKind.MakeChan:
                return RtResult<Value>.Ok(machine.MakeChanValue());

            case BuiltinKind.Send:
                {
                    var ch = ExpectChan(args[0]);
                    if (!ch.IsOk)
                    {
                        return RtResult<Value>.Fail(ch.Error!);
                    }

                    var sent = machine.Chans.Send(ch.Value, args[1]);
                    return sent.IsOk ? RtResult<Value>.Ok(Value.Unit.Default) : RtResult<Value>.Fail(sent.Error!);
                }

            case BuiltinKind.Recv:
                {
                    var ch = ExpectChan(args[0]);
                    if (!ch.IsOk)
                    {
                        return RtResult<Value>.Fail(ch.Error!);
                    }

                    var got = machine.Chans.TryRecv(ch.Value);
                    if (!got.IsOk)
                    {
                        return RtResult<Value>.Fail(got.Error!);
                    }

                    return got.Value.IsValue
                        ? RtResult<Value>.Ok(machine.OkValue(got.Value.Payload!))
                        : RtResult<Value>.Ok(machine.ErrValue(new Value.Str(ChannelTable.ChanEmptyMsg)));
                }

            case BuiltinKind.IsNone:
                {
                    // 证据口径：Option 的 None case → true；非 enum 值 → false
                    var isNone = args[0] is Value.Enum { Case: 1 } e && e.Def == machine.Defs.OptionDef();
                    return RtResult<Value>.Ok(new Value.Bool(isNone));
                }

            default:
                return RtResult<Value>.Fail(RtError.Internal("未知 BuiltinKind（内部不变量）"));
        }
    }

    private static RtResult<ValueId> ExpectSlice(string name, Value v) => v switch
    {
        Value.Slice s => RtResult<ValueId>.Ok(s.Id),
        _ => RtResult<ValueId>.Fail(TypeErr(name, "数组", v)),
    };

    private static RtResult<ValueId> ExpectMap(string name, Value v) => v switch
    {
        Value.Map m => RtResult<ValueId>.Ok(m.Id),
        _ => RtResult<ValueId>.Fail(TypeErr(name, "映射", v)),
    };

    private static RtResult<ChannelId> ExpectChan(Value v) => v switch
    {
        Value.Chan c => RtResult<ChannelId>.Ok(c.Id),
        _ => RtResult<ChannelId>.Fail(TypeErr("channel 操作", "chan", v)),
    };

    private static RtResult<Value> BuiltinLen(Machine machine, IReadOnlyList<Value> args)
    {
        switch (args[0])
        {
            case Value.Str s:
                // lexical-grammar.md：len() 对字符串返回码点数
                return RtResult<Value>.Ok(new Value.Int(Value.CodePointCount(s.V)));
            case Value.Slice slice:
                {
                    var n = machine.Heap.SliceLen(slice.Id);
                    return n.IsOk ? RtResult<Value>.Ok(new Value.Int(n.Value)) : RtResult<Value>.Fail(n.Error!);
                }

            case Value.Map map:
                {
                    var n = machine.Heap.MapLen(map.Id);
                    return n.IsOk ? RtResult<Value>.Ok(new Value.Int(n.Value)) : RtResult<Value>.Fail(n.Error!);
                }

            case Value.Range r:
                // 证据口径：hi <= lo 的区间长度为 0（半开区间）
                return RtResult<Value>.Ok(new Value.Int(r.Hi > r.Lo ? r.Hi - r.Lo : 0));
            default:
                return RtResult<Value>.Fail(TypeErr("len", "string/数组/映射/区间", args[0]));
        }
    }

    private static RtResult<Value> BuiltinInt(Value v)
    {
        switch (v)
        {
            case Value.Int:
                return RtResult<Value>.Ok(v);
            case Value.Float f:
                {
                    // int(f)：NaN → 0；溢出饱和到 int64 边界；其余向零截断
                    // （TYPE-CONV-005 v1.0.1，ADR-0006/RS-006 跨实现硬契约；
                    // C# (long)double 自 .NET Core 3.0 起即该饱和语义）
                    return RtResult<Value>.Ok(new Value.Int((long)f.V));
                }

            case Value.Str s:
                {
                    var trimmed = s.V.Trim();
                    if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        return RtResult<Value>.Ok(new Value.Int(n));
                    }

                    return RtResult<Value>.Fail(new RtError(RtErrorKind.TypeCast, $"无法把 \"{trimmed}\" 转为 int"));
                }

            default:
                return RtResult<Value>.Fail(TypeErr("int()", "int/float/string", v));
        }
    }

    private static RtResult<Value> BuiltinFloat(Value v)
    {
        switch (v)
        {
            case Value.Int i:
                return RtResult<Value>.Ok(new Value.Float(i.V));
            case Value.Float:
                return RtResult<Value>.Ok(v);
            case Value.Str s:
                {
                    var trimmed = s.V.Trim();
                    if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    {
                        return RtResult<Value>.Ok(new Value.Float(f));
                    }

                    return RtResult<Value>.Fail(new RtError(RtErrorKind.TypeCast, $"无法把 \"{trimmed}\" 转为 float"));
                }

            default:
                return RtResult<Value>.Fail(TypeErr("float()", "int/float/string", v));
        }
    }
}
