//! lang-interp —— 双解释器：AST 参考解释器（Reference Oracle）+ IR 解释器。
//!
//! 两个解释器**只共享 lang-runtime 的 Machine/Value/内建与本 crate 的输出面桥**
//! （计划 §R12 的差分价值约束）：遍历彼此独立——AST 侧按 `specs/expressions.md`
//! EVAL-* 直接走树（行为证据 `impls/typescript/src/interp/interp.ts`），IR 侧按
//! `lang_ir::IrProgram` 的指令线性序执行（求值顺序已被 lowering 显式化为指令序）。
//!
//! 职责边界：
//! - 求值顺序按 EVAL-*：callee 先、实参左到右、方法接收者先、赋值 target
//!   地址组件先于右值（EVAL-ASSIGN-003）、短路（EVAL-SHORTCUT-006）；
//! - 值语义全部委托 lang-runtime（struct clone 拷贝、容器共享、Place 写回）；
//! - `go` 语句实参立即求值快照后同步执行（TASK-SPAWN-002 同步确定子集）；
//! - `?` 的 Err 载荷在函数边界合成 `Err` 返回值（RESULT-TRY-003）；
//! - `extern` 调用报「仅转译可用」（FFI-001）；
//! - 宿主内建（jsonParse/jsonStringify）由本 crate 实现（Host 类别在路由前拦截，
//!   RUNTIME-JSON-002 契约面经 A5 冻结的 json 纯函数落地）。

#![forbid(unsafe_code)]

pub mod format;
pub mod ir;
pub mod ops;

mod eval;

use std::collections::HashMap;
use std::rc::Rc;

use lang_core::FunctionId;
use lang_runtime::{
    BuiltinId, EnvId, Machine, OutputSink, RtError, RtErrorKind, RtResult, StructDefId, Value,
};
use lang_semantic::Analysis;
use lang_syntax::{Expr, Stmt};

use crate::format::InterpFmt;

/// 语言标准内建名面（与 `lang_runtime::BuiltinRegistry::core` 冻结名单一致；
/// 一致性由本 crate 测试 `prelude_names_match_runtime_registry` 锁定）。
const PRELUDE_BUILTINS: [&str; 20] = [
    "println",
    "print",
    "len",
    "str",
    "int",
    "float",
    "keys",
    "values",
    "has",
    "push",
    "assert",
    "err",
    "bytes",
    "char",
    "makeChan",
    "send",
    "recv",
    "isNone",
    "jsonParse",
    "jsonStringify",
];

/// 函数定义快照（声明点的 owned 拷贝；闭包按 Rc 共享）。
#[derive(Clone, Debug)]
pub(crate) struct FuncDef {
    pub name: String,
    /// 方法接收者参数名（None = 普通函数）。
    pub recv: Option<String>,
    pub params: Vec<String>,
    pub body: Vec<Stmt>,
    /// extern 声明桩：调用即报「仅转译可用」（FFI-001）。
    pub is_extern: bool,
    pub extern_host: String,
}

impl FuncDef {
    /// 诊断/错误消息里的函数名（匿名 → 「匿名函数」，与证据一致）。
    pub fn display_name(&self) -> &str {
        if self.name.is_empty() {
            "匿名函数"
        } else {
            &self.name
        }
    }
}

/// enum 构造器运行时登记（case 名全局唯一，NAME-CTOR-005）。
#[derive(Clone, Copy, Debug)]
pub(crate) struct CtorEntry {
    pub def: lang_runtime::EnumDefId,
    pub case: u32,
    pub arity: usize,
}

/// 方法登记：接收者 struct def + 方法名 → 函数。
#[derive(Clone, Debug)]
pub(crate) struct MethodEntry {
    pub func: FunctionId,
    pub env: EnvId,
}

/// 控制流信号（TS 证据 Signal 的 Rust 形）：语句层消化 return/break/continue，
/// `?` 的 Err 载荷（Propagate）一路穿到函数边界合成 `Err` 返回值。
#[derive(Debug)]
pub(crate) enum Signal {
    /// 运行时错误（RUNTIME-FAIL-001 分类面）。
    Err(RtError),
    Return(Value),
    Break,
    Continue,
    /// `?` 命中 Err：载荷向上传播。
    Propagate(Value),
}

impl From<RtError> for Signal {
    fn from(e: RtError) -> Signal {
        Signal::Err(e)
    }
}

