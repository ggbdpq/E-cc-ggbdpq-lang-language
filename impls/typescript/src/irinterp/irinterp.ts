// Core IR 的树遍历解释器（v0.9 Semantic Integrity）。
//
// 定位：AST 解释器（internal/interp 镜像）是参考语义 Oracle；irinterp 消费与
// →Go/→JS 后端相同的 Core IR，用于四路差分验证——证明「IR 语义 = AST 语义」。
// 值模型复用 interp 的值类型与 fmt（RUNTIME-OUTPUT-003 禁止第二份实现）；
// 内建经 host Interp.callBuiltin 复用同一套实现。预载 Option/Result/error 在
// 本包重建定义（与 host 的 Def 指针解耦，但输出与语义逐字节一致）。

import { RtError } from "../interp/errors.js";
import { Interp } from "../interp/interp.js";
import {
  codePointCompare, copyValue, fmtVal, intV, mapSet, strV,
  type EnumDef, type StructDef, type Val,
} from "../interp/value.js";
import type { IrExpr, IrFn, IrLocal, IrMatch, IrOp, IrPat, IrProg, IrStmt } from "../ir/model.js";
import type { Symbol } from "../semantic/symbols.js";

const unit: Val = { k: "unit" };

/** 预载定义（与 host prelude 同名同序；判定走 Name/Case 序，与 Def 指针解耦） */
const PRELUDE_ENUMS: readonly { name: string; cases: readonly string[] }[] = [
  { name: "Option", cases: ["Some", "None"] },
  { name: "Result", cases: ["Ok", "Err"] },
];
const PRELUDE_CTORS: readonly { name: string; enumName: string; idx: number }[] = [
  { name: "Some", enumName: "Option", idx: 0 },
  { name: "None", enumName: "Option", idx: 1 },
  { name: "Ok", enumName: "Result", idx: 0 },
  { name: "Err", enumName: "Result", idx: 1 },
];

interface Slot {
  val: Val;
}

class Frame {
  readonly vars = new Map<IrLocal, Slot>();
  readonly fns = new Map<string, IrFn>();
  constructor(readonly parent: Frame | undefined) {}

  slot(l: IrLocal): Slot | undefined {
    for (let cur: Frame | undefined = this; cur !== undefined; cur = cur.parent) {
      const p = cur.vars.get(l);
      if (p !== undefined) return p;
    }
    return undefined;
  }

  lookupFn(name: string): IrFn | undefined {
    for (let cur: Frame | undefined = this; cur !== undefined; cur = cur.parent) {
      const fn = cur.fns.get(name);
      if (fn !== undefined) return fn;
    }
    return undefined;
  }
}

interface CtorInfo {
  def: EnumDef;
  idx: number;
}

type Flow = "normal" | "break" | "continue" | "return";

interface Step {
  flow: Flow;
  val?: Val;
}

class TryErr extends Error {
  constructor(readonly val: Val) {
    super("Err 传播");
    this.name = "TryErr";
  }
}

interface AssignSlot {
  read(): Val;
  write(v: Val): void;
}

interface IrFnVal {
  vk: "irfn";
  fn?: IrFn;
  lit?: Extract<IrExpr, { kind: "fn_lit" }>;
  env?: Frame;
  name: string;
}

interface IrBuiltinVal {
  vk: "irbuiltin";
  name: string;
}

const NORMAL: Step = { flow: "normal" };

/** int64 二补码回绕 */
function wrap64(n: bigint): bigint {
  const M = 1n << 64n;
  let r = ((n % M) + M) % M;
  if (r >= 1n << 63n) r -= M;
  return r;
}

export class Machine {
  private host: Interp;
  private readonly fns = new Map<string, IrFn>();
  private readonly methods = new Map<string, IrFn>();
  private readonly ctors = new Map<string, CtorInfo>();
  private readonly structs = new Map<string, StructDef>();
  private readonly enums = new Map<string, EnumDef>();
  private globals!: Frame;
  /** 按符号 ID 的全局槽位：IR 引用侧的 Local 可能是独立实例，以 Sym.ID 归位同一存储 */
  private readonly globalSlots = new Map<number, Slot>();

