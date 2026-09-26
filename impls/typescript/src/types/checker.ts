// Checker：对 AST 做语义类型检查，产出每表达式的类型（HIR/IR 与后端共用）。
// 遍历结构与 resolver 同构；名字解析结果复用 ResolveResult。
// 本检查器收编了 →Go/→JS 转译器的全部前置约束（参数注解、? 位置与约束、
// Result 函数 return 形态、match 分类与穷尽性），后端不再重复实现。
// 与 Go 证据的两处刻意分歧（按裁决修正）：赋值顶层 `?` 走 inferTry（ADR-0005）；
// 块内 let/循环变量/match 臂绑定写当前块作用域（NAME-SCOPE-001 词法作用域语义）。

import type { Expr, FnDecl, MatchEx, Pat, Stmt, TryEx, TypeEx } from "../syntax/ast.js";
import type { Diagnostic } from "../common/diagnostic.js";
import {
  E3001, E3002, E3003, E3004, E3005, E3006, E3007, E3008, E3009,
  E3010, E3011, E3012, E3013, E3014, E4001, E4002, E4003,
} from "../common/diagnostic.js";
import type { ResolveResult } from "../semantic/resolver.js";
import type { Symbol, SymbolId } from "../semantic/symbols.js";
import {
  Any, Bool, ErrorT, Float, Int, Invalid, String, Unknown,
  ChanOf, EnumOf, FnOf, MapOf, OptionOf, RefOf, ResultOf, SliceOf, StructOf,
  assignable, equal, isConcrete, isNumeric, ResultOk, ResultErr, typeStr,
  type Type,
} from "./model.js";
import { resolveAnno } from "./anno.js";

/** match 的语义分类（checker 判定，HIR/IR/后端直接消费结论） */
export type MatchKind = "literal" | "enum" | "result" | "option";

export interface CheckResult {
  /** 表达式 → 类型 */
  readonly exprTy: Map<Expr, Type>;
  /** 绑定模式 → 捕获类型 */
  readonly patTy: Map<Pat, Type>;
  /** 绑定/参数符号 → 类型（HIR/协议用） */
  readonly symTy: Map<SymbolId, Type>;
  /** fn/方法符号 → 函数类型 */
  readonly fnTy: Map<SymbolId, Type>;
  /** match → 分类 */
  readonly matchKind: Map<MatchEx, MatchKind>;
  readonly diags: Diagnostic[];
}

export const hasCheckErrors = (res: CheckResult): boolean => res.diags.length > 0;

/** 复合赋值运算符的诊断名（Go opName：仅认识复合赋值与 =，其余一律 "?"） */
function opName(k: string): string {
  switch (k) {
    case "+=":
      return "+=";
    case "-=":
      return "-=";
    case "*=":
      return "*=";
    case "/=":
      return "/=";
    case "=":
      return "=";
    default:
      return "?";
  }
}

function joinNames(names: readonly string[]): string {
  return names.join(", ");
}

/** 内建函数签名（参数不检查该位；返回类型依实参特化） */
const builtinSigs: Readonly<Record<string, (c: Checker, args: readonly Type[]) => Type>> = {
  println: () => UnitT,
  print: () => UnitT,
  len: () => Int,
  str: () => String,
  int: () => Int,
  float: () => Float,
  keys: () => SliceOf(Unknown),
  values: () => SliceOf(Unknown),
  has: () => Bool,
  push: (_c, a) => (a.length > 0 ? a[0]! : Unknown),
  assert: () => UnitT,
  err: () => ErrorT,
  bytes: () => SliceOf(Int),
  char: () => String,
  jsonParse: () => Any,
  jsonStringify: () => String,
  isNone: () => Bool,
  makeChan: () => ChanOf(Unknown),
  send: () => UnitT,
  recv: (_c, a) =>
    a.length === 1 && a[0]!.kind === "chan" ? ResultOf(a[0]!.elem ?? Unknown, ErrorT) : ResultOf(Unknown, ErrorT),
};

const UnitT: Type = { kind: "unit", elem: undefined, key: undefined, params: [], ret: undefined, sym: undefined, name: "" };

class Checker {
  private readonly exprTy = new Map<Expr, Type>();
  private readonly patTy = new Map<Pat, Type>();
  private readonly symTy = new Map<SymbolId, Type>();
  private readonly fnTy = new Map<SymbolId, Type>();
  private readonly matchKind = new Map<MatchEx, MatchKind>();
  private readonly diags: Diagnostic[] = [];
  /** 绑定类型作用域栈（名字 → 类型） */
  private scopes: Map<string, Type>[] = [];
  /** 当前函数返回注解（undefined = 无注解） */
  private fnRet: Type | undefined;
  /** 当前函数的泛型参数名集（注解解析用） */
  private tps: Set<string> = new Set();
  private inFn = false;

  constructor(private readonly res: ResolveResult) {}

  private errf(line: number, code: string, msg: string): void {
    this.diags.push({ code, msg, line });
  }

  check(prog: readonly Stmt[]): CheckResult {
    this.walk(prog);
    return {
      exprTy: this.exprTy,
      patTy: this.patTy,
      symTy: this.symTy,
      fnTy: this.fnTy,
      matchKind: this.matchKind,
      diags: this.diags,
    };
  }

  // ———— 程序遍历：与 resolver 同构 ————

  private walk(prog: readonly Stmt[]): void {
    this.pushScope();
    // 1. 顶层绑定依序（前向引用已被 resolver 拦）
    for (const s of prog) {
      if (s.kind === "let_stmt") this.letStmt(s);
    }
    // 2. fn 与方法体
    for (const s of prog) {
      if (s.kind === "fn_decl" && s.recv === undefined) {
        this.fnBody(s, this.res.declSyms.get(s), undefined);
      }
    }
    for (const s of prog) {
      if (s.kind === "fn_decl" && s.recv !== undefined) {
        this.fnBody(s, this.methodSym(s), this.methodStructType(s));
      }
    }
    // 3. 顶层游离语句（脚本模式；interp 可执行，转译不支持——只做类型走查）
    for (const s of prog) {
      if (
        s.kind === "let_stmt" || s.kind === "fn_decl" || s.kind === "struct_decl" ||
        s.kind === "enum_decl" || s.kind === "extern" || s.kind === "import"
      ) {
        continue;
      }
      this.stmt(s);
    }
    this.popScope();
  }

