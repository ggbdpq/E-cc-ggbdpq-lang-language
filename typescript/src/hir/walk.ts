// HIR 全程序遍历（verify 的结构完整性检查用）。回调序：父先子后。

import type { HirExpr, HirProg, HirStmt } from "./model.js";

export type HirWalkCallbacks = {
  stmt?: (s: HirStmt) => void;
  expr?: (e: HirExpr) => void;
};

export function walkHirProg(p: HirProg, cb: HirWalkCallbacks): void {
  for (const g of p.globals) walkHirStmt(g, cb);
  for (const f of p.fns) {
    cb.stmt?.({ kind: "fn_stmt", fn: f, id: f.id, line: f.line, span: f.span, sym: f.sym, typ: f.typ });
    walkHirStmts(f.body, cb);
  }
}

function walkHirStmts(list: readonly HirStmt[], cb: HirWalkCallbacks): void {
  for (const s of list) walkHirStmt(s, cb);
}

export function walkHirStmt(st: HirStmt, cb: HirWalkCallbacks): void {
  cb.stmt?.(st);
  switch (st.kind) {
    case "let":
      walkHirExpr(st.init, cb);
      return;
    case "assign":
      walkHirExpr(st.target, cb);
      walkHirExpr(st.rhs, cb);
      return;
    case "eval":
      walkHirExpr(st.x, cb);
      return;
    case "return":
      if (st.val !== undefined) walkHirExpr(st.val, cb);
      return;
    case "if":
      walkHirExpr(st.cond, cb);
      walkHirStmts(st.then, cb);
      walkHirStmts(st.els, cb);
      return;
    case "while":
      walkHirExpr(st.cond, cb);
      walkHirStmts(st.body, cb);
      return;
    case "for_in":
      walkHirExpr(st.iter, cb);
      walkHirStmts(st.body, cb);
      return;
    case "spawn":
      for (const a of st.args) walkHirExpr(a, cb);
      return;
    case "fn_stmt":
      walkHirStmts(st.fn.body, cb);
      return;
    case "break":
    case "continue":
      return;
    default: {
      const _never: never = st;
      return _never;
    }
  }
}

export function walkHirExpr(e: HirExpr, cb: HirWalkCallbacks): void {
  cb.expr?.(e);
  switch (e.kind) {
    case "lit":
    case "local_ref":
    case "fn_ref":
    case "builtin_ref":
      return;
    case "ctor":
      for (const a of e.args) walkHirExpr(a, cb);
      return;
    case "call":
      if (e.callee !== undefined) walkHirExpr(e.callee, cb);
      for (const a of e.args) walkHirExpr(a, cb);
      return;
    case "method_call":
      walkHirExpr(e.recv, cb);
      for (const a of e.args) walkHirExpr(a, cb);
      return;
    case "unary":
      walkHirExpr(e.x, cb);
      return;
    case "binary":
      walkHirExpr(e.l, cb);
      walkHirExpr(e.r, cb);
      return;
    case "and_then":
    case "or_else":
      walkHirExpr(e.l, cb);
      walkHirExpr(e.r, cb);
      return;
    case "range":
      walkHirExpr(e.lo, cb);
      walkHirExpr(e.hi, cb);
      return;
    case "index":
      walkHirExpr(e.obj, cb);
      walkHirExpr(e.idx, cb);
      return;
    case "slice":
      walkHirExpr(e.obj, cb);
      walkHirExpr(e.lo, cb);
      walkHirExpr(e.hi, cb);
      return;
    case "field":
      walkHirExpr(e.obj, cb);
      return;
    case "try":
      walkHirExpr(e.x, cb);
      return;
    case "struct_lit":
      for (const f of e.fields) walkHirExpr(f.val, cb);
      return;
    case "map_lit":
      for (let i = 0; i < e.keys.length; i++) {
        walkHirExpr(e.keys[i]!, cb);
        walkHirExpr(e.vals[i]!, cb);
      }
      return;
    case "slice_lit":
      for (const el of e.elems) walkHirExpr(el, cb);
      return;
    case "match":
      walkHirExpr(e.scrut, cb);
      for (const arm of e.arms) walkHirExpr(arm.body, cb);
      return;
    case "fn_lit":
      walkHirStmts(e.body, cb);
      return;
    default: {
      const _never: never = e;
      return _never;
    }
  }
}
