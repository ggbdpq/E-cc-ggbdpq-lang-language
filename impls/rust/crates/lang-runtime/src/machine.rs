//! Machine：运行时 facade——解释器（lang-interp）驱动的唯一状态面。
//!
//! 组成：堆 arena、环境 arena、channel 表、调度器、定义表、内建注册表。
//! 值语义操作（引用穿透、Place 读/写、容器原位变更）都在此层实现，
//! 解释器只负责按 EVAL-* 顺序调用。

use std::rc::Rc;

use lang_core::FunctionId;

use crate::builtin::{BuiltinId, BuiltinRegistry, OutputSink, ValueFormatter};
use crate::chan::{ChannelId, ChannelTable, RecvOutcome};
use crate::defs::{DefTables, EnumDefId, StructDefId};
use crate::env::{EnvArena, EnvId};
use crate::error::{RtError, RtErrorKind, RtResult};
use crate::heap::HeapArena;
use crate::place::Place;
use crate::sched::{Scheduler, TaskExec};
use crate::value::{MapKey, StructVal, Value};

/// 引用穿透深度上限（`x = &y; y = &x` 式环的防护；正常程序远达不到）。
const DEREF_LIMIT: u32 = 64;

/// 运行时状态机。
#[derive(Debug)]
pub struct Machine {
    /// 引用语义容器堆。
    pub heap: HeapArena,
    /// 变量/闭包环境 arena。
    pub envs: EnvArena,
    /// channel 表。
    pub chans: ChannelTable,
    /// 协作式调度器。
    pub sched: Scheduler,
    /// struct/enum 定义表（含预载 Option/Result/error）。
    pub defs: DefTables,
    /// 内建注册表。
    pub builtins: BuiltinRegistry,
    /// 根（全局）环境。
    pub root: EnvId,
    fn_names: Vec<Rc<str>>,
}

impl Machine {
    /// 新 Machine：预载定义 + 核心内建 + 根环境。
    pub fn new() -> Machine {
        let mut envs = EnvArena::new();
        let root = envs.root();
        Machine {
            heap: HeapArena::new(),
            envs,
            chans: ChannelTable::new(),
            sched: Scheduler::new(),
            defs: DefTables::with_prelude(),
            builtins: BuiltinRegistry::core(),
            root,
            fn_names: Vec::new(),
        }
    }

    // —— 函数命名（输出 `<fn 名>` 用；id 分配由前级完成时可只登记名字） ——

    /// 顺序分配 FunctionId 并登记名字（v0 解释器波次的便捷入口）。
    pub fn register_function(&mut self, name: &str) -> FunctionId {
        let id = FunctionId(self.fn_names.len() as u32);
        self.fn_names.push(Rc::from(name));
        id
    }

    /// 登记既有 FunctionId 的名字（前级已分配 id 时）。
    pub fn set_fn_name(&mut self, id: FunctionId, name: &str) -> RtResult<()> {
        let slot = usize::try_from(id.0).map_err(|_| RtError::internal("FunctionId 溢出"))?;
        if slot >= self.fn_names.len() {
            self.fn_names.resize(slot + 1, Rc::from(""));
        }
        self.fn_names[slot] = Rc::from(name);
        Ok(())
    }

    /// 函数名（未登记返回空名；输出面渲染为 `<fn 匿名>`）。
    pub fn fn_name(&self, id: FunctionId) -> &str {
        match self.fn_names.get(id.0 as usize) {
            Some(n) => n.as_ref(),
            None => "",
        }
    }

    // —— 值构造便捷 ——

    /// 分配 slice 并返回持索引的值。
    pub fn alloc_slice_value(&mut self, items: Vec<Value>) -> Value {
        self.heap.alloc_slice(items)
    }

    /// 分配空 map 并返回持索引的值。
    pub fn alloc_map_value(&mut self) -> Value {
        self.heap.alloc_map(crate::heap::MapObj::new())
    }

    /// `makeChan()` 值。
    pub fn make_chan_value(&mut self) -> Value {
        Value::Chan(self.chans.make())
    }

    /// `Some(v)`（预载 Option case 0）。
    pub fn some_value(&self, v: Value) -> Value {
        Value::Enum {
            def: self.defs.option_def(),
            case: 0,
            payloads: vec![v],
        }
    }

    /// `None`（预载 Option case 1）。
    pub fn none_value(&self) -> Value {
        Value::Enum {
            def: self.defs.option_def(),
            case: 1,
            payloads: vec![],
        }
    }

    /// `Ok(v)`（预载 Result case 0）。
    pub fn ok_value(&self, v: Value) -> Value {
        Value::Enum {
            def: self.defs.result_def(),
            case: 0,
            payloads: vec![v],
        }
    }

