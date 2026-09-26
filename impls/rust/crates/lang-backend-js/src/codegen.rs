//! Core IR → JS ESM 生成器（`.mjs` 形态、prelude 内联、无外部 import）。
//!
//! 发射架构（与 →Go 同一动态 `any` 值域，控制流承接方式不同）：
//! - **函数**：每 IR 函数发射 `function lf{n}_{名}(...as) {...}`；捕获/参数槽 =
//!   `as[i]`，普通局部 = 函数顶部 `var l{i} = undefined;`（JS var 函数作用域，
//!   无 Go 的声明跨跳限制）。
//! - **基本块 → 状态机**：JS 无 goto，CFG 发射为 `let __pc` + `while(true)
//!   switch(__pc)`：每块 = 一个 `case k: { ... }`，终接改写 `__pc` 后 `break`
//!   （break 绑定 switch，即块间跳转）；`return`/`throw` 直接出函数。
//! - **操作数栈**：符号串栈，同 →Go（无 Dup 指令可内联；Try/IterNext 物化 `const`）。
//! - **同步确定子集**：spawn = 直接调用（实参逐个 `langGoCopy` 快照，TASK-SPAWN-002），
//!   chan = 数组队列，无 async/await（CHAN-MAP-005）。
//! - **确定性**：同 [`IrProgram`] 两次生成逐字节相等（测试锁定）；生成物不承诺
//!   跨版本逐字节稳定（specs/versioning.md）。

use std::fmt::Display;

use lang_core::ids::LocalId;
use lang_ir::{Binop, BlockId, Inst, IrProgram, Terminator, Unop};

use crate::prelude::JS_PRELUDE;

/// 生成失败（内部错误：未知内建名等；非用户诊断）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GenError(pub String);

impl Display for GenError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "JS 生成失败: {}", self.0)
    }
}

impl std::error::Error for GenError {}

/// Core IR → 自包含可执行 ES Module（`node <file>.mjs` 直接可用）。
pub fn generate(prog: &IrProgram) -> Result<String, GenError> {
    JsGen::new(prog).emit_program()
}

/// 标识符消毒（同 →Go 规则）。
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

fn fn_name(idx: usize, name: &str) -> String {
    format!("lf{idx}_{}", sanitize_ident(name))
}

fn global_name(idx: usize, name: &str) -> String {
    format!("g{idx}_{}", sanitize_ident(name))
}

/// JS 源码字符串字面量（JSON 转义面，无 HTML 转义——源码字面量解析后内容不变）。
fn js_str_lit(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\u{08}' => out.push_str("\\b"),
            '\u{0c}' => out.push_str("\\f"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            _ if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            _ => out.push(c),
        }
    }
    out.push('"');
    out
}

/// float 常量的 JS 字面量（Rust `{:e}` 最短科学计数恒为合法 JS 数值字面量）。
fn js_float_lit(f: f64) -> String {
    if f.is_nan() {
        return "NaN".into();
    }
    if f.is_infinite() {
        return if f > 0.0 { "Infinity" } else { "-Infinity" }.into();
    }
    if f == 0.0 {
        return if f.is_sign_negative() { "-0" } else { "0" }.into();
    }
    format!("{f:e}")
}

struct JsGen<'a> {
    prog: &'a IrProgram,
    out: String,
    indent: usize,
    tmp: usize,
    stack: Vec<String>,
    fixed_slots: usize,
    /// 当前块内作为写链地址组件的指令下标（发射 Ref 读形）。
    ref_reads: std::collections::BTreeSet<usize>,
}

impl<'a> JsGen<'a> {
    fn new(prog: &'a IrProgram) -> Self {
        Self {
            prog,
            out: String::new(),
            indent: 0,
            tmp: 0,
            stack: Vec::new(),
            fixed_slots: 0,
            ref_reads: std::collections::BTreeSet::new(),
        }
    }