  constructor(readonly out: { write(s: string): void }) {
    this.host = new Interp({ write: () => {} });
    for (const pd of PRELUDE_ENUMS) {
      this.enums.set(pd.name, { name: pd.name, cases: pd.cases });
    }
    for (const ci of PRELUDE_CTORS) {
      this.ctors.set(ci.name, { def: this.enums.get(ci.enumName)!, idx: ci.idx });
    }
    this.structs.set("error", { name: "error", fields: ["msg"], methods: new Map() });
  }

  /** 执行程序：声明注册 → 全局初始化（合并序）→ 调 main */
  run(p: IrProg): void {
    this.globals = new Frame(undefined);
    for (const sd of p.structs) {
      this.structs.set(sd.name, { name: sd.name, fields: sd.fields.map((f) => f.name), methods: new Map() });
    }
    for (const ed of p.enums) {
      const def: EnumDef = { name: ed.name, cases: ed.cases.map((c) => c.name) };
      this.enums.set(ed.name, def);
      for (let i = 0; i < ed.cases.length; i++) {
        this.ctors.set(ed.cases[i]!.name, { def, idx: i });
      }
    }
    for (const fn of p.fns) {
      if (fn.recv !== undefined) {
        const recvName = fn.recv.typ.kind === "struct" && fn.recv.typ.sym !== undefined ? fn.recv.typ.sym.name : "unknown";
        this.methods.set(`${recvName}.${fn.name}`, fn);
        continue;
      }
      this.fns.set(fn.name, fn);
    }
    for (const g of p.globals) {
      const v = this.eval(g.init, this.globals);
      const slot: Slot = { val: copyValue(v) };
      this.globals.vars.set(g.local, slot);
      this.indexGlobal(g.local, slot);
    }
    if (p.main !== undefined) {
      this.callFn(p.main, undefined, []);
    }
  }

  private indexGlobal(l: IrLocal, slot: Slot): void {
    if (l.sym !== undefined) this.globalSlots.set(l.sym.id, slot);
  }

  private globalSlot(l: IrLocal): Slot | undefined {
    if (l.sym !== undefined) {
      const p = this.globalSlots.get(l.sym.id);
      if (p !== undefined) return p;
    }
    return undefined;
  }

  // —— 语句 ——

  private execStmts(stmts: readonly IrStmt[], f: Frame): Step {
    for (const st of stmts) {
      const s = this.exec(st, f);
      if (s.flow !== "normal") return s;
    }
    return NORMAL;
  }

  private exec(st: IrStmt, f: Frame): Step {
    switch (st.kind) {
      case "let": {
        const v = this.eval(st.init, f);
        f.vars.set(st.local, { val: copyValue(v) });
        return NORMAL;
      }
      case "assign":
        return this.execAssign(st, f);
      case "eval":
        this.eval(st.x, f);
        return NORMAL;
      case "return": {
        let v: Val = unit;
        if (st.val !== undefined) v = copyValue(this.eval(st.val, f));
        return { flow: "return", val: v };
      }
      case "if": {
        const c = this.eval(st.cond, f);
        if (truthyRaw(c)) return this.execStmts(st.then, new Frame(f));
        return this.execStmts(st.els, new Frame(f));
      }
      case "while":
        for (;;) {
          const c = this.eval(st.cond, f);
          if (!truthyRaw(c)) return NORMAL;
          const s = this.execStmts(st.body, new Frame(f));
          if (s.flow === "break" || s.flow === "return") return s;
        }
      case "for_in":
        return this.execForIn(st, f);
      case "break":
        return { flow: "break" };
      case "continue":
        return { flow: "continue" };
      case "spawn": {
        // TASK-SPAWN-002 同步确定子集：实参就地求值后同步执行完
        const args = this.evalArgs(st.args, f);
        const fn = st.fn !== undefined ? this.fns.get(st.fn.name) : undefined;
        if (fn === undefined) {
          throw new RtError(`go 的目标未注册: ${st.fn?.name ?? "?"}`);
        }
        this.callFn(fn, undefined, args);
        return NORMAL;
      }
      case "fn_def":
        f.fns.set(st.fn.name, st.fn);
        return NORMAL;
      default: {
        const _never: never = st;
        return _never;
      }
    }
  }

