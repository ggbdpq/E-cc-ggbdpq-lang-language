//! HIR → Core IR lowering（第三波实现）。
//!
//! 输入 [`lang_hir::HirProg`]（语义事实层）+ 构造期注入的语义查询面
//! （`TypeTables`/`SymbolArena`，用于三张 HIR 未展开的映射：ctor 符号 →
//! (enum, case)、fn/方法符号 → 函数表下标、TypeId → enum/struct 名）。
//! lowering 只做机械降级，不发明语义决策；语义已由 lang-semantic 背书。
//!
//! 块模型带来的两条铁律（validator 强制「块入口栈深 0」的推论）：
//! 1. **多操作数构造一律先落临时槽再统一压栈**（Binary/Range/Index/Slice、
//!    调用实参、Array/Map/Struct/Enum 构造、赋值三元组……）：任一操作数
//!    可能是多块形态（match/短路/`?`），其求值必须发生在干净栈上，汇合值
//!    落槽后由 `LocalGet` 按消费序铺设。单操作数构造（Unary/Field/Try/
//!    IterNew/BoxNew 等）直接消费——操作数值恰在本块栈顶成形。
//! 2. **跨块存活的值一律驻局部槽**：match/短路结果槽、match scrutinee、
//!    for-in 迭代器、`?` 的 err 槽。
//!
//! 降形总表（与 status/A3-hir-ir.md 冻结指令集一一对应）：
//! - 表合成：structs/enums 按声明序，末尾恒追加预载 `error`/`Option`/`Result`
//!   （名字查表需要总表；载荷类型为泛型擦除占位 `None`）；globals 按声明序，
//!   `init` 从 globals 初始化合成（声明序 `GlobalSet`，无全局缺省）。
//! - 函数表：HIR `fns`（含方法，recv 为第 0 参）先全体分配下标（顶层 fn
//!   互相前向可见），再逐个降体；闭包/嵌套 fn 按遭遇序追加；extern 按
//!   `extern_host` 形态落下（无块）。main = HIR main 节点。
//! - 名字→槽位：作用域链（捕获槽 → 接收者/参数 → 局部），链尾未命中落全局。
//! - 装箱纪律（VALUE-REF-003/VALUE-CAPTURE-004）：被闭包捕获或被 `&` 取址的
//!   绑定槽位持 Ref（声明处 `BoxNew`、读 `Deref`、写 `RefSet`）；捕获槽天然
//!   持 Ref。装箱名集 = 函数体内所有 `FnLit.captures` 名 ∪ `&x` 操作数名
//!   （名字集过近似：同名兄弟绑定也被装箱，语义透明仅多一次堆分配）。
//!   装箱参数在入口序言装箱；装箱循环变量逐轮 `BoxNew` 重绑（CTRL-FORIN-003）。
//! - **ClosureNew 捕获槽序约定（A4 执行器对接面）**：Ref 按 `captures` 升序
//!   压栈（captures[0] 最先压）；被调函数捕获槽 i 接收第 i 次压入的 Ref。
//! - 短路（EVAL-SHORTCUT-006）：`&&`/`||` → Branch + 结果临时槽。
//! - match（MATCH-KIND-001）：int 字面量 match → `SwitchInt`（源码臂序，首中
//!   即跳）；string/bool/float → `Binary(Eq)` 链 + Branch；enum/Option/Result
//!   → `SwitchEnum`（arms 与 case 声明序平行）。构造臂子模式：通配/绑定直落，
//!   字面量子模式 → Eq 检查链，嵌套构造量子模式 → 递归 SwitchEnum；同 case
//!   多臂沿源码序试候选，最终落到通配臂或 `Unreachable`（无兜底 = 运行时
//!   RUNTIME-MATCH-001，由消费方解释）。
//! - `?`（RESULT-TRY-003）→ `Inst::Try`；每个 Try 配一个 fail 块（函数收尾时
//!   回填 `LocalGet(err) → EnumNew(Result.Err) → Return`），err 槽每函数一个。
//! - break/continue → `Goto`（循环栈）；if/while/for-in → Branch/Goto 块图；
//!   控制流终止语句（break/continue/return）后的同层语句降入死块
//!   （`Unreachable` 收尾）；体走完未显式返回 = 隐式 `return ()`。
//! - 赋值（EVAL-ASSIGN-003）：整体目标直写槽位/全局（装箱经 `Deref`/`RefSet`
//!   穿透）；Field/Index 链目标——路径 Index 表达式左到右落临时槽（先于右值）、
//!   右值落临时槽，压根槽 `LocalGet`/`GlobalGet` 直压（保 place 溯源，装箱 Ref
//!   原样压栈由 `place_store` 写穿），中间组件 `FieldGet`/`IndexGet` 带溯源不落
//!   槽，压栈序 = 冻结合同（FieldSet (obj,val)、IndexSet (obj,idx,val)）；数组
//!   `+=` 走 `ArrayPush` 原位拼接（无需写回），其余复合赋值读-算-写经原槽。
//! - 调用（CALL-RESOLVE-001）：用户 fn/方法 → `Call`（方法 recv 先于实参）；
//!   makeChan/send/recv → 专指令；其余内建 → `CallBuiltin`；函数值 →
//!   `CallIndirect`（闭包值先压）。顶层 fn 一等引用 → 零捕获 `ClosureNew`。

use std::collections::{HashMap, HashSet};

use lang_core::ids::{FunctionId, LocalId, SymbolId, TypeId};
use lang_hir::{
    AssignOp, BinaryOp as HirBinaryOp, HirAssign, HirEnumDecl, HirExpr, HirFn, HirLet, HirLit,
    HirMatchArm, HirParam, HirPat, HirProg, HirStmt, LitKind, MatchKind, NodeInfo,
    UnaryOp as HirUnaryOp,
};
use lang_semantic::symbols::SymbolArena;
use lang_types::{TypeData, TypeTables};

use crate::ids::{BlockId, EnumId, GlobalId, StructId};
use crate::model::{
    Binop, Block, EnumCase, EnumDecl, FieldDecl, FnSig, GlobalDecl, Inst, IrFunction, IrProgram,
    LocalDecl, StructDecl, Terminator, Unop,
};

/// Typed HIR → Core IR。
pub trait LowerIr {
    /// lowering 失败形态（内部不变量破坏）。
    type Error;

    /// 下降整个程序。产物应通过 `lang_ir::validate`。
    fn lower(&mut self, hir: &lang_hir::HirProg) -> Result<IrProgram, Self::Error>;
}

/// 程序入口（init/main）解析结果：执行器按此启动。
///
/// 约定：有 `init` 先执行 `init` 再执行 `main`；两者可缺省
/// （纯声明库程序无 main）。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct EntryPoints {
    pub init: Option<FunctionId>,
    pub main: Option<FunctionId>,
}

/// lowering 失败（内部不变量破坏：语义已通过却出现不可能形状/事实缺口）。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct LowerError(pub String);

impl std::fmt::Display for LowerError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "IR lowering 内部错误: {}", self.0)
    }
}

impl std::error::Error for LowerError {}

/// [`LowerIr`] 的语义驱动实现。
///
/// `types`/`symbols` 必须来自产生该 HIR 的同一次 `analyze_stmts`（跨分析的
/// 符号 id 无意义）。手构 HIR（无语义背书）降级会在事实缺口处报
/// [`LowerError`]——lowering 是语义的搬运工，不做补救。
pub struct IrLowerer<'a> {
    types: &'a TypeTables,
    symbols: &'a SymbolArena,
    structs: Vec<StructDecl>,
    struct_ids: HashMap<String, StructId>,
    enums: Vec<EnumDecl>,
    enum_ids: HashMap<String, EnumId>,
    globals: Vec<GlobalDecl>,
    global_ids: HashMap<String, GlobalId>,
    functions: Vec<IrFunction>,
    /// fn/方法/嵌套 fn 符号 → 函数表下标（分配即登记，先于体下降）。
    fn_by_sym: HashMap<SymbolId, FunctionId>,
    /// 符号 → (参数个数, 是否有返回值)。分配期从 HIR 登记——函数表本体
    /// 在体下降完成前是占位签名，表达式栈效应判定不得读表（前向引用面）。
    fn_sig_by_sym: HashMap<SymbolId, (u16, bool)>,
}

impl LowerIr for IrLowerer<'_> {
    type Error = LowerError;

    fn lower(&mut self, hir: &HirProg) -> Result<IrProgram, Self::Error> {
        let prog = self.lower_prog(hir)?;
        crate::validate(&prog).map_err(|e| LowerError(format!("产物未过 validate: {e}")))?;
        Ok(prog)
    }
}

impl<'a> IrLowerer<'a> {
    #[must_use]
    pub fn new(types: &'a TypeTables, symbols: &'a SymbolArena) -> Self {
        Self {
            types,
            symbols,
            structs: Vec::new(),
            struct_ids: HashMap::new(),
            enums: Vec::new(),
            enum_ids: HashMap::new(),
            globals: Vec::new(),
            global_ids: HashMap::new(),
            functions: Vec::new(),
            fn_by_sym: HashMap::new(),
            fn_sig_by_sym: HashMap::new(),
        }
    }

    // ———— 程序表合成 ————

