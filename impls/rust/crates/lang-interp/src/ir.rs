//! IR 解释器：`lang_ir::IrProgram` 的栈式基本块执行（与 AST 参考解释器共享
//! Machine/Value/内建/输出面，遍历彼此独立——计划 §R12 的差分价值约束）。
//!
//! 执行模型：
//! - 每次调用建一个帧：帧局部槽位落在 `Machine::envs` 的帧环境里
//!   （`LocalId` = 槽位号），使 `Place::Var` 可寻址局部槽（装箱纪律与
//!   字段/索引写回的地址面）；
//! - 操作数栈条目带可选 Place 溯源：`FieldGet`/`IndexGet` 产生的「地址组件」
//!   在 `FieldSet`/`IndexSet` 写回时落地（与 AST 侧 `place_store` 同一机制）；
//!   `Ref` 值自带 Place（装箱纪律：`BoxNew` 分配 box 槽，读 `Deref` 写 `RefSet`）；
//! - 闭包：`ClosureNew` 弹出捕获的 Ref 值存入闭包环境；调用时逐槽落地到
//!   被调函数局部表前 `capture_count` 个槽位（按引用共享，VALUE-CAPTURE-004）；
//! - `go`（`Spawn`）= 同步确定子集：立即执行至返回，返回值丢弃
//!   （TASK-SPAWN-002）；
//! - for-in 迭代器：`IterNew` 快照元素序列，迭代器本体以 box 槽引用形态
//!   驻留局部槽（不跨块压栈），`IterNext` 耗尽跳 `done`（CTRL-FORIN-003）；
//! - `?`（`Try`）：`Err` 载荷存 `err` 槽跳 `fail` 块，函数边界合成 `Err`
//!   返回（RESULT-TRY-003）。

use std::collections::HashMap;

use lang_core::{FunctionId, LocalId};
use lang_ir::{Binop, BlockId, EnumId, GlobalId, Inst, IrProgram, StructId, Terminator, Unop};
use lang_runtime::{
    BuiltinKind, EnvId, Machine, OutputSink, Place, RtError, RtErrorKind, RtResult, StructDefId,
    Value,
};

use crate::format::{InterpFmt, host_builtin};
use crate::ops::{self, Op};

/// 操作数栈条目：值 + 可选地址溯源（None = 无地址，如常量/运算结果）。
struct StackVal {
    v: Value,
    place: Option<Place>,
}

/// for-in 迭代器状态（`IterNew` 快照；box 槽引用为键）。
struct IterState {
    items: Vec<Value>,
    pos: usize,
}

/// IR 解释器：持有运行时状态机 + 程序引用 + IR 身份到运行时 def 的映射。
pub struct IrMachine<'p> {
    /// 运行时状态（堆/环境/channel/调度/定义表/内建表）。
    pub machine: Machine,
    program: &'p IrProgram,
    /// StructId → 运行时 StructDefId（装载序 = 声明序）。
    struct_ids: Vec<StructDefId>,
    /// EnumId → 运行时 EnumDefId。
    enum_ids: Vec<lang_runtime::EnumDefId>,
    /// 活跃迭代器（box 槽环境 id → 状态）。
    iters: HashMap<EnvId, IterState>,
}

