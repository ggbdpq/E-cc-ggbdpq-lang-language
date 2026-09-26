//! AST 参考解释器：表达式求值与赋值（specs/expressions.md EVAL-*）。
//!
//! 求值顺序即本文件各 `eval` 分支内的语句序：
//! - 调用：callee（含方法接收者）先于实参，实参左到右（EVAL-CALL-002）；
//! - 赋值：target 地址组件先于右值（EVAL-ASSIGN-003）；
//! - 索引：obj 先于 idx（EVAL-INDEX-005）；字面量元素左到右（EVAL-LITERAL-004）；
//! - `&&`/`||` 短路（EVAL-SHORTCUT-006）。
//!
//! 引用穿透：读变量/字段/元素时经 `Machine::deref_value` 自动解引用
//! （VALUE-REF-003 的使用值面）；`&x` 产生 `Value::Ref(Place::Var)`。

use lang_runtime::{Place, RtError, RtErrorKind, RtResult, Value};
use lang_syntax::{Arm, AssignOp, BinaryOp, Expr, Pat, UnaryOp};

use crate::{CtorEntry, EvalResult, ExecResult, Interp, Signal};

impl Interp {
    /// 「使用值」读取：求值并穿透引用（操作数/条件/实参/接收者的语义面）。
    pub(crate) fn deref_eval(
        &mut self,
        e: &Expr,
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> EvalResult {
        let v = self.eval(e, env, out)?;
        self.machine.deref_value(&v).map_err(Signal::from)
    }

    pub(crate) fn eval(
        &mut self,
        e: &Expr,
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> EvalResult {
        match e {
            Expr::IntLit { value, .. } => Ok(Value::Int(*value)),
            Expr::FloatLit { value, .. } => Ok(Value::Float(*value)),
            Expr::StrLit { value, .. } => Ok(Value::str(value.as_str())),
            Expr::BoolLit { value, .. } => Ok(Value::Bool(*value)),
            Expr::Ident { name, .. } => self.eval_ident(name, env),
            Expr::Unary { op, operand, .. } => self.eval_unary(*op, operand, env, out),
            Expr::Binary { op, lhs, rhs, .. } => self.eval_binary(*op, lhs, rhs, env, out),
            Expr::Range { lo, hi, .. } => {
                let lo = self.eval(lo, env, out)?;
                let hi = self.eval(hi, env, out)?;
                let (Value::Int(lo), Value::Int(hi)) = (lo, hi) else {
                    return Err(Signal::Err(RtError::new(
                        RtErrorKind::TypeCast,
                        "区间两端须为 int",
                    )));
                };
                Ok(Value::Range { lo, hi })
            }
            Expr::Call { callee, args, .. } => self.eval_call(callee, args, env, out),
            Expr::Index { obj, idx, .. } => {
                // EVAL-INDEX-005：先 obj 后 idx
                let ov = self.deref_eval(obj, env, out)?;
                let iv = self.eval(idx, env, out)?;
                self.index_value(ov, iv)
            }
            Expr::Slice { obj, lo, hi, .. } => {
                // a[i:j]：a、i、j 依序（EVAL-INDEX-005）
                let ov = self.deref_eval(obj, env, out)?;
                let lov = self.eval(lo, env, out)?;
                let hiv = self.eval(hi, env, out)?;
                self.slice_value(ov, lov, hiv)
            }
            Expr::Field { obj, name, .. } => {
                let ov = self.deref_eval(obj, env, out)?;
                self.field_value(ov, name)
            }
            Expr::Try { operand, .. } => {
                let v = self.eval(operand, env, out)?;
                match v {
                    Value::Enum {
                        def,
                        case,
                        payloads,
                    } if def == self.machine.defs.result_def() => match case {
                        0 => Ok(payloads.into_iter().next().unwrap_or(Value::Unit)),
                        _ => Err(Signal::Propagate(
                            payloads.into_iter().next().unwrap_or(Value::Unit),
                        )),
                    },
                    other => Err(Signal::Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("? 的操作数须为 Result，得到 {}", other.type_hint()),
                    ))),
                }
            }
            Expr::StructLit {
                type_name, fields, ..
            } => self.eval_struct_lit(type_name, fields, env, out),
            Expr::MapLit { items, .. } => {
                let m = self.machine.alloc_map_value();
                let Value::Map(mid) = &m else {
                    return Err(Signal::Err(RtError::internal("新分配映射缺索引")));
                };
                for item in items {
                    // EVAL-LITERAL-004：键值对逐对左到右（键先值后）
                    let k = self.eval(&item.key, env, out)?;
                    let v = self.eval(&item.value, env, out)?;
                    self.machine
                        .heap
                        .map_insert(*mid, &k, v)
                        .map_err(Signal::from)?;
                }
                Ok(m)
            }
            Expr::ArrLit { items, .. } => {
                // EVAL-LITERAL-004：元素左到右
                let mut vals = Vec::with_capacity(items.len());
                for it in items {
                    vals.push(self.eval(it, env, out)?);
                }
                Ok(self.machine.alloc_slice_value(vals))
            }
            Expr::Match {
                scrutinee, arms, ..
            } => self.eval_match(scrutinee, arms, env, out),
            Expr::FnLit { params, body, .. } => {
                let def = crate::FuncDef {
                    name: String::new(),
                    recv: None,
                    params: params.iter().map(|p| p.name.clone()).collect(),
                    body: body.clone(),
                    is_extern: false,
                    extern_host: String::new(),
                };
                let id = self.register_fn(def);
                Ok(Value::Closure { func: id, env })
            }
        }
    }