  private methodSym(d: FnDecl): Symbol | undefined {
    const rt = d.recv?.typ;
    if (rt === undefined || rt.kind !== "named_type") return undefined;
    const st = this.lookupTypeSym(rt.name);
    return st !== undefined ? st.methods.get(d.name) : undefined;
  }

  private methodStructType(d: FnDecl): Type {
    const rt = d.recv?.typ;
    if (rt === undefined || rt.kind !== "named_type") return Unknown;
    const st = this.lookupTypeSym(rt.name);
    return st !== undefined ? StructOf(st) : Unknown;
  }

  /** 函数体检查：参数注解必填（E3007），建立返回上下文 */
  private fnBody(d: FnDecl, sym: Symbol | undefined, recvTy: Type | undefined): void {
    const savedRet = this.fnRet;
    const savedInFn = this.inFn;
    const savedScopes = this.scopes;
    const savedTps = this.tps;
    this.scopes = [new Map()];
    this.tps = tparamsOf(d.typeParams);
    const sig = this.fnSignature(d);
    this.fnRet = d.ret !== undefined ? sig.ret : undefined; // 注解只解析一次，错误不重复报
    this.inFn = true;

    if (d.recv !== undefined) {
      this.scopes[0]!.set(d.recv.name, recvTy ?? Unknown);
    }
    this.declareParams(d, sym, recvTy);
    if (sym !== undefined) this.fnTy.set(sym.id, sig);
    for (const s of d.body) this.stmt(s);
    this.fnRet = savedRet;
    this.inFn = savedInFn;
    this.scopes = savedScopes;
    this.tps = savedTps;
  }

  /** 解析参数注解（缺注解 → E3007 + Unknown）入作用域，并把类型按序落到参数符号的 symTy */
  private declareParams(d: FnDecl, sym: Symbol | undefined, recvTy: Type | undefined): void {
    const psyms: readonly Symbol[] = sym !== undefined ? this.res.fnParams.get(sym.id) ?? [] : [];
    let pi = 0;
    if (d.recv !== undefined) {
      if (pi < psyms.length) {
        this.symTy.set(psyms[pi]!.id, recvTy ?? Unknown);
        pi++;
      }
    }
    for (const p of d.params) {
      let pt: Type;
      if (p.typ === undefined) {
        this.errf(d.line, E3007, `参数 ${p.name} 缺少类型注解`);
        pt = Unknown;
      } else {
        pt = this.resolveTypeEx(p.typ, tparamsOf(d.typeParams));
      }
      this.scopes[0]!.set(p.name, pt);
      if (p.name !== "_" && pi < psyms.length) {
        this.symTy.set(psyms[pi]!.id, pt);
        pi++;
      }
    }
  }

  /** fn/方法的函数类型（参数注解缺失 → Unknown 占位） */
  private fnSignature(d: FnDecl): Type {
    const tps = tparamsOf(d.typeParams);
    const ps: Type[] = [];
    for (const p of d.params) {
      ps.push(p.typ === undefined ? Unknown : this.resolveTypeEx(p.typ, tps));
    }
    const ret = d.ret !== undefined ? this.resolveTypeEx(d.ret, tps) : Unknown;
    return FnOf(ps, ret);
  }

  private letStmt(d: Extract<Stmt, { kind: "let_stmt" }>): void {
    const sym = this.res.declSyms.get(d);
    let ty: Type;
    if (d.init.kind === "try") {
      ty = this.inferTry(d.init);
    } else {
      ty = this.infer(d.init);
    }
    if (d.typ !== undefined) {
      const anno = this.resolveTypeEx(d.typ, this.tps);
      if (!assignable(anno, ty)) {
        this.errf(d.line, E3001, `不能把 ${typeStr(ty)} 绑定为注解类型 ${typeStr(anno)}`);
      }
      ty = anno;
    }
    if (sym !== undefined) this.symTy.set(sym.id, ty);
    this.defineVar(d.name, ty);
  }

  // ———— 语句 ————

  private stmt(s: Stmt): void {
    switch (s.kind) {
      case "let_stmt":
        this.letStmt(s);
        return;

      case "assign":
        this.assign(s);
        return;

      case "expr_stmt":
        this.infer(s.x);
        return;

      case "return":
        this.returnStmt(s);
        return;

      case "if":
        this.requireBool(s.cond, "if 条件");
        this.block(s.then);
        if (s.els !== undefined) this.block(s.els);
        return;

      case "while":
        this.requireBool(s.cond, "while 条件");
        this.block(s.body);
        return;

      case "for_in": {
        const ty = this.infer(s.iter);
        let elem: Type;
        if (ty.kind === "slice") elem = ty.elem ?? Unknown;
        else if (ty.kind === "map") elem = ty.key ?? Unknown;
        else if (ty.kind === "string") elem = String;
        else if (ty.kind === "range") elem = Int;
        else if (!isConcrete(ty)) elem = Unknown;
        else {
          this.errf(s.line, E3010, `${typeStr(ty)} 不可迭代`);
          elem = Unknown;
        }
        const sym = this.res.declSyms.get(s);
        if (sym !== undefined) this.symTy.set(sym.id, elem);
        this.pushScope();
        this.defineVar(s.variable, elem);
        this.stmts(s.body);
        this.popScope();
        return;
      }

      case "go":
        this.inferCall(s.call);
        return;

      case "fn_decl":
        if (s.recv === undefined) this.fnBody(s, this.res.declSyms.get(s), undefined);
        return;

      case "struct_decl":
      case "enum_decl":
        // 嵌套类型声明由 resolver 报 E2010
        return;

      case "break":
      case "continue":
      case "import":
      case "extern":
        // 位置合法性由 resolver 管；extern 无类型面
        return;

      default: {
        const _never: never = s;
        return _never;
      }
    }
  }

  private pushScope(): void {
    this.scopes.push(new Map());
  }

  private popScope(): void {
    this.scopes.pop();
  }

  /** 绑定类型写入当前（最内层）块作用域。NAME-SCOPE-001：块内 let/循环变量/match 臂绑定不得写入函数/全局层 */
  private defineVar(name: string, t: Type): void {
    this.scopes[this.scopes.length - 1]!.set(name, t);
  }

