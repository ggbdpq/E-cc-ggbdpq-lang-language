// Agent Contract 契约层（v0.4）：编译器内部模型 → 稳定 JSON 协议的唯一转换边界。
// 规则：输出 deterministic（显式排序）；schemaVersion "1" 内字段只加不改不删；
// JSON 形态与 Go encoding/json 对齐——字段按结构体序、omitempty 省空、HTML 转义开启。

import type { Span, Position } from "../syntax/span.js";
import type { Diagnostic } from "../common/diagnostic.js";
import type { Stmt, Expr, Pat, TypeEx } from "../syntax/ast.js";
import type { ResolveResult } from "../semantic/resolver.js";
import type { Symbol, SymbolId } from "../semantic/symbols.js";
import type { CheckResult } from "../types/checker.js";
import type { HirProg, HirExpr, HirStmt, HirPat } from "../hir/model.js";
import type { IrProg, IrExpr, IrStmt, IrPat } from "../ir/model.js";
import { typeStr } from "../types/model.js";
import { codePointCompare, goFormatFloat } from "../interp/value.js";
import { walkStmts, walkExpr } from "../syntax/walk.js";
import { ruleOf } from "./rules.js";
import { graphEffects, type GraphEffect } from "../graph/effects.js";

import { version as toolVersion } from "../version/version.js";

/** 契约版本（破坏性变更才升位） */
export const SchemaVersion = "1";
/** 工具版本（与 CLI version 同源） */
export const ToolVersion = toolVersion;

// Kind 输出类别
export const KindInspectSymbols = "inspect.symbols";
export const KindInspectRefs = "inspect.refs";
export const KindInspectType = "inspect.type";
export const KindInspectDependencies = "inspect.dependencies";
export const KindInspectEffects = "inspect.effects";
export const KindInspectCallers = "inspect.callers";
export const KindInspectCallees = "inspect.callees";
export const KindInspectImpact = "inspect.impact";
export const KindInspectDependency = "inspect.dependency";
export const KindDumpAST = "dump.ast";
export const KindDumpHIR = "dump.hir";
export const KindDumpIR = "dump.ir";
export const KindCheck = "check";

// —— JSON 形态（键序 = Go 结构体序；undefined = Go omitempty 省略） ——

export interface JPos {
  readonly offset: number;
  readonly line: number;
  readonly column: number;
}

export interface JSpan {
  readonly file: string;
  readonly start: JPos;
  readonly end: JPos;
}

export interface JNode {
  readonly nodeId: number;
  readonly kind: string;
  readonly span?: JSpan | undefined | null;
  readonly type?: string | undefined;
  readonly name?: string | undefined;
  readonly value?: string | undefined;
  readonly children?: JNode[] | undefined;
}

export interface JDiagnostic {
  readonly code: string;
  readonly ruleId?: string;
  readonly severity: string;
  readonly message: string;
  readonly primarySpan: JSpan | null;
  readonly relatedSpans: JSpan[];
  readonly help: string;
}

/** syntax.Span → 契约 Span；未盖章节点（零值）→ null */
export function spanJSON(sp: Span | undefined): JSpan | null {
  if (sp === undefined) return null;
  if (sp.file === "" && sp.start.offset === 0 && sp.end.offset === 0) return null;
  return {
    file: sp.file,
    start: { offset: sp.start.offset, line: sp.start.line, column: sp.start.col },
    end: { offset: sp.end.offset, line: sp.end.line, column: sp.end.col },
  };
}

const lineSpan = (line: number): JSpan => {
  const l = line < 1 ? 0 : line;
  return {
    file: "",
    start: { offset: 0, line: l, column: 1 },
    end: { offset: 0, line: l, column: 1 },
  };
};

export const diagJSON = (d: Diagnostic): JDiagnostic => ({
  code: d.code,
  ...(ruleOf(d.code) !== "" ? { ruleId: ruleOf(d.code) } : {}),
  severity: "error",
  message: d.msg,
  primarySpan: lineSpan(d.line),
  relatedSpans: [],
  help: "",
});

// —— Envelope ——

export interface Envelope {
  readonly schemaVersion: string;
  readonly toolVersion: string;
  readonly kind: string;
  readonly success?: boolean;
  readonly error?: string;
  readonly diagnostics?: readonly JDiagnostic[];
  readonly symbols?: readonly unknown[];
  readonly references?: readonly unknown[];
  readonly dependencies?: readonly unknown[];
  readonly effects?: readonly unknown[];
  readonly nodes?: readonly JNode[];
  readonly callers?: readonly unknown[];
  readonly callees?: readonly unknown[];
  readonly impact?: unknown;
  readonly dependency?: unknown;
}

export const errorEnvelope = (kind: string, msg: string): Envelope => ({
  schemaVersion: SchemaVersion,
  toolVersion: ToolVersion,
  kind,
  error: msg,
});

export const dumpEnvelope = (kind: string, nodes: readonly JNode[]): Envelope => ({
  schemaVersion: SchemaVersion,
  toolVersion: ToolVersion,
  kind,
  nodes,
});