    fn w(&mut self, line: &str) {
        for _ in 0..self.indent {
            self.out.push_str("  ");
        }
        self.out.push_str(line);
        self.out.push('\n');
    }

    fn new_tmp(&mut self) -> String {
        self.tmp += 1;
        format!("_t{}", self.tmp)
    }

    fn push(&mut self, expr: String) {
        self.stack.push(expr);
    }

    fn pop(&mut self) -> String {
        self.stack
            .pop()
            .unwrap_or_else(|| "/* 栈下溢 */ undefined".to_string())
    }

    fn pop_n(&mut self, n: usize) -> Vec<String> {
        let mut out = vec![String::new(); n];
        for slot in out.iter_mut().rev() {
            *slot = self.pop();
        }
        out
    }

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
        self.out.push_str(JS_PRELUDE.trim_end_matches('\n'));
        self.out.push('\n');

        for (i, g) in self.prog.globals.iter().enumerate() {
            self.w(&format!("var {} = undefined;", global_name(i, &g.name)));
        }
        if !self.prog.globals.is_empty() {
            self.w("");
        }
        for (i, f) in self.prog.functions.iter().enumerate() {
            self.emit_function(i, f)?;
        }
        self.w("// —— 程序入口 ——");
        if let Some(init) = self.prog.init {
            self.w(&format!(
                "{}();",
                fn_name(init.0 as usize, &self.prog.functions[init.0 as usize].name)
            ));
        }
        if let Some(main) = self.prog.main {
            self.w(&format!(
                "{}();",
                fn_name(main.0 as usize, &self.prog.functions[main.0 as usize].name)
            ));
        }
        Ok(self.out)
    }

    fn emit_function(&mut self, idx: usize, f: &lang_ir::IrFunction) -> Result<(), GenError> {
        self.tmp = 0;
        self.fixed_slots = f.fixed_slots();
        let name = fn_name(idx, &f.name);
        if f.extern_host.is_some() {
            self.w(&format!(
                "// extern 宿主函数 {}：独立产物未链接宿主实现",
                js_str_lit(&f.name)
            ));
            self.w(&format!("function {name}(...as) {{"));
            self.indent += 1;
            let host = f
                .extern_host
                .as_deref()
                .unwrap_or("?")
                .replace('\\', "\\\\")
                .replace('"', "\\\"");
            self.w(&format!(
                "throw new Error(\"extern 宿主函数未链接: {host}\");"
            ));
            self.indent -= 1;
            self.w("}");
            self.w("");
            return Ok(());
        }
        self.w(&format!("function {name}(...as) {{"));
        self.indent += 1;
        for i in self.fixed_slots..f.locals.len() {
            self.w(&format!("var l{i} = undefined;"));
        }
        self.w("let __pc = 0;");
        self.w("while (true) {");
        self.indent += 1;
        self.w("switch (__pc) {");
        self.indent += 1;
        for bi in 0..f.blocks.len() {
            let block = &f.blocks[bi];
            self.w(&format!("case {bi}: {{"));
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
        self.indent -= 1;
        self.w("}");
        self.indent -= 1;
        self.w("}");
        self.w("");
        Ok(())
    }

    // ———— 指令 ————

    fn emit_inst(&mut self, pos: usize, inst: &Inst) -> Result<(), GenError> {
        match inst {
            Inst::ConstI64(v) => {
                if *v < 0 {
                    self.push(format!("({v}n)"));
                } else {
                    self.push(format!("{v}n"));
                }
            }
            Inst::ConstF64(x) => self.push(js_float_lit(*x)),
            Inst::ConstBool(b) => self.push(if *b { "true" } else { "false" }.into()),
            Inst::ConstStr(s) => self.push(js_str_lit(s)),

            Inst::LocalGet(id) => {
                let r = self.slot_ref(*id);
                self.push(r);
            }
            Inst::LocalSet(id) => {
                let v = self.pop();
                let r = self.slot_ref(*id);
                self.w(&format!("{r} = langGoCopy({v});"));
            }
            Inst::GlobalGet(g) => {
                let decl = &self.prog.globals[g.0 as usize];
                self.push(global_name(g.0 as usize, &decl.name));
            }
            Inst::GlobalSet(g) => {
                let v = self.pop();
                let decl = &self.prog.globals[g.0 as usize];
                let name = global_name(g.0 as usize, &decl.name);
                self.w(&format!("{name} = langGoCopy({v});"));
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
                self.w(&format!("{v};"));
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
                    self.w(&format!("{call};"));
                }
            }
            Inst::CallIndirect { argc, has_ret } => {
                let args = self.pop_n(*argc as usize);
                let callee = self.pop();
                let call = format!("langGoCallClosure({callee}{})", join_args(&args));
                if *has_ret {
                    self.push(call);
                } else {
                    self.w(&format!("{call};"));
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
                    self.w(&format!("{call};"));
                }
            }
            Inst::Spawn { func, argc } => {
                // 同步确定子集：spawn 等价直接调用，实参快照逐个拷贝（TASK-SPAWN-002）
                let args = self.pop_n(*argc as usize);
                let name = fn_name(func.0 as usize, &self.prog.functions[func.0 as usize].name);
                let copied: Vec<String> = args.iter().map(|a| format!("langGoCopy({a})")).collect();
                self.w(&format!("{name}({});", copied.join(", ")));
            }
            Inst::ClosureNew { func, captures } => {
                let caps = self.pop_n(*captures as usize);
                let env = format!("[{}]", caps.join(", "));
                let tf = &self.prog.functions[func.0 as usize];
                self.push(format!(
                    "{{ __langGoClosure: true, fn: {name}, env: {env}, name: {n} }}",
                    name = fn_name(func.0 as usize, &tf.name),
                    n = js_str_lit(&tf.name)
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
                self.w(&format!("langGoSetIndex({obj}, {idx}, {val});"));
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
                    self.push(format!("langGoFieldRef({obj}, {})", js_str_lit(field)));
                } else {
                    self.push(format!("langGoField({obj}, {})", js_str_lit(field)));
                }
            }
            Inst::FieldSet { field } => {
                let val = self.pop();
                let obj = self.pop();
                self.w(&format!(
                    "langGoSetField({obj}, {}, {val});",
                    js_str_lit(field)
                ));
            }
            Inst::ArrayPush => {
                // 数组 `+=`：rhs 为数组 → 原位拼接（IrMachine ArrayPush 同语义；
                // 单元素追加是 push 内建，走 CallBuiltin langGoPush）
                let val = self.pop();
                let arr = self.pop();
                self.w(&format!("langGoPushArr({arr}, {val});"));
            }

            Inst::ArrayNew { count } => {
                let items = self.pop_n(*count as usize);
                self.push(format!("[{}]", items.join(", ")));
            }
            Inst::MapNew { count } => {
                let flat = self.pop_n(*count as usize * 2);
                let kvs: Vec<String> = flat
                    .chunks(2)
                    .map(|p| format!("{{ K: {}, V: {} }}", p[0], p[1]))
                    .collect();
                self.push(format!("langGoNewMap({})", kvs.join(", ")));
            }
            Inst::StructNew { struct_id } => {
                let decl = &self.prog.structs[struct_id.0 as usize];
                let fields = self.pop_n(decl.fields.len());
                let mut parts = vec![format!("__langGoStruct: {}", js_str_lit(&decl.name))];
                for (f, v) in decl.fields.iter().zip(fields) {
                    parts.push(format!("{}: langGoCopy({v})", js_str_lit(&f.name)));
                }
                self.push(format!("{{ {} }}", parts.join(", ")));
            }
            Inst::EnumNew { enum_id, case_id } => {
                let decl = &self.prog.enums[enum_id.0 as usize];
                let case = &decl.cases[*case_id as usize];
                let payloads = self.pop_n(case.payload_types.len());
                self.push(format!(
                    "langGoEnumVal({}, {}, {})",
                    js_str_lit(&decl.name),
                    js_str_lit(&case.name),
                    payloads.join(", ")
                ));
            }
            Inst::RangeNew => {
                let hi = self.pop();
                let lo = self.pop();
                self.push(format!("{{ __langGoRange: true, Lo: {lo}, Hi: {hi} }}"));
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
                self.w(&format!("langGoRefSet({r}, {val});"));
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
                let it = self.new_tmp();
                self.w(&format!("const {it} = {v};"));
                self.w(&format!(
                    "if ({it}.I >= {it}.Items.length) {{ __pc = {}; break; }}",
                    done.0
                ));
                let e = self.new_tmp();
                self.w(&format!("const {e} = langGoCopy({it}.Items[{it}.I]);"));
                self.w(&format!("{it}.I++;"));
                self.push(e);
            }

            Inst::ChanNew => self.push("langGoNewChan()".into()),
            Inst::ChanSend => {
                let val = self.pop();
                let ch = self.pop();
                self.w(&format!("langGoSend({ch}, {val});"));
            }
            Inst::ChanRecv => {
                let ch = self.pop();
                self.push(format!("langGoRecv({ch})"));
            }

            Inst::Try { err, fail } => {
                let v = self.pop();
                let t = self.new_tmp();
                self.w(&format!("const {t} = {v};"));
                self.w(&format!(
                    "if (langGoIsErr({t})) {{ {} = langGoTryErrOf({t}); __pc = {}; break; }}",
                    self.slot_ref(*err),
                    fail.0
                ));
                self.push(format!("langGoTryOk({t})"));
            }
        }
        Ok(())
    }

    // ———— 终接 ————

    fn emit_terminator(&mut self, term: &Terminator) -> Result<(), GenError> {
        match term {
            Terminator::Goto { target } => {
                self.w(&format!("__pc = {}; break;", target.0));
            }
            Terminator::Branch { if_true, if_false } => {
                let c = self.pop();
                if if_true == if_false {
                    self.w(&format!("__pc = {}; break;", if_true.0));
                } else {
                    self.w(&format!(
                        "if (langGoTruthy({c})) {{ __pc = {}; break; }}",
                        if_true.0
                    ));
                    self.w(&format!("__pc = {}; break;", if_false.0));
                }
            }
            Terminator::SwitchInt { arms, default } => {
                let v = self.pop();
                let t = self.new_tmp();
                self.w(&format!("const {t} = langGoInt({v});"));
                for (val, target) in arms {
                    self.w(&format!(
                        "if ({t} === {val}n) {{ __pc = {}; break; }}",
                        target.0
                    ));
                }
                self.emit_switch_tail(*default)?;
            }
            Terminator::SwitchEnum {
                enum_id,
                arms,
                default,
            } => {
                let v = self.pop();
                let decl = &self.prog.enums[enum_id.0 as usize];
                let t = self.new_tmp();
                self.w(&format!("const {t} = langGoEnumTag({v});"));
                for (case, target) in decl.cases.iter().zip(arms) {
                    self.w(&format!(
                        "if ({t} === {}) {{ __pc = {}; break; }}",
                        js_str_lit(&case.name),
                        target.0
                    ));
                }
                self.emit_switch_tail(*default)?;
            }
            Terminator::Return { has_value } => {
                if *has_value {
                    let v = self.pop();
                    self.w(&format!("return langGoCopy({v});"));
                } else {
                    self.w("return;");
                }
            }
            Terminator::Unreachable => {
                self.w("throw new Error(\"不可达\");");
            }
        }
        Ok(())
    }

    fn emit_switch_tail(&mut self, default: Option<BlockId>) -> Result<(), GenError> {
        match default {
            Some(d) => self.w(&format!("__pc = {}; break;", d.0)),
            None => self.w("throw new Error(\"match 无匹配分支\");"),
        }
        Ok(())
    }
}

fn join_args(args: &[String]) -> String {
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
