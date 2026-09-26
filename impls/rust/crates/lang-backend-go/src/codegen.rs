//! Core IR → Go 源码生成器（单文件、`package main`、prelude 内联）。
//!
//! 发射架构（IR 无静态类型表，TypeId 在此不可解，一切值走动态 `any` 域）：
//! - **函数**：每 IR 函数发射 `func lf{n}_{名}(as ...any) any`；捕获槽/参数槽
//!   （`locals[0..fixed_slots)`）映射 `as[i]`，其余局部预声明 `var l{i} any`。
//!   统一变参签名让直接调用/闭包调用/spawn 共享一条调用约定。
//! - **基本块**：函数顶层 label `L{k}` + 花括号块；块内语句线性，终接发射为
//!   `goto`/`if{goto}+goto`/带 default 的 `switch{goto}`/`return`/`panic`——
//!   每个块都编译为 Go 意义上的**终结语句**，函数无 missing-return。
//! - **操作数栈**：符号串栈。IR 无 Dup 类指令，每个栈值至多消费一次，可安全内联；
//!   需要「一值两用」的指令（[`Inst::Try`]、[`Inst::IterNext`]）先物化临时 `_t{n}`。
//! - **值语义**：用户 struct 以 `*S{n}_{名}` 指针承载，在绑定/传参/返回/容器写入点
//!   显式 `langGoCopy`（prelude）；容器/引用/枚举按引用共享。
//! - **确定性**：发射顺序 = 声明序/块序/指令序，无 map 遍历、无随机源；
//!   同一 [`IrProgram`] 两次生成逐字节相等（测试锁定）。

use std::fmt::Display;

use lang_core::ids::LocalId;
use lang_ir::{Binop, BlockId, Inst, IrProgram, Terminator, Unop};

use crate::fmt::quote_go;
use crate::prelude::GO_PRELUDE;

/// 生成失败（内部错误：未知内建名等；非用户诊断）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GenError(pub String);

impl Display for GenError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Go 生成失败: {}", self.0)
    }
}

impl std::error::Error for GenError {}

/// Core IR → 自包含可编译 Go 源码（`go build <file>` 直接可用）。
pub fn generate(prog: &IrProgram) -> Result<String, GenError> {
    GoGen::new(prog).emit_program()
}

/// Go 关键字（字段/标识符发射时须避开）。
const GO_KEYWORDS: &[&str] = &[
    "break",
    "case",
    "chan",
    "const",
    "continue",
    "default",
    "defer",
    "else",
    "fallthrough",
    "for",
    "func",
    "go",
    "goto",
    "if",
    "import",
    "interface",
    "map",
    "package",
    "range",
    "return",
    "select",
    "struct",
    "switch",
    "type",
    "var",
];

/// 标识符消毒：非字母数字/下划线 → `_`（Go/JS 标识符共用规则）。
fn sanitize_ident(name: &str) -> String {
    name.chars()
        .map(|c| {
            if c.is_alphanumeric() || c == '_' {
                c
            } else {
                '_'
            }
        })
        .collect()
}

/// 用户函数发射名：`lf{下标}_{消毒名}`——下标保证唯一，前缀避开 prelude/关键字。
fn fn_name(idx: usize, name: &str) -> String {
    format!("lf{idx}_{}", sanitize_ident(name))
}

/// 用户 struct 发射类型名：`S{下标}_{消毒名}`。
fn struct_type_name(idx: usize, name: &str) -> String {
    format!("S{idx}_{}", sanitize_ident(name))
}

/// 全局变量发射名：`g{下标}_{消毒名}`。
fn global_name(idx: usize, name: &str) -> String {
    format!("g{idx}_{}", sanitize_ident(name))
}

/// struct 字段的 Go 字段名：消毒 + 关键字/数字开头加 `X` 前缀
/// （动态访问经 `LangGoField(原名)` 接口，不依赖此形）。
fn go_field_name(name: &str) -> String {
    let s = sanitize_ident(name);
    if s.is_empty()
        || s.chars().next().is_some_and(|c| c.is_ascii_digit())
        || GO_KEYWORDS.contains(&s.as_str())
    {
        format!("X{s}")
    } else {
        s
    }
}

