// Builder：AST + resolver 产物 + checker 产物 → Typed HIR。
// 构建前提：semantic/types 两阶段均无诊断（调用方负责）。
// NodeID 先序递增；各构造点的 node() 调用时机按 Go 参考实现逐一复刻（ID 稳定性契约）。

import type {
  Expr,
  FnDecl,
  Pat,
  Stmt,
  StructDecl,
  EnumDecl,
} from "../syntax/ast.js";
import type { ResolveResult } from "../semantic/resolver.js";
import type { CheckResult } from "../types/checker.js";
import type { Symbol } from "../semantic/symbols.js";
import { Bool, Float, Int, String, Unknown, type Type } from "../types/model.js";

type RangeT = import("../types/model.js").Type;
import { resolveAnno } from "../types/anno.js";
import type {
  HirEnumDecl,
  HirExpr,
  HirFn,
  HirLet,
  HirPat,
  HirProg,
  HirStmt,
  HirStructDecl,
  NodeId,
} from "./model.js";

declare const nid: unique symbol;

const node = (id: number): NodeId => id as NodeId;

class Builder {
  private next = 0;
  /** enum 名 → case 符号 */
  private readonly ctors = new Map<string, Symbol[]>();
  private readonly structs: HirStructDecl[] = [];
  private readonly enums: HirEnumDecl[] = [];
  private readonly globals: HirLet[] = [];
  private readonly fns: HirFn[] = [];
  private main: HirFn | undefined;

  constructor(
    private readonly res: ResolveResult,
    private readonly ty: CheckResult,
  ) {}

  private n(span: import("../syntax/span.js").Span, line: number, sym: Symbol | undefined, t: Type | undefined): { id: NodeId; line: number; span: import("../syntax/span.js").Span; sym: Symbol | undefined; typ: Type } {
    this.next++;
    return { id: node(this.next), line, span, sym, typ: t ?? Unknown };
  }

  private anno(t: import("../syntax/ast.js").TypeEx | undefined, tparams: readonly string[]): Type {
    if (t === undefined) return Unknown;
    return resolveAnno(this.res, t, tparams);
  }

  build(prog: readonly Stmt[]): HirProg {
    for (const s of this.res.symbols) {
      if (s.kind === "ctor" && !s.builtinMark) {
        const list = this.ctors.get(s.structName) ?? [];
        list.push(s);
        this.ctors.set(s.structName, list);
      }
    }
    this.collectDecls(prog);
    return { structs: this.structs, enums: this.enums, globals: this.globals, fns: this.fns, main: this.main };
  }

  // —— 顶层收集 ——

  private collectDecls(prog: readonly Stmt[]): void {
    for (const s of prog) {
      if (s.kind === "struct_decl") this.structs.push(this.structDecl(s));
      else if (s.kind === "enum_decl") this.enums.push(this.enumDecl(s));
    }
    for (const s of prog) {
      if (s.kind === "let_stmt") this.globals.push(this.let(s));
    }
    for (const s of prog) {
      if (s.kind === "fn_decl") {
        if (s.recv !== undefined) continue;
        const fn = this.fnDecl(s, this.res.declSyms.get(s));
        if (s.name === "main" && this.main === undefined) this.main = fn;
        this.fns.push(fn);
      } else if (s.kind === "extern") {
        for (const f of s.fns) {
          const fn = this.fnDecl(f, this.externSym(f));
          this.fns.push(withExtern(fn, s.host));
        }
      }
    }
    for (const s of prog) {
      if (s.kind === "fn_decl" && s.recv !== undefined) {
        const st = this.recvStructSym(s);
        if (st !== undefined) this.fns.push(this.fnDecl(s, st.methods.get(s.name)));
      }
    }
  }

  private externSym(f: FnDecl): Symbol | undefined {
    for (const s of this.res.symbols) {
      if (s.kind === "fn" && s.extern && s.name === f.name) return s;
    }
    return undefined;
  }

  private recvStructSym(d: FnDecl): Symbol | undefined {
    const rt = d.recv?.typ;
    if (rt === undefined || rt.kind !== "named_type") return undefined;
    const b = this.res.builtin.get(rt.name);
    if (b !== undefined && b.kind === "struct") return b;
    return this.typeSymByName(rt.name);
  }

