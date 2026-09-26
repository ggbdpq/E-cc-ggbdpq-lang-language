//! lang-formatter —— langGo 规范化打印器（计划 Phase R23，对照 impls/typescript/src/format/format.ts）。
//!
//! 输入 = 已解析的 AST 语句序列 + 无损 trivia（`scan_trivia`）+ 源文本；输出 = 规范形态源码。
//! 硬契约（测试锁定）：`format(format(src)) == format(src)` 幂等、注释 100% 保留、
//! 确定性输出、语义等价（span 无关的 AST 形态不变）。不依赖 Rustfmt。
//!
//! 渲染为行缓冲；注释按 trivia 流插回：
//! - 行首注释：独立行输出，保留注释间空行分组（最多一空行）；
//! - 行尾注释：追加到语句渲染输出的最后一行（两空格间隔）；
//! - 表达式内注释：语句边界冲刷（位置后移，文本零丢失）；
//! - 块尾注释：块内最后冲刷，原位保留在 `}` 之前。
//!
//! 与 TS 证据的偏差（均在 status/A1-syntax.md 登记）：
//! 1. `flush_remaining` 批内首条注释前不补空行——证据实现跨 `}` 的 `lastSrcLine`
//!    跳变会在「空块 + 块尾注释」场景破坏幂等，此修正使空行分组决策上下文无关；
//! 2. 函数字面量签名保留 `type_params`（`fn[T](x: T)`）——证据实现丢弃它会导致
//!    语义信息丢失、重解析 AST 不等。
#![forbid(unsafe_code)]

use lang_core::{Diagnostic, SourceId, SourceText};
use lang_syntax::ast::{AssignOp, BinaryOp, Expr, Param, Pat, Stmt, TypeEx, UnaryOp};
use lang_syntax::{Trivia, TriviaKind, parse, scan_trivia};

/// 缩进单位：4 空格（对照 TS 证据 `"    ".repeat(indent)`）。
const INDENT: &str = "    ";

/// 便捷入口的合成源名（仅用于 `SourceText` 构造，不影响输出）。
const SOURCE_NAME: &str = "format.lang-go";

/// 把源码格式化为规范形态（解析 + trivia 扫描 + 规范打印）。
///
/// 解析失败以 E1001 诊断返回；formatter 自身不产生诊断。
pub fn format_source(src: &str) -> Result<String, Diagnostic> {
    let text = SourceText::new(SOURCE_NAME, src.to_string());
    let stmts = parse(&text, SourceId(0))?;
    let trivia = scan_trivia(&text, SourceId(0));
    Ok(format(&stmts, &trivia, &text))
}

/// 把已解析的语句序列 + 无损 trivia 规范化为规范形态源码文本。
///
/// `source` 是码点 → 行号折算的权威：注释贴附所需的行号一律由 span 的码点
/// offset 经 `SourceText::line_col` 派生（trivia / AST span 的 offset 均为
/// 0 基码点，见 specs/source-span.md）。末尾恒补单换行；空程序输出空串。
pub fn format(stmts: &[Stmt], trivia: &[Trivia], source: &SourceText) -> String {
    let comments: Vec<&Trivia> = trivia
        .iter()
        .filter(|t| t.kind == TriviaKind::Comment)
        .collect();
    let mut f = Formatter {
        lines: Vec::new(),
        text: source,
        comments: &comments,
        pos: 0,
        last_src_line: 0,
    };
    f.stmts(stmts, 0);
    f.out()
}

/// 格式化器状态：输出行缓冲 + 共享注释流游标。
struct Formatter<'a> {
    lines: Vec<String>,
    text: &'a SourceText,
    /// 仅 Comment 类 trivia（依出现序）。
    comments: &'a [&'a Trivia],
    /// 注释流消费位置（函数字面量体渲染时借出、归还）。
    pos: usize,
    /// 最近消费的源码行（空行分组判定；1 基，0 = 尚无）。
    last_src_line: u32,
}

impl<'a> Formatter<'a> {
    /// 码点偏移 → 1 基行号（权威折算走 `SourceText`）。
    fn line_of(&self, offset: u32) -> u32 {
        self.text.line_col(offset).0
    }

    /// 冲刷 `start` 之前的整行注释（行尾注释留在流里给 [`Self::flush_trailing`]）。
    fn flush_leading(&mut self, start: u32, indent: usize) {
        let comments = self.comments;
        while let Some(c) = comments.get(self.pos) {
            if c.span.start.offset >= start {
                return;
            }
            self.pos += 1;
            let line = self.line_of(c.span.start.offset);
            if line.saturating_sub(self.last_src_line) >= 2 {
                self.lines.push(String::new());
            }
            self.lines.push(INDENT.repeat(indent) + &c.text);
            self.last_src_line = line;
        }
    }

