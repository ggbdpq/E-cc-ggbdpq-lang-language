"""HIR 统一遍历器（测试校验、IR 降级共用）。回调序：父节点先于子节点。"""

from __future__ import annotations

from typing import Callable, Optional

from . import nodes as H

StmtFn = Callable[[H.Stmt], None]
ExprFn = Callable[[H.Expr], None]
PatFn = Callable[[H.Pat], None]


def walk_prog(p: H.Prog, s: Optional[StmtFn], e: Optional[ExprFn], pat: Optional[PatFn]) -> None:
    for g in p.globals:
        walk_stmt(g, s, e, pat)
    for f in p.fns:
        walk_stmts(f.body, s, e, pat)


def walk_stmts(list_: list[H.Stmt], s: Optional[StmtFn], e: Optional[ExprFn], pat: Optional[PatFn]) -> None:
    for st in list_:
        walk_stmt(st, s, e, pat)


def walk_stmt(st: H.Stmt, s: Optional[StmtFn], e: Optional[ExprFn], pat: Optional[PatFn]) -> None:
    if s is not None:
        s(st)
    if isinstance(st, H.Let):
        walk_expr(st.init, s, e, pat)
    elif isinstance(st, H.Assign):
        walk_expr(st.target, s, e, pat)
        walk_expr(st.rhs, s, e, pat)
    elif isinstance(st, H.Eval):
        walk_expr(st.x, s, e, pat)
    elif isinstance(st, H.Return):
        if st.val is not None:
            walk_expr(st.val, s, e, pat)
    elif isinstance(st, H.If):
        walk_expr(st.cond, s, e, pat)
        walk_stmts(st.then, s, e, pat)
        walk_stmts(st.els, s, e, pat)
    elif isinstance(st, H.While):
        walk_expr(st.cond, s, e, pat)
        walk_stmts(st.body, s, e, pat)
    elif isinstance(st, H.ForIn):
        walk_expr(st.iter, s, e, pat)
        walk_stmts(st.body, s, e, pat)
    elif isinstance(st, H.Spawn):
        for a in st.args:
            walk_expr(a, s, e, pat)
    elif isinstance(st, H.FnStmt):
        walk_stmts(st.fn.body, s, e, pat)


def walk_expr(ex: H.Expr, s: Optional[StmtFn], e: Optional[ExprFn], pat: Optional[PatFn]) -> None:
    if e is not None:
        e(ex)
    if isinstance(ex, (H.Lit, H.LocalRef, H.FnRef, H.BuiltinRef)):
        return
    if isinstance(ex, H.Ctor):
        for a in ex.args:
            walk_expr(a, s, e, pat)
    elif isinstance(ex, H.Call):
        if ex.callee is not None:
            walk_expr(ex.callee, s, e, pat)
        for a in ex.args:
            walk_expr(a, s, e, pat)
    elif isinstance(ex, H.MethodCall):
        walk_expr(ex.recv, s, e, pat)
        for a in ex.args:
            walk_expr(a, s, e, pat)
    elif isinstance(ex, H.Unary):
        walk_expr(ex.x, s, e, pat)
    elif isinstance(ex, H.Binary):
        walk_expr(ex.l, s, e, pat)
        walk_expr(ex.r, s, e, pat)
    elif isinstance(ex, (H.AndThen, H.OrElse)):
        walk_expr(ex.l, s, e, pat)
        walk_expr(ex.r, s, e, pat)
    elif isinstance(ex, H.Range):
        walk_expr(ex.lo, s, e, pat)
        walk_expr(ex.hi, s, e, pat)
    elif isinstance(ex, H.Index):
        walk_expr(ex.obj, s, e, pat)
        walk_expr(ex.idx, s, e, pat)
    elif isinstance(ex, H.Slice):
        walk_expr(ex.obj, s, e, pat)
        walk_expr(ex.lo, s, e, pat)
        walk_expr(ex.hi, s, e, pat)
    elif isinstance(ex, H.Field):
        walk_expr(ex.obj, s, e, pat)
    elif isinstance(ex, H.Try):
        walk_expr(ex.x, s, e, pat)
    elif isinstance(ex, H.StructLit):
        for f in ex.fields:
            walk_expr(f.val, s, e, pat)
    elif isinstance(ex, H.MapLit):
        for k, v in zip(ex.keys, ex.vals):
            walk_expr(k, s, e, pat)
            walk_expr(v, s, e, pat)
    elif isinstance(ex, H.SliceLit):
        for el in ex.elems:
            walk_expr(el, s, e, pat)
    elif isinstance(ex, H.Match):
        walk_expr(ex.scrut, s, e, pat)
        for arm in ex.arms:
            walk_pat(arm.pat, s, e, pat)
            walk_expr(arm.body, s, e, pat)
    elif isinstance(ex, H.FnLit):
        walk_stmts(ex.body, s, e, pat)


def walk_pat(p: H.Pat, s: Optional[StmtFn], e: Optional[ExprFn], pat: Optional[PatFn]) -> None:
    if pat is not None:
        pat(p)
    if isinstance(p, H.LitPat):
        walk_expr(p.val, s, e, pat)
    elif isinstance(p, H.CtorPat):
        for sp in p.subs:
            walk_pat(sp, s, e, pat)
