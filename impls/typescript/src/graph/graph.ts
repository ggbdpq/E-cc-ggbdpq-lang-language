// 语义关系图（v0.6 Semantic Graph）。
// 真值源：semantic 符号表 + types 产物 + HIR 调用/方法边 + IR 效果推导 + load 文件依赖。
// 纯内存图；节点键 = SymbolID（编译内稳定）；查询为 BFS（访问集防环）。

import type { Stmt, Expr } from "../syntax/ast.js";
import type { Span } from "../syntax/span.js";
import { walkStmt, walkExpr } from "../syntax/walk.js";
import type { ResolveResult } from "../semantic/resolver.js";
import type { CheckResult } from "../types/checker.js";
import type { HirProg, HirStmt, HirExpr } from "../hir/model.js";
import type { IrProg } from "../ir/model.js";
import type { FileInfo } from "../loader/loader.js";
import { typeStr } from "../types/model.js";
import { graphEffects } from "./effects.js";
import type { Symbol } from "../semantic/symbols.js";

/** 图节点（Symbol 粒度） */
export interface GraphNode {
  readonly id: number;
  readonly kind: string;
  readonly name: string;
  readonly module: string;
  readonly span: Span | undefined;
  readonly type: string;
  readonly effects: readonly string[];
  readonly fields: readonly string[];
  readonly extern: boolean;
  readonly immutable: boolean;
}

export type EdgeKind =
  | "call"
  | "references"
  | "writes"
  | "spawn"
  | "type"
  | "pattern"
  | "depends_on"
  | "imports";

export interface GraphEdge {
  readonly from: number;
  readonly to: number;
  readonly kind: EdgeKind;
  readonly span: Span | undefined;
  readonly file: string;
}

export class Graph {
  readonly nodes = new Map<number, GraphNode>();
  readonly order: number[] = [];
  readonly edges: GraphEdge[] = [];
  private readonly inIdx = new Map<number, number[]>();
  private readonly outIdx = new Map<number, number[]>();

  readonly fileImports = new Map<string, string[]>();
  readonly fileDependsOn = new Map<string, string[]>();
  readonly files: string[] = [];

  private readonly depSeen = new Set<string>();

  inEdges(id: number): GraphEdge[] {
    return (this.inIdx.get(id) ?? []).map((i) => this.edges[i]!);
  }

  outEdges(id: number): GraphEdge[] {
    return (this.outIdx.get(id) ?? []).map((i) => this.edges[i]!);
  }

  node(id: number): GraphNode | undefined {
    return this.nodes.get(id);
  }

  addEdge(e: GraphEdge): void {
    this.edges.push(e);
    const idx = this.edges.length - 1;
    const inList = this.inIdx.get(e.to) ?? [];
    inList.push(idx);
    this.inIdx.set(e.to, inList);
    const outList = this.outIdx.get(e.from) ?? [];
    outList.push(idx);
    this.outIdx.set(e.from, outList);
    if (e.from > 0 && e.to > 0) {
      const from = this.nodeModule(e.from);
      const to = this.nodeModule(e.to);
      if (from !== "" && to !== "" && from !== to) {
        const key = `${from}\0${to}`;
        if (!this.depSeen.has(key)) {
          this.depSeen.add(key);
          const list = this.fileDependsOn.get(from) ?? [];
          list.push(to);
          this.fileDependsOn.set(from, list);
        }
      }
    }
  }

  private nodeModule(id: number): string {
    return this.nodes.get(id)?.module ?? "";
  }
}

const symKindStr = (k: string): string =>
  k === "fn" ? "function" : k === "method" ? "method" : k === "var" ? "variable" : k === "param" ? "parameter" : k === "struct" ? "struct" : k === "enum" ? "enum" : k === "ctor" ? "constructor" : "unknown";

