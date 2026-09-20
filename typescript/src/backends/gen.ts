// →Go 后端：Core IR → 自包含可编译 Go 源码（package main，prelude 内联）。
// 名字解析/类型推断/语义校验已由 semantic/types 完成，此处只做类型映射与
// any 边界收窄插桩（langGoTo）。发射文本与 Go 参考实现的输出等价（可编译且行为一致；
// 参考实现另过 gofmt 归一——归一属于工具链步骤，不属于语义）。

import type { Symbol } from "../semantic/symbols.js";
import {
  ResultOk,
  isConcrete,
  type Type,
} from "../types/model.js";
import type { IrBinary, IrCall, IrCtor, IrExpr, IrFn, IrLocal, IrMatch, IrPat, IrProg, IrStmt } from "../ir/model.js";
import { goPrelude } from "./goprelude.js";

type GoType = Type | undefined;

/** match 臂（IR 形态） */
type IrArm = { readonly pat: IrPat; readonly body: IrExpr };

class GenError extends Error {}

/** lang-go 类型 → Go 类型（映射，非推断） */
export function goTypeOf(t: GoType): string {
  if (t === undefined) return "any";
  switch (t.kind) {
    case "int":
      return "int64";
    case "float":
      return "float64";
    case "string":
      return "string";
    case "bool":
      return "bool";
    case "error":
      return "error";
    case "chan":
      return "chan any";
    case "slice":
      return `*LangGoArr[${goTypeOf(t.elem)}]`;
    case "map":
      return "*LangGoMap";
    case "ref":
      return "*" + goTypeOf(t.elem);
    case "option":
      return `LangGoOpt[${goTypeOf(t.elem)}]`;
    case "result":
      return `LangGoRes[${goTypeOf(ResultOk(t))}]`;
    case "enum":
      return "LangGoEnum";
    case "struct":
      return t.sym !== undefined ? t.sym.name : "any";
    case "range":
      return "langGoRange";
    case "type_arg":
      return t.name !== "" ? t.name : "any";
    case "fn": {
      const ps = t.params.map((p) => goTypeOf(p));
      let sig = `func(${ps.join(", ")})`;
      if (t.ret !== undefined && t.ret.kind !== "unit") sig += " " + goTypeOf(t.ret);
      return sig;
    }
    default:
      return "any";
  }
}

function isAnyT(t: GoType): boolean {
  return (
    t === undefined ||
    t.kind === "any" ||
    t.kind === "unknown" ||
    t.kind === "invalid" ||
    t.kind === "type_arg"
  );
}

/** Go strconv.FormatFloat(f,'g',-1,64) 等价（常量发射用） */
function goFloatLit(f: number): string {
  if (Number.isNaN(f)) return "NaN";
  if (f === Number.POSITIVE_INFINITY) return "+Inf";
  if (f === Number.NEGATIVE_INFINITY) return "-Inf";
  return String(f);
}

/** Go strconv.Quote 等价（ASCII 面足够；字符串常量发射用） */
export function goQuote(s: string): string {
  let out = '"';
  for (const r of s) {
    const cp = r.codePointAt(0)!;
    if (cp === 0x22) out += '\\"';
    else if (cp === 0x5c) out += "\\\\";
    else if (cp === 0x0a) out += "\\n";
    else if (cp === 0x09) out += "\\t";
    else if (cp === 0x0d) out += "\\r";
    else if (cp < 0x20 || (cp >= 0x7f && cp < 0xa0)) out += `\\x${cp.toString(16).padStart(2, "0")}`;
    else out += r;
  }
  return out + '"';
}

/** 部分内建在 →Go prelude 里的实际返回形态 */
const goBuiltinRet: Readonly<Record<string, string>> = {
  bytes: "*LangGoArr[any]",
  keys: "*LangGoArr[any]",
  values: "*LangGoArr[any]",
  recv: "LangGoRes[any]",
  makeChan: "chan any",
  jsonParse: "any",
};

/** 内建 → prelude 运行时函数 */
const builtinMap: Readonly<Record<string, string>> = {
  println: "langGoPrintln", print: "langGoPrint", len: "langGoLen",
  str: "langGoStr", int: "langGoInt", float: "langGoFloat",
  keys: "langGoKeys", values: "langGoValues", has: "langGoHas",
  push: "langGoPush", assert: "langGoAssert", err: "langGoErr",
  bytes: "langGoBytes", char: "langGoChar",
  makeChan: "langGoNewChan", send: "langGoSend", recv: "langGoRecv",
  jsonParse: "langGoJSONParse", jsonStringify: "langGoJSONStringify",
  isNone: "langGoIsNone",
};


/** None 构造器在按目标类型收窄实参时的类型实参特化（目标 Option[X]、X 具体才有意义） */
function noneCtorFor(target: GoType, e: IrExpr, expr: string): string {
  if (e.kind === "ctor" && e.sym.name === "None" && target !== undefined && target.kind === "option") {
    const elem = target.elem;
    if (elem !== undefined && !isAnyT(elem)) {
      return `LangGoNone[${goTypeOf(elem)}]()`;
    }
  }
  return expr;
}

class Gen {
  private lines: string[] = [];
  private indent = 0;
  private readonly structs = new Map<string, IrProg["structs"][number]>();
  private readonly caseEnum = new Map<string, string>();
  private readonly fns = new Map<Symbol, IrFn>();
  private tmpN = 0;
  private curFn: IrFn | undefined;
  /** 表达式位置 match 的结果类型（臂体收窄用） */
  private curMatchTyp: GoType;
  /** 局部变量的实际 Go 类型（:= 推断结果） */
  private readonly localGo = new Map<IrLocal, string>();

  constructor(private readonly prog: IrProg) {}

