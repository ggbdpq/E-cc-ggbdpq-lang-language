// Typed HIR（v0.3 Semantic Core 第三层）：AST → 已解析、已标注类型的语义树。
// 每个节点携带 NodeID（编译内稳定）、源码行、ResolvedSymbol、ResolvedType。
//
// HIR 消除的语法糖：裸构造器引用 → Ctor(0 参)、obj.m(args) → MethodCall、
// go <调用> → Spawn。? 以 Try 节点表达（checker 已约束位置）。
// AST 负责保留用户写法；HIR 表达程序语义；后端不再回读 AST。

import type { AssignOp, UnaryOp, BinaryOp } from "../syntax/ast.js";
import type { Span } from "../syntax/span.js";
import type { Symbol, SymbolId } from "../semantic/symbols.js";
import type { MatchKind, CheckResult } from "../types/checker.js";
import type { ResolveResult } from "../semantic/resolver.js";
import { Unknown, type Type } from "../types/model.js";

declare const brand: unique symbol;
export type NodeId = number & { readonly [brand]: "NodeId" };

/** 节点元数据（Span 来自解析器盖章，HIR 保留供契约层输出） */
export interface HirNode {
  readonly id: NodeId;
  readonly line: number;
  readonly span: Span;
  readonly sym: Symbol | undefined;
  readonly typ: Type;
}

// —— 程序 ——

export interface HirProg {
  readonly structs: HirStructDecl[];
  readonly enums: HirEnumDecl[];
  readonly globals: HirLet[];
  /** 含方法（recv 非 undefined）与 extern */
  readonly fns: HirFn[];
  /** 顶层 main（可空） */
  readonly main: HirFn | undefined;
}

/** 函数/方法/extern 声明 */
export interface HirFn extends HirNode {
  readonly kind: "fn";
  readonly name: string;
  readonly recv: HirParam | undefined;
  readonly typeParams: readonly string[];
  readonly params: readonly HirParam[];
  /** 无注解（void）时为 undefined（Go 参考实现为 nil，决定 →Go 是否写返回类型） */
  readonly ret: Type | undefined;
  readonly body: readonly HirStmt[];
  readonly extern: boolean;
  readonly externHost: string;
}

export interface HirParam {
  readonly name: string;
  readonly typ: Type;
}

export interface HirStructDecl extends HirNode {
  readonly kind: "struct_decl";
  readonly name: string;
  readonly typeParams: readonly string[];
  readonly fields: readonly { readonly name: string; readonly typ: Type }[];
}

export interface HirEnumDecl extends HirNode {
  readonly kind: "enum_decl";
  readonly name: string;
  readonly typeParams: readonly string[];
  readonly cases: readonly { readonly sym: Symbol | undefined; readonly name: string; readonly payloads: readonly Type[] }[];
}

// —— 语句 ——

export interface HirLet extends HirNode {
  readonly kind: "let";
  readonly name: string;
  readonly mutable: boolean;
  readonly init: HirExpr;
}

export interface HirAssign extends HirNode {
  readonly kind: "assign";
  readonly target: HirExpr;
  readonly op: AssignOp;
  readonly rhs: HirExpr;
}

export interface HirEval extends HirNode {
  readonly kind: "eval";
  readonly x: HirExpr;
}

export interface HirReturn extends HirNode {
  readonly kind: "return";
  readonly val: HirExpr | undefined;
}

export interface HirIf extends HirNode {
  readonly kind: "if";
  readonly cond: HirExpr;
  readonly then: readonly HirStmt[];
  readonly els: readonly HirStmt[];
}

export interface HirWhile extends HirNode {
  readonly kind: "while";
  readonly cond: HirExpr;
  readonly body: readonly HirStmt[];
}

export interface HirForIn extends HirNode {
  readonly kind: "for_in";
  readonly variable: string;
  readonly varType: Type;
  readonly iter: HirExpr;
  readonly body: readonly HirStmt[];
}

export interface HirBreak extends HirNode {
  readonly kind: "break";
}
export interface HirContinue extends HirNode {
  readonly kind: "continue";
}

/** go <fn 调用>（目标已由 resolver 约束为顶层 fn） */
export interface HirSpawn extends HirNode {
  readonly kind: "spawn";
  readonly fn: Symbol | undefined;
  readonly args: readonly HirExpr[];
}

/** 函数体内嵌套 fn 声明 */
export interface HirFnStmt extends HirNode {
  readonly kind: "fn_stmt";
  readonly fn: HirFn;
}

export type HirStmt =
  | HirLet
  | HirAssign
  | HirEval
  | HirReturn
  | HirIf
  | HirWhile
  | HirForIn
  | HirBreak
  | HirContinue
  | HirSpawn
  | HirFnStmt;

// —— 表达式 ——

export type LitKind = "int" | "float" | "str" | "bool";

export interface HirLit extends HirNode {
  readonly kind: "lit";
  readonly litKind: LitKind;
  readonly i64: bigint;
  readonly f64: number;
  readonly s: string;
  readonly b: boolean;
}

