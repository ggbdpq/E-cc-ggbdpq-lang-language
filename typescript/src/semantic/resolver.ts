// 名字解析（NAME-SCOPE/SHADOW/DUP/UNDEF/CTOR/RECV/MUT/PLACE/NEST/MERGE）。
// 多遍扫描：先收集顶层声明，再做作用域绑定；查名顺序与解释器运行时一致：
// 作用域栈 → 构造器 → 内建。诊断消息按 Go 参考实现逐字复刻。
//
// 四遍结构：全局绑定依序解析（与 interp 顺序执行、→Go var 初始化的顺序语义一致，
// 前向引用全局即 E2002）；fn/方法体最后走——函数体对全局的可见性与声明顺序无关。

import type { EnumDecl, Expr, FnDecl, Pat, Stmt, StructDecl, TypeEx } from "../syntax/ast.js";
import {
  E2001, E2002, E2003, E2004, E2005, E2006, E2007, E2008, E2010,
  type Diagnostic,
} from "../common/diagnostic.js";
import type { Symbol, SymbolId, SymbolKind } from "./symbols.js";
import { SymbolImpl, kindName, symbolId } from "./symbols.js";

/** 内建函数名单（与 interp/gen/genjs 三方现行实现一致的唯一清单） */
export const builtinNames: readonly string[] = [
  "println", "print", "len", "str", "int", "float", "keys", "values",
  "has", "push", "assert", "err", "bytes", "char",
  "jsonParse", "jsonStringify", "isNone", "makeChan", "send", "recv",
];

/** 预载 struct（interp prelude 的 error/HttpResp） */
const preludeStructNames: readonly string[] = ["error", "HttpResp"];

/** 预载构造器（与 interp prelude 的 case 序号一致） */
const preludeCtors: readonly { name: string; enumName: string; idx: number; arity: number }[] = [
  { name: "Some", enumName: "Option", idx: 0, arity: 1 },
  { name: "None", enumName: "Option", idx: 1, arity: 0 },
  { name: "Ok", enumName: "Result", idx: 0, arity: 1 },
  { name: "Err", enumName: "Result", idx: 1, arity: 1 },
];

export interface ResolveResult {
  readonly symbols: readonly Symbol[];
  /** Ident/StructLit 引用 → 符号（按 AST 节点对象同一）；未解析的 Ident 不入表 */
  readonly refs: Map<Expr, Symbol>;
  /** CasePat/BindPat → 符号 */
  readonly patRefs: Map<Pat, Symbol>;
  /** LetStmt/ForInStmt → 绑定符号；FnDecl → fn 符号 */
  readonly declSyms: Map<Stmt, Symbol>;
  /** fn/method 符号 → 接收者+参数符号（按序，跳过 "_"） */
  readonly fnParams: Map<SymbolId, Symbol[]>;
  /** 预载符号（内建函数/预载 struct/http） */
  readonly builtin: Map<string, Symbol>;
  readonly diags: Diagnostic[];
}

export const hasErrors = (res: ResolveResult): boolean => res.diags.length > 0;

function ownerName(s: Symbol): string {
  return s.builtinMark ? "内建" : s.structName;
}

class Resolver {
  private nextId = 0;
  private readonly symbols: SymbolImpl[] = [];
  private readonly diags: Diagnostic[] = [];
  private readonly refs = new Map<Expr, Symbol>();
  private readonly patRefs = new Map<Pat, Symbol>();
  private readonly declSyms = new Map<Stmt, Symbol>();
  private readonly fnParams = new Map<SymbolId, Symbol[]>();
  private readonly builtin = new Map<string, Symbol>();
  /** 值命名空间：fn/绑定/参数 */
  private scopes: Map<string, Symbol>[] = [];
  /** 类型命名空间：struct/enum（与值空间分离，struct 名不可作值） */
  private typeScopes: Map<string, Symbol>[] = [];
  /** 构造器全局命名空间（含预载） */
  private readonly ctorScope = new Map<string, Symbol>();
  private loopDepth = 0;
  private fnDepth = 0;

  private newSym(s: SymbolImpl): SymbolImpl {
    this.nextId++;
    // id 可变仅限此赋值点（编号即创建序）
    (s as { id: SymbolId }).id = symbolId(this.nextId);
    this.symbols.push(s);
    return s;
  }

  private err(line: number, code: string, msg: string): void {
    this.diags.push({ code, msg, line });
  }

  private pushScope(): void {
    this.scopes.push(new Map());
    this.typeScopes.push(new Map());
  }

