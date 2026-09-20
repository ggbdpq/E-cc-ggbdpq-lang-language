// 树遍历解释器（specs/expressions.md 求值顺序；TASK-SPAWN-002 同步确定子集）。
// 错误消息按 Go 参考实现逐字复刻（含行号前缀的有无）。

import type {
  AssignStmt,
  BinaryEx,
  CallEx,
  Expr,
  FnDecl,
  ForInStmt,
  IndexEx,
  MatchEx,
  SliceEx,
  Stmt,
  StructDecl,
  EnumDecl,
  UnaryEx,
} from "../syntax/ast.js";
import {
  boolV, caseIndex, copyValue, equals, fmtVal, mapGet, mapSet, strV, truthy, typeName, intV,
  compare,
  type Closure, type EnumDef, type StructDef, type Val,
} from "./value.js";
import { CORE_BUILTINS, makeStdlib } from "./builtin.js";
import { RtError } from "./errors.js";
import type { EnvLike } from "./value.js";
export { RtError };

// —— 控制流信号与运行时错误 ——

class Signal extends Error {
  constructor(
    readonly sigKind: "return" | "break" | "continue" | "err",
    readonly sigVal?: Val,
  ) {
    super(signalMessage(sigKind, sigVal));
    this.name = "Signal";
  }
}

function signalMessage(kind: Signal["sigKind"], val: Val | undefined): string {
  switch (kind) {
    case "return":
      return "return 出现在函数之外";
    case "break":
      return "break 出现在循环之外";
    case "continue":
      return "continue 出现在循环之外";
    default:
      return "Err 传播出函数之外：" + fmtVal(val ?? unitVal, true);
  }
}

const unitVal: Val = { k: "unit" };

interface Cell {
  val: Val;
  mutable: boolean;
}

export class Env {
  readonly vars = new Map<string, Cell>();
  constructor(readonly parent: EnvLike | undefined) {}

  define(name: string, v: Val, mutable: boolean): RtError | undefined {
    if (this.vars.has(name)) {
      return new RtError(`${name} 在此作用域重复定义`);
    }
    this.vars.set(name, { val: v, mutable });
    return undefined;
  }

  /** 声明式定义，允许覆盖（顶层 fn 在 REPL 里反复声明用） */
  redefine(name: string, v: Val): void {
    this.vars.set(name, { val: v, mutable: false });
  }

  lookup(name: string): Cell | undefined {
    for (let s: EnvLike | undefined = this; s !== undefined; s = s.parent) {
      const c = (s as Env).vars.get(name);
      if (c !== undefined) return c;
    }
    return undefined;
  }
}

interface CtorEntry {
  def: EnumDef;
  idx: number;
  arity: number;
}

interface AssignAddr {
  cell?: Cell;
  vname?: string;
  obj?: Val;
  name?: string;
  idx?: Val;
  line: number;
}

/** int64 二补码回绕（Go int64 溢出语义） */
export function wrap64(n: bigint): bigint {
  const M = 1n << 64n;
  let r = ((n % M) + M) % M;
  if (r >= 1n << 63n) r -= M;
  return r;
}

export class Interp {
  readonly globals = new Env(undefined);
  private readonly structs = new Map<string, StructDef>();
  private readonly enums = new Map<string, EnumDef>();
  private readonly ctors = new Map<string, CtorEntry>();

  constructor(readonly out: { write(s: string): void }) {
    this.prelude();
    this.preludeStdlib();
  }

  private errf(line: number, msg: string): RtError {
    return new RtError(`第${line}行: ${msg}`);
  }

  private prelude(): void {
    const optDef: EnumDef = { name: "Option", cases: ["Some", "None"] };
    const resDef: EnumDef = { name: "Result", cases: ["Ok", "Err"] };
    this.enums.set("Option", optDef);
    this.enums.set("Result", resDef);
    this.ctors.set("Some", { def: optDef, idx: 0, arity: 1 });
    this.ctors.set("None", { def: optDef, idx: 1, arity: 0 });
    this.ctors.set("Ok", { def: resDef, idx: 0, arity: 1 });
    this.ctors.set("Err", { def: resDef, idx: 1, arity: 1 });
    this.structs.set("error", { name: "error", fields: ["msg"], methods: new Map() });
    for (const [name, fn] of CORE_BUILTINS) {
      this.globals.redefine(name, { k: "builtin", name, fn });
    }
  }