  private structDecl(d: StructDecl): HirStructDecl {
    const sym = this.typeSymByName(d.name);
    return {
      kind: "struct_decl",
      ...this.n(d.span, d.line, sym, undefined),
      name: d.name,
      typeParams: d.typeParams,
      fields: d.fields.map((f) => ({ name: f.name, typ: this.anno(f.typ, d.typeParams) })),
    };
  }

  private enumDecl(d: EnumDecl): HirEnumDecl {
    const sym = this.typeSymByName(d.name);
    return {
      kind: "enum_decl",
      ...this.n(d.span, d.line, sym, undefined),
      name: d.name,
      typeParams: d.typeParams,
      cases: d.cases.map((c) => {
        let csym: Symbol | undefined;
        for (const s of this.ctors.get(d.name) ?? []) {
          if (s.name === c.name) csym = s;
        }
        return { sym: csym, name: c.name, payloads: c.payloads.map((pt) => this.anno(pt, d.typeParams)) };
      }),
    };
  }

  private let(d: Extract<Stmt, { kind: "let_stmt" }>): HirLet {
    const sym = this.res.declSyms.get(d);
    return {
      kind: "let",
      ...this.n(d.span, d.line, sym, this.symType(sym)),
      name: d.name,
      mutable: d.mutable,
      init: this.expr(d.init),
    };
  }

  private fnDecl(d: FnDecl, sym: Symbol | undefined): HirFn {
    const ret = d.ret !== undefined ? this.anno(d.ret, d.typeParams) : undefined;
    const fn: {
      kind: "fn";
      id: NodeId;
      line: number;
      span: import("../syntax/span.js").Span;
      sym: Symbol | undefined;
      typ: Type;
      name: string;
      recv: { name: string; typ: Type } | undefined;
      typeParams: readonly string[];
      params: { name: string; typ: Type }[];
      ret: Type | undefined;
      body: HirStmt[];
      extern: boolean;
      externHost: string;
    } = {
      kind: "fn",
      ...this.n(d.span, d.line, sym, ret),
      name: d.name,
      recv: undefined,
      typeParams: d.typeParams,
      params: [],
      ret,
      body: [],
      extern: false,
      externHost: "",
    };
    if (d.recv !== undefined) {
      fn.recv = { name: d.recv.name, typ: this.anno(d.recv.typ, []) };
    }
    for (const p of d.params) fn.params.push({ name: p.name, typ: this.anno(p.typ, d.typeParams) });
    fn.body = d.body.map((s) => this.stmt(s));
    return fn;
  }

  // —— 语句 ——

  private stmt(s: Stmt): HirStmt {
    switch (s.kind) {
      case "let_stmt":
        return this.let(s);

      case "assign":
        return {
          kind: "assign",
          ...this.n(s.span, s.line, undefined, Unknown),
          target: this.assignTarget(s.target),
          op: s.op,
          rhs: this.expr(s.val),
        };

      case "expr_stmt": {
        const x = this.expr(s.x);
        return { kind: "eval", ...this.n(s.span, s.line, undefined, Unknown), x };
      }

      case "return": {
        // Go：node 先于 Val
        const n = this.n(s.span, s.line, undefined, Unknown);
        const val = s.val !== undefined ? this.expr(s.val) : undefined;
        return { kind: "return", ...n, val };
      }

      case "if": {
        const n = this.n(s.span, s.line, undefined, Unknown);
        const cond = this.expr(s.cond);
        const then = s.then.map((x) => this.stmt(x));
        const els = s.els !== undefined ? s.els.map((x) => this.stmt(x)) : [];
        return { kind: "if", ...n, cond, then, els };
      }

      case "while": {
        const n = this.n(s.span, s.line, undefined, Unknown);
        const cond = this.expr(s.cond);
        const body = s.body.map((x) => this.stmt(x));
        return { kind: "while", ...n, cond, body };
      }

      case "for_in": {
        const sym = this.res.declSyms.get(s);
        const n = this.n(s.span, s.line, sym, Unknown);
        const iter = this.expr(s.iter);
        const body = s.body.map((x) => this.stmt(x));
        return {
          kind: "for_in",
          ...n,
          variable: s.variable,
          varType: this.symType(sym),
          iter,
          body,
        };
      }

      case "break":
        return { kind: "break", ...this.n(s.span, s.line, undefined, Unknown) };
      case "continue":
        return { kind: "continue", ...this.n(s.span, s.line, undefined, Unknown) };

      case "go": {
        const n = this.n(s.span, s.line, undefined, Unknown);
        let fn: Symbol | undefined;
        if (s.call.callee.kind === "ident") fn = this.res.refs.get(s.call.callee);
        const args = s.call.args.map((a) => this.expr(a));
        return { kind: "spawn", ...n, fn, args };
      }

      case "fn_decl":
        return {
          kind: "fn_stmt",
          ...this.n(s.span, s.line, undefined, Unknown),
          fn: this.fnDecl(s, this.res.declSyms.get(s)),
        };

      case "struct_decl":
      case "enum_decl":
      case "extern":
      case "import":
        // 不可达：顶层声明由 collectDecls 处理，函数体内不出现
        throw new Error(`HIR.stmt: 未预期的语句 ${s.kind}`);

      default: {
        const _never: never = s;
        return _never;
      }
    }
  }