/** Go encoding/json 兼容输出：两空格缩进 + HTML 转义 */
export function emitJSON(v: unknown): string {
  return JSON.stringify(v, null, 2)
    .replace(/</g, "\\u003c")
    .replace(/>/g, "\\u003e")
    .replace(/&/g, "\\u0026")
    .replace(/\u2028/g, "\\u2028")
    .replace(/\u2029/g, "\\u2029");
}

// —— check ——

export interface CheckJSON {
  readonly schemaVersion: string;
  readonly toolVersion: string;
  readonly kind: string;
  readonly success: boolean;
  readonly diagnostics: readonly JDiagnostic[];
}

export const checkStmtsJSON = (diags: readonly Diagnostic[]): CheckJSON => ({
  schemaVersion: SchemaVersion,
  toolVersion: ToolVersion,
  kind: KindCheck,
  success: diags.length === 0,
  diagnostics: diags.map(diagJSON),
});

// —— dump ast ——

class AstBuilder {
  private id = 0;
  private next(): number {
    this.id++;
    return this.id;
  }

  constructor(
    private readonly res: ResolveResult,
    private readonly ty: CheckResult | undefined,
  ) {}

  private node(sp: Span, kind: string, extra: Partial<JNode> = {}): JNode {
    return { nodeId: this.next(), kind, ...(spanJSON(sp) !== null ? { span: spanJSON(sp)! } : {}), ...extra };
  }

  private typ(e: Expr): string | undefined {
    if (this.ty === undefined) return undefined;
    const t = this.ty.exprTy.get(e);
    return t !== undefined ? typeStr(t) : undefined;
  }

  private symTypeOf(sym: Symbol | undefined): string | undefined {
    if (this.ty === undefined || sym === undefined) return undefined;
    const t = this.ty.symTy.get(sym.id);
    return t !== undefined ? typeStr(t) : undefined;
  }

  private letSymType(st: Extract<Stmt, { kind: "let_stmt" }>): string | undefined {
    if (this.ty === undefined) return undefined;
    return this.symTypeOf(this.res.declSyms.get(st));
  }

  private forVarType(st: Extract<Stmt, { kind: "for_in" }>): string | undefined {
    if (this.ty === undefined) return undefined;
    return this.symTypeOf(this.res.declSyms.get(st));
  }

  stmts(list: readonly Stmt[]): JNode[] {
    return list.map((st) => this.stmt(st));
  }

  stmt(st: Stmt): JNode {
    switch (st.kind) {
      case "let_stmt": {
        const lt = this.letSymType(st);
        const n = this.node(st.span, st.mutable ? "var" : "let", { name: st.name, ...(lt !== undefined ? { type: lt } : {}) });
        return { ...n, children: [this.expr(st.init)] };
      }
      case "assign":
        return {
          ...this.node(st.span, "assign", { name: st.op }),
          children: [this.expr(st.target), this.expr(st.val)],
        };
      case "expr_stmt":
        return { ...this.node(st.span, "eval"), children: [this.expr(st.x)] };
      case "return": {
        const n = this.node(st.span, "return");
        return st.val !== undefined ? { ...n, children: [this.expr(st.val)] } : n;
      }
      case "if": {
        const n = this.node(st.span, "if");
        const children: JNode[] = [this.expr(st.cond), ...this.stmts(st.then)];
        if (st.els !== undefined && st.els.length > 0) {
          const els: JNode = { nodeId: this.next(), kind: "else", children: this.stmts(st.els) };
          children.push(els);
        }
        return { ...n, children };
      }
      case "while": {
        const n = this.node(st.span, "while");
        return { ...n, children: [this.expr(st.cond), ...this.stmts(st.body)] };
      }
      case "for_in": {
        const vt = this.forVarType(st);
        const n = this.node(st.span, "forin", { name: st.variable, ...(vt !== undefined ? { type: vt } : {}) });
        return { ...n, children: [this.expr(st.iter), ...this.stmts(st.body)] };
      }
      case "break":
        return this.node(st.span, "break");
      case "continue":
        return this.node(st.span, "continue");
      case "fn_decl": {
        const n = this.node(st.span, "fn", {
          name: st.recv !== undefined ? `(${st.recv.name}) ${st.name}` : st.name,
        });
        const children: JNode[] = st.params.map((p) => this.param(p));
        children.push(...this.stmts(st.body));
        return { ...n, children };
      }
      case "struct_decl": {
        const n = this.node(st.span, "struct", { name: st.name });
        const children: JNode[] = st.fields.map((f) =>
          this.node(st.span, "field", { name: f.name, ...(f.typ !== undefined ? { type: annoType(f.typ) } : {}) }),
        );
        return { ...n, children };
      }
      case "enum_decl": {
        const n = this.node(st.span, "enum", { name: st.name });
        const children: JNode[] = st.cases.map((c) => {
          const cn = this.node(c.span ?? st.span, "case", { name: c.name });
          const payloads: JNode[] = c.payloads.map((pt, i) =>
            this.node(c.span ?? st.span, "payload", { name: String(i), ...(pt !== undefined ? { type: annoType(pt) } : {}) }),
          );
          return payloads.length > 0 ? { ...cn, children: payloads } : cn;
        });
        return { ...n, children };
      }
      case "extern": {
        const n = this.node(st.span, "extern", { name: st.host });
        const children: JNode[] = st.fns.map((fn) => {
          const fnN = this.node(fn.span, "fn", { name: fn.name });
          return { ...fnN, children: fn.params.map((p) => this.param(p)) };
        });
        return { ...n, children };
      }
      case "go":
        return { ...this.node(st.span, "spawn"), children: [this.expr(st.call)] };
      case "import":
        return this.node(st.span, "import", { name: st.path });
      default: {
        const _never: never = st;
        return _never;
      }
    }
  }