/// float 常量的 Go 字面量（最短 `{:e}` 科学计数恒为合法 Go 浮点字面量；
/// 特殊值经 math 包，规避无 `-0.0` 常量的语义歧义）。
fn go_float_lit(f: f64) -> String {
    if f.is_nan() {
        return "math.NaN()".into();
    }
    if f.is_infinite() {
        return format!("math.Inf({})", if f > 0.0 { 1 } else { -1 });
    }
    if f == 0.0 {
        return if f.is_sign_negative() {
            "math.Copysign(0, -1)".into()
        } else {
            "float64(0)".into()
        };
    }
    format!("float64({f:e})")
}

/// 函数内被任何跳转/分支/分派/迭代耗尽/Try 失败引用的块号集合。
fn referenced_blocks(f: &lang_ir::IrFunction) -> std::collections::BTreeSet<usize> {
    let mut refs = std::collections::BTreeSet::new();
    let mut note = |b: BlockId| {
        refs.insert(b.0 as usize);
    };
    for block in &f.blocks {
        for inst in &block.insts {
            if let Inst::IterNext { done } = inst {
                note(*done);
            }
            if let Inst::Try { fail, .. } = inst {
                note(*fail);
            }
        }
        match &block.term {
            Terminator::Goto { target } => note(*target),
            Terminator::Branch { if_true, if_false } => {
                note(*if_true);
                note(*if_false);
            }
            Terminator::SwitchInt { arms, default } => {
                for (_, t) in arms {
                    note(*t);
                }
                if let Some(d) = default {
                    note(*d);
                }
            }
            Terminator::SwitchEnum { arms, default, .. } => {
                for t in arms {
                    note(*t);
                }
                if let Some(d) = default {
                    note(*d);
                }
            }
            Terminator::Return { .. } | Terminator::Unreachable => {}
        }
    }
    refs
}

/// 标记作为赋值写链地址组件的 `FieldGet`/`IndexGet`（发射为 Ref 读形——
/// 不落副本，最终 set 在底层存储上写穿；与 IrMachine 的 Place 溯源操作数同
/// 语义。普通读保持「读出皆副本」VALUE-ELEM-009）。
///
/// 链形（lowering 约定，块内连续指令）：`[LocalGet|GlobalGet,
/// (FieldGet | LocalGet + IndexGet)*]` 后随 `(LocalGet(val),)` +
/// `FieldSet|IndexSet|ArrayPush`（IndexSet 为 val、idx 两个 LocalGet）。
/// 回溯不匹配链形时什么都不标（保守回退普通读形，hand-built IR 安全）。
fn mark_ref_reads(insts: &[Inst]) -> std::collections::BTreeSet<usize> {
    let mut marked = std::collections::BTreeSet::new();
    for i in 0..insts.len() {
        // obj 操作数产生者相对 set 指令的位置（跳过 val（与 idx）的 LocalGet）。
        let obj_at = match insts[i] {
            Inst::FieldSet { .. } | Inst::ArrayPush => i.checked_sub(2),
            Inst::IndexSet => i.checked_sub(3),
            _ => continue,
        };
        let Some(mut j) = obj_at else {
            continue;
        };
        loop {
            match insts.get(j) {
                Some(Inst::FieldGet { .. }) => {
                    marked.insert(j);
                    j -= 1;
                }
                Some(Inst::IndexGet) => {
                    marked.insert(j);
                    // idx 产生者 = 紧邻的 LocalGet(idx_temp)
                    if j >= 1 && matches!(insts[j - 1], Inst::LocalGet(_)) {
                        j -= 2;
                    } else {
                        break;
                    }
                }
                // 根（LocalGet/GlobalGet）= 链头，停。
                _ => break,
            }
        }
    }
    marked
}