  private assignTarget(t: Expr): HirExpr {
    switch (t.kind) {
      case "ident": {
        const sym = this.res.refs.get(t);
        return {
          kind: "local_ref",
          ...this.n(t.span, t.line, sym, this.symType(sym)),
          name: t.name,
        };
      }
      case "field":
        return {
          kind: "field",
          ...this.n(t.span, t.line, undefined, Unknown),
          obj: this.expr(t.obj),
          name: t.name,
        };
      case "index":
        return {
          kind: "index",
          ...this.n(t.span, t.line, undefined, Unknown),
          obj: this.expr(t.obj),
          idx: this.expr(t.idx),
        };
      default:
        throw new Error("HIR.assignTarget: 未预期的赋值目标");
    }
  }

  // —— 表达式 ——

  private expr(e: Expr): HirExpr {
    switch (e.kind) {
      case "int_lit":
        return { kind: "lit", ...this.n(e.span, e.line, undefined, Int), litKind: "int", i64: e.value, f64: 0, s: "", b: false };
      case "float_lit":
        return { kind: "lit", ...this.n(e.span, e.line, undefined, Float), litKind: "float", i64: 0n, f64: e.value, s: "", b: false };
      case "str_lit":
        return { kind: "lit", ...this.n(e.span, e.line, undefined, String), litKind: "str", i64: 0n, f64: 0, s: e.value, b: false };
      case "bool_lit":
        return { kind: "lit", ...this.n(e.span, e.line, undefined, Bool), litKind: "bool", i64: 0n, f64: 0, s: "", b: e.value };

      case "ident":
        return this.ident(e);

      case "unary": {
        const n = this.n(e.span, e.line, undefined, this.exprType(e));
        const x = this.expr(e.x);
        return { kind: "unary", ...n, op: e.op, x };
      }

      case "binary": {
        if (e.op === "&&") {
          const n = this.n(e.span, e.line, undefined, Bool);
          const l = this.expr(e.l);
          const r = this.expr(e.r);
          return { kind: "and_then", ...n, l, r };
        }
        if (e.op === "||") {
          const n = this.n(e.span, e.line, undefined, Bool);
          const l = this.expr(e.l);
          const r = this.expr(e.r);
          return { kind: "or_else", ...n, l, r };
        }
        {
          const n = this.n(e.span, e.line, undefined, this.exprType(e));
          const l = this.expr(e.l);
          const r = this.expr(e.r);
          return { kind: "binary", ...n, op: e.op, l, r };
        }
      }

      case "range": {
        // Go 参考实现 node typ = types.Range（lower 的 iterKind 归类依赖它）
        const n = this.n(e.span, e.line, undefined, { kind: "range", elem: undefined, key: undefined, params: [], ret: undefined, sym: undefined, name: "" });
        const lo = this.expr(e.lo);
        const hi = this.expr(e.hi);
        return { kind: "range", ...n, lo, hi };
      }

      case "call":
        return this.call(e);

      case "index": {
        const n = this.n(e.span, e.line, undefined, this.exprType(e));
        const obj = this.expr(e.obj);
        const idx = this.expr(e.idx);
        return { kind: "index", ...n, obj, idx };
      }

      case "slice": {
        const n = this.n(e.span, e.line, undefined, this.exprType(e));
        const obj = this.expr(e.obj);
        const lo = this.expr(e.lo);
        const hi = this.expr(e.hi);
        return { kind: "slice", ...n, obj, lo, hi };
      }

      case "field": {
        const n = this.n(e.span, e.line, undefined, this.exprType(e));
        const obj = this.expr(e.obj);
        return { kind: "field", ...n, obj, name: e.name };
      }

      case "try": {
        const n = this.n(e.span, e.line, undefined, this.exprType(e));
        const x = this.expr(e.x);
        return { kind: "try", ...n, x };
      }

      case "struct_lit": {
        const n = this.n(e.span, e.line, this.res.refs.get(e), this.exprType(e));
        const fields = e.fields.map((f) => ({ name: f.name, val: this.expr(f.val) }));
        return { kind: "struct_lit", ...n, fields };
      }

      case "map_lit": {
        const n = this.n(e.span, e.line, undefined, this.exprType(e));
        const keys: HirExpr[] = [];
        const vals: HirExpr[] = [];
        // Go：逐项 键先值后（k1,v1,k2,v2）
        for (const it of e.items) {
          keys.push(this.expr(it.key));
          vals.push(this.expr(it.val));
        }
        return { kind: "map_lit", ...n, keys, vals };
      }

      case "arr_lit":
        return {
          kind: "slice_lit",
          ...this.n(e.span, e.line, undefined, this.exprType(e)),
          elems: e.items.map((el) => this.expr(el)),
        };

      case "match":
        return this.match(e);

      case "fn_lit": {
        const params = e.params.map((p) => ({ name: p.name, typ: this.anno(p.typ, e.typeParams) }));
        const body = e.body.map((s) => this.stmt(s));
        return {
          kind: "fn_lit",
          ...this.n(e.span, e.line, undefined, this.exprType(e)),
          params,
          ret: this.anno(e.ret, e.typeParams),
          body,
        };
      }

      default: {
        const _never: never = e;
        return _never;
      }
    }
  }