  private execForIn(st: Extract<IrStmt, { kind: "for_in" }>, f: Frame): Step {
    const it = this.eval(st.iter, f);
    const items: Val[] = [];
    switch (st.iterKind) {
      case "slice":
        items.push(...(it as Extract<Val, { k: "arr" }>).items);
        break;
      case "map":
        items.push(...(it as Extract<Val, { k: "map" }>).ks);
        break;
      case "string":
        for (const r of (it as Extract<Val, { k: "str" }>).v) items.push(strV(r));
        break;
      case "range": {
        const rv = it as Extract<Val, { k: "range" }>;
        for (let i = rv.lo; i < rv.hi; i++) items.push(intV(i));
        break;
      }
      default:
        throw new RtError("该类型不可迭代");
    }
    for (const item of items) {
      const body = new Frame(f);
      body.vars.set(st.variable, { val: item });
      const s = this.execStmts(st.body, body);
      if (s.flow === "break") break;
      if (s.flow === "return") return s;
    }
    return NORMAL;
  }

  // —— 赋值（地址组件先于右值） ——

  private evalSlot(tgt: IrExpr, f: Frame): AssignSlot {
    switch (tgt.kind) {
      case "local_get": {
        const p = f.slot(tgt.local);
        if (p === undefined) {
          const gp = this.globalSlot(tgt.local);
          if (gp !== undefined) return { read: () => gp.val, write: (v) => void (gp.val = v) };
          throw new RtError(`未定义变量 ${tgt.local.name}`);
        }
        return { read: () => p.val, write: (v) => void (p.val = v) };
      }
      case "field": {
        let ov = this.eval(tgt.obj, f);
        if (ov.k === "ptr") ov = ov.cell.val; // &x 的字段读写落回原值
        const sv = asStruct(ov);
        const idx = sv.def.fields.indexOf(tgt.name);
        if (idx < 0) throw new RtError(`${sv.def.name} 没有字段 ${tgt.name}`);
        return { read: () => sv.fields[idx]!, write: (v) => void (sv.fields[idx] = v) };
      }
      case "index": {
        const ov = this.eval(tgt.obj, f);
        const iv = this.eval(tgt.idx, f);
        if (ov.k === "arr") {
          const i = intIndex(iv, ov.items.length);
          return { read: () => ov.items[i]!, write: (v) => void (ov.items[i] = v) };
        }
        if (ov.k === "map") {
          if (keyStringOf(iv) === undefined) throw new RtError("映射键须为 int/string/bool");
          return {
            read: () => {
              const ks = keyStringOf(iv)!;
              const v = ov.m.get(ks);
              return v === undefined ? unit : v;
            },
            write: (v) => void mapSet(ov, iv, v),
          };
        }
        throw new RtError("该类型不可索引赋值");
      }
      default:
        throw new RtError("赋值目标不合法");
    }
  }

  private execAssign(st: Extract<IrStmt, { kind: "assign" }>, f: Frame): Step {
    const slot = this.evalSlot(st.target, f);
    const v = this.eval(st.val, f);
    if (st.op === "=") {
      slot.write(copyValue(v));
      return NORMAL;
    }
    // 复合赋值：读旧值 → 运算 → 写回；目标读出数组本身且为 += 时原位拼接
    if (st.op === "+=") {
      const cur = slot.read();
      if (cur.k === "arr") {
        if (v.k !== "arr") throw new RtError(`数组 += 需要数组，得到 ${typeName(v)}`);
        cur.items.push(...v.items);
        return NORMAL;
      }
    }
    const combined = this.arith(compoundToBinary(st.op), slot.read(), v);
    slot.write(combined);
    return NORMAL;
  }

  // —— 表达式 ——