    /// 冲刷语句末行上的行尾注释（追加到最近输出行，两空格间隔）。
    fn flush_trailing(&mut self, end_offset: u32, end_line: u32) {
        let comments = self.comments;
        while let Some(c) = comments.get(self.pos) {
            let line = self.line_of(c.span.start.offset);
            if line != end_line || c.span.start.offset < end_offset {
                return;
            }
            self.pos += 1;
            if let Some(last) = self.lines.last_mut() {
                last.push_str("  ");
                last.push_str(&c.text);
            } else {
                self.lines.push(format!("  {}", c.text));
            }
            self.last_src_line = line;
        }
    }

    /// 冲刷全部挂起注释（块尾 / 文件尾 / 函数字面量体尾），整行输出。
    ///
    /// 幂等修正：批内第一条注释前不补空行。证据实现用 `lastSrcLine`（可能指向
    /// `}` 之后）给首条注释做空行分组，注释被收进块内后块尾行号反转，空块场景
    /// 二次格式化会移位；首条不分组使该决策上下文无关，批内相邻注释的分组保留。
    fn flush_remaining(&mut self, indent: usize) {
        let comments = self.comments;
        let mut first = true;
        while let Some(c) = comments.get(self.pos) {
            self.pos += 1;
            let line = self.line_of(c.span.start.offset);
            if !first && line.saturating_sub(self.last_src_line) >= 2 {
                self.lines.push(String::new());
            }
            first = false;
            self.lines.push(INDENT.repeat(indent) + &c.text);
            self.last_src_line = line;
        }
    }

    /// 输出一行（多行文本仅首行带缩进，其余行已内嵌绝对缩进）。
    fn w(&mut self, indent: usize, s: &str) {
        for (i, part) in s.split('\n').enumerate() {
            if i == 0 {
                self.lines.push(INDENT.repeat(indent) + part);
            } else {
                self.lines.push(part.to_string());
            }
        }
    }

    fn out(self) -> String {
        if self.lines.is_empty() {
            String::new()
        } else {
            let mut s = self.lines.join("\n");
            s.push('\n');
            s
        }
    }

    /// 语句序列 + 注释贴附（每条语句边界做前导 / 行尾冲刷，末尾冲刷余量）。
    fn stmts(&mut self, list: &[Stmt], indent: usize) {
        for s in list {
            let sp = s.span();
            self.flush_leading(sp.start.offset, indent);
            let end_line = self.line_of(sp.end.offset);
            self.last_src_line = end_line;
            self.stmt(s, indent);
            self.flush_trailing(sp.end.offset, end_line);
        }
        self.flush_remaining(indent);
    }

    fn stmt(&mut self, s: &Stmt, indent: usize) {
        match s {
            Stmt::Let {
                name,
                mutable,
                typ,
                init,
                ..
            } => {
                let kw = if *mutable { "var" } else { "let" };
                let anno = match typ {
                    Some(t) => format!(": {}", type_str(t)),
                    None => String::new(),
                };
                let init = self.expr(init, 0, indent);
                self.w(indent, &format!("{kw} {name}{anno} = {init}"));
            }

            Stmt::Assign {
                target, op, value, ..
            } => {
                let target = self.expr(target, 0, indent);
                let value = self.expr(value, 0, indent);
                self.w(indent, &format!("{target} {} {value}", assign_op_str(*op)));
            }

            Stmt::ExprStmt { expr, .. } => {
                let e = self.expr(expr, 0, indent);
                self.w(indent, &e);
            }

            Stmt::Return { value, .. } => match value {
                None => self.w(indent, "return"),
                Some(v) => {
                    let e = self.expr(v, 0, indent);
                    self.w(indent, &format!("return {e}"));
                }
            },

            Stmt::If {
                cond,
                then_body,
                else_body,
                ..
            } => self.if_chain(cond, then_body, else_body.as_ref(), indent),

            Stmt::While { cond, body, .. } => {
                let c = self.expr(cond, 0, indent);
                self.w(indent, &format!("while {c} {{"));
                self.stmts(body, indent + 1);
                self.w(indent, "}");
            }

            Stmt::ForIn {
                variable,
                iter,
                body,
                ..
            } => {
                let it = self.expr(iter, 0, indent);
                self.w(indent, &format!("for {variable} in {it} {{"));
                self.stmts(body, indent + 1);
                self.w(indent, "}");
            }

            Stmt::Break { .. } => self.w(indent, "break"),
            Stmt::Continue { .. } => self.w(indent, "continue"),

            Stmt::Go { call, .. } => {
                let c = self.expr(call, 0, indent);
                self.w(indent, &format!("go {c}"));
            }

            Stmt::FnDecl {
                name,
                recv,
                type_params,
                params,
                ret,
                body,
                ..
            } => match recv {
                Some(r) => {
                    // 接收者无冒号（Go 风格）；parser 恒填 recv.typ，缺省走证据的 "?" 占位
                    let rtyp = r
                        .typ
                        .as_ref()
                        .map(type_str)
                        .unwrap_or_else(|| "?".to_string());
                    let ps = params_sig(params);
                    let mut sig = format!("fn ({} {rtyp}).{name}({ps})", r.name);
                    if let Some(t) = ret {
                        sig.push_str(": ");
                        sig.push_str(&type_str(t));
                    }
                    self.w(indent, &format!("{sig} {{"));
                    self.stmts(body, indent + 1);
                    self.w(indent, "}");
                }
                None => {
                    let sig = fn_sig(name, type_params, params, ret.as_ref());
                    self.w(indent, &format!("{sig} {{"));
                    self.stmts(body, indent + 1);
                    self.w(indent, "}");
                }
            },

            Stmt::StructDecl {
                name,
                type_params,
                fields,
                ..
            } => {
                self.w(
                    indent,
                    &format!("struct {}{} {{", name, generic_params(type_params)),
                );
                for fd in fields {
                    // parser 恒填字段类型，缺省走证据的 "?" 占位
                    let anno = match &fd.typ {
                        Some(t) => format!(": {}", type_str(t)),
                        None => ": ?".to_string(),
                    };
                    self.w(indent + 1, &format!("{}{anno}", fd.name));
                }
                self.w(indent, "}");
            }

            Stmt::EnumDecl {
                name,
                type_params,
                cases,
                ..
            } => {
                self.w(
                    indent,
                    &format!("enum {}{} {{", name, generic_params(type_params)),
                );
                for c in cases {
                    if c.payloads.is_empty() {
                        self.w(indent + 1, &c.name);
                    } else {
                        let ps = c
                            .payloads
                            .iter()
                            .map(type_str)
                            .collect::<Vec<_>>()
                            .join(", ");
                        self.w(indent + 1, &format!("{}({ps})", c.name));
                    }
                }
                self.w(indent, "}");
            }

            Stmt::Import { path, .. } => {
                let p = go_quote(path);
                self.w(indent, &format!("import {p}"));
            }

            Stmt::Extern { host, fns, .. } => {
                let h = go_quote(host);
                self.w(indent, &format!("extern {h} {{"));
                for f in fns {
                    // 契约：fns 每项恒 Stmt::FnDecl（body 空），只打印签名
                    if let Stmt::FnDecl {
                        name,
                        type_params,
                        params,
                        ret,
                        ..
                    } = f
                    {
                        let sig = fn_sig(name, type_params, params, ret.as_ref());
                        self.w(indent + 1, &sig);
                    }
                }
                self.w(indent, "}");
            }
        }
    }