  private popScope(): void {
    this.scopes.pop();
    this.typeScopes.pop();
  }

  private define(s: SymbolImpl): void {
    const top = this.scopes[this.scopes.length - 1]!;
    if (top.has(s.name)) {
      this.err(s.line, E2001, `${kindName(s.kind)} ${s.name} 在此作用域重复定义`);
      return;
    }
    top.set(s.name, s);
  }

  private defineCtor(s: SymbolImpl): void {
    const old = this.ctorScope.get(s.name);
    if (old !== undefined) {
      this.err(s.line, E2003, `构造器 ${s.name} 重名（已被 ${ownerName(old)} 使用）`);
      return;
    }
    this.ctorScope.set(s.name, s);
  }

  private lookup(name: string): Symbol | undefined {
    for (let i = this.scopes.length - 1; i >= 0; i--) {
      const s = this.scopes[i]!.get(name);
      if (s !== undefined) return s;
    }
    return undefined;
  }

  private lookupCtor(name: string): Symbol | undefined {
    return this.ctorScope.get(name);
  }

  private lookupTop(name: string): Symbol | undefined {
    return this.scopes[0]!.get(name);
  }

  /** 类型命名空间查名（struct/enum；供 checker 解析注解） */
  private lookupType(name: string): Symbol | undefined {
    for (let i = this.typeScopes.length - 1; i >= 0; i--) {
      const s = this.typeScopes[i]!.get(name);
      if (s !== undefined) return s;
    }
    return undefined;
  }

  // ———— 预载 ————

  private prelude(): void {
    for (const n of builtinNames) {
      this.builtin.set(n, this.newSym(SymbolImpl.of("fn", n, 0, false, { builtinMark: true })));
    }
    for (const n of preludeStructNames) {
      this.builtin.set(n, this.newSym(SymbolImpl.of("struct", n, 0, false, { builtinMark: true })));
    }
    this.builtin.set("http", this.newSym(SymbolImpl.of("var", "http", 0, false, { builtinMark: true })));
    for (const c of preludeCtors) {
      this.ctorScope.set(
        c.name,
        this.newSym(
          SymbolImpl.of("ctor", c.name, 0, false, {
            structName: c.enumName,
            caseIndex: c.idx,
            arity: c.arity,
            // 与 Go prelude 一致：arity-1 载荷为含一个 nil 的切片，arity-0 为空
            payloads: c.arity === 1 ? [undefined] : [],
            builtinMark: true,
          }),
        ),
      );
    }
  }

  // ———— 第一遍：收集 struct/enum ————

  private collectTypes(prog: readonly Stmt[]): void {
    for (const s of prog) {
      if (s.kind === "struct_decl") {
        if (this.typeScopes[0]!.has(s.name)) {
          this.err(s.line, E2001, `struct ${s.name} 重复定义`);
          continue;
        }
        const sym = this.newSym(
          SymbolImpl.of("struct", s.name, s.line, false, {
            typeParams: s.typeParams,
            fields: s.fields.map((f) => f.name),
            fieldTypes: s.fields.map((f) => f.typ),
            decl: s,
          }),
        );
        this.typeScopes[0]!.set(s.name, sym);
      } else if (s.kind === "enum_decl") {
        if (this.typeScopes[0]!.has(s.name)) {
          this.err(s.line, E2001, `enum ${s.name} 重复定义`);
          continue;
        }
        const sym = this.newSym(
          SymbolImpl.of("enum", s.name, s.line, false, { typeParams: s.typeParams, decl: s }),
        );
        this.typeScopes[0]!.set(s.name, sym);
        for (let i = 0; i < s.cases.length; i++) {
          const c = s.cases[i]!;
          if (this.builtin.has(c.name)) {
            this.err(c.line, E2006, `case ${c.name} 与内建名冲突`);
            continue;
          }
          const cs = this.newSym(
            SymbolImpl.of("ctor", c.name, c.line, false, {
              structName: s.name,
              caseIndex: i,
              arity: c.payloads.length,
              payloads: [...c.payloads],
            }),
          );
          this.defineCtor(cs);
        }
      }
    }
  }

  // ———— 第二遍：收集 fn/extern/method 符号（体在第四遍走） ————

  private fnSymbol(d: FnDecl, extern = false): SymbolImpl {
    return this.newSym(
      SymbolImpl.of("fn", d.name, d.line, false, {
        typeEx: d.ret,
        params: d.params.map((p) => p.typ),
        arity: d.params.length,
        typeParams: d.typeParams,
        extern,
        decl: d,
      }),
    );
  }