  private eval(e: IrExpr, f: Frame): Val {
    switch (e.kind) {
      case "const":
        switch (e.constKind) {
          case "int":
            return intV(e.i64);
          case "float":
            return { k: "float", v: e.f64 };
          case "str":
            return strV(e.s);
          case "bool":
            return { k: "bool", v: e.b };
          default: {
            const _never: never = e.constKind;
            return _never;
          }
        }
      case "local_get": {
        const p = f.slot(e.local);
        if (p === undefined) {
          const gp = this.globalSlot(e.local);
          if (gp !== undefined) return gp.val;
          throw new RtError(`未定义变量 ${e.local.name}`);
        }
        return p.val;
      }
      case "fn_get": {
        const fn = this.fns.get(e.name);
        if (fn !== undefined) return { vk: "irfn", fn, name: e.name } as unknown as Val;
        throw new RtError(`未注册函数 ${e.name}`);
      }
      case "builtin_get":
        return { vk: "irbuiltin", name: e.name } as unknown as Val;
      case "ctor": {
        const args = this.evalArgs(e.args, f);
        const ci = this.ctors.get(e.sym.name);
        if (ci === undefined) throw new RtError(`未知构造器 ${e.sym.name}`);
        return { k: "enum", def: ci.def, case: ci.idx, payloads: args };
      }
      case "call":
        return this.evalCall(e, f);
      case "unary": {
        const v = this.eval(e.x, f);
        if (e.op === "-") {
          if (v.k === "int") return intV(wrap64(-v.v));
          if (v.k === "float") return { k: "float", v: -v.v };
          throw new RtError("一元 - 不适用于该类型");
        }
        if (e.op === "!") return { k: "bool", v: !truthyRaw(v) };
        // &x（VALUE-REF-003）：仅 LocalGet 有稳定槽位
        if (e.op === "&") {
          if (e.x.kind === "local_get") {
            const p = f.slot(e.x.local);
            if (p !== undefined) return { k: "ptr", cell: p };
          }
          throw new RtError("& 仅可作用于变量");
        }
        throw new RtError(`irinterp: 未支持的一元运算 ${e.op}`);
      }
      case "binary": {
        const lv = this.eval(e.l, f);
        // EVAL-SHORTCUT-006
        if (e.op === "&&") {
          if (!truthyRaw(lv)) return { k: "bool", v: false };
          return { k: "bool", v: truthyRaw(this.eval(e.r, f)) };
        }
        if (e.op === "||") {
          if (truthyRaw(lv)) return { k: "bool", v: true };
          return { k: "bool", v: truthyRaw(this.eval(e.r, f)) };
        }
        const rv = this.eval(e.r, f);
        return this.evalBinary(e.op, lv, rv);
      }
      case "range": {
        const lo = this.eval(e.lo, f);
        const hi = this.eval(e.hi, f);
        if (lo.k !== "int" || hi.k !== "int") throw new RtError("区间两端须为 int");
        return { k: "range", lo: lo.v, hi: hi.v };
      }
      case "index": {
        const ov = this.eval(e.obj, f);
        const iv = this.eval(e.idx, f);
        if (ov.k === "arr") {
          const i = intIndex(iv, ov.items.length);
          return ov.items[i]!;
        }
        if (ov.k === "map") {
          const { found, v } = mapGetRaw(ov, iv);
          if (!found) throw new RtError(`映射无键 ${fmtVal(iv, true)}（用 has() 探测）`);
          return v!;
        }
        if (ov.k === "str") {
          const runes = Array.from(ov.v);
          const i = intIndex(iv, runes.length);
          return strV(runes[i]!);
        }
        throw new RtError("该类型不可索引");
      }
      case "slice": {
        const ov = this.eval(e.obj, f);
        const loV = this.eval(e.lo, f);
        const hiV = this.eval(e.hi, f);
        if (loV.k !== "int" || hiV.k !== "int") throw new RtError("切片端点须为 int");
        const lo = Number(loV.v);
        const hi = Number(hiV.v);
        if (ov.k === "arr") {
          if (lo < 0 || hi > ov.items.length || lo > hi) {
            throw new RtError(`切片区间 [${lo},${hi}) 越界（长度 ${ov.items.length}）`);
          }
          return { k: "arr", items: ov.items.slice(lo, hi) };
        }
        if (ov.k === "str") {
          const rs = Array.from(ov.v);
          if (lo < 0 || hi > rs.length || lo > hi) {
            throw new RtError(`切片区间 [${lo},${hi}) 越界（长度 ${rs.length}）`);
          }
          return strV(rs.slice(lo, hi).join(""));
        }
        throw new RtError("该类型不可切片");
      }
      case "field": {
        let ov = this.eval(e.obj, f);
        if (ov.k === "ptr") ov = ov.cell.val; // &x 的字段读落回原值
        const sv = asStruct(ov);
        const idx = sv.def.fields.indexOf(e.name);
        if (idx < 0) throw new RtError(`${sv.def.name} 没有字段 ${e.name}`);
        return sv.fields[idx]!;
      }
      case "try": {
        const v = this.eval(e.x, f);
        if (v.k !== "enum") throw new RtError(`? 的操作数须为 Result，得到 ${typeName(v)}`);
        if (v.def.name === "Result" && v.case === 0) return v.payloads[0]!;
        // Err(e)：传播出当前函数（函数边界合成 Err 返回）
        throw new TryErr(v);
      }
      case "struct_lit": {
        const name = e.struct !== undefined ? e.struct.name : "";
        const def = this.structs.get(name);
        if (def === undefined) throw new RtError(`未知 struct ${name}`);
        const vals = new Map<string, Val>();
        for (const fv of e.fields) {
          vals.set(fv.name, this.eval(fv.val, f));
        }
        const fields = def.fields.map((fname) => vals.get(fname) ?? unit);
        return { k: "struct", def, fields };
      }
      case "map_lit": {
        const mm: Val = { k: "map", ks: [], m: new Map() };
        for (let i = 0; i < e.keys.length; i++) {
          const k = this.eval(e.keys[i]!, f);
          const v = this.eval(e.vals[i]!, f);
          const err = mapSet(mm as Extract<Val, { k: "map" }>, k, v);
          if (err !== undefined) throw new RtError(err);
        }
        return mm;
      }
      case "slice_lit": {
        const items = e.elems.map((el) => this.eval(el, f));
        return { k: "arr", items };
      }
      case "match":
        return this.evalMatch(e, f);
      case "fn_lit":
        return { vk: "irfn", lit: e, env: f, name: "" } as unknown as Val;
      default: {
        const _never: never = e;
        return _never;
      }
    }
  }