  private preludeStdlib(): void {
    for (const [name, fn] of makeStdlib(this)) {
      this.globals.redefine(name, { k: "builtin", name, fn });
    }
  }

  enumsAccess(): Map<string, EnumDef> {
    return this.enums;
  }
  structsAccess(): Map<string, StructDef> {
    return this.structs;
  }

  // —— 内建/标准库注册面（builtin.ts 用） ——

  errDef(): StructDef {
    return this.structs.get("error")!;
  }
  optionDef(): EnumDef {
    return this.enums.get("Option")!;
  }
  resultDef(): EnumDef {
    return this.enums.get("Result")!;
  }
  httpRespDef(): StructDef {
    return this.structs.get("HttpResp")!;
  }

  /** 注册 http 服务值（struct 字段持有内建） */
  setupHttpService(getFn: (it: Interp, args: Val[]) => Val): void {
    this.structs.set("HttpResp", { name: "HttpResp", fields: ["status", "body"], methods: new Map() });
    const httpDef: StructDef = { name: "http", fields: ["get"], methods: new Map() };
    this.structs.set("http", httpDef);
    this.globals.redefine("http", {
      k: "struct",
      def: httpDef,
      fields: [{ k: "builtin", name: "http.get", fn: getFn }],
    });
  }

  /** 执行整个程序；定义了 main 则随后调用 */
  run(prog: readonly Stmt[]): void {
    this.execStmts(prog, this.globals);
    const c = this.globals.lookup("main");
    if (c !== undefined && c.val.k === "closure") {
      this.callClosure(c.val, []);
    }
  }

  /** 执行 REPL 片段，返回最后一个表达式语句的值 */
  evalChunk(stmts: readonly Stmt[]): { val: Val | undefined; has: boolean } {
    let last: Val | undefined;
    let has = false;
    for (const s of stmts) {
      if (s.kind === "expr_stmt") {
        last = this.eval(s.x, this.globals);
        has = true;
        continue;
      }
      this.execStmt(s, this.globals);
    }
    return { val: last, has };
  }

  /** 调用全局零参函数（test 运行器用） */
  callByName(name: string): void {
    const c = this.globals.lookup(name);
    if (c === undefined) throw new RtError(`未定义 ${name}`);
    if (c.val.k !== "closure") throw new RtError(`${name} 不是函数`);
    this.callClosure(c.val, []);
  }

  // —— 语句 ——

  private execStmts(stmts: readonly Stmt[], env: Env): void {
    for (const s of stmts) this.execStmt(s, env);
  }

  private execStmt(s: Stmt, env: Env): void {
    switch (s.kind) {
      case "let_stmt": {
        const v = this.eval(s.init, env);
        if (s.name === "_") return;
        const e = env.define(s.name, copyValue(v), s.mutable);
        if (e !== undefined) throw e;
        return;
      }

      case "assign":
        this.assign(s, env);
        return;

      case "expr_stmt":
        this.eval(s.x, env);
        return;

      case "return": {
        let v: Val = unitVal;
        if (s.val !== undefined) {
          v = copyValue(this.eval(s.val, env));
        }
        throw new Signal("return", v);
      }

      case "if": {
        const t = this.cond(s.cond, env);
        if (t) {
          this.execStmts(s.then, new Env(env));
          return;
        }
        if (s.els !== undefined) {
          this.execStmts(s.els, new Env(env));
        }
        return;
      }

      case "while":
        for (;;) {
          const t = this.cond(s.cond, env);
          if (!t) return;
          this.execLoopBody(s.body, new Env(env));
        }

      case "for_in":
        this.execForIn(s, env);
        return;

      case "break":
        throw new Signal("break");

      case "continue":
        throw new Signal("continue");

      case "fn_decl":
        this.declFn(s, env);
        return;

      case "struct_decl":
        this.declStruct(s);
        return;

      case "enum_decl":
        this.declEnum(s);
        return;

      case "extern":
        // extern 声明：解释器一律注册为「仅转译可用」桩
        for (const fn of s.fns) {
          const name = fn.name;
          const host = s.host;
          this.globals.redefine(name, {
            k: "builtin",
            name,
            fn: () => {
              throw new RtError(`extern 函数 ${name} 仅转译可用（宿主 ${host}）`);
            },
          });
        }
        return;

      case "import":
        throw this.errf(s.line, "import 由装载器在合并前展开（不应执行到这里）");

      case "go":
        // 单线程解释器：go 同步立即执行（语义子集，输出确定）
        this.evalCall(s.call, env);
        return;

      default:
        throw new RtError("未知语句");
    }
  }