    fn lower_prog(&mut self, hir: &HirProg) -> Result<IrProgram, LowerError> {
        // 1. struct 表（声明序）+ 预载 error（名字查表需要总表）。
        for sd in &hir.structs {
            let id = StructId(self.structs.len() as u32);
            self.structs.push(StructDecl {
                name: sd.name.clone(),
                type_params: sd.type_params.clone(),
                fields: sd
                    .fields
                    .iter()
                    .map(|f| FieldDecl {
                        name: f.name.clone(),
                        type_id: f.type_id,
                    })
                    .collect(),
            });
            self.struct_ids.insert(sd.name.clone(), id);
        }
        let err_id = StructId(self.structs.len() as u32);
        self.structs.push(StructDecl {
            name: "error".into(),
            type_params: Vec::new(),
            fields: vec![FieldDecl {
                name: "msg".into(),
                type_id: Some(TypeTables::STR),
            }],
        });
        self.struct_ids.insert("error".into(), err_id);

        // 2. enum 表（声明序）+ 预载 Option/Result（case 序与 RESULT-PRELUDE-001
        //    一致；载荷类型为泛型擦除占位 None）。
        for ed in &hir.enums {
            let id = EnumId(self.enums.len() as u32);
            self.enums.push(enum_decl_to_ir(ed));
            self.enum_ids.insert(ed.name.clone(), id);
        }
        for (name, cases) in [
            ("Option", vec![("Some", vec![None]), ("None", vec![])]),
            ("Result", vec![("Ok", vec![None]), ("Err", vec![None])]),
        ] {
            let id = EnumId(self.enums.len() as u32);
            self.enums.push(EnumDecl {
                name: name.into(),
                type_params: Vec::new(),
                cases: cases
                    .into_iter()
                    .map(|(n, ps)| EnumCase {
                        name: n.into(),
                        payload_types: ps,
                    })
                    .collect(),
            });
            self.enum_ids.insert(name.into(), id);
        }

        // 3. 全局表（声明序）。
        for g in &hir.globals {
            let id = GlobalId(self.globals.len() as u32);
            self.globals.push(GlobalDecl {
                name: g.name.clone(),
                mutable: g.mutable,
                type_id: g.meta.type_id,
            });
            self.global_ids.insert(g.name.clone(), id);
        }

        // 4. 函数表：先全体分配下标并登记符号（顶层 fn 互相前向可见），
        //    再逐个降体；main 按 HIR main 节点身份对认。
        let mut fids = Vec::new();
        for f in &hir.fns {
            fids.push(self.alloc_function(f)?);
        }
        let mut main_fid = None;
        for (f, &fid) in hir.fns.iter().zip(&fids) {
            if hir.main.as_ref().is_some_and(|m| m.meta.id == f.meta.id) {
                main_fid = Some(fid);
            }
            if f.is_extern {
                self.functions[usize::from(fid)] = IrFunction {
                    name: f.name.clone(),
                    capture_count: 0,
                    sig: FnSig {
                        param_count: fn_param_count(f),
                        has_ret: f.ret.is_some(),
                    },
                    locals: Vec::new(),
                    blocks: Vec::new(),
                    extern_host: Some(f.extern_host.clone()),
                };
                continue;
            }
            self.prescan_nested(&f.body)?;
            self.lower_fn_body(f, fid, &[])?;
        }

        // 5. init：从 globals 初始化合成（声明序 GlobalSet；无全局缺省）。
        let init = if self.globals.is_empty() {
            None
        } else {
            Some(self.lower_init(&hir.globals)?)
        };

        Ok(IrProgram {
            structs: std::mem::take(&mut self.structs),
            enums: std::mem::take(&mut self.enums),
            globals: std::mem::take(&mut self.globals),
            functions: std::mem::take(&mut self.functions),
            init,
            main: main_fid,
        })
    }

    /// 分配函数表项（占位）并登记符号 → fid。
    fn alloc_function(&mut self, hf: &HirFn) -> Result<FunctionId, LowerError> {
        let sym = hf
            .meta
            .sym
            .ok_or_else(|| LowerError(format!("函数 {} 缺符号结论（语义缺口）", hf.name)))?;
        let fid = FunctionId(self.functions.len() as u32);
        self.fn_by_sym.insert(sym, fid);
        self.fn_sig_by_sym
            .insert(sym, (fn_param_count(hf), hf.ret.is_some()));
        self.functions.push(IrFunction {
            name: hf.name.clone(),
            capture_count: 0,
            sig: FnSig {
                param_count: 0,
                has_ret: false,
            },
            locals: Vec::new(),
            blocks: Vec::new(),
            extern_host: None,
        });
        Ok(fid)
    }

    /// 预扫描体内的嵌套 fn 声明：分配 fid 并登记（兄弟前向引用可达），
    /// 递归其体。只登记不降体；不进 fnlit 体（fnlit 在降位点自行预扫描）。
    fn prescan_nested(&mut self, stmts: &[HirStmt]) -> Result<(), LowerError> {
        for s in stmts {
            match s {
                HirStmt::FnStmt { func, .. } => {
                    self.alloc_function(func)?;
                    self.prescan_nested(&func.body)?;
                }
                HirStmt::If { then, els, .. } => {
                    self.prescan_nested(then)?;
                    self.prescan_nested(els)?;
                }
                HirStmt::While { body, .. } => self.prescan_nested(body)?,
                HirStmt::ForIn { body, .. } => self.prescan_nested(body)?,
                _ => {}
            }
        }
        Ok(())
    }

    /// 下降函数体（捕获槽布局/装箱序言/块图）并回填函数表。
    /// 前置：该函数体内的嵌套 fn 已经 `prescan_nested` 登记。
    fn lower_fn_body(
        &mut self,
        hf: &HirFn,
        fid: FunctionId,
        captures: &[String],
    ) -> Result<(), LowerError> {
        let boxed = collect_boxed_names(&hf.body);
        let mut f = FnCtx::new(
            hf.name.clone(),
            captures,
            hf.recv.as_ref(),
            &hf.params,
            hf.ret,
            boxed,
        );
        // 装箱参数的入口序言：槽位从实参值变为 Ref（捕获槽天然持 Ref）。
        for i in 0..f.param_count {
            let slot = LocalId(u32::from(f.capture_count + i));
            if matches!(f.param_kind(slot), SlotKind::Boxed) {
                f.emit(Inst::LocalGet(slot));
                f.emit(Inst::BoxNew);
                f.emit(Inst::LocalSet(slot));
            }
        }
        let terminated = self.stmts(&mut f, &hf.body)?;
        if !terminated {
            // 体走完未显式返回：隐式 `return ()`（与 interp 语义一致）。
            f.set_term(Terminator::Return { has_value: false })?;
        }
        self.fill_try_fails(&mut f)?;
        let irfn = f.finish()?;
        self.functions[usize::from(fid)] = irfn;
        Ok(())
    }

    /// 回填本函数全部 `Try` 的 fail 块：`LocalGet(err) → EnumNew(Result.Err) →
    /// Return`（函数边界合成 Err 返回，RESULT-TRY-003）。
    fn fill_try_fails(&mut self, f: &mut FnCtx) -> Result<(), LowerError> {
        let Some(err_slot) = f.err_slot else {
            return Ok(());
        };
        let result_id = self.enum_id_by_name("Result")?;
        let fails = std::mem::take(&mut f.pending_try_fails);
        for b in fails {
            f.switch_to(b);
            f.emit(Inst::LocalGet(err_slot));
            f.emit(Inst::EnumNew {
                enum_id: result_id,
                case_id: 1, // Err
            });
            f.set_term(Terminator::Return { has_value: true })?;
        }
        Ok(())
    }

    /// 合成 init 函数：逐全局（声明序）求值 init 并 `GlobalSet`。
    fn lower_init(&mut self, globals: &[HirLet]) -> Result<FunctionId, LowerError> {
        let boxed = collect_boxed_names(
            &globals
                .iter()
                .map(|g| HirStmt::Let(g.clone()))
                .collect::<Vec<_>>(),
        );
        let mut f = FnCtx::new("init".into(), &[], None, &[], None, boxed);
        for (i, g) in globals.iter().enumerate() {
            let has_value = self.expr_has_value(&g.init);
            self.expr(&mut f, &g.init)?;
            if has_value {
                f.emit(Inst::GlobalSet(GlobalId(i as u32)));
            }
            // 无值 init（语义缺口面）：无物可存，不做补救。
        }
        f.set_term(Terminator::Return { has_value: false })?;
        self.fill_try_fails(&mut f)?;
        let irfn = f.finish()?;
        let fid = FunctionId(self.functions.len() as u32);
        self.functions.push(irfn);
        Ok(fid)
    }

    // ———— 查表助手 ————

    fn enum_id_by_name(&self, name: &str) -> Result<EnumId, LowerError> {
        self.enum_ids
            .get(name)
            .copied()
            .ok_or_else(|| LowerError(format!("enum {name} 不在函数表（语义缺口）")))
    }

    fn struct_id_by_name(&self, name: &str) -> Result<StructId, LowerError> {
        self.struct_ids
            .get(name)
            .copied()
            .ok_or_else(|| LowerError(format!("struct {name} 不在结构表（语义缺口）")))
    }

    /// 构造器符号 → (enum, case)（NAME-CTOR-005：case 名全局唯一，
    /// 符号自带 owner 与 case 序）。
    fn ctor_case(&self, sym: SymbolId) -> Result<(EnumId, u16), LowerError> {
        let s = self.symbols.get(sym);
        let eid = self.enum_id_by_name(&s.owner_name)?;
        Ok((eid, s.case_index as u16))
    }

    /// match scrutinee 类型 → enum 表项（Enum(def)/Result/Option）。
    fn enum_id_of_type(&self, t: Option<TypeId>) -> Result<EnumId, LowerError> {
        match t.map(|t| self.types.data(t)) {
            Some(TypeData::Enum(def)) => self.enum_id_by_name(&self.types.enum_def(*def).name),
            Some(TypeData::Result { .. }) => self.enum_id_by_name("Result"),
            Some(TypeData::Option(_)) => self.enum_id_by_name("Option"),
            other => Err(LowerError(format!(
                "match scrutinee 类型非 enum/Result/Option：{other:?}（语义缺口）"
            ))),
        }
    }

    /// 类型 → struct 表项（Struct(def)/Error）。
    fn struct_id_of_type(&self, t: Option<TypeId>) -> Result<StructId, LowerError> {
        match t.map(|t| self.types.data(t)) {
            Some(TypeData::Struct(def)) => {
                self.struct_id_by_name(&self.types.struct_def(*def).name)
            }
            Some(TypeData::Error) => self.struct_id_by_name("error"),
            other => Err(LowerError(format!(
                "struct 字面量类型非 struct：{other:?}（语义缺口）"
            ))),
        }
    }

    fn resolve_place(&self, f: &FnCtx, name: &str) -> Result<Place, LowerError> {
        if let Some(slot) = f.resolve(name) {
            return Ok(Place::Slot(slot));
        }
        if let Some(g) = self.global_ids.get(name) {
            return Ok(Place::Global(*g));
        }
        Err(LowerError(format!(
            "名字 {name} 未解析到槽位或全局（语义缺口）"
        )))
    }

    // ———— 语句 ————

    /// 下降语句序列；返回是否以控制流终止收尾（后续同层语句进死块）。
    fn stmts(&mut self, f: &mut FnCtx, list: &[HirStmt]) -> Result<bool, LowerError> {
        let mut terminated = false;
        for s in list {
            if terminated {
                f.open_dead_block();
            }
            terminated = self.stmt(f, s)? || terminated;
        }
        Ok(terminated)
    }