    /// `Err(e)`（预载 Result case 1）。
    pub fn err_value(&self, e: Value) -> Value {
        Value::Enum {
            def: self.defs.result_def(),
            case: 1,
            payloads: vec![e],
        }
    }

    /// `err(msg)`：构造 `error{msg}` struct（业务错误统一 `Result[T, error]`）。
    pub fn error_value(&self, msg: Rc<str>) -> Value {
        Value::Struct(StructVal {
            def: self.defs.error_def(),
            fields: vec![Value::Str(msg)],
        })
    }

    /// `isNone(v)`：Option 的 None case（非 Option 值 → false）。
    pub fn is_none(&self, v: &Value) -> bool {
        matches!(v, Value::Enum { def, case: 1, .. } if *def == self.defs.option_def())
    }

    /// enum 的 case 名（match 输出/诊断用）。
    pub fn enum_case_name(&self, def: EnumDefId, case: u32) -> RtResult<String> {
        Ok(self
            .defs
            .enum_def(def)?
            .cases
            .get(case as usize)
            .cloned()
            .unwrap_or_default())
    }

    /// struct 的字段名表（输出用）。
    pub fn struct_field_names(&self, def: StructDefId) -> RtResult<Rc<[String]>> {
        Ok(self.defs.struct_def(def)?.fields.clone())
    }

    // —— 引用（VALUE-REF-003）——

    /// `&x`：对变量 cell 取引用（E3012 保证仅变量；解释器在静态面拒绝其余形态）。
    pub fn take_ref(&self, env: EnvId, slot: u32) -> Value {
        Value::Ref(Place::Var { env, slot })
    }

    /// 穿透引用链，返回最终使用值（操作数/条件/实参使用值时的自动解引用面）。
    pub fn deref_value(&self, v: &Value) -> RtResult<Value> {
        self.deref_inner(v.clone(), 0)
    }

    fn deref_inner(&self, v: Value, depth: u32) -> RtResult<Value> {
        if depth > DEREF_LIMIT {
            return Err(RtError::internal("引用穿透过深（疑似引用环）"));
        }
        match v {
            Value::Ref(p) => {
                let stored = self.place_load(&p)?;
                self.deref_inner(stored, depth + 1)
            }
            other => Ok(other),
        }
    }

    /// 位置的「使用值」：装载并穿透引用（读变量持有引用时的语义面）。
    pub fn place_load_resolved(&self, p: &Place) -> RtResult<Value> {
        let v = self.place_load(p)?;
        self.deref_value(&v)
    }

    // —— Place 读/写（EVAL-ASSIGN-003 / VALUE-ELEM-009）——

