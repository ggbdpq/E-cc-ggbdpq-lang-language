// AST 统一遍历器（文件归属、dump ast JSON 共用）。
// 回调序：父节点先于子节点；undefined 回调跳过。

import type { Expr, Pat, Stmt } from "./ast.js";

export interface WalkCallbacks {
  readonly stmt?: (s: Stmt) => void;
  readonly expr?: (e: Expr) => void;
  readonly pat?: (p: Pat) => void;
}

export function walkStmts(list: readonly Stmt[], c: WalkCallbacks): void {
  for (const st of list) walkStmt(st, c);
}

export function walkStmt(st: Stmt, c: WalkCallbacks): void {
  c.stmt?.(st);
  switch (st.kind) {
    case "let_stmt":
      walkExpr(st.init, c);
      return;
    case "assign":
      walkExpr(st.target, c);
      walkExpr(st.val, c);
      return;
    case "expr_stmt":
      walkExpr(st.x, c);
      return;
    case "return":
      if (st.val !== undefined) walkExpr(st.val, c);
      return;
    case "if":
      walkExpr(st.cond, c);
      walkStmts(st.then, c);
      if (st.els !== undefined) walkStmts(st.els, c);
      return;
    case "while":
      walkExpr(st.cond, c);
      walkStmts(st.body, c);
      return;
    case "for_in":
      walkExpr(st.iter, c);
      walkStmts(st.body, c);
      return;
    case "go":
      walkExpr(st.call, c);
      return;
    case "fn_decl":
      walkStmts(st.body, c);
      return;
    case "extern":
      for (const fn of st.fns) walkStmt(fn, c);
      return;
    case "break":
    case "continue":
    case "struct_decl":
    case "enum_decl":
    case "import":
      return;
    default: {
      const _never: never = st;
      return _never;
    }
  }
}

export function walkExpr(ex: Expr, c: WalkCallbacks): void {
  c.expr?.(ex);
  switch (ex.kind) {
    case "int_lit":
    case "float_lit":
    case "str_lit":
    case "bool_lit":
    case "ident":
      return;
    case "unary":
      walkExpr(ex.x, c);
      return;
    case "binary":
      walkExpr(ex.l, c);
      walkExpr(ex.r, c);
      return;
    case "range":
      walkExpr(ex.lo, c);
      walkExpr(ex.hi, c);
      return;
    case "call":
      walkCall(ex, c);
      return;
    case "index":
      walkExpr(ex.obj, c);
      walkExpr(ex.idx, c);
      return;
    case "slice":
      walkExpr(ex.obj, c);
      walkExpr(ex.lo, c);
      walkExpr(ex.hi, c);
      return;
    case "field":
      walkExpr(ex.obj, c);
      return;
    case "try":
      walkExpr(ex.x, c);
      return;
    case "struct_lit":
      for (const f of ex.fields) walkExpr(f.val, c);
      return;
    case "map_lit":
      for (const it of ex.items) {
        walkExpr(it.key, c);
        walkExpr(it.val, c);
      }
      return;
    case "arr_lit":
      for (const el of ex.items) walkExpr(el, c);
      return;
    case "match":
      walkExpr(ex.scrut, c);
      for (const arm of ex.arms) {
        walkPat(arm.pat, c);
        walkExpr(arm.body, c);
      }
      return;
    case "fn_lit":
      walkStmts(ex.body, c);
      return;
    default: {
      const _never: never = ex;
      return _never;
    }
  }
}

function walkCall(x: { callee: Expr; args: readonly Expr[] }, c: WalkCallbacks): void {
  walkExpr(x.callee, c);
  for (const a of x.args) walkExpr(a, c);
}

export function walkPat(p: Pat, c: WalkCallbacks): void {
  c.pat?.(p);
  switch (p.kind) {
    case "lit_pat":
      walkExpr(p.val, c);
      return;
    case "case_pat":
      for (const sp of p.subs) walkPat(sp, c);
      return;
    case "wild_pat":
    case "bind_pat":
      return;
    default: {
      const _never: never = p;
      return _never;
    }
  }
}