  private param(p: { name: string; typ: TypeEx | undefined }): JNode {
    return {
      nodeId: this.next(),
      kind: "param",
      name: p.name,
      ...(p.typ !== undefined ? { type: annoType(p.typ) } : {}),
    };
  }

  expr(e: Expr): JNode {
    switch (e.kind) {
      case "int_lit":
        return this.node(e.span, "int_lit", { value: e.value.toString(10), ...(optType(this.typ(e))) });
      case "float_lit":
        return this.node(e.span, "float_lit", { value: goFormatFloat(e.value), ...(optType(this.typ(e))) });
      case "str_lit":
        return this.node(e.span, "str_lit", { value: e.value, ...(optType(this.typ(e))) });
      case "bool_lit":
        return this.node(e.span, "bool_lit", { value: e.value ? "true" : "false", ...(optType(this.typ(e))) });
      case "ident":
        return this.node(e.span, "ident", { name: e.name, ...(optType(this.typ(e))) });
      case "unary":
        return {
          ...this.node(e.span, "unary", { name: e.op, ...(optType(this.typ(e))) }),
          children: [this.expr(e.x)],
        };
      case "binary":
        return {
          ...this.node(e.span, "binary", { name: e.op, ...(optType(this.typ(e))) }),
          children: [this.expr(e.l), this.expr(e.r)],
        };
      case "range":
        return {
          ...this.node(e.span, "range", optType(this.typ(e))),
          children: [this.expr(e.lo), this.expr(e.hi)],
        };
      case "call": {
        const n = this.node(e.span, "call", optType(this.typ(e)));
        return { ...n, children: [this.expr(e.callee), ...e.args.map((a) => this.expr(a))] };
      }
      case "index":
        return {
          ...this.node(e.span, "index", optType(this.typ(e))),
          children: [this.expr(e.obj), this.expr(e.idx)],
        };
      case "slice":
        return {
          ...this.node(e.span, "slice", optType(this.typ(e))),
          children: [this.expr(e.obj), this.expr(e.lo), this.expr(e.hi)],
        };
      case "field":
        return {
          ...this.node(e.span, "field_access", { name: e.name, ...(optType(this.typ(e))) }),
          children: [this.expr(e.obj)],
        };
      case "try":
        return {
          ...this.node(e.span, "try", optType(this.typ(e))),
          children: [this.expr(e.x)],
        };
      case "struct_lit": {
        const n = this.node(e.span, "struct_lit", { name: e.typeName, ...(optType(this.typ(e))) });
        const children: JNode[] = e.fields.map((f) => ({
          nodeId: this.next(),
          kind: "field_init",
          name: f.name,
          children: [this.expr(f.val)],
        }));
        return { ...n, children };
      }
      case "map_lit": {
        const n = this.node(e.span, "map_lit", optType(this.typ(e)));
        const children: JNode[] = e.items.map((it) => ({
          nodeId: this.next(),
          kind: "item",
          children: [this.expr(it.key), this.expr(it.val)],
        }));
        return { ...n, children };
      }
      case "arr_lit": {
        const n = this.node(e.span, "arr_lit", optType(this.typ(e)));
        return { ...n, children: e.items.map((el) => this.expr(el)) };
      }
      case "match": {
        const n = this.node(e.span, "match", optType(this.typ(e)));
        const children: JNode[] = [this.expr(e.scrut)];
        for (const arm of e.arms) {
          children.push({
            nodeId: this.next(),
            kind: "arm",
            children: [this.pat(arm.pat), this.expr(arm.body)],
          });
        }
        return { ...n, children };
      }
      case "fn_lit": {
        const n = this.node(e.span, "fnlit", optType(this.typ(e)));
        const children: JNode[] = e.params.map((p) => this.param(p));
        children.push(...this.stmts(e.body));
        return { ...n, children };
      }
      default: {
        const _never: never = e;
        return _never;
      }
    }
  }

  pat(p: Pat): JNode {
    switch (p.kind) {
      case "wild_pat":
        return this.node(p.span, "pattern_wild");
      case "bind_pat":
        return this.node(p.span, "pattern_bind", { name: p.name });
      case "lit_pat":
        return { ...this.node(p.span, "pattern_lit"), children: [this.expr(p.val)] };
      case "case_pat": {
        const n = this.node(p.span, "pattern_ctor", { name: p.name });
        return p.subs.length > 0 ? { ...n, children: p.subs.map((sp) => this.pat(sp)) } : n;
      }
      default: {
        const _never: never = p;
        return _never;
      }
    }
  }
}

const optType = (t: string | undefined): { type: string } | Record<string, never> =>
  t !== undefined && t !== "" ? { type: t } : {};

function annoType(_t: TypeEx): string {
  return "annotated";
}