    fn stmt(&mut self, f: &mut FnCtx, s: &HirStmt) -> Result<bool, LowerError> {
        match s {
            HirStmt::Let(l) => {
                let has_value = self.expr_has_value(&l.init);
                self.expr(f, &l.init)?;
                if has_value {
                    if l.name == "_" {
                        f.emit(Inst::Pop);
                    } else {
                        let slot = self.declare_binding(f, &l.name, l.meta.type_id);
                        if f.boxed.contains(&l.name) {
                            f.emit(Inst::BoxNew);
                        }
                        f.emit(Inst::LocalSet(slot));
                    }
                } else if l.name != "_" {
                    // 无值初始化（语义缺口面）：绑槽不存值，不做补救。
                    let _ = self.declare_binding(f, &l.name, l.meta.type_id);
                }
                Ok(false)
            }
            HirStmt::Assign(a) => {
                self.assign(f, a)?;
                Ok(false)
            }
            HirStmt::Eval { expr, .. } => {
                let has_value = self.expr_has_value(expr);
                self.expr(f, expr)?;
                if has_value {
                    f.emit(Inst::Pop);
                }
                Ok(false)
            }
            HirStmt::Return { val, .. } => match val {
                None => {
                    f.set_term(Terminator::Return { has_value: false })?;
                    Ok(true)
                }
                Some(v) => {
                    self.expr(f, v)?;
                    f.set_term(Terminator::Return { has_value: true })?;
                    Ok(true)
                }
            },
            HirStmt::If {
                cond, then, els, ..
            } => {
                let then_b = f.new_block();
                let end_b = f.new_block();
                let else_b = if els.is_empty() { end_b } else { f.new_block() };
                self.expr(f, cond)?;
                f.set_term(Terminator::Branch {
                    if_true: then_b,
                    if_false: else_b,
                })?;
                f.switch_to(then_b);
                f.push_scope();
                let t1 = self.stmts(f, then)?;
                f.pop_scope();
                if !t1 {
                    f.set_term(Terminator::Goto { target: end_b })?;
                }
                let mut t2 = false;
                if !els.is_empty() {
                    f.switch_to(else_b);
                    f.push_scope();
                    t2 = self.stmts(f, els)?;
                    f.pop_scope();
                    if !t2 {
                        f.set_term(Terminator::Goto { target: end_b })?;
                    }
                }
                if !els.is_empty() && t1 && t2 {
                    f.mark_dead(end_b); // 两臂皆终止：end 不可达
                }
                f.switch_to(end_b);
                Ok(!els.is_empty() && t1 && t2)
            }
            HirStmt::While { cond, body, .. } => {
                let cond_b = f.new_block();
                let body_b = f.new_block();
                let end_b = f.new_block();
                f.set_term(Terminator::Goto { target: cond_b })?;
                f.switch_to(cond_b);
                self.expr(f, cond)?;
                f.set_term(Terminator::Branch {
                    if_true: body_b,
                    if_false: end_b,
                })?;
                f.switch_to(body_b);
                f.loops.push((cond_b, end_b));
                f.push_scope();
                let t = self.stmts(f, body)?;
                f.pop_scope();
                f.loops.pop();
                if !t {
                    f.set_term(Terminator::Goto { target: cond_b })?;
                }
                f.switch_to(end_b);
                Ok(false)
            }
            HirStmt::ForIn {
                variable,
                iter,
                body,
                ..
            } => {
                self.for_in(f, variable, iter, body)?;
                Ok(false)
            }
            HirStmt::Break { .. } => {
                let (_, brk) = *f
                    .loops
                    .last()
                    .ok_or_else(|| LowerError("break 出现在循环外（语义缺口）".into()))?;
                f.set_term(Terminator::Goto { target: brk })?;
                Ok(true)
            }
            HirStmt::Continue { .. } => {
                let (cont, _) = *f
                    .loops
                    .last()
                    .ok_or_else(|| LowerError("continue 出现在循环外（语义缺口）".into()))?;
                f.set_term(Terminator::Goto { target: cont })?;
                Ok(true)
            }
            HirStmt::Spawn { func, args, .. } => {
                let sym = func.ok_or_else(|| {
                    LowerError("go 目标缺符号结论（resolver 已约束顶层 fn）".into())
                })?;
                let target = *self
                    .fn_by_sym
                    .get(&sym)
                    .ok_or_else(|| LowerError(format!("go 目标符号 {sym} 未登记（语义缺口）")))?;
                // 实参在 go 语句处立即求值（EVAL-GO-007），左到右。
                let tmps = self.exprs_to_temps(f, args)?;
                for t in &tmps {
                    f.emit(Inst::LocalGet(*t));
                }
                f.emit(Inst::Spawn {
                    func: target,
                    argc: args.len() as u16,
                });
                Ok(false)
            }
            // 嵌套 fn：fid 已预扫描登记；此处降体并回填（互为前向可见）。
            HirStmt::FnStmt { func, .. } => {
                let sym = func.meta.sym.ok_or_else(|| {
                    LowerError(format!("嵌套 fn {} 缺符号结论（语义缺口）", func.name))
                })?;
                let fid = *self
                    .fn_by_sym
                    .get(&sym)
                    .ok_or_else(|| LowerError(format!("嵌套 fn 符号 {sym} 未登记（语义缺口）")))?;
                self.lower_fn_body(func, fid, &[])?;
                Ok(false)
            }
        }
    }

    /// 声明绑定：分配槽位并入当前作用域（返回槽位 id）。
    fn declare_binding(&mut self, f: &mut FnCtx, name: &str, ty: Option<TypeId>) -> LocalId {
        let slot = f.new_local(name.to_string(), ty);
        let kind = if f.boxed.contains(name) {
            SlotKind::Boxed
        } else {
            SlotKind::Plain
        };
        f.bind(name, Slot { local: slot, kind });
        slot
    }

    fn for_in(
        &mut self,
        f: &mut FnCtx,
        variable: &str,
        iter: &HirExpr,
        body: &[HirStmt],
    ) -> Result<(), LowerError> {
        // 迭代器驻局部槽（不跨块压栈——块入口栈深 0）。
        let iter_slot = f.new_local("<iter>".into(), None);
        self.expr(f, iter)?;
        f.emit(Inst::IterNew);
        f.emit(Inst::LocalSet(iter_slot));

        let cond_b = f.new_block();
        let end_b = f.new_block();
        f.set_term(Terminator::Goto { target: cond_b })?;
        f.switch_to(cond_b);
        f.emit(Inst::LocalGet(iter_slot));
        f.emit(Inst::IterNext { done: end_b });
        f.push_scope();
        if variable != "_" {
            // 装箱循环变量逐轮 BoxNew 重绑（CTRL-FORIN-003）。
            let var_slot = self.declare_binding(f, variable, None);
            if f.boxed.contains(variable) {
                f.emit(Inst::BoxNew);
            }
            f.emit(Inst::LocalSet(var_slot));
        } else {
            f.emit(Inst::Pop);
        }
        f.loops.push((cond_b, end_b));
        let t = self.stmts(f, body)?;
        f.loops.pop();
        f.pop_scope();
        if !t {
            f.set_term(Terminator::Goto { target: cond_b })?;
        }
        f.switch_to(end_b);
        Ok(())
    }

    // ———— 赋值（EVAL-ASSIGN-003：地址组件 → 右值 → 读旧值 → 运算 → 写回） ————

    fn assign(&mut self, f: &mut FnCtx, a: &HirAssign) -> Result<(), LowerError> {
        // 数组 `+=` 原位拼接（checker 已约束两侧为数组；VALUE-SHARE-002）。
        let is_concat = a.op == AssignOp::AddAssign
            && matches!(
                meta_of(&a.rhs).type_id.map(|t| self.types.data(t)),
                Some(TypeData::Slice(_))
            );
        // 目标恒为 Ident 根 + Field/Index 链（checker 保证）；分解后分派。
        let (root, path) = split_assign_path(a.target.as_ref());
        let HirExpr::LocalRef { name, .. } = root else {
            return Err(LowerError(format!(
                "未预期的赋值目标根 {root:?}（解析器已约束）"
            )));
        };
        let place = self.resolve_place(f, name)?;
        if path.is_empty() {
            // 整体赋值/复合赋值：局部槽与全局直写，现有路径（正确）保留。
            match (a.op, &place) {
                (AssignOp::Assign, Place::Slot(slot)) if slot.kind == SlotKind::Plain => {
                    self.expr(f, &a.rhs)?;
                    f.emit(Inst::LocalSet(slot.local));
                }
                (AssignOp::Assign, Place::Slot(slot)) => {
                    // 装箱：地址组件（Ref）先行落槽，右值后求值，RefSet 写穿透。
                    let addr = f.new_temp(None);
                    f.emit(Inst::LocalGet(slot.local));
                    f.emit(Inst::LocalSet(addr));
                    let tmp = f.new_temp(None);
                    self.expr(f, &a.rhs)?;
                    f.emit(Inst::LocalSet(tmp));
                    f.emit(Inst::LocalGet(addr));
                    f.emit(Inst::LocalGet(tmp));
                    f.emit(Inst::RefSet);
                }
                (AssignOp::Assign, Place::Global(g)) => {
                    self.expr(f, &a.rhs)?;
                    f.emit(Inst::GlobalSet(*g));
                }
                (_, place) if is_concat => {
                    let tmp = f.new_temp(None);
                    self.expr(f, &a.rhs)?;
                    f.emit(Inst::LocalSet(tmp));
                    self.load_place(f, place)?;
                    f.emit(Inst::LocalGet(tmp));
                    f.emit(Inst::ArrayPush);
                    // 原位拼接：装箱目标无需写回。
                }
                (_, Place::Slot(slot)) if slot.kind == SlotKind::Plain => {
                    let tmp = f.new_temp(None);
                    self.expr(f, &a.rhs)?;
                    f.emit(Inst::LocalSet(tmp));
                    f.emit(Inst::LocalGet(slot.local));
                    f.emit(Inst::LocalGet(tmp));
                    f.emit(Inst::Binary(assign_binop(a.op)));
                    f.emit(Inst::LocalSet(slot.local));
                }
                (_, Place::Slot(slot)) => {
                    let addr = f.new_temp(None);
                    f.emit(Inst::LocalGet(slot.local));
                    f.emit(Inst::LocalSet(addr));
                    let tmp = f.new_temp(None);
                    self.expr(f, &a.rhs)?;
                    f.emit(Inst::LocalSet(tmp));
                    f.emit(Inst::LocalGet(addr));
                    f.emit(Inst::LocalGet(addr));
                    f.emit(Inst::Deref);
                    f.emit(Inst::LocalGet(tmp));
                    f.emit(Inst::Binary(assign_binop(a.op)));
                    f.emit(Inst::RefSet);
                }
                (_, Place::Global(g)) => {
                    let tmp = f.new_temp(None);
                    self.expr(f, &a.rhs)?;
                    f.emit(Inst::LocalSet(tmp));
                    f.emit(Inst::GlobalGet(*g));
                    f.emit(Inst::LocalGet(tmp));
                    f.emit(Inst::Binary(assign_binop(a.op)));
                    f.emit(Inst::GlobalSet(*g));
                }
            }
            return Ok(());
        }
        self.assign_path(f, a, place, &path, is_concat)
    }

