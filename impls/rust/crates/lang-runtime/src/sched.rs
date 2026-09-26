//! 协作式调度器（TASK-MODEL-001 / TASK-SPAWN-002 / TASK-DETERMINISM-003）。
//!
//! `go f(args)` 启动 Task：实参在 go 语句处**立即求值快照**（EVAL-GO-007，
//! args 由解释器求值后以 owned `Vec<Value>` 传入）；Task 执行至返回，返回值
//! 丢弃（fire-and-forget）；不可 join、不可取消、无 Task 标识。
//!
//! **确定子集**（解释器/→JS 面）：spawn 的 Task 在当前语句点同步执行完毕后
//! 当前 Task 继续（等价于直接调用）。因此调度队列在任意语句点恒空；
//! `Machine::recv` 先排空队列再查渠道（协作让位点），同步子集下不改变语义。
//!
//! 函数体执行由宿主（解释器）经 [`TaskExec`] 回调——运行时不知道 IR 形状。

use std::collections::VecDeque;

use lang_core::FunctionId;

use crate::error::RtResult;
use crate::machine::Machine;
use crate::value::Value;

/// 待执行 Task：函数身份 + 已求值的实参快照。
#[derive(Clone, Debug)]
pub struct Task {
    pub func: FunctionId,
    pub args: Vec<Value>,
}

/// Task 体执行回调（解释器实现；返回值被调度器丢弃）。
pub trait TaskExec {
    /// 在 machine 上调用函数。Task 内运行时错误向上传播（程序错误终止）。
    fn call(
        &mut self,
        machine: &mut Machine,
        func: FunctionId,
        args: Vec<Value>,
    ) -> RtResult<Value>;
}

/// 调度器：FIFO 任务队列。
///
/// 队列的入队/出队由 [`Scheduler`] 提供；「执行至排空」的循环在
/// `Machine::run_pending`——需要同时可变访问 machine 与宿主 exec，
/// 从 Machine 方法内做字段级拆分借用。
#[derive(Debug, Default)]
pub struct Scheduler {
    tasks: VecDeque<Task>,
}

impl Scheduler {
    pub fn new() -> Scheduler {
        Scheduler {
            tasks: VecDeque::new(),
        }
    }

    /// 挂起 Task 数（同步子集下语句点恒为 0；诊断/测试用）。
    pub fn pending(&self) -> usize {
        self.tasks.len()
    }

    /// 入队一个 Task。
    pub fn enqueue(&mut self, task: Task) {
        self.tasks.push_back(task);
    }

    /// 出队下一个 Task（None = 队列空）。
    pub fn pop(&mut self) -> Option<Task> {
        self.tasks.pop_front()
    }
}