  /** 执行循环体并消化 break/continue；return/err 信号向上抛 */
  private execLoopBody(body: readonly Stmt[], env: Env): void {
    try {
      this.execStmts(body, env);
    } catch (e) {
      if (e instanceof Signal && (e.sigKind === "break" || e.sigKind === "continue")) {
        return;
      }
      throw e;
    }
  }

  private cond(e: Expr, env: Env): boolean {
    const v = this.eval(e, env);
    const t = truthy(v);
    if (t.err !== undefined) throw new RtError(t.err);
    return t.b!;
  }

  private execForIn(st: ForInStmt, env: Env): void {
    const iter = this.eval(st.iter, env);
    const items: Val[] = [];
    switch (iter.k) {
      case "arr":
        items.push(...iter.items);
        break;
      case "map":
        items.push(...iter.ks);
        break;
      case "str":
        for (const r of iter.v) items.push(strV(r));
        break;
      case "range":
        for (let i = iter.lo; i < iter.hi; i++) items.push(intV(i));
        break;
      default:
        throw this.errf(st.line, `${typeName(iter)} 不可迭代`);
    }
    for (const item of items) {
      const benv = new Env(env);
      if (st.variable !== "_") {
        const e = benv.define(st.variable, copyValue(item), false);
        if (e !== undefined) throw e;
      }
      this.execLoopBody(st.body, benv);
    }
  }

  private declFn(st: FnDecl, env: Env): void {
    if (st.recv !== undefined) {
      const rt = st.recv.typ;
      if (rt === undefined || rt.kind !== "named_type") {
        throw this.errf(st.line, "方法接收者须为具名 struct 类型");
      }
      const def = this.structs.get(rt.name);
      if (def === undefined) {
        throw this.errf(st.line, `为未知 struct ${rt.name} 定义方法`);
      }
      def.methods.set(st.name, { k: "closure", name: st.name, decl: st, env });
      return;
    }
    env.redefine(st.name, { k: "closure", name: st.name, decl: st, env });
  }

  private declStruct(st: StructDecl): void {
    const fields = st.fields.map((f) => f.name);
    this.registerStruct(st.name, fields, st.line);
  }

  private declEnum(st: EnumDecl): void {
    const cases = st.cases.map((c) => c.name);
    const def = this.registerEnum(st.name, cases, st.line);
    for (let i = 0; i < st.cases.length; i++) {
      const c = st.cases[i]!;
      this.ctors.set(c.name, { def, idx: i, arity: c.payloads.length });
    }
  }

  private registerStruct(name: string, fields: readonly string[], line: number): StructDef {
    if (this.structs.has(name)) {
      throw this.errf(line, `struct ${name} 重复定义`);
    }
    const def: StructDef = { name, fields, methods: new Map() };
    this.structs.set(name, def);
    return def;
  }

  private registerEnum(name: string, cases: readonly string[], line: number): EnumDef {
    if (this.enums.has(name)) {
      throw this.errf(line, `enum ${name} 重复定义`);
    }
    for (const c of cases) {
      if (this.ctors.has(c)) {
        throw this.errf(line, `构造器 ${c} 重名`);
      }
      if (this.globals.lookup(c) !== undefined) {
        throw this.errf(line, `case ${c} 与内建名冲突`);
      }
    }
    const def: EnumDef = { name, cases };
    this.enums.set(name, def);
    return def;
  }

  // —— 赋值（EVAL-ASSIGN-003：地址组件先于右值） ——

  private evalAssignAddr(tgt: Expr, env: Env): AssignAddr {
    const line = tgt.line;
    switch (tgt.kind) {
      case "ident": {
        const c = env.lookup(tgt.name);
        if (c === undefined) {
          throw this.errf(line, `未定义变量 ${tgt.name}`);
        }
        return { cell: c, vname: tgt.name, line };
      }
      case "field": {
        let ov = this.eval(tgt.obj, env);
        if (ov.k === "ptr") ov = ov.cell.val;
        if (ov.k !== "struct") {
          throw this.errf(line, `只能给 struct 字段赋值，得到 ${typeName(ov)}`);
        }
        return { obj: ov, name: tgt.name, line };
      }
      case "index": {
        let ov = this.eval(tgt.obj, env);
        if (ov.k === "ptr") ov = ov.cell.val;
        const iv = this.eval(tgt.idx, env);
        return { obj: ov, idx: iv, line };
      }
      default:
        throw this.errf(line, "赋值目标不合法");
    }
  }