  private w(line: string): void {
    this.lines.push("\t".repeat(this.indent) + line);
  }

  private out(): string {
    return this.lines.join("\n") + "\n";
  }

  private newTmp(): string {
    this.tmpN++;
    return `_t${this.tmpN}`;
  }

  /** 局部表达式的「生成侧 Go 类型」 */
  private gt(e: IrExpr): string {
    switch (e.kind) {
      case "local_get": {
        const s = this.localGo.get(e.local);
        if (s !== undefined) return s;
        return goTypeOf(e.local.typ);
      }
      case "index":
      case "slice":
        return "any";
      case "field": {
        if (isAnyT(e.obj.typ)) return "any";
        if (e.obj.typ.kind === "error") return "string";
        break;
      }
      case "call": {
        if (e.builtin !== undefined && e.builtin !== "") {
          const r = goBuiltinRet[e.builtin];
          if (r !== undefined) return r;
        }
        if (e.fn !== undefined) {
          const fi = this.fns.get(e.fn);
          if (fi !== undefined) {
            if (fi.ret !== undefined) return goTypeOf(fi.ret);
            return "any";
          }
        }
        break;
      }
      case "ctor":
        return goTypeOf(e.typ);
      case "match":
        return this.matchRetType(e);
      case "binary": {
        if (e.op === "+") {
          const li = this.gt(e.l);
          const ri = this.gt(e.r);
          if (li === "string" || ri === "string") return "string";
          if (li === "any" || ri === "any") return "any";
          return li;
        }
        if (e.op === "&&" || e.op === "||") return "bool";
        break;
      }
      case "unary":
        if (e.op === "!") return "bool";
        break;
      default:
        break;
    }
    return goTypeOf(e.typ);
  }

  /** 表达式位置 match 的有效返回形态 */
  private matchRetType(m: IrMatch): string {
    const ret = goTypeOf(m.typ);
    for (const arm of m.arms) {
      if (goTypeOf(arm.body.typ) !== ret) return "any";
    }
    return ret;
  }

  /** any 与具体类型交界处插 langGoTo 断言 */
  private maybeWrap(target: GoType, got: string, expr: string): string {
    if (target !== undefined && !isAnyT(target) && got === "any") {
      return `langGoTo[${goTypeOf(target)}](${expr})`;
    }
    return expr;
  }

  private wrapArg(target: GoType, e: IrExpr, expr: string): string {
    return this.maybeWrap(target, this.gt(e), expr);
  }

  // ———— 程序输出 ————

  emitProgram(): string {
    this.lines.push(goPrelude.replace(/\n$/, ""));
    // struct 声明 + 格式化方法（镜像解释器 fmtVal 的 struct 分支）
    for (const s of this.prog.structs) {
      this.structs.set(s.name, s);
    }
    for (const e of this.prog.enums) {
      for (const c of e.cases) this.caseEnum.set(c.name, e.name);
    }
    const walkStmtFns = (s: IrStmt): void => {
      if (s.kind === "fn_def") {
        if (s.fn.sym !== undefined) this.fns.set(s.fn.sym, s.fn);
        walkFns(s.fn.body);
      }
    };
    const walkFns = (list: readonly IrStmt[]): void => {
      for (const s of list) walkStmtFns(s);
    };
    for (const f of this.prog.fns) {
      if (f.sym !== undefined) this.fns.set(f.sym, f);
      walkFns(f.body);
    }

    for (const s of this.prog.structs) {
      const parts = s.fields.map((f) => `${JSON.stringify(f.name)} + ": " + langGoFmt(v.${f.name}, false)`);
      this.w(`type ${s.name} struct {`);
      this.indent++;
      for (const f of s.fields) this.w(`${f.name} ${goTypeOf(f.typ)}`);
      this.indent--;
      this.w("}");
      this.w("");
      this.w(`func (v ${s.name}) langGoString(top bool) string {`);
      this.indent++;
      this.w(
        `return ${JSON.stringify(s.name)} + "{" + strings.Join([]string{${parts.join(", ")}}, ", ") + "}"`,
      );
      this.indent--;
      this.w("}");
      this.w(`func (v ${s.name}) LangGoField(name string) any {`);
      this.indent++;
      this.w("switch name {");
      for (const f of s.fields) {
        this.w(`case ${JSON.stringify(f.name)}:`);
        this.indent++;
        this.w(`return v.${f.name}`);
        this.indent--;
      }
      this.w("}");
      this.w(`panic(${JSON.stringify(s.name)} + " 没有字段 " + name)`);
      this.indent--;
      this.w("}");
      this.w("");
    }
    // 用户 enum 构造器
    for (const en of this.prog.enums) {
      for (const c of en.cases) {
        const ps: string[] = [];
        const vals: string[] = [];
        for (let i = 0; i < c.payloads.length; i++) {
          const pn = `p${i}`;
          ps.push(`${pn} ${goTypeOf(c.payloads[i])}`);
          vals.push(pn);
        }
        const valsExpr = vals.length > 0 ? `[]any{${vals.join(", ")}}` : "nil";
        this.w(
          `func le_${en.name}_${c.name}(${ps.join(", ")}) LangGoEnum { return LangGoEnum{${JSON.stringify(c.name)}, ${valsExpr}} }`,
        );
        this.w("");
      }
    }
    // 全局绑定
    for (const gl of this.prog.globals) {
      const expr = this.expr(gl.init);
      this.w(`var ${gl.local.name} = ${expr}`);
      this.w("");
    }
    // 方法与函数
    for (const f of this.prog.fns) {
      this.emitFunc(f);
    }
    // main
    if (this.prog.main !== undefined) {
      this.w("func main() {");
      this.w("\tlf_main()");
      this.w("\tlangGoWaitAll()");
      this.w("}");
    } else {
      this.w("func main() {}");
    }
    return this.out();
  }