struct GoGen<'a> {
    prog: &'a IrProgram,
    out: String,
    indent: usize,
    /// 当前函数的临时计数（`_t{n}`/`_it{n}`；每函数复位保证确定性）。
    tmp: usize,
    /// 符号操作数栈（块入口为空）。
    stack: Vec<String>,
    /// 当前函数 fixed_slots（捕获 + 参数），槽位寻址用。
    fixed_slots: usize,
    /// 当前函数体含 Spawn（Return 前插 join）。
    spawns: bool,
    /// 当前块内作为写链地址组件的指令下标（发射 Ref 读形）。
    ref_reads: std::collections::BTreeSet<usize>,
}

impl<'a> GoGen<'a> {
    fn new(prog: &'a IrProgram) -> Self {
        Self {
            prog,
            out: String::new(),
            indent: 0,
            tmp: 0,
            stack: Vec::new(),
            fixed_slots: 0,
            spawns: false,
            ref_reads: std::collections::BTreeSet::new(),
        }
    }

    fn w(&mut self, line: &str) {
        for _ in 0..self.indent {
            self.out.push('\t');
        }
        self.out.push_str(line);
        self.out.push('\n');
    }

    fn new_tmp(&mut self) -> String {
        self.tmp += 1;
        format!("_t{}", self.tmp)
    }

    fn new_iter_tmp(&mut self) -> String {
        self.tmp += 1;
        format!("_it{}", self.tmp)
    }

    fn push(&mut self, expr: String) {
        self.stack.push(expr);
    }

    fn pop(&mut self) -> String {
        self.stack
            .pop()
            .unwrap_or_else(|| "panic(\"栈下溢\")".to_string())
    }

    /// 弹 `n` 个并按**压入序**返回（弹序逆序）。
    fn pop_n(&mut self, n: usize) -> Vec<String> {
        let mut out = vec![String::new(); n];
        for slot in out.iter_mut().rev() {
            *slot = self.pop();
        }
        out
    }

    /// 局部槽位引用：捕获/参数槽 = `as[i]`（闭包调用约定把 Env 前置），
    /// 普通局部 = 预声明变量 `l{i}`。
    fn slot_ref(&self, id: LocalId) -> String {
        let i = id.0 as usize;
        if i < self.fixed_slots {
            format!("as[{i}]")
        } else {
            format!("l{i}")
        }
    }

    // ———— 程序 ————

    fn emit_program(mut self) -> Result<String, GenError> {
        self.out.push_str(GO_PRELUDE.trim_end_matches('\n'));
        self.out.push('\n');

        for (i, s) in self.prog.structs.iter().enumerate() {
            self.emit_struct(i, s);
        }
        for (i, g) in self.prog.globals.iter().enumerate() {
            self.w(&format!("var {} any", global_name(i, &g.name)));
        }
        for (i, f) in self.prog.functions.iter().enumerate() {
            self.emit_function(i, f)?;
        }
        self.emit_main();
        Ok(self.out)
    }