pub(crate) type EvalResult = Result<Value, Signal>;
pub(crate) type ExecResult = Result<(), Signal>;

/// 把逸出到语句层之外的信号转为运行时错误（break/continue/return/`?` 出现在
/// 循环/函数之外是静态拒绝面 E2007/E2008/E3005，这里只是兜底分类）。
pub(crate) fn signal_to_rt(s: Signal) -> RtError {
    match s {
        Signal::Err(e) => e,
        Signal::Return(_) => RtError::new(RtErrorKind::Internal, "return 出现在函数之外"),
        Signal::Break => RtError::new(RtErrorKind::Internal, "break 出现在循环之外"),
        Signal::Continue => RtError::new(RtErrorKind::Internal, "continue 出现在循环之外"),
        Signal::Propagate(_) => {
            RtError::new(RtErrorKind::Internal, "Err 传播出函数之外（顶层禁用 ?）")
        }
    }
}

/// AST 参考解释器：持有运行时状态机 + 程序级登记表（struct/enum/构造器/方法）。
pub struct Interp {
    /// 运行时状态（堆/环境/channel/调度/定义表/内建表）。
    pub machine: Machine,
    /// struct 名 → 运行时 def（按声明语句执行序登记，TS 证据同构）。
    structs: HashMap<String, StructDefId>,
    /// enum 名 → 运行时 def。
    enums: HashMap<String, lang_runtime::EnumDefId>,
    /// 构造器名 → (def, case 序号, 载荷数)。含预载 Some/None/Ok/Err。
    ctors: HashMap<String, CtorEntry>,
    /// (接收者 struct def, 方法名) → 方法函数。
    methods: HashMap<(StructDefId, String), MethodEntry>,
    /// FunctionId → 函数定义（与 machine 的 fn_names 对齐：只经本表登记）。
    funcs: Vec<Rc<FuncDef>>,
}

impl Interp {
    /// 新解释器（携带既有运行时状态机；预载定义与核心内建已就绪）。
    pub fn new(machine: Machine) -> Interp {
        let mut interp = Interp {
            machine,
            structs: HashMap::new(),
            enums: HashMap::new(),
            ctors: HashMap::new(),
            methods: HashMap::new(),
            funcs: Vec::new(),
        };
        interp.prelude_ctors();
        interp
    }

    /// 预载构造器（RESULT-PRELUDE-001：Some=0/None=1、Ok=0/Err=1，与
    /// DefTables 冻结 case 序一致）。
    fn prelude_ctors(&mut self) {
        let option = self.machine.defs.option_def();
        let result = self.machine.defs.result_def();
        for (name, def, case, arity) in [
            ("Some", option, 0, 1),
            ("None", option, 1, 0),
            ("Ok", result, 0, 1),
            ("Err", result, 1, 1),
        ] {
            self.ctors
                .insert(name.to_string(), CtorEntry { def, case, arity });
        }
    }

    /// 顺序登记函数并录入本 crate 函数表（与 machine 的 FunctionId 分配对齐）。
    fn register_fn(&mut self, def: FuncDef) -> FunctionId {
        let id = self.machine.register_function(&def.name);
        self.funcs.push(Rc::new(def));
        id
    }

    fn fn_def(&self, id: FunctionId) -> RtResult<Rc<FuncDef>> {
        self.funcs
            .get(id.0 as usize)
            .cloned()
            .ok_or_else(|| RtError::internal(format!("FunctionId({}) 未登记", id.0)))
    }

    /// 解释执行入口（AST 参考解释器）。
    ///
    /// `analysis` 为同一棵 AST 的语义分析产物：含诊断时拒绝执行（诊断是
    /// check 语义的真值，执行只对干净程序负责）。执行顺序与证据一致：
    /// 先顺序执行顶层语句（声明/全局初始化），再调用 `main`（若已定义）。
    pub fn run(
        &mut self,
        stmts: &[Stmt],
        analysis: &Analysis,
        out: &mut dyn OutputSink,
    ) -> RtResult<()> {
        if analysis.has_errors() {
            return Err(RtError::new(
                RtErrorKind::Scope,
                format!("程序含 {} 条静态诊断，拒绝执行", analysis.diags.len()),
            ));
        }
        self.prelude_builtins()?;
        self.exec_stmts(stmts, self.machine.root, out)
            .map_err(signal_to_rt)?;
        // main 在全部顶层语句之后调用（跨文件合并序下所有定义已就绪）
        if let Some((owner, slot)) = self.machine.envs.lookup(self.machine.root, "main")? {
            let v = self.machine.envs.get(owner, slot)?;
            if let Value::Closure { func, env } = v {
                self.run_func(func, env, None, Vec::new(), out)
                    .map_err(signal_to_rt)?;
            }
        }
        Ok(())
    }

