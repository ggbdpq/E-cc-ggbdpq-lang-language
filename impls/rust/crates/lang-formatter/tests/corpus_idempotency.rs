//! 语料幂等矩阵（计划 Phase R23 硬契约，只读仓库语料，期望值不来自本实现）。
//!
//! 对 `conformance/` 全部 16 个分类的 main.lang-go 与 `dogfood/langalgo/` 全部
//! .lang-go 逐个：format → reparse → format，断言
//! 1. 二次输出 == 首次输出（幂等）；
//! 2. 格式化产物可重新解析（canonical 形态合法）；
//! 3. 格式化前后 **span 无关的 AST 形态** 相等（语义保留）；
//! 4. 两次解析的 debug 形态相等（AST 等价）；
//! 5. 同输入两次 format 逐字节相等（确定性）；
//! 6. 注释多重集在格式化前后一致（100% 保留）。
//!
//! 解析失败的语料（syntax 分类里期望 E1001 的 check 类夹具）按「不可格式化」
//! 计数跳过并打印名单；其余全部必须通过。

use std::fmt::Write as _;
use std::fs;
use std::path::{Path, PathBuf};

use lang_core::{SourceId, SourceText};
use lang_formatter::{format, format_source};
use lang_syntax::ast::{EnumCase, Expr, FieldLit, MapItem, Param, Pat, Stmt, TypeEx};
use lang_syntax::{TriviaKind, parse, scan_trivia};

fn repo_root() -> PathBuf {
    // crates/lang-formatter → crates → rust → impls → <repo 根>
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../..")
}

/// 递归收集 root 下文件名恰为 `name` 的文件（按路径排序保证确定性）。
fn collect_named(root: &Path, name: &str, out: &mut Vec<PathBuf>) {
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    let mut paths: Vec<PathBuf> = entries.flatten().map(|e| e.path()).collect();
    paths.sort();
    for p in paths {
        if p.is_dir() {
            collect_named(&p, name, out);
        } else if p.file_name().is_some_and(|n| n == name) {
            out.push(p);
        }
    }
}

/// 递归收集 root 下全部 .lang-go 文件。
fn collect_lang_go(root: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    let mut paths: Vec<PathBuf> = entries.flatten().map(|e| e.path()).collect();
    paths.sort();
    for p in paths {
        if p.is_dir() {
            collect_lang_go(&p, out);
        } else if p.extension().is_some_and(|e| e == "lang-go") {
            out.push(p);
        }
    }
}

/// 检查结果：Unparseable = 源码本身解析失败（check 类夹具，跳过）；
/// Failed = 矩阵断言破坏（真实失败）。
enum Outcome {
    Unparseable(String),
    Failed(String),
}

fn comments_multiset(src: &str) -> Vec<String> {
    let text = SourceText::new("comments", src.to_string());
    let mut v: Vec<String> = scan_trivia(&text, SourceId(0))
        .into_iter()
        .filter(|t| t.kind == TriviaKind::Comment)
        .map(|t| t.text)
        .collect();
    v.sort();
    v
}

