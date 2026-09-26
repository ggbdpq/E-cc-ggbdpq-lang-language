//! AST 走查（预序）：rename/safe_remove 的引用面事实来源。
//!
//! 单一递归走查覆盖全部 15 语句 / 18 表达式 / 4 模式变体（含 FnLit 体内
//! 语句、match 臂与构造子模式、extern 内联签名），访问顺序 = 源码出现序
//! （确定性）；三个回调分别收 语句 / 表达式 / 模式 节点。

use lang_core::SymbolId;
use lang_syntax::{Expr, Pat, Stmt};

use crate::input::RefactorInput;

/// 预序走查：先访节点自身，再递归子节点。
pub fn walk<'a, S, E, P>(stmts: &'a [Stmt], on_stmt: &mut S, on_expr: &mut E, on_pat: &mut P)
where
    S: FnMut(&'a Stmt),
    E: FnMut(&'a Expr),
    P: FnMut(&'a Pat),
{
    for s in stmts {
        on_stmt(s);
        match s {
            Stmt::Let { init, .. } => walk_expr(init, on_expr, on_pat),
            Stmt::Assign { target, value, .. } => {
                walk_expr(target, on_expr, on_pat);
                walk_expr(value, on_expr, on_pat);
            }
            Stmt::ExprStmt { expr, .. } => walk_expr(expr, on_expr, on_pat),
            Stmt::Return { value, .. } => {
                if let Some(v) = value {
                    walk_expr(v, on_expr, on_pat);
                }
            }
            Stmt::If {
                then_body,
                else_body,
                ..
            } => {
                walk(then_body, on_stmt, on_expr, on_pat);
                if let Some(els) = else_body {
                    walk(els, on_stmt, on_expr, on_pat);
                }
            }
            Stmt::While { body, .. } => walk(body, on_stmt, on_expr, on_pat),
            Stmt::ForIn { body, .. } => walk(body, on_stmt, on_expr, on_pat),
            Stmt::FnDecl { body, .. } => walk(body, on_stmt, on_expr, on_pat),
            Stmt::Extern { fns, .. } => walk(fns, on_stmt, on_expr, on_pat),
            Stmt::Go { call, .. } => walk_expr(call, on_expr, on_pat),
            Stmt::Break { .. }
            | Stmt::Continue { .. }
            | Stmt::StructDecl { .. }
            | Stmt::EnumDecl { .. }
            | Stmt::Import { .. } => {}
        }
    }
}

fn walk_expr<'a, E, P>(e: &'a Expr, on_expr: &mut E, on_pat: &mut P)
where
    E: FnMut(&'a Expr),
    P: FnMut(&'a Pat),
{
    on_expr(e);
    match e {
        Expr::Unary { operand, .. } => walk_expr(operand, on_expr, on_pat),
        Expr::Binary { lhs, rhs, .. } => {
            walk_expr(lhs, on_expr, on_pat);
            walk_expr(rhs, on_expr, on_pat);
        }
        Expr::Range { lo, hi, .. } => {
            walk_expr(lo, on_expr, on_pat);
            walk_expr(hi, on_expr, on_pat);
        }
        Expr::Call { callee, args, .. } => {
            walk_expr(callee, on_expr, on_pat);
            for a in args {
                walk_expr(a, on_expr, on_pat);
            }
        }
        Expr::Index { obj, idx, .. } => {
            walk_expr(obj, on_expr, on_pat);
            walk_expr(idx, on_expr, on_pat);
        }
        Expr::Slice { obj, lo, hi, .. } => {
            walk_expr(obj, on_expr, on_pat);
            walk_expr(lo, on_expr, on_pat);
            walk_expr(hi, on_expr, on_pat);
        }
        Expr::Field { obj, .. } => walk_expr(obj, on_expr, on_pat),
        Expr::Try { operand, .. } => walk_expr(operand, on_expr, on_pat),
        Expr::StructLit { fields, .. } => {
            for fl in fields {
                walk_expr(&fl.value, on_expr, on_pat);
            }
        }
        Expr::MapLit { items, .. } => {
            for it in items {
                walk_expr(&it.key, on_expr, on_pat);
                walk_expr(&it.value, on_expr, on_pat);
            }
        }
        Expr::ArrLit { items, .. } => {
            for it in items {
                walk_expr(it, on_expr, on_pat);
            }
        }
        Expr::Match {
            scrutinee, arms, ..
        } => {
            walk_expr(scrutinee, on_expr, on_pat);
            for arm in arms {
                walk_pat(&arm.pat, on_pat);
                walk_expr(&arm.body, on_expr, on_pat);
            }
        }
        Expr::FnLit { body, .. } => walk(body, &mut |_| {}, on_expr, on_pat),
        Expr::Ident { .. }
        | Expr::IntLit { .. }
        | Expr::FloatLit { .. }
        | Expr::StrLit { .. }
        | Expr::BoolLit { .. } => {}
    }
}

fn walk_pat<'a, P>(p: &'a Pat, on_pat: &mut P)
where
    P: FnMut(&'a Pat),
{
    on_pat(p);
    if let Pat::Case { subs, .. } = p {
        for sp in subs {
            walk_pat(sp, on_pat);
        }
    }
}

/// 目标符号的全部引用点（Ident/StructLit 引用；出现序）。
pub fn refs_of<'a>(input: &'a RefactorInput, target: SymbolId) -> Vec<&'a Expr> {
    let mut out = Vec::new();
    walk(
        &input.merged,
        &mut |_| {},
        &mut |e: &'a Expr| {
            if input.analysis.symbol_of_ref(e) == Some(target) {
                out.push(e);
            }
        },
        &mut |_| {},
    );
    out
}

/// 目标符号的声明语句锚（Let/ForIn/FnDecl，任何深度；出现序）。
pub fn decl_stmts_of<'a>(input: &'a RefactorInput, target: SymbolId) -> Vec<&'a Stmt> {
    let mut out = Vec::new();
    walk(
        &input.merged,
        &mut |s: &'a Stmt| {
            if input.analysis.symbol_of_decl(s) == Some(target) {
                out.push(s);
            }
        },
        &mut |_| {},
        &mut |_| {},
    );
    out
}