    /// 预载内建进根环境（值面：`println` 等作为一等值可传递/打印）。
    fn prelude_builtins(&mut self) -> RtResult<()> {
        for name in PRELUDE_BUILTINS {
            let id =
                self.machine.builtins.lookup(name).ok_or_else(|| {
                    RtError::internal(format!("内建 {name} 未在运行时注册表登记"))
                })?;
            self.machine
                .envs
                .redefine(self.machine.root, name, Value::Builtin(id))?;
        }
        Ok(())
    }

    // ———— 语句 ————

    pub(crate) fn exec_stmts(
        &mut self,
        stmts: &[Stmt],
        env: EnvId,
        out: &mut dyn OutputSink,
    ) -> ExecResult {
        for s in stmts {
            self.exec_stmt(s, env, out)?;
        }
        Ok(())
    }

    pub(crate) fn exec_stmt(
        &mut self,
        s: &Stmt,
        env: EnvId,
        out: &mut dyn OutputSink,
    ) -> ExecResult {
        match s {
            Stmt::Let {
                name,
                mutable,
                init,
                ..
            } => {
                let v = self.eval(init, env, out)?;
                if name != "_" {
                    self.machine
                        .envs
                        .define(env, name, v, *mutable)
                        .map_err(Signal::from)?;
                }
                Ok(())
            }
            Stmt::Assign {
                target, op, value, ..
            } => self.exec_assign(target, *op, value, env, out),
            Stmt::ExprStmt { expr, .. } => {
                self.eval(expr, env, out)?;
                Ok(())
            }
            Stmt::Return { value, .. } => {
                let v = match value {
                    Some(e) => self.eval(e, env, out)?,
                    None => Value::Unit,
                };
                Err(Signal::Return(v))
            }
            Stmt::If {
                cond,
                then_body,
                else_body,
                ..
            } => {
                if self.cond(cond, env, out)? {
                    let child = self.machine.envs.child(env);
                    self.exec_stmts(then_body, child, out)
                } else if let Some(els) = else_body {
                    let child = self.machine.envs.child(env);
                    self.exec_stmts(els, child, out)
                } else {
                    Ok(())
                }
            }
            Stmt::While { cond, body, .. } => {
                while self.cond(cond, env, out)? {
                    let child = self.machine.envs.child(env);
                    match self.exec_stmts(body, child, out) {
                        Ok(()) => {}
                        Err(Signal::Break) => return Ok(()),
                        Err(Signal::Continue) => {}
                        Err(other) => return Err(other),
                    }
                }
                Ok(())
            }
            Stmt::ForIn {
                variable,
                iter,
                body,
                ..
            } => self.exec_for_in(variable, iter, body, env, out),
            Stmt::Break { .. } => Err(Signal::Break),
            Stmt::Continue { .. } => Err(Signal::Continue),
            Stmt::FnDecl {
                name,
                recv,
                params,
                body,
                ..
            } => self.decl_fn(name, recv.as_ref(), params, body, env),
            Stmt::StructDecl { name, fields, .. } => self.decl_struct(name, fields),
            Stmt::EnumDecl { name, cases, .. } => self.decl_enum(name, cases),
            // go = 同步确定子集：实参在此处求值后立即执行至返回，返回值丢弃
            // （TASK-SPAWN-002；等价直接调用，输出确定）。
            Stmt::Go { call, .. } => {
                self.eval(call, env, out)?;
                Ok(())
            }
            Stmt::Import { .. } => Err(Signal::Err(RtError::new(
                RtErrorKind::Scope,
                "import 由装载器在合并前展开（不应执行到这里）",
            ))),
            Stmt::Extern { host, fns, .. } => {
                for f in fns {
                    let Stmt::FnDecl { name, params, .. } = f else {
                        return Err(Signal::Err(RtError::internal("extern 签名恒为 FnDecl")));
                    };
                    let def = FuncDef {
                        name: name.clone(),
                        recv: None,
                        params: params.iter().map(|p| p.name.clone()).collect(),
                        body: Vec::new(),
                        is_extern: true,
                        extern_host: host.clone(),
                    };
                    let id = self.register_fn(def);
                    self.machine
                        .envs
                        .redefine(env, name, Value::Closure { func: id, env })?;
                }
                Ok(())
            }
        }
    }

