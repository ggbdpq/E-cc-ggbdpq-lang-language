// Lower：HIR → Core IR。机械降级——局部槽位化、迭代协议归类、元数据剥离。
// 前置条件：HIR 构建成功（semantic/types 无诊断）。
// Local 槽位以对象同一性共享：闭包捕获解析到外层同一 Local（VALUE-CAPTURE-004）。

import type { HirExpr, HirFn, HirLet, HirPat, HirProg, HirStmt } from "../hir/model.js";
import { typ as hirTyp } from "../hir/model.js";
import { Unknown, type Type } from "../types/model.js";
import type { IrExpr, IrFn, IrForIn, IrIterKind, IrLet, IrLocal, IrOp, IrPat, IrProg, IrStmt } from "./model.js";

interface Scope {
  readonly vars: Map<string, IrLocal>;
  readonly parent: Scope | undefined;
}

function lookup(s: Scope | undefined, name: string): IrLocal | undefined {
  for (let c = s; c !== undefined; c = c.parent) {
    const v = c.vars.get(name);
    if (v !== undefined) return v;
  }
  return undefined;
}

class Lowerer {
  private out!: IrProgShaped;
  private scopes!: Scope;
  /** 当前 fn 的体局部（声明序） */
  private locals: IrLocal[] = [];
  /** 函数体下降期间才收集 locals */
  private inFn = false;

  private define(v: IrLocal): void {
    (this.scopes.vars as Map<string, IrLocal>).set(v.name, v);
    if (this.inFn) this.locals.push(v);
  }

  private push(): void {
    this.scopes = { vars: new Map(), parent: this.scopes };
  }

  private pop(): void {
    this.scopes = this.scopes.parent!;
  }

  private local(name: string, mutable: boolean, t: Type | undefined, sym: import("../semantic/symbols.js").Symbol | undefined): IrLocal {
    return { name, mutable, typ: t ?? Unknown, sym };
  }

  lower(p: HirProg): IrProg {
    this.scopes = { vars: new Map(), parent: undefined };
    const structs = p.structs.map((s) => ({
      name: s.name,
      typeParams: s.typeParams,
      fields: s.fields.map((f) => ({ name: f.name, typ: f.typ })),
    }));
    const enums = p.enums.map((e) => ({
      name: e.name,
      typeParams: e.typeParams,
      cases: e.cases.map((c) => ({ name: c.name, payloads: c.payloads })),
    }));
    const globals = p.globals.map((g) => this.lowerLet(g));
    const fns: IrFn[] = [];
    let main: IrFn | undefined;
    for (const f of p.fns) {
      const fn = this.lowerFn(f);
      if (f === p.main) main = fn;
      fns.push(fn);
    }
    return { structs, enums, globals, fns, main };
  }

  private lowerFn(hf: HirFn): IrFn {
    const savedScopes = this.scopes;
    const savedLocals = this.locals;
    const savedInFn = this.inFn;
    this.scopes = { vars: new Map(), parent: undefined };
    this.locals = [];
    this.inFn = true;
    // 参数/接收者局部不带 fn 节点的符号（sym=undefined）：
    // 后端按名引用，错误关联 fn 符号会把参数误当 fn 值绑定
    let recv: IrLocal | undefined;
    if (hf.recv !== undefined) {
      recv = this.local(hf.recv.name, false, hf.recv.typ, undefined);
      (this.scopes.vars as Map<string, IrLocal>).set(recv.name, recv);
    }
    const params = hf.params.map((p) => {
      const pl = this.local(p.name, false, p.typ, undefined);
      (this.scopes.vars as Map<string, IrLocal>).set(p.name, pl);
      return pl;
    });
    const body = this.lowerStmts(hf.body);
    const fn: IrFn = {
      name: hf.name,
      sym: hf.sym,
      recv,
      typeParams: hf.typeParams,
      params,
      ret: hf.ret,
      locals: this.locals,
      body,
      extern: hf.extern,
      externHost: hf.externHost,
    };
    this.scopes = savedScopes;
    this.locals = savedLocals;
    this.inFn = savedInFn;
    return fn;
  }

  private lowerLet(hl: HirLet): IrLet {
    const init = this.lowerExpr(hl.init);
    const name = hl.name;
    // 绑定类型 = Let 节点类型（checker 已把注解并入；无注解时为 init 类型）
    let t = hl.typ;
    if (t.kind === "unknown") t = hirTyp(hl.init);
    const v = this.local(name, hl.mutable, t, hl.sym);
    if (name !== "_") {
      this.define(v);
    }
    return { kind: "let", local: v, init };
  }

  private lowerStmts(list: readonly HirStmt[]): IrStmt[] {
    return list.map((s) => this.lowerStmt(s));
  }