/// 目标符号的绑定模式点（match 绑定；出现序）。
pub fn pat_bindings_of<'a>(input: &'a RefactorInput, target: SymbolId) -> Vec<&'a Pat> {
    let mut out = Vec::new();
    walk(
        &input.merged,
        &mut |_| {},
        &mut |_| {},
        &mut |p: &'a Pat| {
            if input.analysis.symbol_of_pat(p) == Some(target) {
                out.push(p);
            }
        },
    );
    out
}

/// 目标符号的引用点及其**所属函数**（参数改名定位用；出现序去重）。
pub fn ref_owners_of(input: &RefactorInput, target: SymbolId) -> Vec<Option<SymbolId>> {
    let mut out: Vec<Option<SymbolId>> = Vec::new();
    walk_owned(input, &input.merged, None, &mut |e, owner| {
        if input.analysis.symbol_of_ref(e) == Some(target) && !out.contains(&owner) {
            out.push(owner);
        }
    });
    out
}

/// 带所属函数上下文的表达式走查（owner = 最近一层 FnDecl 的声明符号；
/// 嵌套 fn 切换上下文；FnLit 不切换——匿名函数不是符号）。
pub fn walk_owned<'a, F>(
    input: &'a RefactorInput,
    stmts: &'a [Stmt],
    owner: Option<SymbolId>,
    f: &mut F,
) where
    F: FnMut(&'a Expr, Option<SymbolId>),
{
    for s in stmts {
        match s {
            Stmt::Let { init, .. } => walk_expr_owned(input, init, owner, f),
            Stmt::Assign { target, value, .. } => {
                walk_expr_owned(input, target, owner, f);
                walk_expr_owned(input, value, owner, f);
            }
            Stmt::ExprStmt { expr, .. } => walk_expr_owned(input, expr, owner, f),
            Stmt::Return { value: Some(v), .. } => walk_expr_owned(input, v, owner, f),
            Stmt::If {
                cond,
                then_body,
                else_body,
                ..
            } => {
                walk_expr_owned(input, cond, owner, f);
                walk_owned(input, then_body, owner, f);
                if let Some(els) = else_body {
                    walk_owned(input, els, owner, f);
                }
            }
            Stmt::While { cond, body, .. } => {
                walk_expr_owned(input, cond, owner, f);
                walk_owned(input, body, owner, f);
            }
            Stmt::ForIn { iter, body, .. } => {
                walk_expr_owned(input, iter, owner, f);
                walk_owned(input, body, owner, f);
            }
            Stmt::FnDecl { body, .. } => {
                let next = input.analysis.symbol_of_decl(s);
                walk_owned(input, body, next, f);
            }
            Stmt::Extern { fns, .. } => walk_owned(input, fns, owner, f),
            Stmt::Go { call, .. } => walk_expr_owned(input, call, owner, f),
            _ => {}
        }
    }
}

fn walk_expr_owned<'a, F>(input: &'a RefactorInput, e: &'a Expr, owner: Option<SymbolId>, f: &mut F)
where
    F: FnMut(&'a Expr, Option<SymbolId>),
{
    f(e, owner);
    match e {
        Expr::Unary { operand, .. } => walk_expr_owned(input, operand, owner, f),
        Expr::Binary { lhs, rhs, .. } => {
            walk_expr_owned(input, lhs, owner, f);
            walk_expr_owned(input, rhs, owner, f);
        }
        Expr::Range { lo, hi, .. } => {
            walk_expr_owned(input, lo, owner, f);
            walk_expr_owned(input, hi, owner, f);
        }
        Expr::Call { callee, args, .. } => {
            walk_expr_owned(input, callee, owner, f);
            for a in args {
                walk_expr_owned(input, a, owner, f);
            }
        }
        Expr::Index { obj, idx, .. } => {
            walk_expr_owned(input, obj, owner, f);
            walk_expr_owned(input, idx, owner, f);
        }
        Expr::Slice { obj, lo, hi, .. } => {
            walk_expr_owned(input, obj, owner, f);
            walk_expr_owned(input, lo, owner, f);
            walk_expr_owned(input, hi, owner, f);
        }
        Expr::Field { obj, .. } => walk_expr_owned(input, obj, owner, f),
        Expr::Try { operand, .. } => walk_expr_owned(input, operand, owner, f),
        Expr::StructLit { fields, .. } => {
            for fl in fields {
                walk_expr_owned(input, &fl.value, owner, f);
            }
        }
        Expr::MapLit { items, .. } => {
            for it in items {
                walk_expr_owned(input, &it.key, owner, f);
                walk_expr_owned(input, &it.value, owner, f);
            }
        }
        Expr::ArrLit { items, .. } => {
            for it in items {
                walk_expr_owned(input, it, owner, f);
            }
        }
        Expr::Match {
            scrutinee, arms, ..
        } => {
            walk_expr_owned(input, scrutinee, owner, f);
            for arm in arms {
                walk_expr_owned(input, &arm.body, owner, f);
            }
        }
        Expr::FnLit { body, .. } => walk_owned(input, body, owner, f),
        Expr::Ident { .. }
        | Expr::IntLit { .. }
        | Expr::FloatLit { .. }
        | Expr::StrLit { .. }
        | Expr::BoolLit { .. } => {}
    }
}
