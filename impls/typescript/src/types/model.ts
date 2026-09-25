// 语义类型模型。统一 Type + 推断 + 兼容性判定。
// 保守性铁律：任一侧为 Any/Unknown 时不报错（Any 来自 jsonParse/extern 等动态边界，
// Unknown 来自无注解参数——保证现有合法程序零误杀）。

import type { Symbol } from "../semantic/symbols.js";

export type TypeKind =
  | "invalid" // 错误传播占位（已有诊断，下游静默）
  | "int"
  | "float"
  | "bool"
  | "string"
  | "error" // error struct
  | "unit" // ()
  | "range" // lo..hi
  | "any" // 动态值（jsonParse/extern 边界）
  | "unknown" // 无注解推断不出
  | "never"
  | "slice" // [T]
  | "map" // map[K]V
  | "ref" // &T
  | "chan" // chan[T]
  | "fn" // fn(...): ...
  | "struct" // 名义：按符号同一
  | "enum" // 名义：按符号同一
  | "option" // Option[T]
  | "result" // Result[T, E]
  | "type_arg"; // 泛型类型参数（如 fn[T] 里的 T）

export interface Type {
  readonly kind: TypeKind;
  /** slice/ref/chan/option → elem；map → elem（值侧） */
  readonly elem: Type | undefined;
  /** map → key */
  readonly key: Type | undefined;
  /** fn → params；result → params[0]=T */
  readonly params: readonly Type[];
  /** fn/result → ret（result 的 ret=E 侧） */
  readonly ret: Type | undefined;
  /** struct/enum/type_arg → 符号 */
  readonly sym: Symbol | undefined;
  /** type_arg：泛型参数名 */
  readonly name: string;
}

const mk = (kind: TypeKind, o: Partial<Omit<Type, "kind">> = {}): Type => ({
  kind,
  elem: o.elem,
  key: o.key,
  params: o.params ?? [],
  ret: o.ret,
  sym: o.sym,
  name: o.name ?? "",
});

// 预载原始类型单例（同一性即类型同一）
export const Int: Type = mk("int");
export const Float: Type = mk("float");
export const Bool: Type = mk("bool");
export const String: Type = mk("string");
export const ErrorT: Type = mk("error");
export const Unit: Type = mk("unit");
export const Range: Type = mk("range");
export const Any: Type = mk("any");
export const Unknown: Type = mk("unknown");
export const Never: Type = mk("never");
export const Invalid: Type = mk("invalid");

export const SliceOf = (elem: Type): Type => mk("slice", { elem });
export const MapOf = (key: Type, val: Type): Type => mk("map", { key, elem: val });
export const RefOf = (elem: Type): Type => mk("ref", { elem });
export const ChanOf = (elem: Type): Type => mk("chan", { elem });
export const OptionOf = (elem: Type): Type => mk("option", { elem });
export const ResultOf = (ok: Type, err: Type): Type => mk("result", { params: [ok], ret: err });
export const FnOf = (params: readonly Type[], ret: Type): Type => mk("fn", { params, ret });
export const StructOf = (sym: Symbol): Type => mk("struct", { sym });
export const EnumOf = (sym: Symbol): Type => mk("enum", { sym });
export const TypeArgOf = (sym: Symbol, name: string): Type => mk("type_arg", { sym, name });

/** Result 的 T / E 侧 */
export const ResultOk = (t: Type): Type =>
  t.kind === "result" && t.params.length === 1 ? t.params[0]! : Unknown;
export const ResultErr = (t: Type): Type => (t.kind === "result" && t.ret !== undefined ? t.ret : Unknown);

