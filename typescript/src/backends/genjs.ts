// →JS 后端：Core IR → 自包含可执行 ES Module（prelude 内联）。
// 平台映射：int=BigInt（64 位语义精确）、全函数 async（await-everything 吸收染色）、
// struct 值语义经 langGoCopy 三时机（绑定/传参/返回）。

import type { Symbol } from "../semantic/symbols.js";
import type { IrExpr, IrFn, IrMatch, IrPat, IrProg, IrStmt } from "../ir/model.js";
import { jsPrelude } from "./jsprelude.js";
import { goQuote } from "./gen.js";

/** JS 侧字符串常量发射：双引号 + 最小转义（与 Go strconv.Quote 输出兼容） */
function jsStr(s: string): string {
  return goQuote(s);
}

/** Go strconv.FormatFloat(f,'g',-1,64) 等价（常量发射用） */
function goFloatLit(f: number): string {
  if (Number.isNaN(f)) return "NaN";
  if (f === Number.POSITIVE_INFINITY) return "+Inf";
  if (f === Number.NEGATIVE_INFINITY) return "-Inf";
  return String(f);
}

const builtins: Readonly<Record<string, string>> = {
  println: "langGoPrintln", print: "langGoPrint", len: "langGoLen",
  str: "langGoStr", int: "langGoInt", float: "langGoFloat",
  keys: "langGoKeys", values: "langGoValues", has: "langGoHas",
  push: "langGoPush", assert: "langGoAssert", err: "langGoErr",
  bytes: "langGoBytes", char: "langGoChar",
  jsonParse: "langGoJSONParse", jsonStringify: "langGoJSONStringify",
  isNone: "langGoIsNone", makeChan: "langGoNewChan",
  send: "langGoChanSend", recv: "langGoChanRecv",
};

type IrArm = { readonly pat: IrPat; readonly body: IrExpr };

class JsGen {
  private lines: string[] = [];
  private indent = 0;
  private readonly structs = new Map<string, IrProg["structs"][number]>();
  private readonly fns = new Map<Symbol, IrFn>();
  private tmpN = 0;
  private curFn: IrFn | undefined;

  constructor(private readonly prog: IrProg) {}

  private w(line: string): void {
    this.lines.push("  ".repeat(this.indent) + line);
  }

  private out(): string {
    return this.lines.join("\n") + "\n";
  }

  private newTmp(): string {
    this.tmpN++;
    return `_t${this.tmpN}`;
  }

  private collectNested(list: readonly IrStmt[]): void {
    for (const s of list) {
      if (s.kind === "fn_def") {
        if (s.fn.sym !== undefined) this.fns.set(s.fn.sym, s.fn);
        this.collectNested(s.fn.body);
      }
    }
  }

  emitProgram(): string {
    this.lines.push(jsPrelude.replace(/\n$/, ""));
    for (const s of this.prog.structs) this.structs.set(s.name, s);
    for (const f of this.prog.fns) {
      if (f.sym !== undefined) this.fns.set(f.sym, f);
      this.collectNested(f.body);
    }
    // 用户 enum 构造器
    for (const en of this.prog.enums) {
      for (const c of en.cases) {
        const params = c.payloads.map((_, i) => `p${i}`);
        const vals = params.length > 0 ? `[${params.join(", ")}]` : "[]";
        this.w(`function le_${en.name}_${c.name}(${params.join(", ")}) { return langGoEnumVal(${JSON.stringify(c.name)}, ${vals}); }`);
        this.w("");
      }
    }
    // 全局绑定
    for (const gl of this.prog.globals) {
      const expr = this.expr(gl.init);
      this.w(`let ${gl.local.name} = langGoCopy(${expr});`);
      this.w("");
    }
    // 方法与函数
    for (const f of this.prog.fns) {
      this.emitFunc(f);
    }
    if (this.prog.main !== undefined) {
      this.w("await lf_main();");
    }
    return this.out();
  }

  private fnName(f: IrFn): string {
    if (f.recv !== undefined) {
      let recvName = "unknown";
      const st = f.recv.typ;
      if (st !== undefined && st.kind === "struct" && st.sym !== undefined) recvName = st.sym.name;
      return `lf_${recvName}_${f.name}`;
    }
    return `lf_${f.name}`;
  }

