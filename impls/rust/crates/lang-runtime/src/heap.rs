//! 堆 arena：引用语义容器的槽位分配（VALUE-SHARE-002 / VALUE-MAP-006）。
//!
//! slice 与 map 是语言中仅有的引用语义容器，以 arena 索引（[`ValueId`]）承载：
//! 值只持索引，clone 即共享别名；原位变更对全部别名可见。struct/enum 为内联值
//! （`Value::clone` = VALUE-COPY-001），不入堆；channel/env 各有专属表
//! （生命周期与访问模式不同，类型不混用，无 `dyn Any`）。
//!
//! 解释器进程生命周期 = 程序生命周期，arena 不做回收（GC 委托宿主进程退出）。

use indexmap::IndexMap;

use crate::error::{RtError, RtErrorKind, RtResult};
use crate::value::{MapKey, Value};

/// 堆槽位身份（arena 下标 newtype）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ValueId(pub u32);

impl From<ValueId> for usize {
    fn from(v: ValueId) -> usize {
        v.0 as usize
    }
}

impl std::fmt::Display for ValueId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}

/// 映射槽位：插入序键 + 原键值（输出 `{k: v}` 与 keys() 需要原始键形态）。
#[derive(Clone, Debug, PartialEq)]
pub struct MapSlot {
    /// 首次插入时的原键值（后续覆盖不改写）。
    pub key: Value,
    /// 当前映射值。
    pub value: Value,
}

/// 保插入序的映射体（VALUE-MAP-006）。
#[derive(Clone, Debug, Default, PartialEq)]
pub struct MapObj {
    entries: IndexMap<MapKey, MapSlot>,
}

impl MapObj {
    pub fn new() -> MapObj {
        MapObj {
            entries: IndexMap::new(),
        }
    }

    /// 条目数。
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// 取值（不含原键）。
    pub fn get(&self, key: &MapKey) -> Option<&Value> {
        self.entries.get(key).map(|s| &s.value)
    }

    /// 取值（可变；仅运行时内部写路径使用）。
    pub fn get_mut(&mut self, key: &MapKey) -> Option<&mut Value> {
        self.entries.get_mut(key).map(|s| &mut s.value)
    }

    /// 插入/覆盖：已存在的键保持原插入位与原键形态（VALUE-MAP-006），
    /// 新键追加到末尾。
    pub fn insert(&mut self, key: MapKey, original: Value, value: Value) {
        match self.entries.get_mut(&key) {
            Some(slot) => slot.value = value,
            None => {
                self.entries.insert(
                    key,
                    MapSlot {
                        key: original,
                        value,
                    },
                );
            }
        }
    }

    /// 是否含键（`has()` 探测，RUNTIME-INDEX-001 的唯一合法探测方式）。
    pub fn contains(&self, key: &MapKey) -> bool {
        self.entries.contains_key(key)
    }

    /// 插入序键值对迭代。
    pub fn iter(&self) -> impl Iterator<Item = (&Value, &Value)> {
        self.entries.values().map(|s| (&s.key, &s.value))
    }
}

/// 堆槽位对象：当前仅引用语义容器；struct/enum 内联于 [`Value`]。
#[derive(Clone, Debug, PartialEq)]
pub enum HeapObj {
    /// `[T]` 容器体。
    Slice(Vec<Value>),
    /// `map[K]V` 容器体（插入序）。
    Map(MapObj),
}

/// 堆 arena：只增不减的槽位表。
#[derive(Debug, Default)]
pub struct HeapArena {
    slots: Vec<HeapObj>,
}

impl HeapArena {
    pub fn new() -> HeapArena {
        HeapArena { slots: Vec::new() }
    }

    /// 分配 slice 槽位，返回持索引的值。
    pub fn alloc_slice(&mut self, items: Vec<Value>) -> Value {
        let id = ValueId(self.slots.len() as u32);
        self.slots.push(HeapObj::Slice(items));
        Value::Slice(id)
    }

    /// 分配 map 槽位，返回持索引的值。
    pub fn alloc_map(&mut self, obj: MapObj) -> Value {
        let id = ValueId(self.slots.len() as u32);
        self.slots.push(HeapObj::Map(obj));
        Value::Map(id)
    }

    fn slot(&self, id: ValueId) -> RtResult<&HeapObj> {
        self.slots
            .get(usize::from(id))
            .ok_or_else(|| RtError::internal(format!("ValueId({}) 越界", usize::from(id))))
    }

