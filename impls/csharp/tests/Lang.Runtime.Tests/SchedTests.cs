using Lang.Core;

namespace Lang.Runtime.Tests;

/// <summary>
/// 调度器与 spawn 语义（specs/concurrency.md TASK-MODEL-001 / TASK-SPAWN-002 / TASK-DETERMINISM-003
/// + specs/expressions.md EVAL-GO-007）：实参快照、语句点同步排空、嵌套 spawn、错误上抛、返回值丢弃。
/// </summary>
public class SchedTests
{
    // —— TASK-SPAWN-002：同步执行 + 实参快照 ——

    [Fact]
    public void Spawn_RunsToCompletionAtStatementPoint()
    {
        var m = new Machine();
        var f = m.RegisterFunction("sendOne");
        var exec = new RecExec();
        // 实参在 go 语句处求值快照（此处由测试直接给列表）
        T.Ok(m.Spawn(exec, f, new Value[] { T.I(1), T.S("x") }));
        // spawn 返回即执行完毕（同步子集）
        Assert.Single(exec.Calls);
        Assert.Equal(f, exec.Calls[0].Func);
        Assert.Equal(new Value[] { T.I(1), T.S("x") }, exec.Calls[0].Args);
        Assert.Equal(0, m.Sched.Pending);
    }

    [Fact]
    public void Spawn_ArgsAreSnapshot_NotAliased()
    {
        // EVAL-GO-007/VALUE-TASKSHARE-007：go 的实参是快照——调用方列表清空不影响已入队 Task
        var m = new Machine();
        var f = m.RegisterFunction("work");
        var args = new List<Value> { T.I(1), T.S("🦀𝕏") };
        var exec = new RecExec();
        T.Ok(m.Spawn(exec, f, args));
        args.Clear();
        Assert.Equal(T.I(1), exec.Calls[0].Args[0]);
        Assert.Equal(T.S("🦀𝕏"), exec.Calls[0].Args[1]);
    }

    [Fact]
    public void Spawn_DrainsNestedTasks()
    {
        var m = new Machine();
        var f = m.RegisterFunction("chain");
        var exec = new NestedExec(3);
        T.Ok(m.Spawn(exec, f, Array.Empty<Value>()));
        // 手算：depth=3 → 恰好 3 次调用后排空
        Assert.Equal(3, exec.Calls);
        Assert.Equal(0, m.Sched.Pending);
    }

    private sealed class NestedExec : TaskExec
    {
        public int Depth { get; private set; }

        public int Calls { get; private set; }

        public NestedExec(int depth) => Depth = depth;

        public RtResult<Value> Call(Machine machine, FunctionId func, IReadOnlyList<Value> args)
        {
            Calls++;
            if (Depth > 1)
            {
                // Task 体内再 spawn：由调度循环消化（同一 exec 复用，计数共享）
                Depth--;
                var r = machine.Spawn(this, func, Array.Empty<Value>());
                if (!r.IsOk)
                {
                    return RtResult<Value>.Fail(r.Error!);
                }
            }

            return RtResult<Value>.Ok(Value.Unit.Default);
        }
    }

    [Fact]
    public void TaskError_Propagates_AndResultDropped()
    {
        var m = new Machine();
        var f = m.RegisterFunction("boom");
        var exec = new RecExec { Fail = true };
        var err = T.Err(m.Spawn(exec, f, Array.Empty<Value>()));
        Assert.Equal(RtErrorKind.Assert, err.Kind);
        // 返回值 Int(99) 被调度器丢弃：无返回值通道、无挂起
        Assert.Empty(exec.Calls);
        Assert.Equal(0, m.Sched.Pending);
    }

    // —— 协作让位点：recv 先排空挂起 Task 再出队 ——

    [Fact]
    public void Recv_YieldsToPendingTasksFirst()
    {
        // 直接入队（绕过 Spawn 的即时排空），recv 时先让挂起 Task 跑：
        // Task 向 ch send 7 → recv 拿到 Ok(7)
        var m = new Machine();
        var ch = m.MakeChanValue();
        var chId = Assert.IsType<Value.Chan>(ch).Id;
        var exec = new SendThenDie(chId);
        m.Sched.Enqueue(new Task(m.RegisterFunction("producer"), Array.Empty<Value>()));
        m.Sched.Enqueue(new Task(m.RegisterFunction("second"), Array.Empty<Value>()));
        var got = T.Ok(m.Recv(exec, chId));
        Assert.True(got.IsValue);
        Assert.Equal(T.I(7), got.Payload);
        // 队列被全部排空（两个 Task 都跑过）
        Assert.Equal(2, exec.Calls);
        Assert.Equal(0, m.Sched.Pending);
    }

    private sealed class SendThenDie(ChannelId ch) : TaskExec
    {
        public int Calls { get; private set; }

        public RtResult<Value> Call(Machine machine, FunctionId func, IReadOnlyList<Value> args)
        {
            Calls++;
            var r = machine.Chans.Send(ch, T.I(7));
            return r.IsOk ? RtResult<Value>.Ok(Value.Unit.Default) : RtResult<Value>.Fail(r.Error!);
        }
    }
}