    /// if / else if / else 扁平化输出。
    fn if_chain(
        &mut self,
        cond: &Expr,
        then_body: &[Stmt],
        else_body: Option<&Vec<Stmt>>,
        indent: usize,
    ) {
        let c = self.expr(cond, 0, indent);
        self.w(indent, &format!("if {c} {{"));
        self.stmts(then_body, indent + 1);
        self.else_tail(else_body, indent);
    }

    fn else_tail(&mut self, else_body: Option<&Vec<Stmt>>, indent: usize) {
        if let Some(els) = else_body {
            // else if 保持同起始行（链式展开）
            if let [
                Stmt::If {
                    cond,
                    then_body,
                    else_body: nested,
                    ..
                },
            ] = els.as_slice()
            {
                let c = self.expr(cond, 0, indent);
                self.w(indent, &format!("}} else if {c} {{"));
                self.stmts(then_body, indent + 1);
                self.else_tail(nested.as_ref(), indent);
                return;
            }
            if !els.is_empty() {
                self.w(indent, "} else {");
                self.stmts(els, indent + 1);
            }
        }
        self.w(indent, "}");
    }

    /// 以 `min_prec` 门槛输出表达式；低于门槛的子式加括号。
    fn expr(&mut self, e: &Expr, min_prec: u8, indent: usize) -> String {
        let s = self.expr_raw(e, indent);
        if expr_prec(e) < min_prec {
            format!("({s})")
        } else {
            s
        }
    }