    fn slot_mut(&mut self, id: ValueId) -> RtResult<&mut HeapObj> {
        let n = self.slots.len();
        self.slots.get_mut(usize::from(id)).ok_or_else(|| {
            RtError::internal(format!(
                "ValueId({}) 越界（堆共 {} 槽）",
                usize::from(id),
                n
            ))
        })
    }

    /// 槽位种类是否匹配（运行时值自静态类型来，不匹配 = 内部不变量破坏）。
    fn kind_mismatch(id: ValueId, want: &str, got: &HeapObj) -> RtError {
        let got = match got {
            HeapObj::Slice(_) => "数组",
            HeapObj::Map(_) => "映射",
        };
        RtError::internal(format!(
            "ValueId({}) 期望 {}，实际 {}",
            usize::from(id),
            want,
            got
        ))
    }

    pub fn slice(&self, id: ValueId) -> RtResult<&Vec<Value>> {
        match self.slot(id)? {
            HeapObj::Slice(items) => Ok(items),
            other => Err(Self::kind_mismatch(id, "数组", other)),
        }
    }

    pub fn slice_mut(&mut self, id: ValueId) -> RtResult<&mut Vec<Value>> {
        match self.slot_mut(id)? {
            HeapObj::Slice(items) => Ok(items),
            other => Err(Self::kind_mismatch(id, "数组", other)),
        }
    }

    pub fn map(&self, id: ValueId) -> RtResult<&MapObj> {
        match self.slot(id)? {
            HeapObj::Map(m) => Ok(m),
            other => Err(Self::kind_mismatch(id, "映射", other)),
        }
    }

    pub fn map_mut(&mut self, id: ValueId) -> RtResult<&mut MapObj> {
        match self.slot_mut(id)? {
            HeapObj::Map(m) => Ok(m),
            other => Err(Self::kind_mismatch(id, "映射", other)),
        }
    }

    // —— slice 操作 ——

    /// 长度。
    pub fn slice_len(&self, id: ValueId) -> RtResult<usize> {
        Ok(self.slice(id)?.len())
    }

    /// 原位追加（VALUE-SHARE-002：`push(arr, v)` 对所有别名可见）。
    pub fn slice_push(&mut self, id: ValueId, v: Value) -> RtResult<()> {
        self.slice_mut(id)?.push(v);
        Ok(())
    }

    /// 读元素：clone 返回（VALUE-ELEM-009 读出即副本；struct 元素与源槽位脱钩）。
    /// 越界 → `RtErrorKind::Index`（RUNTIME-INDEX-001）。
    pub fn slice_get(&self, id: ValueId, index: usize) -> RtResult<Value> {
        let items = self.slice(id)?;
        match items.get(index) {
            Some(v) => Ok(v.clone()),
            None => Err(RtError::new(
                RtErrorKind::Index,
                format!("索引 {} 越界（长度 {}）", index, items.len()),
            )),
        }
    }

    /// 写槽位。越界 → `RtErrorKind::Index`。
    pub fn slice_set(&mut self, id: ValueId, index: usize, v: Value) -> RtResult<()> {
        let len = self.slice(id)?.len();
        if index >= len {
            return Err(RtError::new(
                RtErrorKind::Index,
                format!("索引 {} 越界（长度 {}）", index, len),
            ));
        }
        self.slice_mut(id)?[index] = v;
        Ok(())
    }

    // —— map 操作 ——

    /// 条目数。
    pub fn map_len(&self, id: ValueId) -> RtResult<usize> {
        Ok(self.map(id)?.len())
    }

    /// 取值（`m[k]`）；键不存在 → `RtErrorKind::Index`（RUNTIME-INDEX-001，
    /// 合法探测只有 `has()`）。
    pub fn map_get(&self, id: ValueId, key: &MapKey) -> RtResult<Value> {
        match self.map(id)?.get(key) {
            Some(v) => Ok(v.clone()),
            None => Err(RtError::new(
                RtErrorKind::Index,
                format!("映射不存在的键 {}", key.plain()),
            )),
        }
    }

    /// `has(m, k)` 探测。
    pub fn map_has(&self, id: ValueId, key: &MapKey) -> RtResult<bool> {
        Ok(self.map(id)?.contains(key))
    }

    /// 插入/覆盖（保持插入位，VALUE-MAP-006）。键类型非法 →
    /// `RtErrorKind::TypeCast`（E3014 的运行时面）。
    pub fn map_insert(&mut self, id: ValueId, key: &Value, value: Value) -> RtResult<()> {
        let k = MapKey::from_value(key).ok_or_else(|| {
            RtError::new(
                RtErrorKind::TypeCast,
                format!("映射键须为 int/string/bool，得到 {}", key.type_hint()),
            )
        })?;
        self.map_mut(id)?.insert(k, key.clone(), value);
        Ok(())
    }