    /// Field/Index 链赋值降形（压栈序 = 冻结合同：FieldSet 2→0 (obj,val)、
    /// IndexSet 3→0 (obj,idx,val)；弹序 = 压序逆序）。
    ///
    /// 地址组件不落临时槽——根槽 `LocalGet`/`GlobalGet` 直接压栈（操作数带
    /// `Place::Var` 溯源；装箱槽原样压 Ref，`place_store` 写穿透原槽/全局），
    /// 中间组件经 `FieldGet`/`LocalGet(idx)+IndexGet` 产生带溯源的操作数。
    /// 路径上的 Index 表达式从左到右求值落临时槽（int/键值，无身份问题），
    /// 且先于右值（EVAL-ASSIGN-003）；右值统一落临时槽（多块右值安全）。
    fn assign_path(
        &mut self,
        f: &mut FnCtx,
        a: &HirAssign,
        root: Place,
        path: &[PathComp<'_>],
        is_concat: bool,
    ) -> Result<(), LowerError> {
        // 1. 路径 Index 表达式左到右落临时槽（地址组件先于右值）。
        let mut idx_temps: Vec<LocalId> = Vec::new();
        for comp in path {
            if let PathComp::Index(e) = comp {
                let t = f.new_temp(None);
                self.expr(f, e)?;
                f.emit(Inst::LocalSet(t));
                idx_temps.push(t);
            }
        }
        // 2. 右值求值落临时槽。
        let val_t = f.new_temp(None);
        self.expr(f, &a.rhs)?;
        f.emit(Inst::LocalSet(val_t));

        let Some((last, mids)) = path.split_last() else {
            return Err(LowerError("赋值路径为空（内部序错）".into()));
        };
        // 第 k 个 Index 组件 ↔ idx_temps[k]；prefix 消费 mids 里的，
        // 终组件的临时槽由各分支自取。
        let mut k = 0usize;
        // 3. 压根 + 中间组件（FieldGet/IndexGet 带溯源，不落临时槽）。
        self.push_path_prefix(f, root, mids, idx_temps.as_slice(), &mut k)?;
        match (a.op, last) {
            (AssignOp::Assign, PathComp::Field(name)) => {
                f.emit(Inst::LocalGet(val_t));
                f.emit(Inst::FieldSet {
                    field: name.to_string(),
                });
            }
            (AssignOp::Assign, PathComp::Index(_)) => {
                f.emit(Inst::LocalGet(idx_temps[k]));
                f.emit(Inst::LocalGet(val_t));
                f.emit(Inst::IndexSet);
            }
            (_, _) if is_concat => {
                // 数组 `+=`：读终组件（容器共享堆身份）+ 右值 → 原位拼接。
                match last {
                    PathComp::Field(name) => {
                        f.emit(Inst::FieldGet {
                            field: name.to_string(),
                        });
                    }
                    PathComp::Index(_) => {
                        f.emit(Inst::LocalGet(idx_temps[k]));
                        f.emit(Inst::IndexGet);
                    }
                }
                f.emit(Inst::LocalGet(val_t));
                f.emit(Inst::ArrayPush);
            }
            (op, last) => {
                // 复合赋值：读旧值 → 运算 → 写回原槽（读写都经带溯源操作数，
                // place_store 落到根槽本体）。
                match last {
                    PathComp::Field(name) => {
                        f.emit(Inst::FieldGet {
                            field: name.to_string(),
                        });
                    }
                    PathComp::Index(_) => {
                        f.emit(Inst::LocalGet(idx_temps[k]));
                        f.emit(Inst::IndexGet);
                    }
                }
                f.emit(Inst::LocalGet(val_t));
                f.emit(Inst::Binary(assign_binop(op)));
                let new_t = f.new_temp(None);
                f.emit(Inst::LocalSet(new_t));
                // 写回：重压根 + 中间组件（两读间无副作用，槽值未变）。
                let mut k2 = 0usize;
                self.push_path_prefix(f, root, mids, idx_temps.as_slice(), &mut k2)?;
                match last {
                    PathComp::Field(name) => {
                        f.emit(Inst::LocalGet(new_t));
                        f.emit(Inst::FieldSet {
                            field: name.to_string(),
                        });
                    }
                    PathComp::Index(_) => {
                        f.emit(Inst::LocalGet(idx_temps[k2]));
                        f.emit(Inst::LocalGet(new_t));
                        f.emit(Inst::IndexSet);
                    }
                }
            }
        }
        Ok(())
    }

    /// 压根槽与中间组件（`FieldGet`/`LocalGet(idx)+IndexGet`，操作数带 place
    /// 溯源）。`k` 沿途消费 Index 组件的临时槽序号。
    fn push_path_prefix(
        &mut self,
        f: &mut FnCtx,
        root: Place,
        mids: &[PathComp<'_>],
        idx_temps: &[LocalId],
        k: &mut usize,
    ) -> Result<(), LowerError> {
        match root {
            Place::Slot(slot) => f.emit(Inst::LocalGet(slot.local)),
            Place::Global(g) => f.emit(Inst::GlobalGet(g)),
        }
        for comp in mids {
            match comp {
                PathComp::Field(name) => {
                    f.emit(Inst::FieldGet {
                        field: name.to_string(),
                    });
                }
                PathComp::Index(_) => {
                    let t = *idx_temps.get(*k).ok_or_else(|| {
                        LowerError("路径 Index 临时槽缺失（lowering 内部序错）".into())
                    })?;
                    *k += 1;
                    f.emit(Inst::LocalGet(t));
                    f.emit(Inst::IndexGet);
                }
            }
        }
        Ok(())
    }

    // ———— 表达式 ————

    /// 该表达式求值后是否在栈上留值（栈效应声明的唯一事实来源）。
    /// 调用看目标签名；内建看单元面；其余看节点类型。
    fn expr_has_value(&self, e: &HirExpr) -> bool {
        match e {
            HirExpr::Call {
                func: Some(sym), ..
            } => self
                .fn_sig_by_sym
                .get(sym)
                .map(|(_, has_ret)| *has_ret)
                .unwrap_or(false),
            HirExpr::Call {
                builtin: Some(name),
                ..
            } => !matches!(name.as_str(), "println" | "print" | "assert" | "send"),
            HirExpr::MethodCall {
                method: Some(sym), ..
            } => self
                .fn_sig_by_sym
                .get(sym)
                .map(|(_, has_ret)| *has_ret)
                .unwrap_or(false),
            other => self.type_has_value(other),
        }
    }

    /// 节点类型是否携带值（Unit/Unknown/Invalid/Never 视为无值：void 调用与
    /// checker 缺口面；与栈平衡的保守侧一致）。
    fn type_has_value(&self, e: &HirExpr) -> bool {
        match meta_of(e).type_id {
            None => false,
            Some(t) => !matches!(
                self.types.data(t),
                TypeData::Unit | TypeData::Unknown | TypeData::Invalid | TypeData::Never
            ),
        }
    }

    /// 表达式下降：进入时栈必须干净（深度 0），返回时恰留 0/1 个值
    /// （1 ⇔ [`Self::expr_has_value`]）。
    fn expr(&mut self, f: &mut FnCtx, e: &HirExpr) -> Result<(), LowerError> {
        match e {
            HirExpr::Lit(l) => {
                self.emit_lit(f, l)?;
            }
            HirExpr::LocalRef { name, .. } => {
                let place = self.resolve_place(f, name)?;
                self.load_place(f, &place)?;
            }
            HirExpr::FnRef { meta, .. } => {
                // 顶层 fn 一等引用 = 零捕获闭包值。
                let sym = meta
                    .sym
                    .ok_or_else(|| LowerError("fn 引用缺符号结论（语义缺口）".into()))?;
                let fid = *self
                    .fn_by_sym
                    .get(&sym)
                    .ok_or_else(|| LowerError(format!("fn 符号 {sym} 未登记（语义缺口）")))?;
                f.emit(Inst::ClosureNew {
                    func: fid,
                    captures: 0,
                });
            }
            HirExpr::BuiltinRef { name, .. } => {
                return Err(LowerError(format!(
                    "内建 {name} 的一等引用无 IR 形态（CallBuiltin 为指令非值）；\
                     语义面未冻结，登记 SPEC-GAP 候选"
                )));
            }
            HirExpr::Ctor { ctor_sym, args, .. } => {
                let sym =
                    ctor_sym.ok_or_else(|| LowerError("构造器缺符号结论（语义缺口）".into()))?;
                let (enum_id, case_id) = self.ctor_case(sym)?;
                // 载荷按 case 声明序压栈；实参求值左到右（先落临时槽）。
                let argc = self.enums[usize::from(enum_id)].cases[usize::from(case_id)]
                    .payload_types
                    .len();
                let tmps = self.exprs_to_temps(f, args)?;
                if tmps.len() != argc {
                    return Err(LowerError(format!(
                        "构造器实参 {} 个 != case 载荷 {argc} 个（语义缺口）",
                        tmps.len()
                    )));
                }
                for t in &tmps {
                    f.emit(Inst::LocalGet(*t));
                }
                f.emit(Inst::EnumNew { enum_id, case_id });
            }
            HirExpr::Call {
                func,
                builtin,
                callee,
                args,
                ..
            } => {
                if let Some(sym) = func {
                    let fid = *self
                        .fn_by_sym
                        .get(sym)
                        .ok_or_else(|| LowerError(format!("调用符号 {sym} 未登记（语义缺口）")))?;
                    let tmps = self.exprs_to_temps(f, args)?;
                    for t in &tmps {
                        f.emit(Inst::LocalGet(*t));
                    }
                    f.emit(Inst::Call(fid));
                } else if let Some(name) = builtin {
                    self.builtin_call(f, name, args, e)?;
                } else if let Some(c) = callee {
                    // 函数值调用：闭包值先压，再压实参（CALL-CLOSURE-003）。
                    let callee_t = f.new_temp(None);
                    self.expr(f, c)?;
                    f.emit(Inst::LocalSet(callee_t));
                    let tmps = self.exprs_to_temps(f, args)?;
                    f.emit(Inst::LocalGet(callee_t));
                    for t in &tmps {
                        f.emit(Inst::LocalGet(*t));
                    }
                    f.emit(Inst::CallIndirect {
                        argc: args.len() as u16,
                        has_ret: self.type_has_value(e),
                    });
                } else {
                    return Err(LowerError(
                        "调用节点 func/builtin/callee 全空（HIR 缺口）".into(),
                    ));
                }
            }
            HirExpr::MethodCall {
                recv,
                method,
                field,
                args,
                ..
            } => {
                if let Some(sym) = method {
                    // 方法调用：接收者先于实参（EVAL-CALL-002）。
                    let fid = *self
                        .fn_by_sym
                        .get(sym)
                        .ok_or_else(|| LowerError(format!("方法符号 {sym} 未登记（语义缺口）")))?;
                    let recv_t = f.new_temp(None);
                    self.expr(f, recv)?;
                    f.emit(Inst::LocalSet(recv_t));
                    let tmps = self.exprs_to_temps(f, args)?;
                    f.emit(Inst::LocalGet(recv_t));
                    for t in &tmps {
                        f.emit(Inst::LocalGet(*t));
                    }
                    f.emit(Inst::Call(fid));
                } else {
                    // 字段函数值间接调用（CALL-RESOLVE-001）：
                    // callee = 字段读取（值 = 闭包），闭包值先压再压实参。
                    let fname = field.clone().ok_or_else(|| {
                        LowerError("MethodCall 缺 method 与 field（HIR 缺口）".into())
                    })?;
                    let callee_t = f.new_temp(None);
                    self.expr(f, recv)?;
                    f.emit(Inst::FieldGet { field: fname });
                    f.emit(Inst::LocalSet(callee_t));
                    let tmps = self.exprs_to_temps(f, args)?;
                    f.emit(Inst::LocalGet(callee_t));
                    for t in &tmps {
                        f.emit(Inst::LocalGet(*t));
                    }
                    f.emit(Inst::CallIndirect {
                        argc: args.len() as u16,
                        has_ret: self.type_has_value(e),
                    });
                }
            }
            HirExpr::Unary { op, expr, .. } => match op {
                HirUnaryOp::Ref => {
                    // `&x`（E3012 仅变量）：装箱槽位直接持 Ref。
                    let HirExpr::LocalRef { name, .. } = expr.as_ref() else {
                        return Err(LowerError("& 操作数非变量（E3012 已拒绝）".into()));
                    };
                    match f.resolve(name) {
                        Some(slot) if slot.kind != SlotKind::Plain => {
                            f.emit(Inst::LocalGet(slot.local));
                        }
                        Some(_) => {
                            return Err(LowerError(format!(
                                "& 目标 {name} 未装箱（装箱名集缺口）"
                            )));
                        }
                        None => {
                            return Err(LowerError(
                                "& 仅可作用于局部变量（全局取引用无 IR 形态，SPEC-GAP 候选）"
                                    .into(),
                            ));
                        }
                    }
                }
                op => {
                    self.expr(f, expr)?;
                    f.emit(Inst::Unary(match op {
                        HirUnaryOp::Neg => Unop::Neg,
                        HirUnaryOp::Not => Unop::Not,
                        HirUnaryOp::Ref => unreachable!("Ref 已在上方分支处理"),
                    }));
                }
            },
            HirExpr::Binary { op, lhs, rhs, .. } => {
                let l = f.new_temp(None);
                self.expr(f, lhs)?;
                f.emit(Inst::LocalSet(l));
                let r = f.new_temp(None);
                self.expr(f, rhs)?;
                f.emit(Inst::LocalSet(r));
                f.emit(Inst::LocalGet(l));
                f.emit(Inst::LocalGet(r));
                f.emit(Inst::Binary(binop(*op)));
            }
            HirExpr::AndThen { lhs, rhs, .. } => {
                self.short_circuit(f, lhs, rhs, false)?;
            }
            HirExpr::OrElse { lhs, rhs, .. } => {
                self.short_circuit(f, lhs, rhs, true)?;
            }
            HirExpr::Range { lo, hi, .. } => {
                let l = f.new_temp(None);
                self.expr(f, lo)?;
                f.emit(Inst::LocalSet(l));
                let h = f.new_temp(None);
                self.expr(f, hi)?;
                f.emit(Inst::LocalSet(h));
                f.emit(Inst::LocalGet(l));
                f.emit(Inst::LocalGet(h));
                f.emit(Inst::RangeNew);
            }
            HirExpr::Index { obj, idx, .. } => {
                let o = f.new_temp(None);
                self.expr(f, obj)?;
                f.emit(Inst::LocalSet(o));
                let i = f.new_temp(None);
                self.expr(f, idx)?;
                f.emit(Inst::LocalSet(i));
                f.emit(Inst::LocalGet(o));
                f.emit(Inst::LocalGet(i));
                f.emit(Inst::IndexGet);
            }
            HirExpr::Slice { obj, lo, hi, .. } => {
                let o = f.new_temp(None);
                self.expr(f, obj)?;
                f.emit(Inst::LocalSet(o));
                let l = f.new_temp(None);
                self.expr(f, lo)?;
                f.emit(Inst::LocalSet(l));
                let h = f.new_temp(None);
                self.expr(f, hi)?;
                f.emit(Inst::LocalSet(h));
                f.emit(Inst::LocalGet(o));
                f.emit(Inst::LocalGet(l));
                f.emit(Inst::LocalGet(h));
                f.emit(Inst::SliceGet);
            }
            HirExpr::Field { obj, name, .. } => {
                self.expr(f, obj)?;
                f.emit(Inst::FieldGet {
                    field: name.clone(),
                });
            }
            HirExpr::Try { expr, .. } => {
                // `?` 传播（RESULT-TRY-003）：Ok 压解包值继续；Err 存槽跳 fail 块。
                let err_slot = match f.err_slot {
                    Some(l) => l,
                    None => {
                        let l = f.new_local("<err>".into(), Some(TypeTables::ERROR));
                        f.err_slot = Some(l);
                        l
                    }
                };
                let fail_b = f.new_block();
                self.expr(f, expr)?;
                f.emit(Inst::Try {
                    err: err_slot,
                    fail: fail_b,
                });
                f.pending_try_fails.push(fail_b);
            }
            HirExpr::StructLit { meta, fields, .. } => {
                let sid = self.struct_id_of_type(meta.type_id)?;
                // 字段值按源码序求值（EVAL-LITERAL-004），按声明序压栈。
                let mut temps: Vec<(String, LocalId)> = Vec::new();
                for (n, v) in fields {
                    let t = f.new_temp(None);
                    self.expr(f, v)?;
                    f.emit(Inst::LocalSet(t));
                    temps.push((n.clone(), t));
                }
                let decl = &self.structs[usize::from(sid)];
                for fd in &decl.fields {
                    let t = *temps
                        .iter()
                        .find(|(n, _)| n == &fd.name)
                        .map(|(_, t)| t)
                        .ok_or_else(|| {
                            LowerError(format!(
                                "struct {} 字面量缺字段 {}（语义缺口）",
                                decl.name, fd.name
                            ))
                        })?;
                    f.emit(Inst::LocalGet(t));
                }
                f.emit(Inst::StructNew { struct_id: sid });
            }
            HirExpr::MapLit { items, .. } => {
                // 键先值后、逐对左到右（EVAL-LITERAL-004）；压序 k1,v1,k2,v2。
                let mut temps = Vec::new();
                for it in items {
                    let k = f.new_temp(None);
                    self.expr(f, &it.key)?;
                    f.emit(Inst::LocalSet(k));
                    let v = f.new_temp(None);
                    self.expr(f, &it.val)?;
                    f.emit(Inst::LocalSet(v));
                    temps.push((k, v));
                }
                for (k, v) in &temps {
                    f.emit(Inst::LocalGet(*k));
                    f.emit(Inst::LocalGet(*v));
                }
                f.emit(Inst::MapNew {
                    count: items.len() as u32,
                });
            }
            HirExpr::ArrayLit { elems, .. } => {
                let tmps = self.exprs_to_temps(f, elems)?;
                for t in &tmps {
                    f.emit(Inst::LocalGet(*t));
                }
                f.emit(Inst::ArrayNew {
                    count: elems.len() as u32,
                });
            }
            HirExpr::Match {
                scrutinee,
                kind,
                arms,
                ..
            } => {
                self.match_expr(f, *kind, scrutinee, arms, e)?;
            }
            HirExpr::FnLit {
                params,
                ret,
                body,
                captures,
                ..
            } => {
                // 捕获 Ref 按 captures 升序压栈（A4 对接约定，见模块文档）。
                for name in captures {
                    match f.resolve(name) {
                        Some(slot) if slot.kind != SlotKind::Plain => {
                            f.emit(Inst::LocalGet(slot.local));
                        }
                        Some(_) => {
                            return Err(LowerError(format!(
                                "捕获绑定 {name} 未装箱（装箱名集缺口）"
                            )));
                        }
                        None => {
                            return Err(LowerError(format!(
                                "捕获名 {name} 未解析到外层槽位（递归闭包/语义缺口）"
                            )));
                        }
                    }
                }
                let fid = FunctionId(self.functions.len() as u32);
                self.functions.push(IrFunction {
                    name: "<fnlit>".into(),
                    capture_count: 0,
                    sig: FnSig {
                        param_count: 0,
                        has_ret: false,
                    },
                    locals: Vec::new(),
                    blocks: Vec::new(),
                    extern_host: None,
                });
                f.emit(Inst::ClosureNew {
                    func: fid,
                    captures: captures.len() as u16,
                });
                // fnlit 是函数边界：新 FnCtx（捕获槽 → 参数 → 体），作用域不接外层。
                let boxed = collect_boxed_names(body);
                let mut g = FnCtx::new("<fnlit>".into(), captures, None, params, *ret, boxed);
                self.prescan_nested(body)?;
                let terminated = self.stmts(&mut g, body)?;
                if !terminated {
                    g.set_term(Terminator::Return { has_value: false })?;
                }
                self.fill_try_fails(&mut g)?;
                let irfn = g.finish()?;
                self.functions[usize::from(fid)] = irfn;
            }
        }
        Ok(())
    }

    fn emit_lit(&mut self, f: &mut FnCtx, l: &HirLit) -> Result<(), LowerError> {
        match l.kind {
            LitKind::Int => f.emit(Inst::ConstI64(l.i64)),
            LitKind::Float => f.emit(Inst::ConstF64(l.f64)),
            LitKind::Str => f.emit(Inst::ConstStr(l.s.clone())),
            LitKind::Bool => f.emit(Inst::ConstBool(l.b)),
        }
        Ok(())
    }

    /// 逐实参求值并落临时槽（求值序 = 源码序；返回槽序列供压栈）。
    fn exprs_to_temps(
        &mut self,
        f: &mut FnCtx,
        args: &[HirExpr],
    ) -> Result<Vec<LocalId>, LowerError> {
        let mut out = Vec::with_capacity(args.len());
        for a in args {
            let t = f.new_temp(None);
            self.expr(f, a)?;
            f.emit(Inst::LocalSet(t));
            out.push(t);
        }
        Ok(out)
    }

    fn load_place(&mut self, f: &mut FnCtx, place: &Place) -> Result<(), LowerError> {
        match place {
            Place::Slot(slot) if slot.kind == SlotKind::Plain => f.emit(Inst::LocalGet(slot.local)),
            Place::Slot(slot) => {
                f.emit(Inst::LocalGet(slot.local));
                f.emit(Inst::Deref);
            }
            Place::Global(g) => f.emit(Inst::GlobalGet(*g)),
        }
        Ok(())
    }

    /// 短路降形（EVAL-SHORTCUT-006）：左值 Branch 分派，短路值常量落槽。
    fn short_circuit(
        &mut self,
        f: &mut FnCtx,
        lhs: &HirExpr,
        rhs: &HirExpr,
        is_or: bool,
    ) -> Result<(), LowerError> {
        let res = f.new_temp(Some(TypeTables::BOOL));
        let rhs_b = f.new_block();
        let short_b = f.new_block();
        let end_b = f.new_block();
        self.expr(f, lhs)?;
        // &&：左 false → 短路 false 不求值右；||：左 true → 短路 true。
        f.set_term(Terminator::Branch {
            if_true: if is_or { short_b } else { rhs_b },
            if_false: if is_or { rhs_b } else { short_b },
        })?;
        f.switch_to(rhs_b);
        self.expr(f, rhs)?;
        f.emit(Inst::LocalSet(res));
        f.set_term(Terminator::Goto { target: end_b })?;
        f.switch_to(short_b);
        f.emit(Inst::ConstBool(is_or));
        f.emit(Inst::LocalSet(res));
        f.set_term(Terminator::Goto { target: end_b })?;
        f.switch_to(end_b);
        f.emit(Inst::LocalGet(res));
        Ok(())
    }

    fn builtin_call(
        &mut self,
        f: &mut FnCtx,
        name: &str,
        args: &[HirExpr],
        call: &HirExpr,
    ) -> Result<(), LowerError> {
        // makeChan/send/recv 升专指令（冻结约定）；其余落 CallBuiltin。
        match name {
            "makeChan" => {
                f.emit(Inst::ChanNew);
            }
            "send" => {
                if args.len() != 2 {
                    return Err(LowerError(format!(
                        "send 实参 {} 个 != 2（语义缺口）",
                        args.len()
                    )));
                }
                let ch = f.new_temp(None);
                self.expr(f, &args[0])?;
                f.emit(Inst::LocalSet(ch));
                let val = f.new_temp(None);
                self.expr(f, &args[1])?;
                f.emit(Inst::LocalSet(val));
                f.emit(Inst::LocalGet(ch));
                f.emit(Inst::LocalGet(val));
                f.emit(Inst::ChanSend);
            }
            "recv" => {
                if args.len() != 1 {
                    return Err(LowerError(format!(
                        "recv 实参 {} 个 != 1（语义缺口）",
                        args.len()
                    )));
                }
                self.expr(f, &args[0])?;
                f.emit(Inst::ChanRecv);
            }
            _ => {
                let tmps = self.exprs_to_temps(f, args)?;
                for t in &tmps {
                    f.emit(Inst::LocalGet(*t));
                }
                f.emit(Inst::CallBuiltin {
                    name: name.to_string(),
                    argc: args.len() as u16,
                    has_ret: self.type_has_value(call),
                });
            }
        }
        Ok(())
    }

    // ———— match 降形 ————

    fn match_expr(
        &mut self,
        f: &mut FnCtx,
        kind: MatchKind,
        scrutinee: &HirExpr,
        arms: &[HirMatchArm],
        match_node: &HirExpr,
    ) -> Result<(), LowerError> {
        let match_ty = meta_of(match_node).type_id;
        let scrut = f.new_temp(None);
        self.expr(f, scrutinee)?;
        f.emit(Inst::LocalSet(scrut));
        // 分派块 = 持有 scrut 的当前块；臂体下降会切走 cur，
        // 由 match_literal/match_enum 负责切回并终接分派块。
        let dispatch_b = f.cur;
        // 值性取 match 节点类型（checker 已统一臂型）：void 臂（如
        // `Ok(v) => println(v)`）无结果槽，臂体只求值副作用。
        let has_value = self.type_has_value(match_node);
        let res = has_value.then(|| f.new_temp(match_ty));
        let end_b = f.new_block();
        let plan = MatchPlan {
            dispatch_b,
            scrut,
            scrutinee,
            arms,
            res,
            end_b,
        };
        match kind {
            MatchKind::Literal => self.match_literal(f, &plan)?,
            MatchKind::Enum | MatchKind::Result | MatchKind::Option => self.match_enum(f, &plan)?,
        }
        f.switch_to(end_b);
        if let Some(res) = res {
            f.emit(Inst::LocalGet(res));
        }
        Ok(())
    }

    /// 臂体公共尾：绑定作用域内求值臂体 →（有值 match）结果槽 → Goto(end)。
    fn arm_body(
        &mut self,
        f: &mut FnCtx,
        body: &HirExpr,
        res: Option<LocalId>,
        end_b: BlockId,
    ) -> Result<(), LowerError> {
        self.expr(f, body)?;
        if let Some(res) = res {
            f.emit(Inst::LocalSet(res));
        }
        f.set_term(Terminator::Goto { target: end_b })?;
        Ok(())
    }

    /// 字面量 match：int → SwitchInt；str/bool/float → Eq 链 + Branch。
    /// 负责终接 `plan.dispatch_b`（分派指令所在的块）。
    fn match_literal(&mut self, f: &mut FnCtx, plan: &MatchPlan) -> Result<(), LowerError> {
        let scrut_is_int = matches!(
            meta_of(plan.scrutinee).type_id.map(|t| self.types.data(t)),
            Some(TypeData::Int)
        );
        let all_int_arms = plan.arms.iter().all(|a| match &a.pat {
            HirPat::Lit { lit } => lit.kind == LitKind::Int,
            HirPat::Wild | HirPat::Bind { .. } => true,
            HirPat::Ctor { .. } => false,
        });
        if scrut_is_int && all_int_arms {
            // SwitchInt：arms 依源码臂序，首个命中即跳；通配/绑定臂 = default。
            let mut switch_arms: Vec<(i64, BlockId)> = Vec::new();
            let mut default = None;
            let mut arm_blocks: Vec<(BlockId, &HirMatchArm)> = Vec::new();
            for a in plan.arms {
                match &a.pat {
                    HirPat::Lit { lit } if lit.kind == LitKind::Int => {
                        let b = f.new_block();
                        switch_arms.push((lit.i64, b));
                        arm_blocks.push((b, a));
                    }
                    HirPat::Wild | HirPat::Bind { .. } => {
                        if default.is_none() {
                            let b = f.new_block();
                            default = Some(b);
                            arm_blocks.push((b, a));
                        }
                        // 第二个通配臂不可达（首中即跳），不生成。
                    }
                    _ => return Err(LowerError("int match 混入构造模式（语义缺口）".into())),
                }
            }
            f.switch_to(plan.dispatch_b);
            f.emit(Inst::LocalGet(plan.scrut));
            f.set_term(Terminator::SwitchInt {
                arms: switch_arms,
                default,
            })?;
            for (b, a) in arm_blocks {
                f.switch_to(b);
                f.push_scope();
                self.bind_whole(f, &a.pat, plan.scrut)?;
                self.arm_body(f, &a.body, plan.res, plan.end_b)?;
                f.pop_scope();
            }
        } else {
            // Eq 链：逐臂 LocalGet(scrut) → 常量 → Eq → Branch；首检查落分派块。
            f.switch_to(plan.dispatch_b);
            let fallback = Self::wildcard_arm(plan.arms);
            for a in plan.arms {
                let HirPat::Lit { lit } = &a.pat else {
                    continue;
                };
                let arm_b = f.new_block();
                let fail_b = f.new_block();
                f.emit(Inst::LocalGet(plan.scrut));
                self.emit_lit(f, lit)?;
                f.emit(Inst::Binary(Binop::Eq));
                f.set_term(Terminator::Branch {
                    if_true: arm_b,
                    if_false: fail_b,
                })?;
                f.switch_to(arm_b);
                f.push_scope();
                self.arm_body(f, &a.body, plan.res, plan.end_b)?;
                f.pop_scope();
                f.switch_to(fail_b);
            }
            match fallback {
                Some(a) => {
                    f.push_scope();
                    self.bind_whole(f, &a.pat, plan.scrut)?;
                    self.arm_body(f, &a.body, plan.res, plan.end_b)?;
                    f.pop_scope();
                }
                None => {
                    // 无兜底：运行时无匹配 = RUNTIME-MATCH-001（消费方解释）。
                    f.mark_dead(f.cur);
                    f.set_term(Terminator::Unreachable)?;
                }
            }
        }
        Ok(())
    }

    fn wildcard_arm(arms: &[HirMatchArm]) -> Option<&HirMatchArm> {
        arms.iter()
            .find(|a| matches!(a.pat, HirPat::Wild | HirPat::Bind { .. }))
    }

    /// 通配/绑定臂对 scrut 的整体绑定（绑定臂落槽；通配臂无操作）。
    fn bind_whole(
        &mut self,
        f: &mut FnCtx,
        pat: &HirPat,
        scrut: LocalId,
    ) -> Result<(), LowerError> {
        if let HirPat::Bind { name, .. } = pat {
            let slot = self.declare_binding(f, name, None);
            f.emit(Inst::LocalGet(scrut));
            if f.boxed.contains(name) {
                f.emit(Inst::BoxNew);
            }
            f.emit(Inst::LocalSet(slot));
        }
        Ok(())
    }

    /// enum/Option/Result match：SwitchEnum 分派 + 同 case 候选链 + 递归子模式。
    /// 负责终接 `plan.dispatch_b`。
    fn match_enum(&mut self, f: &mut FnCtx, plan: &MatchPlan) -> Result<(), LowerError> {
        let enum_id = self.enum_id_of_type(meta_of(plan.scrutinee).type_id)?;
        let case_count = self.enums[usize::from(enum_id)].cases.len();
        // 臂分类：构造臂按 case 分组（源码序）；首个通配/绑定臂 = 兜底。
        let mut per_case: Vec<Vec<&HirMatchArm>> = vec![Vec::new(); case_count];
        let mut wildcard: Option<&HirMatchArm> = None;
        for a in plan.arms {
            match &a.pat {
                HirPat::Ctor { ctor_sym, .. } => {
                    let sym = ctor_sym
                        .ok_or_else(|| LowerError("构造模式缺符号结论（语义缺口）".into()))?;
                    let ci = self.symbols.get(sym).case_index as usize;
                    if ci >= case_count {
                        return Err(LowerError(format!(
                            "case 序 {ci} 越界（enum 共 {case_count} case；语义缺口）"
                        )));
                    }
                    per_case[ci].push(a);
                }
                HirPat::Wild | HirPat::Bind { .. } => {
                    if wildcard.is_none() {
                        wildcard = Some(a);
                    }
                }
                HirPat::Lit { .. } => {
                    return Err(LowerError(
                        "enum match 混入字面量模式（E4002 已拒绝）".into(),
                    ));
                }
            }
        }
        // 兜底块（通配臂或不可达锚）。
        let fallback_b = f.new_block();
        match wildcard {
            Some(a) => {
                f.switch_to(fallback_b);
                f.push_scope();
                self.bind_whole(f, &a.pat, plan.scrut)?;
                self.arm_body(f, &a.body, plan.res, plan.end_b)?;
                f.pop_scope();
            }
            None => {
                f.switch_to(fallback_b);
                f.mark_dead(fallback_b);
                f.set_term(Terminator::Unreachable)?;
            }
        }
        // 每 case：候选链（源码序），末候选失败落兜底。
        let mut switch_arms: Vec<BlockId> = Vec::with_capacity(case_count);
        for candidates in &per_case {
            if candidates.is_empty() {
                switch_arms.push(fallback_b);
                continue;
            }
            let mut cblocks = Vec::with_capacity(candidates.len());
            for _ in candidates {
                cblocks.push(f.new_block());
            }
            switch_arms.push(cblocks[0]);
            for (k, a) in candidates.iter().enumerate() {
                let on_fail = cblocks.get(k + 1).copied().unwrap_or(fallback_b);
                let HirPat::Ctor { subs, .. } = &a.pat else {
                    return Err(LowerError("候选臂非构造模式（内部序错）".into()));
                };
                f.switch_to(cblocks[k]);
                f.push_scope();
                // 载荷解构：按位置取载荷入临时槽，再逐子模式匹配。
                let mut pays = Vec::with_capacity(subs.len());
                for i in 0..subs.len() {
                    let p = f.new_temp(None);
                    f.emit(Inst::LocalGet(plan.scrut));
                    f.emit(Inst::EnumPayloadGet { index: i as u16 });
                    f.emit(Inst::LocalSet(p));
                    pays.push(p);
                }
                for (i, sp) in subs.iter().enumerate() {
                    let ok_b = self.emit_sub_pat(f, sp, pays[i], on_fail)?;
                    f.switch_to(ok_b);
                }
                self.arm_body(f, &a.body, plan.res, plan.end_b)?;
                f.pop_scope();
            }
        }
        f.switch_to(plan.dispatch_b);
        f.emit(Inst::LocalGet(plan.scrut));
        f.set_term(Terminator::SwitchEnum {
            enum_id,
            arms: switch_arms,
            default: wildcard.map(|_| fallback_b),
        })?;
        Ok(())
    }

    /// 子模式匹配：值驻 `val` 槽；失败跳 `on_fail`；成功控制到达返回块
    /// （绑定已装入当前作用域）。通配/绑定直落，字面量 Eq 检查，
    /// 嵌套构造递归 SwitchEnum。
    fn emit_sub_pat(
        &mut self,
        f: &mut FnCtx,
        pat: &HirPat,
        val: LocalId,
        on_fail: BlockId,
    ) -> Result<BlockId, LowerError> {
        match pat {
            HirPat::Wild => Ok(f.cur),
            HirPat::Bind { name, .. } => {
                let slot = self.declare_binding(f, name, None);
                f.emit(Inst::LocalGet(val));
                if f.boxed.contains(name) {
                    f.emit(Inst::BoxNew);
                }
                f.emit(Inst::LocalSet(slot));
                Ok(f.cur)
            }
            HirPat::Lit { lit } => {
                let ok_b = f.new_block();
                f.emit(Inst::LocalGet(val));
                self.emit_lit(f, lit)?;
                f.emit(Inst::Binary(Binop::Eq));
                f.set_term(Terminator::Branch {
                    if_true: ok_b,
                    if_false: on_fail,
                })?;
                f.switch_to(ok_b);
                Ok(ok_b)
            }
            HirPat::Ctor { ctor_sym, subs } => {
                let sym = ctor_sym
                    .ok_or_else(|| LowerError("构造子模式缺符号结论（语义缺口）".into()))?;
                let (enum_id, case_id) = self.ctor_case(sym)?;
                let case_count = self.enums[usize::from(enum_id)].cases.len();
                let entry = f.new_block();
                let mut arms = vec![on_fail; case_count];
                arms[case_id as usize] = entry;
                f.emit(Inst::LocalGet(val));
                f.set_term(Terminator::SwitchEnum {
                    enum_id,
                    arms,
                    default: None,
                })?;
                f.switch_to(entry);
                let mut cur = f.cur;
                for (i, sp) in subs.iter().enumerate() {
                    let p = f.new_temp(None);
                    f.emit(Inst::LocalGet(val));
                    f.emit(Inst::EnumPayloadGet { index: i as u16 });
                    f.emit(Inst::LocalSet(p));
                    cur = self.emit_sub_pat(f, sp, p, on_fail)?;
                    f.switch_to(cur);
                }
                Ok(cur)
            }
        }
    }
}

// ———— 自由函数 ————

/// 节点元数据读取（HIR 元数据按变体内嵌，统一取用口）。
fn meta_of(e: &HirExpr) -> &NodeInfo {
    match e {
        HirExpr::Lit(l) => &l.meta,
        HirExpr::LocalRef { meta, .. }
        | HirExpr::FnRef { meta, .. }
        | HirExpr::BuiltinRef { meta, .. }
        | HirExpr::Ctor { meta, .. }
        | HirExpr::Call { meta, .. }
        | HirExpr::MethodCall { meta, .. }
        | HirExpr::Unary { meta, .. }
        | HirExpr::Binary { meta, .. }
        | HirExpr::AndThen { meta, .. }
        | HirExpr::OrElse { meta, .. }
        | HirExpr::Range { meta, .. }
        | HirExpr::Index { meta, .. }
        | HirExpr::Slice { meta, .. }
        | HirExpr::Field { meta, .. }
        | HirExpr::Try { meta, .. }
        | HirExpr::StructLit { meta, .. }
        | HirExpr::MapLit { meta, .. }
        | HirExpr::ArrayLit { meta, .. }
        | HirExpr::Match { meta, .. }
        | HirExpr::FnLit { meta, .. } => meta,
    }
}

/// 装箱名集：函数体内所有 `FnLit.captures` 名 ∪ `&x` 操作数名。
/// 名字集过近似（同名兄弟绑定亦装箱，语义透明）；不穿越嵌套具名 fn 边界。
fn collect_boxed_names(stmts: &[HirStmt]) -> HashSet<String> {
    let mut set = HashSet::new();
    collect_stmts(stmts, &mut set);
    set
}

fn collect_stmts(stmts: &[HirStmt], set: &mut HashSet<String>) {
    for s in stmts {
        match s {
            HirStmt::Let(l) => collect_expr(&l.init, set),
            HirStmt::Assign(a) => {
                collect_expr(&a.target, set);
                collect_expr(&a.rhs, set);
            }
            HirStmt::Eval { expr, .. } => collect_expr(expr, set),
            HirStmt::Return { val, .. } => {
                if let Some(v) = val {
                    collect_expr(v, set);
                }
            }
            HirStmt::If {
                cond, then, els, ..
            } => {
                collect_expr(cond, set);
                collect_stmts(then, set);
                collect_stmts(els, set);
            }
            HirStmt::While { cond, body, .. } => {
                collect_expr(cond, set);
                collect_stmts(body, set);
            }
            HirStmt::ForIn { iter, body, .. } => {
                collect_expr(iter, set);
                collect_stmts(body, set);
            }
            HirStmt::Spawn { args, .. } => {
                for a in args {
                    collect_expr(a, set);
                }
            }
            // 嵌套具名 fn 是独立函数边界：捕获不穿越，其体自行收集。
            HirStmt::FnStmt { .. } | HirStmt::Break { .. } | HirStmt::Continue { .. } => {}
        }
    }
}

fn collect_expr(e: &HirExpr, set: &mut HashSet<String>) {
    match e {
        HirExpr::FnLit { captures, body, .. } => {
            for n in captures {
                set.insert(n.clone());
            }
            collect_stmts(body, set);
        }
        HirExpr::Unary {
            op: HirUnaryOp::Ref,
            expr,
            ..
        } => {
            if let HirExpr::LocalRef { name, .. } = expr.as_ref() {
                set.insert(name.clone());
            }
        }
        HirExpr::Unary { expr, .. } => collect_expr(expr, set),
        HirExpr::Binary { lhs, rhs, .. }
        | HirExpr::AndThen { lhs, rhs, .. }
        | HirExpr::OrElse { lhs, rhs, .. } => {
            collect_expr(lhs, set);
            collect_expr(rhs, set);
        }
        HirExpr::Range { lo, hi, .. } => {
            collect_expr(lo, set);
            collect_expr(hi, set);
        }
        HirExpr::Index { obj, idx, .. } => {
            collect_expr(obj, set);
            collect_expr(idx, set);
        }
        HirExpr::Slice { obj, lo, hi, .. } => {
            collect_expr(obj, set);
            collect_expr(lo, set);
            collect_expr(hi, set);
        }
        HirExpr::Field { obj, .. } => collect_expr(obj, set),
        HirExpr::Try { expr, .. } => collect_expr(expr, set),
        HirExpr::Ctor { args, .. } | HirExpr::ArrayLit { elems: args, .. } => {
            for a in args {
                collect_expr(a, set);
            }
        }
        HirExpr::Call { callee, args, .. } => {
            if let Some(c) = callee {
                collect_expr(c, set);
            }
            for a in args {
                collect_expr(a, set);
            }
        }
        HirExpr::MethodCall { recv, args, .. } => {
            collect_expr(recv, set);
            for a in args {
                collect_expr(a, set);
            }
        }
        HirExpr::StructLit { fields, .. } => {
            for (_, v) in fields {
                collect_expr(v, set);
            }
        }
        HirExpr::MapLit { items, .. } => {
            for it in items {
                collect_expr(&it.key, set);
                collect_expr(&it.val, set);
            }
        }
        HirExpr::Match {
            scrutinee, arms, ..
        } => {
            collect_expr(scrutinee, set);
            for a in arms {
                collect_expr(&a.body, set);
            }
        }
        HirExpr::Lit(_)
        | HirExpr::LocalRef { .. }
        | HirExpr::FnRef { .. }
        | HirExpr::BuiltinRef { .. } => {}
    }
}

fn enum_decl_to_ir(ed: &HirEnumDecl) -> EnumDecl {
    EnumDecl {
        name: ed.name.clone(),
        type_params: ed.type_params.clone(),
        cases: ed
            .cases
            .iter()
            .map(|c| EnumCase {
                name: c.name.clone(),
                payload_types: c.payloads.clone(),
            })
            .collect(),
    }
}

fn fn_param_count(f: &HirFn) -> u16 {
    f.params.len() as u16 + u16::from(f.recv.is_some())
}

fn assign_binop(op: AssignOp) -> Binop {
    match op {
        AssignOp::AddAssign => Binop::Add,
        AssignOp::SubAssign => Binop::Sub,
        AssignOp::MulAssign => Binop::Mul,
        AssignOp::DivAssign => Binop::Div,
        AssignOp::Assign => unreachable!("Assign 不进复合赋值降形"),
    }
}

fn binop(op: HirBinaryOp) -> Binop {
    match op {
        HirBinaryOp::Eq => Binop::Eq,
        HirBinaryOp::Ne => Binop::Ne,
        HirBinaryOp::Lt => Binop::Lt,
        HirBinaryOp::Le => Binop::Le,
        HirBinaryOp::Gt => Binop::Gt,
        HirBinaryOp::Ge => Binop::Ge,
        HirBinaryOp::Add => Binop::Add,
        HirBinaryOp::Sub => Binop::Sub,
        HirBinaryOp::Mul => Binop::Mul,
        HirBinaryOp::Div => Binop::Div,
        HirBinaryOp::Rem => Binop::Rem,
        HirBinaryOp::BitAnd => Binop::BitAnd,
        HirBinaryOp::BitOr => Binop::BitOr,
        HirBinaryOp::BitXor => Binop::BitXor,
        HirBinaryOp::Shl => Binop::Shl,
        HirBinaryOp::Shr => Binop::Shr,
    }
}

/// 槽位种类：Plain 直存值；Boxed 槽位持 Ref；Capture 捕获槽（天然持 Ref）。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum SlotKind {
    Plain,
    Boxed,
    Capture,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
struct Slot {
    local: LocalId,
    kind: SlotKind,
}

/// 名字的落点：局部槽位或全局。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Place {
    Slot(Slot),
    Global(GlobalId),
}

/// 赋值目标路径组件（源码序）：`a[i].j` = [Index(i), Field("j")]。
#[derive(Clone, Copy, Debug)]
enum PathComp<'a> {
    Field(&'a str),
    Index(&'a HirExpr),
}

/// 赋值目标分解：Ident 根 + Field/Index 链（checker 保证目标形态）。
/// 返回 (根表达式, 链组件 源码序)。
fn split_assign_path(target: &HirExpr) -> (&HirExpr, Vec<PathComp<'_>>) {
    let mut comps = Vec::new();
    let mut cur = target;
    loop {
        match cur {
            HirExpr::Field { obj, name, .. } => {
                comps.push(PathComp::Field(name));
                cur = obj;
            }
            HirExpr::Index { obj, idx, .. } => {
                comps.push(PathComp::Index(idx));
                cur = obj;
            }
            other => return (other, comps.into_iter().rev().collect()),
        }
    }
}

/// match 降形的上下文打包（分派块/scrut 槽/臂集/结果槽/汇合块）。
struct MatchPlan<'a> {
    dispatch_b: BlockId,
    scrut: LocalId,
    scrutinee: &'a HirExpr,
    arms: &'a [lang_hir::HirMatchArm],
    res: Option<LocalId>,
    end_b: BlockId,
}

/// 每函数下降上下文：局部表（捕获 → 接收者/参数 → 局部/临时）、块图、
/// 作用域链、循环栈、`?` 的 err 槽与 fail 块队列。
struct FnCtx {
    name: String,
    capture_count: u16,
    param_count: u16,
    has_ret: bool,
    locals: Vec<LocalDecl>,
    blocks: Vec<BlockData>,
    dead: Vec<bool>,
    cur: BlockId,
    scopes: Vec<Vec<(String, Slot)>>,
    loops: Vec<(BlockId, BlockId)>,
    err_slot: Option<LocalId>,
    pending_try_fails: Vec<BlockId>,
    boxed: HashSet<String>,
}

struct BlockData {
    insts: Vec<Inst>,
    term: Option<Terminator>,
}

impl FnCtx {
    fn new(
        name: String,
        captures: &[String],
        recv: Option<&HirParam>,
        params: &[HirParam],
        ret: Option<TypeId>,
        boxed: HashSet<String>,
    ) -> Self {
        let mut f = Self {
            name,
            capture_count: captures.len() as u16,
            param_count: params.len() as u16 + u16::from(recv.is_some()),
            has_ret: ret.is_some(),
            locals: Vec::new(),
            blocks: Vec::new(),
            dead: Vec::new(),
            cur: BlockId(0),
            scopes: vec![Vec::new()],
            loops: Vec::new(),
            err_slot: None,
            pending_try_fails: Vec::new(),
            boxed,
        };
        // 布局 = [捕获槽][接收者][参数]（recv 为第 0 参，与 Call 压栈序一致）。
        for c in captures {
            let local = f.push_local(c.clone(), None);
            f.bind(
                c,
                Slot {
                    local,
                    kind: SlotKind::Capture,
                },
            );
        }
        if let Some(r) = recv {
            let kind = f.slot_kind_for(&r.name);
            let local = f.push_local(r.name.clone(), r.type_id);
            f.bind(&r.name, Slot { local, kind });
        }
        for p in params {
            let kind = f.slot_kind_for(&p.name);
            let local = f.push_local(p.name.clone(), p.type_id);
            f.bind(&p.name, Slot { local, kind });
        }
        f.new_block(); // 入口块 = 块 0
        f.cur = BlockId(0);
        f
    }