  private writeAddr(a: AssignAddr, v: Val): void {
    if (a.cell !== undefined) {
      if (!a.cell.mutable) {
        throw this.errf(a.line, `${a.vname} 由 let 绑定，不可重新赋值`);
      }
      a.cell.val = v;
      return;
    }
    const container = a.obj;
    if (container === undefined) throw this.errf(a.line, "赋值目标不合法");
    switch (container.k) {
      case "struct": {
        const idx = container.def.fields.indexOf(a.name ?? "");
        if (idx < 0) {
          throw this.errf(a.line, `${container.def.name} 没有字段 ${a.name}`);
        }
        container.fields[idx] = v;
        return;
      }
      case "arr": {
        const i = this.intIndex(a.idx!, container.items.length, a.line);
        container.items[i] = v;
        return;
      }
      case "map": {
        const e = mapSet(container, a.idx!, v);
        if (e !== undefined) throw this.errf(a.line, e);
        return;
      }
      case "str":
        throw this.errf(a.line, "字符串不可变");
      default:
        throw this.errf(a.line, `${typeName(container)} 不可索引赋值`);
    }
  }

  private readAddr(a: AssignAddr): Val {
    if (a.cell !== undefined) return a.cell.val;
    const container = a.obj;
    if (container === undefined) throw this.errf(a.line, "赋值目标不合法");
    switch (container.k) {
      case "struct": {
        const idx = container.def.fields.indexOf(a.name ?? "");
        if (idx < 0) {
          throw this.errf(a.line, `${container.def.name} 没有字段 ${a.name}`);
        }
        return container.fields[idx]!;
      }
      case "arr": {
        const i = this.intIndex(a.idx!, container.items.length, a.line);
        return container.items[i]!;
      }
      case "map": {
        const { found, v } = mapGet(container, a.idx!);
        if (!found) {
          throw this.errf(a.line, `映射无键 ${fmtVal(a.idx!, true)}（用 has() 探测）`);
        }
        return v!;
      }
      case "str":
        return container;
      default:
        throw this.errf(a.line, `${typeName(container)} 不可索引读取`);
    }
  }

  private assign(st: AssignStmt, env: Env): void {
    const addr = this.evalAssignAddr(st.target, env);
    let v = this.eval(st.val, env);
    v = copyValue(v);

    if (st.op !== "=") {
      this.assignCompound(st, addr, v);
      return;
    }
    this.writeAddr(addr, v);
  }

  /** 复合赋值（+= -= *= /=）：读旧值、运算、写回；数组 += 是原位 push */
  private assignCompound(st: AssignStmt, addr: AssignAddr, v: Val): void {
    if (st.op === "+=") {
      let cur = this.readAddr(addr);
      if (cur.k === "ptr") cur = cur.cell.val;
      if (cur.k === "arr") {
        if (v.k !== "arr") {
          throw this.errf(st.line, `数组 += 需要数组，得到 ${typeName(v)}`);
        }
        cur.items.push(...v.items);
        return;
      }
    }

    let old = this.readAddr(addr);
    if (old.k === "ptr") old = old.cell.val;
    const combined = this.applyOp(st.op, old, v, st.line);
    this.writeAddr(addr, combined);
  }

  private applyOp(op: string, old: Val, v: Val, line: number): Val {
    if (op === "+=") {
      if (old.k === "int" && v.k === "int") return intV(wrap64(old.v + v.v));
      if (old.k === "float" && v.k === "float") return { k: "float", v: old.v + v.v };
      if (old.k === "str" && v.k === "str") return strV(old.v + v.v);
    } else if (op === "-=") {
      if (old.k === "int" && v.k === "int") return intV(wrap64(old.v - v.v));
      if (old.k === "float" && v.k === "float") return { k: "float", v: old.v - v.v };
    } else if (op === "*=") {
      if (old.k === "int" && v.k === "int") return intV(wrap64(old.v * v.v));
      if (old.k === "float" && v.k === "float") return { k: "float", v: old.v * v.v };
    } else if (op === "/=") {
      if (old.k === "int" && v.k === "int") {
        if (v.v === 0n) throw this.errf(line, "整数除零");
        return intV(old.v / v.v);
      }
      if (old.k === "float" && v.k === "float") {
        if (v.v === 0) throw this.errf(line, "浮点除零");
        return { k: "float", v: old.v / v.v };
      }
    }
    throw this.errf(line, `${typeName(old)} 与 ${typeName(v)} 不可做该运算（无隐式转换）`);
  }