  private lowerStmt(s: HirStmt): IrStmt {
    switch (s.kind) {
      case "let":
        return this.lowerLet(s);

      case "assign":
        return {
          kind: "assign",
          target: this.lowerExpr(s.target),
          op: s.op as IrOp,
          val: this.lowerExpr(s.rhs),
        };

      case "eval":
        return { kind: "eval", x: this.lowerExpr(s.x) };

      case "return":
        return { kind: "return", val: s.val !== undefined ? this.lowerExpr(s.val) : undefined };

      case "if":
        return {
          kind: "if",
          cond: this.lowerExpr(s.cond),
          then: this.lowerBlock(s.then),
          els: this.lowerBlock(s.els),
        };

      case "while":
        return {
          kind: "while",
          cond: this.lowerExpr(s.cond),
          body: this.lowerBlock(s.body),
        };

      case "for_in": {
        this.push();
        const v = this.local(s.variable, false, s.varType, s.sym);
        if (s.variable !== "_") {
          (this.scopes.vars as Map<string, IrLocal>).set(s.variable, v);
        }
        const fn: IrForIn = {
          kind: "for_in",
          variable: v,
          iterKind: iterKind(hirTyp(s.iter)),
          iter: this.lowerExpr(s.iter),
          body: this.lowerStmts(s.body),
        };
        this.pop();
        return fn;
      }

      case "break":
        return { kind: "break" };
      case "continue":
        return { kind: "continue" };

      case "spawn":
        return {
          kind: "spawn",
          fn: s.fn,
          args: s.args.map((a) => this.lowerExpr(a)),
        };

      case "fn_stmt": {
        const saved = this.saveFnCtx();
        const fn = this.lowerFn(s.fn);
        this.restoreFnCtx(saved);
        return { kind: "fn_def", fn };
      }

      default: {
        const _never: never = s;
        return _never;
      }
    }
  }

  private lowerBlock(list: readonly HirStmt[]): IrStmt[] {
    this.push();
    const out = this.lowerStmts(list);
    this.pop();
    return out;
  }

  private saveFnCtx(): { scopes: Scope; locals: IrLocal[]; inFn: boolean } {
    return { scopes: this.scopes, locals: this.locals, inFn: this.inFn };
  }

  private restoreFnCtx(c: { scopes: Scope; locals: IrLocal[]; inFn: boolean }): void {
    this.scopes = c.scopes;
    this.locals = c.locals;
    this.inFn = c.inFn;
  }

  // —— 表达式 ——

