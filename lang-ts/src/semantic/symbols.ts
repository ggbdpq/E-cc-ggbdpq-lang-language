// 静态符号模型（v0.3 Semantic Core 第一层）。
// 每个符号拥有编译内稳定的 SymbolID（按声明/绑定顺序编号，含预载符号）。

import type { EnumDecl, FnDecl, StructDecl, TypeEx } from "../syntax/ast.js";

declare const brand: unique symbol;
export type SymbolId = number & { readonly [brand]: "SymbolId" };

export const symbolId = (n: number): SymbolId => n as SymbolId;

export type SymbolKind = "fn" | "method" | "var" | "param" | "struct" | "enum" | "ctor";

export interface Symbol {
  readonly id: SymbolId;
  readonly kind: SymbolKind;
  readonly name: string;
  readonly line: number;
  readonly mutable: boolean;

  /** 声明注解（fn 返回值 / let 注解） */
  readonly typeEx: TypeEx | undefined;
  /** 参数注解（可含 undefined） */
  readonly params: readonly (TypeEx | undefined)[];
  /** method：接收者 struct 名 */
  readonly recvName: string | undefined;
  readonly arity: number;
  readonly typeParams: readonly string[];

  /** ctor：所属 enum 名 */
  readonly structName: string;
  /** ctor：case 序号 */
  readonly caseIndex: number;
  /** ctor：载荷类型（可含 undefined；预载 arity-1 构造器为 [undefined]，与 Go 一致） */
  readonly payloads: readonly (TypeEx | undefined)[];

  /** struct：字段名（保序） */
  readonly fields: readonly string[];
  /** struct：字段类型（与 fields 对齐，可含 undefined） */
  readonly fieldTypes: readonly (TypeEx | undefined)[];
  /** struct：方法表 */
  readonly methods: ReadonlyMap<string, Symbol>;

  /** fn：extern 声明（无函数体） */
  readonly extern: boolean;

  /** 预载符号（内建函数/预载 struct/http/Some 等），非用户源码声明 */
  readonly builtinMark: boolean;
  /** 源 AST 声明节点（checker/HIR 复用；预载符号为 undefined） */
  readonly decl: FnDecl | StructDecl | EnumDecl | undefined;
}

export interface SymbolOpts {
  readonly typeEx?: TypeEx | undefined;
  readonly params?: readonly (TypeEx | undefined)[];
  readonly recvName?: string;
  readonly arity?: number;
  readonly typeParams?: readonly string[];
  readonly structName?: string;
  readonly caseIndex?: number;
  readonly payloads?: readonly (TypeEx | undefined)[];
  readonly fields?: readonly string[];
  readonly fieldTypes?: readonly (TypeEx | undefined)[];
  readonly extern?: boolean;
  readonly builtinMark?: boolean;
  readonly decl?: FnDecl | StructDecl | EnumDecl | undefined;
}

/** 符号可变实现（解析期组装 methods/decl；对外只暴露 readonly Symbol 视图） */
export class SymbolImpl implements Symbol {
  readonly methods = new Map<string, Symbol>();
  decl: FnDecl | StructDecl | EnumDecl | undefined;

  constructor(
    readonly id: SymbolId,
    readonly kind: SymbolKind,
    readonly name: string,
    readonly line: number,
    readonly mutable: boolean,
    readonly typeEx: TypeEx | undefined,
    readonly params: readonly (TypeEx | undefined)[],
    readonly recvName: string | undefined,
    readonly arity: number,
    readonly typeParams: readonly string[],
    readonly structName: string,
    readonly caseIndex: number,
    readonly payloads: readonly (TypeEx | undefined)[],
    readonly fields: readonly string[],
    readonly fieldTypes: readonly (TypeEx | undefined)[],
    readonly extern: boolean,
    readonly builtinMark: boolean,
  ) {}

  static of(kind: SymbolKind, name: string, line = 0, mutable = false, o: SymbolOpts = {}): SymbolImpl {
    const sym = new SymbolImpl(
      symbolId(0),
      kind,
      name,
      line,
      mutable,
      o.typeEx,
      o.params ?? [],
      o.recvName,
      o.arity ?? 0,
      o.typeParams ?? [],
      o.structName ?? "",
      o.caseIndex ?? 0,
      o.payloads ?? [],
      o.fields ?? [],
      o.fieldTypes ?? [],
      o.extern ?? false,
      o.builtinMark ?? false,
    );
    if (o.decl !== undefined) sym.decl = o.decl;
    return sym;
  }
}
/** 构造器（枚举 case）判定 */
export const isCtor = (s: Symbol): boolean => s.kind === "ctor";

/** 符号类别诊断名（与 Go kindName 一致） */
export function kindName(k: SymbolKind): string {
  switch (k) {
    case "fn":
      return "fn";
    case "method":
      return "方法";
    case "var":
      return "绑定";
    case "param":
      return "参数";
    case "struct":
      return "struct";
    case "enum":
      return "enum";
    case "ctor":
      return "构造器";
    default: {
      const _never: never = k;
      return _never;
    }
  }
}