  private emitFunc(f: IrFn): void {
    const saved = this.curFn;
    this.curFn = f;
    const ps: string[] = [];
    if (f.recv !== undefined) ps.push(f.recv.name);
    for (const p of f.params) ps.push(p.name);
    this.w(`async function ${this.fnName(f)}(${ps.join(", ")}) {`);
    // 值语义：参数进入函数体即拷贝
    this.indent = 1;
    if (f.recv !== undefined) {
      this.w(`  ${f.recv.name} = langGoCopy(${f.recv.name});`);
    }
    for (const p of f.params) {
      this.w(`  ${p.name} = langGoCopy(${p.name});`);
    }
    this.emitBody(f.body);
    this.indent = 0;
    this.curFn = saved;
    this.w("}");
    this.w("");
  }

  private emitBody(stmts: readonly IrStmt[]): void {
    for (const st of stmts) this.emitStmt(st);
  }

  // —— 语句 ——

  private emitStmt(s: IrStmt): void {
    switch (s.kind) {
      case "let":
        this.emitLet(s);
        return;

      case "assign":
        this.emitAssign(s);
        return;

      case "eval": {
        const expr = this.expr(s.x);
        if (s.x.kind === "match") {
          this.w(`await ${expr};`);
        } else {
          this.w(`${expr};`);
        }
        return;
      }

      case "return":
        this.emitReturn(s);
        return;

      case "if": {
        const cond = this.expr(s.cond);
        this.w(`if (${cond}) {`);
        this.emitBlock(s.then);
        if (s.els.length > 0) {
          this.w("} else {");
          this.emitBlock(s.els);
        }
        this.w("}");
        return;
      }

      case "while": {
        const cond = this.expr(s.cond);
        this.w(`while (${cond}) {`);
        this.emitBlock(s.body);
        this.w("}");
        return;
      }

      case "for_in":
        this.emitForIn(s);
        return;

      case "break":
        this.w("break");
        return;
      case "continue":
        this.w("continue");
        return;

      case "spawn": {
        // →JS 单线程：go 同步立即执行（顺序确定）
        const fi = s.fn !== undefined ? this.fns.get(s.fn) : undefined;
        if (fi === undefined) {
          throw new JsError(`内部错误：go 目标未注册 ${s.fn?.name ?? "?"}`);
        }
        const args = s.args.map((a) => `langGoCopy(${this.expr(a)})`);
        this.w(`await ${this.fnName(fi)}(${args.join(", ")});`);
        return;
      }

      case "fn_def": {
        // 嵌套 fn：JS 提升，递归可用
        this.emitFunc(s.fn);
        return;
      }

      default: {
        const _never: never = s;
        return _never;
      }
    }
  }

  /** let/var：? 在初始化顶层时先展开传播 */
  private emitLet(st: Extract<IrStmt, { kind: "let" }>): void {
    const name = st.local.name;
    if (st.init.kind === "try") {
      const tmp = this.genTry(st.init);
      if (name !== "_") {
        this.w(`let ${name} = langGoCopy(${tmp}.v);`);
      }
      return;
    }
    const expr = this.expr(st.init);
    if (name === "_") {
      this.w(`${expr};`);
      return;
    }
    if (isRef(st.init)) {
      this.w(`let ${name} = ${expr};`); // & 引用赋值：不拷贝
    } else {
      this.w(`let ${name} = langGoCopy(await ${expr});`);
    }
  }

  private emitAssign(st: Extract<IrStmt, { kind: "assign" }>): void {
    if (st.op !== "=") {
      this.emitCompoundAssign(st);
      return;
    }
    const tgt = st.target;
    if (tgt.kind === "local_get") {
      if (st.val.kind === "try") {
        const tmp = this.genTry(st.val);
        this.w(`${tgt.local.name} = langGoCopy(${tmp}.v);`);
        return;
      }
      const expr = this.expr(st.val);
      if (isRef(st.val)) {
        this.w(`${tgt.local.name} = ${expr};`);
      } else {
        this.w(`${tgt.local.name} = langGoCopy(await ${expr});`);
      }
      return;
    }
    if (tgt.kind === "field") {
      const obj = this.expr(tgt.obj);
      const expr = this.expr(st.val);
      this.w(`${obj}.${tgt.name} = langGoCopy(await ${expr});`);
      return;
    }
    if (tgt.kind === "index") {
      const obj = this.expr(tgt.obj);
      const idx = this.expr(tgt.idx);
      const expr = this.expr(st.val);
      this.w(`langGoSetIndex(${obj}, ${idx}, langGoCopy(await ${expr}));`);
      return;
    }
    throw new JsError("赋值目标不合法");
  }