  private lowerExpr(e: HirExpr): IrExpr {
    const t = hirTyp(e);
    switch (e.kind) {
      case "lit":
        switch (e.litKind) {
          case "int":
            return { kind: "const", constKind: "int", i64: e.i64, f64: 0, s: "", b: false, typ: t };
          case "float":
            return { kind: "const", constKind: "float", i64: 0n, f64: e.f64, s: "", b: false, typ: t };
          case "str":
            return { kind: "const", constKind: "str", i64: 0n, f64: 0, s: e.s, b: false, typ: t };
          case "bool":
            return { kind: "const", constKind: "bool", i64: 0n, f64: 0, s: "", b: e.b, typ: t };
          default: {
            const _never: never = e.litKind;
            return _never;
          }
        }

      case "local_ref": {
        const v = lookup(this.scopes, e.name);
        if (v !== undefined) return { kind: "local_get", local: v, typ: t };
        // 全局（跨 fn 引用）：降级为按名槽位
        return { kind: "local_get", local: { name: e.name, mutable: false, typ: t, sym: e.sym }, typ: t };
      }

      case "fn_ref":
        return { kind: "fn_get", name: e.name, sym: e.sym, typ: t };

      case "builtin_ref":
        return { kind: "builtin_get", name: e.name, typ: t };

      case "ctor":
        return {
          kind: "ctor",
          sym: e.ctorSym,
          args: e.args.map((a) => this.lowerExpr(a)),
          typ: t,
        };

      case "call": {
        let fn: import("../semantic/symbols.js").Symbol | undefined;
        let builtin: string | undefined;
        let callee: IrExpr | undefined;
        if (e.fn !== undefined) {
          fn = e.fn;
        } else if (e.builtin !== undefined && e.builtin !== "") {
          builtin = e.builtin;
        } else {
          callee = e.callee !== undefined ? this.lowerExpr(e.callee) : undefined;
        }
        return {
          kind: "call",
          fn,
          builtin,
          method: undefined,
          recv: undefined,
          callee,
          args: e.args.map((a) => this.lowerExpr(a)),
          typ: t,
        };
      }

      case "method_call": {
        if (e.method !== undefined) {
          return {
            kind: "call",
            fn: undefined,
            builtin: undefined,
            method: e.method,
            recv: this.lowerExpr(e.recv),
            callee: undefined,
            args: e.args.map((a) => this.lowerExpr(a)),
            typ: t,
          };
        }
        // 字段函数值调用：callee = 字段读取（复用 method_call 节点元数据）
        const fieldRead: HirExpr = {
          kind: "field",
          id: e.id,
          line: e.line,
          span: e.span,
          sym: e.sym,
          typ: e.typ,
          obj: e.recv,
          name: e.field ?? "",
        };
        return {
          kind: "call",
          fn: undefined,
          builtin: undefined,
          method: undefined,
          recv: undefined,
          callee: this.lowerExpr(fieldRead),
          args: e.args.map((a) => this.lowerExpr(a)),
          typ: t,
        };
      }

      case "unary":
        return { kind: "unary", op: e.op as IrOp, x: this.lowerExpr(e.x), typ: t };

      case "binary":
        return { kind: "binary", op: e.op as IrOp, l: this.lowerExpr(e.l), r: this.lowerExpr(e.r), typ: t };

      case "and_then":
        return { kind: "binary", op: "&&", l: this.lowerExpr(e.l), r: this.lowerExpr(e.r), typ: t };
      case "or_else":
        return { kind: "binary", op: "||", l: this.lowerExpr(e.l), r: this.lowerExpr(e.r), typ: t };

      case "range":
        return { kind: "range", lo: this.lowerExpr(e.lo), hi: this.lowerExpr(e.hi), typ: t };

      case "index":
        return { kind: "index", obj: this.lowerExpr(e.obj), idx: this.lowerExpr(e.idx), typ: t };

      case "slice":
        return { kind: "slice", obj: this.lowerExpr(e.obj), lo: this.lowerExpr(e.lo), hi: this.lowerExpr(e.hi), typ: t };

      case "field":
        return { kind: "field", obj: this.lowerExpr(e.obj), name: e.name, typ: t };

      case "try":
        return { kind: "try", x: this.lowerExpr(e.x), typ: t };

      case "struct_lit":
        return {
          kind: "struct_lit",
          struct: e.sym,
          fields: e.fields.map((f) => ({ name: f.name, val: this.lowerExpr(f.val) })),
          typ: t,
        };

      case "map_lit": {
        const keys: IrExpr[] = [];
        const vals: IrExpr[] = [];
        for (let i = 0; i < e.keys.length; i++) {
          keys.push(this.lowerExpr(e.keys[i]!));
          vals.push(this.lowerExpr(e.vals[i]!));
        }
        return { kind: "map_lit", keys, vals, typ: t };
      }

      case "slice_lit":
        return { kind: "slice_lit", elems: e.elems.map((el) => this.lowerExpr(el)), typ: t };

      case "match": {
        const scrut = this.lowerExpr(e.scrut);
        const arms = e.arms.map((arm) => {
          this.push();
          const irArm = { pat: this.lowerPat(arm.pat), body: this.lowerExpr(arm.body) };
          this.pop();
          return irArm;
        });
        return { kind: "match", scrut, matchKind: e.matchKind, arms, typ: t };
      }

      case "fn_lit": {
        const saved = this.saveFnCtx();
        // 闭包作用域继承外层链（按引用捕获）：捕获变量解析到外层的同一 Local 槽位
        this.push();
        this.locals = [];
        this.inFn = true;
        const params = e.params.map((p) => {
          const pl = this.local(p.name, false, p.typ, e.sym);
          (this.scopes.vars as Map<string, IrLocal>).set(p.name, pl);
          return pl;
        });
        const body = this.lowerStmts(e.body);
        const fnLit: IrExpr = {
          kind: "fn_lit",
          params,
          ret: e.ret,
          locals: this.locals,
          body,
          typ: t,
        };
        this.restoreFnCtx(saved);
        return fnLit;
      }

      default: {
        const _never: never = e;
        return _never;
      }
    }
  }

  private lowerPat(p: HirPat): IrPat {
    switch (p.kind) {
      case "wild_pat":
        return { kind: "wild_pat" };
      case "bind_pat": {
        const v = this.local(p.name, false, p.typ, undefined);
        if (p.name !== "_") {
          (this.scopes.vars as Map<string, IrLocal>).set(p.name, v);
        }
        return { kind: "bind_pat", name: p.name, typ: p.typ, local: v };
      }
      case "lit_pat":
        return { kind: "lit_pat", val: this.lowerExpr(p.val) };
      case "ctor_pat":
        return { kind: "ctor_pat", sym: p.ctor, subs: p.subs.map((sp) => this.lowerPat(sp)) };
      default: {
        const _never: never = p;
        return _never;
      }
    }
  }
}

type IrProgShaped = {
  structs: { name: string; typeParams: readonly string[]; fields: { name: string; typ: Type }[] }[];
  enums: { name: string; typeParams: readonly string[]; cases: { name: string; payloads: readonly (Type | undefined)[] }[] }[];
  globals: IrLet[];
  fns: IrFn[];
  main: IrFn | undefined;
};

function iterKind(t: Type): IrIterKind {
  switch (t.kind) {
    case "slice":
      return "slice";
    case "map":
      return "map";
    case "string":
      return "string";
    case "range":
      return "range";
    default:
      return "unknown";
  }
}

/** 降级整个程序 */
export function lower(p: HirProg): IrProg {
  return new Lowerer().lower(p);
}