impl<'p> IrMachine<'p> {
    /// 装载程序：登记 struct/enum 定义、函数名与全局槽位。
    ///
    /// 程序表尾部的预载 `error`/`Option`/`Result`（lowering 恒追加，供名字
    /// 查表）映射到运行时 DefTables 的预载定义（error=StructDefId(0)、
    /// Option=EnumDefId(0)、Result=EnumDefId(1)）——`Try` 的 Result 判定、
    /// `isNone`、JSON None 序列化与 `ok_value`/`err_value` 构造都以预载 id
    /// 为身份锚；重复注册会产生第二套同形定义，预载判定全部落空。名字判定
    /// 与 lowering 的 `struct_id_by_name`/`enum_id_by_name` 同口径（合法程序
    /// 中该三名为预载保留名）。
    pub fn new(program: &'p IrProgram) -> IrMachine<'p> {
        let mut machine = Machine::new();
        let mut struct_ids = Vec::with_capacity(program.structs.len());
        for s in &program.structs {
            let def = if s.name == "error" {
                machine.defs.error_def()
            } else {
                let fields: Vec<String> = s.fields.iter().map(|f| f.name.clone()).collect();
                machine.defs.register_struct(&s.name, fields)
            };
            struct_ids.push(def);
        }
        let mut enum_ids = Vec::with_capacity(program.enums.len());
        for e in &program.enums {
            let def = match e.name.as_str() {
                "Option" => machine.defs.option_def(),
                "Result" => machine.defs.result_def(),
                _ => {
                    let cases: Vec<String> = e.cases.iter().map(|c| c.name.clone()).collect();
                    machine.defs.register_enum(&e.name, cases)
                }
            };
            enum_ids.push(def);
        }
        for (i, f) in program.functions.iter().enumerate() {
            let _ = machine.set_fn_name(FunctionId(i as u32), &f.name);
        }
        // 全局槽位：GlobalId = 根环境槽位号（声明序 GlobalSet 寻址）
        for (i, g) in program.globals.iter().enumerate() {
            let _ = g;
            let name = format!("%g{i}");
            // 语言级 let/var 可变性是静态检查面；IR 帧槽位一律可写
            let _ = machine.envs.define(machine.root, &name, Value::Unit, true);
        }
        IrMachine {
            machine,
            program,
            struct_ids,
            enum_ids,
            iters: HashMap::new(),
        }
    }

    /// 执行程序：先 `init`（全局初始化）再 `main`（均可缺省）。
    pub fn run(&mut self, out: &mut dyn OutputSink) -> RtResult<()> {
        if let Some(init) = self.program.init {
            self.call_function(init.0, Vec::new(), None, out)?;
        }
        if let Some(main) = self.program.main {
            self.call_function(main.0, Vec::new(), None, out)?;
        }
        Ok(())
    }

    /// 调用函数：帧环境 + 局部槽位落位 + 基本块循环。
    fn call_function(
        &mut self,
        func: u32,
        args: Vec<Value>,
        capture_env: Option<EnvId>,
        out: &mut dyn OutputSink,
    ) -> RtResult<Value> {
        let program: &'p IrProgram = self.program;
        let f = program
            .functions
            .get(func as usize)
            .ok_or_else(|| RtError::internal(format!("FunctionId({func}) 越界")))?;
        if f.extern_host.is_some() {
            return Err(RtError::new(
                RtErrorKind::UnsupportedHost,
                format!("extern 函数 {} 仅转译可用（宿主已声明，未链接）", f.name),
            ));
        }
        let env = self.machine.envs.child(self.machine.root);
        // 局部槽位：捕获槽 + 参数 + 局部/临时；语言级可变性是静态面，槽位一律可写
        for (i, _l) in f.locals.iter().enumerate() {
            let name = format!("%{i}");
            self.machine.envs.define(env, &name, Value::Unit, true)?;
        }
        // 捕获槽：从闭包环境逐槽取 Ref 值（按引用共享）
        if let Some(cenv) = capture_env {
            for i in 0..usize::from(f.capture_count) {
                let v = self.machine.envs.get(cenv, i as u32)?;
                self.machine.envs.set(env, i as u32, v)?;
            }
        }
        // 参数槽
        let param_base = usize::from(f.capture_count);
        if args.len() != usize::from(f.sig.param_count) {
            return Err(RtError::internal(format!(
                "{} 实参个数与签名不符（{} vs {}）",
                f.name,
                args.len(),
                f.sig.param_count
            )));
        }
        for (i, a) in args.into_iter().enumerate() {
            self.machine.envs.set(env, (param_base + i) as u32, a)?;
        }

        // 基本块循环：块入口栈深恒 0（validator 不变量），栈随块连续推进
        let mut stack: Vec<StackVal> = Vec::new();
        let mut cur = BlockId(0);
        loop {
            let block = program
                .functions
                .get(func as usize)
                .and_then(|f| f.blocks.get(cur.0 as usize))
                .ok_or_else(|| RtError::internal(format!("块 {cur} 越界")))?;
            let mut jumped = false;
            for inst in &block.insts {
                if let Some(target) = self.exec_inst(program, inst, env, &mut stack, out)? {
                    // 块内跳转（IterNext 耗尽 / Try 的 fail 块）：终接不执行
                    cur = target;
                    jumped = true;
                    break;
                }
            }
            if jumped {
                continue;
            }
            let term = &program.functions[func as usize].blocks[cur.0 as usize].term;
            match term {
                Terminator::Goto { target } => cur = *target,
                Terminator::Branch { if_true, if_false } => {
                    let cond = self.pop(&mut stack)?.v;
                    cur = if cond.truthy()? { *if_true } else { *if_false };
                }
                Terminator::SwitchInt { arms, default } => {
                    let v = self.pop(&mut stack)?.v;
                    let Value::Int(i) = v else {
                        return Err(RtError::new(
                            RtErrorKind::TypeCast,
                            format!("int match 分派须为 int，得到 {}", v.type_hint()),
                        ));
                    };
                    match arms
                        .iter()
                        .find(|(k, _)| *k == i)
                        .map(|(_, b)| *b)
                        .or(*default)
                    {
                        Some(t) => cur = t,
                        None => {
                            return Err(RtError::new(RtErrorKind::MatchNoArm, "match 无匹配分支"));
                        }
                    }
                }
                Terminator::SwitchEnum {
                    enum_id,
                    arms,
                    default,
                } => {
                    let v = self.pop(&mut stack)?.v;
                    cur = self.switch_enum(*enum_id, arms, *default, v)?;
                }
                Terminator::Return { has_value } => {
                    let v = if *has_value {
                        self.pop(&mut stack)?.v
                    } else {
                        Value::Unit
                    };
                    return Ok(v);
                }
                Terminator::Unreachable => {
                    return Err(RtError::internal("执行到不可达锚"));
                }
            }
        }
    }

    fn switch_enum(
        &self,
        enum_id: EnumId,
        arms: &[BlockId],
        default: Option<BlockId>,
        v: Value,
    ) -> RtResult<BlockId> {
        let Value::Enum { def, case, .. } = &v else {
            return Err(RtError::new(
                RtErrorKind::TypeCast,
                format!("enum match 分派须为 enum 值，得到 {}", v.type_hint()),
            ));
        };
        if *def == self.enum_def(enum_id)? && (*case as usize) < arms.len() {
            return Ok(arms[*case as usize]);
        }
        default.ok_or_else(|| RtError::new(RtErrorKind::MatchNoArm, "match 无匹配分支"))
    }

    fn enum_def(&self, id: EnumId) -> RtResult<lang_runtime::EnumDefId> {
        self.enum_ids
            .get(usize::from(id))
            .copied()
            .ok_or_else(|| RtError::internal(format!("EnumId({}) 越界", usize::from(id))))
    }

    fn struct_def(&self, id: StructId) -> RtResult<StructDefId> {
        self.struct_ids
            .get(usize::from(id))
            .copied()
            .ok_or_else(|| RtError::internal(format!("StructId({}) 越界", usize::from(id))))
    }

    fn pop(&mut self, stack: &mut Vec<StackVal>) -> RtResult<StackVal> {
        stack
            .pop()
            .ok_or_else(|| RtError::internal("操作数栈下溢（IR 栈效应被破坏）"))
    }

    /// 栈条目的地址形态：溯源 Place 优先，Ref 值自带 Place 次之。
    fn place_of(e: &StackVal) -> Option<Place> {
        match (&e.place, &e.v) {
            (Some(p), _) => Some(p.clone()),
            (None, Value::Ref(p)) => Some(p.clone()),
            (None, _) => None,
        }
    }

    fn exec_inst(
        &mut self,
        program: &'p IrProgram,
        inst: &Inst,
        env: EnvId,
        stack: &mut Vec<StackVal>,
        out: &mut dyn OutputSink,
    ) -> RtResult<Option<BlockId>> {
        match inst {
            Inst::ConstI64(v) => stack.push(StackVal {
                v: Value::Int(*v),
                place: None,
            }),
            Inst::ConstF64(v) => stack.push(StackVal {
                v: Value::Float(*v),
                place: None,
            }),
            Inst::ConstBool(v) => stack.push(StackVal {
                v: Value::Bool(*v),
                place: None,
            }),
            Inst::ConstStr(s) => stack.push(StackVal {
                v: Value::str(s.as_str()),
                place: None,
            }),

            Inst::LocalGet(l) => {
                let v = self.machine.envs.get(env, local_slot(*l))?;
                stack.push(StackVal {
                    v,
                    place: Some(Place::Var {
                        env,
                        slot: local_slot(*l),
                    }),
                });
            }
            Inst::LocalSet(l) => {
                let v = self.pop(stack)?.v;
                self.machine.envs.set(env, local_slot(*l), v)?;
            }
            Inst::GlobalGet(g) => {
                let slot = global_slot(*g);
                let v = self.machine.envs.get(self.machine.root, slot)?;
                stack.push(StackVal {
                    v,
                    place: Some(Place::Var {
                        env: self.machine.root,
                        slot,
                    }),
                });
            }
            Inst::GlobalSet(g) => {
                let v = self.pop(stack)?.v;
                self.machine
                    .envs
                    .set(self.machine.root, global_slot(*g), v)?;
            }

            Inst::Unary(op) => {
                let e = self.pop(stack)?;
                let v = self.machine.deref_value(&e.v)?;
                stack.push(StackVal {
                    v: match (op, v) {
                        (Unop::Neg, Value::Int(i)) => Value::Int(i.wrapping_neg()),
                        (Unop::Neg, Value::Float(f)) => Value::Float(-f),
                        (Unop::Not, v) => Value::Bool(!v.truthy()?),
                        (_, other) => {
                            return Err(RtError::new(
                                RtErrorKind::TypeCast,
                                format!("一元运算不支持 {}", other.type_hint()),
                            ));
                        }
                    },
                    place: None,
                });
            }
            Inst::Binary(op) => {
                let r = self.pop(stack)?;
                let l = self.pop(stack)?;
                let lv = self.machine.deref_value(&l.v)?;
                let rv = self.machine.deref_value(&r.v)?;
                stack.push(StackVal {
                    v: ops::binary(ir_binop(*op), &lv, &rv)?,
                    place: None,
                });
            }
            Inst::Pop => {
                self.pop(stack)?;
            }

            Inst::Call(fid) => {
                let f = &program.functions[fid.0 as usize];
                let argc = usize::from(f.sig.param_count);
                let args: Vec<Value> = self.pop_n(stack, argc)?.into_iter().map(|e| e.v).collect();
                let v = self.call_function(fid.0, args, None, out)?;
                if f.sig.has_ret {
                    stack.push(StackVal { v, place: None });
                }
            }
            Inst::CallIndirect { argc, has_ret } => {
                let args: Vec<Value> = self
                    .pop_n(stack, usize::from(*argc))?
                    .into_iter()
                    .map(|e| e.v)
                    .collect();
                let callee = self.pop(stack)?;
                let Value::Closure { func, env: cenv } = callee.v else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("{} 不可调用", callee.v.type_hint()),
                    ));
                };
                let v = self.call_function(func.0, args, Some(cenv), out)?;
                if *has_ret {
                    stack.push(StackVal { v, place: None });
                }
            }
            Inst::CallBuiltin {
                name,
                argc,
                has_ret,
            } => {
                let args: Vec<Value> = self
                    .pop_n(stack, usize::from(*argc))?
                    .into_iter()
                    .map(|e| e.v)
                    .collect();
                let v = self.call_builtin_by_name(name, args, out)?;
                if *has_ret {
                    stack.push(StackVal { v, place: None });
                }
            }
            Inst::Spawn { func, argc } => {
                let args: Vec<Value> = self
                    .pop_n(stack, usize::from(*argc))?
                    .into_iter()
                    .map(|e| e.v)
                    .collect();
                // 同步确定子集：立即执行至返回，返回值丢弃（TASK-SPAWN-002）
                self.call_function(func.0, args, None, out)?;
            }
            Inst::ClosureNew { func, captures } => {
                let n = usize::from(*captures);
                // 捕获值按压栈序（=捕获声明序）落槽；pop_n 保序
                let caps = self.pop_n(stack, n)?;
                let cenv = self.machine.envs.child(self.machine.root);
                for (i, c) in caps.into_iter().enumerate() {
                    self.machine
                        .envs
                        .define(cenv, &format!("%c{i}"), c.v, true)?;
                }
                stack.push(StackVal {
                    v: Value::Closure {
                        func: *func,
                        env: cenv,
                    },
                    place: None,
                });
            }