/** 整棵 AST → 契约节点（ty 可为 undefined：仅 dump 语法树时不标类型） */
export function astJSON(prog: readonly Stmt[], res: ResolveResult, ty: CheckResult | undefined): JNode[] {
  return new AstBuilder(res, ty).stmts(prog);
}

// —— dump hir ——

const hirNodeOf = (
  meta: { id: number; span: Span; typ: import("../types/model.js").Type },
  kind: string,
  extra: Partial<JNode> = {},
): JNode => ({
  nodeId: meta.id,
  kind,
  ...(spanJSON(meta.span) !== null ? { span: spanJSON(meta.span)! } : {}),
  type: typeStr(meta.typ),
  ...extra,
});

export function hirJSON(p: HirProg): JNode[] {
  const out: JNode[] = [];
  for (const s of p.structs) {
    const n: JNode = { nodeId: s.id, kind: "struct", ...(spanJSON(s.span) !== null ? { span: spanJSON(s.span)! } : {}), name: s.name };
    const children: JNode[] = s.fields.map((f) => ({
      nodeId: s.id,
      kind: "field",
      name: f.name,
      type: typeStr(f.typ),
    }));
    out.push(children.length > 0 ? { ...n, children } : n);
  }
  for (const e of p.enums) {
    const n: JNode = { nodeId: e.id, kind: "enum", ...(spanJSON(e.span) !== null ? { span: spanJSON(e.span)! } : {}), name: e.name };
    const children: JNode[] = e.cases.map((c) => ({ nodeId: e.id, kind: "case", name: c.name }));
    out.push(children.length > 0 ? { ...n, children } : n);
  }
  for (const g of p.globals) out.push(hirStmtJSON(g));
  for (const f of p.fns) out.push(hirFnJSON(f));
  return out;
}

function hirFnJSON(f: HirProg["fns"][number]): JNode {
  const n = hirNodeOf(f, "fn", { name: f.sym !== undefined ? f.sym.name : f.name });
  const children: JNode[] = [];
  if (f.recv !== undefined) {
    children.push({ ...hirNodeOf(f, "recv"), name: f.recv.name, type: typeStr(f.recv.typ) });
  }
  for (const p of f.params) {
    children.push({ ...hirNodeOf(f, "param"), name: p.name, type: typeStr(p.typ) });
  }
  children.push(...f.body.map(hirStmtJSON));
  return { ...n, children };
}

function hirStmtJSON(st: HirStmt): JNode {
  switch (st.kind) {
    case "let": {
      const n = hirNodeOf(st, st.mutable ? "var" : "let", { name: st.name });
      return { ...n, children: [hirExprJSON(st.init)] };
    }
    case "assign":
      return {
        ...hirNodeOf(st, "assign", { name: st.op }),
        children: [hirExprJSON(st.target), hirExprJSON(st.rhs)],
      };
    case "eval":
      return { ...hirNodeOf(st, "eval"), children: [hirExprJSON(st.x)] };
    case "return": {
      const n = hirNodeOf(st, "return");
      return st.val !== undefined ? { ...n, children: [hirExprJSON(st.val)] } : n;
    }
    case "if": {
      const n = hirNodeOf(st, "if");
      const children: JNode[] = [hirExprJSON(st.cond), ...st.then.map(hirStmtJSON)];
      if (st.els.length > 0) {
        children.push({ nodeId: st.id, kind: "else", ...(spanJSON(st.span) !== null ? { span: spanJSON(st.span)! } : {}), children: st.els.map(hirStmtJSON) });
      }
      return { ...n, children };
    }
    case "while": {
      const n = hirNodeOf(st, "while");
      return { ...n, children: [hirExprJSON(st.cond), ...st.body.map(hirStmtJSON)] };
    }
    case "for_in": {
      const n = hirNodeOf(st, "forin", { name: st.variable, type: typeStr(st.varType) });
      return { ...n, children: [hirExprJSON(st.iter), ...st.body.map(hirStmtJSON)] };
    }
    case "break":
      return hirNodeOf(st, "break");
    case "continue":
      return hirNodeOf(st, "continue");
    case "spawn": {
      const n = hirNodeOf(st, "spawn", { name: st.fn !== undefined ? st.fn.name : "" });
      return { ...n, children: st.args.map(hirExprJSON) };
    }
    case "fn_stmt":
      return hirFnJSON(st.fn);
    default: {
      const _never: never = st;
      return _never;
    }
  }
}

