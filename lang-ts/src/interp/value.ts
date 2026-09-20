// 运行时值模型与输出格式（specs/values.md、runtime.md RUNTIME-OUTPUT-003）。
// int 以 bigint 承载（int64 精确）；float 以 number 承载；
// struct 拷贝、容器共享、map 保插入序；输出格式与 Go fmt 逐字节对齐。

import type { FnDecl } from "../syntax/ast.js";
import type { Interp } from "./interp.js";

export interface IntVal {
  readonly k: "int";
  readonly v: bigint;
}
export interface FloatVal {
  readonly k: "float";
  readonly v: number;
}
export interface BoolVal {
  readonly k: "bool";
  readonly v: boolean;
}
export interface StrVal {
  readonly k: "str";
  readonly v: string;
}
export interface UnitVal {
  readonly k: "unit";
}
export interface RangeVal {
  readonly k: "range";
  readonly lo: bigint;
  readonly hi: bigint;
}
/** 切片：引用语义 */
export interface ArrVal {
  readonly k: "arr";
  readonly items: Val[];
}
/** 保插入序的映射 */
export interface MapVal {
  readonly k: "map";
  readonly ks: Val[];
  readonly m: Map<string, Val>;
}
export interface StructDef {
  readonly name: string;
  readonly fields: readonly string[];
  readonly methods: Map<string, Closure>;
}
export interface EnumDef {
  readonly name: string;
  readonly cases: readonly string[];
}
export interface StructVal {
  readonly k: "struct";
  readonly def: StructDef;
  readonly fields: Val[];
}
export interface EnumVal {
  readonly k: "enum";
  readonly def: EnumDef;
  readonly case: number;
  readonly payloads: Val[];
}
export interface PtrVal {
  readonly k: "ptr";
  readonly cell: { val: Val };
}
export interface ChanVal {
  readonly k: "chan";
  readonly items: Val[];
}
/** 环境的结构视图（实现在 interp.ts 的 Env 类；此处仅类型依赖，避免循环导入） */
export interface EnvLike {
  readonly parent?: EnvLike | undefined;
  lookup(name: string): { val: Val; mutable: boolean } | undefined;
}

export interface Closure {
  readonly k: "closure";
  readonly name: string;
  readonly decl: FnDecl;
  readonly env: EnvLike;
}
export interface Builtin {
  readonly k: "builtin";
  readonly name: string;
  readonly fn: (it: Interp, args: Val[]) => Val;
}

export type Val =
  | IntVal
  | FloatVal
  | BoolVal
  | StrVal
  | UnitVal
  | RangeVal
  | ArrVal
  | MapVal
  | StructVal
  | EnumVal
  | PtrVal
  | ChanVal
  | Closure
  | Builtin;

export const intV = (v: bigint): Val => ({ k: "int", v });
export const floatV = (v: number): Val => ({ k: "float", v });
export const boolV = (v: boolean): Val => ({ k: "bool", v });
export const strV = (v: string): Val => ({ k: "str", v });
export const unitV: Val = { k: "unit" };

export function caseIndex(def: EnumDef, name: string): number {
  return def.cases.indexOf(name);
}