    fn emit_struct(&mut self, idx: usize, s: &lang_ir::StructDecl) {
        let gname = struct_type_name(idx, &s.name);
        self.w(&format!("type {gname} struct {{"));
        self.indent += 1;
        for f in &s.fields {
            self.w(&format!("{} any", go_field_name(&f.name)));
        }
        self.indent -= 1;
        self.w("}");
        self.w("");

        // 值语义拷贝（langGoCopy 的 struct 分派点）
        self.w(&format!("func (v *{gname}) langGoClone() any {{"));
        self.indent += 1;
        self.w("n := *v");
        for f in &s.fields {
            let fname = go_field_name(&f.name);
            self.w(&format!("n.{fname} = langGoCopy(v.{fname})"));
        }
        self.w("return &n");
        self.indent -= 1;
        self.w("}");

        // 打印（镜像解释器 fmtVal struct 分支：声明序、嵌套形）
        let parts: Vec<String> = s
            .fields
            .iter()
            .map(|f| {
                format!(
                    "{} + \": \" + langGoFmt(v.{}, false)",
                    quote_go(&f.name),
                    go_field_name(&f.name)
                )
            })
            .collect();
        self.w(&format!(
            "func (v *{gname}) langGoString(top bool) string {{"
        ));
        self.indent += 1;
        self.w(&format!(
            "return {} + \"{{\" + strings.Join([]string{{{}}}, \", \") + \"}}\"",
            quote_go(&s.name),
            parts.join(", ")
        ));
        self.indent -= 1;
        self.w("}");

        // 动态字段读写（FieldGet/FieldSet 经接口分派）
        self.w(&format!(
            "func (v *{gname}) LangGoField(name string) any {{"
        ));
        self.indent += 1;
        self.w("switch name {");
        for f in &s.fields {
            self.w(&format!("case {}:", quote_go(&f.name)));
            self.indent += 1;
            self.w(&format!("return v.{}", go_field_name(&f.name)));
            self.indent -= 1;
        }
        self.w("}");
        self.w(&format!(
            "panic({} + \" 没有字段 \" + name)",
            quote_go(&s.name)
        ));
        self.indent -= 1;
        self.w("}");

        self.w(&format!(
            "func (v *{gname}) LangGoSetField(name string, val any) {{"
        ));
        self.indent += 1;
        self.w("switch name {");
        for f in &s.fields {
            let fname = go_field_name(&f.name);
            self.w(&format!("case {}:", quote_go(&f.name)));
            self.indent += 1;
            self.w(&format!("v.{fname} = langGoCopy(val)"));
            self.w("return");
            self.indent -= 1;
        }
        self.w("}");
        self.w(&format!(
            "panic({} + \" 没有字段 \" + name)",
            quote_go(&s.name)
        ));
        self.indent -= 1;
        self.w("}");

        self.w(&format!(
            "func (v *{gname}) LangGoTypeName() string {{ return {} }}",
            quote_go(&s.name)
        ));
        self.w("");
    }

    fn emit_function(&mut self, idx: usize, f: &lang_ir::IrFunction) -> Result<(), GenError> {
        self.tmp = 0;
        self.spawns = false;
        self.fixed_slots = f.fixed_slots();
        let name = fn_name(idx, &f.name);
        if f.extern_host.is_some() {
            // extern 宿主函数：独立产物无宿主可接，执行到即内部错误
            self.w(&format!(
                "// extern 宿主函数 {}（{}）：独立产物未链接宿主实现",
                quote_go(&f.name),
                f.extern_host.as_deref().unwrap_or("?")
            ));
            self.w(&format!("func {name}(as ...any) any {{"));
            self.indent += 1;
            self.w(&format!(
                "panic(\"extern 宿主函数未链接: {}\")",
                f.extern_host
                    .as_deref()
                    .unwrap_or("?")
                    .replace('\\', "\\\\")
                    .replace('"', "\\\"")
            ));
            self.indent -= 1;
            self.w("}");
            self.w("");
            return Ok(());
        }
        self.w(&format!("func {name}(as ...any) any {{"));
        self.indent += 1;
        for i in self.fixed_slots..f.locals.len() {
            self.w(&format!("var l{i} any"));
            self.w(&format!("_ = l{i}"));
        }
        // 只给被跳转引用的块发 label（入口块与死块无 label；
        // Go 把「label defined and not used」当编译错误）
        let referenced = referenced_blocks(f);
        for bi in 0..f.blocks.len() {
            let block = &f.blocks[bi];
            if referenced.contains(&bi) {
                self.w(&format!("L{bi}:"));
            }
            self.w("{");
            self.indent += 1;
            self.stack.clear();
            self.ref_reads = mark_ref_reads(&block.insts);
            for (ii, inst) in block.insts.iter().enumerate() {
                self.emit_inst(ii, inst)?;
            }
            self.emit_terminator(&block.term)?;
            self.indent -= 1;
            self.w("}");
        }
        self.indent -= 1;
        self.w("}");
        self.w("");
        Ok(())
    }