function hirExprJSON(e: HirExpr): JNode {
  switch (e.kind) {
    case "lit": {
      switch (e.litKind) {
        case "int":
          return hirNodeOf(e, "int_lit", { value: e.i64.toString(10) });
        case "float":
          return hirNodeOf(e, "float_lit", { value: goFormatFloat(e.f64) });
        case "str":
          return hirNodeOf(e, "str_lit", { value: e.s });
        case "bool":
          return hirNodeOf(e, "bool_lit", { value: e.b ? "true" : "false" });
        default: {
          const _never: never = e.litKind;
          return _never;
        }
      }
    }
    case "local_ref":
      return hirNodeOf(e, "local", { name: e.name });
    case "fn_ref":
      return hirNodeOf(e, "fnref", { name: e.name });
    case "builtin_ref":
      return hirNodeOf(e, "builtin_ref", { name: e.name });
    case "ctor": {
      const n = hirNodeOf(e, "ctor", { name: e.ctorSym.name });
      return e.args.length > 0 ? { ...n, children: e.args.map(hirExprJSON) } : n;
    }
    case "call": {
      let n: JNode = hirNodeOf(e, "call");
      const children: JNode[] = [];
      if (e.fn !== undefined) {
        n = { ...n, name: e.fn.name };
      } else if (e.builtin !== undefined && e.builtin !== "") {
        n = { ...n, kind: "builtin_call", name: e.builtin };
      } else {
        n = { ...n, kind: "call_value" };
        if (e.callee !== undefined) children.push(hirExprJSON(e.callee));
      }
      children.push(...e.args.map(hirExprJSON));
      return children.length > 0 ? { ...n, children } : n;
    }
    case "method_call": {
      let n: JNode;
      const children: JNode[] = [];
      if (e.method !== undefined) {
        n = hirNodeOf(e, "method", { name: e.method.name });
        children.push(hirExprJSON(e.recv));
      } else {
        n = hirNodeOf(e, "call_field", { name: e.field ?? "" });
        children.push(hirExprJSON(e.recv));
      }
      children.push(...e.args.map(hirExprJSON));
      return { ...n, children };
    }
    case "unary":
      return { ...hirNodeOf(e, "unary", { name: e.op }), children: [hirExprJSON(e.x)] };
    case "binary":
      return { ...hirNodeOf(e, "binary", { name: e.op }), children: [hirExprJSON(e.l), hirExprJSON(e.r)] };
    case "and_then":
      return { ...hirNodeOf(e, "and"), children: [hirExprJSON(e.l), hirExprJSON(e.r)] };
    case "or_else":
      return { ...hirNodeOf(e, "or"), children: [hirExprJSON(e.l), hirExprJSON(e.r)] };
    case "range":
      return { ...hirNodeOf(e, "range"), children: [hirExprJSON(e.lo), hirExprJSON(e.hi)] };
    case "index":
      return { ...hirNodeOf(e, "index"), children: [hirExprJSON(e.obj), hirExprJSON(e.idx)] };
    case "slice":
      return { ...hirNodeOf(e, "slice"), children: [hirExprJSON(e.obj), hirExprJSON(e.lo), hirExprJSON(e.hi)] };
    case "field":
      return { ...hirNodeOf(e, "field_access", { name: e.name }), children: [hirExprJSON(e.obj)] };
    case "try":
      return { ...hirNodeOf(e, "try"), children: [hirExprJSON(e.x)] };
    case "struct_lit": {
      const n = hirNodeOf(e, "struct_lit", { name: e.sym !== undefined ? e.sym.name : "" });
      const children: JNode[] = e.fields.map((f) => ({
        nodeId: e.id,
        kind: "field_init",
        name: f.name,
        children: [hirExprJSON(f.val)],
      }));
      return { ...n, children };
    }
    case "map_lit": {
      const n = hirNodeOf(e, "map_lit");
      const children: JNode[] = e.keys.map((k, i) => ({
        nodeId: e.id,
        kind: "item",
        children: [hirExprJSON(k), hirExprJSON(e.vals[i]!)],
      }));
      return { ...n, children };
    }
    case "slice_lit": {
      const n = hirNodeOf(e, "arr_lit");
      const children = e.elems.map(hirExprJSON);
      return children.length > 0 ? { ...n, children } : n;
    }
    case "match": {
      const kindName =
        e.matchKind === "enum" ? "enum" : e.matchKind === "result" ? "result" : e.matchKind === "option" ? "option" : "literal";
      const n = hirNodeOf(e, "match", { name: kindName });
      const children: JNode[] = [hirExprJSON(e.scrut)];
      for (const arm of e.arms) {
        children.push({ nodeId: e.id, kind: "arm", children: [hirPatJSON(arm.pat), hirExprJSON(arm.body)] });
      }
      return { ...n, children };
    }
    case "fn_lit": {
      const n = hirNodeOf(e, "fnlit");
      const children: JNode[] = e.params.map((p) => ({ ...hirNodeOf(e, "param"), name: p.name, type: typeStr(p.typ) }));
      children.push(...e.body.map(hirStmtJSON));
      return { ...n, children };
    }
    default: {
      const _never: never = e;
      return _never;
    }
  }
}

function hirPatJSON(p: HirPat): JNode {
  switch (p.kind) {
    case "wild_pat":
      return { kind: "pattern_wild" } as JNode;
    case "bind_pat":
      return { kind: "pattern_bind", name: p.name, type: typeStr(p.typ) } as JNode;
    case "lit_pat":
      return { kind: "pattern_lit", children: [hirExprJSON(p.val)] } as JNode;
    case "ctor_pat": {
      const n = { kind: "pattern_ctor", name: p.ctor !== undefined ? p.ctor.name : "" } as JNode;
      return p.subs.length > 0 ? { ...n, children: p.subs.map(hirPatJSON) } : n;
    }
    default: {
      const _never: never = p;
      return _never;
    }
  }
}

// —— dump ir ——

class IrBuilder {
  private id = 0;
  private next(): number {
    this.id++;
    return this.id;
  }