/** 从全前端产物构建语义图 */
export function buildGraph(
  prog: readonly Stmt[],
  res: ResolveResult,
  ty: CheckResult,
  h: HirProg,
  irp: IrProg,
  files: readonly FileInfo[],
): Graph {
  const g = new Graph();
  for (const f of files) {
    g.files.push(f.path);
    g.fileImports.set(f.path, [...f.imports]);
  }

  // 1) 节点（用户符号）
  for (const sym of res.symbols) {
    if (sym.builtinMark) continue;
    let span: Span | undefined;
    let module = "";
    if (sym.decl !== undefined) {
      span = sym.decl.span;
      module = span.file;
    }
    let type = "";
    const st = ty.symTy.get(sym.id);
    if (st !== undefined) type = typeStr(st);
    else {
      const ft = ty.fnTy.get(sym.id);
      if (ft !== undefined) type = typeStr(ft);
    }
    const n: GraphNode = {
      id: sym.id,
      kind: symKindStr(sym.kind),
      name: sym.name,
      module,
      span,
      type,
      effects: [],
      fields: sym.kind === "struct" ? [...sym.fields] : [],
      extern: sym.extern,
      immutable: !sym.mutable,
    };
    g.nodes.set(n.id, n);
    g.order.push(n.id);
  }

  // 2) 效果（IR 推导）附着到函数/方法节点
  for (const e of graphEffects(irp)) {
    const n = g.nodes.get(e.symbolId);
    if (n !== undefined) {
      (n as { effects: readonly string[] }).effects = e.effects;
    }
  }

  // 3) call / spawn / method 边（HIR）
  for (const gl of h.globals) {
    collectHIRStmt(gl, 0, g);
  }
  for (const f of h.fns) {
    const owner = f.sym !== undefined ? f.sym.id : 0;
    collectHIRStmts(f.body, owner, g);
  }

  // 4) reference / write / type 边 + 文件级 depends_on
  collectRefs(prog, res, g);
  return g;
}

function collectHIRStmts(list: readonly HirStmt[], from: number, g: Graph): void {
  for (const st of list) collectHIRStmt(st, from, g);
}

function collectHIRStmt(st: HirStmt, from: number, g: Graph): void {
  switch (st.kind) {
    case "let":
      collectHIRExpr(st.init, from, g);
      return;
    case "assign":
      collectHIRExpr(st.target, from, g);
      collectHIRExpr(st.rhs, from, g);
      return;
    case "eval":
      collectHIRExpr(st.x, from, g);
      return;
    case "return":
      if (st.val !== undefined) collectHIRExpr(st.val, from, g);
      return;
    case "if":
      collectHIRExpr(st.cond, from, g);
      collectHIRStmts(st.then, from, g);
      collectHIRStmts(st.els, from, g);
      return;
    case "while":
      collectHIRExpr(st.cond, from, g);
      collectHIRStmts(st.body, from, g);
      return;
    case "for_in":
      collectHIRExpr(st.iter, from, g);
      collectHIRStmts(st.body, from, g);
      return;
    case "spawn":
      if (st.fn !== undefined) {
        g.addEdge({ from, to: st.fn.id, kind: "spawn", span: undefined, file: "" });
      }
      for (const a of st.args) collectHIRExpr(a, from, g);
      return;
    case "fn_stmt":
      collectHIRStmts(st.fn.body, from, g);
      return;
    default:
      return;
  }
}

function collectHIRExpr(e: HirExpr, from: number, g: Graph): void {
  switch (e.kind) {
    case "ctor":
      for (const a of e.args) collectHIRExpr(a, from, g);
      return;
    case "call":
      if (e.fn !== undefined) {
        g.addEdge({ from, to: e.fn.id, kind: "call", span: e.span, file: "" });
      }
      if (e.callee !== undefined) collectHIRExpr(e.callee, from, g);
      for (const a of e.args) collectHIRExpr(a, from, g);
      return;
    case "method_call":
      if (e.method !== undefined) {
        g.addEdge({ from, to: e.method.id, kind: "call", span: e.span, file: "" });
      }
      collectHIRExpr(e.recv, from, g);
      for (const a of e.args) collectHIRExpr(a, from, g);
      return;
    case "unary":
      collectHIRExpr(e.x, from, g);
      return;
    case "binary":
      collectHIRExpr(e.l, from, g);
      collectHIRExpr(e.r, from, g);
      return;
    case "and_then":
      collectHIRExpr(e.l, from, g);
      collectHIRExpr(e.r, from, g);
      return;
    case "or_else":
      collectHIRExpr(e.l, from, g);
      collectHIRExpr(e.r, from, g);
      return;
    case "range":
      collectHIRExpr(e.lo, from, g);
      collectHIRExpr(e.hi, from, g);
      return;
    case "index":
      collectHIRExpr(e.obj, from, g);
      collectHIRExpr(e.idx, from, g);
      return;
    case "slice":
      collectHIRExpr(e.obj, from, g);
      collectHIRExpr(e.lo, from, g);
      collectHIRExpr(e.hi, from, g);
      return;
    case "field":
      collectHIRExpr(e.obj, from, g);
      return;
    case "try":
      collectHIRExpr(e.x, from, g);
      return;
    case "struct_lit":
      for (const f of e.fields) collectHIRExpr(f.val, from, g);
      return;
    case "map_lit":
      for (let i = 0; i < e.keys.length; i++) {
        collectHIRExpr(e.keys[i]!, from, g);
        collectHIRExpr(e.vals[i]!, from, g);
      }
      return;
    case "slice_lit":
      for (const el of e.elems) collectHIRExpr(el, from, g);
      return;
    case "match":
      collectHIRExpr(e.scrut, from, g);
      for (const arm of e.arms) collectHIRExpr(arm.body, from, g);
      return;
    case "fn_lit":
      collectHIRStmts(e.body, from, g);
      return;
    default:
      return;
  }
}