  private intIndex(v: Val, length: number, line: number): number {
    if (v.k !== "int") {
      throw this.errf(line, `索引须为 int，得到 ${typeName(v)}`);
    }
    const idx = Number(v.v);
    if (idx < 0 || idx >= length) {
      throw this.errf(line, `索引 ${idx} 越界（长度 ${length}）`);
    }
    return idx;
  }

  // —— 表达式 ——

  eval(e: Expr, env: Env): Val {
    switch (e.kind) {
      case "int_lit":
        return intV(e.value);
      case "float_lit":
        return { k: "float", v: e.value };
      case "str_lit":
        return strV(e.value);
      case "bool_lit":
        return boolV(e.value);

      case "ident": {
        const c = env.lookup(e.name);
        if (c !== undefined) return c.val;
        const ct = this.ctors.get(e.name);
        if (ct !== undefined) {
          if (ct.arity === 0) {
            // 零参构造器允许裸写（None 这类）
            return { k: "enum", def: ct.def, case: ct.idx, payloads: [] };
          }
          throw this.errf(e.line, `构造器 ${e.name} 须直接调用`);
        }
        throw this.errf(e.line, `未定义 ${e.name}`);
      }

      case "unary":
        return this.evalUnary(e, env);

      case "binary":
        return this.evalBinary(e, env);

      case "range": {
        const lo = this.eval(e.lo, env);
        const hi = this.eval(e.hi, env);
        if (lo.k !== "int" || hi.k !== "int") {
          throw this.errf(e.line, "区间两端须为 int");
        }
        return { k: "range", lo: lo.v, hi: hi.v };
      }

      case "call":
        return this.evalCall(e, env);

      case "index":
        return this.evalIndex(e, env);

      case "slice":
        return this.evalSlice(e, env);

      case "field": {
        let ov = this.eval(e.obj, env);
        if (ov.k === "ptr") ov = ov.cell.val;
        if (ov.k !== "struct") {
          throw this.errf(e.line, `${e.name} 没有字段（${typeName(ov)} 不是 struct）`);
        }
        const idx = ov.def.fields.indexOf(e.name);
        if (idx >= 0) return ov.fields[idx]!;
        throw this.errf(e.line, `${ov.def.name} 没有字段 ${e.name}`);
      }

      case "try": {
        const v = this.eval(e.x, env);
        if (v.k !== "enum" || v.def.name !== "Result") {
          throw this.errf(e.line, `? 的操作数须为 Result，得到 ${typeName(v)}`);
        }
        if (v.case === 0) return v.payloads[0]!; // Ok
        throw new Signal("err", v.payloads[0]);
      }

      case "struct_lit": {
        const def = this.structs.get(e.typeName);
        if (def === undefined) {
          throw this.errf(e.line, `未知 struct ${e.typeName}`);
        }
        if (e.fields.length !== def.fields.length) {
          throw this.errf(e.line, `${def.name} 需要 ${def.fields.length} 个字段，得到 ${e.fields.length} 个`);
        }
        const fields: Val[] = new Array(e.fields.length);
        for (let i = 0; i < e.fields.length; i++) {
          const f = e.fields[i]!;
          if (f.name !== def.fields[i]) {
            throw this.errf(e.line, `${def.name} 第 ${i + 1} 个字段应为 ${def.fields[i]}，得到 ${f.name}`);
          }
          fields[i] = copyValue(this.eval(f.val, env));
        }
        return { k: "struct", def, fields };
      }

      case "map_lit": {
        const m: Val = { k: "map", ks: [], m: new Map() };
        for (const item of e.items) {
          const k = this.eval(item.key, env);
          const v = this.eval(item.val, env);
          const e2 = mapSet(m as Extract<Val, { k: "map" }>, k, copyValue(v));
          if (e2 !== undefined) throw this.errf(e.line, e2);
        }
        return m;
      }

      case "arr_lit": {
        const items: Val[] = [];
        for (const el of e.items) {
          items.push(copyValue(this.eval(el, env)));
        }
        return { k: "arr", items };
      }

      case "match":
        return this.evalMatch(e, env);

      case "fn_lit": {
        const decl: FnDecl = {
          kind: "fn_decl",
          name: "",
          recv: undefined,
          typeParams: e.typeParams,
          params: e.params,
          ret: e.ret,
          body: e.body,
          line: e.line,
          span: e.span,
        };
        return { k: "closure", name: "", decl, env };
      }

      default:
        throw new RtError("未知表达式");
    }
  }