    fn emit_main(&mut self) {
        self.w("func main() {");
        self.indent += 1;
        if let Some(init) = self.prog.init {
            self.w(&format!(
                "{}()",
                fn_name(init.0 as usize, &self.prog.functions[init.0 as usize].name)
            ));
        }
        if let Some(main) = self.prog.main {
            self.w(&format!(
                "{}()",
                fn_name(main.0 as usize, &self.prog.functions[main.0 as usize].name)
            ));
        }
        self.w("langGoWaitAll()");
        self.indent -= 1;
        self.w("}");
    }

    // ———— 指令 ————

    fn emit_inst(&mut self, pos: usize, inst: &Inst) -> Result<(), GenError> {
        match inst {
            Inst::ConstI64(v) => self.push(format!("int64({v})")),
            Inst::ConstF64(x) => self.push(go_float_lit(*x)),
            Inst::ConstBool(b) => self.push(if *b { "true" } else { "false" }.into()),
            Inst::ConstStr(s) => self.push(quote_go(s)),

            Inst::LocalGet(id) => {
                let r = self.slot_ref(*id);
                self.push(r);
            }
            Inst::LocalSet(id) => {
                let v = self.pop();
                let r = self.slot_ref(*id);
                self.w(&format!("{r} = langGoCopy({v})"));
            }
            Inst::GlobalGet(g) => {
                let decl = &self.prog.globals[g.0 as usize];
                let name = global_name(g.0 as usize, &decl.name);
                self.push(name);
            }
            Inst::GlobalSet(g) => {
                let v = self.pop();
                let decl = &self.prog.globals[g.0 as usize];
                let name = global_name(g.0 as usize, &decl.name);
                self.w(&format!("{name} = langGoCopy({v})"));
            }

            Inst::Unary(op) => {
                let v = self.pop();
                match op {
                    Unop::Neg => self.push(format!("langGoNeg({v})")),
                    Unop::Not => self.push(format!("langGoNot({v})")),
                }
            }
            Inst::Binary(op) => {
                let r = self.pop();
                let l = self.pop();
                self.push(emit_binop(*op, &l, &r));
            }
            Inst::Pop => {
                let v = self.pop();
                self.w(&format!("_ = {v}"));
            }

            Inst::Call(func) => {
                let target = &self.prog.functions[func.0 as usize];
                let args = self.pop_n(target.sig.param_count as usize);
                let name = fn_name(func.0 as usize, &target.name);
                let call = format!("{name}({})", args.join(", "));
                // out = 目标 has_ret：有值压栈，无值为语句（调用本身即效果）
                if target.sig.has_ret {
                    self.push(call);
                } else {
                    self.w(&call);
                }
            }
            Inst::CallIndirect { argc, has_ret } => {
                let args = self.pop_n(*argc as usize);
                let callee = self.pop();
                let call = format!("langGoCallClosure({}{})", callee, prepend_args(&args));
                if *has_ret {
                    self.push(call);
                } else {
                    self.w(&call);
                }
            }
            Inst::CallBuiltin {
                name,
                argc,
                has_ret,
            } => {
                let args = self.pop_n(*argc as usize);
                let call = emit_builtin(name, &args)?;
                if *has_ret {
                    self.push(call);
                } else {
                    self.w(&call);
                }
            }
            Inst::Spawn { func, argc } => {
                let args = self.pop_n(*argc as usize);
                self.spawns = true;
                let name = fn_name(func.0 as usize, &self.prog.functions[func.0 as usize].name);
                self.w(&format!(
                    "langGoGo(func(sa ...any) {{ {name}(sa...) }}, []any{{{}}})",
                    args.join(", ")
                ));
            }
            Inst::ClosureNew { func, captures } => {
                let caps = self.pop_n(*captures as usize);
                let env = if caps.is_empty() {
                    "nil".to_string()
                } else {
                    format!("[]any{{{}}}", caps.join(", "))
                };
                let name = fn_name(func.0 as usize, &self.prog.functions[func.0 as usize].name);
                self.push(format!(
                    "&langGoClosure{{Fn: {name}, Env: {env}, Name: {}}}",
                    quote_go(&self.prog.functions[func.0 as usize].name)
                ));
            }

            Inst::IndexGet => {
                let idx = self.pop();
                let obj = self.pop();
                if self.ref_reads.contains(&pos) {
                    // 写链地址组件：Ref 读形（不落副本，最终 set 写穿）
                    self.push(format!("langGoIndexRef({obj}, {idx})"));
                } else {
                    self.push(format!("langGoIndex({obj}, {idx})"));
                }
            }
            Inst::IndexSet => {
                let val = self.pop();
                let idx = self.pop();
                let obj = self.pop();
                self.w(&format!("langGoSetIndex({obj}, {idx}, {val})"));
            }
            Inst::SliceGet => {
                let hi = self.pop();
                let lo = self.pop();
                let obj = self.pop();
                self.push(format!("langGoSlice({obj}, {lo}, {hi})"));
            }
            Inst::FieldGet { field } => {
                let obj = self.pop();
                if self.ref_reads.contains(&pos) {
                    // 写链地址组件：Ref 读形（不落副本，最终 set 写穿）
                    self.push(format!("langGoFieldRef({obj}, {})", quote_go(field)));
                } else {
                    self.push(format!("langGoField({obj}, {})", quote_go(field)));
                }
            }
            Inst::FieldSet { field } => {
                let val = self.pop();
                let obj = self.pop();
                self.w(&format!(
                    "langGoSetField({obj}, {}, {val})",
                    quote_go(field)
                ));
            }
            Inst::ArrayPush => {
                // 数组 `+=`：rhs 为数组 → 原位拼接（IrMachine ArrayPush 同语义；
                // 单元素追加是 push 内建，走 CallBuiltin langGoPush）
                let val = self.pop();
                let arr = self.pop();
                self.w(&format!("langGoPushArr({arr}, {val})"));
            }

            Inst::ArrayNew { count } => {
                let items = self.pop_n(*count as usize);
                if items.is_empty() {
                    self.push("&LangGoArr{}".into());
                } else {
                    self.push(format!(
                        "&LangGoArr{{Items: []any{{{}}}}}",
                        items.join(", ")
                    ));
                }
            }
            Inst::MapNew { count } => {
                let flat = self.pop_n(*count as usize * 2);
                let kvs: Vec<String> = flat
                    .chunks(2)
                    .map(|p| format!("langGoKV{{{}}}", p.join(", ")))
                    .collect();
                self.push(format!("langGoNewMap({})", kvs.join(", ")));
            }
            Inst::StructNew { struct_id } => {
                let decl = &self.prog.structs[struct_id.0 as usize];
                let fields = self.pop_n(decl.fields.len());
                let inits: Vec<String> = decl
                    .fields
                    .iter()
                    .zip(fields)
                    .map(|(f, v)| format!("{}: {v}", go_field_name(&f.name)))
                    .collect();
                self.push(format!(
                    "&{}{{{}}}",
                    struct_type_name(struct_id.0 as usize, &decl.name),
                    inits.join(", ")
                ));
            }
            Inst::EnumNew { enum_id, case_id } => {
                let decl = &self.prog.enums[enum_id.0 as usize];
                let case = &decl.cases[*case_id as usize];
                let payloads = self.pop_n(case.payload_types.len());
                self.push(format!(
                    "langGoEnumVal({}, {}, {})",
                    quote_go(&decl.name),
                    quote_go(&case.name),
                    payloads.join(", ")
                ));
            }
            Inst::RangeNew => {
                // 操作数经临时槽（any）入 struct 字面量须断言 int64
                //（对齐 SwitchInt 等整型操作数的 langGoInt 处理）
                let hi = self.pop();
                let lo = self.pop();
                self.push(format!(
                    "langGoRange{{Lo: langGoInt({lo}), Hi: langGoInt({hi})}}"
                ));
            }

            Inst::BoxNew => {
                let v = self.pop();
                self.push(format!("langGoBox({v})"));
            }
            Inst::Deref => {
                let v = self.pop();
                self.push(format!("langGoDeref({v})"));
            }
            Inst::RefSet => {
                let val = self.pop();
                let r = self.pop();
                self.w(&format!("langGoRefSet({r}, {val})"));
            }

            Inst::EnumPayloadGet { index } => {
                let v = self.pop();
                self.push(format!("langGoEnumPayload({v}, {index})"));
            }

            Inst::IterNew => {
                let v = self.pop();
                self.push(format!("langGoIterNew({v})"));
            }
            Inst::IterNext { done } => {
                let v = self.pop();
                let it = self.new_iter_tmp();
                self.w(&format!("{it} := {v}.(*langGoIter)"));
                self.w(&format!("if {it}.I >= int64(len({it}.Items)) {{"));
                self.indent += 1;
                self.w(&format!("goto L{}", done.0));
                self.indent -= 1;
                self.w("}");
                let e = self.new_tmp();
                self.w(&format!("{e} := {it}.Items[{it}.I]"));
                self.w(&format!("{it}.I++"));
                self.push(format!("langGoCopy({e})"));
            }

            Inst::ChanNew => self.push("langGoNewChan()".into()),
            Inst::ChanSend => {
                let val = self.pop();
                let ch = self.pop();
                self.w(&format!("langGoSend({ch}, {val})"));
            }
            Inst::ChanRecv => {
                let ch = self.pop();
                self.push(format!("langGoRecv({ch})"));
            }

            Inst::Try { err, fail } => {
                let v = self.pop();
                let t = self.new_tmp();
                self.w(&format!("{t} := {v}"));
                self.w(&format!("if langGoIsErr({t}) {{"));
                self.indent += 1;
                let slot = self.slot_ref(*err);
                self.w(&format!("{slot} = langGoTryErrOf({t})"));
                self.w(&format!("goto L{}", fail.0));
                self.indent -= 1;
                self.w("}");
                self.push(format!("langGoTryOk({t})"));
            }
        }
        Ok(())
    }