  build(p: IrProg): JNode[] {
    const out: JNode[] = [];
    for (const s of p.structs) {
      const n: JNode = { nodeId: this.next(), kind: "struct", name: s.name };
      const children: JNode[] = s.fields.map((f) => ({
        nodeId: this.next(),
        kind: "field",
        name: f.name,
        type: typeStr(f.typ),
      }));
      out.push({ ...n, children });
    }
    for (const e of p.enums) {
      const n: JNode = { nodeId: this.next(), kind: "enum", name: e.name };
      const children: JNode[] = e.cases.map((c) => ({ nodeId: this.next(), kind: "case", name: c.name }));
      out.push({ ...n, children });
    }
    for (const g of p.globals) {
      const n: JNode = {
        nodeId: this.next(),
        kind: g.local.mutable ? "var" : "let",
        name: g.local.name,
        type: typeStr(g.local.typ),
      };
      out.push({ ...n, children: [this.expr(g.init)] });
    }
    for (const f of p.fns) out.push(this.fn(f));
    return out;
  }

  private fn(f: IrFnShaped): JNode {
    const n: JNode = { nodeId: this.next(), kind: "fn", name: f.name, type: f.ret !== undefined ? typeStr(f.ret) : typeStr(undefined) };
    const children: JNode[] = [];
    if (f.recv !== undefined) {
      children.push({ nodeId: this.next(), kind: "recv", name: f.recv.name, type: typeStr(f.recv.typ) });
    }
    for (const p of f.params) {
      children.push({ nodeId: this.next(), kind: "param", name: p.name, type: typeStr(p.typ) });
    }
    children.push(...f.body.map((s) => this.stmt(s)));
    return { ...n, children };
  }

  private stmt(st: IrStmt): JNode {
    switch (st.kind) {
      case "let": {
        const n: JNode = {
          nodeId: this.next(),
          kind: st.local.mutable ? "var" : "let",
          name: st.local.name,
          type: typeStr(st.local.typ),
        };
        return { ...n, children: [this.expr(st.init)] };
      }
      case "assign":
        return {
          nodeId: this.next(),
          kind: "assign",
          name: st.op,
          children: [this.expr(st.target), this.expr(st.val)],
        };
      case "eval":
        return { nodeId: this.next(), kind: "eval", children: [this.expr(st.x)] };
      case "return": {
        const n: JNode = { nodeId: this.next(), kind: "return" };
        return st.val !== undefined ? { ...n, children: [this.expr(st.val)] } : n;
      }
      case "if": {
        const n: JNode = { nodeId: this.next(), kind: "if" };
        const children: JNode[] = [this.expr(st.cond), ...st.then.map((s) => this.stmt(s))];
        if (st.els.length > 0) {
          children.push({ nodeId: this.next(), kind: "else", children: st.els.map((s) => this.stmt(s)) });
        }
        return { ...n, children };
      }
      case "while": {
        const n: JNode = { nodeId: this.next(), kind: "while" };
        return { ...n, children: [this.expr(st.cond), ...st.body.map((s) => this.stmt(s))] };
      }
      case "for_in": {
        const n: JNode = { nodeId: this.next(), kind: "forin", name: st.variable.name, type: typeStr(st.variable.typ) };
        return { ...n, children: [this.expr(st.iter), ...st.body.map((s) => this.stmt(s))] };
      }
      case "break":
        return { nodeId: this.next(), kind: "break" };
      case "continue":
        return { nodeId: this.next(), kind: "continue" };
      case "spawn": {
        const n: JNode = { nodeId: this.next(), kind: "spawn", name: st.fn !== undefined ? st.fn.name : "" };
        return st.args.length > 0 ? { ...n, children: st.args.map((a) => this.expr(a)) } : n;
      }
      case "fn_def":
        return this.fn(st.fn);
      default: {
        const _never: never = st;
        return _never;
      }
    }
  }

