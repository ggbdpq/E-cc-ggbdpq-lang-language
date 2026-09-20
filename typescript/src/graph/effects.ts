// Effect Metadata 从 Core IR 推导（v0.4 起，v0.6 迁入 graph）。
// 直接效果来自 IR 节点（内建调用/extern/spawn）。
// 枚举固定顺序：PURE IO FS NET TIME RANDOM SPAWN CHANNEL FFI。
// FS/TIME/RANDOM 为保留枚举——当前语言无生产者。
// 注意：与 Go 参考实现一致——调用图 fixpoint 传播在参考实现中因符号表未接线
// 而实际未生效（symToUnit 恒空），此处如实镜像「仅直接效果」的现状行为。

import type { IrExpr, IrProg, IrStmt } from "../ir/model.js";

export const EffPure = "PURE";
export const EffIO = "IO";
export const EffFS = "FS";
export const EffNET = "NET";
export const EffTIME = "TIME";
export const EffRANDOM = "RANDOM";
export const EffSPAWN = "SPAWN";
export const EffCHANNEL = "CHANNEL";
export const EffFFI = "FFI";

const effectOrder: readonly string[] = [EffPure, EffIO, EffFS, EffNET, EffTIME, EffRANDOM, EffSPAWN, EffCHANNEL, EffFFI];

/** 内建直接效果（未列出的内建视为 PURE） */
const builtinEffects: Readonly<Record<string, string>> = {
  println: EffIO,
  print: EffIO,
  "http.get": EffNET,
  makeChan: EffCHANNEL,
  send: EffCHANNEL,
  recv: EffCHANNEL,
};

export interface GraphEffect {
  readonly symbolId: number;
  readonly name: string;
  readonly effects: readonly string[];
}

interface FnDirect {
  readonly self: Set<string>;
}

/** 效果推导：每个顶层 fn/方法/嵌套 fn 各一条，按 SymbolID 排序 */
export function graphEffects(p: IrProg): GraphEffect[] {
  const units: IrFnU[] = [];
  const walkFns = (list: readonly IrStmt[]): void => {
    for (const s of list) {
      if (s.kind === "fn_def") {
        units.push(s.fn);
        walkFns(s.fn.body);
      }
    }
  };
  for (const f of p.fns) {
    units.push(f);
    walkFns(f.body);
  }

  const direct = new Map<IrFnU, FnDirect>();
  for (const f of units) direct.set(f, { self: new Set() });

  let cur: IrFnU;
  const addDirect = (name: string): void => {
    const n = builtinEffects[name];
    if (n !== undefined) direct.get(cur)!.self.add(n);
  };

  const visitExpr = (e: IrExpr): void => {
    switch (e.kind) {
      case "call": {
        if (e.builtin === "http.get") {
          direct.get(cur)!.self.add(EffNET);
        } else if (e.builtin !== undefined && e.builtin !== "") {
          addDirect(e.builtin);
        }
        if (e.recv !== undefined) visitExpr(e.recv);
        if (e.callee !== undefined) visitExpr(e.callee);
        for (const a of e.args) visitExpr(a);
        return;
      }
      case "ctor":
        for (const a of e.args) visitExpr(a);
        return;
      case "unary":
        visitExpr(e.x);
        return;
      case "binary":
        visitExpr(e.l);
        visitExpr(e.r);
        return;
      case "range":
        visitExpr(e.lo);
        visitExpr(e.hi);
        return;
      case "index":
        visitExpr(e.obj);
        visitExpr(e.idx);
        return;
      case "slice":
        visitExpr(e.obj);
        visitExpr(e.lo);
        visitExpr(e.hi);
        return;
      case "field":
        visitExpr(e.obj);
        return;
      case "try":
        visitExpr(e.x);
        return;
      case "struct_lit":
        for (const f of e.fields) visitExpr(f.val);
        return;
      case "map_lit":
        for (let i = 0; i < e.keys.length; i++) {
          visitExpr(e.keys[i]!);
          visitExpr(e.vals[i]!);
        }
        return;
      case "slice_lit":
        for (const el of e.elems) visitExpr(el);
        return;
      case "match":
        visitExpr(e.scrut);
        for (const arm of e.arms) visitExpr(arm.body);
        return;
      case "fn_lit":
        // 保守：字面量体的效果归当前单元（闭包定义处）
        for (const st of e.body) visitStmts([st]);
        return;
      default:
        return;
    }
  };

  const visitStmts = (list: readonly IrStmt[]): void => {
    for (const s of list) {
      switch (s.kind) {
        case "let":
          visitExpr(s.init);
          break;
        case "assign":
          visitExpr(s.target);
          visitExpr(s.val);
          break;
        case "eval":
          visitExpr(s.x);
          break;
        case "return":
          if (s.val !== undefined) visitExpr(s.val);
          break;
        case "if":
          visitExpr(s.cond);
          visitStmts(s.then);
          visitStmts(s.els);
          break;
        case "while":
          visitExpr(s.cond);
          visitStmts(s.body);
          break;
        case "for_in":
          visitExpr(s.iter);
          visitStmts(s.body);
          break;
        case "spawn":
          direct.get(cur)!.self.add(EffSPAWN);
          for (const a of s.args) visitExpr(a);
          break;
        case "fn_def":
          // 嵌套 fn 是独立单元；其效果不并入外层
          break;
        default:
          break;
      }
    }
  };

  for (const f of units) {
    cur = f;
    visitStmts(f.body);
  }

  // extern：直接效果 FFI
  for (const f of units) {
    if (f.extern) direct.get(f)!.self.add(EffFFI);
  }

  // 输出（SymbolID 排序；预载/无名跳过；同一 fn 去重）
  const out: GraphEffect[] = [];
  const seen = new Set<IrFnU>();
  for (const f of units) {
    if (seen.has(f) || f.sym === undefined || f.sym.builtinMark) continue;
    seen.add(f);
    out.push({ symbolId: f.sym.id, name: f.name, effects: orderedEffects(direct.get(f)!.self) });
  }
  out.sort((a, b) => a.symbolId - b.symbolId);
  return out;
}

type IrFnU = IrProg["fns"][number];

function orderedEffects(set: ReadonlySet<string>): string[] {
  if (set.size === 0) return [EffPure];
  return effectOrder.filter((e) => set.has(e));
}
