//! 赋值目标位置（Place）——显式建模的左值地址（EVAL-ASSIGN-003）。
//!
//! Place 是运行时「地址组件」的物化：解释器按规范先求值 target 的地址组件
//! （Ident 绑定；`obj.f` 的 obj；`a[i]` 的 obj、i），得到 Place 后再求值右值、
//! 最后经 `Machine::place_store` 写入。读写操作见 `machine` 模块。
//!
//! - `Var`：变量 cell（env + 槽位）。
//! - `Element`：slice 槽位（容器引用共享，直写）。
//! - `MapEntry`：映射条目（覆盖保插入位）。
//! - `Field`：struct 字段，**经 base 读出副本 → 改字段 → 写回 base**
//!   （VALUE-ELEM-009：`xs[i].f = v` 不共享中间副本，写回槽位对容器别名可见）。
//!
//! 引用（`&x`）不在 Place 构造面：E3012 规定 `&` 仅限变量；写穿引用由
//! `place_store` 解析 `Var` 所存 `Value::Ref` 后落到内层 Place 完成
//! （VALUE-REF-003：经引用读写字段/索引即读写原值）。

use crate::env::EnvId;
use crate::heap::ValueId;
use crate::value::MapKey;

/// 左值地址。
#[derive(Clone, Debug, PartialEq)]
pub enum Place {
    /// 变量 cell。
    Var { env: EnvId, slot: u32 },
    /// slice 元素槽位。
    Element { slice: ValueId, index: u32 },
    /// 映射条目（key 为规范键形）。
    MapEntry { map: ValueId, key: MapKey },
    /// struct 字段：base 落点上的第 index 个字段。
    Field { base: Box<Place>, index: u32 },
}