    fn expr_raw(&mut self, e: &Expr, indent: usize) -> String {
        match e {
            Expr::IntLit { value, .. } => value.to_string(),
            Expr::FloatLit { value, .. } => {
                let s = go_format_float(*value);
                // 保住 float 身份：整数值的浮点字面量补 ".0"（否则重解析变 int）
                if s.contains(['.', 'e', 'E']) {
                    s
                } else {
                    format!("{s}.0")
                }
            }
            Expr::StrLit { value, .. } => go_quote(value),
            Expr::BoolLit { value, .. } => {
                if *value {
                    "true".to_string()
                } else {
                    "false".to_string()
                }
            }
            Expr::Ident { name, .. } => name.clone(),
            Expr::Unary { op, operand, .. } => {
                let x = self.expr(operand, 8, indent);
                format!("{}{x}", unary_op_str(*op))
            }
            Expr::Binary { op, lhs, rhs, .. } => {
                let p = binary_prec(*op);
                let l = self.expr(lhs, p, indent);
                let r = self.expr(rhs, p + 1, indent);
                format!("{l} {} {r}", binary_op_str(*op))
            }
            Expr::Range { lo, hi, .. } => {
                let l = self.expr(lo, 5, indent);
                let r = self.expr(hi, 6, indent);
                format!("{l}..{r}")
            }
            Expr::Call { callee, args, .. } => {
                let c = self.expr(callee, 9, indent);
                let as_str = args
                    .iter()
                    .map(|a| self.expr(a, 0, indent))
                    .collect::<Vec<_>>()
                    .join(", ");
                format!("{c}({as_str})")
            }
            Expr::Index { obj, idx, .. } => {
                let o = self.expr(obj, 9, indent);
                let i = self.expr(idx, 0, indent);
                format!("{o}[{i}]")
            }
            Expr::Slice { obj, lo, hi, .. } => {
                let o = self.expr(obj, 9, indent);
                let l = self.expr(lo, 0, indent);
                let h = self.expr(hi, 0, indent);
                format!("{o}[{l}:{h}]")
            }
            Expr::Field { obj, name, .. } => {
                let o = self.expr(obj, 9, indent);
                format!("{o}.{name}")
            }
            Expr::Try { operand, .. } => {
                let x = self.expr(operand, 9, indent);
                format!("{x}?")
            }
            Expr::StructLit {
                type_name, fields, ..
            } => {
                let fs = fields
                    .iter()
                    .map(|fl| {
                        let v = self.expr(&fl.value, 0, indent);
                        format!("{}: {v}", fl.name)
                    })
                    .collect::<Vec<_>>()
                    .join(", ");
                format!("{type_name}{{{fs}}}")
            }
            Expr::MapLit { items, .. } => {
                let kvs = items
                    .iter()
                    .map(|it| {
                        let k = self.expr(&it.key, 0, indent);
                        let v = self.expr(&it.value, 0, indent);
                        format!("{k}: {v}")
                    })
                    .collect::<Vec<_>>()
                    .join(", ");
                format!("{{{kvs}}}")
            }
            Expr::ArrLit { items, .. } => {
                let is_str = items
                    .iter()
                    .map(|el| self.expr(el, 0, indent))
                    .collect::<Vec<_>>()
                    .join(", ");
                format!("[{is_str}]")
            }
            Expr::Match {
                scrutinee, arms, ..
            } => {
                let mut b = format!("match {} {{\n", self.expr(scrutinee, 0, indent));
                for arm in arms {
                    b.push_str(&INDENT.repeat(indent + 1));
                    b.push_str(&self.pat(&arm.pat));
                    b.push_str(" => ");
                    b.push_str(&self.expr(&arm.body, 0, indent + 1));
                    b.push('\n');
                }
                b.push_str(&INDENT.repeat(indent));
                b.push('}');
                b
            }
            Expr::FnLit {
                type_params,
                params,
                ret,
                body,
                ..
            } => {
                let sig = fn_lit_sig(type_params, params, ret.as_ref());
                let mut inner = Formatter {
                    lines: Vec::new(),
                    text: self.text,
                    comments: self.comments,
                    pos: self.pos,
                    last_src_line: 0,
                };
                let head = format!("{sig} {{");
                inner.w(0, &head);
                inner.stmts(body, indent + 1);
                inner.w(indent, "}");
                // 注释流游标归还外层（体尾注释已由 inner 的 flush_remaining 冲刷）
                self.pos = inner.pos;
                inner.lines.join("\n")
            }
        }
    }

    fn pat(&mut self, p: &Pat) -> String {
        match p {
            Pat::Wild { .. } => "_".to_string(),
            Pat::Bind { name, .. } => name.clone(),
            Pat::Lit { value, .. } => self.expr(value, 0, 0),
            Pat::Case { name, subs, .. } => {
                if subs.is_empty() {
                    name.clone()
                } else {
                    let ss = subs
                        .iter()
                        .map(|s| self.pat(s))
                        .collect::<Vec<_>>()
                        .join(", ");
                    format!("{name}({ss})")
                }
            }
        }
    }
}

// —— 语法面 → 文本（纯函数） ——

fn generic_params(tps: &[String]) -> String {
    if tps.is_empty() {
        String::new()
    } else {
        format!("[{}]", tps.join(", "))
    }
}

fn params_sig(params: &[Param]) -> String {
    params
        .iter()
        .map(|p| match &p.typ {
            Some(t) => format!("{}: {}", p.name, type_str(t)),
            None => p.name.clone(),
        })
        .collect::<Vec<_>>()
        .join(", ")
}

/// 函数 / extern 签名（`fn name[tps](params): ret`）。
fn fn_sig(name: &str, type_params: &[String], params: &[Param], ret: Option<&TypeEx>) -> String {
    let mut sig = format!(
        "fn {name}{}({})",
        generic_params(type_params),
        params_sig(params)
    );
    append_ret(&mut sig, ret);
    sig
}