    fn eval_ident(&mut self, name: &str, env: lang_runtime::EnvId) -> EvalResult {
        // 环境优先，其次零参构造器裸写（None 这类 case），最后报未定义
        if let Some((owner, slot)) = self.machine.envs.lookup(env, name)? {
            return self.machine.envs.get(owner, slot).map_err(Signal::from);
        }
        if let Some(entry) = self.ctors.get(name).copied() {
            return self.ctor_value(entry, Vec::new(), name);
        }
        Err(Signal::Err(RtError::new(
            RtErrorKind::Scope,
            format!("未定义 {name}"),
        )))
    }

    fn ctor_value(&self, entry: CtorEntry, payloads: Vec<Value>, name: &str) -> EvalResult {
        if payloads.len() != entry.arity {
            return Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                format!(
                    "{name} 需要 {} 个参数，得到 {} 个",
                    entry.arity,
                    payloads.len()
                ),
            )));
        }
        Ok(Value::Enum {
            def: entry.def,
            case: entry.case,
            payloads,
        })
    }

    fn eval_unary(
        &mut self,
        op: UnaryOp,
        operand: &Expr,
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> EvalResult {
        let v = self.eval(operand, env, out)?;
        match op {
            UnaryOp::Neg => match self.machine.deref_value(&v)? {
                Value::Int(i) => Ok(Value::Int(i.wrapping_neg())),
                Value::Float(f) => Ok(Value::Float(-f)),
                other => Err(Signal::Err(RtError::new(
                    RtErrorKind::TypeCast,
                    format!("- 不能用于 {}", other.type_hint()),
                ))),
            },
            UnaryOp::Not => {
                let b = self.machine.deref_value(&v)?.truthy()?;
                Ok(Value::Bool(!b))
            }
            UnaryOp::Ref => {
                // E3012：仅变量可取引用（静态面）；运行时只对 Ident 产生 Place
                let Expr::Ident { name, .. } = operand else {
                    return Err(Signal::Err(RtError::new(
                        RtErrorKind::TypeCast,
                        "v0 只能对变量取引用",
                    )));
                };
                let Some((owner, slot)) = self.machine.envs.lookup(env, name)? else {
                    return Err(Signal::Err(RtError::new(
                        RtErrorKind::Scope,
                        format!("未定义 {name}"),
                    )));
                };
                Ok(self.machine.take_ref(owner, slot))
            }
        }
    }

    fn eval_binary(
        &mut self,
        op: BinaryOp,
        lhs: &Expr,
        rhs: &Expr,
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> EvalResult {
        // EVAL-SHORTCUT-006：短路运算左侧决定是否求值右侧
        match op {
            BinaryOp::AndAnd => {
                if !self.cond(lhs, env, out)? {
                    return Ok(Value::Bool(false));
                }
                return Ok(Value::Bool(self.cond(rhs, env, out)?));
            }
            BinaryOp::OrOr => {
                if self.cond(lhs, env, out)? {
                    return Ok(Value::Bool(true));
                }
                return Ok(Value::Bool(self.cond(rhs, env, out)?));
            }
            _ => {}
        }
        let l = self.eval(lhs, env, out)?;
        let r = self.eval(rhs, env, out)?;
        let l = self.machine.deref_value(&l)?;
        let r = self.machine.deref_value(&r)?;
        crate::ops::binary(op.into(), &l, &r).map_err(Signal::from)
    }

    fn eval_call(
        &mut self,
        callee: &Expr,
        args: &[Expr],
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> EvalResult {
        // 构造器调用（调用位置的 Ident 先查构造器表，证据口径）
        if let Expr::Ident { name, .. } = callee {
            if let Some(entry) = self.ctors.get(name).copied() {
                let payloads = self.eval_args(args, env, out)?;
                return self.ctor_value(entry, payloads, name);
            }
        }
        // 方法调用：接收者先于实参（EVAL-CALL-002）；非方法落通用路径
        if let Expr::Field { obj, name, .. } = callee {
            let ov = self.deref_eval(obj, env, out)?;
            if let Value::Struct(sv) = &ov {
                if let Some(m) = self.methods.get(&(sv.def, name.clone())).cloned() {
                    let argv = self.eval_args(args, env, out)?;
                    return self.run_func(m.func, m.env, Some(ov), argv, out);
                }
            }
            // 字段存函数值：落到通用路径（callee 表达式整体再求值）
        }
        // 通用：callee 先于实参（EVAL-CALL-002）
        let cv = self.eval(callee, env, out)?;
        let cv = self.machine.deref_value(&cv)?;
        let argv = self.eval_args(args, env, out)?;
        match cv {
            Value::Closure { func, env: cenv } => self.call_closure_value(func, cenv, argv, out),
            Value::Builtin(id) => self.call_builtin_route(id, argv, out),
            other => Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                format!("{} 不可调用", other.type_hint()),
            ))),
        }
    }

    fn eval_args(
        &mut self,
        args: &[Expr],
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> Result<Vec<Value>, Signal> {
        let mut vals = Vec::with_capacity(args.len());
        for a in args {
            let v = self.eval(a, env, out)?;
            let v = self.machine.deref_value(&v)?;
            vals.push(v);
        }
        Ok(vals)
    }

    // ———— 索引 / 切片 / 字段 ————

    fn index_value(&mut self, obj: Value, idx: Value) -> EvalResult {
        match obj {
            Value::Slice(id) => {
                let i = int_index(&idx, self.machine.heap.slice_len(id)?)?;
                Ok(self.machine.heap.slice_get(id, i)?)
            }
            Value::Map(id) => {
                let key = self.machine.map_key_of(&idx)?;
                Ok(self.machine.heap.map_get(id, &key)?)
            }
            Value::Str(s) => {
                let n = s.chars().count();
                let i = int_index(&idx, n)?;
                match s.chars().nth(i) {
                    Some(c) => Ok(Value::str(c.to_string())),
                    None => Err(RtError::internal("字符串码点越界（边界已校验）").into()),
                }
            }
            other => Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                format!("{} 不可索引", other.type_hint()),
            ))),
        }
    }

    fn slice_value(&mut self, obj: Value, lo: Value, hi: Value) -> EvalResult {
        let (Value::Int(lo), Value::Int(hi)) = (lo, hi) else {
            return Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                "切片端点须为 int",
            )));
        };
        match obj {
            Value::Slice(id) => {
                let n = self.machine.heap.slice_len(id)?;
                check_slice_bounds(lo, hi, n)?;
                let items = self.machine.heap.slice(id)?[lo as usize..hi as usize].to_vec();
                Ok(self.machine.alloc_slice_value(items))
            }
            Value::Str(s) => {
                let n = s.chars().count();
                check_slice_bounds(lo, hi, n)?;
                let text: String = s
                    .chars()
                    .skip(lo as usize)
                    .take((hi - lo) as usize)
                    .collect();
                Ok(Value::str(text))
            }
            other => Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                format!("{} 不可切片", other.type_hint()),
            ))),
        }
    }

    fn field_value(&mut self, obj: Value, name: &str) -> EvalResult {
        let Value::Struct(sv) = obj else {
            return Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                format!("{name} 没有字段（{} 不是 struct）", obj.type_hint()),
            )));
        };
        let fields = self.machine.defs.struct_def(sv.def)?.fields.clone();
        let Some(i) = fields.iter().position(|f| f == name) else {
            let dname = self.machine.defs.struct_def(sv.def)?.name.to_string();
            return Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                format!("{dname} 没有字段 {name}"),
            )));
        };
        Ok(sv.fields[i].clone())
    }

    fn eval_struct_lit(
        &mut self,
        type_name: &str,
        fields: &[lang_syntax::FieldLit],
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> EvalResult {
        let Some(&sdef) = self.structs.get(type_name) else {
            return Err(Signal::Err(RtError::new(
                RtErrorKind::Scope,
                format!("未知 struct {type_name}"),
            )));
        };
        let names = self.machine.defs.struct_def(sdef)?.fields.clone();
        if fields.len() != names.len() {
            return Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                format!(
                    "{type_name} 需要 {} 个字段，得到 {} 个",
                    names.len(),
                    fields.len()
                ),
            )));
        }
        // EVAL-LITERAL-004：字段值按声明序左到右；字段名须与声明序一致
        let mut vals = Vec::with_capacity(fields.len());
        for (i, f) in fields.iter().enumerate() {
            if f.name != names[i] {
                return Err(Signal::Err(RtError::new(
                    RtErrorKind::TypeCast,
                    format!(
                        "{type_name} 第 {} 个字段应为 {}，得到 {}",
                        i + 1,
                        names[i],
                        f.name
                    ),
                )));
            }
            vals.push(self.eval(&f.value, env, out)?);
        }
        Ok(Value::Struct(lang_runtime::StructVal {
            def: sdef,
            fields: vals,
        }))
    }

    // ———— match ————

    fn eval_match(
        &mut self,
        scrutinee: &Expr,
        arms: &[Arm],
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> EvalResult {
        let scrut = self.eval(scrutinee, env, out)?;
        for arm in arms {
            // 臂内新作用域：绑定限本臂（MATCH-BIND-003）
            let aenv = self.machine.envs.child(env);
            if self.match_pat(&scrut, &arm.pat, aenv, out)? {
                return self.eval(&arm.body, aenv, out);
            }
        }
        Err(Signal::Err(RtError::new(
            RtErrorKind::MatchNoArm,
            "match 无匹配分支",
        )))
    }

    fn match_pat(
        &mut self,
        v: &Value,
        pat: &Pat,
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> Result<bool, Signal> {
        match pat {
            Pat::Wild { .. } => Ok(true),
            Pat::Bind { name, .. } => {
                if name != "_" {
                    self.machine
                        .envs
                        .define(env, name, v.clone(), false)
                        .map_err(Signal::from)?;
                }
                Ok(true)
            }
            Pat::Lit { value, .. } => {
                // 字面量模式按 == 比较（类型不可比 = 不匹配，与证据一致）
                let lv = self.eval(value, env, out)?;
                let lv = self.machine.deref_value(&lv)?;
                Ok(Value::equals(v, &lv).unwrap_or(false))
            }
            Pat::Case { name, subs, .. } => {
                let Value::Enum {
                    def,
                    case,
                    payloads,
                } = v
                else {
                    return Ok(false);
                };
                let case_names = self.machine.defs.enum_def(*def)?.cases.clone();
                let Some(idx) = case_names.iter().position(|c| c == name) else {
                    return Ok(false);
                };
                if idx as u32 != *case {
                    return Ok(false);
                }
                if subs.len() != payloads.len() {
                    return Err(Signal::Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!(
                            "case {name} 需要 {} 个子模式，得到 {} 个",
                            payloads.len(),
                            subs.len()
                        ),
                    )));
                }
                for (sub, p) in subs.iter().zip(payloads) {
                    if !self.match_pat(p, sub, env, out)? {
                        return Ok(false);
                    }
                }
                Ok(true)
            }
        }
    }

    // ———— 赋值（EVAL-ASSIGN-003） ————

    /// 解析赋值目标的地址（地址组件按 EVAL-* 求值：Ident 绑定名；`obj.f` 的
    /// obj；`a[i]` 的 obj、i——均先于右值执行）。
    fn assign_place(
        &mut self,
        target: &Expr,
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> Result<Place, Signal> {
        match target {
            Expr::Ident { name, .. } => {
                let Some((owner, slot)) = self.machine.envs.lookup(env, name)? else {
                    return Err(Signal::Err(RtError::new(
                        RtErrorKind::Scope,
                        format!("未定义变量 {name}"),
                    )));
                };
                Ok(Place::Var { env: owner, slot })
            }
            Expr::Field { obj, name, .. } => {
                let base = self.assign_place(obj, env, out)?;
                // 字段以名寻址：读 base 当前 struct 定位槽位（地址组件面）
                let objv = self.machine.place_load_resolved(&base)?;
                let Value::Struct(sv) = objv else {
                    return Err(Signal::Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("只能给 struct 字段赋值，得到 {}", objv.type_hint()),
                    )));
                };
                let names = self.machine.defs.struct_def(sv.def)?.fields.clone();
                let Some(i) = names.iter().position(|f| f == name) else {
                    let dname = self.machine.defs.struct_def(sv.def)?.name.to_string();
                    return Err(Signal::Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("{dname} 没有字段 {name}"),
                    )));
                };
                Ok(Place::Field {
                    base: Box::new(base),
                    index: i as u32,
                })
            }
            Expr::Index { obj, idx, .. } => {
                let ov = self.deref_eval(obj, env, out)?;
                let iv = self.eval(idx, env, out)?;
                match ov {
                    Value::Slice(id) => {
                        let i = int_index(&iv, self.machine.heap.slice_len(id)?)?;
                        Ok(Place::Element {
                            slice: id,
                            index: i as u32,
                        })
                    }
                    Value::Map(id) => {
                        let key = self.machine.map_key_of(&iv)?;
                        Ok(Place::MapEntry { map: id, key })
                    }
                    other => Err(Signal::Err(RtError::new(
                        RtErrorKind::TypeCast,
                        format!("{} 不可索引赋值", other.type_hint()),
                    ))),
                }
            }
            _ => Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                "赋值目标不合法",
            ))),
        }
    }

    pub(crate) fn exec_assign(
        &mut self,
        target: &Expr,
        op: AssignOp,
        value: &Expr,
        env: lang_runtime::EnvId,
        out: &mut dyn lang_runtime::OutputSink,
    ) -> ExecResult {
        // 1) 地址组件先求值（EVAL-ASSIGN-003）
        match target {
            Expr::Ident { name, .. } => {
                let Some((owner, slot)) = self.machine.envs.lookup(env, name)? else {
                    return Err(Signal::Err(RtError::new(
                        RtErrorKind::Scope,
                        format!("未定义变量 {name}"),
                    )));
                };
                // 2) 右值
                let rhs = self.eval(value, env, out)?;
                if op == AssignOp::Assign {
                    // Ident 重绑定写 cell 本身（不穿透引用——VALUE-REF-003 只覆盖
                    // 字段/索引面；重绑定面 = cell 替换，与证据一致）
                    return self
                        .machine
                        .envs
                        .set(owner, slot, rhs)
                        .map_err(Signal::from);
                }
                // 3) 复合：读旧值 → 运算 → 写回
                let old = self.machine.envs.get(owner, slot)?;
                let old = self.machine.deref_value(&old)?;
                // 数组 +=：原位扩展（VALUE-SHARE-002），别名同见
                if op == AssignOp::AddAssign {
                    if let Value::Slice(lhs) = &old {
                        return self.array_extend(*lhs, rhs).map_err(Signal::from);
                    }
                }
                let combined = crate::ops::compound(op.into(), &old, &rhs).map_err(Signal::from)?;
                self.machine
                    .envs
                    .set(owner, slot, combined)
                    .map_err(Signal::from)
            }
            _ => {
                let place = self.assign_place(target, env, out)?;
                let rhs = self.eval(value, env, out)?;
                if op == AssignOp::Assign {
                    return self.machine.place_store(&place, rhs).map_err(Signal::from);
                }
                // 复合：读旧值（Field/Index 的 base 经引用穿透）→ 运算 → 写回
                let old = self.machine.place_load(&place)?;
                let old = self.machine.deref_value(&old)?;
                if op == AssignOp::AddAssign {
                    if let Value::Slice(lhs) = &old {
                        return self.array_extend(*lhs, rhs).map_err(Signal::from);
                    }
                }
                let combined = crate::ops::compound(op.into(), &old, &rhs).map_err(Signal::from)?;
                self.machine
                    .place_store(&place, combined)
                    .map_err(Signal::from)
            }
        }
    }

    /// `xs += ys`（数组）：把 ys 的元素逐个原位 push 进 xs（证据口径：
    /// 数组复合加法的右操作数须为数组）。
    fn array_extend(&mut self, lhs: lang_runtime::ValueId, rhs: Value) -> RtResult<()> {
        let Value::Slice(rhs_id) = &rhs else {
            return Err(RtError::new(
                RtErrorKind::TypeCast,
                format!("数组 += 需要数组，得到 {}", rhs.type_hint()),
            ));
        };
        let items = self.machine.heap.slice(*rhs_id)?.clone();
        for v in items {
            self.machine.heap.slice_push(lhs, v)?;
        }
        Ok(())
    }
}

/// 索引合法性（RUNTIME-INDEX-001）：须为 int 且 0 <= i < len。
fn int_index(v: &Value, len: usize) -> Result<usize, RtError> {
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

fn check_slice_bounds(lo: i64, hi: i64, len: usize) -> Result<(), RtError> {
    if lo < 0 || hi < lo || hi > len as i64 {
        return Err(RtError::new(
            RtErrorKind::Index,
            format!("切片 [{lo}:{hi}] 越界（长度 {len}）"),
        ));
    }
    Ok(())
}
