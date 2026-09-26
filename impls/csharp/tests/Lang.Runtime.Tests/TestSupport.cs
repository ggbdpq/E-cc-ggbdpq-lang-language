using System.Globalization;
using Lang.Core;

namespace Lang.Runtime.Tests;

/// <summary>测试辅助：结果解包、值构造缩写、堆索引提取。</summary>
internal static class T
{
    /// <summary>解包 Ok 结果；失败即断言失败并显示错误消息。</summary>
    public static TVal Ok<TVal>(RtResult<TVal> r, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(r))] string? expr = null)
    {
        Assert.True(r.IsOk, $"期望 Ok（{expr}），得到：{r.Error?.Message}");
        return r.Value;
    }

    /// <summary>断言无载荷结果为 Ok。</summary>
    public static void Ok(RtResult r, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(r))] string? expr = null) =>
        Assert.True(r.IsOk, $"期望 Ok（{expr}），得到：{r.Error?.Message}");

    /// <summary>断言失败结果并取错误（kind 分类是契约）。</summary>
    public static RtError Err(RtResult r)
    {
        Assert.False(r.IsOk, "期望错误结果，得到 Ok");
        return r.Error!;
    }

    public static RtError Err<TVal>(RtResult<TVal> r)
    {
        Assert.False(r.IsOk, "期望错误结果，得到 Ok");
        return r.Error!;
    }

    public static Value.Int I(long v) => new(v);

    public static Value.Str S(string v) => new(v);

    /// <summary>从堆容器值取索引（测试辅助）。</summary>
    public static ValueId IdOf(Value v) => v switch
    {
        Value.Slice s => s.Id,
        Value.Map m => m.Id,
        var other => throw new InvalidOperationException($"测试辅助：期望堆容器值，得到 {other.TypeHint()}"),
    };

    /// <summary>取映射键的纯文本形（测试断言用；Nullable 值类型不吃 null-forgiving，集中在此拆）。</summary>
    public static string KeyPlain(Value v) =>
        MapKey.FromValue(v) is { } k ? k.Plain() : throw new InvalidOperationException($"测试辅助：值不是合法映射键：{v.TypeHint()}");
}

/// <summary>测试用极简格式化器（正式格式化是 A5 领地；这里只保证确定性拼接）。</summary>
public sealed class TestFmt : ValueFormatter
{
    public string Fmt(Machine machine, Value v, bool top) => v switch
    {
        Value.Int i => i.V.ToString(CultureInfo.InvariantCulture),
        Value.Float f => f.V.ToString("R", CultureInfo.InvariantCulture),
        Value.Bool b => b.V ? "true" : "false",
        Value.Str s => top ? s.V : "\"" + s.V + "\"",
        Value.Unit => "()",
        var other => $"<{other.TypeHint()}>",
    };
}

/// <summary>缓冲输出汇。</summary>
public sealed class VecSink : OutputSink
{
    public string Text { get; private set; } = string.Empty;

    public void Write(string s) => Text += s;
}

/// <summary>记录调用的 Task 执行体（可选拒绝执行）。</summary>
public sealed class RecExec : TaskExec
{
    public List<(FunctionId Func, IReadOnlyList<Value> Args)> Calls { get; } = new();

    public bool Fail { get; init; }

    public RtResult<Value> Call(Machine machine, FunctionId func, IReadOnlyList<Value> args)
    {
        if (Fail)
        {
            return RtResult<Value>.Fail(new RtError(RtErrorKind.Assert, "task 内失败"));
        }

        Calls.Add((func, args));
        return RtResult<Value>.Ok(new Value.Int(99)); // fire-and-forget：此返回值应被调度器丢弃
    }
}
