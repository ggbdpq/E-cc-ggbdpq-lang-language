// 内建函数与最小标准库面（jsonParse/jsonStringify/makeChan/send/recv/isNone/http.get）。
// JSON 行为与 Go encoding/json 对齐：null→None、整数优先 int、键字母序输出、HTML 转义。
// 错误消息按 Go 参考实现逐字复刻（内建错误不带行号前缀）。

import { spawnSync } from "node:child_process";
import { RtError } from "./errors.js";
import type { Interp } from "./interp.js";
import {
  boolV, codePointCompare, fmtVal, goJSONFloat, goJSONString, goQuote, intV, mapGet, mapSet,
  strV, typeName, unitV,
  type Builtin, type MapVal, type Val,
} from "./value.js";

const unit: Val = { k: "unit" };
const INT64_MAX = 0x7fffffffffffffffn;
const INT64_MIN = -0x8000000000000000n;

function arity(name: string, args: Val[], n: number): void {
  if (args.length !== n) throw new RtError(`${name} 需要 ${n} 个参数，得到 ${args.length} 个`);
}

/** Go strconv.ParseInt(s, 10, 64) 等价（入参须已 TrimSpace） */
function goParseInt(s: string): bigint | undefined {
  if (!/^[+-]?[0-9]+$/.test(s)) return undefined;
  const v = BigInt(s);
  if (v > INT64_MAX || v < INT64_MIN) return undefined;
  return v;
}