  private collectFns(prog: readonly Stmt[]): void {
    for (const s of prog) {
      if (s.kind === "fn_decl") {
        if (s.recv !== undefined) {
          this.collectMethod(s);
          continue;
        }
        const old = this.lookupTop(s.name);
        if (old !== undefined) {
          this.err(s.line, E2001, `fn ${s.name} 重复定义（先前定义于第 ${old.line} 行）`);
          continue;
        }
        this.scopes[0]!.set(s.name, this.fnSymbol(s));
      } else if (s.kind === "extern") {
        for (const fn of s.fns) {
          const old = this.lookupTop(fn.name);
          if (old !== undefined) {
            this.err(s.line, E2001, `fn ${fn.name} 重复定义（先前定义于第 ${old.line} 行）`);
            continue;
          }
          const sym = this.fnSymbol(fn, true);
          this.scopes[0]!.set(fn.name, sym);
        }
      }
    }
  }

  private collectMethod(d: FnDecl): void {
    if (d.recv === undefined) return;
    const rt = d.recv.typ;
    if (rt === undefined || rt.kind !== "named_type") {
      this.err(d.line, E2004, "方法接收者须为具名 struct 类型");
      return;
    }
    const st = this.typeScopes[0]!.get(rt.name);
    if (st === undefined || st.kind !== "struct") {
      this.err(d.line, E2004, `为未知 struct ${rt.name} 定义方法`);
      return;
    }
    if (st.methods.has(d.name)) {
      this.err(d.line, E2001, `方法 ${d.name} 在 struct ${rt.name} 上重复定义`);
      return;
    }
    const sym = this.newSym(
      SymbolImpl.of("method", d.name, d.line, false, {
        typeEx: d.ret,
        params: d.params.map((p) => p.typ),
        recvName: rt.name,
        arity: d.params.length,
      }),
    );
    (st as SymbolImpl).methods.set(d.name, sym);
  }

  // ———— 第三遍：顶层绑定依序解析 ————

  private bindGlobals(prog: readonly Stmt[]): void {
    for (const s of prog) {
      if (s.kind === "let_stmt") {
        this.expr(s.init);
        if (s.name === "_") continue;
        const old = this.lookupTop(s.name);
        if (old !== undefined) {
          this.err(s.line, E2001, `绑定 ${s.name} 在此作用域重复定义（先前定义于第 ${old.line} 行）`);
          continue;
        }
        const sym = this.newSym(SymbolImpl.of("var", s.name, s.line, s.mutable, { typeEx: s.typ }));
        this.scopes[0]!.set(s.name, sym);
        this.declSyms.set(s, sym);
      } else if (
        s.kind === "fn_decl" || s.kind === "struct_decl" || s.kind === "enum_decl" ||
        s.kind === "extern" || s.kind === "import"
      ) {
        // 声明在其余遍处理
      } else {
        this.stmt(s);
      }
    }
  }

  // ———— 第四遍：函数体 ————

  private bindFns(prog: readonly Stmt[]): void {
    for (const s of prog) {
      if (s.kind === "fn_decl" && s.recv === undefined) {
        const sym = this.scopes[0]!.get(s.name);
        if (sym !== undefined) this.declSyms.set(s, sym);
        this.fnScope(s, sym);
      }
    }
    for (const s of prog) {
      if (s.kind !== "fn_decl" || s.recv === undefined) continue;
      const rt = s.recv.typ;
      if (rt !== undefined && rt.kind === "named_type") {
        const st = this.typeScopes[0]!.get(rt.name);
        if (st !== undefined && st.kind === "struct") {
          const sym = st.methods.get(s.name);
          if (sym !== undefined) {
            (sym as { decl: FnDecl | StructDecl | EnumDecl | undefined }).decl = s;
            this.fnScope(s, sym);
          }
        }
      }
    }
  }