  /** 复合赋值：ident/field 原生 +=；数组 += 元素拼接；index 复合走 langGoAddIndex */
  private emitCompoundAssign(st: Extract<IrStmt, { kind: "assign" }>): void {
    const op = st.op;
    const tgt = st.target;
    if (tgt.kind === "local_get") {
      const expr = this.expr(st.val);
      if (op === "+=") {
        // 数组 += 是元素拼接；数值/字符串原生 += 对 BigInt/number/string 均正确
        this.w(
          `if (Array.isArray(${tgt.local.name})) { ${tgt.local.name}.push(...langGoIter(${expr})) } else { ${tgt.local.name} ${op} ${expr} }`,
        );
        return;
      }
      this.w(`${tgt.local.name} ${op} ${expr};`);
      return;
    }
    if (tgt.kind === "field") {
      const obj = this.expr(tgt.obj);
      const expr = this.expr(st.val);
      this.w(`${obj}.${tgt.name} ${op} ${expr};`);
      return;
    }
    if (tgt.kind === "index") {
      const obj = this.expr(tgt.obj);
      const idx = this.expr(tgt.idx);
      const expr = this.expr(st.val);
      this.w(`langGoAddIndex(${obj}, ${idx}, ${expr}, ${JSON.stringify(op)})`);
      return;
    }
    throw new JsError("赋值目标不合法");
  }

  private emitReturn(st: Extract<IrStmt, { kind: "return" }>): void {
    if (st.val === undefined) {
      this.w("return;");
      return;
    }
    if (st.val.kind === "try") {
      const tmp = this.genTry(st.val);
      this.w(`return langGoCopy(${tmp}.v);`);
      return;
    }
    if (this.curFn !== undefined && this.curFn.ret !== undefined && this.curFn.ret.kind === "result") {
      const ctor = st.val;
      if (ctor.kind !== "ctor" || (ctor.sym.name !== "Ok" && ctor.sym.name !== "Err") || ctor.args.length !== 1) {
        throw new JsError("内部错误：Result return 形态异常");
      }
      const expr = this.expr(ctor.args[0]!);
      if (ctor.sym.name === "Ok") {
        this.w(`return { __langGoRes: true, ok: true, v: langGoCopy(await ${expr}), e: undefined };`);
      } else {
        this.w(`return { __langGoRes: true, ok: false, v: undefined, e: langGoCopy(${expr}) };`);
      }
      return;
    }
    const expr = this.expr(st.val);
    this.w(`return langGoCopy(await ${expr});`);
  }

  /** 展开 ? 传播 */
  private genTry(t: Extract<IrExpr, { kind: "try" }>): string {
    if (this.curFn === undefined || this.curFn.ret === undefined || this.curFn.ret.kind !== "result") {
      throw new JsError("带 ? 的函数须注解为 Result[T, error]");
    }
    const tmp = this.newTmp();
    const expr = this.expr(t.x);
    this.w(`const ${tmp} = await ${expr};`);
    this.w(`if (!${tmp}.ok) {`);
    this.indent++;
    this.w(`return { __langGoRes: true, ok: false, v: undefined, e: ${tmp}.e };`);
    this.indent--;
    this.w("}");
    return tmp;
  }

  private emitForIn(st: Extract<IrStmt, { kind: "for_in" }>): void {
    const name = st.variable.name;
    if (st.iterKind === "range") {
      // 区间经绑定（let r = a..b; for i in r）与字面量两条路径
      if (st.iter.kind === "range") {
        const lo = this.expr(st.iter.lo);
        const hi = this.expr(st.iter.hi);
        this.w(`for (let ${name} = ${lo}; ${name} < ${hi}; ${name}++) {`);
        this.emitBlockTyped(st.body, name);
        this.w("}");
        return;
      }
      const rv = this.expr(st.iter);
      const tmp = this.newTmp();
      this.w(`const ${tmp} = ${rv};`);
      this.w(`for (let ${name} = ${tmp}.lo; ${name} < ${tmp}.hi; ${name}++) {`);
      this.emitBlockTyped(st.body, name);
      this.w("}");
      return;
    }
    const it = this.expr(st.iter);
    this.w(`for (let ${name} of langGoIter(${it})) {`);
    this.emitBlockTyped(st.body, name);
    this.w("}");
  }