  private evalUnary(x: UnaryEx, env: Env): Val {
    const v = this.eval(x.x, env);
    switch (x.op) {
      case "-":
        if (v.k === "int") return intV(wrap64(-v.v));
        if (v.k === "float") return { k: "float", v: -v.v };
        throw this.errf(x.line, `- 不能用于 ${typeName(v)}`);
      case "!": {
        const t = truthy(v);
        if (t.err !== undefined) throw this.errf(x.line, t.err);
        return boolV(!t.b!);
      }
      case "&": {
        if (x.x.kind !== "ident") {
          throw this.errf(x.line, "v0 只能对变量取引用");
        }
        const c = env.lookup(x.x.name);
        if (c === undefined) {
          throw this.errf(x.line, `未定义 ${x.x.name}`);
        }
        return { k: "ptr", cell: c };
      }
      default: {
        const _never: never = x.op;
        return _never;
      }
    }
  }

  private evalBinary(x: BinaryEx, env: Env): Val {
    switch (x.op) {
      case "&&": {
        const l = this.cond(x.l, env);
        if (!l) return boolV(false);
        return boolV(this.cond(x.r, env));
      }
      case "||": {
        const l = this.cond(x.l, env);
        if (l) return boolV(true);
        return boolV(this.cond(x.r, env));
      }
      default:
        break;
    }

    const l = this.eval(x.l, env);
    const r = this.eval(x.r, env);

    switch (x.op) {
      case "==":
      case "!=": {
        const { ok, eq, err } = equals(l, r);
        if (!ok) throw this.errf(x.line, err!);
        return boolV(x.op === "!=" ? !eq! : eq!);
      }
      case "&":
      case "|":
      case "^":
      case "<<":
      case ">>": {
        if (l.k !== "int" || r.k !== "int") {
          throw this.errf(x.line, `${typeName(l)} 与 ${typeName(r)} 不可位运算（须为 int）`);
        }
        if (x.op === "&") return intV(l.v & r.v);
        if (x.op === "|") return intV(l.v | r.v);
        if (x.op === "^") return intV(l.v ^ r.v);
        if (r.v < 0n || r.v >= 64n) {
          throw this.errf(x.line, `移位位数须在 0..63，得到 ${r.v.toString(10)}`);
        }
        if (x.op === "<<") return intV(wrap64(l.v << r.v));
        return intV(l.v >> r.v); // BigInt >> 为算术右移，与 Go int64 一致
      }
      case "<":
      case "<=":
      case ">":
      case ">=": {
        const cmp = compare(l, r);
        if (cmp === undefined) {
          throw this.errf(x.line, `${typeName(l)} 与 ${typeName(r)} 不可比较大小（无隐式转换）`);
        }
        if (x.op === "<") return boolV(cmp < 0);
        if (x.op === "<=") return boolV(cmp <= 0);
        if (x.op === ">") return boolV(cmp > 0);
        return boolV(cmp >= 0);
      }
      default:
        break;
    }

    switch (l.k) {
      case "int": {
        if (r.k !== "int") {
          throw this.errf(x.line, `${typeName(l)} 与 ${typeName(r)} 不可比较（无隐式转换）`);
        }
        return this.arith(x, l.v, r.v);
      }
      case "float": {
        if (r.k !== "float") {
          throw this.errf(x.line, `${typeName(l)} 与 ${typeName(r)} 不可比较（无隐式转换）`);
        }
        switch (x.op) {
          case "+":
            return { k: "float", v: l.v + r.v };
          case "-":
            return { k: "float", v: l.v - r.v };
          case "*":
            return { k: "float", v: l.v * r.v };
          case "/":
            if (r.v === 0) throw this.errf(x.line, "浮点除零");
            return { k: "float", v: l.v / r.v };
          default:
            throw this.errf(x.line, "% 不能用于 float");
        }
      }
      case "str": {
        if (r.k !== "str" || x.op !== "+") {
          throw this.errf(x.line, `${typeName(l)} 与 ${typeName(r)} 不可比较（无隐式转换）`);
        }
        return strV(l.v + r.v);
      }
      default:
        break;
    }
    throw this.errf(x.line, `${typeName(l)} 不支持运算 ${x.op}`);
  }