    /// 插入序键列表（keys()/for-in 用；返回原键值副本）。
    pub fn map_keys(&self, id: ValueId) -> RtResult<Vec<Value>> {
        Ok(self.map(id)?.iter().map(|(k, _)| k.clone()).collect())
    }

    /// 插入序值列表（values()/for-in 用）。
    pub fn map_values(&self, id: ValueId) -> RtResult<Vec<Value>> {
        Ok(self.map(id)?.iter().map(|(_, v)| v.clone()).collect())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::defs::StructDefId;
    use crate::value::StructVal;

    /// 从值中取出堆索引（测试辅助；heap API 一律以 ValueId 为参数）。
    fn id_of(v: &Value) -> ValueId {
        match v {
            Value::Slice(id) | Value::Map(id) => *id,
            other => panic!("测试辅助：期望堆容器值，得到 {:?}", other),
        }
    }

    // —— VALUE-SHARE-002 / conformance: containers-shared-aliasing ——

    #[test]
    fn slice_alias_sees_inplace_push() {
        // let a = [1]; let b = a; push(b, 2) → a、b 同见 [1, 2]
        let mut heap = HeapArena::new();
        let a = heap.alloc_slice(vec![Value::Int(1)]);
        let b = a.clone(); // 引用语义：clone 即共享同一槽位
        heap.slice_push(id_of(&b), Value::Int(2)).unwrap();
        // 手算（conformance 期望 stdout "[1, 2] [1, 2]"）：两个别名都看到长度 2
        assert_eq!(heap.slice_len(id_of(&a)).unwrap(), 2);
        assert_eq!(heap.slice_len(id_of(&b)).unwrap(), 2);
        assert_eq!(heap.slice_get(id_of(&a), 1).unwrap(), Value::Int(2));
    }

    // —— VALUE-MAP-006 插入序 ——

    #[test]
    fn map_keeps_insertion_order_and_overwrite_keeps_position() {
        let mut heap = HeapArena::new();
        let m = id_of(&heap.alloc_map(MapObj::new()));
        heap.map_insert(m, &Value::str("c"), Value::Int(3)).unwrap();
        heap.map_insert(m, &Value::str("a"), Value::Int(1)).unwrap();
        heap.map_insert(m, &Value::str("b"), Value::Int(2)).unwrap();
        // 覆盖已存在键：位置不动，值更新
        heap.map_insert(m, &Value::str("a"), Value::Int(10))
            .unwrap();
        let keys = heap.map_keys(m).unwrap();
        let plain: Vec<String> = keys
            .iter()
            .map(|k| MapKey::from_value(k).unwrap().plain())
            .collect();
        assert_eq!(plain, ["c", "a", "b"]);
        let values = heap.map_values(m).unwrap();
        assert_eq!(values, [Value::Int(3), Value::Int(10), Value::Int(2)]);
    }

    #[test]
    fn map_insert_int_and_bool_keys() {
        let mut heap = HeapArena::new();
        let m = id_of(&heap.alloc_map(MapObj::new()));
        heap.map_insert(m, &Value::Int(7), Value::str("seven"))
            .unwrap();
        heap.map_insert(m, &Value::Bool(true), Value::Int(1))
            .unwrap();
        assert!(heap.map_has(m, &MapKey::Int(7)).unwrap());
        assert_eq!(heap.map_get(m, &MapKey::Bool(true)).unwrap(), Value::Int(1));
        assert!(!heap.map_has(m, &MapKey::Int(8)).unwrap());
    }

    #[test]
    fn map_get_missing_key_is_index_error() {
        let mut heap = HeapArena::new();
        let m = id_of(&heap.alloc_map(MapObj::new()));
        heap.map_insert(m, &Value::str("k"), Value::Int(1)).unwrap();
        // RUNTIME-INDEX-001：取不存在键为运行时错误（has() 是唯一合法探测）
        assert_eq!(
            heap.map_get(m, &MapKey::Str("x".into()))
                .unwrap_err()
                .kind(),
            RtErrorKind::Index
        );
    }

    #[test]
    fn map_insert_rejects_non_scalar_key() {
        let mut heap = HeapArena::new();
        let m = id_of(&heap.alloc_map(MapObj::new()));
        let err = heap
            .map_insert(m, &Value::Float(1.0), Value::Unit)
            .unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::TypeCast);
    }

    // —— RUNTIME-INDEX-001 越界 ——

