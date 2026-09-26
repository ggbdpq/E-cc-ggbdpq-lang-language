//! IR 确定性文本 dump（调试视图）。
//!
//! **这是调试视图，不是跨版本契约**：格式随 IR 演进可调整，不承诺
//! 不同工具版本间逐字节稳定；对外稳定文本面以 conformance/CLI 契约为准。
//! 在同一工具版本内确定性成立：同一 [`IrProgram`] 两次 dump 逐字节相等
//! （表序遍历、无哈希迭代序、浮点 `{:?}` 最短往返、字符串 `escape_debug`）。
//!
//! 本视图**不含 span**（specs/source-span.md §2：IR 为精简执行视图，
//! 定位回 HIR 取），也不打印 `TypeId`（语义表下标对调试无用且引入跨表依赖）。
//!
//! 格式速览（每行一个元素；块行缩进 2、指令行缩进 4、声明子行缩进 2）：
//!
//! ```text
//! entry init=<funcId|->
//! entry main=<funcId|->
//! struct <i> "<name>" <类型参数> fields=<n>
//!   field "<name>"
//! enum <i> "<name>" <类型参数> cases=<n>
//!   case <ci> "<name>" payloads=<n>
//! global <i> "<name>" <mut|imm>
//! func <i> "<name>" captures=<c> params=<p> ret=<0|1>
//! func <i> "<name>" extern="<host>"          // extern 函数无块
//!   block <b>:
//!     <指令/终接助记符>
//! ```

use std::fmt::Write;

use crate::model::{Binop, Inst, IrFunction, IrProgram, Terminator, Unop};

/// dump 整个程序（确定性文本；格式见模块文档）。
pub fn dump_program(prog: &IrProgram) -> String {
    let mut out = String::new();
    entry_line(&mut out, "init", prog.init);
    entry_line(&mut out, "main", prog.main);
    for (i, s) in prog.structs.iter().enumerate() {
        let _ = writeln!(
            out,
            "struct {i} \"{}\"{} fields={}",
            s.name,
            type_params(s.type_params.as_slice()),
            s.fields.len()
        );
        for f in &s.fields {
            let _ = writeln!(out, "  field \"{}\"", f.name);
        }
    }
    for (i, e) in prog.enums.iter().enumerate() {
        let _ = writeln!(
            out,
            "enum {i} \"{}\"{} cases={}",
            e.name,
            type_params(e.type_params.as_slice()),
            e.cases.len()
        );
        for (ci, c) in e.cases.iter().enumerate() {
            let _ = writeln!(
                out,
                "  case {ci} \"{}\" payloads={}",
                c.name,
                c.payload_types.len()
            );
        }
    }
    for (i, g) in prog.globals.iter().enumerate() {
        let _ = writeln!(
            out,
            "global {i} \"{}\" {}",
            g.name,
            if g.mutable { "mut" } else { "imm" }
        );
    }
    for (i, f) in prog.functions.iter().enumerate() {
        dump_function_into(&mut out, i, f);
    }
    out
}

/// dump 单个函数（不含程序头；`index` 写进 `func <i>` 行）。
pub fn dump_function(func: &IrFunction, index: lang_core::ids::FunctionId) -> String {
    let mut out = String::new();
    dump_function_into(&mut out, usize::from(index), func);
    out
}

fn entry_line(out: &mut String, label: &str, fid: Option<lang_core::ids::FunctionId>) {
    let _ = match fid {
        Some(f) => writeln!(out, "entry {label}={f}"),
        None => writeln!(out, "entry {label}=-"),
    };
}

fn type_params(params: &[String]) -> String {
    if params.is_empty() {
        return String::new();
    }
    // 类型参数是源码标识符（无引号/控制字符），原样拼接。
    format!(" <{}>", params.join(", "))
}

fn dump_function_into(out: &mut String, index: usize, func: &IrFunction) {
    match &func.extern_host {
        Some(host) => {
            let _ = writeln!(out, "func {index} \"{}\" extern=\"{}\"", func.name, host);
            return;
        }
        None => {
            let _ = writeln!(
                out,
                "func {index} \"{}\" captures={} params={} ret={}",
                func.name,
                func.capture_count,
                func.sig.param_count,
                u8::from(func.sig.has_ret)
            );
        }
    }
    for (bi, block) in func.blocks.iter().enumerate() {
        let _ = writeln!(out, "  block {bi}:");
        for inst in &block.insts {
            let _ = writeln!(out, "    {}", dump_inst(inst));
        }
        let _ = writeln!(out, "    {}", dump_term(&block.term));
    }
}

/// 字符串字面量转义（`escape_debug`：转义引号/反斜杠/控制字符，确定性）。
fn escape(s: &str) -> String {
    format!("\"{}\"", s.escape_debug())
}

