# 并发规范（TASK / CHAN）

语言层定义宿主无关的 **Task/Channel 抽象模型**；`go` 语句与 `chan[T]` 语法保留。各实现以宿主机制承载 Task（Go goroutine / JS 单线程调度 / 解释器协作调度 / Rust future / Python coroutine），但**语义由本文件定义**，宿主行为不得反向定义语言。

## TASK-MODEL-001 Task

`go f(args)` 启动一个 Task：执行 `f(args)` 直至返回，返回值丢弃（fire-and-forget）。Task 不可 join、不可取消、无 Task 标识（v0.9 冻结面）。main 返回即程序退出，不等待未完成 Task 的副作用（→Go 后端实现可在 main 尾部 join 以吸收调度差异——这是实现自由度，不是语义）。

## TASK-SPAWN-002 spawn 语义

- 实参在 go 语句处立即求值（EVAL-GO-007）
- spawn 后当前 Task 继续；后续语句不被阻塞
- **确定性保证**：解释器/→JS 为顺序确定同步子集——spawn 的 Task 在当前语句点同步执行完毕后当前 Task 继续（等价于直接调用）
- **调度自由度**：→Go 可真并行执行；依赖「同步执行」时序的程序非法（不可移植），必须用 channel 建立同步点
- go 的目标须为**顶层函数**（静态拒绝闭包/方法/内建 → E2002）

## TASK-DETERMINISM-003 确定性边界

规范保证的确定性：单 Task 内语句序、channel 的 FIFO 与插入序语义、send/recv 的值序。规范不保证：多 Task 的交错执行序（实现自由度，输出不可移植）。concurrency 类 Conformance Case 只能使用 TASK-SPAWN-002 同步子集形态（如 `go send(ch, v)` 后主 Task `recv`）。

## CHAN-CREATE-001 channel

`makeChan()` 创建缓冲 1024 的 `chan[T]`；T 仅静态文档（运行时装箱 any）。容量为语言常量（v0.9 冻结）。

## CHAN-SEND-002 send

`send(ch, v)` 不阻塞；缓冲满为运行时错误（RUNTIME-CHAN-001）。send 恒返回 `()`。

## CHAN-RECV-003 recv

`recv(ch)` **阻塞直到有值**，返回 `Result[T, error]`：有值 `Ok(v)`（FIFO）；同步确定子集下 recv 时无值且无并发 send 方（程序死锁）→ `Err("channel 空")`；→Go 原生阻塞，确无 send 方时由宿主 panic 兜底（语义等价：程序错误终止）。

## CHAN-CLOSE-004 无 close（冻结）

v0.9 无 channel close/选择/超时。channel 生命周期 = 程序生命周期。

## CHAN-MAP-005 后端映射

| 语义点 | 解释器/IR 解释器 | →Go | →JS |
|--------|------------------|-----|------|
| Task 执行 | 同步（语句点执行完） | 真 goroutine + main 尾 join | 同步（async 立即续跑） |
| chan | 队列模拟（缓冲 1024） | 原生 `chan any` | 队列模拟 |
| recv 空 | Err"channel 空" | 阻塞（死锁宿主兜底） | Err"channel 空" |
| send 满 | 运行时错误 | 运行时错误 | 运行时错误 |