            Inst::IndexGet => {
                let idx = self.pop(stack)?;
                let obj = self.pop(stack)?;
                let ov = self.machine.deref_value(&obj.v)?;
                let iv = self.machine.deref_value(&idx.v)?;
                match ov {
                    Value::Slice(id) => {
                        let i = int_index(&iv, self.machine.heap.slice_len(id)?)?;
                        let v = self.machine.heap.slice_get(id, i)?;
                        stack.push(StackVal {
                            v,
                            place: Some(Place::Element {
                                slice: id,
                                index: i as u32,
                            }),
                        });
                    }
                    Value::Map(id) => {
                        let key = self.machine.map_key_of(&iv)?;
                        let v = self.machine.heap.map_get(id, &key)?;
                        stack.push(StackVal {
                            v,
                            place: Some(Place::MapEntry { map: id, key }),
                        });
                    }
                    Value::Str(s) => {
                        let i = int_index(&iv, s.chars().count())?;
                        let c = s.chars().nth(i).unwrap_or('\u{0}');
                        stack.push(StackVal {
                            v: Value::str(c.to_string()),
                            place: None,
                        });
                    }
                    other => {
                        return Err(RtError::new(
                            RtErrorKind::TypeCast,
                            format!("{} 不可索引", other.type_hint()),
                        ));
                    }
                }
            }
            Inst::IndexSet => {
                let val = self.pop(stack)?.v;
                let idx = self.pop(stack)?;
                let obj = self.pop(stack)?;
                let ov = self.machine.deref_value(&obj.v)?;
                let iv = self.machine.deref_value(&idx.v)?;
                match ov {
                    Value::Slice(id) => {
                        let i = int_index(&iv, self.machine.heap.slice_len(id)?)?;
                        self.machine.heap.slice_set(id, i, val)?;
                    }
                    Value::Map(id) => {
                        self.machine.heap.map_insert(id, &iv, val)?;
                    }
                    Value::Str(_) => {
                        return Err(RtError::new(RtErrorKind::TypeCast, "字符串不可索引赋值"));
                    }
                    other => {
                        return Err(RtError::new(
                            RtErrorKind::TypeCast,
                            format!("{} 不可索引赋值", other.type_hint()),
                        ));
                    }
                }
            }
            Inst::SliceGet => {
                let hi = self.pop(stack)?.v;
                let lo = self.pop(stack)?.v;
                let obj = self.pop(stack)?;
                let ov = self.machine.deref_value(&obj.v)?;
                let (Value::Int(lo), Value::Int(hi)) = (lo, hi) else {
                    return Err(RtError::new(RtErrorKind::TypeCast, "切片端点须为 int"));
                };
                match ov {
                    Value::Slice(id) => {
                        let n = self.machine.heap.slice_len(id)?;
                        check_slice(lo, hi, n)?;
                        let items = self.machine.heap.slice(id)?[lo as usize..hi as usize].to_vec();
                        stack.push(StackVal {
                            v: self.machine.alloc_slice_value(items),
                            place: None,
                        });
                    }
                    Value::Str(s) => {
                        check_slice(lo, hi, s.chars().count())?;
                        let text: String = s
                            .chars()
                            .skip(lo as usize)
                            .take((hi - lo) as usize)
                            .collect();
                        stack.push(StackVal {
                            v: Value::str(text),
                            place: None,
                        });
                    }
                    other => {
                        return Err(RtError::new(
                            RtErrorKind::TypeCast,
                            format!("{} 不可切片", other.type_hint()),
                        ));
                    }
                }
            }
            Inst::FieldGet { field } => {
                let obj = self.pop(stack)?;
                let base = Self::place_of(&obj);
                let ov = self.machine.deref_value(&obj.v)?;
                let Value::Struct(sv) = &ov else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("{} 不是 struct，无字段 {field}", ov.type_hint()),
                    ));
                };
                let names = self.machine.defs.struct_def(sv.def)?.fields.clone();
                let Some(i) = names.iter().position(|f| f == field) else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("没有字段 {field}"),
                    ));
                };
                stack.push(StackVal {
                    v: sv.fields[i].clone(),
                    place: base.map(|b| Place::Field {
                        base: Box::new(b),
                        index: i as u32,
                    }),
                });
            }
            Inst::FieldSet { field } => {
                let val = self.pop(stack)?.v;
                let obj = self.pop(stack)?;
                let Some(base) = Self::place_of(&obj) else {
                    return Err(RtError::internal(
                        "FieldSet 目标无地址（lowering 须保留槽位溯源或装箱）",
                    ));
                };
                let ov = self.machine.deref_value(&obj.v)?;
                let Value::Struct(sv) = &ov else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("只能给 struct 字段赋值，得到 {}", ov.type_hint()),
                    ));
                };
                let names = self.machine.defs.struct_def(sv.def)?.fields.clone();
                let Some(i) = names.iter().position(|f| f == field) else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("没有字段 {field}"),
                    ));
                };
                self.machine.place_store(
                    &Place::Field {
                        base: Box::new(base),
                        index: i as u32,
                    },
                    val,
                )?;
            }
            Inst::ArrayPush => {
                let val = self.pop(stack)?;
                let arr = self.pop(stack)?;
                let av = self.machine.deref_value(&arr.v)?;
                let vv = self.machine.deref_value(&val.v)?;
                let Value::Slice(lhs) = &av else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("数组 += 需要数组，得到 {}", av.type_hint()),
                    ));
                };
                let Value::Slice(rhs) = &vv else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("数组 += 需要数组，得到 {}", vv.type_hint()),
                    ));
                };
                let items = self.machine.heap.slice(*rhs)?.clone();
                for v in items {
                    self.machine.heap.slice_push(*lhs, v)?;
                }
            }

            Inst::ArrayNew { count } => {
                // 元素左到右压栈；pop_n 按栈序（=压入序）返回，不反转
                let items = self.pop_n(stack, *count as usize)?;
                let vals: Vec<Value> = items.into_iter().map(|e| e.v).collect();
                stack.push(StackVal {
                    v: self.machine.alloc_slice_value(vals),
                    place: None,
                });
            }
            Inst::MapNew { count } => {
                // 键值对左到右压栈（k1,v1,k2,v2,…）；pop_n 保压入序，成对插入
                // 即压入对序（VALUE-MAP-006）
                let flat = self.pop_n(stack, *count as usize * 2)?;
                let m = self.machine.alloc_map_value();
                let Value::Map(mid) = &m else {
                    return Err(RtError::internal("新分配映射缺索引"));
                };
                for pair in flat.chunks(2) {
                    self.machine
                        .heap
                        .map_insert(*mid, &pair[0].v, pair[1].v.clone())?;
                }
                stack.push(StackVal { v: m, place: None });
            }
            Inst::StructNew { struct_id } => {
                let def = self.struct_def(*struct_id)?;
                let n = program.structs[usize::from(*struct_id)].fields.len();
                // 字段值按声明序压栈；pop_n 保压入序 = 声明序
                let fields = self.pop_n(stack, n)?;
                stack.push(StackVal {
                    v: Value::Struct(lang_runtime::StructVal {
                        def,
                        fields: fields.into_iter().map(|e| e.v).collect(),
                    }),
                    place: None,
                });
            }
            Inst::EnumNew { enum_id, case_id } => {
                let def = self.enum_def(*enum_id)?;
                let n = program.enums[usize::from(*enum_id)].cases[usize::from(*case_id)]
                    .payload_types
                    .len();
                // 载荷按 case 声明序压栈；pop_n 保压入序
                let payloads = self.pop_n(stack, n)?;
                stack.push(StackVal {
                    v: Value::Enum {
                        def,
                        case: u32::from(*case_id),
                        payloads: payloads.into_iter().map(|e| e.v).collect(),
                    },
                    place: None,
                });
            }
            Inst::RangeNew => {
                let hi = self.pop(stack)?.v;
                let lo = self.pop(stack)?.v;
                let (Value::Int(lo), Value::Int(hi)) = (lo, hi) else {
                    return Err(RtError::new(RtErrorKind::TypeCast, "区间两端须为 int"));
                };
                stack.push(StackVal {
                    v: Value::Range { lo, hi },
                    place: None,
                });
            }

            Inst::BoxNew => {
                let v = self.pop(stack)?.v;
                let box_env = self.machine.envs.child(self.machine.root);
                self.machine.envs.define(box_env, "%box", v, true)?;
                stack.push(StackVal {
                    v: Value::Ref(Place::Var {
                        env: box_env,
                        slot: 0,
                    }),
                    place: None,
                });
            }
            Inst::Deref => {
                let e = self.pop(stack)?;
                match &e.v {
                    Value::Ref(p) => {
                        let v = self.machine.place_load(p)?;
                        stack.push(StackVal {
                            v,
                            place: Some(p.clone()),
                        });
                    }
                    _ => stack.push(e),
                }
            }
            Inst::RefSet => {
                let val = self.pop(stack)?.v;
                let r = self.pop(stack)?;
                let Value::Ref(p) = &r.v else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("RefSet 需要 Ref，得到 {}", r.v.type_hint()),
                    ));
                };
                self.machine.place_store(p, val)?;
            }

            Inst::EnumPayloadGet { index } => {
                let e = self.pop(stack)?;
                let Value::Enum { payloads, .. } = &e.v else {
                    return Err(RtError::internal("EnumPayloadGet 需要 enum 值"));
                };
                let p = payloads
                    .get(usize::from(*index))
                    .cloned()
                    .ok_or_else(|| RtError::internal(format!("enum 载荷 {index} 越界")))?;
                stack.push(StackVal { v: p, place: None });
            }

            Inst::IterNew => {
                let e = self.pop(stack)?;
                let v = self.machine.deref_value(&e.v)?;
                let items = match v {
                    Value::Slice(id) => self.machine.heap.slice(id)?.clone(),
                    Value::Map(id) => self.machine.heap.map_keys(id)?,
                    Value::Str(s) => s.chars().map(|c| Value::str(c.to_string())).collect(),
                    Value::Range { lo, hi } => {
                        if hi > lo {
                            (lo..hi).map(Value::Int).collect()
                        } else {
                            Vec::new()
                        }
                    }
                    other => {
                        return Err(RtError::new(
                            RtErrorKind::NotIterable,
                            format!("{} 不可迭代", other.type_hint()),
                        ));
                    }
                };
                // 迭代器本体驻留局部槽：以 box 槽引用为句柄（Value 形态内表达）
                let box_env = self.machine.envs.child(self.machine.root);
                self.machine
                    .envs
                    .define(box_env, "%iter", Value::Unit, true)?;
                self.iters.insert(box_env, IterState { items, pos: 0 });
                stack.push(StackVal {
                    v: Value::Ref(Place::Var {
                        env: box_env,
                        slot: 0,
                    }),
                    place: None,
                });
            }
            Inst::IterNext { done } => {
                let e = self.pop(stack)?;
                let Value::Ref(Place::Var { env: handle, .. }) = &e.v else {
                    return Err(RtError::internal("IterNext 需要迭代器句柄"));
                };
                let state = self
                    .iters
                    .get_mut(handle)
                    .ok_or_else(|| RtError::internal("迭代器状态丢失"))?;
                if state.pos < state.items.len() {
                    let v = state.items[state.pos].clone();
                    state.pos += 1;
                    stack.push(StackVal { v, place: None });
                } else {
                    return Ok(Some(*done));
                }
            }

            Inst::ChanNew => {
                stack.push(StackVal {
                    v: self.machine.make_chan_value(),
                    place: None,
                });
            }
            Inst::ChanSend => {
                let val = self.pop(stack)?.v;
                let ch = self.pop(stack)?.v;
                let Value::Chan(id) = &ch else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("send 需要通道，得到 {}", ch.type_hint()),
                    ));
                };
                self.machine.chans.send(*id, val)?;
            }
            Inst::ChanRecv => {
                let ch = self.pop(stack)?.v;
                let Value::Chan(id) = &ch else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("recv 需要通道，得到 {}", ch.type_hint()),
                    ));
                };
                let v = match self.machine.chans.try_recv(*id)? {
                    lang_runtime::RecvOutcome::Value(v) => self.machine.ok_value(v),
                    lang_runtime::RecvOutcome::Empty => self
                        .machine
                        .err_value(Value::str(lang_runtime::CHAN_EMPTY_MSG)),
                };
                stack.push(StackVal { v, place: None });
            }

            Inst::Try { err, fail } => {
                let e = self.pop(stack)?;
                let Value::Enum {
                    def,
                    case,
                    payloads,
                } = &e.v
                else {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("? 的操作数须为 Result，得到 {}", e.v.type_hint()),
                    ));
                };
                if *def != self.machine.defs.result_def() {
                    return Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("? 的操作数须为 Result，得到 {}", e.v.type_hint()),
                    ));
                }
                let payload = payloads.first().cloned().unwrap_or(Value::Unit);
                if *case == 0 {
                    stack.push(StackVal {
                        v: payload,
                        place: None,
                    });
                } else {
                    self.machine.envs.set(env, local_slot(*err), payload)?;
                    return Ok(Some(*fail));
                }
            }
        }
        Ok(None)
    }

    fn call_builtin_by_name(
        &mut self,
        name: &str,
        args: Vec<Value>,
        out: &mut dyn OutputSink,
    ) -> RtResult<Value> {
        let id = self
            .machine
            .builtins
            .lookup(name)
            .ok_or_else(|| RtError::internal(format!("内建 {name} 未注册")))?;
        let kind = self.machine.builtins.kind(id)?;
        if matches!(kind, BuiltinKind::Host) {
            return host_builtin(&mut self.machine, name, args);
        }
        self.machine.call_builtin(out, &InterpFmt, id, args)
    }

    fn pop_n(&mut self, stack: &mut Vec<StackVal>, n: usize) -> RtResult<Vec<StackVal>> {
        if stack.len() < n {
            return Err(RtError::internal("操作数栈下溢（IR 栈效应被破坏）"));
        }
        Ok(stack.split_off(stack.len() - n))
    }
}