    /// 装载位置当前存储值（**不**自动穿透引用——可能返回 `Value::Ref`）。
    pub fn place_load(&self, p: &Place) -> RtResult<Value> {
        match p {
            Place::Var { env, slot } => self.envs.get(*env, *slot),
            Place::Element { slice, index } => self.heap.slice_get(*slice, *index as usize),
            Place::MapEntry { map, key } => self.heap.map_get(*map, key),
            Place::Field { base, index } => {
                // base 经引用穿透（VALUE-REF-003：经引用读字段即读原值）；
                // 读出即副本（VALUE-ELEM-009）
                let base = self.resolve_place(base, 0)?;
                let obj = self.place_load(&base)?;
                match obj {
                    Value::Struct(sv) => sv.fields.get(*index as usize).cloned().ok_or_else(|| {
                        RtError::internal(format!(
                            "字段槽位 {} 越界（{} 字段）",
                            index,
                            sv.fields.len()
                        ))
                    }),
                    other => Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("只能读取 struct 字段，得到 {}", other.type_hint()),
                    )),
                }
            }
        }
    }

    /// 写位置：先解析引用穿透，再按位置种类落地。
    ///
    /// `Field` 走「读元素副本 → 改字段 → 写回槽位」（VALUE-ELEM-009），
    /// 写回对容器别名可见，但不影响此前读出的其他副本。
    pub fn place_store(&mut self, p: &Place, v: Value) -> RtResult<()> {
        let target = self.resolve_place(p, 0)?;
        match target {
            Place::Var { env, slot } => self.envs.set(env, slot, v),
            Place::Element { slice, index } => self.heap.slice_set(slice, index as usize, v),
            Place::MapEntry { map, key } => {
                // 覆盖保插入位与原键形态；新键追加（VALUE-MAP-006）
                let orig_key = key_to_value(&key);
                self.heap.map_mut(map)?.insert(key, orig_key, v);
                Ok(())
            }
            Place::Field { base, index } => {
                let base = self.resolve_place(&base, 0)?;
                let mut obj = self.place_load(&base)?;
                match &mut obj {
                    Value::Struct(sv) => {
                        if index as usize >= sv.fields.len() {
                            return Err(RtError::internal(format!(
                                "字段槽位 {} 越界（{} 字段）",
                                index,
                                sv.fields.len()
                            )));
                        }
                        sv.fields[index as usize] = v;
                    }
                    other => {
                        return Err(RtError::new(
                            RtErrorKind::TypeCast,
                            format!("只能给 struct 字段赋值，得到 {}", other.type_hint()),
                        ));
                    }
                }
                // 写回 base：Var 走字段写回路径（let cell 亦允许字段更新，
                // VALUE-COPY-001——绑定不可重绑 ≠ 值的字段不可变）；
                // 其余 base 形态照常落地。
                match &base {
                    Place::Var { env, slot } => self.envs.set_field_writeback(*env, *slot, obj),
                    other => self.place_store(other, obj),
                }
            }
        }
    }

    /// 解析位置的引用穿透：装载为 `Value::Ref` 则落到内层位置（循环穿透）。
    ///
    /// 探测装载失败（映射新键、越界元素等暂不可读位置）视为「无引用可穿透」，
    /// 原样返回；真实错误由后续读/写路径自行报出。
    fn resolve_place(&self, p: &Place, depth: u32) -> RtResult<Place> {
        if depth > DEREF_LIMIT {
            return Err(RtError::internal("引用穿透过深（疑似引用环）"));
        }
        match self.place_load(p) {
            Ok(Value::Ref(inner)) => self.resolve_place(&inner, depth + 1),
            Ok(_) => Ok(p.clone()),
            Err(_) => Ok(p.clone()),
        }
    }

    /// 由值提取映射键（E3014 运行时面：int/string/bool 之外拒绝）。
    pub fn map_key_of(&self, v: &Value) -> RtResult<MapKey> {
        MapKey::from_value(v).ok_or_else(|| {
            RtError::new(
                RtErrorKind::TypeCast,
                format!("映射键须为 int/string/bool，得到 {}", v.type_hint()),
            )
        })
    }

    // —— 调度（TASK-*）——

    /// `go f(args)`：args 为 go 语句处求值的实参快照（EVAL-GO-007）；
    /// 同步子集下 Task 在本语句点执行完毕（TASK-SPAWN-002）。
    pub fn spawn(
        &mut self,
        exec: &mut dyn TaskExec,
        func: FunctionId,
        args: Vec<Value>,
    ) -> RtResult<()> {
        self.sched.enqueue(crate::sched::Task { func, args });
        self.run_pending(exec)
    }

    /// 排空挂起 Task：逐个同步执行至返回，返回值丢弃（fire-and-forget）；
    /// 嵌套 spawn 入队后由本循环消化；Task 内运行时错误向上传播。
    pub fn run_pending(&mut self, exec: &mut dyn TaskExec) -> RtResult<()> {
        while let Some(task) = self.sched.pop() {
            let _ = exec.call(self, task.func, task.args)?;
        }
        Ok(())
    }

    /// `recv(ch)` 调度面：先给挂起 Task 运行机会（协作让位点），再 FIFO 出队。
    /// 同步确定子集下队列恒空，语义与直接出队一致（TASK-SPAWN-002）。
    pub fn recv(&mut self, exec: &mut dyn TaskExec, ch: ChannelId) -> RtResult<RecvOutcome> {
        if self.sched.pending() > 0 {
            self.run_pending(exec)?;
        }
        self.chans.try_recv(ch)
    }

    // —— 内建调用口 ——

    /// 调用运行时自有内建。`Host` 类别（json/http 等）返回 `UnsupportedHost`——
    /// 宿主（解释器/后端波次）应在路由前拦截自行实现。
    pub fn call_builtin(
        &mut self,
        out: &mut dyn OutputSink,
        fmt: &dyn ValueFormatter,
        id: BuiltinId,
        args: Vec<Value>,
    ) -> RtResult<Value> {
        crate::builtin::dispatch(self, out, fmt, id, args)
    }
}

fn key_to_value(key: &MapKey) -> Value {
    match key {
        MapKey::Int(i) => Value::Int(*i),
        MapKey::Str(s) => Value::Str(s.clone()),
        MapKey::Bool(b) => Value::Bool(*b),
    }
}