/** Go strconv.ParseFloat 等价（十进制/十六进制浮点、Inf/NaN、整串消费） */
function goParseFloat(raw: string): number | undefined {
  const s = raw.trim();
  if (s === "") return undefined;
  const sign = s.startsWith("-") ? -1 : 1;
  const lower = s.toLowerCase();
  const body = lower.startsWith("+") || lower.startsWith("-") ? lower.slice(1) : lower;
  if (body === "inf" || body === "infinity") return sign * Number.POSITIVE_INFINITY;
  if (body === "nan") return Number.NaN;
  // 十六进制浮点 0xh[.h][p±d]
  const hexMatch = /^0x([0-9a-f]*)(?:\.([0-9a-f]*))?(?:p([+-]?[0-9]+))?$/.exec(body);
  if (hexMatch !== null) {
    const intPart = hexMatch[1] ?? "";
    const fracPart = hexMatch[2] ?? "";
    if (intPart === "" && fracPart === "") return undefined;
    let v = 0;
    for (const c of intPart) v = v * 16 + parseInt(c, 16);
    let scale = 1 / 16;
    for (const c of fracPart) {
      v += parseInt(c, 16) * scale;
      scale /= 16;
    }
    if (hexMatch[3] !== undefined) v *= Math.pow(2, Number(hexMatch[3]));
    return sign * v;
  }
  // 十进制：整串消费
  if (!/^(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$/.test(body)) return undefined;
  const v = Number(s);
  if (Number.isNaN(v)) return undefined;
  return v;
}

function fmtArgs(args: Val[]): string {
  return args.map((a) => fmtVal(a, true)).join(" ");
}

function errResult(it: Interp, msg: string): Val {
  const errVal: Val = { k: "struct", def: it.errDef(), fields: [strV(msg)] };
  return { k: "enum", def: it.resultDef(), case: 1, payloads: [errVal] };
}

// —— JSON ——

/** json.Number 语义：整数优先 int64，否则 float */
function jsonNumber(literal: string): Val {
  const n = goParseInt(literal);
  if (n !== undefined) return intV(n);
  const f = goParseFloat(literal);
  return { k: "float", v: f === undefined ? Number.NaN : f };
}

interface RawNumber {
  jsonTag: "number";
  literal: string;
}

function isRawNumber(x: unknown): x is RawNumber {
  return typeof x === "object" && x !== null && (x as { jsonTag?: unknown }).jsonTag === "number";
}

/** 解析 JSON 并保留数字字面文本（先用 JSON.parse 校验合法性） */
function parseJson(text: string): unknown {
  JSON.parse(text);
  const parser = new JsonNumberParser(text);
  return parser.parse();
}

/** 极简 JSON 读取器（数字保留字面；其余语义与 JSON.parse 一致——合法性已先行校验） */
class JsonNumberParser {
  private i = 0;
  constructor(private readonly text: string) {}

  parse(): unknown {
    this.ws();
    const v = this.value();
    this.ws();
    return v;
  }

  private ws(): void {
    while (this.i < this.text.length) {
      const c = this.text[this.i];
      if (c === " " || c === "\t" || c === "\n" || c === "\r") this.i++;
      else break;
    }
  }

  private value(): unknown {
    this.ws();
    const c = this.text[this.i];
    if (c === "{") {
      this.i++;
      const obj: Record<string, unknown> = {};
      this.ws();
      if (this.text[this.i] === "}") {
        this.i++;
        return obj;
      }
      for (;;) {
        this.ws();
        const key = this.str();
        this.ws();
        this.i++; // ':'（合法性已由 JSON.parse 保证）
        obj[key] = this.value();
        this.ws();
        const d = this.text[this.i];
        if (d === ",") {
          this.i++;
          continue;
        }
        this.i++; // '}'
        return obj;
      }
    }
    if (c === "[") {
      this.i++;
      const arr: unknown[] = [];
      this.ws();
      if (this.text[this.i] === "]") {
        this.i++;
        return arr;
      }
      for (;;) {
        arr.push(this.value());
        this.ws();
        const d = this.text[this.i];
        if (d === ",") {
          this.i++;
          continue;
        }
        this.i++; // ']'
        return arr;
      }
    }
    if (c === '"') return this.str();
    if (this.text.startsWith("true", this.i)) {
      this.i += 4;
      return true;
    }
    if (this.text.startsWith("false", this.i)) {
      this.i += 5;
      return false;
    }
    if (this.text.startsWith("null", this.i)) {
      this.i += 4;
      return null;
    }
    return this.numLit();
  }

  private str(): string {
    const start = this.i;
    this.i++; // "
    while (this.i < this.text.length) {
      const c = this.text[this.i]!;
      if (c === "\\") {
        this.i += 2;
        continue;
      }
      if (c === '"') {
        this.i++;
        break;
      }
      this.i++;
    }
    return JSON.parse(this.text.slice(start, this.i)) as string;
  }

  private numLit(): RawNumber {
    const start = this.i;
    while (this.i < this.text.length && /[-+0-9.eE]/.test(this.text[this.i]!)) this.i++;
    return { jsonTag: "number", literal: this.text.slice(start, this.i) };
  }
}

/** JSON 表示 → lang-go 值；null → None；对象键按字母序（码点序）载入 */
function jsonToValue(raw: unknown, it: Interp): Val {
  if (isRawNumber(raw)) return jsonNumber(raw.literal);
  if (raw === null) return { k: "enum", def: it.optionDef(), case: 1, payloads: [] };
  if (typeof raw === "boolean") return boolV(raw);
  if (typeof raw === "string") return strV(raw);
  if (Array.isArray(raw)) return { k: "arr", items: raw.map((el) => jsonToValue(el, it)) };
  if (typeof raw === "object") {
    const m: Val = { k: "map", ks: [], m: new Map() };
    const keys = Object.keys(raw).sort(codePointCompare);
    for (const k of keys) {
      mapSet(m as MapVal, strV(k), jsonToValue((raw as Record<string, unknown>)[k], it));
    }
    return m;
  }
  return unitV;
}

function jsonParseBuiltin(it: Interp, args: Val[]): Val {
  arity("jsonParse", args, 1);
  const s = args[0]!;
  if (s.k !== "str") {
    throw new RtError(`jsonParse 需要字符串，得到 ${typeName(s)}`);
  }
  let raw: unknown;
  try {
    raw = parseJson(s.v);
  } catch (e) {
    throw new RtError(`JSON 解析失败: ${goJsonErrMessage(e)}`);
  }
  return jsonToValue(raw, it);
}

/** Go json.Decoder 错误文本的常见形态复刻 */
function goJsonErrMessage(e: unknown): string {
  const msg = e instanceof Error ? e.message : String(e);
  if (msg.includes("Unexpected end") || msg.includes("unexpected end")) {
    return "unexpected end of JSON input";
  }
  const m = /Unexpected token (.)/.exec(msg);
  if (m !== null) {
    return `invalid character ${m[1]} looking for beginning of value`;
  }
  return "invalid character looking for beginning of value";
}

// —— jsonStringify ——

/** lang-go 值 → 中间 JSON 树；None → null */
function valueToJSON(v: Val): unknown {
  switch (v.k) {
    case "unit":
      return { jsonNull: true as const };
    case "bool":
      return v.v;
    case "int":
      return { jsonInt: v.v };
    case "float":
      return { jsonFloat: v.v };
    case "str":
      return v.v;
    case "enum":
      if (v.def.name === "Option" && v.case === 1) return { jsonNull: true as const };
      throw new RtError(`${v.def.cases[v.case] ?? "?"} 不可 JSON 序列化`);
    case "arr":
      return v.items.map(valueToJSON);
    case "map": {
      const out: Record<string, unknown> = {};
      for (const k of v.ks) {
        if (k.k !== "str") {
          throw new RtError(`JSON 对象键须为 string，得到 ${typeName(k)}`);
        }
        out[k.v] = valueToJSON(mapGet(v, k).v ?? unitV);
      }
      return out;
    }
    default:
      throw new RtError(`${typeName(v)} 不可 JSON 序列化`);
  }
}

/** 序列化：键码点序、HTML 转义开启、浮点按 Go encoding/json 形 */
function marshalJSON(j: unknown): string {
  if (j === null) return "null";
  if (typeof j === "object" && "jsonNull" in (j as Record<string, unknown>)) return "null";
  if (typeof j === "object" && "jsonInt" in (j as Record<string, unknown>)) {
    return ((j as { jsonInt: bigint }).jsonInt).toString(10);
  }
  if (typeof j === "object" && "jsonFloat" in (j as Record<string, unknown>)) {
    const f = (j as { jsonFloat: number }).jsonFloat;
    if (Number.isNaN(f) || !Number.isFinite(f)) {
      const name = Number.isNaN(f) ? "NaN" : f > 0 ? "+Inf" : "-Inf";
      throw new RtError(`JSON 序列化失败: json: unsupported value: ${name}`);
    }
    return goJSONFloat(f);
  }
  if (typeof j === "boolean") return j ? "true" : "false";
  if (typeof j === "string") return goJSONString(j);
  if (Array.isArray(j)) return "[" + j.map(marshalJSON).join(",") + "]";
  if (typeof j === "object") {
    const keys = Object.keys(j).sort(codePointCompare);
    const parts = keys.map((k) => `${goJSONString(k)}:${marshalJSON((j as Record<string, unknown>)[k])}`);
    return "{" + parts.join(",") + "}";
  }
  return "null";
}

function jsonStringifyBuiltin(_it: Interp, args: Val[]): Val {
  arity("jsonStringify", args, 1);
  return strV(marshalJSON(valueToJSON(args[0]!)));
}

// —— chan ——

function makeChan(_it: Interp, args: Val[]): Val {
  if (args.length !== 0) throw new RtError("makeChan 不需要参数");
  return { k: "chan", items: [] };
}

function send(_it: Interp, args: Val[]): Val {
  if (args.length !== 2) {
    throw new RtError(`send 需要 2 个参数（ch, v），得到 ${args.length} 个`);
  }
  const ch = args[0]!;
  if (ch.k !== "chan") {
    throw new RtError(`send 需要通道，得到 ${typeName(ch)}`);
  }
  ch.items.push(args[1]!);
  return unit;
}

function recv(it: Interp, args: Val[]): Val {
  if (args.length !== 1) {
    throw new RtError(`recv 需要 1 个参数（ch），得到 ${args.length} 个`);
  }
  const ch = args[0]!;
  if (ch.k !== "chan") {
    throw new RtError(`recv 需要通道，得到 ${typeName(ch)}`);
  }
  if (ch.items.length === 0) {
    return errResult(it, "channel 空");
  }
  const v = ch.items[0]!;
  ch.items.shift();
  return { k: "enum", def: it.resultDef(), case: 0, payloads: [v] };
}

function isNone(_it: Interp, args: Val[]): Val {
  arity("isNone", args, 1);
  const x = args[0]!;
  if (x.k === "enum") {
    return boolV(x.def.name === "Option" && x.case === 1);
  }
  return boolV(false);
}

// —— http.get（平台绑定；FFI-001 口径不进 Conformance） ——

/** 同步 GET：Node 无同步 fetch，走 curl 子进程（30s 超时对齐 Go 侧） */
export function httpGetImpl(it: Interp, args: Val[]): Val {
  if (args.length !== 2) {
    throw new RtError(`http.get 需要 2 个参数（url, headers），得到 ${args.length} 个`);
  }
  const url = args[0]!;
  if (url.k !== "str") {
    throw new RtError(`http.get 的 url 须为 string，得到 ${typeName(url)}`);
  }
  const headerArgs: string[] = [];
  const a1 = args[1]!;
  if (a1.k !== "unit") {
    if (a1.k !== "map") {
      throw new RtError(`http.get 的 headers 须为 map[string]string，得到 ${typeName(a1)}`);
    }
    for (const k of a1.ks) {
      if (k.k !== "str") continue;
      const v = mapGet(a1, k).v;
      if (v !== undefined && v.k === "str") {
        headerArgs.push("-H", `${k.v}: ${v.v}`);
      }
    }
  }
  const res = spawnSync(
    "curl",
    ["-sS", "--max-time", "30", "-w", "\n%{http_code}", ...headerArgs, url.v],
    { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 },
  );
  if (res.error !== undefined || res.status === null) {
    return errResult(it, res.error !== undefined ? res.error.message : "请求失败");
  }
  const out = res.stdout ?? "";
  const nl = out.lastIndexOf("\n");
  const body = nl >= 0 ? out.slice(0, nl) : out;
  const status = Number.parseInt(nl >= 0 ? out.slice(nl + 1).trim() : "0", 10);
  return {
    k: "enum",
    def: it.resultDef(),
    case: 0,
    payloads: [
      { k: "struct", def: it.httpRespDef(), fields: [intV(BigInt(Number.isNaN(status) ? 0 : status)), strV(body)] },
    ],
  };
}

// —— 核心内建（prelude） ——

export const CORE_BUILTINS: readonly (readonly [string, Builtin["fn"]])[] = [
  [
    "println",
    (it, args) => {
      it.out.write(fmtArgs(args) + "\n");
      return unit;
    },
  ],
  [
    "print",
    (it, args) => {
      it.out.write(fmtArgs(args));
      return unit;
    },
  ],
  [
    "len",
    (_it, args) => {
      arity("len", args, 1);
      const x = args[0]!;
      switch (x.k) {
        case "str":
          return intV(BigInt(Array.from(x.v).length));
        case "arr":
          return intV(BigInt(x.items.length));
        case "map":
          return intV(BigInt(x.ks.length));
        case "range":
          return intV(x.hi > x.lo ? x.hi - x.lo : 0n);
        default:
          throw new RtError(`len 不支持 ${typeName(x)}`);
      }
    },
  ],
  [
    "str",
    (_it, args) => {
      arity("str", args, 1);
      return strV(fmtVal(args[0]!, true));
    },
  ],
  [
    "int",
    (_it, args) => {
      arity("int", args, 1);
      const x = args[0]!;
      switch (x.k) {
        case "int":
          return x;
        case "float": {
          // int(float) 边界（TYPE-CONV-005 v1.0.1，ADR-0006/RS-006 跨实现硬契约）：
          // NaN → 0；正/负溢出饱和到 int64 边界；其余向零截断
          if (Number.isNaN(x.v)) return intV(0n);
          if (x.v >= 2 ** 63) return intV(INT64_MAX);
          if (x.v <= -(2 ** 63)) return intV(INT64_MIN);
          return intV(BigInt(Math.trunc(x.v)));
        }
        case "str": {
          const n = goParseInt(x.v.trim());
          if (n === undefined) {
            throw new RtError(`无法把 ${goQuote(x.v)} 转为 int`);
          }
          return intV(n);
        }
        default:
          throw new RtError(`int() 不支持 ${typeName(x)}`);
      }
    },
  ],
  [
    "float",
    (_it, args) => {
      arity("float", args, 1);
      const x = args[0]!;
      switch (x.k) {
        case "int":
          return { k: "float", v: Number(x.v) };
        case "float":
          return x;
        case "str": {
          const f = goParseFloat(x.v.trim());
          if (f === undefined) {
            throw new RtError(`无法把 ${goQuote(x.v)} 转为 float`);
          }
          return { k: "float", v: f };
        }
        default:
          throw new RtError(`float() 不支持 ${typeName(x)}`);
      }
    },
  ],
  [
    "keys",
    (_it, args) => {
      arity("keys", args, 1);
      const m = args[0]!;
      if (m.k !== "map") throw new RtError(`keys 需要映射，得到 ${typeName(m)}`);
      return { k: "arr", items: [...m.ks] };
    },
  ],
  [
    "values",
    (_it, args) => {
      arity("values", args, 1);
      const m = args[0]!;
      if (m.k !== "map") throw new RtError(`values 需要映射，得到 ${typeName(m)}`);
      const vs: Val[] = [];
      for (const k of m.ks) {
        vs.push(mapGet(m, k).v ?? unitV);
      }
      return { k: "arr", items: vs };
    },
  ],
  [
    "has",
    (_it, args) => {
      arity("has", args, 2);
      const m = args[0]!;
      if (m.k !== "map") throw new RtError(`has 需要映射，得到 ${typeName(m)}`);
      return boolV(mapGet(m, args[1]!).found);
    },
  ],
  [
    "push",
    (_it, args) => {
      if (args.length < 2) throw new RtError("push 需要至少 2 个参数");
      const arr = args[0]!;
      if (arr.k !== "arr") throw new RtError(`push 需要数组，得到 ${typeName(arr)}`);
      arr.items.push(...args.slice(1));
      return arr;
    },
  ],
  [
    "assert",
    (_it, args) => {
      if (args.length < 1 || args.length > 2) {
        throw new RtError("assert 需要 1~2 个参数");
      }
      if (args[0]!.k !== "bool") {
        throw new RtError(`条件须为 bool，得到 ${typeName(args[0]!)}`);
      }
      if (!args[0]!.v) {
        if (args.length === 2) {
          throw new RtError(`assert 失败: ${fmtVal(args[1]!, true)}`);
        }
        throw new RtError("assert 失败");
      }
      return unit;
    },
  ],
  [
    "err",
    (it, args) => {
      arity("err", args, 1);
      const x = args[0]!;
      if (x.k !== "str") throw new RtError(`err 需要字符串，得到 ${typeName(x)}`);
      return { k: "struct", def: it.errDef(), fields: [x] };
    },
  ],
  [
    "bytes",
    (_it, args) => {
      arity("bytes", args, 1);
      const x = args[0]!;
      if (x.k !== "str") throw new RtError(`bytes 需要字符串，得到 ${typeName(x)}`);
      const bs = new TextEncoder().encode(x.v);
      return { k: "arr", items: Array.from(bs, (b) => intV(BigInt(b))) };
    },
  ],
  [
    "char",
    (_it, args) => {
      arity("char", args, 1);
      const x = args[0]!;
      if (x.k !== "int") throw new RtError(`char 需要 int，得到 ${typeName(x)}`);
      // Go string(rune(n))：非法码点/代理区 → U+FFFD
      let cp = x.v;
      if (cp < 0n || cp > 0x10ffffn || (cp >= 0xd800n && cp <= 0xdfffn)) cp = 0xfffdn;
      return strV(String.fromCodePoint(Number(cp)));
    },
  ],
];

// —— 标准库（json/chan/isNone/http 服务值） ——

/** 预载 HttpResp/http 服务值与 json/chan 内建（返回注册项；http 服务值由 Interp 组装） */
export function makeStdlib(it: Interp): readonly (readonly [string, Builtin["fn"]])[] {
  it.setupHttpService(httpGetImpl);
  return [
    ["jsonParse", jsonParseBuiltin],
    ["jsonStringify", jsonStringifyBuiltin],
    [
      "makeChan",
      makeChan,
    ],
    ["send", send],
    ["recv", recv],
    ["isNone", isNone],
  ];
}

export type { Val, MapVal };