    // ———— 终接 ————

    fn emit_terminator(&mut self, term: &Terminator) -> Result<(), GenError> {
        match term {
            Terminator::Goto { target } => {
                self.w(&format!("goto L{}", target.0));
            }
            Terminator::Branch { if_true, if_false } => {
                let c = self.pop();
                if if_true == if_false {
                    self.w(&format!("goto L{}", if_true.0));
                } else {
                    self.w(&format!("if langGoTruthy({c}) {{"));
                    self.indent += 1;
                    self.w(&format!("goto L{}", if_true.0));
                    self.indent -= 1;
                    self.w("}");
                    self.w(&format!("goto L{}", if_false.0));
                }
            }
            Terminator::SwitchInt { arms, default } => {
                let v = self.pop();
                self.w(&format!("switch langGoInt({v}) {{"));
                for (val, target) in arms {
                    self.w(&format!("case {val}:"));
                    self.indent += 1;
                    self.w(&format!("goto L{}", target.0));
                    self.indent -= 1;
                }
                self.emit_switch_tail(*default)?;
                self.w("}");
            }
            Terminator::SwitchEnum {
                enum_id,
                arms,
                default,
            } => {
                let v = self.pop();
                let decl = &self.prog.enums[enum_id.0 as usize];
                self.w(&format!("switch langGoEnumTag({v}) {{"));
                for (case, target) in decl.cases.iter().zip(arms) {
                    self.w(&format!("case {}:", quote_go(&case.name)));
                    self.indent += 1;
                    self.w(&format!("goto L{}", target.0));
                    self.indent -= 1;
                }
                self.emit_switch_tail(*default)?;
                self.w("}");
            }
            Terminator::Return { has_value } => {
                // 函数边界 join：含 go 语句的函数每个返回点先等自身 Task（TASK-DETERMINISM-003）
                if self.spawns {
                    self.w("langGoWaitAll()");
                }
                if *has_value {
                    let v = self.pop();
                    self.w(&format!("return langGoCopy({v})"));
                } else {
                    self.w("return nil");
                }
            }
            Terminator::Unreachable => {
                self.w("panic(\"不可达\")");
            }
        }
        Ok(())
    }

