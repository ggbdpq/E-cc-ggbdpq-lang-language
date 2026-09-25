"""AST 统一遍历器（文件归属盖章、dump ast 共用）。回调序：父节点先于子节点。"""

from __future__ import annotations

from typing import Callable, Optional

from . import ast_nodes as A

StmtFn = Callable[[A.Stmt], None]
ExprFn = Callable[[A.Expr], None]
PatFn = Callable[[A.Pat], None]


def walk_stmts(list_: list[A.Stmt], s: Optional[StmtFn], e: Optional[ExprFn], pat: Optional[PatFn]) -> None:
    for st in list_:
        walk_stmt(st, s, e, pat)


def walk_stmt(st: A.Stmt, s: Optional[StmtFn], e: Optional[ExprFn], pat: Optional[PatFn]) -> None:
    if s is not None:
        s(st)
    if isinstance(st, A.LetStmt):
        walk_expr(st.init, s, e, pat)
    elif isinstance(st, A.AssignStmt):
        walk_expr(st.target, s, e, pat)
        walk_expr(st.val, s, e, pat)
    elif isinstance(st, A.ExprStmt):
        walk_expr(st.x, s, e, pat)
    elif isinstance(st, A.ReturnStmt):
        if st.val is not None:
            walk_expr(st.val, s, e, pat)
    elif isinstance(st, A.IfStmt):
        walk_expr(st.cond, s, e, pat)
        walk_stmts(st.then, s, e, pat)
        if st.els is not None:
            walk_stmts(st.els, s, e, pat)
    elif isinstance(st, A.WhileStmt):
        walk_expr(st.cond, s, e, pat)
        walk_stmts(st.body, s, e, pat)
    elif isinstance(st, A.ForInStmt):
        walk_expr(st.iter, s, e, pat)
        walk_stmts(st.body, s, e, pat)
    elif isinstance(st, A.GoStmt):
        walk_expr(st.call, s, e, pat)
    elif isinstance(st, A.FnDecl):
        walk_stmts(st.body, s, e, pat)
    elif isinstance(st, A.ExternDecl):
        for fn in st.fns:
            walk_stmt(fn, s, e, pat)


def walk_expr(ex: A.Expr, s: Optional[StmtFn], e: Optional[ExprFn], pat: Optional[PatFn]) -> None:
    if e is not None:
        e(ex)
    if isinstance(ex, (A.IntLit, A.FloatLit, A.StrLit, A.BoolLit, A.Ident)):
        return
    if isinstance(ex, A.Unary):
        walk_expr(ex.x, s, e, pat)
    elif isinstance(ex, A.Binary):
        walk_expr(ex.l, s, e, pat)
        walk_expr(ex.r, s, e, pat)
    elif isinstance(ex, A.RangeEx):
        walk_expr(ex.lo, s, e, pat)
        walk_expr(ex.hi, s, e, pat)
    elif isinstance(ex, A.CallEx):
        walk_expr(ex.callee, s, e, pat)
        for a in ex.args:
            walk_expr(a, s, e, pat)
    elif isinstance(ex, A.IndexEx):
        walk_expr(ex.obj, s, e, pat)
        walk_expr(ex.idx, s, e, pat)
    elif isinstance(ex, A.SliceEx):
        walk_expr(ex.obj, s, e, pat)
        walk_expr(ex.lo, s, e, pat)
        walk_expr(ex.hi, s, e, pat)
    elif isinstance(ex, A.FieldEx):
        walk_expr(ex.obj, s, e, pat)
    elif isinstance(ex, A.TryEx):
        walk_expr(ex.x, s, e, pat)
    elif isinstance(ex, A.StructLit):
        for f in ex.fields:
            walk_expr(f.val, s, e, pat)
    elif isinstance(ex, A.MapLit):
        for it in ex.items:
            walk_expr(it.key, s, e, pat)
            walk_expr(it.val, s, e, pat)
    elif isinstance(ex, A.ArrLit):
        for el in ex.items:
            walk_expr(el, s, e, pat)
    elif isinstance(ex, A.MatchEx):
        walk_expr(ex.scrut, s, e, pat)
        for arm in ex.arms:
            walk_pat(arm.pat, s, e, pat)
            walk_expr(arm.body, s, e, pat)
    elif isinstance(ex, A.FnLit):
        walk_stmts(ex.body, s, e, pat)


def walk_pat(p: A.Pat, s: Optional[StmtFn], e: Optional[ExprFn], pat: Optional[PatFn]) -> None:
    if pat is not None:
        pat(p)
    if isinstance(p, A.LitPat):
        walk_expr(p.val, s, e, pat)
    elif isinstance(p, A.CasePat):
        for sp in p.subs:
            walk_pat(sp, s, e, pat)


def stamp_file(stmts: list[A.Stmt], file: str) -> None:
    """Span.file 归属盖章（装载器在解析后调用）。"""

    def on_stmt(st: A.Stmt) -> None:
        st.span = st.span.with_file(file)

    def on_expr(ex: A.Expr) -> None:
        ex.span = ex.span.with_file(file)

    def on_pat(p: A.Pat) -> None:
        p.span = p.span.with_file(file)

    walk_stmts(stmts, on_stmt, on_expr, on_pat)