/// 单文件全链检查：format → reparse → format 的五重断言。
fn check_file(path: &Path, rel: &str) -> Result<(), Outcome> {
    let src = match fs::read_to_string(path) {
        Ok(s) => s,
        Err(e) => return Err(Outcome::Failed(format!("{rel}: 读取失败 {e}"))),
    };
    let text = SourceText::new(rel.to_string(), src.clone());
    let stmts1 = match parse(&text, SourceId(0)) {
        Ok(s) => s,
        Err(_) => {
            return Err(Outcome::Unparseable(format!(
                "{rel}: 解析失败（E1001，预期为 check 类夹具）"
            )));
        }
    };
    let trivia1 = scan_trivia(&text, SourceId(0));
    let out1 = format(&stmts1, &trivia1, &text);

    // 确定性：同输入两次 format 逐字节相等
    let out1b = format(&stmts1, &trivia1, &text);
    if out1 != out1b {
        return Err(Outcome::Failed(format!("{rel}: 确定性破坏")));
    }

    // 注释 100% 保留：多重集一致（位置允许规范移动，文本零丢失）
    if comments_multiset(&src) != comments_multiset(&out1) {
        return Err(Outcome::Failed(format!(
            "{rel}: 注释丢失或改变\n=== 一次输出 ===\n{out1}"
        )));
    }

    // 语义保留：span 无关的 AST 形态在格式化前后不变
    let shape_src = shape_stmts(&stmts1);
    let text2 = SourceText::new(rel.to_string(), out1.clone());
    let stmts2 = match parse(&text2, SourceId(0)) {
        Ok(s) => s,
        Err(d) => {
            return Err(Outcome::Failed(format!(
                "{rel}: 格式化产物不可解析: {} {}\n=== 一次输出 ===\n{out1}",
                d.code.as_str(),
                d.message
            )));
        }
    };
    let shape_out = shape_stmts(&stmts2);
    if shape_src != shape_out {
        return Err(Outcome::Failed(format!(
            "{rel}: 语义保留破坏（AST 形态漂移）\n=== 原 ===\n{shape_src}\n=== 格式化后 ===\n{shape_out}\n=== 一次输出 ===\n{out1}"
        )));
    }

    // 幂等：format(format(src)) == format(src)
    let trivia2 = scan_trivia(&text2, SourceId(0));
    let out2 = format(&stmts2, &trivia2, &text2);
    if out2 != out1 {
        return Err(Outcome::Failed(format!(
            "{rel}: 不幂等\n=== 一次 ===\n{out1}\n=== 二次 ===\n{out2}"
        )));
    }

    // AST 等价：两次解析的 debug 形态相等
    let text3 = SourceText::new(rel.to_string(), out2);
    let stmts3 = match parse(&text3, SourceId(0)) {
        Ok(s) => s,
        Err(d) => {
            return Err(Outcome::Failed(format!(
                "{rel}: 二次产物不可解析: {}",
                d.message
            )));
        }
    };
    let debug2 = format!("{stmts2:?}");
    let debug3 = format!("{stmts3:?}");
    if debug2 != debug3 {
        return Err(Outcome::Failed(format!("{rel}: AST 等价破坏")));
    }
    Ok(())
}

/// 跑完整个矩阵：失败累积汇报，跳过名单打印（带 `--nocapture` 可见）。
fn run_matrix(files: &[PathBuf], label: &str) {
    let mut failures = Vec::new();
    let mut skipped = Vec::new();
    let mut ok = 0usize;
    for f in files {
        let rel = f
            .strip_prefix(repo_root())
            .unwrap_or(f)
            .to_string_lossy()
            .to_string();
        match check_file(f, &rel) {
            Ok(()) => ok += 1,
            Err(Outcome::Unparseable(msg)) => skipped.push(msg),
            Err(Outcome::Failed(msg)) => failures.push(msg),
        }
    }
    println!(
        "{label}: 通过 {ok} / 共 {}（跳过解析失败 {} 个）",
        files.len(),
        skipped.len()
    );
    for s in &skipped {
        println!("  [skip] {s}");
    }
    assert!(
        failures.is_empty(),
        "{label} 幂等矩阵失败 {} 项:\n{}",
        failures.len(),
        failures.join("\n---\n")
    );
}

#[test]
fn conformance_16_categories_idempotency_matrix() {
    let conf = repo_root().join("conformance");
    assert!(conf.is_dir(), "conformance 不存在: {}", conf.display());
    let categories = [
        "closure",
        "collections",
        "concurrency",
        "diagnostics",
        "enum",
        "expressions",
        "generics",
        "io",
        "match",
        "module",
        "option",
        "result",
        "struct",
        "syntax",
        "types",
        "values",
    ];
    for c in categories {
        assert!(conf.join(c).is_dir(), "缺分类目录 {c}");
    }
    let mut files = Vec::new();
    collect_named(&conf, "main.lang-go", &mut files);
    assert!(
        files.len() >= 151,
        "conformance main.lang-go 应至少 151 个，实得 {}",
        files.len()
    );
    run_matrix(&files, "conformance 幂等矩阵");
}