  private emitBlockTyped(stmts: readonly IrStmt[], name: string): void {
    this.indent++;
    this.w(`${name} = langGoCopy(${name});`);
    this.emitBody(stmts);
    this.indent--;
  }

  private emitBlock(stmts: readonly IrStmt[]): void {
    this.indent++;
    this.emitBody(stmts);
    this.indent--;
  }

  // —— match ——

  private emitMatchStmt(m: IrMatch): void {
    const tmp = this.newTmp();
    const scrut = this.expr(m.scrut);
    this.w(`const ${tmp} = ${scrut};`);
    switch (m.matchKind) {
      case "result": {
        const [okArm, errArm] = splitAlgebraic(m, "Ok", "Err");
        this.w(`if (${tmp}.ok) {`);
        this.indent++;
        if (okArm !== undefined) {
          this.bindArmValue(okArm, `${tmp}.v`);
          this.emitArmStmt(okArm);
        }
        this.indent--;
        this.w("} else {");
        this.indent++;
        if (errArm !== undefined) {
          this.bindArmValue(errArm, `${tmp}.e`);
          this.emitArmStmt(errArm);
        }
        this.indent--;
        this.w("}");
        break;
      }
      case "option": {
        const [someArm, noneArm] = splitAlgebraic(m, "Some", "None");
        this.w(`if (${tmp}.has) {`);
        this.indent++;
        if (someArm !== undefined) {
          this.bindArmValue(someArm, `${tmp}.v`);
          this.emitArmStmt(someArm);
        }
        this.indent--;
        if (noneArm !== undefined) {
          this.w("} else {");
          this.indent++;
          this.emitArmStmt(noneArm);
          this.indent--;
        }
        this.w("}");
        break;
      }
      case "enum": {
        this.w(`switch (${tmp}.tag) {`);
        this.indent++;
        let hasDefault = false;
        for (const arm of m.arms) {
          if (arm.pat.kind === "ctor_pat") {
            this.w(`case ${JSON.stringify(arm.pat.sym?.name ?? "")}: {`);
            this.indent++;
            this.bindEnumPayloads(arm.pat, tmp);
            this.emitArmStmt(arm);
            this.indent--;
            this.w("}");
          } else {
            hasDefault = true;
            this.w("default: {");
            this.indent++;
            if (arm.pat.kind === "bind_pat") {
              this.w(`const ${arm.pat.name} = ${tmp};`);
            }
            this.emitArmStmt(arm);
            this.indent--;
            this.w("}");
          }
        }
        if (!hasDefault) {
          this.w(`default: throw new Error("match 无匹配分支");`);
        }
        this.indent--;
        this.w("}");
        break;
      }
      default:
        this.emitLiteralMatchStmt(m, tmp);
        break;
    }
  }

  private bindArmValue(arm: IrArm, valueExpr: string): void {
    const name = armBindName(arm);
    if (name !== "_") {
      this.w(`const ${name} = langGoCopy(${valueExpr});`);
    }
  }

  private bindEnumPayloads(p: Extract<IrPat, { kind: "ctor_pat" }>, tmp: string): void {
    for (let j = 0; j < p.subs.length; j++) {
      const sp = p.subs[j]!;
      if (sp.kind === "bind_pat") {
        this.w(`const ${sp.name} = langGoCopy(${tmp}.vals[${j}]);`);
      }
    }
  }

  private emitArmStmt(arm: IrArm): void {
    const expr = this.expr(arm.body);
    this.w(`${expr};`);
  }

  private emitLiteralMatchStmt(m: IrMatch, tmp: string): void {
    for (let i = 0; i < m.arms.length; i++) {
      const arm = m.arms[i]!;
      const p = arm.pat;
      if (p.kind === "lit_pat") {
        const kw = i > 0 ? "} else if" : "if";
        const lit = this.expr(p.val);
        this.w(`${kw} (langGoEq(${tmp}, ${lit})) {`);
        this.emitArmBodyBlock(arm.body);
      } else if (p.kind === "bind_pat") {
        this.w("} else {");
        this.indent++;
        this.w(`const ${p.name} = ${tmp};`);
        this.emitArmStmt(arm);
        this.indent--;
      } else if (p.kind === "wild_pat") {
        this.w("} else {");
        this.emitArmBodyBlock(arm.body);
      }
    }
    this.w("}");
  }