    fn slot_kind_for(&self, name: &str) -> SlotKind {
        if self.boxed.contains(name) {
            SlotKind::Boxed
        } else {
            SlotKind::Plain
        }
    }

    fn push_local(&mut self, name: String, ty: Option<TypeId>) -> LocalId {
        let id = LocalId(self.locals.len() as u32);
        self.locals.push(LocalDecl {
            name,
            mutable: true,
            type_id: ty,
        });
        id
    }

    fn new_local(&mut self, name: String, ty: Option<TypeId>) -> LocalId {
        self.push_local(name, ty)
    }

    fn new_temp(&mut self, ty: Option<TypeId>) -> LocalId {
        let n = self.locals.len();
        self.push_local(format!("<t{n}>"), ty)
    }

    fn param_kind(&self, slot: LocalId) -> SlotKind {
        // 捕获槽天然 Ref；装箱参数由装箱名集判定（构造时已登记 kind 于作用域）。
        let name = &self.locals[usize::from(slot)].name;
        if self.boxed.contains(name) {
            SlotKind::Boxed
        } else {
            SlotKind::Plain
        }
    }

    fn new_block(&mut self) -> BlockId {
        self.blocks.push(BlockData {
            insts: Vec::new(),
            term: None,
        });
        self.dead.push(false);
        BlockId(self.blocks.len() as u32 - 1)
    }