// —— 引用/写入/类型边（AST + resolver 引用表） ——

interface RefCtx {
  readonly g: Graph;
  readonly res: ResolveResult;
  fnID: number;
  file: string;
  readonly writeSet: Set<Expr>;
  readonly callsSet: Set<Expr>;
}

function collectRefs(prog: readonly Stmt[], res: ResolveResult, g: Graph): void {
  const c: RefCtx = { g, res, fnID: 0, file: "", writeSet: new Set(), callsSet: new Set() };
  const scanCb = {
    stmt: (s: Stmt): void => {
      if (s.kind === "assign" && s.target.kind === "ident") c.writeSet.add(s.target);
    },
    expr: (e: Expr): void => {
      if (e.kind === "call" && e.callee.kind === "ident") c.callsSet.add(e.callee);
    },
  };
  for (const st of prog) walkStmt(st, scanCb);
  for (const st of prog) refStmt(c, st);
}

function refStmt(c: RefCtx, s: Stmt): void {
  const f = s.span.file;
  if (f !== "") c.file = f;
  switch (s.kind) {
    case "let_stmt":
      refExpr(c, s.init);
      return;
    case "assign":
      refExpr(c, s.target);
      refExpr(c, s.val);
      return;
    case "expr_stmt":
      refExpr(c, s.x);
      return;
    case "return":
      if (s.val !== undefined) refExpr(c, s.val);
      return;
    case "if":
      refExpr(c, s.cond);
      refBlock(c, s.then);
      refBlock(c, s.els ?? []);
      return;
    case "while":
      refExpr(c, s.cond);
      refBlock(c, s.body);
      return;
    case "for_in":
      refExpr(c, s.iter);
      refBlock(c, s.body);
      return;
    case "go":
      refExpr(c, s.call);
      return;
    case "fn_decl": {
      // 嵌套 fn：切换所属函数上下文
      const saved = c.fnID;
      const sym = c.res.declSyms.get(s);
      if (sym !== undefined) c.fnID = sym.id;
      for (const st of s.body) refStmt(c, st);
      c.fnID = saved;
      return;
    }
    case "extern":
    case "struct_decl":
    case "enum_decl":
    case "import":
    case "break":
    case "continue":
      return;
    default: {
      const _never: never = s;
      return _never;
    }
  }
}

function refBlock(c: RefCtx, list: readonly Stmt[]): void {
  for (const s of list) refStmt(c, s);
}

function refExpr(c: RefCtx, e: Expr): void {
  walkExpr(e, {
    expr: (x) => {
      if (x.kind === "ident") {
        const sym = c.res.refs.get(x);
        if (sym === undefined) return;
        // 直接调用的 fn 名：call 边由 HIR 收集，此处跳过
        if (c.callsSet.has(x) && sym.kind === "fn") return;
        const kind = c.writeSet.has(x) ? "writes" : "references";
        c.g.addEdge({ from: c.fnID, to: sym.id, kind, span: x.span, file: "" });
      } else if (x.kind === "struct_lit") {
        const sym = c.res.refs.get(x);
        if (sym !== undefined) {
          c.g.addEdge({ from: c.fnID, to: sym.id, kind: "type", span: x.span, file: "" });
        }
      }
    },
  });
}

// —— 查询 ——

export interface ImpactItem {
  readonly from: number;
  readonly kind: EdgeKind | "";
  readonly span: Span | undefined;
  readonly transitive: boolean;
}