  private arith(x: BinaryEx, a: bigint, b: bigint): Val {
    switch (x.op) {
      case "+":
        return intV(wrap64(a + b));
      case "-":
        return intV(wrap64(a - b));
      case "*":
        return intV(wrap64(a * b));
      case "/":
        if (b === 0n) throw this.errf(x.line, "整数除零");
        return intV(a / b);
      case "%":
        if (b === 0n) throw this.errf(x.line, "整数模零");
        return intV(a % b);
      default:
        throw this.errf(x.line, "未知算术运算");
    }
  }

  private evalIndex(x: IndexEx, env: Env): Val {
    let ov = this.eval(x.obj, env);
    if (ov.k === "ptr") ov = ov.cell.val;
    const iv = this.eval(x.idx, env);
    switch (ov.k) {
      case "arr": {
        const i = this.intIndex(iv, ov.items.length, x.line);
        return ov.items[i]!;
      }
      case "map": {
        const { found, v } = mapGet(ov, iv);
        if (!found) {
          throw this.errf(x.line, `映射无键 ${fmtVal(iv, true)}（用 has() 探测）`);
        }
        return v!;
      }
      case "str": {
        const runes = Array.from(ov.v);
        const i = this.intIndex(iv, runes.length, x.line);
        return strV(runes[i]!);
      }
      default:
        throw this.errf(x.line, `${typeName(ov)} 不可索引`);
    }
  }

  private evalSlice(x: SliceEx, env: Env): Val {
    let ov = this.eval(x.obj, env);
    if (ov.k === "ptr") ov = ov.cell.val;
    const lov = this.eval(x.lo, env);
    const hiv = this.eval(x.hi, env);
    const bounds = (n: number): [number, number] => {
      if (lov.k !== "int" || hiv.k !== "int") {
        throw this.errf(x.line, "切片端点须为 int");
      }
      const loi = Number(lov.v);
      const hii = Number(hiv.v);
      if (loi < 0 || hii < loi || hii > n) {
        throw this.errf(x.line, `切片 [${loi}:${hii}] 越界（长度 ${n}）`);
      }
      return [loi, hii];
    };
    switch (ov.k) {
      case "arr": {
        const [lo, hi] = bounds(ov.items.length);
        return { k: "arr", items: ov.items.slice(lo, hi) };
      }
      case "str": {
        const runes = Array.from(ov.v);
        const [lo, hi] = bounds(runes.length);
        return strV(runes.slice(lo, hi).join(""));
      }
      default:
        throw this.errf(x.line, `${typeName(ov)} 不可切片`);
    }
  }

  private evalMatch(x: MatchEx, env: Env): Val {
    const scrut = this.eval(x.scrut, env);
    const isEnum = scrut.k === "enum";
    const covered = new Set<number>();
    for (const arm of x.arms) {
      const penv = new Env(env);
      const ok = this.matchPat(scrut, arm.pat, penv);
      if (isEnum && arm.pat.kind === "case_pat") {
        const ev = scrut as Extract<Val, { k: "enum" }>;
        const idx = caseIndex(ev.def, arm.pat.name);
        if (idx >= 0) covered.add(idx);
      }
      if (ok) {
        return this.eval(arm.body, penv);
      }
    }
    if (isEnum) {
      const ev = scrut as Extract<Val, { k: "enum" }>;
      const missing: string[] = [];
      for (let i = 0; i < ev.def.cases.length; i++) {
        if (!covered.has(i)) missing.push(ev.def.cases[i]!);
      }
      if (missing.length > 0) {
        throw this.errf(x.line, `match 不穷尽，缺少: ${missing.join(", ")}`);
      }
    }
    throw this.errf(x.line, "match 无匹配分支");
  }

  private matchPat(v: Val, pat: import("../syntax/ast.js").Pat, env: Env): boolean {
    switch (pat.kind) {
      case "wild_pat":
        return true;
      case "bind_pat": {
        const e = env.define(pat.name, v, false);
        if (e !== undefined) throw e;
        return true;
      }
      case "lit_pat": {
        const lv = this.eval(pat.val, env);
        const { eq } = equals(v, lv);
        return eq === true;
      }
      case "case_pat": {
        if (v.k !== "enum") return false;
        const idx = caseIndex(v.def, pat.name);
        if (idx < 0 || idx !== v.case) return false;
        if (pat.subs.length !== v.payloads.length) {
          throw this.errf(pat.line, `case ${pat.name} 需要 ${v.payloads.length} 个子模式，得到 ${pat.subs.length} 个`);
        }
        for (let i = 0; i < pat.subs.length; i++) {
          if (!this.matchPat(v.payloads[i]!, pat.subs[i]!, env)) return false;
        }
        return true;
      }
      default: {
        const _never: never = pat;
        return _never;
      }
    }
  }