fn local_slot(l: LocalId) -> u32 {
    l.0
}

fn global_slot(g: GlobalId) -> u32 {
    g.0
}

fn ir_binop(op: Binop) -> Op {
    match op {
        Binop::Eq => Op::Eq,
        Binop::Ne => Op::Ne,
        Binop::Lt => Op::Lt,
        Binop::Le => Op::Le,
        Binop::Gt => Op::Gt,
        Binop::Ge => Op::Ge,
        Binop::Add => Op::Add,
        Binop::Sub => Op::Sub,
        Binop::Mul => Op::Mul,
        Binop::Div => Op::Div,
        Binop::Rem => Op::Rem,
        Binop::BitAnd => Op::BitAnd,
        Binop::BitOr => Op::BitOr,
        Binop::BitXor => Op::BitXor,
        Binop::Shl => Op::Shl,
        Binop::Shr => Op::Shr,
    }
}

fn int_index(v: &Value, len: usize) -> RtResult<usize> {
    let Value::Int(i) = v else {
        return Err(RtError::new(
            RtErrorKind::TypeCast,
            format!("索引须为 int，得到 {}", v.type_hint()),
        ));
    };
    if *i < 0 || *i >= len as i64 {
        return Err(RtError::new(
            RtErrorKind::Index,
            format!("索引 {i} 越界（长度 {len}）"),
        ));
    }
    Ok(*i as usize)
}

fn check_slice(lo: i64, hi: i64, len: usize) -> RtResult<()> {
    if lo < 0 || hi < lo || hi > len as i64 {
        return Err(RtError::new(
            RtErrorKind::Index,
            format!("切片 [{lo}:{hi}] 越界（长度 {len}）"),
        ));
    }
    Ok(())
}
