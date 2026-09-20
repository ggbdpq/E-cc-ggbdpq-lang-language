// Core IR（v0.3 Semantic Core 第四层）：HIR 降级而成的最小后端友好节点集。
// 三个执行后端（interpreter/→Go/→JS）优先消费 IR；后端只做目标平台映射。
// v0.3 不做 SSA：树形中间表示，「去元数据 + 局部槽位化 + 迭代协议显式化」。

import type { Symbol, SymbolId } from "../semantic/symbols.js";
import type { Type } from "../types/model.js";

export interface IrProg {
  readonly structs: readonly { readonly name: string; readonly typeParams: readonly string[]; readonly fields: readonly { readonly name: string; readonly typ: Type }[] }[];
  readonly enums: readonly { readonly name: string; readonly typeParams: readonly string[]; readonly cases: readonly { readonly name: string; readonly payloads: readonly (Type | undefined)[] }[] }[];
  readonly globals: readonly IrLet[];
  readonly fns: readonly IrFn[];
  readonly main: IrFn | undefined;
}

export interface IrLocal {
  readonly name: string;
  readonly mutable: boolean;
  readonly typ: Type;
  readonly sym: Symbol | undefined;
}

export interface IrFn {
  readonly name: string;
  readonly sym: Symbol | undefined;
  readonly recv: IrLocal | undefined;
  readonly typeParams: readonly string[];
  readonly params: readonly IrLocal[];
  /** 无注解（void）时为 undefined */
  readonly ret: Type | undefined;
  /** 函数体声明的局部（参数/接收者之外的），按声明序 */
  readonly locals: readonly IrLocal[];
  readonly body: readonly IrStmt[];
  readonly extern: boolean;
  readonly externHost: string;
}

// —— 语句 ——

export interface IrLet {
  readonly kind: "let";
  readonly local: IrLocal;
  readonly init: IrExpr;
}

export interface IrAssign {
  readonly kind: "assign";
  readonly target: IrExpr;
  readonly op: IrOp;
  readonly val: IrExpr;
}

export interface IrEval {
  readonly kind: "eval";
  readonly x: IrExpr;
}

export interface IrReturn {
  readonly kind: "return";
  readonly val: IrExpr | undefined;
}

export interface IrIf {
  readonly kind: "if";
  readonly cond: IrExpr;
  readonly then: readonly IrStmt[];
  readonly els: readonly IrStmt[];
}

export interface IrWhile {
  readonly kind: "while";
  readonly cond: IrExpr;
  readonly body: readonly IrStmt[];
}

export type IrIterKind = "unknown" | "slice" | "map" | "string" | "range";

export interface IrForIn {
  readonly kind: "for_in";
  readonly variable: IrLocal;
  readonly iterKind: IrIterKind;
  readonly iter: IrExpr;
  readonly body: readonly IrStmt[];
}

export interface IrBreak {
  readonly kind: "break";
}
export interface IrContinue {
  readonly kind: "continue";
}

export interface IrSpawn {
  readonly kind: "spawn";
  readonly fn: Symbol | undefined;
  readonly args: readonly IrExpr[];
}

export interface IrFnDef {
  readonly kind: "fn_def";
  readonly fn: IrFn;
}

export type IrStmt =
  | IrLet
  | IrAssign
  | IrEval
  | IrReturn
  | IrIf
  | IrWhile
  | IrForIn
  | IrBreak
  | IrContinue
  | IrSpawn
  | IrFnDef;

// —— 表达式 ——

export type IrOp =
  | "=" | "+" | "-" | "*" | "/" | "%"
  | "+=" | "-=" | "*=" | "/="
  | "==" | "!=" | "<" | "<=" | ">" | ">="
  | "&" | "|" | "^" | "<<" | ">>"
  | "&&" | "||"
  | "!";

export type IrConstKind = "int" | "float" | "str" | "bool";

export interface IrConst {
  readonly kind: "const";
  readonly constKind: IrConstKind;
  readonly i64: bigint;
  readonly f64: number;
  readonly s: string;
  readonly b: boolean;
  readonly typ: Type;
}

export interface IrLocalGet {
  readonly kind: "local_get";
  readonly local: IrLocal;
  readonly typ: Type;
}