  // —— 调用 ——

  private evalCall(x: CallEx, env: Env): Val {
    // 构造器调用（Some/None/Ok/Err 与用户 enum case）
    if (x.callee.kind === "ident") {
      const c = this.ctors.get(x.callee.name);
      if (c !== undefined) {
        if (x.args.length !== c.arity) {
          throw this.errf(x.line, `${x.callee.name} 需要 ${c.arity} 个参数，得到 ${x.args.length} 个`);
        }
        const payloads: Val[] = [];
        for (const a of x.args) {
          payloads.push(copyValue(this.eval(a, env)));
        }
        return { k: "enum", def: c.def, case: c.idx, payloads };
      }
    }

    // 方法调用：Callee 为 FieldEx 且对象是 struct
    if (x.callee.kind === "field") {
      const fe = x.callee;
      let ov = this.eval(fe.obj, env);
      if (ov.k === "ptr") ov = ov.cell.val;
      if (ov.k === "struct") {
        const cl = ov.def.methods.get(fe.name);
        if (cl !== undefined) {
          const args = this.evalArgs(x.args, env);
          return this.callMethod(cl, ov, args);
        }
      }
      // 字段存函数值的情况落到通用路径
    }

    const cv = this.eval(x.callee, env);
    const args = this.evalArgs(x.args, env);
    if (cv.k === "closure") return this.callClosure(cv, args);
    if (cv.k === "builtin") return cv.fn(this, args);
    throw this.errf(x.line, `${typeName(cv)} 不可调用`);
  }

  private evalArgs(exprs: readonly Expr[], env: Env): Val[] {
    const args: Val[] = [];
    for (const a of exprs) {
      args.push(copyValue(this.eval(a, env)));
    }
    return args;
  }

  callClosure(cl: Closure, args: Val[]): Val {
    const name = cl.name === "" ? "匿名函数" : cl.name;
    if (args.length !== cl.decl.params.length) {
      throw new RtError(`${name} 需要 ${cl.decl.params.length} 个参数，得到 ${args.length} 个`);
    }
    const fenv = new Env(cl.env);
    for (let i = 0; i < cl.decl.params.length; i++) {
      const e = fenv.define(cl.decl.params[i]!.name, args[i]!, false);
      if (e !== undefined) throw e;
    }
    return this.runBody(cl, fenv, name);
  }

  private callMethod(cl: Closure, recv: Val, args: Val[]): Val {
    const name = cl.name;
    if (args.length !== cl.decl.params.length) {
      throw new RtError(`${name} 需要 ${cl.decl.params.length} 个参数，得到 ${args.length} 个`);
    }
    const fenv = new Env(cl.env);
    if (cl.decl.recv === undefined) {
      throw new RtError(`${name} 不是方法`);
    }
    const e = fenv.define(cl.decl.recv.name, copyValue(recv), false);
    if (e !== undefined) throw e;
    for (let i = 0; i < cl.decl.params.length; i++) {
      const e2 = fenv.define(cl.decl.params[i]!.name, args[i]!, false);
      if (e2 !== undefined) throw e2;
    }
    return this.runBody(cl, fenv, name);
  }

  /** 执行函数体并消化控制流信号（? 传播在边界合成 Err 返回） */
  private runBody(cl: Closure, fenv: Env, name: string): Val {
    try {
      this.execStmts(cl.decl.body, fenv);
    } catch (e) {
      if (e instanceof Signal) {
        if (e.sigKind === "return") return e.sigVal!;
        if (e.sigKind === "err") {
          return {
            k: "enum",
            def: this.enums.get("Result")!,
            case: 1,
            payloads: [e.sigVal!],
          };
        }
        throw e;
      }
      throw e;
    }
    return unitVal;
  }

  /** 调用预载内建（IR 层执行器复用同一套内建实现）。found=false 表示未注册该名字 */
  callBuiltin(name: string, args: Val[]): { v?: Val; found: boolean } {
    const c = this.globals.lookup(name);
    if (c === undefined) return { found: false };
    if (c.val.k !== "builtin") return { found: false };
    return { v: c.val.fn(this, args), found: true };
  }
}