  private expr(e: IrExpr): JNode {
    switch (e.kind) {
      case "const": {
        let value: string | undefined;
        switch (e.constKind) {
          case "int":
            value = e.i64.toString(10);
            break;
          case "float":
            value = goFormatFloat(e.f64);
            break;
          case "str":
            value = e.s;
            break;
          case "bool":
            value = e.b ? "true" : "false";
            break;
        }
        return { nodeId: this.next(), kind: "const", type: typeStr(e.typ), value };
      }
      case "local_get":
        return { nodeId: this.next(), kind: "local", name: e.local.name, type: typeStr(e.typ) };
      case "fn_get":
        return { nodeId: this.next(), kind: "fnref", name: e.name, type: typeStr(e.typ) };
      case "builtin_get":
        return { nodeId: this.next(), kind: "builtin_ref", name: e.name };
      case "ctor": {
        const n: JNode = { nodeId: this.next(), kind: "ctor", name: e.sym.name, type: typeStr(e.typ) };
        return e.args.length > 0 ? { ...n, children: e.args.map((a) => this.expr(a)) } : n;
      }
      case "call": {
        let n: JNode = { nodeId: this.next(), kind: "call", type: typeStr(e.typ) };
        const children: JNode[] = [];
        if (e.fn !== undefined) {
          n = { ...n, name: e.fn.name };
        } else if (e.builtin !== undefined && e.builtin !== "") {
          n = { ...n, kind: "builtin_call", name: e.builtin };
        } else if (e.method !== undefined) {
          n = { ...n, kind: "method", name: e.method.name };
          children.push(this.expr(e.recv!));
        } else {
          n = { ...n, kind: "call_value" };
          if (e.callee !== undefined) children.push(this.expr(e.callee));
        }
        children.push(...e.args.map((a) => this.expr(a)));
        return children.length > 0 ? { ...n, children } : n;
      }
      case "unary":
        return { ...{ nodeId: this.next(), kind: "unary", name: e.op, type: typeStr(e.typ) }, children: [this.expr(e.x)] };
      case "binary":
        return { ...{ nodeId: this.next(), kind: "binary", name: e.op, type: typeStr(e.typ) }, children: [this.expr(e.l), this.expr(e.r)] };
      case "range":
        return { ...{ nodeId: this.next(), kind: "range", type: typeStr(e.typ) }, children: [this.expr(e.lo), this.expr(e.hi)] };
      case "index":
        return { ...{ nodeId: this.next(), kind: "index", type: typeStr(e.typ) }, children: [this.expr(e.obj), this.expr(e.idx)] };
      case "slice":
        return { ...{ nodeId: this.next(), kind: "slice", type: typeStr(e.typ) }, children: [this.expr(e.obj), this.expr(e.lo), this.expr(e.hi)] };
      case "field":
        return { ...{ nodeId: this.next(), kind: "field_access", name: e.name, type: typeStr(e.typ) }, children: [this.expr(e.obj)] };
      case "try":
        return { ...{ nodeId: this.next(), kind: "try", type: typeStr(e.typ) }, children: [this.expr(e.x)] };
      case "struct_lit": {
        const n: JNode = { nodeId: this.next(), kind: "struct_lit", name: e.struct !== undefined ? e.struct.name : "", type: typeStr(e.typ) };
        const children: JNode[] = e.fields.map((f) => ({
          nodeId: this.next(),
          kind: "field_init",
          name: f.name,
          children: [this.expr(f.val)],
        }));
        return { ...n, children };
      }
      case "map_lit": {
        const n: JNode = { nodeId: this.next(), kind: "map_lit", type: typeStr(e.typ) };
        const children: JNode[] = e.keys.map((k, i) => ({
          nodeId: this.next(),
          kind: "item",
          children: [this.expr(k), this.expr(e.vals[i]!)],
        }));
        return { ...n, children };
      }
      case "slice_lit": {
        const n: JNode = { nodeId: this.next(), kind: "arr_lit", type: typeStr(e.typ) };
        const children = e.elems.map((el) => this.expr(el));
        return children.length > 0 ? { ...n, children } : n;
      }
      case "match": {
        const kindName =
          e.matchKind === "enum" ? "enum" : e.matchKind === "result" ? "result" : e.matchKind === "option" ? "option" : "literal";
        const n: JNode = { nodeId: this.next(), kind: "match", name: kindName, type: typeStr(e.typ) };
        const children: JNode[] = [this.expr(e.scrut)];
        for (const arm of e.arms) {
          children.push({ nodeId: this.next(), kind: "arm", children: [this.pat(arm.pat), this.expr(arm.body)] });
        }
        return { ...n, children };
      }
      case "fn_lit": {
        const n: JNode = { nodeId: this.next(), kind: "fnlit", type: typeStr(e.typ) };
        const children: JNode[] = e.params.map((p) => ({
          nodeId: this.next(),
          kind: "param",
          name: p.name,
          type: typeStr(p.typ),
        }));
        children.push(...e.body.map((s) => this.stmt(s)));
        return { ...n, children };
      }
      default: {
        const _never: never = e;
        return _never;
      }
    }
  }

  private pat(p: IrPat): JNode {
    switch (p.kind) {
      case "wild_pat":
        return { nodeId: this.next(), kind: "pattern_wild" };
      case "bind_pat":
        return { nodeId: this.next(), kind: "pattern_bind", name: p.name, type: typeStr(p.typ) };
      case "lit_pat":
        return { nodeId: this.next(), kind: "pattern_lit", children: [this.expr(p.val)] };
      case "ctor_pat": {
        const n: JNode = { nodeId: this.next(), kind: "pattern_ctor", name: p.sym !== undefined ? p.sym.name : "" };
        return p.subs.length > 0 ? { ...n, children: p.subs.map((sp) => this.pat(sp)) } : n;
      }
      default: {
        const _never: never = p;
        return _never;
      }
    }
  }
}

type IrFnShaped = IrProg["fns"][number];

/** 整个 IR 程序 → 契约节点（契约层按先序编号，无 span） */
export function irJSON(p: IrProg): JNode[] {
  return new IrBuilder().build(p);
}

// —— inspect ——

export const kindStrOf = (k: string): string => k;