  /** 标识符 → LocalRef / FnRef / BuiltinRef / 裸构造器 */
  private ident(x: Extract<Expr, { kind: "ident" }>): HirExpr {
    const t = this.exprType(x);
    const sym = this.res.refs.get(x);
    if (sym !== undefined) {
      switch (sym.kind) {
        case "fn":
          return { kind: "fn_ref", ...this.n(x.span, x.line, sym, t), name: x.name };
        case "ctor":
          return { kind: "ctor", ...this.n(x.span, x.line, sym, t), ctorSym: sym, args: [] };
        default:
          return { kind: "local_ref", ...this.n(x.span, x.line, sym, t), name: x.name };
      }
    }
    if (this.res.builtin.has(x.name)) {
      return { kind: "builtin_ref", ...this.n(x.span, x.line, undefined, t), name: x.name };
    }
    // 未定义名（checker 已报，构建不该到这里）→ LocalRef 占位
    return { kind: "local_ref", ...this.n(x.span, x.line, undefined, t), name: x.name };
  }

  /** 调用脱糖：构造器 / 内建 / 用户 fn / 方法 / 函数值 */
  private call(x: Extract<Expr, { kind: "call" }>): HirExpr {
    const t = this.exprType(x);
    if (x.callee.kind === "ident") {
      const id = x.callee;
      const sym = this.res.refs.get(id);
      if (sym !== undefined && sym.kind === "ctor") {
        return {
          kind: "ctor",
          ...this.n(x.span, x.line, sym, t),
          ctorSym: sym,
          args: x.args.map((a) => this.expr(a)),
        };
      }
      if (sym === undefined && this.res.builtin.has(id.name)) {
        return {
          kind: "call",
          ...this.n(x.span, x.line, undefined, t),
          fn: undefined,
          builtin: id.name,
          callee: undefined,
          args: x.args.map((a) => this.expr(a)),
        };
      }
      if (sym !== undefined && sym.kind === "fn") {
        return {
          kind: "call",
          ...this.n(x.span, x.line, sym, t),
          fn: sym,
          builtin: undefined,
          callee: undefined,
          args: x.args.map((a) => this.expr(a)),
        };
      }
    }
    if (x.callee.kind === "field") {
      const fe = x.callee;
      const recv = this.expr(fe.obj);
      const st = structOf(recv.typ);
      const m = st !== undefined ? st.methods.get(fe.name) : undefined;
      if (st !== undefined && m !== undefined) {
        return {
          kind: "method_call",
          ...this.n(x.span, x.line, m, t),
          recv,
          method: m,
          field: undefined,
          args: x.args.map((a) => this.expr(a)),
          nameSp: methodNameSpan(fe.span, fe.name),
        };
      }
      // http.get：预载服务值 → 内建
      if (
        fe.obj.kind === "ident" && fe.obj.name === "http" && fe.name === "get" &&
        this.res.builtin.has("http")
      ) {
        return {
          kind: "call",
          ...this.n(x.span, x.line, undefined, t),
          fn: undefined,
          builtin: "http.get",
          callee: undefined,
          args: x.args.map((a) => this.expr(a)),
        };
      }
      // 字段持有的函数值
      return {
        kind: "method_call",
        ...this.n(x.span, x.line, undefined, t),
        recv,
        method: undefined,
        field: fe.name,
        args: x.args.map((a) => this.expr(a)),
        nameSp: methodNameSpan(fe.span, fe.name),
      };
    }
    // 表达式 callee（函数值）
    return {
      kind: "call",
      ...this.n(x.span, x.line, undefined, t),
      fn: undefined,
      builtin: undefined,
      callee: this.expr(x.callee),
      args: x.args.map((a) => this.expr(a)),
    };
  }