  private stmt(s: Stmt): void {
    switch (s.kind) {
      case "let_stmt": {
        this.expr(s.init);
        if (s.name === "_") return;
        const sym = this.newSym(SymbolImpl.of("var", s.name, s.line, s.mutable, { typeEx: s.typ }));
        this.define(sym);
        this.declSyms.set(s, sym);
        return;
      }

      case "assign": {
        if (s.target.kind === "ident") {
          const id = s.target;
          const sym = this.lookup(id.name);
          if (sym === undefined) {
            this.err(id.line, E2002, `未定义变量 ${id.name}`);
          } else if (!sym.mutable) {
            this.err(s.line, E2005, `${id.name} 由 let 绑定，不可重新赋值`);
          } else {
            this.refs.set(id, sym);
          }
        } else {
          this.assignTarget(s.target);
        }
        this.expr(s.val);
        return;
      }

      case "expr_stmt":
        this.expr(s.x);
        return;

      case "return":
        if (this.fnDepth === 0) this.err(s.line, E2008, "return 出现在函数之外");
        if (s.val !== undefined) this.expr(s.val);
        return;

      case "if":
        this.expr(s.cond);
        this.block(s.then);
        if (s.els !== undefined) this.block(s.els);
        return;

      case "while":
        this.expr(s.cond);
        this.loopDepth++;
        this.block(s.body);
        this.loopDepth--;
        return;

      case "for_in": {
        this.expr(s.iter);
        this.pushScope();
        if (s.variable !== "_") {
          const sym = this.newSym(SymbolImpl.of("var", s.variable, s.line));
          this.define(sym);
          this.declSyms.set(s, sym);
        }
        this.loopDepth++;
        for (const b of s.body) this.stmt(b);
        this.loopDepth--;
        this.popScope();
        return;
      }

      case "break":
        if (this.loopDepth === 0) this.err(s.line, E2007, "break 出现在循环之外");
        return;
      case "continue":
        if (this.loopDepth === 0) this.err(s.line, E2007, "continue 出现在循环之外");
        return;

      case "fn_decl": {
        if (s.recv !== undefined) return; // 方法体在 bindFns 统一走
        // 嵌套 fn：定义进当前作用域
        const sym = this.fnSymbol(s);
        this.define(sym);
        this.declSyms.set(s, sym);
        this.fnScope(s, sym);
        return;
      }

      case "struct_decl": {
        // 嵌套类型声明：E2010（interp 容忍属实现越界，转译目标无法表达）
        this.err(s.line, E2010, `嵌套类型声明不支持（struct ${s.name} 须在顶层声明）`);
        const top = this.typeScopes[this.typeScopes.length - 1]!;
        if (!top.has(s.name)) {
          const sym = this.newSym(
            SymbolImpl.of("struct", s.name, s.line, false, {
              typeParams: s.typeParams,
              fields: s.fields.map((f) => f.name),
              fieldTypes: s.fields.map((f) => f.typ),
            }),
          );
          top.set(s.name, sym);
        }
        return;
      }

      case "enum_decl": {
        this.err(s.line, E2010, `嵌套类型声明不支持（enum ${s.name} 须在顶层声明）`);
        const top = this.typeScopes[this.typeScopes.length - 1]!;
        if (!top.has(s.name)) {
          this.newSym(SymbolImpl.of("enum", s.name, s.line, false, { typeParams: s.typeParams }));
        }
        return;
      }

      case "extern":
        for (const fn of s.fns) {
          if (this.lookup(fn.name) === undefined) {
            const sym = this.fnSymbol(fn, true);
            this.define(sym);
          }
        }
        return;

      case "go": {
        const id = s.call.callee;
        if (id.kind !== "ident") {
          this.err(s.line, E2002, "go 的目标须是具名函数调用");
        } else {
          const sym = this.lookup(id.name);
          if (sym === undefined || sym.kind !== "fn") {
            this.err(id.line, E2002, `go 的目标须是顶层函数：${id.name}`);
          } else {
            this.refs.set(id, sym);
          }
        }
        for (const a of s.call.args) this.expr(a);
        return;
      }

      case "import":
        // 装载器已展开
        return;

      default: {
        const _never: never = s;
        return _never;
      }
    }
  }

  private block(stmts: readonly Stmt[]): void {
    this.pushScope();
    for (const s of stmts) this.stmt(s);
    this.popScope();
  }

  /** 函数体作用域：接收者 + 参数，fnDepth 保护 return。
   *  参数符号按序记入 fnParams[fn 符号]（接收者在前），供 checker/HIR 对位取类型。 */
  private fnScope(d: FnDecl, sym: Symbol | undefined): void {
    this.pushScope();
    const psyms: Symbol[] = [];
    if (d.recv !== undefined) {
      const ps = this.newSym(SymbolImpl.of("param", d.recv.name, d.line, false, { typeEx: d.recv.typ }));
      this.define(ps);
      psyms.push(ps);
    }
    for (const p of d.params) {
      if (p.name === "_") continue;
      const ps = this.newSym(SymbolImpl.of("param", p.name, d.line, false, { typeEx: p.typ }));
      this.define(ps);
      psyms.push(ps);
    }
    if (sym !== undefined) this.fnParams.set(sym.id, psyms);
    this.fnDepth++;
    for (const s of d.body) this.stmt(s);
    this.fnDepth--;
    this.popScope();
  }