#[test]
fn dogfood_langalgo_idempotency_matrix() {
    let root = repo_root().join("dogfood").join("langalgo");
    assert!(root.is_dir(), "dogfood/langalgo 不存在: {}", root.display());
    let mut files = Vec::new();
    collect_lang_go(&root, &mut files);
    assert!(
        files.len() >= 60,
        "dogfood/langalgo .lang-go 应至少 60 个，实得 {}",
        files.len()
    );
    run_matrix(&files, "dogfood 幂等矩阵");
}

#[test]
fn convenience_entry_matches_api_entry() {
    let dir = repo_root().join("conformance").join("values");
    assert!(dir.is_dir());
    let mut files = Vec::new();
    collect_named(&dir, "main.lang-go", &mut files);
    let first = files.first().expect("values 分类应有夹具");
    let src = fs::read_to_string(first).expect("读夹具");
    let text = SourceText::new("x.lang-go", src.clone());
    let stmts = parse(&text, SourceId(0));
    match stmts {
        Ok(stmts) => {
            let trivia = scan_trivia(&text, SourceId(0));
            assert_eq!(
                format(&stmts, &trivia, &text),
                format_source(&src).expect("应成功")
            );
        }
        Err(_) => {
            // 该夹具解析失败时便捷入口必须返回同一诊断路径（Err 即可，不比较文本）
            assert!(format_source(&src).is_err());
        }
    }
}

// —— span 无关的 AST 形态（语义保留的对照基准；忽略 span/offset，只比结构与值）——

fn shape_stmts(stmts: &[Stmt]) -> String {
    let mut s = String::new();
    for st in stmts {
        shape_stmt(st, &mut s);
        s.push(';');
    }
    s
}

fn shape_opt_type(t: Option<&TypeEx>, out: &mut String) {
    match t {
        Some(t) => shape_type(t, out),
        None => out.push('-'),
    }
}

fn shape_params(params: &[Param], out: &mut String) {
    out.push('[');
    for p in params {
        out.push('(');
        out.push_str(&p.name);
        out.push(' ');
        shape_opt_type(p.typ.as_ref(), out);
        out.push(')');
    }
    out.push(']');
}