impl Default for Machine {
    fn default() -> Machine {
        Machine::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::builtin::BuiltinKind;
    use crate::chan::{CHAN_CAP, CHAN_EMPTY_MSG};

    /// 测试用极简格式化器（正式格式化是 A5 领地；这里只保证确定性拼接）。
    struct TestFmt;

    impl ValueFormatter for TestFmt {
        fn fmt(&self, _m: &Machine, v: &Value, top: bool) -> String {
            match v {
                Value::Int(i) => i.to_string(),
                Value::Float(f) => f.to_string(),
                Value::Bool(b) => b.to_string(),
                Value::Str(s) => {
                    if top {
                        s.to_string()
                    } else {
                        format!("{:?}", s.as_ref())
                    }
                }
                Value::Unit => "()".to_string(),
                other => format!("<{}>", other.type_hint()),
            }
        }
    }

    #[derive(Default)]
    struct VecSink(String);

    impl OutputSink for VecSink {
        fn write(&mut self, s: &str) {
            self.0.push_str(s);
        }
    }

    /// 记录调用的 Task 执行体（可选拒绝执行）。
    struct RecExec {
        calls: Vec<(FunctionId, Vec<Value>)>,
        fail: bool,
    }

    impl RecExec {
        fn new() -> RecExec {
            RecExec {
                calls: Vec::new(),
                fail: false,
            }
        }
    }

    impl TaskExec for RecExec {
        fn call(
            &mut self,
            _machine: &mut Machine,
            func: FunctionId,
            args: Vec<Value>,
        ) -> RtResult<Value> {
            if self.fail {
                return Err(RtError::new(RtErrorKind::Assert, "task 内失败"));
            }
            self.calls.push((func, args));
            Ok(Value::Int(99)) // fire-and-forget：此返回值应被调度器丢弃
        }
    }

    /// 在环境里定义 int var 并返回 (env, slot)。
    fn def_var(m: &mut Machine, name: &str, v: i64, mutable: bool) -> (EnvId, u32) {
        let slot = m.envs.define(m.root, name, Value::Int(v), mutable).unwrap();
        (m.root, slot)
    }

    // —— VALUE-REF-003 / conformance: reference-alias-write ——

    #[test]
    fn ref_alias_reads_through() {
        // var n = 5; let r = &n; n = 6; println(n) → 6（conformance 期望）
        let mut m = Machine::new();
        let (env, n_slot) = def_var(&mut m, "n", 5, true);
        let r_slot = m
            .envs
            .define(m.root, "r", m.take_ref(env, n_slot), false)
            .unwrap();

        // n = 6：写 n 的 cell
        m.place_store(&Place::Var { env, slot: n_slot }, Value::Int(6))
            .unwrap();
        // 经 r 读：自动穿透 → 6（别名可见）
        let via_r = m
            .place_load_resolved(&Place::Var { env, slot: r_slot })
            .unwrap();
        assert_eq!(via_r, Value::Int(6));
    }

    // —— VALUE-REF-003 / conformance: reference-read-write ——

    #[test]
    fn ref_field_write_goes_through_to_original() {
        // var s = P{v:1}; let r = &s; r.v = 2; println(s.v, r.v) → "2 2"
        let mut m = Machine::new();
        let p_def = m.defs.register_struct("P", vec!["v".to_string()]);
        let s_val = Value::Struct(StructVal {
            def: p_def,
            fields: vec![Value::Int(1)],
        });
        let s_slot = m.envs.define(m.root, "s", s_val, true).unwrap();
        let r_slot = m
            .envs
            .define(m.root, "r", m.take_ref(m.root, s_slot), false)
            .unwrap();

        // r.v = 2：Field 的 base 是持有引用的变量 → 穿透写穿到 s 的 cell
        let target = Place::Field {
            base: Box::new(Place::Var {
                env: m.root,
                slot: r_slot,
            }),
            index: 0,
        };
        m.place_store(&target, Value::Int(2)).unwrap();

        let s_now = m.envs.get(m.root, s_slot).unwrap();
        assert_eq!(
            s_now,
            Value::Struct(StructVal {
                def: p_def,
                fields: vec![Value::Int(2)]
            })
        );
        // r.v 读也穿透：2
        let via_r = m
            .place_load_resolved(&Place::Field {
                base: Box::new(Place::Var {
                    env: m.root,
                    slot: r_slot,
                }),
                index: 0,
            })
            .unwrap();
        assert_eq!(via_r, Value::Int(2));
    }

    // —— VALUE-ELEM-009：容器元素字段写回 + 读出副本 ——

    #[test]
    fn container_element_field_writeback_and_copy_out() {
        // xs = [P{v:1}]; ys = xs; xs[0].v = 9 → ys 同见 9（容器引用共享）；
        // e = xs[0]; e.v = 5 → xs 不变（读出即副本）
        let mut m = Machine::new();
        let p_def = m.defs.register_struct("P", vec!["v".to_string()]);
        let xs = m.alloc_slice_value(vec![Value::Struct(StructVal {
            def: p_def,
            fields: vec![Value::Int(1)],
        })]);
        let xs_id = match &xs {
            Value::Slice(id) => *id,
            other => panic!("期望 slice，得到 {:?}", other),
        };
        let ys = xs.clone();

        // xs[0].v = 9：读副本 → 改 → 写回槽位
        let target = Place::Field {
            base: Box::new(Place::Element {
                slice: xs_id,
                index: 0,
            }),
            index: 0,
        };
        m.place_store(&target, Value::Int(9)).unwrap();

        // 容器别名 ys[0].v == 9
        let via_alias = m
            .place_load(&Place::Element {
                slice: match &ys {
                    Value::Slice(id) => *id,
                    other => panic!("期望 slice，得到 {:?}", other),
                },
                index: 0,
            })
            .unwrap();
        assert_eq!(
            via_alias,
            Value::Struct(StructVal {
                def: p_def,
                fields: vec![Value::Int(9)]
            })
        );

        // 读出副本改字段 → 不回写
        let copy = m.heap.slice_get(xs_id, 0).unwrap();
        let mut mutated = copy.clone();
        match &mut mutated {
            Value::Struct(sv) => sv.fields[0] = Value::Int(5),
            other => panic!("期望 struct，得到 {:?}", other),
        }
        let still9 = m.heap.slice_get(xs_id, 0).unwrap();
        assert_eq!(
            still9,
            Value::Struct(StructVal {
                def: p_def,
                fields: vec![Value::Int(9)]
            })
        );
        let _ = mutated;
    }

    #[test]
    fn map_entry_store_keeps_insertion_order() {
        let mut m = Machine::new();
        let map = m.alloc_map_value();
        let map_id = match &map {
            Value::Map(id) => *id,
            other => panic!("期望 map，得到 {:?}", other),
        };
        let k_a = m.map_key_of(&Value::str("a")).unwrap();
        let k_b = m.map_key_of(&Value::str("b")).unwrap();
        m.place_store(
            &Place::MapEntry {
                map: map_id,
                key: k_a.clone(),
            },
            Value::Int(1),
        )
        .unwrap();
        m.place_store(
            &Place::MapEntry {
                map: map_id,
                key: k_b.clone(),
            },
            Value::Int(2),
        )
        .unwrap();
        // 覆盖 a：插入位不动
        m.place_store(
            &Place::MapEntry {
                map: map_id,
                key: k_a.clone(),
            },
            Value::Int(10),
        )
        .unwrap();
        let keys = m.heap.map_keys(map_id).unwrap();
        let plain: Vec<String> = keys
            .iter()
            .map(|k| MapKey::from_value(k).unwrap().plain())
            .collect();
        assert_eq!(plain, ["a", "b"]);
        assert_eq!(m.heap.map_get(map_id, &k_a).unwrap(), Value::Int(10));
        // 新键追加到末尾
        let k_c = m.map_key_of(&Value::str("c")).unwrap();
        m.place_store(
            &Place::MapEntry {
                map: map_id,
                key: k_c.clone(),
            },
            Value::Int(3),
        )
        .unwrap();
        let keys = m.heap.map_keys(map_id).unwrap();
        let plain: Vec<String> = keys
            .iter()
            .map(|k| MapKey::from_value(k).unwrap().plain())
            .collect();
        assert_eq!(plain, ["a", "b", "c"]);
    }

    #[test]
    fn deref_follows_ref_chain() {
        // r2 = &r1（引用的引用）：穿透两层得原值
        let mut m = Machine::new();
        let (env, n_slot) = def_var(&mut m, "n", 7, true);
        let r1 = m.take_ref(env, n_slot);
        let r1_slot = m.envs.define(m.root, "r1", r1, false).unwrap();
        let r2_slot = m
            .envs
            .define(m.root, "r2", m.take_ref(m.root, r1_slot), false)
            .unwrap();
        let v = m
            .place_load_resolved(&Place::Var {
                env: m.root,
                slot: r2_slot,
            })
            .unwrap();
        assert_eq!(v, Value::Int(7));
    }

    #[test]
    fn immutable_var_store_rejected() {
        let mut m = Machine::new();
        let (env, slot) = def_var(&mut m, "a", 1, false);
        let err = m
            .place_store(&Place::Var { env, slot }, Value::Int(2))
            .unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::ImmutableAssign);
    }

