namespace Lang.Runtime;

/// <summary>channel 身份（ChannelTable 下标 newtype）。</summary>
public readonly record struct ChannelId(uint Value);

/// <summary>recv 结果：有值 / 空（空 = 同步子集死锁面，由上层构造 Err(ChanEmptyMsg) 值）。</summary>
public sealed record RecvOutcome(Value? Payload, bool IsValue)
{
    public static RecvOutcome Of(Value v) => new(v, true);

    public static RecvOutcome Empty { get; } = new(null, false);
}

/// <summary>单条 channel：FIFO 缓冲（Queue 迭代序不外泄，出队即 FIFO）。</summary>
internal sealed class Channel
{
    internal readonly Queue<Value> Buf = new();
}

/// <summary>
/// channel 表（CHAN-CREATE-001 ~ CHAN-CLOSE-004，冻结面）。
/// - 缓冲 1024（语言常量）；send 不阻塞，满 → RUNTIME-CHAN-001；
/// - recv FIFO；同步确定子集下空且无并发 send 方 = 程序死锁面，
///   语言层表现为 Err("channel 空") 值（CHAN-RECV-003 / RUNTIME-CHAN-002）；
/// - 无 close/选择/超时；channel 生命周期 = 程序生命周期。
/// 载荷按值语义传递（VALUE-TASKSHARE-007）：入队即 CloneValue 快照
/// （struct 拷贝、容器共享索引——与其他传值路径一致）。
/// </summary>
public sealed class ChannelTable
{
    /// <summary>channel 缓冲容量（CHAN-CREATE-001 语言常量，v0.9 冻结）。</summary>
    public const int ChanCap = 1024;

    /// <summary>recv 空渠道时 Err 载荷的规范文本（CHAN-RECV-003 逐字冻结——它是程序可打印的值，不是内部消息）。</summary>
    public const string ChanEmptyMsg = "channel 空";

    private readonly List<Channel> _chans = new();

    /// <summary>makeChan()：缓冲 ChanCap 的 channel。</summary>
    public ChannelId Make()
    {
        var id = new ChannelId((uint)_chans.Count);
        _chans.Add(new Channel());
        return id;
    }

    private RtResult<Channel> ChanAt(ChannelId id) =>
        id.Value < (uint)_chans.Count
            ? RtResult<Channel>.Ok(_chans[(int)id.Value])
            : RtResult<Channel>.Fail(RtError.Internal($"ChannelId({id.Value}) 越界"));

    /// <summary>send(ch, v)：不阻塞；满 → <see cref="RtErrorKind.ChanFull"/>（RUNTIME-CHAN-001）。载荷入队即快照。</summary>
    public RtResult Send(ChannelId id, Value v)
    {
        var r = ChanAt(id);
        if (!r.IsOk)
        {
            return RtResult.Fail(r.Error!);
        }

        var buf = r.Value.Buf;
        if (buf.Count >= ChanCap)
        {
            return RtResult.Fail(new RtError(RtErrorKind.ChanFull, $"向已满的 channel 发送（容量 {ChanCap}）"));
        }

        buf.Enqueue(v.CloneValue());
        return RtResult.Ok;
    }

    /// <summary>recv 的存储面：FIFO 出队；空 → <see cref="RecvOutcome.Empty"/>。</summary>
    public RtResult<RecvOutcome> TryRecv(ChannelId id)
    {
        var r = ChanAt(id);
        if (!r.IsOk)
        {
            return RtResult<RecvOutcome>.Fail(r.Error!);
        }

        return r.Value.Buf.TryDequeue(out var v)
            ? RtResult<RecvOutcome>.Ok(RecvOutcome.Of(v))
            : RtResult<RecvOutcome>.Ok(RecvOutcome.Empty);
    }

    /// <summary>当前缓冲长度（测试/诊断用）。</summary>
    public RtResult<int> Len(ChannelId id) => ChanAt(id).Map(c => c.Buf.Count);
}