  private goName(f: IrFn): string {
    if (f.recv !== undefined) return f.name;
    return `lf_${f.name}`;
  }

  private emitFunc(f: IrFn): void {
    let generic = "";
    if (f.typeParams.length > 0) {
      const gs = f.typeParams.map((tp) => `${tp} any`);
      generic = `[${gs.join(", ")}]`;
    }
    let recvPrefix = "";
    if (f.recv !== undefined) {
      recvPrefix = `(${f.recv.name} ${goTypeOf(f.recv.typ)}) `;
      this.localGo.set(f.recv, goTypeOf(f.recv.typ));
    }
    const ps = f.params.map((p) => {
      this.localGo.set(p, goTypeOf(p.typ));
      return `${p.name} ${goTypeOf(p.typ)}`;
    });
    const retDecl = f.ret !== undefined ? " " + goTypeOf(f.ret) : "";
    this.w(`func ${recvPrefix}${this.goName(f)}${generic}(${ps.join(", ")})${retDecl} {`);
    const savedFn = this.curFn;
    this.curFn = f;
    this.indent++;
    this.emitBody(f.body);
    // 函数体内有 go 语句：函数边界 join
    if (bodyHasGo(f.body)) this.w("\tlangGoWaitAll()");
    this.indent--;
    this.curFn = savedFn;
    this.w("}");
    this.w("");
  }

  private emitBody(stmts: readonly IrStmt[]): void {
    for (const st of stmts) this.emitStmt(st);
  }

  // ———— 语句 ————