export interface FileDep {
  readonly module: string;
  readonly imports: readonly string[];
  readonly dependsOn: readonly string[];
}

/** 反向调用 BFS：谁（直接/传递）调用 id */
export function graphCallers(g: Graph, id: number, depth: number): ImpactItem[] {
  return callBFS(g, id, true, depth);
}

/** 正向调用 BFS：id（直接/传递）调用谁 */
export function graphCallees(g: Graph, id: number, depth: number): ImpactItem[] {
  return callBFS(g, id, false, depth);
}

function callBFS(g: Graph, id: number, reverse: boolean, depth: number): ImpactItem[] {
  const visited = new Set<number>([id]);
  let level = [id];
  const out: ImpactItem[] = [];
  let d = 0;
  while (level.length > 0 && (depth < 0 || d < depth)) {
    const next: number[] = [];
    for (const cur of level) {
      const edges = reverse ? g.inEdges(cur) : g.outEdges(cur);
      for (const e of edges) {
        if (e.kind !== "call") continue;
        const other = reverse ? e.from : e.to;
        if (visited.has(other)) continue;
        visited.add(other);
        next.push(other);
        out.push({ from: other, kind: "", span: undefined, transitive: d + 1 > 1 });
      }
    }
    level = next;
    d++;
  }
  out.sort((a, b) => a.from - b.from);
  return out;
}

export interface Impact {
  readonly symbol: GraphNode | undefined;
  readonly direct: ImpactItem[];
  readonly transitive: ImpactItem[];
  readonly effectWatch: number[];
  readonly modules: readonly string[];
}

/** 影响分析：direct=全部入边；transitive=沿调用链传递；effectWatch=传递调用方 */
export function graphImpact(g: Graph, id: number, depth: number): Impact {
  const symbol = g.nodes.get(id);
  const seen = new Set<number>([id]);
  const direct: ImpactItem[] = [];
  const effectWatch: number[] = [];
  const transitive: ImpactItem[] = [];

  // 直接影响：全部入边
  for (const e of g.inEdges(id)) {
    if (e.from === 0) continue;
    direct.push({ from: e.from, kind: e.kind, span: e.span, transitive: false });
    if (e.kind === "call" && !seen.has(e.from)) {
      seen.add(e.from);
      effectWatch.push(e.from);
    }
  }

  // 传递影响：直接调用方为第一层种子，沿调用边反向逐层
  let level: number[] = [];
  for (const e of g.inEdges(id)) {
    if (e.kind === "call" && e.from > 0) level.push(e.from);
  }
  let d = 1;
  while (level.length > 0 && (depth < 0 || d < depth)) {
    const next: number[] = [];
    for (const cur of level) {
      for (const e of g.inEdges(cur)) {
        if (e.from === 0 || seen.has(e.from)) continue;
        seen.add(e.from);
        next.push(e.from);
        effectWatch.push(e.from);
        const it: ImpactItem = { from: e.from, kind: e.kind, span: undefined, transitive: true };
        if (!transitive.some((x) => x.from === it.from && x.kind === it.kind)) {
          transitive.push(it);
        }
      }
    }
    level = next;
    d++;
  }

  // 模块集合
  const mods = new Set<string>();
  for (const it of [...direct, ...transitive]) {
    const n = g.nodes.get(it.from);
    if (n !== undefined && n.module !== "") mods.add(n.module);
  }
  direct.sort((a, b) => a.from - b.from);
  transitive.sort((a, b) => a.from - b.from);
  effectWatch.sort((a, b) => a - b);
  return { symbol, direct, transitive, effectWatch, modules: [...mods].sort() };
}

/** 文件级依赖查询 */
export function graphFileDep(g: Graph, module: string): FileDep | undefined {
  if (g.files.length === 0) {
    const imports = g.fileImports.get(module);
    if (imports !== undefined) return { module, imports, dependsOn: [] };
    return undefined;
  }
  const imports = g.fileImports.get(module);
  const hasIt = imports !== undefined || g.files.includes(module);
  if (hasIt) {
    const deps: string[] = [];
    for (const d of g.fileDependsOn.get(module) ?? []) {
      if (!deps.includes(d)) deps.push(d);
    }
    return { module, imports: imports ?? [], dependsOn: deps };
  }
  return undefined;
}

export type { Symbol };