  private lookupVar(name: string): { t: Type; ok: boolean } {
    for (let i = this.scopes.length - 1; i >= 0; i--) {
      const t = this.scopes[i]!.get(name);
      if (t !== undefined) return { t, ok: true };
    }
    return { t: Unknown, ok: false };
  }

  private block(list: readonly Stmt[]): void {
    this.pushScope();
    this.stmts(list);
    this.popScope();
  }

  private stmts(list: readonly Stmt[]): void {
    for (const s of list) this.stmt(s);
  }

  private assign(st: Extract<Stmt, { kind: "assign" }>): void {
    let valTy: Type;
    if (st.val.kind === "try") {
      // ADR-0005：赋值右值位置的顶层 ? 合法，走与 let/return 相同的传播检查
      valTy = this.inferTry(st.val);
    } else {
      valTy = this.infer(st.val);
    }
    const tgt = st.target;
    if (tgt.kind === "ident") {
      const { t: tgtTy } = this.lookupVar(tgt.name);
      if (st.op !== "=") {
        this.checkCompound(st, tgtTy, valTy);
        return;
      }
      if (!assignable(tgtTy, valTy)) {
        this.errf(st.line, E3001, `不能把 ${typeStr(valTy)} 赋给 ${typeStr(tgtTy)}`);
      }
      return;
    }
    if (tgt.kind === "field") {
      const objTy = this.infer(tgt.obj);
      const fTy = this.fieldType(objTy, tgt.name, tgt.line);
      if (st.op !== "=") {
        this.checkCompound(st, fTy, valTy);
        return;
      }
      if (!assignable(fTy, valTy)) {
        this.errf(st.line, E3001, `不能把 ${typeStr(valTy)} 赋给字段 ${tgt.name}（${typeStr(fTy)}）`);
      }
      return;
    }
    if (tgt.kind === "index") {
      const objTy = this.infer(tgt.obj);
      const idxTy = this.infer(tgt.idx);
      this.checkIndexKey(objTy, idxTy, tgt.line);
      const elemTy = this.indexElemTy(objTy, tgt.line);
      if (st.op !== "=") {
        this.checkCompound(st, elemTy, valTy);
        return;
      }
      if (!assignable(elemTy, valTy)) {
        this.errf(st.line, E3001, `不能把 ${typeStr(valTy)} 赋给容器元素（${typeStr(elemTy)}）`);
      }
      return;
    }
  }

  /** 复合赋值（+= -= *= /=）：同型数值/字符串；数组 += 数组 */
  private checkCompound(
    st: Extract<Stmt, { kind: "assign" }>,
    tgtTy: Type,
    valTy: Type,
  ): void {
    if (!isConcrete(tgtTy) || !isConcrete(valTy)) return;
    if (tgtTy.kind === "slice") {
      if (st.op === "+=" && valTy.kind === "slice") return;
      this.errf(st.line, E3001, `数组 ${opName(st.op)} 需要数组右值，得到 ${typeStr(valTy)}`);
      return;
    }
    if (tgtTy.kind === valTy.kind && (isNumeric(tgtTy) || tgtTy.kind === "string")) return;
    this.errf(st.line, E3002, `${typeStr(tgtTy)} 与 ${typeStr(valTy)} 不可做 ${opName(st.op)}（无隐式转换）`);
  }

  private returnStmt(st: Extract<Stmt, { kind: "return" }>): void {
    if (!this.inFn) return; // resolver 已报 E2008
    if (st.val === undefined) {
      if (this.fnRet !== undefined && this.fnRet.kind === "result") {
        this.errf(st.line, E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)");
      } else if (this.fnRet !== undefined && isConcrete(this.fnRet)) {
        this.errf(st.line, E3001, `注解为 ${typeStr(this.fnRet)} 的函数不能裸 return`);
      }
      return;
    }
    if (st.val.kind === "try") {
      this.inferTry(st.val);
      return;
    }
    if (this.fnRet !== undefined && this.fnRet.kind === "result") {
      this.inferResultReturn(st, this.fnRet);
      return;
    }
    const valTy = this.infer(st.val);
    if (this.fnRet !== undefined && isConcrete(this.fnRet) && !assignable(this.fnRet, valTy)) {
      this.errf(st.line, E3001, `返回 ${typeStr(valTy)} 与注解 ${typeStr(this.fnRet)} 不匹配`);
    }
  }

  private inferResultReturn(st: Extract<Stmt, { kind: "return" }>, ret: Type): void {
    const call = st.val;
    if (call === undefined || call.kind !== "call") {
      this.errf(st.line, E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)");
      if (call !== undefined) this.infer(call);
      return;
    }
    const id = call.callee;
    if (id.kind !== "ident" || (id.name !== "Ok" && id.name !== "Err") || call.args.length !== 1) {
      this.errf(st.line, E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)");
      this.infer(call);
      return;
    }
    const argTy = this.infer(call.args[0]!);
    if (id.name === "Ok") {
      if (!assignable(ResultOk(ret), argTy)) {
        this.errf(st.line, E3001, `Ok(${typeStr(argTy)}) 与返回类型 ${typeStr(ret)} 不匹配`);
      }
    } else if (!assignable(ResultErr(ret), argTy)) {
      this.errf(st.line, E3001, `Err(${typeStr(argTy)}) 与返回类型 ${typeStr(ret)} 不匹配`);
    }
    this.exprTy.set(call, ret);
  }

  // ———— 注解 ————

  private lookupTypeSym(name: string): Symbol | undefined {
    const b = this.res.builtin.get(name);
    if (b !== undefined && b.kind === "struct") return b;
    for (const s of this.res.symbols) {
      if ((s.kind === "struct" || s.kind === "enum") && s.name === name) return s;
    }
    return undefined;
  }