    /// 分派 switch 的收尾：default 或运行时无臂命中错误（RUNTIME-MATCH-001）。
    fn emit_switch_tail(&mut self, default: Option<BlockId>) -> Result<(), GenError> {
        self.w("default:");
        self.indent += 1;
        match default {
            Some(d) => self.w(&format!("goto L{}", d.0)),
            None => self.w("panic(\"match 无匹配分支\")"),
        }
        self.indent -= 1;
        Ok(())
    }
}

/// 调用实参拼接：`, a, b`（无实参为空串）。
fn prepend_args(args: &[String]) -> String {
    if args.is_empty() {
        String::new()
    } else {
        format!(", {}", args.join(", "))
    }
}

fn emit_binop(op: Binop, l: &str, r: &str) -> String {
    match op {
        Binop::Eq => format!("langGoEq({l}, {r})"),
        Binop::Ne => format!("!langGoEq({l}, {r})"),
        Binop::Lt => format!("langGoCompare({l}, {r}) < 0"),
        Binop::Le => format!("langGoCompare({l}, {r}) <= 0"),
        Binop::Gt => format!("langGoCompare({l}, {r}) > 0"),
        Binop::Ge => format!("langGoCompare({l}, {r}) >= 0"),
        Binop::Add => format!("langGoArith(\"+\", {l}, {r})"),
        Binop::Sub => format!("langGoArith(\"-\", {l}, {r})"),
        Binop::Mul => format!("langGoArith(\"*\", {l}, {r})"),
        Binop::Div => format!("langGoArith(\"/\", {l}, {r})"),
        Binop::Rem => format!("langGoArith(\"%\", {l}, {r})"),
        Binop::BitAnd => format!("langGoBits(\"&\", {l}, {r})"),
        Binop::BitOr => format!("langGoBits(\"|\", {l}, {r})"),
        Binop::BitXor => format!("langGoBits(\"^\", {l}, {r})"),
        Binop::Shl => format!("langGoBits(\"<<\", {l}, {r})"),
        Binop::Shr => format!("langGoBits(\">>\", {l}, {r})"),
    }
}

/// 内建名 → prelude 运行时调用（实参个数由 IR argc 保证，未知名 = 内部错误）。
fn emit_builtin(name: &str, args: &[String]) -> Result<String, GenError> {
    let join = args.join(", ");
    let call = match name {
        "println" => format!("langGoPrintln({join})"),
        "print" => format!("langGoPrint({join})"),
        "len" => format!("langGoLen({join})"),
        "str" => format!("langGoStr({join})"),
        "int" => format!("langGoInt({join})"),
        "float" => format!("langGoFloat({join})"),
        "keys" => format!("langGoKeys({join})"),
        "values" => format!("langGoValues({join})"),
        "has" => format!("langGoHas({join})"),
        "push" => format!("langGoPush({join})"),
        "assert" => format!("langGoAssert({join})"),
        "err" => format!("langGoErr({join})"),
        "bytes" => format!("langGoBytes({join})"),
        "char" => format!("langGoChar({join})"),
        "jsonParse" => format!("langGoJSONParse({join})"),
        "jsonStringify" => format!("langGoJSONStringify({join})"),
        "isNone" => format!("langGoIsNone({join})"),
        other => return Err(GenError(format!("未知内建 {other}"))),
    };
    Ok(call)
}