    fn switch_to(&mut self, b: BlockId) {
        self.cur = b;
    }

    fn open_dead_block(&mut self) {
        let b = self.new_block();
        self.dead[b.0 as usize] = true;
        self.cur = b;
    }

    fn mark_dead(&mut self, b: BlockId) {
        self.dead[b.0 as usize] = true;
    }

    fn emit(&mut self, inst: Inst) {
        self.blocks[self.cur.0 as usize].insts.push(inst);
    }

    fn set_term(&mut self, term: Terminator) -> Result<(), LowerError> {
        let b = &mut self.blocks[self.cur.0 as usize];
        if b.term.is_some() {
            return Err(LowerError(format!(
                "fn {} block {} 重复终接（lowering 内部序错）",
                self.name, self.cur
            )));
        }
        b.term = Some(term);
        Ok(())
    }

    fn push_scope(&mut self) {
        self.scopes.push(Vec::new());
    }

    fn pop_scope(&mut self) {
        self.scopes.pop();
    }

    fn bind(&mut self, name: &str, slot: Slot) {
        if let Some(top) = self.scopes.last_mut() {
            top.push((name.to_string(), slot));
        }
    }

    /// 名字 → 槽位（内层作用域优先，后绑定遮蔽先绑定）。
    fn resolve(&self, name: &str) -> Option<Slot> {
        for scope in self.scopes.iter().rev() {
            for (n, s) in scope.iter().rev() {
                if n == name {
                    return Some(*s);
                }
            }
        }
        None
    }

    /// 组装 [`IrFunction`]；未终接块：死块补 `Unreachable`，活块为内部错误。
    fn finish(mut self) -> Result<IrFunction, LowerError> {
        for (bid, b) in self.blocks.iter_mut().enumerate() {
            if b.term.is_none() {
                if self.dead[bid] {
                    b.term = Some(Terminator::Unreachable);
                } else {
                    return Err(LowerError(format!(
                        "fn {} block {bid} 未终接（lowering 内部序错）",
                        self.name
                    )));
                }
            }
        }
        let blocks = self
            .blocks
            .into_iter()
            .map(|b| Block {
                insts: b.insts,
                term: b.term.unwrap_or(Terminator::Unreachable),
            })
            .collect();
        Ok(IrFunction {
            name: self.name,
            capture_count: self.capture_count,
            sig: FnSig {
                param_count: self.param_count,
                has_ret: self.has_ret,
            },
            locals: self.locals,
            blocks,
            extern_host: None,
        })
    }
}