  /** 源码注解 → 语义类型。tps 为当前泛型参数名集 */
  private resolveTypeEx(t: TypeEx, tps: Set<string>): Type {
    switch (t.kind) {
      case "named_type":
        switch (t.name) {
          case "int":
            return Int;
          case "float":
            return Float;
          case "string":
            return String;
          case "bool":
            return Bool;
          case "error":
            return ErrorT;
          case "any":
            return Any;
          case "chan": {
            const elem = t.args.length === 1 ? this.resolveTypeEx(t.args[0]!, tps) : Unknown;
            return ChanOf(elem);
          }
          case "Option":
            if (t.args.length === 1) return OptionOf(this.resolveTypeEx(t.args[0]!, tps));
            this.errf(0, E3001, "Option 需要一个类型参数");
            return Invalid;
          case "Result":
            if (t.args.length !== 2) {
              this.errf(0, E3001, "Result 需要两个类型参数");
              return Invalid;
            }
            {
              const en = t.args[1]!;
              if (en.kind !== "named_type" || en.name !== "error") {
                this.errf(0, E3009, "Result 第二类型参数须为 error");
                // 仍返回 Result 形：避免下游把 Result 函数误判为普通函数（E3006 连锁）
                return ResultOf(this.resolveTypeEx(t.args[0]!, tps), ErrorT);
              }
            }
            return ResultOf(this.resolveTypeEx(t.args[0]!, tps), ErrorT);
          default: {
            if (tps.has(t.name)) {
              return { kind: "type_arg", elem: undefined, key: undefined, params: [], ret: undefined, sym: undefined, name: t.name };
            }
            const sym = this.lookupTypeSym(t.name);
            if (sym !== undefined) return sym.kind === "enum" ? EnumOf(sym) : StructOf(sym);
            return Unknown; // 未知注解不阻塞（保守）
          }
        }
      case "arr_type":
        return SliceOf(this.resolveTypeEx(t.elem, tps));
      case "map_type":
        return MapOf(this.resolveTypeEx(t.key, tps), this.resolveTypeEx(t.val, tps));
      case "ptr_type":
        return RefOf(this.resolveTypeEx(t.elem, tps));
      case "fn_type": {
        const ps = t.params.map((p) => this.resolveTypeEx(p, tps));
        const ret = t.ret !== undefined ? this.resolveTypeEx(t.ret, tps) : Unknown;
        return FnOf(ps, ret);
      }
      default: {
        const _never: never = t;
        return _never;
      }
    }
  }

  // ———— 推断主入口 ————

  /** 推断并记录表达式类型 */
  private infer(e: Expr): Type {
    const memo = this.exprTy.get(e);
    if (memo !== undefined) return memo;
    const t = this.compute(e);
    this.exprTy.set(e, t);
    return t;
  }

  private compute(e: Expr): Type {
    switch (e.kind) {
      case "int_lit":
        return Int;
      case "float_lit":
        return Float;
      case "str_lit":
        return String;
      case "bool_lit":
        return Bool;

      case "ident":
        return this.identType(e);

      case "unary":
        return this.inferUnary(e);

      case "binary":
        return this.inferBinary(e);

      case "range": {
        const lo = this.infer(e.lo);
        const hi = this.infer(e.hi);
        if ((isConcrete(lo) && lo.kind !== "int") || (isConcrete(hi) && hi.kind !== "int")) {
          this.errf(e.line, E3001, `区间两端须为 int，得到 ${typeStr(lo)}..${typeStr(hi)}`);
          return Invalid;
        }
        return { kind: "range", elem: undefined, key: undefined, params: [], ret: undefined, sym: undefined, name: "" };
      }

      case "call":
        return this.inferCall(e);

      case "index": {
        const obj = this.infer(e.obj);
        const idx = this.infer(e.idx);
        this.checkIndexKey(obj, idx, e.line);
        return this.indexElemTy(obj, e.line);
      }

      case "slice": {
        const obj = deref(this.infer(e.obj));
        const lo = this.infer(e.lo);
        const hi = this.infer(e.hi);
        for (const t of [lo, hi]) {
          if (isConcrete(t) && t.kind !== "int") {
            this.errf(e.line, E3013, `切片端点须为 int，得到 ${typeStr(t)}`);
          }
        }
        switch (obj.kind) {
          case "slice":
            return obj;
          case "string":
            return String;
          case "any":
          case "unknown":
          case "invalid":
            return Unknown;
          default:
            this.errf(e.line, E3001, `${typeStr(obj)} 不可切片`);
            return Invalid;
        }
      }

      case "field":
        return this.fieldType(this.infer(e.obj), e.name, e.line);

      case "struct_lit":
        return this.inferStructLit(e);

      case "map_lit":
        return this.inferMapLit(e);

      case "arr_lit":
        return this.inferArrLit(e);

      case "match":
        return this.inferMatch(e);

      case "fn_lit":
        return this.inferFnLit(e);

      case "try":
        // 非语句顶层位置（let/return/赋值已在语句层处理）
        this.errf(e.line, E3005, "? 只能出现在 let/return/赋值的顶层");
        return this.inferTryOperand(e);

      default: {
        const _never: never = e;
        return _never;
      }
    }
  }

  /** Ident 类型：作用域栈（参数/绑定/臂捕获）优先，再走符号路径 */
  private identType(x: Extract<Expr, { kind: "ident" }>): Type {
    const lv = this.lookupVar(x.name);
    if (lv.ok && lv.t.kind !== "invalid") return lv.t;
    const sym = this.res.refs.get(x);
    if (sym !== undefined) {
      switch (sym.kind) {
        case "fn": {
          const t = this.fnTy.get(sym.id);
          if (t !== undefined) return t;
          if (sym.decl !== undefined && sym.decl.kind === "fn_decl") {
            const t = this.fnSignature(sym.decl);
            this.fnTy.set(sym.id, t);
            return t;
          }
          return Unknown;
        }
        case "ctor":
          return this.ctorValueType(sym, x.line);
        default: {
          const t = this.symTy.get(sym.id);
          return t !== undefined ? t : Unknown;
        }
      }
    }
    return Unknown; // 内建裸引用 / 未定义名（已报）→ 保守
  }

  /** 裸构造器引用（零参才合法，如 None） */
  private ctorValueType(sym: Symbol, line: number): Type {
    if (sym.arity !== 0) {
      this.errf(line, E3003, `构造器 ${sym.name} 须直接调用`);
      return Invalid;
    }
    return this.ctorResultType(sym, []);
  }

  /** 构造器调用的结果类型 */
  private ctorResultType(sym: Symbol, args: readonly Type[]): Type {
    switch (sym.structName) {
      case "Option": {
        const elem = args.length === 1 ? args[0]! : Unknown;
        return OptionOf(elem);
      }
      case "Result": {
        const ok = args.length === 1 ? args[0]! : Unknown;
        return ResultOf(ok, ErrorT);
      }
      default: {
        const en = this.lookupTypeSym(sym.structName);
        return en !== undefined ? EnumOf(en) : Unknown;
      }
    }
  }