  private emitArmBodyBlock(body: IrExpr): void {
    this.indent++;
    if (body.kind === "match") {
      const expr = this.expr(body);
      this.w(`await ${expr};`);
    } else {
      this.w(`${this.expr(body)};`);
    }
    this.indent--;
  }

  // —— 表达式 ——

  private expr(e: IrExpr): string {
    switch (e.kind) {
      case "const":
        switch (e.constKind) {
          case "int":
            return `${e.i64.toString(10)}n`;
          case "float":
            return goFloatLit(e.f64);
          case "str":
            return jsStr(e.s);
          case "bool":
            return e.b ? "true" : "false";
          default: {
            const _never: never = e.constKind;
            return _never;
          }
        }

      case "local_get":
        return e.local.name;

      case "fn_get": {
        const fi = e.sym !== undefined ? this.fns.get(e.sym) : undefined;
        if (fi !== undefined) return this.fnName(fi);
        return `lf_${e.name}`;
      }

      case "builtin_get":
        throw new JsError(`内建 ${e.name} 须直接调用`);

      case "ctor":
        return this.emitCtor(e);

      case "call":
        return this.emitCall(e);

      case "unary": {
        // 一元套一元必须括号化（--5n 是 JS 自减语法错）
        const inner = e.x.kind === "unary" ? `(${this.expr(e.x)})` : this.expr(e.x);
        if (e.op === "-") return `(-${inner})`; // 嵌套负号防 -- 前缀递减
        if (e.op === "!") return `!${inner}`;
        // JS 对象赋值即共享引用，与 lang-go 的 & 语义天然对齐
        return inner;
      }

      case "binary":
        return this.emitBinary(e);

      case "range": {
        const lo = this.expr(e.lo);
        const hi = this.expr(e.hi);
        return `{ __langGoRange: true, lo: ${lo}, hi: ${hi} }`;
      }

      case "index": {
        const obj = this.expr(e.obj);
        const idx = this.expr(e.idx);
        return `langGoIndex(${obj}, ${idx})`;
      }

      case "slice": {
        const obj = this.expr(e.obj);
        const lo = this.expr(e.lo);
        const hi = this.expr(e.hi);
        return `langGoSlice(${obj}, ${lo}, ${hi})`;
      }

      case "field": {
        const obj = this.expr(e.obj);
        return `${obj}.${e.name}`;
      }

      case "try":
        throw new JsError("? 只能出现在 let/return/赋值的顶层（→JS 约束）");

      case "struct_lit": {
        const name = e.struct !== undefined ? e.struct.name : "";
        const fs: string[] = [`__langGoStruct: ${JSON.stringify(name)}`];
        for (const f of e.fields) {
          const v = this.expr(f.val);
          fs.push(`${f.name}: langGoCopy(${v})`);
        }
        return `{ ${fs.join(", ")} }`;
      }

      case "map_lit": {
        const kvs = e.keys.map((k, i) => {
          const ks = this.expr(k);
          const vs = this.expr(e.vals[i]!);
          return `[${ks}, langGoCopy(${vs})]`;
        });
        return `langGoNewMap(${kvs.join(", ")})`;
      }

      case "slice_lit": {
        const items = e.elems.map((el) => `langGoCopy(${this.expr(el)})`);
        return `[${items.join(", ")}]`;
      }

      case "match":
        return this.exprMatch(e);

      case "fn_lit":
        return this.emitFnLit(e);

      default: {
        const _never: never = e;
        return _never;
      }
    }
  }

  private emitCtor(x: Extract<IrExpr, { kind: "ctor" }>): string {
    switch (x.sym.name) {
      case "None":
        return "{ __langGoOpt: true, has: false, v: undefined }";
      case "Some": {
        if (x.args.length !== 1) throw new JsError("Some 需要 1 个参数");
        const arg = this.expr(x.args[0]!);
        return `{ __langGoOpt: true, has: true, v: langGoCopy(${arg}) }`;
      }
      default: {
        if (x.sym.name === "Ok" || x.sym.name === "Err") {
          throw new JsError("Ok/Err 只能出现在 Result 函数的 return（→JS 约束）");
        }
        let en = "";
        for (const ed of this.prog.enums) {
          for (const c of ed.cases) {
            if (c.name === x.sym.name) en = ed.name;
          }
        }
        const args = x.args.map((a) => `langGoCopy(${this.expr(a)})`);
        return `le_${en}_${x.sym.name}(${args.join(", ")})`;
      }
    }
  }