  private match(x: Extract<Expr, { kind: "match" }>): HirExpr {
    const n = this.n(x.span, x.line, undefined, this.exprType(x));
    const scrut = this.expr(x.scrut);
    const arms = x.arms.map((arm) => ({ line: arm.line, pat: this.pat(arm.pat), body: this.expr(arm.body) }));
    const kind = this.ty.matchKind.get(x) ?? "literal";
    return { kind: "match", ...n, scrut, matchKind: kind, arms };
  }

  private pat(p: Pat): HirPat {
    switch (p.kind) {
      case "wild_pat":
        return { kind: "wild_pat", line: p.line };
      case "bind_pat":
        return { kind: "bind_pat", line: p.line, name: p.name, typ: this.patType(p) };
      case "lit_pat":
        return { kind: "lit_pat", line: p.line, val: this.expr(p.val) };
      case "case_pat":
        return {
          kind: "ctor_pat",
          line: p.line,
          ctor: this.res.patRefs.get(p),
          subs: p.subs.map((sp) => this.pat(sp)),
        };
      default: {
        const _never: never = p;
        return _never;
      }
    }
  }

  // —— 查表助手 ——

  private exprType(e: Expr): Type {
    return this.ty.exprTy.get(e) ?? Unknown;
  }

  private patType(p: Pat): Type {
    return this.ty.patTy.get(p) ?? Unknown;
  }

  private symType(s: Symbol | undefined): Type {
    if (s === undefined) return Unknown;
    return this.ty.symTy.get(s.id) ?? Unknown;
  }

  private typeSymByName(name: string): Symbol | undefined {
    const b = this.res.builtin.get(name);
    if (b !== undefined && b.kind === "struct") return b;
    for (const s of this.res.symbols) {
      if ((s.kind === "struct" || s.kind === "enum") && s.name === name) return s;
    }
    return undefined;
  }
}

function withExtern(fn: HirFn, host: string): HirFn {
  return { ...fn, extern: true, externHost: host, body: [] };
}

/** 方法名的 token 区间（fe.Sp.End = 名字 token 终点；File 随表达式区间） */
function methodNameSpan(sp: import("../syntax/span.js").Span, name: string): import("../syntax/span.js").Span {
  const n = Array.from(name).length;
  return {
    file: sp.file,
    start: {
      offset: sp.end.offset - n,
      line: sp.end.line,
      col: sp.end.col - n,
    },
    end: sp.end,
  };
}

/** 取类型对应的 struct 符号（引用自动解引用） */
function structOf(t: Type): Symbol | undefined {
  let cur = t;
  while (cur.kind === "ref") cur = cur.elem ?? Unknown;
  return cur.kind === "struct" ? cur.sym : undefined;
}

/** 构建 HIR */
export function build(prog: readonly Stmt[], res: ResolveResult, ty: CheckResult): HirProg {
  return new Builder(res, ty).build(prog);
}
