namespace Lang.Runtime;

/// <summary>
/// 运行时错误分类（specs/runtime.md RUNTIME-FAIL-001）。
/// 运行时错误 = 程序向 stderr 输出错误消息并以非零退出码终止；分类（kind）是契约，
/// message 文本允许实现差异。语言级可恢复结果（如 recv 空渠道返回 Err 值）不走本模型。
/// </summary>
public enum RtErrorKind
{
    /// <summary>RUNTIME-INDEX-001：数组/字符串索引越界、切片端点非法、映射取不存在键。</summary>
    Index,

    /// <summary>RUNTIME-ARITH-002：整数除零/模零。</summary>
    Arith,

    /// <summary>RUNTIME-EQ-003：==/!= 作用于数组/映射/struct（仅标量与 enum 可比）。</summary>
    Eq,

    /// <summary>RUNTIME-ASSERT-004：assert 条件为 false。</summary>
    Assert,

    /// <summary>RUNTIME-CHAN-001：send 到满 channel。</summary>
    ChanFull,

    /// <summary>RUNTIME-MATCH-001：字面量 match 无臂匹配。</summary>
    MatchNoArm,

    /// <summary>RUNTIME-TYPE-005：any 边界断言失败、内建实参类型不符等运行时类型错。</summary>
    TypeCast,

    /// <summary>RUNTIME-JSON-001：jsonParse 非法 JSON（宿主内建实现时使用）。</summary>
    JsonParse,

    /// <summary>对 let 绑定赋值（静态检漏兜底；正常程序在静态期被 E 码拒绝）。</summary>
    ImmutableAssign,

    /// <summary>对不支持迭代的值做迭代（E3010 的运行时面）。</summary>
    NotIterable,

    /// <summary>作用域错误（重复定义等；正常程序在静态期被拒绝）。</summary>
    Scope,

    /// <summary>由宿主（解释器/后端）实现的内建被路由到运行时调用口。</summary>
    UnsupportedHost,

    /// <summary>内部不变量被破坏（非用户源码错误）。</summary>
    Internal,
}

/// <summary>运行时错误：分类 + 消息。受控错误类型——禁用 C# 异常表达用户源码错误。</summary>
public sealed record RtError(RtErrorKind Kind, string Message)
{
    /// <summary>内部不变量破坏（非用户源码错误）。</summary>
    public static RtError Internal(string message) => new(RtErrorKind.Internal, message);

    public override string ToString() => Message;
}

/// <summary>无载荷运行时结果：Ok 或 Error（Rust RtResult&lt;()&gt; 同构）。</summary>
public readonly struct RtResult
{
    public RtError? Error { get; }

    public bool IsOk => Error is null;

    private RtResult(RtError error) => Error = error;

    public static RtResult Ok => default;

    public static RtResult Fail(RtError error) => new(error);
}

/// <summary>带载荷运行时结果：Value 或 Error（受控错误通道，不抛异常）。</summary>
public readonly struct RtResult<T>
{
    public T Value { get; }

    public RtError? Error { get; }

    public bool IsOk => Error is null;

    private RtResult(T value)
    {
        Value = value;
        Error = null;
    }

    private RtResult(RtError error)
    {
        Value = default!;
        Error = error;
    }

    public static RtResult<T> Ok(T value) => new(value);

    public static RtResult<T> Fail(RtError error) => new(error);

    /// <summary>丢弃载荷只看成败。</summary>
    public RtResult Ignore() => IsOk ? RtResult.Ok : RtResult.Fail(Error!);

    /// <summary>成功时映射载荷。</summary>
    public RtResult<R> Map<R>(Func<T, R> f) => IsOk ? RtResult<R>.Ok(f(Value)) : RtResult<R>.Fail(Error!);

    /// <summary>成功时接续下一个可能失败的操作。</summary>
    public RtResult<R> Then<R>(Func<T, RtResult<R>> f) => IsOk ? f(Value) : RtResult<R>.Fail(Error!);
}