fn shape_stmt(st: &Stmt, out: &mut String) {
    match st {
        Stmt::Let {
            name,
            mutable,
            typ,
            init,
            ..
        } => {
            let kw = if *mutable { "let-var" } else { "let" };
            let _ = write!(out, "({kw} {name} ");
            shape_opt_type(typ.as_ref(), out);
            out.push(' ');
            shape_expr(init, out);
            out.push(')');
        }
        Stmt::Assign {
            target, op, value, ..
        } => {
            let _ = write!(out, "(assign {:?} ", op);
            shape_expr(target, out);
            out.push(' ');
            shape_expr(value, out);
            out.push(')');
        }
        Stmt::ExprStmt { expr, .. } => {
            out.push_str("(expr ");
            shape_expr(expr, out);
            out.push(')');
        }
        Stmt::Return { value, .. } => {
            out.push_str("(return");
            if let Some(v) = value {
                out.push(' ');
                shape_expr(v, out);
            }
            out.push(')');
        }
        Stmt::If {
            cond,
            then_body,
            else_body,
            ..
        } => {
            out.push_str("(if ");
            shape_expr(cond, out);
            out.push_str(" then[");
            out.push_str(&shape_stmts(then_body));
            out.push(']');
            if let Some(els) = else_body {
                out.push_str(" else[");
                out.push_str(&shape_stmts(els));
                out.push(']');
            }
            out.push(')');
        }
        Stmt::While { cond, body, .. } => {
            out.push_str("(while ");
            shape_expr(cond, out);
            out.push_str(" [");
            out.push_str(&shape_stmts(body));
            out.push_str("])");
        }
        Stmt::ForIn {
            variable,
            iter,
            body,
            ..
        } => {
            let _ = write!(out, "(for {variable} ");
            shape_expr(iter, out);
            out.push_str(" [");
            out.push_str(&shape_stmts(body));
            out.push_str("])");
        }
        Stmt::Break { .. } => out.push_str("(break)"),
        Stmt::Continue { .. } => out.push_str("(continue)"),
        Stmt::FnDecl {
            name,
            recv,
            type_params,
            params,
            ret,
            body,
            ..
        } => {
            let _ = write!(out, "(fn {name} tps{:?} ", type_params);
            match recv {
                Some(r) => {
                    out.push_str("(recv ");
                    out.push_str(&r.name);
                    out.push(' ');
                    shape_opt_type(r.typ.as_ref(), out);
                    out.push(')');
                }
                None => out.push('-'),
            }
            out.push(' ');
            shape_params(params, out);
            out.push(' ');
            shape_opt_type(ret.as_ref(), out);
            out.push_str(" [");
            out.push_str(&shape_stmts(body));
            out.push_str("])");
        }
        Stmt::StructDecl {
            name,
            type_params,
            fields,
            ..
        } => {
            let _ = write!(out, "(struct {name} tps{:?} ", type_params);
            shape_params(fields, out);
            out.push(')');
        }
        Stmt::EnumDecl {
            name,
            type_params,
            cases,
            ..
        } => {
            let _ = write!(out, "(enum {name} tps{:?} ", type_params);
            for c in cases {
                shape_case(c, out);
            }
            out.push(')');
        }
        Stmt::Go { call, .. } => {
            out.push_str("(go ");
            shape_expr(call, out);
            out.push(')');
        }
        Stmt::Import { path, .. } => {
            let _ = write!(out, "(import {path:?})");
        }
        Stmt::Extern { host, fns, .. } => {
            let _ = write!(out, "(extern {host:?} ");
            for f in fns {
                shape_stmt(f, out);
            }
            out.push(')');
        }
    }
}

fn shape_case(c: &EnumCase, out: &mut String) {
    let _ = write!(out, "(case {} ", c.name);
    out.push('[');
    for p in &c.payloads {
        shape_type(p, out);
    }
    out.push_str("])");
}