  private inferUnary(x: Extract<Expr, { kind: "unary" }>): Type {
    const t = this.infer(x.x);
    switch (x.op) {
      case "-":
        if (!isConcrete(t)) return t;
        if (isNumeric(t)) return t;
        this.errf(x.line, E3011, `- 不能用于 ${typeStr(t)}`);
        return Invalid;
      case "!":
        if (isConcrete(t) && t.kind !== "bool") {
          this.errf(x.line, E3011, `! 不能用于 ${typeStr(t)}（须为 bool）`);
          return Invalid;
        }
        return Bool;
      case "&":
        if (x.x.kind !== "ident") {
          this.errf(x.line, E3012, "v0 只能对变量取引用");
          return Invalid;
        }
        return RefOf(t);
      default: {
        const _never: never = x.op;
        return _never;
      }
    }
  }

  private inferBinary(x: Extract<Expr, { kind: "binary" }>): Type {
    // 短路逻辑：操作数须 bool（与 interp truthy 一致）
    if (x.op === "&&" || x.op === "||") {
      this.requireBool(x.l, "逻辑运算");
      this.requireBool(x.r, "逻辑运算");
      return Bool;
    }
    const l = this.infer(x.l);
    const r = this.infer(x.r);

    switch (x.op) {
      case "==":
      case "!=": {
        if (isConcrete(l) && isConcrete(r)) {
          const sameScalar = l.kind === r.kind && (isNumeric(l) || l.kind === "string" || l.kind === "bool");
          const sameEnum = l.kind === "enum" && r.kind === "enum" && l.sym === r.sym;
          if (!sameScalar && !sameEnum) {
            this.errf(x.line, E3001, `${typeStr(l)} 与 ${typeStr(r)} 不可用 == 比较`);
            return Invalid;
          }
        }
        return Bool;
      }
      case "<":
      case "<=":
      case ">":
      case ">=": {
        if (isConcrete(l) && isConcrete(r)) {
          const ok = l.kind === r.kind && (isNumeric(l) || l.kind === "string");
          if (!ok) {
            this.errf(x.line, E3001, `${typeStr(l)} 与 ${typeStr(r)} 不可比较大小（无隐式转换）`);
            return Invalid;
          }
        }
        return Bool;
      }
      case "&":
      case "|":
      case "^":
      case "<<":
      case ">>": {
        if (isConcrete(l) && isConcrete(r)) {
          if (l.kind !== "int" || r.kind !== "int") {
            this.errf(x.line, E3001, `${typeStr(l)} 与 ${typeStr(r)} 不可位运算（须为 int）`);
            return Invalid;
          }
          return Int;
        }
        return Any;
      }

      // 算术
      case "+": {
        if (isConcrete(l) && l.kind === "string" && isConcrete(r) && r.kind === "string") {
          return String;
        }
        return this.arithType(x, l, r);
      }
      case "-":
      case "*":
      case "/":
        return this.arithType(x, l, r);
      case "%": {
        if (isConcrete(l) && l.kind === "float") {
          this.errf(x.line, E3001, "% 不能用于 float");
          return Invalid;
        }
        return this.arithType(x, l, r);
      }
      default: {
        const _never: never = x.op;
        return _never;
      }
    }
  }

  /** 数值运算类型：int⊗int→int、float⊗float→float、混用→E3002、动态→Any */
  private arithType(x: Extract<Expr, { kind: "binary" }>, l: Type, r: Type): Type {
    if (!isConcrete(l) || !isConcrete(r)) return Any;
    if (l.kind === r.kind && isNumeric(l)) return l;
    this.errf(x.line, E3002, `${typeStr(l)} 与 ${typeStr(r)} 不可做 ${opName(x.op)}（无隐式转换）`);
    return Invalid;
  }

  private requireBool(e: Expr, what: string): void {
    const t = this.infer(e);
    if (isConcrete(t) && t.kind !== "bool") {
      this.errf(e.line, E3011, `${what}须为 bool，得到 ${typeStr(t)}`);
    }
  }

  // ———— 调用 ————

  /** 调用推断 */
  private inferCall(x: Extract<Expr, { kind: "call" }>): Type {
    // 构造器 / 内建 / 用户 fn 的 Ident 调用
    if (x.callee.kind === "ident") {
      const id = x.callee;
      const sym = this.res.refs.get(id);
      if (sym !== undefined && sym.kind === "ctor") {
        const args: Type[] = [];
        for (const a of x.args) args.push(this.infer(a));
        if (args.length !== sym.arity) {
          this.errf(x.line, E3003, `${id.name} 需要 ${sym.arity} 个参数，得到 ${args.length} 个`);
          return Invalid;
        }
        if (sym.name === "Ok" || sym.name === "Err") {
          // Ok/Err 只能出现在 Result 函数的 return（returnStmt 已处理该路径）
          this.errf(x.line, E3006, "Ok/Err 只能出现在 Result 函数的 return");
        }
        return this.ctorResultType(sym, args);
      }
      // 内建调用（内建不进 refs，裸引用已由 resolver 放行给调用路径）
      if (sym === undefined) {
        const fn = builtinSigs[id.name];
        if (fn !== undefined && this.res.builtin.has(id.name)) {
          const args: Type[] = [];
          for (const a of x.args) args.push(this.infer(a));
          return fn(this, args);
        }
      }
      // 用户 fn
      if (sym !== undefined && sym.kind === "fn") {
        if (sym.decl !== undefined && sym.decl.kind === "fn_decl") {
          this.checkArgsVsDecl(sym.decl, x.args, x.line);
        }
        const t = this.fnTy.get(sym.id);
        if (t !== undefined) return t.ret ?? Unknown;
        return Unknown;
      }
    }

    // 方法/字段函数值调用：obj.name(args)
    if (x.callee.kind === "field") {
      return this.inferMethodCall(x.callee, x);
    }

    // 表达式 callee（函数字面量/索引）：按函数类型检查
    const calleeTy = this.infer(x.callee);
    const args: Type[] = [];
    for (const a of x.args) args.push(this.infer(a));
    if (calleeTy.kind === "fn") {
      this.checkArgsVsFn(calleeTy, args, x.line);
      return calleeTy.ret ?? Unknown;
    }
    return Unknown;
  }