/** map 键的规范串（i:%d / s:%s / b:%v） */
export function keyString(v: Val): string | undefined {
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

export function mapGet(m: MapVal, k: Val): { found: boolean; v?: Val } {
  const ks = keyString(k);
  if (ks === undefined) return { found: false };
  const v = m.m.get(ks);
  return v === undefined ? { found: false } : { found: true, v };
}

export function mapSet(m: MapVal, k: Val, v: Val): string | undefined {
  const ks = keyString(k);
  if (ks === undefined) return `映射键须为 int/string/bool，得到 ${typeName(k)}`;
  if (!m.m.has(ks)) m.ks.push(k);
  m.m.set(ks, v);
  return undefined;
}

/** 落实 struct 值语义：struct 递归拷贝、enum 载荷递归拷贝；slice/map 共享 */
export function copyValue(v: Val): Val {
  switch (v.k) {
    case "struct": {
      const fs = v.fields.map(copyValue);
      return { k: "struct", def: v.def, fields: fs };
    }
    case "enum": {
      const ps = v.payloads.map(copyValue);
      return { k: "enum", def: v.def, case: v.case, payloads: ps };
    }
    default:
      return v;
  }
}

export function typeName(v: Val): string {
  switch (v.k) {
    case "int":
      return "int";
    case "float":
      return "float";
    case "bool":
      return "bool";
    case "str":
      return "string";
    case "unit":
      return "()";
    case "arr":
      return "数组";
    case "map":
      return "映射";
    case "range":
      return "区间";
    case "struct":
      return v.def.name;
    case "enum":
      return v.def.name;
    case "closure":
      return "函数";
    case "builtin":
      return "内建函数";
    case "ptr":
      return "引用";
    case "chan":
      return "chan";
    default: {
      const _never: never = v;
      return _never;
    }
  }
}

/** == 语义：标量同型比较、enum 同 case 递归比较；容器不可比 */
export function equals(a: Val, b: Val): { ok: boolean; eq?: boolean; err?: string } {
  switch (a.k) {
    case "int":
      if (b.k !== "int") return { ok: false, err: typeErr(a, b) };
      return { ok: true, eq: a.v === b.v };
    case "float":
      if (b.k !== "float") return { ok: false, err: typeErr(a, b) };
      return { ok: true, eq: a.v === b.v };
    case "bool":
      if (b.k !== "bool") return { ok: false, err: typeErr(a, b) };
      return { ok: true, eq: a.v === b.v };
    case "str":
      if (b.k !== "str") return { ok: false, err: typeErr(a, b) };
      return { ok: true, eq: a.v === b.v };
    case "enum": {
      if (b.k !== "enum" || b.def !== a.def || b.case !== a.case) return { ok: true, eq: false };
      for (let i = 0; i < a.payloads.length; i++) {
        const r = equals(a.payloads[i]!, b.payloads[i]!);
        if (r.err !== undefined) return { ok: false, err: r.err };
        if (!r.eq) return { ok: true, eq: false };
      }
      return { ok: true, eq: true };
    }
    default:
      return { ok: false, err: `${typeName(a)} 不可用 == 比较` };
  }
}

function typeErr(a: Val, b: Val): string {
  return `${typeName(a)} 与 ${typeName(b)} 不可比较（无隐式转换）`;
}

export function truthy(v: Val): { ok: boolean; b?: boolean; err?: string } {
  if (v.k !== "bool") return { ok: false, err: `条件须为 bool，得到 ${typeName(v)}` };
  return { ok: true, b: v.v };
}

/** 同型 int/float/str 大小比较；不可比返回 undefined */
export function compare(a: Val, b: Val): number | undefined {
  if (a.k === "int" && b.k === "int") {
    return a.v < b.v ? -1 : a.v > b.v ? 1 : 0;
  }
  if (a.k === "float" && b.k === "float") {
    return a.v < b.v ? -1 : a.v > b.v ? 1 : 0;
  }
  if (a.k === "str" && b.k === "str") {
    // Go strings.Compare：字节序 = 码点序
    return codePointCompare(a.v, b.v);
  }
  return undefined;
}

/** 导出的值格式化（REPL 与工具用） */
export const fmt = (v: Val): string => fmtVal(v, true);

// —— 输出格式 ——

/**
 * 复刻 Go strconv.FormatFloat(f, 'g', -1, 64)：
 * 最短舍入数字 + exp<-4||exp>=21 走 %e（指数至少两位、恒带符号），否则 %f。
 */
export function goFormatFloat(f: number): string {
  if (Number.isNaN(f)) return "NaN";
  if (f === Number.POSITIVE_INFINITY) return "+Inf";
  if (f === Number.NEGATIVE_INFINITY) return "-Inf";
  if (f === 0) return Object.is(f, -0) ? "-0" : "0";
  const neg = f < 0;
  const a = Math.abs(f);
  const { digits, dp } = shortestDigits(a);
  const exp = dp - 1;
  let body: string;
  if (exp < -4 || exp >= 21) {
    body = sciFormat(digits, exp);
  } else {
    body = fixedFormat(digits, dp);
  }
  return neg ? `-${body}` : body;
}

/** 最短舍入数字（来自 JS toString 的最短表示），dp：value = 0.digits × 10^dp */
function shortestDigits(a: number): { digits: string; dp: number } {
  let s = a.toString(); // 形如 "123.45" / "1.2e+21"
  let digits: string;
  let dp: number;
  const eIdx = s.indexOf("e");
  if (eIdx >= 0) {
    const mant = s.slice(0, eIdx);
    const exp = Number(s.slice(eIdx + 1));
    const dot = mant.indexOf(".");
    if (dot >= 0) {
      digits = mant.slice(0, dot) + mant.slice(dot + 1);
      dp = exp + dot + 1;
    } else {
      digits = mant;
      dp = exp + mant.length;
    }
  } else {
    const dot = s.indexOf(".");
    if (dot >= 0) {
      digits = s.slice(0, dot) + s.slice(dot + 1);
      dp = dot;
    } else {
      digits = s;
      dp = s.length;
    }
  }
  // 去尾零（JS 平凡形式可能带尾零，如 "1200"）
  let last = digits.length;
  while (last > 1 && digits[last - 1] === "0") last--;
  digits = digits.slice(0, last);
  // 去前导零（"0.001" → digits "1"，每去一位 dp 减一）
  let start = 0;
  while (start < digits.length - 1 && digits[start] === "0") start++;
  if (start > 0) {
    digits = digits.slice(start);
    dp -= start;
  }
  return { digits, dp };
}

/** %e 形：d.ddde±XX（指数至少两位） */
function sciFormat(digits: string, exp: number): string {
  let mant = digits[0]!;
  if (digits.length > 1) mant += "." + digits.slice(1);
  const sign = exp < 0 ? "-" : "+";
  const ae = Math.abs(exp).toString().padStart(2, "0");
  return `${mant}e${sign}${ae}`;
}

/** %f 形：定点（不省尾零——最短数字本身不含多余零） */
function fixedFormat(digits: string, dp: number): string {
  if (dp <= 0) return "0." + "0".repeat(-dp) + digits;
  if (dp >= digits.length) return digits + "0".repeat(dp - digits.length);
  return digits.slice(0, dp) + "." + digits.slice(dp);
}

/** 复刻 Go strconv.Quote（%q） */
export function goQuote(s: string): string {
  let out = '"';
  for (const r of s) {
    const cp = r.codePointAt(0)!;
    if (cp < 0x80) {
      if (cp === 0x22) out += '\\"';
      else if (cp === 0x5c) out += "\\\\";
      else if (cp >= 0x20 && cp < 0x7f) out += r;
      else out += asciiEscape(cp);
      continue;
    }
    if (cp === 0x2028 || cp === 0x2029 || !isPrintUnicode(cp)) {
      if (cp > 0xffff) {
        const hex = cp.toString(16).padStart(8, "0");
        out += `\\${hex}`;
      } else {
        out += `\\u${cp.toString(16).padStart(4, "0")}`;
      }
      continue;
    }
    out += r;
  }
  return out + '"';
}

function asciiEscape(cp: number): string {
  switch (cp) {
    case 0x07:
      return "\\a";
    case 0x08:
      return "\\b";
    case 0x0c:
      return "\\f";
    case 0x0a:
      return "\\n";
    case 0x0d:
      return "\\r";
    case 0x09:
      return "\\t";
    case 0x0b:
      return "\\v";
    default:
      return `\\x${cp.toString(16).padStart(2, "0")}`;
  }
}

/** Go unicode.IsPrint：L/M/N/P/S + ASCII 空格 */
const RE_PRINT = /[\p{L}\p{M}\p{N}\p{P}\p{S}]/u;
function isPrintUnicode(cp: number): boolean {
  if (cp === 0x20) return true;
  return RE_PRINT.test(String.fromCodePoint(cp));
}

export function fmtVal(v: Val, top: boolean): string {
  switch (v.k) {
    case "int":
      return v.v.toString(10);
    case "float":
      return goFormatFloat(v.v);
    case "bool":
      return v.v ? "true" : "false";
    case "str":
      return top ? v.v : goQuote(v.v);
    case "unit":
      return "()";
    case "arr":
      return "[" + v.items.map((it) => fmtVal(it, false)).join(", ") + "]";
    case "map": {
      const parts: string[] = [];
      for (const k of v.ks) {
        const { v: kv } = mapGet(v, k);
        parts.push(`${fmtVal(k, false)}: ${fmtVal(kv ?? unitV, false)}`);
      }
      return "{" + parts.join(", ") + "}";
    }
    case "struct": {
      const parts = v.fields.map((f, i) => `${v.def.fields[i]}: ${fmtVal(f, false)}`);
      return v.def.name + "{" + parts.join(", ") + "}";
    }
    case "enum": {
      const name = v.def.cases[v.case];
      if (v.payloads.length === 0) return name ?? "";
      return name + "(" + v.payloads.map((p) => fmtVal(p, false)).join(", ") + ")";
    }
    case "range":
      return `${v.lo.toString(10)}..${v.hi.toString(10)}`;
    case "closure":
      return v.name !== "" ? `<fn ${v.name}>` : "<fn 匿名>";
    case "builtin":
      return `<内建 ${v.name}>`;
    case "ptr":
      return "&" + fmtVal(v.cell.val, false);
    case "chan":
      return "?"; // Go 值域无此分支（chan 值不打印），与 Go 一致落到 "?"
    default: {
      const _never: never = v;
      return _never;
    }
  }
}

// —— Go JSON 兼容（RUNTIME-JSON-002）——

/**
 * 复刻 Go encoding/json 的 float 编码：
 * abs<1e-6 || abs>=1e21 走 'e'（指数去前导零），否则 'f' 最短。
 */
export function goJSONFloat(f: number): string {
  const a = Math.abs(f);
  let body: string;
  if (a !== 0 && (a < 1e-6 || a >= 1e21)) {
    const { digits, dp } = shortestDigits(a);
    const exp = dp - 1;
    let mant = digits[0]!;
    if (digits.length > 1) mant += "." + digits.slice(1);
    // Go 清理：e-09 → e-9（仅去掉指数前导零，至少 1 位）
    const sign = exp < 0 ? "-" : "+";
    body = `${mant}e${sign}${Math.abs(exp).toString()}`;
  } else {
    const { digits, dp } = shortestDigits(Math.abs(f));
    body = fixedFormat(digits, dp);
  }
  return f < 0 ? `-${body}` : body;
}

/** 复刻 Go encoding/json 的字符串编码（默认 HTML 转义开启） */
export function goJSONString(s: string): string {
  let out = '"';
  for (const r of s) {
    const cp = r.codePointAt(0)!;
    switch (cp) {
      case 0x22:
        out += '\\"';
        continue;
      case 0x5c:
        out += "\\\\";
        continue;
      case 0x3c:
        out += "\\u003c";
        continue;
      case 0x3e:
        out += "\\u003e";
        continue;
      case 0x26:
        out += "\\u0026";
        continue;
      case 0x08:
        out += "\\b";
        continue;
      case 0x0c:
        out += "\\f";
        continue;
      case 0x0a:
        out += "\\n";
        continue;
      case 0x0d:
        out += "\\r";
        continue;
      case 0x09:
        out += "\\t";
        continue;
      default: {
        if (cp < 0x20) {
          out += `\\u${cp.toString(16).padStart(4, "0")}`;
          continue;
        }
        out += r;
      }
    }
  }
  return out + '"';
}

/** 码点序键比较（Go sort.Strings 按 UTF-8 字节序 = 码点序） */
export function codePointCompare(a: string, b: string): number {
  const ca = Array.from(a);
  const cb = Array.from(b);
  const n = Math.min(ca.length, cb.length);
  for (let i = 0; i < n; i++) {
    const x = ca[i]!.codePointAt(0)!;
    const y = cb[i]!.codePointAt(0)!;
    if (x !== y) return x < y ? -1 : 1;
  }
  return ca.length - cb.length;
}