  private emitBinary(x: Extract<IrExpr, { kind: "binary" }>): string {
    const l = this.expr(x.l);
    const r = this.expr(x.r);
    switch (x.op) {
      case "&&":
        return `(${l} && ${r})`;
      case "||":
        return `(${l} || ${r})`;
      case "==":
        return `langGoEq(${l}, ${r})`;
      case "!=":
        return `!langGoEq(${l}, ${r})`;
      case "/":
        return `langGoDiv(${l}, ${r})`;
      default:
        return `(${l} ${x.op} ${r})`;
    }
  }

  /** 用户 fn / 内建 / 方法 / 函数值 */
  private emitCall(x: Extract<IrExpr, { kind: "call" }>): string {
    // 内建
    if (x.builtin !== undefined && x.builtin !== "") {
      if (x.builtin === "http.get") {
        const args = x.args.map((a) => this.expr(a));
        return `langGoHTTP.get(${args.join(", ")})`;
      }
      const bf = builtins[x.builtin];
      if (bf === undefined) {
        throw new JsError(`未知内建 ${x.builtin}`);
      }
      const args = x.args.map((a) => {
        let arg = this.expr(a);
        if (x.builtin === "push") arg = `langGoCopy(${arg})`;
        return arg;
      });
      return `${bf}(${args.join(", ")})`;
    }
    // 方法：obj.m(args) → lf_<Struct>_<name>(obj, ...)（方法名全局唯一）
    if (x.method !== undefined) {
      const obj = this.expr(x.recv!);
      const fi = this.fns.get(x.method);
      if (fi === undefined) {
        throw new JsError(`内部错误：方法未注册 ${x.method.name}`);
      }
      const args = x.args.map((a) => `langGoCopy(${this.expr(a)})`);
      return `await ${this.fnName(fi)}(${obj}, ${args.join(", ")})`;
    }
    // 用户 fn
    if (x.fn !== undefined) {
      const fi = this.fns.get(x.fn);
      if (fi === undefined) {
        throw new JsError(`内部错误：fn 未注册 ${x.fn.name}`);
      }
      const args = x.args.map((a) => `langGoCopy(${this.expr(a)})`);
      return `await ${this.fnName(fi)}(${args.join(", ")})`;
    }
    // 函数值（fnlit 绑定直通；字段函数值经 obj.f）
    let callee: string;
    if (x.callee !== undefined && x.callee.kind === "local_get") {
      callee = x.callee.local.name;
    } else {
      callee = x.callee !== undefined ? this.expr(x.callee) : "";
    }
    const args = x.args.map((a) => this.expr(a));
    return `await ${callee}(${args.join(", ")})`;
  }

  /** 表达式位置 match → 立即调用的 async 函数（调用侧 await） */
  private exprMatch(m: IrMatch): string {
    const savedLines = this.lines;
    const savedIndent = this.indent;
    this.lines = [];
    this.indent = 1;

    const tmp = this.newTmp();
    const scrut = this.expr(m.scrut);
    this.w("(async () => {");
    this.indent++;
    this.w(`const ${tmp} = ${scrut};`);
    switch (m.matchKind) {
      case "result": {
        const [okArm, errArm] = splitAlgebraic(m, "Ok", "Err");
        this.w(`if (${tmp}.ok) {`);
        this.indent++;
        if (okArm !== undefined) {
          this.bindArmValue(okArm, `${tmp}.v`);
          this.emitArmReturn(okArm);
        }
        this.indent--;
        this.w("} else {");
        this.indent++;
        if (errArm !== undefined) {
          this.bindArmValue(errArm, `${tmp}.e`);
          this.emitArmReturn(errArm);
        }
        this.indent--;
        this.w("}");
        break;
      }
      case "option": {
        const [someArm, noneArm] = splitAlgebraic(m, "Some", "None");
        this.w(`if (${tmp}.has) {`);
        this.indent++;
        if (someArm !== undefined) {
          this.bindArmValue(someArm, `${tmp}.v`);
          this.emitArmReturn(someArm);
        }
        this.indent--;
        if (noneArm !== undefined) {
          this.w("} else {");
          this.indent++;
          this.emitArmReturn(noneArm);
          this.indent--;
          this.w("}");
        } else {
          this.w("}");
        }
        break;
      }
      case "enum": {
        this.w(`switch (${tmp}.tag) {`);
        this.indent++;
        for (const arm of m.arms) {
          if (arm.pat.kind === "ctor_pat") {
            this.w(`case ${JSON.stringify(arm.pat.sym?.name ?? "")}: {`);
            this.indent++;
            this.bindEnumPayloads(arm.pat, tmp);
            this.emitArmReturn(arm);
            this.indent--;
            this.w("}");
          } else {
            this.w("default: {");
            this.indent++;
            if (arm.pat.kind === "bind_pat") {
              this.w(`const ${arm.pat.name} = ${tmp};`);
            }
            this.emitArmReturn(arm);
            this.indent--;
            this.w("}");
          }
        }
        this.indent--;
        this.w("}");
        break;
      }
      default:
        this.emitLiteralMatchReturn(m, tmp);
        break;
    }
    this.indent--;
    this.w("})()");
    const out = this.lines.join("\n");
    this.lines = savedLines;
    this.indent = savedIndent;
    // 片段自带 await：match 可出现在任意嵌套表达式位置
    return `await ${out}`;
  }