export interface IrFnGet {
  readonly kind: "fn_get";
  readonly name: string;
  readonly sym: Symbol | undefined;
  readonly typ: Type;
}

export interface IrBuiltinGet {
  readonly kind: "builtin_get";
  readonly name: string;
  readonly typ: Type;
}

export interface IrCtor {
  readonly kind: "ctor";
  readonly sym: Symbol;
  readonly args: readonly IrExpr[];
  readonly typ: Type;
}

/** 三形态：用户 fn（fn）/内建（builtin）/方法（method+recv）/函数值（callee） */
export interface IrCall {
  readonly kind: "call";
  readonly fn: Symbol | undefined;
  readonly builtin: string | undefined;
  readonly method: Symbol | undefined;
  readonly recv: IrExpr | undefined;
  readonly callee: IrExpr | undefined;
  readonly args: readonly IrExpr[];
  readonly typ: Type;
}

export interface IrUnary {
  readonly kind: "unary";
  readonly op: IrOp;
  readonly x: IrExpr;
  readonly typ: Type;
}

export interface IrBinary {
  readonly kind: "binary";
  readonly op: IrOp;
  readonly l: IrExpr;
  readonly r: IrExpr;
  readonly typ: Type;
}

export interface IrRange {
  readonly kind: "range";
  readonly lo: IrExpr;
  readonly hi: IrExpr;
  readonly typ: Type;
}

export interface IrIndex {
  readonly kind: "index";
  readonly obj: IrExpr;
  readonly idx: IrExpr;
  readonly typ: Type;
}

export interface IrSlice {
  readonly kind: "slice";
  readonly obj: IrExpr;
  readonly lo: IrExpr;
  readonly hi: IrExpr;
  readonly typ: Type;
}

export interface IrField {
  readonly kind: "field";
  readonly obj: IrExpr;
  readonly name: string;
  readonly typ: Type;
}

export interface IrTry {
  readonly kind: "try";
  readonly x: IrExpr;
  readonly typ: Type;
}

export interface IrStructLit {
  readonly kind: "struct_lit";
  readonly struct: Symbol | undefined;
  readonly fields: readonly { readonly name: string; readonly val: IrExpr }[];
  readonly typ: Type;
}

export interface IrMapLit {
  readonly kind: "map_lit";
  readonly keys: readonly IrExpr[];
  readonly vals: readonly IrExpr[];
  readonly typ: Type;
}

export interface IrSliceLit {
  readonly kind: "slice_lit";
  readonly elems: readonly IrExpr[];
  readonly typ: Type;
}

export type IrMatchKind = "literal" | "enum" | "result" | "option";

export interface IrMatch {
  readonly kind: "match";
  readonly scrut: IrExpr;
  readonly matchKind: IrMatchKind;
  readonly arms: readonly { readonly pat: IrPat; readonly body: IrExpr }[];
  readonly typ: Type;
}

export interface IrFnLit {
  readonly kind: "fn_lit";
  readonly params: readonly IrLocal[];
  readonly ret: Type;
  readonly locals: readonly IrLocal[];
  readonly body: readonly IrStmt[];
  readonly typ: Type;
}

export type IrExpr =
  | IrConst
  | IrLocalGet
  | IrFnGet
  | IrBuiltinGet
  | IrCtor
  | IrCall
  | IrUnary
  | IrBinary
  | IrRange
  | IrIndex
  | IrSlice
  | IrField
  | IrTry
  | IrStructLit
  | IrMapLit
  | IrSliceLit
  | IrMatch
  | IrFnLit;

export interface IrWildPat {
  readonly kind: "wild_pat";
}
export interface IrBindPat {
  readonly kind: "bind_pat";
  readonly name: string;
  readonly typ: Type;
  /** 臂绑定的局部槽位（lowering 建立并注册进作用域） */
  readonly local: IrLocal;
}
export interface IrLitPat {
  readonly kind: "lit_pat";
  readonly val: IrExpr;
}
export interface IrCtorPat {
  readonly kind: "ctor_pat";
  readonly sym: Symbol | undefined;
  readonly subs: readonly IrPat[];
}

export type IrPat = IrWildPat | IrBindPat | IrLitPat | IrCtorPat;

/** fn/method 符号键（graph 用） */
export type IrSymKey = SymbolId;