  /** obj.name(args)：用户方法 / http.get / 字段函数值 */
  private inferMethodCall(fe: Extract<Expr, { kind: "field" }>, x: Extract<Expr, { kind: "call" }>): Type {
    const objTy = deref(this.infer(fe.obj));
    if (fe.obj.kind === "ident" && objTy.kind === "unknown") {
      const id = fe.obj;
      // http.get：预载服务值
      if (id.name === "http" && this.res.builtin.has("http") && fe.name === "get") {
        for (const a of x.args) this.infer(a);
        if (x.args.length !== 2) {
          this.errf(x.line, E3003, `http.get 需要 2 个参数（url, headers），得到 ${x.args.length} 个`);
        }
        const resp = this.lookupTypeSym("HttpResp");
        return resp !== undefined ? ResultOf(StructOf(resp), ErrorT) : ResultOf(Unknown, ErrorT);
      }
    }
    if (objTy.kind === "struct" && objTy.sym !== undefined) {
      const m = objTy.sym.methods.get(fe.name);
      if (m !== undefined) {
        if (m.decl !== undefined && m.decl.kind === "fn_decl") {
          this.checkArgsVsDecl(m.decl, x.args, x.line);
          const ret = m.decl.ret !== undefined ? this.resolveTypeEx(m.decl.ret, tparamsOf(m.decl.typeParams)) : Unknown;
          return ret;
        }
        return Unknown;
      }
      // 字段持有的函数值
      const fTy = this.fieldType(objTy, fe.name, fe.line);
      const args: Type[] = [];
      for (const a of x.args) args.push(this.infer(a));
      if (fTy.kind === "fn") {
        this.checkArgsVsFn(fTy, args, x.line);
        return fTy.ret ?? Unknown;
      }
      if (!isConcrete(fTy) || fTy.kind === "invalid") return Unknown;
      this.errf(x.line, E3004, `${fe.name} 不是方法也不是函数值字段`);
      return Invalid;
    }
    if (!isConcrete(objTy)) {
      for (const a of x.args) this.infer(a);
      return Unknown;
    }
    this.errf(x.line, E3004, `${typeStr(objTy)} 没有方法 ${fe.name}`);
    return Invalid;
  }

  /** 实参 ↔ 声明参数（个数 + 已注解参数的兼容性） */
  private checkArgsVsDecl(d: FnDecl, args: readonly Expr[], line: number): void {
    if (args.length !== d.params.length) {
      this.errf(line, E3003, `${d.name} 需要 ${d.params.length} 个参数，得到 ${args.length} 个`);
      for (const a of args) this.infer(a);
      return;
    }
    const tps = tparamsOf(d.typeParams);
    for (let i = 0; i < args.length; i++) {
      const at = this.infer(args[i]!);
      const anno = d.params[i]!.typ;
      if (anno === undefined) continue;
      const pt = this.resolveTypeEx(anno, tps);
      if (!assignable(pt, at)) {
        this.errf(args[i]!.line, E3001, `参数 ${d.params[i]!.name} 需要 ${typeStr(pt)}，得到 ${typeStr(at)}`);
      }
    }
  }

  private checkArgsVsFn(fnTy: Type, args: readonly Type[], line: number): void {
    if (args.length !== fnTy.params.length) {
      this.errf(line, E3003, `需要 ${fnTy.params.length} 个参数，得到 ${args.length} 个`);
      return;
    }
    for (let i = 0; i < args.length; i++) {
      if (!assignable(fnTy.params[i], args[i])) {
        this.errf(line, E3001, `第 ${i + 1} 个参数需要 ${typeStr(fnTy.params[i]!)}，得到 ${typeStr(args[i]!)}`);
      }
    }
  }

  // ———— 字面量与容器 ————

  private inferStructLit(x: Extract<Expr, { kind: "struct_lit" }>): Type {
    const sym = this.res.refs.get(x); // resolver 已绑 struct 符号
    if (sym === undefined) {
      for (const f of x.fields) this.infer(f.val);
      return Unknown;
    }
    if (x.fields.length !== sym.fields.length) {
      this.errf(x.line, E3001, `${sym.name} 需要 ${sym.fields.length} 个字段，得到 ${x.fields.length} 个`);
    }
    for (let i = 0; i < x.fields.length; i++) {
      const f = x.fields[i]!;
      const ft = this.infer(f.val);
      if (i < sym.fields.length) {
        if (f.name !== sym.fields[i]) {
          this.errf(x.line, E3001, `${sym.name} 第 ${i + 1} 个字段应为 ${sym.fields[i]}，得到 ${f.name}`);
        }
        if (i < sym.fieldTypes.length && sym.fieldTypes[i] !== undefined) {
          const anno = resolveAnno(this.res, sym.fieldTypes[i], sym.typeParams);
          if (!assignable(anno, ft)) {
            this.errf(f.val.line, E3001, `字段 ${f.name} 需要 ${typeStr(anno)}，得到 ${typeStr(ft)}`);
          }
        }
      }
    }
    return StructOf(sym);
  }

  private inferMapLit(x: Extract<Expr, { kind: "map_lit" }>): Type {
    let keyT: Type | undefined;
    let valT: Type | undefined;
    const mixed = (cur: Type | undefined, next: Type): Type => {
      if (cur === undefined) return next;
      if (equal(cur, next)) return cur;
      if (!isConcrete(next)) return cur;
      if (!isConcrete(cur)) return next;
      return Unknown; // 混型键/值：interp 合法，动态处理
    };
    for (const item of x.items) {
      keyT = mixed(keyT, this.infer(item.key));
      valT = mixed(valT, this.infer(item.val));
    }
    keyT = keyT ?? Unknown;
    valT = valT ?? Unknown;
    if (isConcrete(keyT) && keyT.kind !== "int" && keyT.kind !== "string" && keyT.kind !== "bool") {
      this.errf(x.line, E3014, `映射键须为 int/string/bool，得到 ${typeStr(keyT)}`);
      return MapOf(Unknown, valT);
    }
    return MapOf(keyT, valT);
  }