  // —— 调用 ——

  private evalCall(x: Extract<IrExpr, { kind: "call" }>, f: Frame): Val {
    if (x.builtin !== undefined && x.builtin !== "") {
      const args = this.evalArgs(x.args, f);
      return this.callBuiltin(x.builtin, args);
    }
    if (x.method !== undefined) {
      const recv = this.eval(x.recv!, f);
      const sv = asStruct(recv);
      const fn = this.methods.get(`${sv.def.name}.${x.method.name}`);
      if (fn === undefined) throw new RtError(`${sv.def.name} 没有方法 ${x.method.name}`);
      const args = this.evalArgs(x.args, f);
      return this.callFn(fn, recv, args);
    }
    if (x.fn !== undefined) {
      const args = this.evalArgs(x.args, f);
      const fn = this.fns.get(x.fn.name);
      if (fn === undefined) {
        const nested = f.lookupFn(x.fn.name);
        if (nested !== undefined) return this.callFn(nested, undefined, args);
        throw new RtError(`未注册函数 ${x.fn.name}`);
      }
      return this.callFn(fn, undefined, args);
    }
    const cv = this.eval(x.callee!, f);
    const args = this.evalArgs(x.args, f);
    const fv = cv as unknown as IrFnVal;
    if (fv.vk === "irfn") return this.callFnVal(fv, args);
    if (fv.vk === "irbuiltin") return this.callBuiltin(fv.name, args);
    throw new RtError("该值不可调用");
  }

  private evalArgs(args: readonly IrExpr[], f: Frame): Val[] {
    return args.map((a) => this.eval(a, f));
  }

  private callBuiltin(name: string, args: Val[]): Val {
    if (name === "println") {
      this.out.write(fmtArgs(args) + "\n");
      return unit;
    }
    if (name === "print") {
      this.out.write(fmtArgs(args));
      return unit;
    }
    const { v, found } = this.host.callBuiltin(name, args);
    if (!found) throw new RtError(`irinterp: 未支持的内建 ${name}`);
    return v!;
  }

  private callFn(fn: IrFn, recv: Val | undefined, args: Val[]): Val {
    if (fn.extern) throw new RtError("extern 函数仅转译可用");
    const f = new Frame(this.globals);
    if (fn.recv !== undefined) {
      f.vars.set(fn.recv, { val: copyValue(recv ?? unit) });
    }
    for (let i = 0; i < fn.params.length; i++) {
      const v = i < args.length ? copyValue(args[i]!) : unit;
      f.vars.set(fn.params[i]!, { val: v });
    }
    return this.execBody(fn.body, f);
  }