/** 结构同一（struct/enum 按符号同一——名义类型；type_arg 为擦除占位，彼此同一） */
export function equal(a: Type | undefined, b: Type | undefined): boolean {
  if (a === b) return true;
  if (a === undefined || b === undefined || a.kind !== b.kind) return false;
  switch (a.kind) {
    case "slice":
    case "ref":
    case "chan":
    case "option":
      return equal(a.elem, b.elem);
    case "map":
      return equal(a.key, b.key) && equal(a.elem, b.elem);
    case "fn": {
      if (a.params.length !== b.params.length) return false;
      for (let i = 0; i < a.params.length; i++) {
        if (!equal(a.params[i], b.params[i])) return false;
      }
      return equal(a.ret, b.ret);
    }
    case "result":
      return equal(ResultOk(a), ResultOk(b)) && equal(ResultErr(a), ResultErr(b));
    case "struct":
    case "enum":
      return a.sym !== undefined && a.sym === b.sym;
    case "type_arg":
      return true; // 泛型参数擦除：所有 type_arg 互为同一
    default:
      return true; // 原始类型
  }
}

/** 宽松类型：参与即放行（动态边界/推断缺口/泛型擦除/错误传播） */
const laxAny = (t: Type): boolean =>
  t.kind === "any" || t.kind === "unknown" || t.kind === "invalid" || t.kind === "type_arg";

/** 赋值兼容：宽松类型双向放行；容器与代数类型按元素协变（运行时容器是擦除的）；
 *  fn 按参数逆协变 + 返回协变；struct/enum 名义同一。 */
export function assignable(to: Type | undefined, from: Type | undefined): boolean {
  if (to === undefined || from === undefined) return true;
  if (laxAny(to) || laxAny(from)) return true;
  if (to.kind !== from.kind) return false;
  switch (to.kind) {
    case "slice":
    case "ref":
    case "chan":
    case "option":
      return assignable(to.elem, from.elem);
    case "map":
      return assignable(to.key, from.key) && assignable(to.elem, from.elem);
    case "result":
      return assignable(ResultOk(to), ResultOk(from)) && assignable(ResultErr(to), ResultErr(from));
    case "fn": {
      if (to.params.length !== from.params.length) return false;
      for (let i = 0; i < to.params.length; i++) {
        if (!assignable(from.params[i], to.params[i])) return false; // 参数逆协变
      }
      return assignable(to.ret, from.ret);
    }
    default:
      return equal(to, from);
  }
}

/** 是否数值类型 */
export const isNumeric = (t: Type): boolean => t.kind === "int" || t.kind === "float";

/** 是否具体类型（非 any/unknown/invalid） */
export const isConcrete = (t: Type | undefined): boolean =>
  t !== undefined && t.kind !== "any" && t.kind !== "unknown" && t.kind !== "invalid";

/** 诊断与 dump 用类型名（贴近源码写法；与 Go Type.String() 逐字一致） */
export function typeStr(t: Type | undefined): string {
  if (t === undefined) return "unknown";
  const k: TypeKind = t.kind;
  switch (k) {
    case "invalid":
      return "invalid";
    case "int":
      return "int";
    case "float":
      return "float";
    case "bool":
      return "bool";
    case "string":
      return "string";
    case "error":
      return "error";
    case "unit":
      return "()";
    case "range":
      return "range";
    case "any":
      return "any";
    case "unknown":
      return "unknown";
    case "never":
      return "never";
    case "slice":
      return `[${typeStr(t.elem)}]`;
    case "map":
      return `map[${typeStr(t.key)}]${typeStr(t.elem)}`;
    case "ref":
      return `&${typeStr(t.elem)}`;
    case "chan":
      return `chan[${typeStr(t.elem)}]`;
    case "fn": {
      const ps = t.params.map((p) => typeStr(p)).join(", ");
      let s = `fn(${ps})`;
      if (t.ret !== undefined && t.ret.kind !== "unit") s += `: ${typeStr(t.ret)}`;
      return s;
    }
    case "struct":
      return symName(t.sym);
    case "enum":
      return symName(t.sym);
    case "option":
      return `Option[${typeStr(t.elem)}]`;
    case "result":
      return `Result[${typeStr(ResultOk(t))}, ${typeStr(ResultErr(t))}]`;
    case "type_arg":
      return t.name !== "" ? t.name : symName(t.sym);
    default: {
      const _never: never = k;
      return _never;
    }
  }
}

function symName(s: Symbol | undefined): string {
  return s === undefined ? "?" : s.name;
}