    /// 条件求值：仅 bool（CTRL-IF-001，无隐式真值转换）。
    pub(crate) fn cond(
        &mut self,
        e: &Expr,
        env: EnvId,
        out: &mut dyn OutputSink,
    ) -> Result<bool, Signal> {
        let v = self.eval(e, env, out)?;
        v.truthy().map_err(Signal::from)
    }

    fn exec_for_in(
        &mut self,
        variable: &str,
        iter: &Expr,
        body: &[Stmt],
        env: EnvId,
        out: &mut dyn OutputSink,
    ) -> ExecResult {
        // 迭代对象在循环开始时求值一次并快照元素（CTRL-FORIN-003）：
        // 数组索引序 / 映射插入序键 / 字符串码点 / 区间半开。
        let v = self.eval(iter, env, out)?;
        let v = self.machine.deref_value(&v)?;
        let items: Vec<Value> = match &v {
            Value::Slice(id) => self.machine.heap.slice(*id)?.clone(),
            Value::Map(id) => self.machine.heap.map_keys(*id)?,
            Value::Str(s) => s.chars().map(|c| Value::str(c.to_string())).collect(),
            Value::Range { lo, hi } => {
                if hi > lo {
                    (*lo..*hi).map(Value::Int).collect()
                } else {
                    Vec::new()
                }
            }
            other => {
                return Err(Signal::Err(RtError::new(
                    RtErrorKind::NotIterable,
                    format!("{} 不可迭代", other.type_hint()),
                )));
            }
        };
        for item in items {
            // 每迭代新子环境：循环变量每轮重新绑定，闭包捕获互不串扰
            // （CTRL-FORIN-003 / VALUE-CAPTURE-004 的 loop capture 面）。
            let benv = self.machine.envs.child(env);
            if variable != "_" {
                self.machine
                    .envs
                    .define(benv, variable, item, false)
                    .map_err(Signal::from)?;
            }
            match self.exec_stmts(body, benv, out) {
                Ok(()) => {}
                Err(Signal::Break) => return Ok(()),
                Err(Signal::Continue) => {}
                Err(other) => return Err(other),
            }
        }
        Ok(())
    }

    fn decl_fn(
        &mut self,
        name: &str,
        recv: Option<&lang_syntax::Param>,
        params: &[lang_syntax::Param],
        body: &[Stmt],
        env: EnvId,
    ) -> ExecResult {
        let def = if let Some(r) = recv {
            // 方法：接收者须为已声明 struct（E2004 静态面；运行时兜底报错）
            let Some(lang_syntax::TypeEx::Named { name: rname, .. }) = &r.typ else {
                return Err(Signal::Err(RtError::new(
                    RtErrorKind::TypeCast,
                    "方法接收者须为具名 struct 类型",
                )));
            };
            let Some(&sdef) = self.structs.get(rname) else {
                return Err(Signal::Err(RtError::new(
                    RtErrorKind::Scope,
                    format!("为未知 struct {rname} 定义方法"),
                )));
            };
            let fdef = FuncDef {
                name: name.to_string(),
                recv: Some(r.name.clone()),
                params: params.iter().map(|p| p.name.clone()).collect(),
                body: body.to_vec(),
                is_extern: false,
                extern_host: String::new(),
            };
            let id = self.register_fn(fdef);
            let entry = MethodEntry { func: id, env };
            self.methods.insert((sdef, name.to_string()), entry);
            return Ok(());
        } else {
            FuncDef {
                name: name.to_string(),
                recv: None,
                params: params.iter().map(|p| p.name.clone()).collect(),
                body: body.to_vec(),
                is_extern: false,
                extern_host: String::new(),
            }
        };
        let id = self.register_fn(def);
        self.machine
            .envs
            .redefine(env, name, Value::Closure { func: id, env })
            .map_err(Signal::from)?;
        Ok(())
    }

    fn decl_struct(&mut self, name: &str, fields: &[lang_syntax::Param]) -> ExecResult {
        if self.structs.contains_key(name) {
            return Err(Signal::Err(RtError::new(
                RtErrorKind::Scope,
                format!("struct {name} 重复定义"),
            )));
        }
        let field_names: Vec<String> = fields.iter().map(|f| f.name.clone()).collect();
        let id = self.machine.defs.register_struct(name, field_names);
        self.structs.insert(name.to_string(), id);
        Ok(())
    }