/** 符号清单（ID 序 = 声明/绑定序；预载符号不输出） */
export function symbolsJSON(prog: readonly Stmt[], res: ResolveResult, ty: CheckResult): unknown[] {
  // 反查绑定节点的定义 Span（var/for 变量）
  const stmtSpan = new Map<SymbolId, Span>();
  for (const [st, sym] of res.declSyms) {
    stmtSpan.set(sym.id, st.span);
  }
  const typeOf = (sym: Symbol): string => {
    const t = ty.symTy.get(sym.id);
    if (t !== undefined) return typeStr(t);
    const ft = ty.fnTy.get(sym.id);
    if (ft !== undefined) return typeStr(ft);
    return "";
  };
  const defSpan = (sym: Symbol): JSpan | null => {
    if (sym.decl !== undefined) return spanJSON(sym.decl.span);
    const sp = stmtSpan.get(sym.id);
    if (sp !== undefined) return spanJSON(sp);
    return null;
  };

  const out: unknown[] = [];
  for (const sym of res.symbols) {
    if (sym.builtinMark) continue;
    const visibility = sym.line > 0 && !sym.mutable && isTopLevel(sym) ? "public" : "local";
    const sp = defSpan(sym);
    const module = sp !== null ? sp.file : "";
    out.push({
      symbolId: sym.id,
      name: sym.name,
      kind: symKindStr(sym.kind),
      definition: sp,
      type: typeOf(sym),
      visibility,
      module,
    });
  }
  return out;
}

function isTopLevel(sym: Symbol): boolean {
  return sym.kind === "fn" || sym.kind === "method" || sym.kind === "struct" || sym.kind === "enum" || sym.kind === "ctor";
}

export const symKindStr = (k: string): string =>
  k === "fn" ? "function" : k === "method" ? "method" : k === "var" ? "variable" : k === "param" ? "parameter" : k === "struct" ? "struct" : k === "enum" ? "enum" : k === "ctor" ? "constructor" : "unknown";

/** 引用清单：read/write/call/pattern/type（排序 symbolId → file → offset） */
export function referencesJSON(prog: readonly Stmt[], res: ResolveResult, symbolId: number): unknown[] {
  const writes = new Set<Expr>();
  const calls = new Set<Expr>();
  // 预扫（walk 父先子后，与 Go passA 等价）：赋值目标集合（write）与调用位置集合（call）
  walkStmts(prog, {
    stmt: (st) => {
      if (st.kind === "assign" && st.target.kind === "ident") writes.add(st.target);
    },
  });
  walkStmts(prog, {
    expr: (e) => {
      if (e.kind === "call" && e.callee.kind === "ident") calls.add(e.callee);
    },
  });

  const out: { symbolId: number; span: JSpan | null; referenceKind: string }[] = [];
  const add = (sp: Span, sym: Symbol | undefined, kind: string): void => {
    if (sym === undefined) return;
    out.push({ symbolId: sym.id, span: spanJSON(sp), referenceKind: kind });
  };
  walkStmts(prog, {
    expr: (e) => {
      if (e.kind === "ident") {
        const sym = res.refs.get(e);
        if (sym !== undefined) {
          const kind = writes.has(e) ? "write" : calls.has(e) ? "call" : "read";
          add(e.span, sym, kind);
        }
      } else if (e.kind === "struct_lit") {
        const sym = res.refs.get(e);
        if (sym !== undefined) add(e.span, sym, "type");
      }
    },
    pat: (p) => {
      if (p.kind === "case_pat") {
        const sym = res.patRefs.get(p);
        if (sym !== undefined) add(p.span, sym, "pattern");
      }
    },
  });
  out.sort((a, b) => {
    if (a.symbolId !== b.symbolId) return a.symbolId - b.symbolId;
    const af = a.span?.file ?? "";
    const bf = b.span?.file ?? "";
    if (a.span === null || b.span === null) return a.span === null ? (b.span === null ? 0 : 1) : -1;
    if (af !== bf) return codePointCompare(af, bf);
    return a.span.start.offset - b.span.start.offset;
  });
  if (symbolId >= 0) return out.filter((r) => r.symbolId === symbolId);
  return out;
}

/** 行列定位最内层表达式节点；未命中返回 undefined */
export function typeAtJSON(
  prog: readonly Stmt[],
  res: ResolveResult,
  ty: CheckResult,
  file: string,
  line: number,
  col: number,
): JNode | undefined {
  let best: JNode | undefined;
  let bestSize = 0;
  let found = false;
  const consider = (e: Expr): void => {
    const s = e.span;
    if (file !== "" && s.file !== file) return;
    if (!containsPos(s, line, col)) return;
    const size = spanSize(s);
    if (!found || size < bestSize) {
      const ab = new AstBuilder(res, ty);
      best = ab.expr(e);
      found = true;
      bestSize = size;
    }
  };
  walkStmts(prog, { expr: consider });
  return found ? best : undefined;
}

function containsPos(s: Span, line: number, col: number): boolean {
  if (line < s.start.line || line > s.end.line) return false;
  if (line === s.start.line && col < s.start.col) return false;
  if (line === s.end.line && col > s.end.col) return false;
  return true;
}

function spanSize(s: Span): number {
  return (s.end.line - s.start.line) * 100000 + s.end.col - s.start.col;
}

// —— effects ——

export interface JEffect {
  readonly symbolId: number;
  readonly name: string;
  readonly effects: readonly string[];
}

/** 推导全部函数效果（按 SymbolID 排序，效果按固定序去重） */
export function effectsJSON(p: IrProg): JEffect[] {
  return graphEffects(p).map((e) => ({ symbolId: e.symbolId, name: e.name, effects: e.effects }));
}

export type { GraphEffect };