  private inferArrLit(x: Extract<Expr, { kind: "arr_lit" }>): Type {
    let elem: Type | undefined;
    for (const el of x.items) {
      const t = this.infer(el);
      if (elem === undefined) {
        elem = t;
        continue;
      }
      if (equal(elem, t)) continue;
      if (!isConcrete(t)) continue;
      if (!isConcrete(elem)) {
        elem = t;
        continue;
      }
      elem = Unknown; // 混型数组：interp 合法
    }
    return SliceOf(elem ?? Unknown);
  }

  // ———— match ————

  private inferMatch(x: MatchEx): Type {
    const scrut = this.infer(x.scrut);
    const [kind, ok] = this.classifyMatch(x);
    this.matchKind.set(x, kind);
    if (!ok) return Unknown; // 分类已报错（混用/字面量臂），不再叠加穷尽诊断

    if (kind === "enum") {
      this.checkEnumExhaustive(x);
    } else if (kind === "result" || kind === "option") {
      this.checkAlgebraicExhaustive(x);
    }

    // 臂绑定类型与臂体类型
    let body: Type | undefined;
    for (const arm of x.arms) {
      const bindTypes = this.armBindTypes(arm.pat, kind, scrut);
      // 臂绑定写臂级作用域（NAME-SCOPE-001）：臂内可见，臂结束销毁，不残留外层
      this.pushScope();
      for (const [name, t] of bindTypes) {
        this.defineVar(name, t);
      }
      const bt = this.infer(arm.body);
      this.popScope();
      if (body === undefined) {
        body = bt;
      } else if (!equal(body, bt)) {
        if (assignable(bt, body)) {
          // 臂体可归入已定类型 → 保持（如 [] 归入 [Ref]）
        } else if (assignable(body, bt)) {
          body = bt;
        } else if (isConcrete(body) && isConcrete(bt)) {
          this.errf(arm.line, E3001, `match 臂类型不一致：${typeStr(body)} 与 ${typeStr(bt)}`);
        } else if (!isConcrete(body)) {
          body = bt;
        }
      }
    }
    return body ?? Unknown;
  }

  /** 模式绑定的捕获类型 */
  private armBindTypes(p: Pat, kind: MatchKind, scrut: Type): Map<string, Type> {
    const out = new Map<string, Type>();
    const walk = (pt: Pat, t: Type): void => {
      switch (pt.kind) {
        case "bind_pat":
          out.set(pt.name, t);
          this.patTy.set(pt, t);
          return;
        case "case_pat": {
          const sym = this.res.patRefs.get(pt);
          if (sym === undefined) return;
          for (let i = 0; i < pt.subs.length; i++) {
            let subT: Type;
            if (sym.structName === "Result") {
              subT = sym.name === "Ok" ? ResultOk(scrut) : ErrorT;
            } else if (sym.structName === "Option") {
              subT = scrut.elem ?? Unknown;
            } else {
              const en = this.lookupTypeSym(sym.structName);
              if (en !== undefined && i < pt.subs.length) {
                subT = this.casePayloadType(en, sym.caseIndex, i);
              } else {
                subT = Unknown;
              }
            }
            walk(pt.subs[i]!, subT);
          }
          return;
        }
        case "lit_pat":
        case "wild_pat":
          return;
        default: {
          const _never: never = pt;
          return _never;
        }
      }
    };
    walk(p, Unknown);
    return out;
  }

  /** enum case 载荷类型 */
  private casePayloadType(enumSym: Symbol, caseIdx: number, payloadIdx: number): Type {
    const d = enumSym.decl;
    if (d === undefined || d.kind !== "enum_decl") return Unknown;
    if (caseIdx >= d.cases.length) return Unknown;
    const payloads = d.cases[caseIdx]!.payloads;
    if (payloadIdx >= payloads.length) return Unknown;
    const pt = payloads[payloadIdx]!;
    return this.resolveTypeEx(pt, tparamsOf(enumSym.typeParams));
  }

  /** match 分类（唯一判定点）。第二返回值 false 表示分类阶段已报错 */
  private classifyMatch(x: MatchEx): [MatchKind, boolean] {
    let hasCase = false;
    let hasLit = false;
    let resultCtor = false;
    let optionCtor = false;
    let enumName = "";
    let mixedEnum = false;
    for (const arm of x.arms) {
      const p = arm.pat;
      if (p.kind === "lit_pat") {
        hasLit = true;
      } else if (p.kind === "case_pat") {
        hasCase = true;
        const sym = this.res.patRefs.get(p);
        if (sym === undefined) continue;
        if (sym.structName === "Result") {
          resultCtor = true;
        } else if (sym.structName === "Option") {
          optionCtor = true;
        } else if (enumName === "") {
          enumName = sym.structName;
        } else if (enumName !== sym.structName) {
          mixedEnum = true;
        }
      }
    }
    if (resultCtor && optionCtor) {
      this.errf(x.line, E4003, "Result 与 Option 的模式混用");
      return ["literal", false];
    }
    if (resultCtor) {
      if (hasLit) {
        this.errf(x.line, E4002, "代数类型的 match 不能混入字面量模式");
        return ["result", false];
      }
      return ["result", true];
    }
    if (optionCtor) {
      if (hasLit) {
        this.errf(x.line, E4002, "代数类型的 match 不能混入字面量模式");
        return ["option", false];
      }
      return ["option", true];
    }
    if (hasCase) {
      if (hasLit) {
        this.errf(x.line, E4002, "enum 的 match 不能混入字面量模式");
        return ["enum", false];
      }
      if (mixedEnum) {
        this.errf(x.line, E4003, "match 混入了不同 enum 的构造器");
        return ["enum", false];
      }
      return ["enum", true];
    }
    return ["literal", true];
  }

  /** enum match 穷尽性（编译期） */
  private checkEnumExhaustive(x: MatchEx): void {
    const covered = new Set<string>();
    let hasWild = false;
    let enumSym: Symbol | undefined;
    for (const arm of x.arms) {
      const p = arm.pat;
      if (p.kind === "case_pat") {
        const sym = this.res.patRefs.get(p);
        if (sym === undefined) continue;
        if (enumSym === undefined) {
          const en = this.lookupTypeSym(sym.structName);
          if (en !== undefined) enumSym = en;
        }
        covered.add(p.name);
      } else if (p.kind === "wild_pat" || p.kind === "bind_pat") {
        hasWild = true;
      }
    }
    if (hasWild || enumSym === undefined) return;
    const d = enumSym.decl;
    if (d === undefined || d.kind !== "enum_decl") return;
    const missing: string[] = [];
    for (const cs of d.cases) {
      if (!covered.has(cs.name)) missing.push(cs.name);
    }
    if (missing.length > 0) {
      this.errf(x.line, E4001, `match 不穷尽，缺少: ${joinNames(missing)}`);
    }
  }