/** 局部/全局绑定读取（写目标复用此节点） */
export interface HirLocalRef extends HirNode {
  readonly kind: "local_ref";
  readonly name: string;
}

export interface HirFnRef extends HirNode {
  readonly kind: "fn_ref";
  readonly name: string;
}

export interface HirBuiltinRef extends HirNode {
  readonly kind: "builtin_ref";
  readonly name: string;
}

/** 构造器调用（裸零参引用即空参调用） */
export interface HirCtor extends HirNode {
  readonly kind: "ctor";
  readonly ctorSym: Symbol;
  readonly args: readonly HirExpr[];
}

/** 用户 fn（fn 非 undefined）/ 内建（builtin 非空）/ 表达式 callee（callee 非 undefined） */
export interface HirCall extends HirNode {
  readonly kind: "call";
  readonly fn: Symbol | undefined;
  readonly builtin: string | undefined;
  readonly callee: HirExpr | undefined;
  readonly args: readonly HirExpr[];
}

/** 方法调用（脱糖自 obj.m(args)）或字段函数值调用（method 为 undefined） */
export interface HirMethodCall extends HirNode {
  readonly kind: "method_call";
  readonly recv: HirExpr;
  readonly method: Symbol | undefined;
  readonly field: string | undefined;
  readonly args: readonly HirExpr[];
  /** 方法名的 token 区间（rename 用；字段函数值调用为字段名区间） */
  readonly nameSp: Span;
}

export interface HirUnary extends HirNode {
  readonly kind: "unary";
  readonly op: UnaryOp;
  readonly x: HirExpr;
}

export interface HirBinary extends HirNode {
  readonly kind: "binary";
  readonly op: BinaryOp;
  readonly l: HirExpr;
  readonly r: HirExpr;
}

export interface HirAndThen extends HirNode {
  readonly kind: "and_then";
  readonly l: HirExpr;
  readonly r: HirExpr;
}

export interface HirOrElse extends HirNode {
  readonly kind: "or_else";
  readonly l: HirExpr;
  readonly r: HirExpr;
}

export interface HirRange extends HirNode {
  readonly kind: "range";
  readonly lo: HirExpr;
  readonly hi: HirExpr;
}

export interface HirIndex extends HirNode {
  readonly kind: "index";
  readonly obj: HirExpr;
  readonly idx: HirExpr;
}

export interface HirSlice extends HirNode {
  readonly kind: "slice";
  readonly obj: HirExpr;
  readonly lo: HirExpr;
  readonly hi: HirExpr;
}

export interface HirField extends HirNode {
  readonly kind: "field";
  readonly obj: HirExpr;
  readonly name: string;
}

export interface HirTry extends HirNode {
  readonly kind: "try";
  readonly x: HirExpr;
}

export interface HirStructLit extends HirNode {
  readonly kind: "struct_lit";
  readonly fields: readonly { readonly name: string; readonly val: HirExpr }[];
}

export interface HirMapLit extends HirNode {
  readonly kind: "map_lit";
  readonly keys: readonly HirExpr[];
  readonly vals: readonly HirExpr[];
}

export interface HirSliceLit extends HirNode {
  readonly kind: "slice_lit";
  readonly elems: readonly HirExpr[];
}

export interface HirMatch extends HirNode {
  readonly kind: "match";
  readonly scrut: HirExpr;
  readonly matchKind: MatchKind;
  readonly arms: readonly { readonly line: number; readonly pat: HirPat; readonly body: HirExpr }[];
}

export interface HirFnLit extends HirNode {
  readonly kind: "fn_lit";
  readonly params: readonly HirParam[];
  /** 与 Go 参考实现一致：FnLit.Ret 经 anno(nil) 恒为 Unknown（非 undefined） */
  readonly ret: Type;
  readonly body: readonly HirStmt[];
}

export type HirExpr =
  | HirLit
  | HirLocalRef
  | HirFnRef
  | HirBuiltinRef
  | HirCtor
  | HirCall
  | HirMethodCall
  | HirUnary
  | HirBinary
  | HirAndThen
  | HirOrElse
  | HirRange
  | HirIndex
  | HirSlice
  | HirField
  | HirTry
  | HirStructLit
  | HirMapLit
  | HirSliceLit
  | HirMatch
  | HirFnLit;

// —— 模式 ——

export interface HirWildPat {
  readonly kind: "wild_pat";
  readonly line: number;
}
export interface HirBindPat {
  readonly kind: "bind_pat";
  readonly line: number;
  readonly name: string;
  readonly typ: Type;
}
export interface HirLitPat {
  readonly kind: "lit_pat";
  readonly line: number;
  readonly val: HirExpr;
}
export interface HirCtorPat {
  readonly kind: "ctor_pat";
  readonly line: number;
  readonly ctor: Symbol | undefined;
  readonly subs: readonly HirPat[];
}

export type HirPat = HirWildPat | HirBindPat | HirLitPat | HirCtorPat;

/** 表达式语义类型 */
export const typ = (e: HirExpr | undefined): Type => (e === undefined ? Unknown : e.typ);

/** fn/method 符号标识（graph 用） */
export type SymbolKey = SymbolId;