fn shape_expr(e: &Expr, out: &mut String) {
    match e {
        Expr::IntLit { value, .. } => {
            let _ = write!(out, "(int {value})");
        }
        Expr::FloatLit { value, .. } => {
            let _ = write!(out, "(float {value:?})");
        }
        Expr::StrLit { value, .. } => {
            let _ = write!(out, "(str {value:?})");
        }
        Expr::BoolLit { value, .. } => {
            let _ = write!(out, "(bool {value})");
        }
        Expr::Ident { name, .. } => {
            let _ = write!(out, "(id {name})");
        }
        Expr::Unary { op, operand, .. } => {
            let _ = write!(out, "(un {:?} ", op);
            shape_expr(operand, out);
            out.push(')');
        }
        Expr::Binary { op, lhs, rhs, .. } => {
            let _ = write!(out, "(bin {:?} ", op);
            shape_expr(lhs, out);
            out.push(' ');
            shape_expr(rhs, out);
            out.push(')');
        }
        Expr::Range { lo, hi, .. } => {
            out.push_str("(range ");
            shape_expr(lo, out);
            out.push(' ');
            shape_expr(hi, out);
            out.push(')');
        }
        Expr::Call { callee, args, .. } => {
            out.push_str("(call ");
            shape_expr(callee, out);
            out.push('[');
            for a in args {
                shape_expr(a, out);
            }
            out.push(']');
            out.push(')');
        }
        Expr::Index { obj, idx, .. } => {
            out.push_str("(index ");
            shape_expr(obj, out);
            out.push(' ');
            shape_expr(idx, out);
            out.push(')');
        }
        Expr::Slice { obj, lo, hi, .. } => {
            out.push_str("(slice ");
            shape_expr(obj, out);
            out.push(' ');
            shape_expr(lo, out);
            out.push(' ');
            shape_expr(hi, out);
            out.push(')');
        }
        Expr::Field { obj, name, .. } => {
            let _ = write!(out, "(get ");
            shape_expr(obj, out);
            let _ = write!(out, " {name})");
        }
        Expr::Try { operand, .. } => {
            out.push_str("(try ");
            shape_expr(operand, out);
            out.push(')');
        }
        Expr::StructLit {
            type_name, fields, ..
        } => {
            let _ = write!(out, "(slit {type_name} ");
            for fl in fields {
                shape_field(fl, out);
            }
            out.push(')');
        }
        Expr::MapLit { items, .. } => {
            out.push_str("(mlit ");
            for it in items {
                shape_item(it, out);
            }
            out.push(')');
        }
        Expr::ArrLit { items, .. } => {
            out.push_str("(alit [");
            for el in items {
                shape_expr(el, out);
            }
            out.push_str("])");
        }
        Expr::Match {
            scrutinee, arms, ..
        } => {
            out.push_str("(match ");
            shape_expr(scrutinee, out);
            for arm in arms {
                out.push_str("(arm ");
                shape_pat(&arm.pat, out);
                out.push(' ');
                shape_expr(&arm.body, out);
                out.push(')');
            }
            out.push(')');
        }
        Expr::FnLit {
            type_params,
            params,
            ret,
            body,
            ..
        } => {
            let _ = write!(out, "(fnlit tps{:?} ", type_params);
            shape_params(params, out);
            out.push(' ');
            shape_opt_type(ret.as_ref(), out);
            out.push_str(" [");
            out.push_str(&shape_stmts(body));
            out.push_str("])");
        }
    }
}

fn shape_field(fl: &FieldLit, out: &mut String) {
    let _ = write!(out, "(fl {} ", fl.name);
    shape_expr(&fl.value, out);
    out.push(')');
}

fn shape_item(it: &MapItem, out: &mut String) {
    out.push_str("(mi ");
    shape_expr(&it.key, out);
    out.push(' ');
    shape_expr(&it.value, out);
    out.push(')');
}

fn shape_pat(p: &Pat, out: &mut String) {
    match p {
        Pat::Wild { .. } => out.push_str("(wild)"),
        Pat::Bind { name, .. } => {
            let _ = write!(out, "(bind {name})");
        }
        Pat::Lit { value, .. } => {
            out.push_str("(lit ");
            shape_expr(value, out);
            out.push(')');
        }
        Pat::Case { name, subs, .. } => {
            let _ = write!(out, "(case {name} [");
            for s in subs {
                shape_pat(s, out);
            }
            out.push_str("])");
        }
    }
}

fn shape_type(t: &TypeEx, out: &mut String) {
    match t {
        TypeEx::Named { name, args, .. } => {
            let _ = write!(out, "(tnamed {name} [");
            for a in args {
                shape_type(a, out);
            }
            out.push_str("])");
        }
        TypeEx::Arr { elem, .. } => {
            out.push_str("(tarr ");
            shape_type(elem, out);
            out.push(')');
        }
        TypeEx::Map { key, val, .. } => {
            out.push_str("(tmap ");
            shape_type(key, out);
            out.push(' ');
            shape_type(val, out);
            out.push(')');
        }
        TypeEx::Ptr { elem, .. } => {
            out.push_str("(tptr ");
            shape_type(elem, out);
            out.push(')');
        }
        TypeEx::Fn { params, ret, .. } => {
            out.push_str("(tfn [");
            for p in params {
                shape_type(p, out);
            }
            out.push_str("] ");
            match ret {
                Some(r) => shape_type(r, out),
                None => out.push('-'),
            }
            out.push(')');
        }
    }
}
