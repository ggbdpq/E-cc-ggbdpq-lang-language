//! lang-runtime —— 值模型与执行语义地基（Agent 4）。
//!
//! 行为真值源：`specs/values.md`、`specs/expressions.md`（EVAL-*）、
//! `specs/concurrency.md`、`specs/runtime.md`；实现证据（只读）：
//! `impls/typescript/src/interp/`、`impls/go/internal/interp/`。
//!
//! # Runtime Model（arena + 索引，禁 Rc<RefCell>/dyn Any）
//!
//! | 语义对象 | 承载 | 语义依据 |
//! |---|---|---|
//! | struct / enum | 内联于 [`Value`]，`clone` 即深拷贝 | VALUE-COPY-001 / VALUE-ELEM-009 |
//! | `[T]` / `map[K]V` | [`HeapArena`] 槽位，值只持 [`ValueId`] | VALUE-SHARE-002 / VALUE-MAP-006 |
//! | 变量/闭包环境 | [`EnvArena`] cell（共享 EnvId = 按引用捕获） | VALUE-CAPTURE-004 |
//! | 引用 `&x` | [`Value::Ref`]([`Place`]) 显式建模 | VALUE-REF-003 |
//! | channel | [`ChannelTable`]（缓冲 1024） | CHAN-CREATE-001 |
//! | Task | [`Scheduler`] 同步确定子集 | TASK-SPAWN-002 |
//!
//! 解释器（lang-interp）持有 [`Machine`]，按 EVAL-* 顺序驱动；输出格式化
//! 细节（float 安全区/%q/JSON 字节契约）是 A5 领地，本 crate 经
//! [`ValueFormatter`] 钩子消费。

#![forbid(unsafe_code)]

pub mod builtin;
pub mod chan;
pub mod defs;
pub mod env;
pub mod error;
pub mod heap;
pub mod machine;
pub mod place;
pub mod sched;
pub mod value;

pub use builtin::{
    BuiltinId, BuiltinKind, BuiltinRegistry, OutputSink, StdoutSink, ValueFormatter,
};
pub use chan::{CHAN_CAP, CHAN_EMPTY_MSG, ChannelId, ChannelTable, RecvOutcome};
pub use defs::{DefTables, EnumDef, EnumDefId, StructDef, StructDefId};
pub use env::{Cell, EnvArena, EnvId};
pub use error::{RtError, RtErrorKind, RtResult};
pub use heap::{HeapArena, HeapObj, MapObj, MapSlot, ValueId};
pub use machine::Machine;
pub use place::Place;
pub use sched::{Scheduler, Task, TaskExec};
pub use value::{MapKey, StructVal, Value};