    // —— conformance: struct-field-write / nested-struct-copy ——
    // let 绑定的 struct 值允许字段更新（重绑定才被拒）；嵌套字段链写回逐层落地。

    #[test]
    fn let_cell_field_write_updates_value_in_place() {
        // let c = Counter{n: 0}; c.n = 41 → 合法；c = ... 重绑定仍被拒
        let mut m = Machine::new();
        let def = m.defs.register_struct("Counter", vec!["n".to_string()]);
        let slot = m
            .envs
            .define(
                m.root,
                "c",
                Value::Struct(StructVal {
                    def,
                    fields: vec![Value::Int(0)],
                }),
                false,
            )
            .unwrap();
        m.place_store(
            &Place::Field {
                base: Box::new(Place::Var { env: m.root, slot }),
                index: 0,
            },
            Value::Int(41),
        )
        .unwrap();
        match m.envs.get(m.root, slot).unwrap() {
            Value::Struct(sv) => assert_eq!(sv.fields, vec![Value::Int(41)]),
            other => panic!("期望 struct，得到 {:?}", other),
        }
        // 重绑定路径（Var 直写）仍拒绝 let cell
        let err = m
            .place_store(&Place::Var { env: m.root, slot }, Value::Unit)
            .unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::ImmutableAssign);
    }

    #[test]
    fn nested_field_chain_write_through_let_cells() {
        // let a = Out{m: Mid{i: In{v: 7}}}; a.m.i.v = 99 → 手算 a.m.i.v == 99
        let mut m = Machine::new();
        let in_def = m.defs.register_struct("In", vec!["v".to_string()]);
        let mid_def = m.defs.register_struct("Mid", vec!["i".to_string()]);
        let out_def = m.defs.register_struct("Out", vec!["m".to_string()]);
        let slot = m
            .envs
            .define(
                m.root,
                "a",
                Value::Struct(StructVal {
                    def: out_def,
                    fields: vec![Value::Struct(StructVal {
                        def: mid_def,
                        fields: vec![Value::Struct(StructVal {
                            def: in_def,
                            fields: vec![Value::Int(7)],
                        })],
                    })],
                }),
                false,
            )
            .unwrap();
        let target = Place::Field {
            base: Box::new(Place::Field {
                base: Box::new(Place::Field {
                    base: Box::new(Place::Var { env: m.root, slot }),
                    index: 0,
                }),
                index: 0,
            }),
            index: 0,
        };
        m.place_store(&target, Value::Int(99)).unwrap();
        match m.envs.get(m.root, slot).unwrap() {
            Value::Struct(out) => match &out.fields[0] {
                Value::Struct(mid) => match &mid.fields[0] {
                    Value::Struct(inner) => assert_eq!(inner.fields[0], Value::Int(99)),
                    other => panic!("期望 In，得到 {:?}", other),
                },
                other => panic!("期望 Mid，得到 {:?}", other),
            },
            other => panic!("期望 Out，得到 {:?}", other),
        }
    }

    // —— TASK-SPAWN-002：同步执行 + 实参快照 ——

    #[test]
    fn spawn_runs_to_completion_at_statement_point() {
        let mut m = Machine::new();
        let f = m.register_function("sendOne");
        let mut exec = RecExec::new();
        // 实参在 go 语句处求值快照（此处由测试直接给 owned Vec）
        m.spawn(&mut exec, f, vec![Value::Int(1), Value::str("x")])
            .unwrap();
        // spawn 返回即执行完毕（同步子集）
        assert_eq!(exec.calls.len(), 1);
        assert_eq!(exec.calls[0].0, f);
        assert_eq!(exec.calls[0].1, vec![Value::Int(1), Value::str("x")]);
        assert_eq!(m.sched.pending(), 0);
    }

    #[test]
    fn spawn_drains_nested_tasks() {
        struct NestedExec {
            depth: u32,
            calls: u32,
        }
        impl TaskExec for NestedExec {
            fn call(
                &mut self,
                machine: &mut Machine,
                func: FunctionId,
                _args: Vec<Value>,
            ) -> RtResult<Value> {
                self.calls += 1;
                if self.depth > 1 {
                    self.depth -= 1;
                    // Task 体内再 spawn：由调度循环消化
                    machine.spawn(self, func, vec![])?;
                }
                Ok(Value::Unit)
            }
        }
        let mut m = Machine::new();
        let f = m.register_function("chain");
        let mut exec = NestedExec { depth: 3, calls: 0 };
        m.spawn(&mut exec, f, vec![]).unwrap();
        // 手算：depth=3 → 恰好 3 次调用后排空
        assert_eq!(exec.calls, 3);
    }

    #[test]
    fn task_error_propagates_and_result_dropped() {
        let mut m = Machine::new();
        let f = m.register_function("boom");
        let mut exec = RecExec {
            calls: vec![],
            fail: true,
        };
        let err = m.spawn(&mut exec, f, vec![]).unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::Assert);
    }

    // —— 内建调用口 ——

    #[test]
    fn println_print_output_contract() {
        let mut m = Machine::new();
        let id = m.builtins.lookup("println").unwrap();
        let mut sink = VecSink::default();
        // RUNTIME-OUTPUT-003：空格分隔 + 换行
        m.call_builtin(
            &mut sink,
            &TestFmt,
            id,
            vec![Value::Int(1), Value::str("a")],
        )
        .unwrap();
        assert_eq!(sink.0, "1 a\n");
        let id = m.builtins.lookup("print").unwrap();
        let mut sink = VecSink::default();
        m.call_builtin(&mut sink, &TestFmt, id, vec![Value::Int(1)])
            .unwrap();
        assert_eq!(sink.0, "1");
    }

    #[test]
    fn builtin_arity_checked() {
        let mut m = Machine::new();
        let id = m.builtins.lookup("len").unwrap();
        let mut sink = VecSink::default();
        let err = m.call_builtin(&mut sink, &TestFmt, id, vec![]).unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::TypeCast);
    }

    #[test]
    fn host_builtin_routed_error() {
        let mut m = Machine::new();
        let id = m.builtins.lookup("jsonParse").unwrap();
        assert_eq!(m.builtins.kind(id).unwrap(), BuiltinKind::Host);
        let mut sink = VecSink::default();
        let err = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::str("{}")])
            .unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::UnsupportedHost);
    }

    #[test]
    fn recv_returns_result_values() {
        // CHAN-RECV-003 / RESULT-CHAN-005：有值 Ok(v)；空 → Err("channel 空")
        let mut m = Machine::new();
        let recv_id = m.builtins.lookup("recv").unwrap();
        let send_id = m.builtins.lookup("send").unwrap();
        let mk_id = m.builtins.lookup("makeChan").unwrap();
        let mut sink = VecSink::default();

        let ch = m.call_builtin(&mut sink, &TestFmt, mk_id, vec![]).unwrap();
        m.call_builtin(
            &mut sink,
            &TestFmt,
            send_id,
            vec![ch.clone(), Value::Int(5)],
        )
        .unwrap();
        let ok = m
            .call_builtin(&mut sink, &TestFmt, recv_id, vec![ch.clone()])
            .unwrap();
        match ok {
            Value::Enum {
                def,
                case,
                payloads,
            } => {
                assert_eq!(def, m.defs.result_def());
                assert_eq!(case, 0);
                assert_eq!(payloads, vec![Value::Int(5)]);
            }
            other => panic!("期望 Result 值，得到 {:?}", other),
        }
        // 空：Err 值，载荷文本逐字冻结
        let err_v = m
            .call_builtin(&mut sink, &TestFmt, recv_id, vec![ch])
            .unwrap();
        match err_v {
            Value::Enum {
                def,
                case,
                payloads,
            } => {
                assert_eq!(def, m.defs.result_def());
                assert_eq!(case, 1);
                assert_eq!(payloads, vec![Value::str(CHAN_EMPTY_MSG)]);
            }
            other => panic!("期望 Result 值，得到 {:?}", other),
        }
    }

    #[test]
    fn send_rejects_non_chan_and_full_buffer() {
        let mut m = Machine::new();
        let send_id = m.builtins.lookup("send").unwrap();
        let mut sink = VecSink::default();
        let err = m
            .call_builtin(
                &mut sink,
                &TestFmt,
                send_id,
                vec![Value::Int(1), Value::Unit],
            )
            .unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::TypeCast);

        let ch = m.make_chan_value();
        for i in 0..CHAN_CAP as i64 {
            m.chans
                .send(
                    match &ch {
                        Value::Chan(id) => *id,
                        other => panic!("期望 chan，得到 {:?}", other),
                    },
                    Value::Int(i),
                )
                .unwrap();
        }
        let err = m
            .call_builtin(&mut sink, &TestFmt, send_id, vec![ch, Value::Int(-1)])
            .unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::ChanFull);
    }

    #[test]
    fn is_none_and_preloaded_constructors() {
        let m = Machine::new();
        assert!(m.is_none(&m.none_value()));
        assert!(!m.is_none(&m.some_value(Value::Int(1))));
        assert!(!m.is_none(&Value::Int(0)));
        // Result 的 Err 不是 Option 的 None
        assert!(!m.is_none(&m.err_value(Value::Unit)));
        assert_eq!(m.ok_value(Value::Int(1)).type_name(&m.defs), "Result");
        assert_eq!(m.none_value().type_name(&m.defs), "Option");
        assert_eq!(m.error_value("x".into()).type_name(&m.defs), "error");
    }

    #[test]
    fn err_builtin_builds_error_struct() {
        let mut m = Machine::new();
        let id = m.builtins.lookup("err").unwrap();
        let mut sink = VecSink::default();
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::str("boom")])
            .unwrap();
        match v {
            Value::Struct(sv) => {
                assert_eq!(sv.def, m.defs.error_def());
                assert_eq!(sv.fields, vec![Value::str("boom")]);
            }
            other => panic!("期望 error struct，得到 {:?}", other),
        }
    }

    #[test]
    fn len_counts_codepoints() {
        // lexical-grammar.md：len() 返回码点数（"hél" = 3 个码点）
        let mut m = Machine::new();
        let id = m.builtins.lookup("len").unwrap();
        let mut sink = VecSink::default();
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::str("hél")])
            .unwrap();
        assert_eq!(v, Value::Int(3));
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::Range { lo: 2, hi: 5 }])
            .unwrap();
        assert_eq!(v, Value::Int(3));
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::Range { lo: 5, hi: 2 }])
            .unwrap();
        assert_eq!(v, Value::Int(0));
    }

    #[test]
    fn push_visible_through_alias() {
        // VALUE-SHARE-002：push 原位追加对所有别名可见
        let mut m = Machine::new();
        let id = m.builtins.lookup("push").unwrap();
        let mut sink = VecSink::default();
        let a = m.alloc_slice_value(vec![Value::Int(1)]);
        let b = a.clone();
        m.call_builtin(&mut sink, &TestFmt, id, vec![a, Value::Int(2)])
            .unwrap();
        let a_id = match &b {
            Value::Slice(id) => *id,
            other => panic!("期望 slice，得到 {:?}", other),
        };
        assert_eq!(m.heap.slice_len(a_id).unwrap(), 2);
    }

    #[test]
    fn int_cast_truncates_toward_zero() {
        let mut m = Machine::new();
        let id = m.builtins.lookup("int").unwrap();
        let mut sink = VecSink::default();
        // Go int64()：向零截断（手算：-2.7 → -2；2.9 → 2）
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::Float(-2.7)])
            .unwrap();
        assert_eq!(v, Value::Int(-2));
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::Float(2.9)])
            .unwrap();
        assert_eq!(v, Value::Int(2));
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::str("42")])
            .unwrap();
        assert_eq!(v, Value::Int(42));
        let err = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::str("x")])
            .unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::TypeCast);
        let err = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::Bool(true)])
            .unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::TypeCast);
    }

    #[test]
    fn char_rejects_invalid_codepoints_to_replacement() {
        let mut m = Machine::new();
        let id = m.builtins.lookup("char").unwrap();
        let mut sink = VecSink::default();
        // 证据口径：代理区/越界 → U+FFFD
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::Int(0xD800)])
            .unwrap();
        assert_eq!(v, Value::str("\u{FFFD}"));
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::Int(0x4E2D)])
            .unwrap();
        assert_eq!(v, Value::str("中"));
    }

    #[test]
    fn bytes_split_utf8() {
        let mut m = Machine::new();
        let id = m.builtins.lookup("bytes").unwrap();
        let mut sink = VecSink::default();
        // 手算："h" = 0x68；"é" = U+00E9 = UTF-8 C3 A9
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::str("hé")])
            .unwrap();
        let arr_id = match &v {
            Value::Slice(id) => *id,
            other => panic!("期望 slice，得到 {:?}", other),
        };
        let items = m.heap.slice(arr_id).unwrap();
        assert_eq!(
            items,
            &vec![Value::Int(0x68), Value::Int(0xC3), Value::Int(0xA9)]
        );
    }

    #[test]
    fn assert_passes_and_fails() {
        let mut m = Machine::new();
        let id = m.builtins.lookup("assert").unwrap();
        let mut sink = VecSink::default();
        let v = m
            .call_builtin(&mut sink, &TestFmt, id, vec![Value::Bool(true)])
            .unwrap();
        assert_eq!(v, Value::Unit);
        let err = m
            .call_builtin(
                &mut sink,
                &TestFmt,
                id,
                vec![Value::Bool(false), Value::str("oops")],
            )
            .unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::Assert);
        assert!(err.message().contains("oops"));
    }

    #[test]
    fn fn_name_registered_and_missing() {
        let mut m = Machine::new();
        let f = m.register_function("work");
        assert_eq!(m.fn_name(f), "work");
        assert_eq!(m.fn_name(FunctionId(99)), "");
        // 既存 id 补登记
        m.set_fn_name(FunctionId(5), "late").unwrap();
        assert_eq!(m.fn_name(FunctionId(5)), "late");
    }
}