  private callFnVal(fv: IrFnVal, args: Val[]): Val {
    if (fv.fn !== undefined) return this.callFn(fv.fn, undefined, args);
    if (fv.lit !== undefined && fv.env !== undefined) {
      const f = new Frame(fv.env);
      // VALUE-CAPTURE-004 按引用捕获：Locals 中已存在于捕获链的槽位直接复用
      for (const l of fv.lit.locals) {
        const p = fv.env.slot(l);
        if (p !== undefined) f.vars.set(l, p);
      }
      for (let i = 0; i < fv.lit.params.length; i++) {
        const v = i < args.length ? copyValue(args[i]!) : unit;
        f.vars.set(fv.lit.params[i]!, { val: v });
      }
      return this.execBody(fv.lit.body, f);
    }
    throw new RtError("空函数值不可调用");
  }

  /** 执行函数体；Err 传播（TryErr）到函数边界合成返回值（RESULT-TRY-003） */
  private execBody(body: readonly IrStmt[], f: Frame): Val {
    let s: Step;
    try {
      s = this.execStmts(body, f);
    } catch (e) {
      if (e instanceof TryErr) return e.val;
      throw e;
    }
    if (s.flow === "return") return s.val ?? unit;
    return unit;
  }

  // —— match ——

  private evalMatch(x: IrMatch, f: Frame): Val {
    const scrut = this.eval(x.scrut, f);
    for (const arm of x.arms) {
      const bind = new Frame(f);
      if (!this.matchPat(arm.pat, scrut, bind)) continue;
      return this.eval(arm.body, bind);
    }
    throw new RtError("match 无匹配分支");
  }

  private matchPat(p: IrPat, v: Val, bind: Frame): boolean {
    switch (p.kind) {
      case "wild_pat":
        return true;
      case "bind_pat":
        bind.vars.set(p.local, { val: v });
        return true;
      case "lit_pat": {
        const lv = this.eval(p.val, bind);
        return eqValues(lv, v);
      }
      case "ctor_pat": {
        if (v.k !== "enum") return false;
        if (v.def.cases[v.case] !== p.sym?.name) return false;
        for (let i = 0; i < p.subs.length; i++) {
          if (i >= v.payloads.length) return false;
          if (!this.matchPat(p.subs[i]!, v.payloads[i]!, bind)) return false;
        }
        return true;
      }
      default: {
        const _never: never = p;
        return _never;
      }
    }
  }

  // —— 运算 ——

  private evalBinary(op: IrOp, lv: Val, rv: Val): Val {
    if (op === "==") return { k: "bool", v: eqValues(lv, rv) };
    if (op === "!=") return { k: "bool", v: !eqValues(lv, rv) };
    if (op === "<" || op === "<=" || op === ">" || op === ">=") return this.compare(op, lv, rv);
    return this.arith(op, lv, rv);
  }

  private arith(op: IrOp, lv: Val, rv: Val): Val {
    if (lv.k === "int") {
      if (rv.k !== "int") throw new RtError("int 与 float 不可混合运算（无隐式转换）");
      const a = lv.v;
      const b = rv.v;
      switch (op) {
        case "+":
          return intV(wrap64(a + b));
        case "-":
          return intV(wrap64(a - b));
        case "*":
          return intV(wrap64(a * b));
        case "/":
          if (b === 0n) throw new RtError("整数除零");
          return intV(a / b);
        case "%":
          if (b === 0n) throw new RtError("整数模零");
          return intV(a % b);
        case "&":
          return intV(a & b);
        case "|":
          return intV(a | b);
        case "^":
          return intV(a ^ b);
        case "<<":
          return intV(shiftLeft(a, b));
        case ">>":
          return intV(shiftRight(a, b));
        default:
          break;
      }
    } else if (lv.k === "float") {
      if (rv.k !== "float") throw new RtError("int 与 float 不可混合运算（无隐式转换）");
      const a = lv.v;
      const b = rv.v;
      switch (op) {
        case "+":
          return { k: "float", v: a + b };
        case "-":
          return { k: "float", v: a - b };
        case "*":
          return { k: "float", v: a * b };
        case "/":
          return { k: "float", v: a / b };
        default:
          break;
      }
    } else if (lv.k === "str") {
      if (rv.k !== "str") throw new RtError("string 只能与 string 拼接");
      if (op === "+" || op === "+=") return strV(lv.v + rv.v);
    }
    throw new RtError("运算不适用于该类型");
  }