/// 函数字面量签名：无名字、`fn` 后无空格（`fn[tps](params): ret`）。
fn fn_lit_sig(type_params: &[String], params: &[Param], ret: Option<&TypeEx>) -> String {
    let mut sig = format!("fn{}({})", generic_params(type_params), params_sig(params));
    append_ret(&mut sig, ret);
    sig
}

fn append_ret(sig: &mut String, ret: Option<&TypeEx>) {
    if let Some(t) = ret {
        sig.push_str(": ");
        sig.push_str(&type_str(t));
    }
}

fn type_str(t: &TypeEx) -> String {
    match t {
        TypeEx::Named { name, args, .. } => {
            if args.is_empty() {
                name.clone()
            } else {
                let as_str = args.iter().map(type_str).collect::<Vec<_>>().join(", ");
                format!("{name}[{as_str}]")
            }
        }
        TypeEx::Arr { elem, .. } => format!("[{}]", type_str(elem)),
        TypeEx::Map { key, val, .. } => format!("map[{}]{}", type_str(key), type_str(val)),
        TypeEx::Ptr { elem, .. } => format!("&{}", type_str(elem)),
        TypeEx::Fn { params, ret, .. } => {
            let ps = params.iter().map(type_str).collect::<Vec<_>>().join(", ");
            let mut sig = format!("fn({ps})");
            if let Some(r) = ret {
                sig.push_str(": ");
                sig.push_str(&type_str(r));
            }
            sig
        }
    }
}

fn unary_op_str(op: UnaryOp) -> &'static str {
    match op {
        UnaryOp::Neg => "-",
        UnaryOp::Not => "!",
        UnaryOp::Ref => "&",
    }
}

fn binary_op_str(op: BinaryOp) -> &'static str {
    match op {
        BinaryOp::Eq => "==",
        BinaryOp::Ne => "!=",
        BinaryOp::Lt => "<",
        BinaryOp::Le => "<=",
        BinaryOp::Gt => ">",
        BinaryOp::Ge => ">=",
        BinaryOp::Add => "+",
        BinaryOp::Sub => "-",
        BinaryOp::Mul => "*",
        BinaryOp::Div => "/",
        BinaryOp::Rem => "%",
        BinaryOp::BitAnd => "&",
        BinaryOp::BitOr => "|",
        BinaryOp::BitXor => "^",
        BinaryOp::Shl => "<<",
        BinaryOp::Shr => ">>",
        BinaryOp::AndAnd => "&&",
        BinaryOp::OrOr => "||",
    }
}

fn assign_op_str(op: AssignOp) -> &'static str {
    match op {
        AssignOp::Assign => "=",
        AssignOp::AddAssign => "+=",
        AssignOp::SubAssign => "-=",
        AssignOp::MulAssign => "*=",
        AssignOp::DivAssign => "/=",
    }
}

/// 二元运算符优先级（PARSE-004 同表；1 低 → 7 高）。
fn binary_prec(op: BinaryOp) -> u8 {
    match op {
        BinaryOp::OrOr => 1,
        BinaryOp::AndAnd => 2,
        BinaryOp::Eq | BinaryOp::Ne => 3,
        BinaryOp::Lt | BinaryOp::Le | BinaryOp::Gt | BinaryOp::Ge => 4,
        BinaryOp::Add | BinaryOp::Sub | BinaryOp::BitOr | BinaryOp::BitXor => 6,
        BinaryOp::Mul
        | BinaryOp::Div
        | BinaryOp::Rem
        | BinaryOp::BitAnd
        | BinaryOp::Shl
        | BinaryOp::Shr => 7,
    }
}

/// 节点优先级：二元取运算符级，一元 8，其余（字面量 / 后缀链 / 复合字面量）9。
fn expr_prec(e: &Expr) -> u8 {
    match e {
        Expr::Binary { op, .. } => binary_prec(*op),
        Expr::Unary { .. } => 8,
        _ => 9,
    }
}

/// langGo 转义规则输出字符串 / import 路径（仅 `\n \t \r \" \\` 五种转义，
/// 其余码点原样保留；TS quoteStr 与 goQuoteStr 行为一致，合一实现）。
fn go_quote(s: &str) -> String {
    let mut b = String::with_capacity(s.len() + 2);
    b.push('"');
    for r in s.chars() {
        match r {
            '\n' => b.push_str("\\n"),
            '\t' => b.push_str("\\t"),
            '\r' => b.push_str("\\r"),
            '"' => b.push_str("\\\""),
            '\\' => b.push_str("\\\\"),
            _ => b.push(r),
        }
    }
    b.push('"');
    b
}