  /** Result/Option match 须两臂齐全 */
  private checkAlgebraicExhaustive(x: MatchEx): void {
    let pos = false;
    let neg = false;
    for (const arm of x.arms) {
      const p = arm.pat;
      if (p.kind === "case_pat") {
        if (p.name === "Ok" || p.name === "Some") pos = true;
        else if (p.name === "Err" || p.name === "None") neg = true;
      } else if (p.kind === "wild_pat") {
        pos = true;
        neg = true;
      }
    }
    if (!pos || !neg) {
      this.errf(x.line, E4001, "match 不穷尽：Result/Option 须覆盖两臂");
    }
  }

  // ———— ? 传播 ————

  /** 语句顶层 ? 的检查：所在函数须注解 Result，操作数须 Result */
  private inferTry(t: TryEx): Type {
    if (!this.inFn || this.fnRet === undefined || this.fnRet.kind !== "result") {
      this.errf(t.line, E3005, "带 ? 的函数须注解为 Result[T, error]");
    }
    return this.inferTryOperand(t);
  }

  private inferTryOperand(t: TryEx): Type {
    const op = this.infer(t.x);
    switch (op.kind) {
      case "result":
        return ResultOk(op);
      case "any":
      case "unknown":
      case "invalid":
        return Unknown;
      case "option":
        this.errf(t.line, E3008, `? 的操作数须为 Result，得到 ${typeStr(op)}（Option 不可用 ?）`);
        return Invalid;
      default:
        this.errf(t.line, E3008, `? 的操作数须为 Result，得到 ${typeStr(op)}`);
        return Invalid;
    }
  }

  // ———— 字段与索引 ————

  /** 引用自动解引用（&T 的字段/索引/方法访问与 interp 的 PtrVal 语义一致） */
  private deref(t: Type): Type {
    let cur = t;
    while (cur.kind === "ref") cur = cur.elem ?? Unknown;
    return cur;
  }

  /** 字段访问类型检查 */
  private fieldType(objTy: Type, name: string, line: number): Type {
    const ty = this.deref(objTy);
    if (ty.kind === "struct" && ty.sym !== undefined) {
      const sym = ty.sym;
      for (let i = 0; i < sym.fields.length; i++) {
        if (sym.fields[i] === name) {
          if (i < sym.fieldTypes.length && sym.fieldTypes[i] !== undefined) {
            return resolveAnno(this.res, sym.fieldTypes[i], sym.typeParams);
          }
          return Unknown;
        }
      }
      this.errf(line, E3004, `${symNameOf(sym)} 没有字段 ${name}`);
      return Invalid;
    }
    if (ty.kind === "error") {
      if (name === "msg") return String;
      this.errf(line, E3004, "error 只有 msg 字段");
      return Invalid;
    }
    if (!isConcrete(ty)) return Unknown;
    this.errf(line, E3004, `${typeStr(ty)} 没有字段 ${name}`);
    return Invalid;
  }

  /** 索引读取的元素类型 */
  private indexElemTy(objTy: Type, line: number): Type {
    const ty = this.deref(objTy);
    switch (ty.kind) {
      case "slice":
        return ty.elem ?? Unknown;
      case "map":
        return ty.elem ?? Unknown;
      case "string":
        return String;
      case "any":
      case "unknown":
      case "invalid":
        return Unknown;
      default:
        this.errf(line, E3001, `${typeStr(ty)} 不可索引`);
        return Invalid;
    }
  }

  /** 索引类型检查：容器须 int 下标；映射键限 int/string/bool */
  private checkIndexKey(objTy: Type, idxTy: Type, line: number): void {
    if (!isConcrete(idxTy)) return;
    const ty = this.deref(objTy);
    switch (ty.kind) {
      case "slice":
      case "string":
        if (idxTy.kind !== "int") {
          this.errf(line, E3013, `索引须为 int，得到 ${typeStr(idxTy)}`);
        }
        return;
      case "map":
        if (idxTy.kind !== "int" && idxTy.kind !== "string" && idxTy.kind !== "bool") {
          this.errf(line, E3014, `映射键须为 int/string/bool，得到 ${typeStr(idxTy)}`);
        }
        return;
      default:
        return;
    }
  }

  // ———— 函数字面量 ————

  private inferFnLit(x: Extract<Expr, { kind: "fn_lit" }>): Type {
    const savedRet = this.fnRet;
    const savedInFn = this.inFn;
    const savedScopes = this.scopes;
    this.scopes = [new Map()];
    this.fnRet = x.ret !== undefined ? this.resolveTypeEx(x.ret, tparamsOf(x.typeParams)) : undefined;
    this.inFn = true;
    const ps: Type[] = [];
    for (const p of x.params) {
      if (p.typ === undefined) {
        this.errf(x.line, E3007, `函数字面量参数 ${p.name} 缺少类型注解`);
        ps.push(Unknown);
        this.scopes[0]!.set(p.name, Unknown);
        continue;
      }
      const pt = this.resolveTypeEx(p.typ, tparamsOf(x.typeParams));
      ps.push(pt);
      this.scopes[0]!.set(p.name, pt);
    }
    for (const s of x.body) this.stmt(s);
    this.fnRet = savedRet;
    this.inFn = savedInFn;
    this.scopes = savedScopes;
    return FnOf(ps, x.ret !== undefined ? this.resolveTypeEx(x.ret, tparamsOf(x.typeParams)) : Unknown);
  }
}

function deref(t: Type): Type {
  let cur = t;
  while (cur.kind === "ref") cur = cur.elem ?? Unknown;
  return cur;
}

function symNameOf(s: Symbol): string {
  return s.name;
}

function tparamsOf(names: readonly string[]): Set<string> {
  return new Set(names);
}

/** 运行类型检查（res 需先通过 resolve） */
export function check(prog: readonly Stmt[], res: ResolveResult): CheckResult {
  return new Checker(res).check(prog);
}