  private compare(op: IrOp, lv: Val, rv: Val): Val {
    if (lv.k === "int" && rv.k === "int") {
      const cmp = lv.v < rv.v ? -1 : lv.v > rv.v ? 1 : 0;
      return { k: "bool", v: applyCmp(op, cmp) };
    }
    if (lv.k === "float" && rv.k === "float") {
      // IEEE：NaN 参与任何比较一律 false（TYPE-CONV-005 v1.0.1，ADR-0006/RS-007）；
      // 直落运算符，不经全序 cmp（NaN 被兜成 0 会把 `<=`/`>=` 错判为 true）
      const a = lv.v;
      const b = rv.v;
      const v = op === "<" ? a < b : op === "<=" ? a <= b : op === ">" ? a > b : a >= b;
      return { k: "bool", v };
    }
    if (lv.k === "str" && rv.k === "str") {
      // Go string 比较：字节序 = 码点序
      const cmp = codePointCompare(lv.v, rv.v);
      return { k: "bool", v: applyCmp(op, cmp) };
    }
    throw new RtError("该类型不可比较");
  }
}

function applyCmp(op: IrOp, cmp: number): boolean {
  if (op === "<") return cmp < 0;
  if (op === "<=") return cmp <= 0;
  if (op === ">") return cmp > 0;
  return cmp >= 0;
}

/** Go int64 <<：shift >= 64 为 0；负数移位为宿主 panic 面（按运行时错误呈现） */
function shiftLeft(a: bigint, b: bigint): bigint {
  if (b < 0n) throw new RtError("negative shift count");
  if (b >= 64n) return 0n;
  return wrap64(a << b);
}

/** Go int64 >>：算术右移；shift >= 64 时按符号填满 */
function shiftRight(a: bigint, b: bigint): bigint {
  if (b < 0n) throw new RtError("negative shift count");
  if (b >= 64n) return a < 0n ? -1n : 0n;
  return a >> b;
}

function compoundToBinary(op: IrOp): IrOp {
  switch (op) {
    case "+=":
      return "+";
    case "-=":
      return "-";
    case "*=":
      return "*";
    case "/=":
      return "/";
    default:
      return op;
  }
}

function fmtArgs(args: Val[]): string {
  return args.map((a) => fmtVal(a, true)).join(" ");
}

function truthyRaw(v: Val): boolean {
  return v.k === "bool" && v.v;
}

function asStruct(v: Val): Extract<Val, { k: "struct" }> {
  if (v.k === "struct") return v;
  throw new RtError(`该值不是 struct（是 ${typeName(v)}）`);
}

function intIndex(v: Val, length: number): number {
  if (v.k !== "int") throw new RtError("索引须为 int");
  const n = Number(v.v);
  if (n < 0 || n >= length) throw new RtError(`索引 ${n} 越界（长度 ${length}）`);
  return n;
}

function keyStringOf(v: Val): string | undefined {
  switch (v.k) {
    case "int":
      return `i:${v.v.toString(10)}`;
    case "str":
      return `s:${v.v}`;
    case "bool":
      return `b:${v.v ? "true" : "false"}`;
    default:
      return undefined;
  }
}

function mapGetRaw(m: Extract<Val, { k: "map" }>, k: Val): { found: boolean; v?: Val } {
  const ks = keyStringOf(k);
  if (ks === undefined) return { found: false };
  const v = m.m.get(ks);
  return v === undefined ? { found: false } : { found: true, v };
}

/** irinterp 的 ==：失配即 false（不报错）；enum 按名与 case 序比较 */
function eqValues(a: Val, b: Val): boolean {
  switch (a.k) {
    case "int":
      return b.k === "int" && a.v === b.v;
    case "float":
      return b.k === "float" && a.v === b.v;
    case "bool":
      return b.k === "bool" && a.v === b.v;
    case "str":
      return b.k === "str" && a.v === b.v;
    case "enum": {
      if (b.k !== "enum") return false;
      if (a.def.name !== b.def.name || a.case !== b.case || a.payloads.length !== b.payloads.length) return false;
      for (let i = 0; i < a.payloads.length; i++) {
        if (!eqValues(a.payloads[i]!, b.payloads[i]!)) return false;
      }
      return true;
    }
    default:
      return false;
  }
}

function typeName(v: Val): string {
  switch (v.k) {
    case "int":
      return "int";
    case "float":
      return "float";
    case "bool":
      return "bool";
    case "str":
      return "string";
    case "arr":
      return "数组";
    case "map":
      return "映射";
    case "struct":
      return "struct";
    case "enum":
      return "enum";
    default:
      return "未知值";
  }
}