  /** 字段/索引赋值目标：只走子表达式（形状合法性由解析器 checkTarget 保证） */
  private assignTarget(t: Expr): void {
    switch (t.kind) {
      case "field":
        this.expr(t.obj);
        return;
      case "index":
        this.expr(t.obj);
        this.expr(t.idx);
        return;
      default:
        this.expr(t);
        return;
    }
  }

  private expr(e: Expr): void {
    switch (e.kind) {
      case "ident": {
        const sym = this.lookup(e.name);
        if (sym !== undefined) {
          this.refs.set(e, sym);
          return;
        }
        const ctor = this.lookupCtor(e.name);
        if (ctor !== undefined) {
          this.refs.set(e, ctor);
          return;
        }
        if (this.builtin.has(e.name)) return; // 裸内建引用是否合法由 checker 判定
        this.err(e.line, E2002, `未定义 ${e.name}`);
        return;
      }

      case "unary":
        this.expr(e.x);
        return;
      case "binary":
        this.expr(e.l);
        this.expr(e.r);
        return;
      case "range":
        this.expr(e.lo);
        this.expr(e.hi);
        return;
      case "call":
        this.expr(e.callee);
        for (const a of e.args) this.expr(a);
        return;
      case "index":
        this.expr(e.obj);
        this.expr(e.idx);
        return;
      case "slice":
        this.expr(e.obj);
        this.expr(e.lo);
        this.expr(e.hi);
        return;
      case "field":
        this.expr(e.obj);
        return;
      case "try":
        this.expr(e.x);
        return;

      case "struct_lit": {
        const sym = this.lookupType(e.typeName);
        if (sym !== undefined) {
          this.refs.set(e, sym);
        } else {
          this.err(e.line, E2002, `未知 struct ${e.typeName}`);
        }
        for (const f of e.fields) this.expr(f.val);
        return;
      }

      case "map_lit":
        for (const item of e.items) {
          this.expr(item.key);
          this.expr(item.val);
        }
        return;
      case "arr_lit":
        for (const el of e.items) this.expr(el);
        return;

      case "match":
        this.expr(e.scrut);
        for (const arm of e.arms) {
          this.pushScope();
          this.pat(arm.pat);
          this.expr(arm.body);
          this.popScope();
        }
        return;

      case "fn_lit": {
        this.pushScope();
        for (const p of e.params) {
          if (p.name !== "_") {
            this.define(this.newSym(SymbolImpl.of("param", p.name, e.line, false, { typeEx: p.typ })));
          }
        }
        this.fnDepth++;
        for (const s of e.body) this.stmt(s);
        this.fnDepth--;
        this.popScope();
        return;
      }

      case "int_lit":
      case "float_lit":
      case "str_lit":
      case "bool_lit":
        return;

      default: {
        const _never: never = e;
        return _never;
      }
    }
  }

  private pat(p: Pat): void {
    switch (p.kind) {
      case "wild_pat":
        return;
      case "bind_pat": {
        const sym = this.newSym(SymbolImpl.of("var", p.name, p.line));
        this.define(sym);
        this.patRefs.set(p, sym);
        return;
      }
      case "lit_pat":
        this.expr(p.val);
        return;
      case "case_pat": {
        const sym = this.lookupCtor(p.name);
        if (sym === undefined) {
          this.err(p.line, E2002, `未知构造器 ${p.name}`);
        } else {
          this.patRefs.set(p, sym);
        }
        for (const sp of p.subs) this.pat(sp);
        return;
      }
      default: {
        const _never: never = p;
        return _never;
      }
    }
  }

  resolve(prog: readonly Stmt[]): ResolveResult {
    this.prelude();
    this.scopes = [new Map()];
    this.typeScopes = [new Map()];
    this.collectTypes(prog);
    this.collectFns(prog);
    this.bindGlobals(prog);
    this.bindFns(prog);
    this.popScope();
    return {
      symbols: this.symbols,
      refs: this.refs,
      patRefs: this.patRefs,
      declSyms: this.declSyms,
      fnParams: this.fnParams,
      builtin: this.builtin,
      diags: this.diags,
    };
  }
}

/** 解析整个程序（装载器合并后的语句序列）。有诊断时仍返回部分产物，调用方以 hasErrors 判定。 */
export function resolve(prog: readonly Stmt[]): ResolveResult {
  return new Resolver().resolve(prog);
}

export type { TypeEx };
