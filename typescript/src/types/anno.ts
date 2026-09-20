// 注解解析的独立入口：HIR 构建阶段把声明上的注解解析为语义类型。
// 前置条件：checker 已通过（注解合法，无需再报错）。

import type { TypeEx } from "../syntax/ast.js";
import type { ResolveResult } from "../semantic/resolver.js";
import type { Symbol } from "../semantic/symbols.js";
import {
  Any, Bool, ErrorT, Float, Int, String, Unknown,
  ChanOf, EnumOf, FnOf, MapOf, OptionOf, RefOf, ResultOf, SliceOf, StructOf,
  type Type,
} from "./model.js";

/** 解析注解。tparams 为泛型参数名集（可空） */
export function resolveAnno(res: ResolveResult, t: TypeEx | undefined, tparams: readonly string[] = []): Type {
  if (t === undefined) return Unknown;
  return resolveAnnoInner(res, t, new Set(tparams));
}

function tparamsSet(names: readonly string[]): Set<string> {
  return new Set(names);
}

function lookupTypeSym(res: ResolveResult, name: string): Symbol | undefined {
  const b = res.builtin.get(name);
  if (b !== undefined && b.kind === "struct") return b;
  for (const s of res.symbols) {
    if ((s.kind === "struct" || s.kind === "enum") && s.name === name) return s;
  }
  return undefined;
}

function resolveAnnoInner(res: ResolveResult, t: TypeEx, tps: Set<string>): Type {
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
          const elem = t.args.length === 1 ? resolveAnnoInner(res, t.args[0]!, tps) : Unknown;
          return ChanOf(elem);
        }
        case "Option":
          return t.args.length === 1
            ? OptionOf(resolveAnnoInner(res, t.args[0]!, tps))
            : OptionOf(Unknown);
        case "Result": {
          const ok = t.args.length >= 1 ? resolveAnnoInner(res, t.args[0]!, tps) : Unknown;
          return ResultOf(ok, ErrorT);
        }
        default: {
          if (tps.has(t.name)) return { kind: "type_arg", elem: undefined, key: undefined, params: [], ret: undefined, sym: undefined, name: t.name };
          const sym = lookupTypeSym(res, t.name);
          if (sym !== undefined) return sym.kind === "enum" ? EnumOf(sym) : StructOf(sym);
          return Unknown;
        }
      }
    case "arr_type":
      return SliceOf(resolveAnnoInner(res, t.elem, tps));
    case "map_type":
      return MapOf(resolveAnnoInner(res, t.key, tps), resolveAnnoInner(res, t.val, tps));
    case "ptr_type":
      return RefOf(resolveAnnoInner(res, t.elem, tps));
    case "fn_type": {
      const ps = t.params.map((p) => resolveAnnoInner(res, p, tps));
      const ret = t.ret !== undefined ? resolveAnnoInner(res, t.ret, tps) : Unknown;
      return FnOf(ps, ret);
    }
    default: {
      const _never: never = t;
      return _never;
    }
  }
}

export { tparamsSet };
