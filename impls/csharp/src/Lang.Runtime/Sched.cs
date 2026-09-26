using Lang.Core;

namespace Lang.Runtime;

/// <summary>待执行 Task：函数身份 + 已求值的实参快照（EVAL-GO-007：实参在 go 语句处立即求值）。</summary>
public sealed class Task
{
    public FunctionId Func { get; }

    public IReadOnlyList<Value> Args { get; }

    public Task(FunctionId func, IReadOnlyList<Value> args)
    {
        Func = func;
        Args = args;
    }
}

/// <summary>Task 体执行回调（解释器实现；返回值被调度器丢弃）。Task 内运行时错误向上传播（程序错误终止）。</summary>
public interface TaskExec
{
    RtResult<Value> Call(Machine machine, FunctionId func, IReadOnlyList<Value> args);
}

/// <summary>
/// 协作式调度器（TASK-MODEL-001 / TASK-SPAWN-002 / TASK-DETERMINISM-003）：FIFO 任务队列。
/// go f(args) 启动 Task：实参在 go 语句处立即求值快照；Task 执行至返回，返回值丢弃
/// （fire-and-forget）；不可 join、不可取消、无 Task 标识。
/// **确定子集**（解释器面）：spawn 的 Task 在当前语句点同步执行完毕后当前 Task 继续
/// （等价于直接调用），因此调度队列在任意语句点恒空；「执行至排空」的循环在
/// <see cref="Machine.RunPending"/>。
/// </summary>
public sealed class Scheduler
{
    private readonly Queue<Task> _tasks = new();

    /// <summary>挂起 Task 数（同步子集下语句点恒为 0；诊断/测试用）。</summary>
    public int Pending => _tasks.Count;

    /// <summary>入队一个 Task。</summary>
    public void Enqueue(Task task) => _tasks.Enqueue(task);

    /// <summary>出队下一个 Task（null = 队列空）。</summary>
    public Task? Pop() => _tasks.Count > 0 ? _tasks.Dequeue() : null;
}