fn dump_inst(inst: &Inst) -> String {
    match inst {
        Inst::ConstI64(v) => format!("const.i64 {v}"),
        Inst::ConstF64(v) => format!("const.f64 {v:?}"),
        Inst::ConstBool(v) => format!("const.bool {v}"),
        Inst::ConstStr(s) => format!("const.str {}", escape(s)),
        Inst::LocalGet(id) => format!("local.get {id}"),
        Inst::LocalSet(id) => format!("local.set {id}"),
        Inst::GlobalGet(id) => format!("global.get {id}"),
        Inst::GlobalSet(id) => format!("global.set {id}"),
        Inst::Unary(op) => format!(
            "unary.{}",
            match op {
                Unop::Neg => "neg",
                Unop::Not => "not",
            }
        ),
        Inst::Binary(op) => format!("binary.{}", binop_name(*op)),
        Inst::Pop => "pop".into(),
        Inst::Call(f) => format!("call {f}"),
        Inst::CallIndirect { argc, has_ret } => {
            format!("call.indirect argc={argc} ret={}", u8::from(*has_ret))
        }
        Inst::CallBuiltin {
            name,
            argc,
            has_ret,
        } => format!(
            "call.builtin {} argc={argc} ret={}",
            escape(name),
            u8::from(*has_ret)
        ),
        Inst::Spawn { func, argc } => format!("spawn {func} argc={argc}"),
        Inst::ClosureNew { func, captures } => format!("closure.new {func} captures={captures}"),
        Inst::IndexGet => "index.get".into(),
        Inst::IndexSet => "index.set".into(),
        Inst::SliceGet => "slice.get".into(),
        Inst::FieldGet { field } => format!("field.get {}", escape(field)),
        Inst::FieldSet { field } => format!("field.set {}", escape(field)),
        Inst::ArrayPush => "array.push".into(),
        Inst::ArrayNew { count } => format!("array.new {count}"),
        Inst::MapNew { count } => format!("map.new {count}"),
        Inst::StructNew { struct_id } => format!("struct.new {struct_id}"),
        Inst::EnumNew {
            enum_id,
            case_id: case,
        } => format!("enum.new {enum_id} {case}"),
        Inst::RangeNew => "range.new".into(),
        Inst::BoxNew => "box.new".into(),
        Inst::Deref => "deref".into(),
        Inst::RefSet => "ref.set".into(),
        Inst::EnumPayloadGet { index } => format!("enum.payload.get {index}"),
        Inst::IterNew => "iter.new".into(),
        Inst::IterNext { done } => format!("iter.next {done}"),
        Inst::ChanNew => "chan.new".into(),
        Inst::ChanSend => "chan.send".into(),
        Inst::ChanRecv => "chan.recv".into(),
        Inst::Try { err, fail } => format!("try err={err} fail={fail}"),
    }
}

fn dump_term(term: &Terminator) -> String {
    match term {
        Terminator::Goto { target } => format!("goto {target}"),
        Terminator::Branch { if_true, if_false } => format!("branch {if_true} {if_false}"),
        Terminator::SwitchInt { arms, default } => {
            let mut s = String::from("switch.int");
            for (v, b) in arms {
                let _ = write!(s, " {v}->{b}");
            }
            write_default(&mut s, *default);
            s
        }
        Terminator::SwitchEnum {
            enum_id,
            arms,
            default,
        } => {
            let mut s = format!("switch.enum {enum_id}");
            for (ci, b) in arms.iter().enumerate() {
                let _ = write!(s, " {ci}->{b}");
            }
            write_default(&mut s, *default);
            s
        }
        Terminator::Return { has_value } => format!("return {}", u8::from(*has_value)),
        Terminator::Unreachable => "unreachable".into(),
    }
}

fn write_default(s: &mut String, default: Option<crate::ids::BlockId>) {
    let _ = match default {
        Some(b) => write!(s, " default={b}"),
        None => write!(s, " default=-"),
    };
}

fn binop_name(op: Binop) -> &'static str {
    match op {
        Binop::Eq => "eq",
        Binop::Ne => "ne",
        Binop::Lt => "lt",
        Binop::Le => "le",
        Binop::Gt => "gt",
        Binop::Ge => "ge",
        Binop::Add => "add",
        Binop::Sub => "sub",
        Binop::Mul => "mul",
        Binop::Div => "div",
        Binop::Rem => "rem",
        Binop::BitAnd => "bit.and",
        Binop::BitOr => "bit.or",
        Binop::BitXor => "bit.xor",
        Binop::Shl => "shl",
        Binop::Shr => "shr",
    }
}