  private emitLiteralMatchReturn(m: IrMatch, tmp: string): void {
    for (let i = 0; i < m.arms.length; i++) {
      const arm = m.arms[i]!;
      const p = arm.pat;
      if (p.kind === "lit_pat") {
        const kw = i > 0 ? "} else if" : "if";
        const lit = this.expr(p.val);
        this.w(`${kw} (langGoEq(${tmp}, ${lit})) {`);
        this.indent++;
        this.emitArmReturn(arm);
        this.indent--;
      } else if (p.kind === "bind_pat") {
        this.w("} else {");
        this.indent++;
        this.w(`const ${p.name} = ${tmp};`);
        this.emitArmReturn(arm);
        this.indent--;
      } else if (p.kind === "wild_pat") {
        this.w("} else {");
        this.indent++;
        this.emitArmReturn(arm);
        this.indent--;
      }
    }
    this.w("}");
  }

  private emitArmReturn(arm: IrArm): void {
    const expr = this.expr(arm.body);
    this.w(`return langGoCopy(await ${expr});`);
  }

  /** 函数字面量 */
  private emitFnLit(x: Extract<IrExpr, { kind: "fn_lit" }>): string {
    const ps = x.params.map((p) => p.name);
    const savedLines = this.lines;
    const savedIndent = this.indent;
    const savedFn = this.curFn;
    this.curFn = {
      name: "匿名函数",
      sym: undefined,
      recv: undefined,
      typeParams: [],
      params: [],
      ret: x.ret,
      locals: [],
      body: [],
      extern: false,
      externHost: "",
    };
    this.lines = [];
    this.indent = 0;
    this.w(`async function(${ps.join(", ")}) {`);
    this.indent++;
    for (const p of x.params) {
      this.w(`${p.name} = langGoCopy(${p.name});`);
    }
    for (const st of x.body) this.emitStmt(st);
    this.w("}");
    this.curFn = savedFn;
    const out = this.lines.join("\n");
    this.lines = savedLines;
    this.indent = savedIndent;
    return out;
  }
}

class JsError extends Error {}

function isRef(e: IrExpr): boolean {
  return e.kind === "unary" && e.op === "&";
}

function splitAlgebraic(m: IrMatch, posName: string, negName: string): [IrArm | undefined, IrArm | undefined] {
  let pos: IrArm | undefined;
  let neg: IrArm | undefined;
  for (const arm of m.arms) {
    if (arm.pat.kind === "ctor_pat") {
      if (arm.pat.sym?.name === posName) pos = arm;
      else if (arm.pat.sym?.name === negName) neg = arm;
    }
  }
  return [pos, neg];
}

function armBindName(arm: IrArm): string {
  if (arm.pat.kind === "ctor_pat" && arm.pat.subs.length === 1) {
    const bp = arm.pat.subs[0]!;
    if (bp.kind === "bind_pat") return bp.name;
  }
  return "_";
}

/** TranspileJS IR → JS 源码 */
export function transpileJS(p: IrProg): string {
  return new JsGen(p).emitProgram();
}