    fn decl_enum(&mut self, name: &str, cases: &[lang_syntax::EnumCase]) -> ExecResult {
        if self.enums.contains_key(name) {
            return Err(Signal::Err(RtError::new(
                RtErrorKind::Scope,
                format!("enum {name} 重复定义"),
            )));
        }
        let case_names: Vec<String> = cases.iter().map(|c| c.name.clone()).collect();
        // case 名全局唯一（NAME-CTOR-005）：与既有构造器及内建名冲突即拒
        for c in cases {
            if self.ctors.contains_key(&c.name) || self.machine.builtins.lookup(&c.name).is_some() {
                return Err(Signal::Err(RtError::new(
                    RtErrorKind::Scope,
                    format!("构造器 {} 重名", c.name),
                )));
            }
        }
        let def = self.machine.defs.register_enum(name, case_names);
        for (i, c) in cases.iter().enumerate() {
            self.ctors.insert(
                c.name.clone(),
                CtorEntry {
                    def,
                    case: i as u32,
                    arity: c.payloads.len(),
                },
            );
        }
        self.enums.insert(name.to_string(), def);
        Ok(())
    }

    // ———— 调用 ————

    /// 调用闭包值（CALL-CLOSURE-003：定义处环境 + 调用点实参）。
    pub(crate) fn call_closure_value(
        &mut self,
        func: FunctionId,
        cenv: EnvId,
        args: Vec<Value>,
        out: &mut dyn OutputSink,
    ) -> EvalResult {
        self.run_func(func, cenv, None, args, out)
    }

    /// 函数体执行：帧环境 = 定义处环境的子环境；接收者/参数依次落位；
    /// Return 正常返回、Propagate 在边界合成 `Err`（RESULT-TRY-003）。
    pub(crate) fn run_func(
        &mut self,
        func: FunctionId,
        def_env: EnvId,
        recv: Option<Value>,
        args: Vec<Value>,
        out: &mut dyn OutputSink,
    ) -> EvalResult {
        let f = self.fn_def(func)?;
        if f.is_extern {
            return Err(Signal::Err(RtError::new(
                RtErrorKind::UnsupportedHost,
                format!(
                    "extern 函数 {} 仅转译可用（宿主 {}）",
                    f.name, f.extern_host
                ),
            )));
        }
        let mut slots: Vec<(String, Value)> = Vec::with_capacity(args.len() + 1);
        if let (Some(rname), Some(rv)) = (&f.recv, recv) {
            slots.push((rname.clone(), rv));
        }
        if args.len() != f.params.len() {
            return Err(Signal::Err(RtError::new(
                RtErrorKind::TypeCast,
                format!(
                    "{} 需要 {} 个参数，得到 {} 个",
                    f.display_name(),
                    f.params.len(),
                    args.len()
                ),
            )));
        }
        for (p, a) in f.params.iter().zip(args) {
            slots.push((p.clone(), a));
        }
        let fenv = self.machine.envs.child(def_env);
        for (n, v) in slots {
            self.machine
                .envs
                .define(fenv, &n, v, false)
                .map_err(Signal::from)?;
        }
        match self.exec_stmts(&f.body, fenv, out) {
            Ok(()) => Ok(Value::Unit),
            Err(Signal::Return(v)) => Ok(v),
            Err(Signal::Propagate(e)) => Ok(self.machine.err_value(e)),
            Err(other) => Err(other),
        }
    }

    /// 内建路由：Host 类别（jsonParse/jsonStringify）由本 crate 实现；
    /// 其余交 `Machine::call_builtin`（println/str 的输出面经 InterpFmt）。
    pub(crate) fn call_builtin_route(
        &mut self,
        id: BuiltinId,
        args: Vec<Value>,
        out: &mut dyn OutputSink,
    ) -> EvalResult {
        let kind = self.machine.builtins.kind(id)?;
        if matches!(kind, lang_runtime::BuiltinKind::Host) {
            let name = self.machine.builtins.name(id)?.to_string();
            return format::host_builtin(&mut self.machine, &name, args).map_err(Signal::from);
        }
        self.machine
            .call_builtin(out, &InterpFmt, id, args)
            .map_err(Signal::from)
    }
}