/// Go `strconv.FormatFloat(f, 'g', -1, 64)` 等价（最短往返数字，科学计数门槛
/// eprec=6；指数至少两位、带符号）。NaN / ±Inf / ±0 与 Go 同文。
fn go_format_float(f: f64) -> String {
    if f.is_nan() {
        return "NaN".to_string();
    }
    if f == f64::INFINITY {
        return "+Inf".to_string();
    }
    if f == f64::NEG_INFINITY {
        return "-Inf".to_string();
    }
    if f == 0.0 {
        return if f.is_sign_negative() {
            "-0".to_string()
        } else {
            "0".to_string()
        };
    }
    let neg = f < 0.0;
    // Rust LowerExp 对非零有限 f64 恒输出最短往返数字（d.ddde<exp>）
    let sci = format!("{:e}", f.abs());
    let Some((mant, exp_s)) = sci.split_once('e') else {
        // 不可达：LowerExp 恒带 'e'；为全性保留原形
        return if neg { format!("-{sci}") } else { sci };
    };
    let Ok(exp) = exp_s.parse::<i32>() else {
        return if neg { format!("-{sci}") } else { sci };
    };
    let digits: String = mant.chars().filter(|c| *c != '.').collect();
    let digits = digits.trim_end_matches('0');
    let digits = if digits.is_empty() { "0" } else { digits };
    let len = digits.len() as i32;
    let dp = exp + 1; // 小数点位置（相对数字串起点）
    // 科学计数门槛 eprec=6：exp < -4 或 exp >= 6（对照 Go strconv 'g' shortest）
    let body = if !(-4..6).contains(&exp) {
        // 科学计数：d[.ddd]e±dd
        let head: String = digits.chars().take(1).collect();
        let tail: String = digits.chars().skip(1).collect();
        let frac = if tail.is_empty() {
            String::new()
        } else {
            format!(".{tail}")
        };
        let sign = if exp < 0 { '-' } else { '+' };
        format!("{head}{frac}e{sign}{:02}", exp.abs())
    } else if dp <= 0 {
        format!("0.{}{digits}", "0".repeat((-dp) as usize))
    } else if dp >= len {
        format!("{digits}{}", "0".repeat((dp - len) as usize))
    } else {
        let head: String = digits.chars().take(dp as usize).collect();
        let tail: String = digits.chars().skip(dp as usize).collect();
        format!("{head}.{tail}")
    };
    if neg { format!("-{body}") } else { body }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fmt(src: &str) -> String {
        format_source(src).unwrap_or_default()
    }

    #[test]
    fn indent_normalization_matches_go_evidence() {
        // 期望取自 impls/go/internal/format/format_test.go Test缩进规范化
        let out = fmt("fn main() {\nprintln(1)\nif true {\nprintln(2)\n}\n}\n");
        assert_eq!(
            out,
            "fn main() {\n    println(1)\n    if true {\n        println(2)\n    }\n}\n"
        );
    }

    #[test]
    fn empty_input_is_empty_output() {
        assert_eq!(fmt(""), "");
        assert_eq!(fmt("\n\n  \t \n"), "");
    }

    #[test]
    fn trailing_single_newline_always() {
        assert_eq!(fmt("let x = 1"), "let x = 1\n");
        assert_eq!(fmt("let x = 1\n\n\n"), "let x = 1\n");
    }

    #[test]
    fn else_if_chain_stays_flattened() {
        let src = "if a > 0 {\n    println(1)\n} else if a < 0 {\n    println(2)\n} else {\n    println(3)\n}\n";
        assert_eq!(fmt(src), src);
    }

    #[test]
    fn bare_return_and_while_for_break_continue_go() {
        let src = "\
fn f(n: int): int {
    while n > 0 {
        n -= 1
        if n == 3 {
            continue
        }
        if n == 1 {
            break
        }
    }
    for x in [1, 2] {
        println(x)
    }
    go worker(1)
    return
}
";
        assert_eq!(fmt(src), src);
    }

    #[test]
    fn method_and_generic_fn_and_struct_enum() {
        let src = "\
fn (r Rect).area(): int {
    return r.w * r.h
}
fn id[T](x: T): T {
    return x
}
struct Point[T] {
    x: T
    y: T
}
enum Shape {
    Circle(int)
    Rect(int, int)
    Unit
}
";
        assert_eq!(fmt(src), src);
    }

    #[test]
    fn import_and_extern_kept() {
        // 期望取自 impls/go/internal/format/format_test.go TestImport与Extern保留
        let src = "import \"./lib\"\nextern \"go\" {\n    fn hostFn(x: int): int\n}\nfn main() {\n    println(1)\n}\n";
        let out = fmt(src);
        assert!(out.contains("import \"./lib\""));
        assert!(out.contains("extern \"go\" {"));
        assert!(out.contains("    fn hostFn(x: int): int"));
        assert_eq!(fmt(&out), out);
    }

    #[test]
    fn chan_annotation_passthrough() {
        // 期望取自 impls/go/internal/format/format_test.go TestChan注解输出
        let out = fmt("fn prod(ch: chan[int], n: int) {\nsend(ch, n)\n}\n");
        assert!(out.contains("fn prod(ch: chan[int], n: int)"));
        assert_eq!(fmt(&out), out);
    }

    #[test]
    fn parens_only_where_needed() {
        assert_eq!(fmt("let a = (1 + 2) * 3"), "let a = (1 + 2) * 3\n");
        assert_eq!(fmt("let a = 1 + 2 * 3"), "let a = 1 + 2 * 3\n");
        // 结合性需要的括号保留
        assert_eq!(fmt("let a = 1 - (2 - 3)"), "let a = 1 - (2 - 3)\n");
        assert_eq!(fmt("let a = 1 - 2 - 3"), "let a = 1 - 2 - 3\n");
        // 冗余括号规范化掉（解析即弃）
        assert_eq!(fmt("let a = (((1)))"), "let a = 1\n");
        // 后缀链优先级高于一元
        assert_eq!(fmt("let a = -p.x"), "let a = -p.x\n");
        assert_eq!(fmt("let a = (-p).x"), "let a = (-p).x\n");
    }

    #[test]
    fn hex_and_float_literals_normalize() {
        assert_eq!(fmt("let a = 0xFF"), "let a = 255\n");
        // 保 float 身份：整数值浮点补 .0
        assert_eq!(fmt("let a = 2.0"), "let a = 2.0\n");
        assert_eq!(fmt("let a = 2.50"), "let a = 2.5\n");
        assert_eq!(fmt("let a = 123456.0"), "let a = 123456.0\n");
        assert_eq!(fmt("let a = 0.5"), "let a = 0.5\n");
    }

    #[test]
    fn string_escapes_round_trip() {
        let src = "let s = \"a\\n\\t\\r\\\"\\\\b\"\n";
        assert_eq!(fmt(src), src);
        // 字符串内的伪注释不是注释，原样保留在字面量里
        let out = fmt("let s = \"http://x //not\"\n");
        assert_eq!(out, "let s = \"http://x //not\"\n");
    }

    #[test]
    fn match_and_fnlit_shapes() {
        let src = "\
let m = match v {
    1 => \"一\"
    _ => \"多\"
}
let f = fn(x: int): int {
    return x + 1
}
";
        assert_eq!(fmt(src), src);
    }

    #[test]
    fn fnlit_keeps_type_params() {
        // 语义保留偏差修正：证据实现丢弃 fnlit 的 type_params，这里保留
        let out = fmt("let f = fn[T](x: T): T {\n    return x\n}\n");
        assert_eq!(out, "let f = fn[T](x: T): T {\n    return x\n}\n");
    }

    #[test]
    fn struct_lit_map_lit_arr_lit_one_line() {
        assert_eq!(
            fmt("let p = Point{x: 1, y: 2}\n"),
            "let p = Point{x: 1, y: 2}\n"
        );
        assert_eq!(
            fmt("let m = {\"a\": 1, \"b\": 2}\n"),
            "let m = {\"a\": 1, \"b\": 2}\n"
        );
        assert_eq!(fmt("let a = [1, 2, 3]\n"), "let a = [1, 2, 3]\n");
        assert_eq!(fmt("let s = a[1:2].b(x)?\n"), "let s = a[1:2].b(x)?\n");
    }

    #[test]
    fn range_and_operator_spacing() {
        assert_eq!(
            fmt("for i in 1..n {\n    println(i)\n}\n"),
            "for i in 1..n {\n    println(i)\n}\n"
        );
        assert_eq!(fmt("let a = 1 << 2 + 1\n"), "let a = 1 << 2 + 1\n");
        assert_eq!(
            fmt("let a = x == 1 && y != 2\n"),
            "let a = x == 1 && y != 2\n"
        );
    }

    // —— go_format_float：Go strconv.FormatFloat(f,'g',-1,64) 等价 ——

    #[test]
    fn go_float_matches_go_g_format() {
        // 期望值逐条对照 Go：strconv.FormatFloat(f, 'g', -1, 64)
        assert_eq!(go_format_float(0.0), "0");
        assert_eq!(go_format_float(-0.0), "-0");
        assert_eq!(go_format_float(2.5), "2.5");
        assert_eq!(go_format_float(0.5), "0.5");
        assert_eq!(go_format_float(1.25), "1.25");
        assert_eq!(go_format_float(100.25), "100.25");
        assert_eq!(go_format_float(1234.5), "1234.5");
        assert_eq!(go_format_float(123456.0), "123456"); // exp 5 < 6 → 十进制
        assert_eq!(go_format_float(1234567.0), "1.234567e+06"); // exp 6 → 科学
        assert_eq!(go_format_float(0.0001), "0.0001"); // exp -4 → 十进制
        assert_eq!(go_format_float(0.00001), "1e-05"); // exp -5 → 科学
        assert_eq!(go_format_float(1e21), "1e+21");
        assert_eq!(go_format_float(-2.75), "-2.75");
        assert_eq!(go_format_float(1.005), "1.005");
    }

    // —— 注释贴附策略（行为级，与 impls/go/internal/format/format_v05_test.go 同形）——

    #[test]
    fn trailing_comment_keeps_two_space_gap_on_same_line() {
        let out = fmt("fn main() {\n    let x = 1 // 行尾注释\n    println(x)\n}\n");
        assert!(out.contains("let x = 1  // 行尾注释"), "{out}");
        assert_eq!(fmt(&out), out);
    }

    #[test]
    fn leading_and_block_tail_comments_kept_in_place() {
        let src = "// 文件头注释\nfn add(a: int, b: int): int {\n    // 求和\n    return a + b\n}\n\n// 主入口\nfn main() {\n    println(add(1, 2))\n    // 块尾注释\n}\n";
        let out = fmt(src);
        for want in ["// 文件头注释", "// 求和", "// 主入口", "// 块尾注释"] {
            assert!(out.contains(want), "注释丢失 {want}\n{out}");
        }
        assert_eq!(fmt(&out), out);
    }

    #[test]
    fn comment_groups_keep_blank_line_grouping() {
        let src = "fn main() {\n    println(1)\n}\n\n// 尾注释组 A\n\n// 尾注释组 B\n";
        let out = fmt(src);
        assert!(
            out.contains("\n    // 尾注释组 A\n\n    // 尾注释组 B\n"),
            "{out}"
        );
        assert_eq!(fmt(&out), out);
    }

    #[test]
    fn blank_line_grouping_inside_body() {
        let src =
            "fn f() {\n    let a = 1\n\n    // 分组说明\n    let b = 2\n    println(a, b)\n}\n";
        let out = fmt(src);
        assert!(
            out.contains("let a = 1\n\n    // 分组说明\n    let b = 2\n"),
            "{out}"
        );
        assert_eq!(fmt(&out), out);
    }

    #[test]
    fn pure_comment_file_and_empty_file() {
        assert_eq!(fmt("// 只有注释\n// 第二行\n"), "// 只有注释\n// 第二行\n");
        assert_eq!(fmt(""), "");
    }

    #[test]
    fn comments_after_empty_body_stay_idempotent() {
        // 幂等修正的目标场景：注释物理上位于 `}` 之后、被收进空块
        let src = "fn main() {\n}\n\n// A\n\n// B\n";
        let once = fmt(src);
        let twice = fmt(&once);
        assert_eq!(once, twice, "一次:\n{once}\n二次:\n{twice}");
    }

    #[test]
    fn crlf_source_normalizes_to_lf() {
        let out = fmt("fn main() {\r\n    let x = 1 // CRLF 注释\r\n    println(x)\r\n}\r\n");
        assert!(out.contains("let x = 1  // CRLF 注释"), "{out}");
        assert!(!out.contains('\r'));
        assert_eq!(fmt(&out), out);
    }

    #[test]
    fn expression_interior_comments_migrate_to_stmt_boundary_without_loss() {
        // match 臂注释：语句边界冲刷（位置后移，文本零丢失——证据同构）
        let src = "fn f(s: int): int {\n    return match s {\n        1 => 10 // 一\n        _ => 0 // 其他\n    }\n}\nfn main() {\n    println(f(1))\n}\n";
        let out = fmt(src);
        assert!(out.contains("// 一"), "{out}");
        assert!(out.contains("// 其他"), "{out}");
        assert_eq!(fmt(&out), out);
    }

    #[test]
    fn struct_field_comments_survive() {
        let src = "struct P {\n    x: int // 横坐标\n    y: int\n}\nfn main() {\n    println(P{x: 1, y: 2})\n}\n";
        let out = fmt(src);
        assert!(out.contains("// 横坐标"), "{out}");
        assert_eq!(fmt(&out), out);
    }

    #[test]
    fn fnlit_body_comments_survive() {
        let src = "let f = fn(x: int) {\n    // 参数说明\n    println(x)\n}\n";
        let out = fmt(src);
        assert!(out.contains("// 参数说明"), "{out}");
        assert_eq!(fmt(&out), out);
    }

    // —— API 形态：format(stmts, trivia, source) ——

    #[test]
    fn api_format_takes_stmts_trivia_source() {
        let text = SourceText::new("t.lang-go", "let x = 1 // 尾\n".to_string());
        let stmts = parse(&text, SourceId(0)).unwrap_or_default();
        let trivia = scan_trivia(&text, SourceId(0));
        let out = format(&stmts, &trivia, &text);
        assert_eq!(out, "let x = 1  // 尾\n");
    }

    #[test]
    fn determinism_same_input_same_output() {
        let src = "fn main() {\n    let x = 1 // 注\n    println(x)\n}\n";
        assert_eq!(fmt(src), fmt(src));
        let text = SourceText::new("t.lang-go", src.to_string());
        let stmts = parse(&text, SourceId(0)).unwrap_or_default();
        let trivia = lang_syntax::scan_trivia(&text, SourceId(0));
        assert_eq!(
            format(&stmts, &trivia, &text),
            format(&stmts, &trivia, &text)
        );
    }
}