    #[test]
    fn slice_index_bounds() {
        let mut heap = HeapArena::new();
        let a = heap.alloc_slice(vec![Value::Int(1), Value::Int(2)]);
        let id = id_of(&a);
        assert_eq!(heap.slice_get(id, 1).unwrap(), Value::Int(2));
        assert_eq!(
            heap.slice_get(id, 2).unwrap_err().kind(),
            RtErrorKind::Index
        );
        assert_eq!(
            heap.slice_set(id, 5, Value::Int(0)).unwrap_err().kind(),
            RtErrorKind::Index
        );
        heap.slice_set(id, 0, Value::Int(9)).unwrap();
        assert_eq!(heap.slice_get(id, 0).unwrap(), Value::Int(9));
    }

    #[test]
    fn slice_push_visible_through_cloned_value() {
        let mut heap = HeapArena::new();
        let a = heap.alloc_slice(vec![]);
        let b = a.clone();
        heap.slice_push(id_of(&a), Value::Int(1)).unwrap();
        heap.slice_push(id_of(&b), Value::Int(2)).unwrap();
        assert_eq!(heap.slice_len(id_of(&a)).unwrap(), 2);
    }

    // —— VALUE-ELEM-009：struct 入容器/出容器均为值语义 ——

    #[test]
    fn struct_element_read_out_is_a_copy() {
        // struct 入容器（写入副本）→ 读出的是副本 → 改副本不回写容器
        let mut heap = HeapArena::new();
        let mut p = StructVal {
            def: StructDefId(0),
            fields: vec![Value::Int(1)],
        };
        p.fields[0] = Value::Int(42);
        let xs = heap.alloc_slice(vec![Value::Struct(p.clone())]);
        let id = id_of(&xs);

        let mut elem = heap.slice_get(id, 0).unwrap(); // 读出副本
        match &mut elem {
            Value::Struct(sv) => sv.fields[0] = Value::Int(7),
            _ => panic!("期望 struct 元素"),
        }
        // 原槽位不受影响（手算：仍为 42）
        assert_eq!(heap.slice_get(id, 0).unwrap(), Value::Struct(p.clone()));
    }

    #[test]
    fn struct_element_writeback_visible_to_container_alias() {
        // xs[i].f = v 等价于「读元素副本 → 改字段 → 写回槽位」；
        // 容器本身引用共享 → 容器别名看到写回
        let mut heap = HeapArena::new();
        let p = StructVal {
            def: StructDefId(0),
            fields: vec![Value::Int(1)],
        };
        let xs = heap.alloc_slice(vec![Value::Struct(p.clone())]);
        let ys = xs.clone(); // 容器别名
        let id = id_of(&xs);

        let mut elem = heap.slice_get(id, 0).unwrap();
        match &mut elem {
            Value::Struct(sv) => sv.fields[0] = Value::Int(9),
            _ => panic!("期望 struct 元素"),
        }
        heap.slice_set(id, 0, elem).unwrap(); // 写回槽位
        match heap.slice_get(id_of(&ys), 0).unwrap() {
            Value::Struct(sv) => assert_eq!(sv.fields[0], Value::Int(9)),
            _ => panic!("期望 struct"),
        }
    }

    // —— VALUE-COPY-001：struct clone 深拷贝、容器字段共享 ——

    #[test]
    fn struct_clone_copies_fields_but_shares_container_field() {
        let mut heap = HeapArena::new();
        let items = heap.alloc_slice(vec![Value::Int(1)]);
        let nested = StructVal {
            def: StructDefId(1),
            fields: vec![Value::Int(5)],
        };
        let outer = StructVal {
            def: StructDefId(0),
            fields: vec![
                Value::Int(1),
                Value::Struct(nested.clone()),
                Value::Slice(id_of(&items)),
            ],
        };

        let mut copy = outer.clone(); // 绑定/传参即此语义
        // 顶层标量字段独立：改副本不影响源
        copy.fields[0] = Value::Int(100);
        assert_eq!(outer.fields[0], Value::Int(1));
        // 嵌套 struct 递归拷贝：改副本嵌套字段不影响源
        match &mut copy.fields[1] {
            Value::Struct(inner) => inner.fields[0] = Value::Int(500),
            _ => panic!("期望嵌套 struct"),
        }
        assert_eq!(nested.fields[0], Value::Int(5));
        // 容器字段共享：经副本 push 源可见（VALUE-SHARE-002）
        match &copy.fields[2] {
            Value::Slice(sid) => heap.slice_push(*sid, Value::Int(2)).unwrap(),
            _ => panic!("期望容器字段"),
        }
        assert_eq!(heap.slice_len(id_of(&items)).unwrap(), 2);
    }
}
