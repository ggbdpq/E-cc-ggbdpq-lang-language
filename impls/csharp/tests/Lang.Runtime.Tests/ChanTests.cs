namespace Lang.Runtime.Tests;

/// <summary>
/// channel 表（specs/concurrency.md CHAN-CREATE-001 ~ CHAN-CLOSE-004）：
/// 缓冲 1024、send 不阻塞、FIFO、空渠道冻结文本、载荷值语义（VALUE-TASKSHARE-007）。
/// </summary>
public class ChanTests
{
    // —— CHAN-RECV-003 FIFO / 对抗重点：single producer FIFO ——

    [Fact]
    public void SingleProducer_Fifo()
    {
        // 手算：send 1、2、3 → recv 依次得 1、2、3（先进先出）
        var chans = new ChannelTable();
        var ch = chans.Make();
        T.Ok(chans.Send(ch, T.I(1)));
        T.Ok(chans.Send(ch, T.I(2)));
        T.Ok(chans.Send(ch, T.I(3)));
        Assert.Equal(3, T.Ok(chans.Len(ch)));
        Assert.Equal(RecvOutcome.Of(T.I(1)), T.Ok(chans.TryRecv(ch)));
        Assert.Equal(RecvOutcome.Of(T.I(2)), T.Ok(chans.TryRecv(ch)));
        Assert.Equal(RecvOutcome.Of(T.I(3)), T.Ok(chans.TryRecv(ch)));
        Assert.Equal(RecvOutcome.Empty, T.Ok(chans.TryRecv(ch)));
    }

    [Fact]
    public void Fifo_AstralPayloadsInOrder()
    {
        var chans = new ChannelTable();
        var ch = chans.Make();
        T.Ok(chans.Send(ch, T.S("🦀")));
        T.Ok(chans.Send(ch, T.S("𝕏")));
        T.Ok(chans.Send(ch, T.S("中")));
        Assert.Equal(RecvOutcome.Of(T.S("🦀")), T.Ok(chans.TryRecv(ch)));
        Assert.Equal(RecvOutcome.Of(T.S("𝕏")), T.Ok(chans.TryRecv(ch)));
        Assert.Equal(RecvOutcome.Of(T.S("中")), T.Ok(chans.TryRecv(ch)));
    }

    // —— VALUE-TASKSHARE-007：channel 载荷按值语义复制/共享 ——

    [Fact]
    public void SendPayload_StructCopied_ContainerShared()
    {
        var chans = new ChannelTable();
        var heap = new HeapArena();
        var arr = heap.AllocSlice(new Value[] { T.I(1) });
        var ch = chans.Make();

        // 容器载荷：send 后对堆的 push 对队列中的载荷可见（共享索引）
        T.Ok(chans.Send(ch, arr));
        T.Ok(heap.SlicePush(new ValueId(0), T.I(2)));
        var got = T.Ok(chans.TryRecv(ch));
        Assert.True(got.IsValue);
        Assert.Equal(2, T.Ok(heap.SliceLen(T.IdOf(got.Payload!))));

        // struct 载荷：入队为独立快照，改源（写新值）不影响队列中的副本
        var p = new StructVal(new StructDefId(0), new Value[] { T.I(1) });
        T.Ok(chans.Send(ch, new Value.Struct(p)));
        var mutated = new Value.Struct(new StructVal(new StructDefId(0), new Value[] { T.I(99) }));
        var gotStruct = T.Ok(chans.TryRecv(ch));
        Assert.Equal(new Value.Struct(p), gotStruct.Payload);
        Assert.NotEqual(mutated, gotStruct.Payload);
    }

    // —— RUNTIME-CHAN-001 缓冲 1024 ——

    [Fact]
    public void SendFull_AtCapacity()
    {
        var chans = new ChannelTable();
        var ch = chans.Make();
        for (long i = 0; i < ChannelTable.ChanCap; i++)
        {
            T.Ok(chans.Send(ch, T.I(i)));
        }

        Assert.Equal(ChannelTable.ChanCap, T.Ok(chans.Len(ch)));
        var err = T.Err(chans.Send(ch, T.I(-1)));
        Assert.Equal(RtErrorKind.ChanFull, err.Kind);
        // 满后原缓冲不动：recv 仍从 0 起 FIFO
        Assert.Equal(RecvOutcome.Of(T.I(0)), T.Ok(chans.TryRecv(ch)));
    }

    // —— CHAN-RECV-003 空渠道 ——

    [Fact]
    public void RecvEmpty_ReportsEmptyOutcome_AndFrozenText()
    {
        var chans = new ChannelTable();
        var ch = chans.Make();
        Assert.Equal(RecvOutcome.Empty, T.Ok(chans.TryRecv(ch)));
        // 规范文本逐字冻结（程序可 println 的 Err 载荷）
        Assert.Equal("channel 空", ChannelTable.ChanEmptyMsg);
    }
}