  private emitStmt(s: IrStmt): void {
    switch (s.kind) {
      case "let":
        this.emitLet(s);
        return;

      case "assign":
        this.emitAssign(s);
        return;

      case "eval": {
        if (s.x.kind === "match") {
          this.emitMatchStmt(s.x);
          return;
        }
        const expr = this.expr(s.x);
        if (this.isVoidCall(s.x)) {
          this.w(expr);
        } else {
          this.w(`_ = ${expr}`);
        }
        return;
      }

      case "return":
        this.emitReturn(s);
        return;

      case "if": {
        const cond = this.condExpr(s.cond);
        this.w(`if ${cond} {`);
        this.emitBlock(s.then);
        if (s.els.length > 0) {
          this.w("} else {");
          this.emitBlock(s.els);
        }
        this.w("}");
        return;
      }

      case "while": {
        const cond = this.condExpr(s.cond);
        this.w(`for ${cond} {`);
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
        const fi = s.fn !== undefined ? this.fns.get(s.fn) : undefined;
        if (fi === undefined) {
          throw new GenError(`内部错误：go 目标未注册 ${s.fn?.name ?? "?"}`);
        }
        // EVAL-GO-007：实参在 go 语句处就地求值并快照（临时变量），
        // goroutine 闭包不得按引用捕获调用方可变局部
        const args: string[] = [];
        for (let i = 0; i < s.args.length; i++) {
          const a = s.args[i]!;
          let arg = this.expr(a);
          if (i < fi.params.length) {
            arg = noneCtorFor(fi.params[i]!.typ, a, arg);
            arg = this.wrapArg(fi.params[i]!.typ, a, arg);
          }
          const tmp = this.newTmp();
          this.w(`${tmp} := ${arg}`);
          this.w(`_ = ${tmp}`);
          args.push(tmp);
        }
        this.w(`langGoGo(func() { ${this.goName(fi)}(${args.join(", ")}) })`);
        return;
      }

      case "fn_def":
        this.emitFunc(s.fn);
        return;

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
        // 收窄源是 ? 操作数的 Result T
        let bindType = "any";
        const rt = st.init.x.typ;
        if (rt.kind === "result") bindType = goTypeOf(ResultOk(rt));
        let rhs = `${tmp}.v`;
        if (bindType !== "any") rhs = `langGoTo[${bindType}](${rhs})`;
        this.w(`${name} := ${rhs}`);
      }
      return;
    }
    let expr = this.expr(st.init);
    if (name === "_") {
      this.w(`_ = ${expr}`);
      return;
    }
    const bindType = goTypeOf(st.local.typ);
    let bindGo = this.gt(st.init);
    // 空数组字面量继承注解类型：var out: [U] = []
    if (st.init.kind === "slice_lit" && st.init.elems.length === 0 && bindType.startsWith("*LangGoArr[")) {
      expr = `&${bindType.slice(1)}{}`;
      bindGo = bindType;
    } else {
      if (bindType !== "any" && this.gt(st.init) === "any") {
        bindGo = bindType;
      }
      expr = this.wrapArg(st.local.typ, st.init, expr);
    }
    this.localGo.set(st.local, bindGo);
    this.w(`${name} := ${expr}`);
    // lang-go 无「局部绑定必须使用」规则，Go 有——统一消 unused 编译错
    this.w(`_ = ${name}`);
    // 函数值绑定注册参数/返回类型，调用点据此收窄
    if (st.init.kind === "fn_lit" && st.local.sym !== undefined) {
      const fi: IrFn = {
        name,
        sym: undefined,
        recv: undefined,
        typeParams: [],
        params: st.init.params,
        ret: st.init.ret,
        locals: [],
        body: [],
        extern: false,
        externHost: "",
      };
      this.fns.set(st.local.sym, fi);
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
        this.w(`${tgt.local.name} = ${tmp}.v`);
        return;
      }
      let expr = this.expr(st.val);
      expr = this.wrapArg(tgt.local.typ, st.val, expr);
      this.w(`${tgt.local.name} = ${expr}`);
      return;
    }
    if (tgt.kind === "field") {
      const obj = this.expr(tgt.obj);
      const expr = this.expr(st.val);
      if (tgt.obj.kind === "index") {
        // 容器元素字段链 xs[i].f = v：读改写（VALUE-ELEM-009）
        const cobj = this.expr(tgt.obj.obj);
        const cidx = this.expr(tgt.obj.idx);
        const elemT = goTypeOf(tgt.obj.typ);
        const tmp = this.newTmp();
        this.w(`${tmp} := langGoIndex[${elemT}](${cobj}, ${cidx})`);
        this.w(`${tmp}.${tgt.name} = ${expr}`);
        this.w(`langGoSetIndex(${cobj}, ${cidx}, ${tmp})`);
        return;
      }
      this.w(`${obj}.${tgt.name} = ${expr}`);
      return;
    }
    if (tgt.kind === "index") {
      const obj = this.expr(tgt.obj);
      const idx = this.expr(tgt.idx);
      const expr = this.expr(st.val);
      this.w(`langGoSetIndex(${obj}, ${idx}, ${expr})`);
      return;
    }
    throw new GenError("赋值目标不合法");
  }

  /** 复合赋值：ident/field 原生 +=；数组 += 走 langGoPushFrom；index 走 langGoAddIndex */
  private emitCompoundAssign(st: Extract<IrStmt, { kind: "assign" }>): void {
    const op = st.op;
    const tgt = st.target;
    if (tgt.kind === "local_get") {
      const expr = this.expr(st.val);
      const lt = goTypeOf(tgt.local.typ);
      if (op === "+=" && lt.startsWith("*LangGoArr[")) {
        this.w(`${tgt.local.name} = langGoPushFrom(${tgt.local.name}, ${expr})`);
        return;
      }
      this.w(`${tgt.local.name} ${op} ${this.wrapArg(tgt.local.typ, st.val, expr)}`);
      return;
    }
    if (tgt.kind === "field") {
      const obj = this.expr(tgt.obj);
      const expr = this.expr(st.val);
      this.w(`${obj}.${tgt.name} ${op} ${expr}`);
      return;
    }
    if (tgt.kind === "index") {
      const obj = this.expr(tgt.obj);
      const idx = this.expr(tgt.idx);
      const expr = this.expr(st.val);
      this.w(`langGoAddIndex(${obj}, ${idx}, ${expr}, ${JSON.stringify(op)})`);
      return;
    }
    throw new GenError("赋值目标不合法");
  }

  /** 判定「无返回值的调用」（语句位置不加 _ =） */
  private isVoidCall(e: IrExpr): boolean {
    if (e.kind !== "call") return false;
    if (e.fn !== undefined) {
      const fi = this.fns.get(e.fn);
      if (fi !== undefined) return fi.ret === undefined;
      return false;
    }
    if (e.method !== undefined) {
      const fi = this.fns.get(e.method);
      if (fi !== undefined) return fi.ret === undefined;
      return false;
    }
    if (e.builtin !== undefined && e.builtin !== "") {
      switch (e.builtin) {
        case "println":
        case "print":
        case "push":
        case "assert":
        case "send":
          return true;
        default:
          return false;
      }
    }
    return false;
  }

  private emitReturn(st: Extract<IrStmt, { kind: "return" }>): void {
    if (st.val === undefined) {
      this.w("return");
      return;
    }
    if (st.val.kind === "try") {
      const tmp = this.genTry(st.val);
      this.w(`return ${tmp}.v`);
      return;
    }
    if (this.curFn !== undefined && this.curFn.ret !== undefined && this.curFn.ret.kind === "result") {
      const ctor = st.val;
      if (ctor.kind !== "ctor" || (ctor.sym.name !== "Ok" && ctor.sym.name !== "Err") || ctor.args.length !== 1) {
        throw new GenError("内部错误：Result return 形态异常");
      }
      const t = goTypeOf(ResultOk(this.curFn.ret));
      const expr = this.expr(ctor.args[0]!);
      if (ctor.sym.name === "Ok") {
        this.w(`return LangGoOk[${t}](${expr})`);
      } else {
        this.w(`return LangGoResErr[${t}](${expr})`);
      }
      return;
    }
    let expr = this.expr(st.val);
    if (this.curFn === undefined || this.curFn.ret === undefined) {
      throw new GenError("void 函数不应返回值");
    }
    const rt = goTypeOf(this.curFn.ret);
    if (rt !== "any" && this.gt(st.val) === "any") {
      expr = `langGoTo[${rt}](${expr})`;
    }
    this.w(`return ${expr}`);
  }

  /** 展开 ? 传播；返回持有 Result 的临时变量名 */
  private genTry(t: Extract<IrExpr, { kind: "try" }>): string {
    if (this.curFn === undefined || this.curFn.ret === undefined || this.curFn.ret.kind !== "result") {
      throw new GenError("带 ? 的函数须注解为 Result[T, error]");
    }
    const tmp = this.newTmp();
    let expr = this.expr(t.x);
    if (this.gt(t.x) === "any") {
      expr = `langGoTo[LangGoRes[any]](${expr})`;
    }
    const tt = goTypeOf(ResultOk(this.curFn.ret));
    this.w(`${tmp} := ${expr}`);
    this.w(`if !${tmp}.ok {`);
    this.indent++;
    this.w(`return LangGoResErr[${tt}](${tmp}.e)`);
    this.indent--;
    this.w("}");
    return tmp;
  }

  private condExpr(e: IrExpr): string {
    const expr = this.expr(e);
    if (this.gt(e) !== "bool") {
      return `langGoTruthy(${expr})`;
    }
    return expr;
  }

  private emitBlock(stmts: readonly IrStmt[]): void {
    this.indent++;
    this.emitBody(stmts);
    this.indent--;
  }

  private emitForIn(st: Extract<IrStmt, { kind: "for_in" }>): void {
    if (st.iterKind === "range") {
      this.localGo.set(st.variable, "int64");
      // 区间经绑定（let r = a..b; for i in r）与字面量两条路径
      if (st.iter.kind === "range") {
        const lo = this.expr(st.iter.lo);
        const hi = this.expr(st.iter.hi);
        this.w(`for ${st.variable.name} := ${lo}; ${st.variable.name} < ${hi}; ${st.variable.name}++ {`);
        this.emitBlock(st.body);
        this.w("}");
        return;
      }
      const rv = this.expr(st.iter);
      const tmp = this.newTmp();
      this.w(`${tmp} := ${rv}`);
      this.w(`for ${st.variable.name} := ${tmp}.Lo; ${st.variable.name} < ${tmp}.Hi; ${st.variable.name}++ {`);
      this.emitBlock(st.body);
      this.w("}");
      return;
    }
    const tmp = this.newTmp();
    const it = this.expr(st.iter);
    const name = st.variable.name;
    const iterGo = this.gt(st.iter);
    if (st.iterKind === "slice" && iterGo !== "any") {
      this.localGo.set(st.variable, iterGo.slice("*LangGoArr[".length, -1));
      this.w(`${tmp} := ${it}`);
      this.w(`for _, ${name} := range ${tmp}.Items {`);
      this.emitBlock(st.body);
      this.w("}");
    } else if (st.iterKind === "string") {
      // lang-go 语义：按码点迭代，绑定单字符字符串
      this.localGo.set(st.variable, "string");
      this.w(`for _, _r${tmp} := range []rune(${it}) {`);
      this.indent++;
      this.w(`${name} := string(_r${tmp})`);
      this.emitBody(st.body);
      this.indent--;
      this.w("}");
    } else {
      // 动态值：运行时分派迭代（langGoIter 做快照）
      this.localGo.set(st.variable, "any");
      this.w(`${tmp} := langGoIter(${it})`);
      this.w(`for _, ${name} := range ${tmp}.Items {`);
      this.emitBlock(st.body);
      this.w("}");
    }
  }

  // ———— match ————

  private emitMatchStmt(m: IrMatch): void {
    const tmp = this.newTmp();
    let scrut = this.expr(m.scrut);
    scrut = this.coerceAlgebraic(scrut, m);
    this.w(`${tmp} := ${scrut}`);
    switch (m.matchKind) {
      case "result": {
        const [okArm, errArm] = splitAlgebraic(m, "Ok", "Err");
        this.w(`if ${tmp}.ok {`);
        this.emitResultArmBlock(okArm, tmp, ".v");
        this.w("} else {");
        this.emitResultArmBlock(errArm, tmp, ".e");
        this.w("}");
        break;
      }
      case "option": {
        const [someArm, noneArm] = splitAlgebraic(m, "Some", "None");
        this.w(`if ${tmp}.has {`);
        this.emitResultArmBlock(someArm, tmp, ".v");
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
        this.w(`switch ${tmp}.Tag {`);
        this.indent++;
        for (const arm of m.arms) {
          if (arm.pat.kind === "ctor_pat") {
            this.w(`case ${JSON.stringify(arm.pat.sym?.name ?? "")}:`);
            this.indent++;
            this.bindEnumPayloads(arm.pat, tmp);
            this.emitArmStmt(arm);
            this.indent--;
          } else {
            this.w("default:");
            this.indent++;
            if (arm.pat.kind === "bind_pat") {
              this.w(`${arm.pat.name} := ${tmp}`);
              this.w(`_ = ${arm.pat.name}`);
            }
            this.emitArmStmt(arm);
            this.indent--;
          }
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

  /** 装箱的 Result/Option（如 http.get 返回 any）在解包前收窄 */
  private coerceAlgebraic(scrut: string, m: IrMatch): string {
    if (!isAnyT(m.scrut.typ)) return scrut;
    if (m.matchKind === "result") return `langGoTo[LangGoRes[any]](${scrut})`;
    if (m.matchKind === "option") return `langGoTo[LangGoOpt[any]](${scrut})`;
    return scrut;
  }

  private emitResultArmBlock(arm: IrArm | undefined, tmp: string, field: string): void {
    this.indent++;
    if (arm !== undefined) {
      const name = armBindName(arm);
      if (name !== "_") {
        this.w(`${name} := ${tmp}${field}`);
        this.w(`_ = ${name}`); // 臂体未用时消 Go unused
      }
      this.emitArmStmt(arm);
    }
    this.indent--;
  }

  private bindEnumPayloads(p: Extract<IrPat, { kind: "ctor_pat" }>, tmp: string): void {
    for (let j = 0; j < p.subs.length; j++) {
      const sp = p.subs[j]!;
      if (sp.kind === "bind_pat") {
        let pt = "any";
        if (sp.typ !== undefined && !isAnyT(sp.typ)) pt = goTypeOf(sp.typ);
        this.w(`${sp.name} := langGoTo[${pt}](${tmp}.Vals[${j}])`);
      }
    }
  }

  /** 臂体在语句位置（ExprStmt 展开）的输出 */
  private emitArmStmt(arm: IrArm): void {
    if (arm.body.kind === "match") {
      this.emitMatchStmt(arm.body);
      return;
    }
    const expr = this.expr(arm.body);
    if (this.isVoidCall(arm.body)) {
      this.w(expr);
    } else {
      this.w(`_ = ${expr}`);
    }
  }

  /** 字面量/通配 match（语句位置）→ if/else 链 */
  private emitLiteralMatchStmt(m: IrMatch, tmp: string): void {
    for (let i = 0; i < m.arms.length; i++) {
      const arm = m.arms[i]!;
      const p = arm.pat;
      if (p.kind === "lit_pat") {
        const kw = i > 0 ? "} else if" : "if";
        const lit = this.expr(p.val);
        this.w(`${kw} langGoEq(${tmp}, ${lit}) {`);
        this.emitBlockArmBody(arm.body);
      } else if (p.kind === "bind_pat") {
        this.w("} else {");
        this.indent++;
        this.w(`${p.name} := ${tmp}`);
        this.w(`_ = ${p.name}`);
        this.emitArmStmt(arm);
        this.indent--;
      } else if (p.kind === "wild_pat") {
        this.w("} else {");
        this.emitBlockArmBody(arm.body);
      }
    }
    this.w("}");
  }

  private emitBlockArmBody(body: IrExpr): void {
    if (body.kind === "match") {
      this.indent++;
      this.emitMatchStmt(body);
      this.indent--;
      return;
    }
    const expr = this.expr(body);
    if (this.isVoidCall(body)) {
      this.w(expr);
    } else {
      this.w(`_ = ${expr}`);
    }
  }

  // ———— 表达式 ————

  /** 二元/一元运算的操作数：子式本身是运算时强制加括号 */
  private operand(e: IrExpr): string {
    const s = this.expr(e);
    if (e.kind === "binary" || e.kind === "unary") return `(${s})`;
    return s;
  }

  private expr(e: IrExpr): string {
    switch (e.kind) {
      case "const":
        switch (e.constKind) {
          case "int":
            return `int64(${e.i64.toString(10)})`;
          case "float":
            return `float64(${goFloatLit(e.f64)})`;
          case "str":
            return goQuote(e.s);
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
        if (fi !== undefined) return this.goName(fi);
        return `lf_${e.name}`;
      }

      case "builtin_get":
        throw new GenError(`内建 ${e.name} 须直接调用`);

      case "ctor":
        return this.emitCtor(e);

      case "call":
        return this.emitCall(e);

      case "unary": {
        const inner = this.operand(e.x);
        if (e.op === "-") return `-${inner}`;
        if (e.op === "!") return `!${inner}`;
        return `&${inner}`;
      }

      case "binary":
        return this.emitBinary(e);

      case "range": {
        const lo = this.expr(e.lo);
        const hi = this.expr(e.hi);
        return `langGoRange{Lo: ${lo}, Hi: ${hi}}`;
      }

      case "index": {
        const obj = this.expr(e.obj);
        const idx = this.expr(e.idx);
        return `langGoIndex[${goTypeOf(e.typ)}](${obj}, ${idx})`;
      }

      case "slice": {
        const obj = this.expr(e.obj);
        const lo = this.expr(e.lo);
        const hi = this.expr(e.hi);
        return `langGoSlice(${obj}, ${lo}, ${hi})`;
      }

      case "field": {
        const obj = this.expr(e.obj);
        if (!isAnyT(e.obj.typ) && e.obj.typ.kind === "error") {
          if (e.name === "msg") return `langGoErrMsg(${obj})`;
          throw new GenError("error 只有 msg 字段");
        }
        if (this.gt(e) === "any") {
          return `langGoField(${obj}, ${JSON.stringify(e.name)})`;
        }
        return `${obj}.${e.name}`;
      }

      case "try":
        throw new GenError("? 只能出现在 let/return/赋值的顶层（→Go 约束）");

      case "struct_lit": {
        const name = e.struct !== undefined ? e.struct.name : "";
        const sd = this.structs.get(name);
        const fs = e.fields.map((f, i) => {
          let v = this.expr(f.val);
          if (sd !== undefined && i < sd.fields.length) {
            v = this.wrapArg(sd.fields[i]!.typ, e.fields[i]!.val, v);
          }
          return `${f.name}: ${v}`;
        });
        return `${name}{${fs.join(", ")}}`;
      }

      case "map_lit": {
        const kvs = e.keys.map((k, i) => {
          const ks = this.expr(k);
          const vs = this.expr(e.vals[i]!);
          return `langGoKV{${ks}, ${vs}}`;
        });
        return `langGoNewMap(${kvs.join(", ")})`;
      }

      case "slice_lit": {
        let elem = "any";
        if (e.typ.kind === "slice" && !isAnyT(e.typ.elem)) {
          elem = goTypeOf(e.typ.elem);
        }
        const items = e.elems.map((el) => {
          let v = this.expr(el);
          if (e.typ.elem !== undefined && !isAnyT(e.typ.elem) && this.gt(el) === "any") {
            v = `langGoTo[${goTypeOf(e.typ.elem)}](${v})`;
          }
          return v;
        });
        if (items.length === 0) return `&LangGoArr[${elem}]{}`;
        return `&LangGoArr[${elem}]{Items: []${elem}{${items.join(", ")}}}`;
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

  /** 构造器：预载 Option/Result 与用户 enum */
  private emitCtor(x: IrCtor): string {
    switch (x.sym.name) {
      case "None": {
        // None 无载荷无法靠推断：类型实参取当前函数返回类型的元素
        let tp = "any";
        if (this.curFn !== undefined) {
          if (this.curFn.typeParams.length === 1) {
            tp = this.curFn.typeParams[0]!;
          } else if (
            this.curFn.ret !== undefined && this.curFn.ret.kind === "option" &&
            this.curFn.ret.elem !== undefined && !isAnyT(this.curFn.ret.elem)
          ) {
            tp = goTypeOf(this.curFn.ret.elem);
          }
        }
        return `LangGoNone[${tp}]()`;
      }
      case "Some": {
        if (x.args.length !== 1) throw new GenError("Some 需要 1 个参数");
        const arg = this.expr(x.args[0]!);
        return `LangGoSome(${arg})`;
      }
      default: {
        if (x.sym.name === "Ok" || x.sym.name === "Err") {
          throw new GenError("Ok/Err 只能出现在 Result 函数的 return（→Go 约束）");
        }
        const en = this.caseEnum.get(x.sym.name) ?? "";
        const args = x.args.map((a) => this.expr(a));
        return `le_${en}_${x.sym.name}(${args.join(", ")})`;
      }
    }
  }

  private emitBinary(x: IrBinary): string {
    let l = this.operand(x.l);
    let r = this.operand(x.r);
    switch (x.op) {
      case "&&":
        return `${l} && ${r}`;
      case "||":
        return `${l} || ${r}`;
      case "==":
        return `langGoEq(${l}, ${r})`;
      case "!=":
        return `!langGoEq(${l}, ${r})`;
      case "<":
      case "<=":
      case ">":
      case ">=": {
        const cmp = `langGoCompare(${l}, ${r})`;
        if (x.op === "<") return `${cmp} < 0`;
        if (x.op === "<=") return `${cmp} <= 0`;
        if (x.op === ">") return `${cmp} > 0`;
        return `${cmp} >= 0`;
      }
      case "+": {
        const li = this.gt(x.l);
        const ri = this.gt(x.r);
        if (li === "any" || ri === "any") {
          if (li === "string" || ri === "string") {
            return `langGoStrVal(${l}) + langGoStrVal(${r})`;
          }
          return `langGoInt(${l}) + langGoInt(${r})`;
        }
        break;
      }
      case "-":
      case "*":
      case "/":
      case "%":
      case "&":
      case "|":
      case "^":
      case "<<":
      case ">>": {
        if (this.gt(x.l) === "any") l = `langGoInt(${l})`;
        if (this.gt(x.r) === "any") r = `langGoInt(${r})`;
        break;
      }
      default:
        break;
    }
    return `${l} ${x.op} ${r}`;
  }

  /** 用户 fn / 内建 / 方法 / 函数值 */
  private emitCall(x: IrCall): string {
    // 内建
    if (x.builtin !== undefined && x.builtin !== "") {
      if (x.builtin === "http.get") {
        const args = x.args.map((a) => this.expr(a));
        return `langGoHTTP.get(${args.join(", ")})`;
      }
      const bf = builtinMap[x.builtin];
      if (bf === undefined) {
        throw new GenError(`未知内建 ${x.builtin}`);
      }
      return this.emitBuiltinCall(x, bf);
    }
    // 用户 fn
    if (x.fn !== undefined) {
      const fi = this.fns.get(x.fn);
      if (fi === undefined) {
        throw new GenError(`内部错误：fn 未注册 ${x.fn.name}`);
      }
      const args = x.args.map((a, i) => {
        let arg = this.expr(a);
        if (i < fi.params.length) {
          arg = noneCtorFor(fi.params[i]!.typ, a, arg);
          arg = this.wrapArg(fi.params[i]!.typ, a, arg);
        }
        return arg;
      });
      return `${this.goName(fi)}(${args.join(", ")})`;
    }
    // 方法
    if (x.method !== undefined) {
      const obj = this.expr(x.recv!);
      const args = x.args.map((a) => this.expr(a));
      return `${obj}.${x.method.name}(${args.join(", ")})`;
    }
    // 函数值（含 fnlit 绑定：按注册的参数类型收窄实参）
    if (x.callee !== undefined && x.callee.kind === "local_get" && x.callee.local.sym !== undefined) {
      const fi = this.fns.get(x.callee.local.sym);
      if (fi !== undefined) {
        const args = x.args.map((a, i) => {
          let arg = this.expr(a);
          if (i < fi.params.length) {
            arg = noneCtorFor(fi.params[i]!.typ, a, arg);
            arg = this.wrapArg(fi.params[i]!.typ, a, arg);
          }
          return arg;
        });
        return `${x.callee.local.name}(${args.join(", ")})`;
      }
    }
    const callee = x.callee !== undefined ? this.expr(x.callee) : "";
    const args = x.args.map((a) => this.expr(a));
    return `${callee}(${args.join(", ")})`;
  }

  private emitBuiltinCall(x: IrCall, bf: string): string {
    if (bf === "langGoAssert") {
      if (x.args.length < 1 || x.args.length > 2) {
        throw new GenError("assert 需要 1~2 个参数");
      }
      let cond = this.expr(x.args[0]!);
      if (this.gt(x.args[0]!) !== "bool") {
        cond = `langGoTruthy(${cond})`;
      }
      if (x.args.length === 2) {
        const msg = this.expr(x.args[1]!);
        return `langGoAssert(${cond}, ${msg})`;
      }
      return `langGoAssert(${cond})`;
    }
    if (bf === "langGoPush") {
      if (x.args.length < 2) {
        throw new GenError("push 需要至少 2 个参数");
      }
      const arr = this.expr(x.args[0]!);
      const at = goTypeOf(x.args[0]!.typ);
      if (!at.startsWith("*LangGoArr[")) {
        throw new GenError("push 目标类型未知（→Go 需可推断的数组）");
      }
      const elem = at.slice("*LangGoArr[".length, -1);
      const vs = x.args.slice(1).map((a) => {
        let v = this.expr(a);
        if (elem !== "any" && this.gt(a) === "any") {
          v = `langGoTo[${elem}](${v})`;
        }
        return v;
      });
      return `${arr}.Push(${vs.join(", ")})`;
    }
    const args = x.args.map((a) => this.expr(a));
    return `${bf}(${args.join(", ")})`;
  }

  /** 表达式位置的 match：包成立即调用的函数字面量（独立缓冲渲染） */
  private exprMatch(m: IrMatch): string {
    const savedLines = this.lines;
    const savedIndent = this.indent;
    this.lines = [];
    this.indent = 0;

    const retType = this.matchRetType(m);
    const savedMatchTyp = this.curMatchTyp;
    this.curMatchTyp = m.typ;
    const tmp = this.newTmp();
    let scrut = this.expr(m.scrut);
    scrut = this.coerceAlgebraic(scrut, m);
    this.w(`func() ${retType} {`);
    this.indent++;
    this.w(`${tmp} := ${scrut}`);
    switch (m.matchKind) {
      case "result": {
        const [okArm, errArm] = splitAlgebraic(m, "Ok", "Err");
        this.w(`if ${tmp}.ok {`);
        this.emitResultArmReturn(okArm, tmp, ".v");
        this.w("} else {");
        this.emitResultArmReturn(errArm, tmp, ".e");
        this.w("}");
        break;
      }
      case "option": {
        const [someArm, noneArm] = splitAlgebraic(m, "Some", "None");
        this.w(`if ${tmp}.has {`);
        this.emitResultArmReturn(someArm, tmp, ".v");
        if (noneArm !== undefined) {
          this.w("} else {");
          this.emitResultArmReturnOpt(noneArm, tmp, ".e");
        }
        this.w("}");
        break;
      }
      case "enum": {
        this.w(`switch ${tmp}.Tag {`);
        this.indent++;
        let hasDefault = false;
        for (const arm of m.arms) {
          if (arm.pat.kind === "ctor_pat") {
            this.w(`case ${JSON.stringify(arm.pat.sym?.name ?? "")}:`);
            this.indent++;
            this.bindEnumPayloads(arm.pat, tmp);
            this.emitArmReturn(arm);
            this.indent--;
          } else {
            hasDefault = true;
            this.w("default:");
            this.indent++;
            if (arm.pat.kind === "bind_pat") {
              this.w(`${arm.pat.name} := ${tmp}`);
              this.w(`_ = ${arm.pat.name}`);
            }
            this.emitArmReturn(arm);
            this.indent--;
          }
        }
        if (!hasDefault) {
          // 编译期穷尽检查已保证不可达；仅为满足 Go 的 missing return
          this.w("default:");
          this.indent++;
          this.w(`panic("match 无匹配分支")`);
          this.indent--;
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
    this.w("}()");
    this.curMatchTyp = savedMatchTyp;
    const out = this.lines.join("\n");
    this.lines = savedLines;
    this.indent = savedIndent;
    return out;
  }

  private emitResultArmReturn(arm: IrArm | undefined, tmp: string, field: string): void {
    if (arm === undefined) return;
    this.indent++;
    const name = armBindName(arm);
    if (name !== "_") {
      this.w(`${name} := ${tmp}${field}`);
      this.w(`_ = ${name}`);
    }
    this.emitArmReturn(arm);
    this.indent--;
  }

  /** Option 的 None 臂（field 参数语义仅用于 Result Err；None 臂为通配返回） */
  private emitResultArmReturnOpt(arm: IrArm, _tmp: string, _field: string): void {
    this.indent++;
    this.emitArmReturn(arm);
    this.indent--;
  }

  /** 把 any 臂体收窄到 match 的结果类型 */
  private wrapTo(expr: string, body: IrExpr, target: GoType): string {
    if (target !== undefined && !isAnyT(target) && this.gt(body) === "any") {
      return `langGoTo[${goTypeOf(target)}](${expr})`;
    }
    return expr;
  }

  private emitLiteralMatchReturn(m: IrMatch, tmp: string): void {
    for (let i = 0; i < m.arms.length; i++) {
      const arm = m.arms[i]!;
      const p = arm.pat;
      if (p.kind === "lit_pat") {
        const kw = i > 0 ? "} else if" : "if";
        const lit = this.expr(p.val);
        this.w(`${kw} langGoEq(${tmp}, ${lit}) {`);
        this.indent++;
        this.emitArmReturn(arm);
        this.indent--;
      } else if (p.kind === "bind_pat") {
        this.w("} else {");
        this.indent++;
        this.w(`${p.name} := ${tmp}`);
        this.w(`_ = ${p.name}`);
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
    let expr = this.expr(arm.body);
    expr = this.wrapTo(expr, arm.body, this.curMatchTyp);
    this.w(`return ${expr}`);
  }

  /** 函数字面量：独立缓冲渲染后作为表达式内嵌 */
  private emitFnLit(x: Extract<IrExpr, { kind: "fn_lit" }>): string {
    const ps = x.params.map((p) => `${p.name} ${goTypeOf(p.typ)}`);
    const retDecl = x.ret !== undefined ? " " + goTypeOf(x.ret) : "";
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
    this.w(`func(${ps.join(", ")})${retDecl} {`);
    for (const st of x.body) this.emitStmt(st);
    this.w("}");
    this.curFn = savedFn;
    const out = this.lines.join("\n");
    this.lines = savedLines;
    this.indent = savedIndent;
    return out;
  }
}

function bodyHasGo(stmts: readonly IrStmt[]): boolean {
  for (const st of stmts) {
    switch (st.kind) {
      case "spawn":
        return true;
      case "if":
        if (bodyHasGo(st.then) || bodyHasGo(st.els)) return true;
        break;
      case "while":
        if (bodyHasGo(st.body)) return true;
        break;
      case "for_in":
        if (bodyHasGo(st.body)) return true;
        break;
      default:
        break;
    }
  }
  return false;
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

/** Transpile IR → Go 源码 */
export function transpile(p: IrProg): string {
  return new Gen(p).emitProgram();
}
