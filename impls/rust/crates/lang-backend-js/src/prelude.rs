//! →JS 产物内联运行时（ESM 无依赖；int=BigInt、同步确定子集、struct 标签对象）。
//!
//! 文本形状参照 `impls/typescript/src/backends/jsprelude.ts`，按本后端语义修正：
//! - **无 async**：语言并发为同步确定子集（TASK-SPAWN-002：spawn 等价直接调用），
//!   chan 为数组队列（CHAN-MAP-005），无 await 染色；
//! - **映射键类型前缀**（`i:/s:/b:`）：参考实现 `String(k)` 使 int 1 与 string "1"
//!   撞键；原始键以 `#o:` 前缀条目保留首插序（键串带 `#o:` 前缀者必为内部标记，
//!   用户键经类型前缀永不撞）；
//! - Option/Result 以 enum 值表示（tag = Some/None/Ok/Err），与 IR 指令对齐；
//! - JSON 面按 RUNTIME-JSON-002 硬契约（码点序键、HTML 转义、int64 精度、
//!   -0.0 → `0`），不照抄参考实现分歧（SPEC-GAP-RS-002）。
pub const JS_PRELUDE: &str = r##"// ———— lang-go 运行时镜像（→JS ESM；由 lang-rs 转译器生成，勿手改） ————

function langGoTypeName(v) {
  if (typeof v === "bigint") return "int";
  if (typeof v === "number") return "float";
  if (typeof v === "boolean") return "bool";
  if (typeof v === "string") return "string";
  if (v === null || v === undefined) return "()";
  if (Array.isArray(v)) return "数组";
  if (v instanceof Map) return "映射";
  if (v && v.__langGoRange !== undefined) return "区间";
  if (v && v.__langGoEnum) return v.enumName;
  if (v && v.__langGoErr) return "error";
  if (v && v.__langGoRef) return "引用";
  if (v && v.__langGoClosure) return "函数";
  if (v && v.__langGoChan) return "通道";
  if (v && v.__langGoStruct) return v.__langGoStruct;
  return "未知值";
}

// 值语义拷贝点：用户 struct 深拷贝（嵌套递归），容器/引用/枚举/闭包/chan 共享
function langGoCopy(v) {
  if (v === null || typeof v !== "object") return v;
  if (Array.isArray(v) || v instanceof Map || typeof v === "function") return v;
  if (v.__langGoEnum || v.__langGoChan || v.__langGoRange !== undefined || v.__langGoRef || v.__langGoClosure || v.__langGoErr) return v;
  if (v.__langGoStruct) {
    const out = { __langGoStruct: v.__langGoStruct };
    for (const k of Object.keys(v)) {
      if (k !== "__langGoStruct") out[k] = langGoCopy(v[k]);
    }
    return out;
  }
  return v;
}

// Go strconv.FormatFloat('g',-1,64) 等价（RUNTIME-OUTPUT-004；手算契约锁定）
function langGoFloatStr(v) {
  if (Number.isNaN(v)) return "NaN";
  if (v === Infinity) return "+Inf";
  if (v === -Infinity) return "-Inf";
  if (v === 0) return Object.is(v, -0) ? "-0" : "0";
  const m = /^(-?)(\d)(?:\.(\d+))?[eE]([+-]\d+)$/.exec(v.toExponential());
  const sign = m[1];
  let digits = m[2] + (m[3] || "");
  digits = digits.replace(/0+$/, "");
  const exp = Number(m[4]);
  const dp = exp + 1;
  let body;
  if (exp < -4 || exp >= 6) {
    body = digits[0] + (digits.length > 1 ? "." + digits.slice(1) : "") + "e" + (exp < 0 ? "-" : "+") + String(Math.abs(exp)).padStart(2, "0");
  } else if (dp <= 0) {
    body = "0." + "0".repeat(-dp) + digits;
  } else if (dp >= digits.length) {
    body = digits + "0".repeat(dp - digits.length);
  } else {
    body = digits.slice(0, dp) + "." + digits.slice(dp);
  }
  return sign + body;
}

// Go unicode.IsPrint 近似（Zs/Zl/Zp/Cf/Co/非字符 → 非打印；与 Rust 侧同一近似）
function langGoIsPrint(cp) {
  if (cp === 0x20) return true;
  const zs = [0xa0, 0x1680, 0x202f, 0x205f, 0x3000];
  if (zs.includes(cp) || (cp >= 0x2000 && cp <= 0x200a)) return false;
  if (cp === 0x2028 || cp === 0x2029) return false;
  const cfSingle = [0xad, 0x61c, 0x6dd, 0x70f, 0x180e, 0xfeff];
  const cfRanges = [[0x600, 0x605], [0x200b, 0x200f], [0x202a, 0x202e], [0x2060, 0x2064], [0x2066, 0x206f], [0xfff9, 0xfffb]];
  if (cfSingle.includes(cp)) return false;
  for (const [lo, hi] of cfRanges) if (cp >= lo && cp <= hi) return false;
  if ((cp >= 0xe000 && cp <= 0xf8ff) || (cp >= 0xf0000 && cp <= 0xffffd) || (cp >= 0x100000 && cp <= 0x10fffd)) return false;
  if ((cp >= 0xfdd0 && cp <= 0xfdef) || (cp & 0xfffe) === 0xfffe) return false;
  return true;
}

// Go strconv.Quote 等价（嵌套字符串引号，SPEC-GAP-RS-001 的 JS 侧镜像）
function langGoQuote(s) {
  let out = '"';
  for (const r of s) {
    const cp = r.codePointAt(0);
    if (cp === 0x22) out += '\\"';
    else if (cp === 0x5c) out += "\\\\";
    else if (cp === 0x07) out += "\\a";
    else if (cp === 0x08) out += "\\b";
    else if (cp === 0x0c) out += "\\f";
    else if (cp === 0x0a) out += "\\n";
    else if (cp === 0x0d) out += "\\r";
    else if (cp === 0x09) out += "\\t";
    else if (cp === 0x0b) out += "\\v";
    else if (cp < 0x20 || (cp >= 0x7f && cp < 0xa0)) out += "\\x" + cp.toString(16).padStart(2, "0");
    else if (cp === 0x2028 || cp === 0x2029 || !langGoIsPrint(cp)) {
      out += cp > 0xffff ? "\\U" + cp.toString(16).padStart(8, "0") : "\\u" + cp.toString(16).padStart(4, "0");
    } else out += r;
  }
  return out + '"';
}

function langGoFmt(v, top) {
  if (typeof v === "bigint") return v.toString();
  if (typeof v === "number") return langGoFloatStr(v);
  if (typeof v === "boolean") return v ? "true" : "false";
  if (typeof v === "string") return top ? v : langGoQuote(v);
  if (v === null || v === undefined) return "()";
  if (Array.isArray(v)) return "[" + v.map(x => langGoFmt(x, false)).join(", ") + "]";
  if (v instanceof Map) {
    const parts = [];
    for (const k of langGoKeys(v)) parts.push(langGoFmt(k, false) + ": " + langGoFmt(langGoMapGet(v, k), false));
    return "{" + parts.join(", ") + "}";
  }
  if (v && v.__langGoRange !== undefined) return v.Lo.toString() + ".." + v.Hi.toString();
  if (v && v.__langGoEnum) {
    if (v.vals.length === 0) return v.tag;
    return v.tag + "(" + v.vals.map(x => langGoFmt(x, false)).join(", ") + ")";
  }
  if (v && v.__langGoErr) return "error{msg: " + langGoQuote(v.msg) + "}";
  if (v && v.__langGoRef) return "&" + langGoFmt(v.V, false);
  if (v && v.__langGoClosure) return v.name ? "<fn " + v.name + ">" : "<fn 匿名>";
  if (v && v.__langGoStruct) {
    const parts = [];
    for (const k of Object.keys(v)) {
      if (k !== "__langGoStruct") parts.push(k + ": " + langGoFmt(v[k], false));
    }
    return v.__langGoStruct + "{" + parts.join(", ") + "}";
  }
  return "?";
}

function langGoFmtArgs(args) { return args.map(a => langGoFmt(a, true)).join(" "); }
function langGoPrintln(...args) { console.log(langGoFmtArgs(args)); }
function langGoPrint(...args) { process.stdout.write(langGoFmtArgs(args)); }

// ———— 内建函数 ————

function langGoLen(v) {
  if (typeof v === "string") return BigInt([...v].length);
  if (Array.isArray(v)) return BigInt(v.length);
  if (v instanceof Map) return BigInt(langGoKeys(v).length);
  if (v && v.__langGoRange !== undefined) return v.Hi > v.Lo ? v.Hi - v.Lo : 0n;
  throw new Error("len 不支持 " + langGoTypeName(v));
}
function langGoStr(v) { return langGoFmt(v, true); }
function langGoInt(v) {
  if (typeof v === "bigint") return v;
  if (typeof v === "number") return BigInt(Math.trunc(v));
  if (typeof v === "string") {
    const t = v.trim();
    if (!/^-?\d+$/.test(t)) throw new Error("无法把 " + langGoQuote(v) + " 转为 int");
    return BigInt(t);
  }
  throw new Error("int() 不支持 " + langGoTypeName(v));
}
function langGoFloat(v) {
  if (typeof v === "number") return v;
  if (typeof v === "bigint") return Number(v);
  if (typeof v === "string") {
    const f = Number(v.trim());
    if (v.trim() === "" || Number.isNaN(f)) throw new Error("无法把 " + langGoQuote(v) + " 转为 float");
    return f;
  }
  throw new Error("float() 不支持 " + langGoTypeName(v));
}

// 映射：类型前缀键（int 1 与 string "1" 不撞）+ #o: 原始键条目（首插序）
function langGoKeyString(k) {
  if (typeof k === "bigint") return "i:" + k.toString(10);
  if (typeof k === "string") return "s:" + k;
  if (typeof k === "boolean") return "b:" + (k ? "true" : "false");
  throw new Error("映射键须为 int/string/bool，得到 " + langGoTypeName(k));
}
function langGoMapSet(m, k, v) {
  if (!(m instanceof Map)) throw new Error(langGoTypeName(m) + " 不可索引赋值");
  const ks = langGoKeyString(k);
  m.set(ks, langGoCopy(v));
  if (!m.has("#o:" + ks)) m.set("#o:" + ks, k);
}
function langGoMapGet(m, k) {
  if (!(m instanceof Map)) throw new Error(langGoTypeName(m) + " 不可索引");
  const ks = langGoKeyString(k);
  if (!m.has(ks)) throw new Error("映射无键 " + langGoFmt(k, true) + "（用 has() 探测）");
  return langGoCopy(m.get(ks));
}
function langGoKeys(m) {
  if (!(m instanceof Map)) throw new Error("keys 需要映射，得到 " + langGoTypeName(m));
  const out = [];
  for (const k of m.keys()) {
    if (typeof k === "string" && k.startsWith("#o:")) out.push(m.get(k));
  }
  return out;
}
function langGoNewMap(...kvs) {
  const m = new Map();
  for (const kv of kvs) langGoMapSet(m, kv.K, kv.V);
  return m;
}
function langGoValues(m) {
  if (!(m instanceof Map)) throw new Error("values 需要映射，得到 " + langGoTypeName(m));
  const out = [];
  for (const k of langGoKeys(m)) out.push(langGoMapGet(m, k));
  return out;
}
function langGoHas(m, k) {
  if (!(m instanceof Map)) throw new Error("has 需要映射，得到 " + langGoTypeName(m));
  return m.has(langGoKeyString(k));
}
// push 返回数组本身（TS/Go 证据同口径；IR has_ret=1 会把调用当值消费）
function langGoPush(arr, ...vs) {
  if (!Array.isArray(arr)) throw new Error("push 需要数组，得到 " + langGoTypeName(arr));
  for (const v of vs) arr.push(langGoCopy(v));
  return arr;
}
// ArrayPush（数组 `+=`）：rhs 为数组 → 原位拼接（逐元素追加 rhs 全部元素，
// 与 IrMachine ArrayPush 同语义；错误消息逐字对齐解释器）。单元素追加是
// push 内建（langGoPush），两者不得混用。
function langGoPushArr(arr, rhs) {
  arr = langGoUnref(arr);
  if (!Array.isArray(arr)) throw new Error("数组 += 需要数组，得到 " + langGoTypeName(arr));
  if (!Array.isArray(rhs)) throw new Error("数组 += 需要数组，得到 " + langGoTypeName(rhs));
  for (const v of rhs) arr.push(langGoCopy(v));
  return arr;
}
function langGoAssert(cond, ...msg) {
  if (typeof cond !== "boolean") throw new Error("条件须为 bool，得到 " + langGoTypeName(cond));
  if (cond !== true) throw new Error(msg.length ? "assert 失败: " + langGoFmt(msg[0], true) : "assert 失败");
}
function langGoErr(msg) {
  if (typeof msg !== "string") throw new Error("err 需要字符串，得到 " + langGoTypeName(msg));
  return { __langGoErr: true, msg };
}
function langGoErrMsg(e) { return e.msg; }
function langGoBytes(v) {
  if (typeof v !== "string") throw new Error("bytes 需要字符串，得到 " + langGoTypeName(v));
  return [...new TextEncoder().encode(v)].map(b => BigInt(b));
}
function langGoChar(v) {
  if (typeof v !== "bigint") throw new Error("char 需要 int，得到 " + langGoTypeName(v));
  return String.fromCodePoint(Number(v));
}
function langGoIsNone(v) { return !!(v && v.__langGoEnum && v.tag === "None"); }

// ———— 运算（int 回绕 + 移位位数检查，与解释器一致） ————

function langGoWrap64(v) {
  v &= 0xffffffffffffffffn;
  return v >= 0x8000000000000000n ? v - 0x10000000000000000n : v;
}
function langGoArith(op, a, b) {
  if (op === "+" && typeof a === "string") {
    if (typeof b !== "string") throw new Error(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较（无隐式转换）");
    return a + b;
  }
  if (typeof a === "bigint") {
    if (typeof b !== "bigint") throw new Error(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较（无隐式转换）");
    if (op === "+") return langGoWrap64(a + b);
    if (op === "-") return langGoWrap64(a - b);
    if (op === "*") return langGoWrap64(a * b);
    if (op === "/") {
      if (b === 0n) throw new Error("整数除零");
      return a / b;
    }
    if (op === "%") {
      if (b === 0n) throw new Error("整数除零");
      return a % b;
    }
  }
  if (typeof a === "number") {
    if (typeof b !== "number") throw new Error(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较（无隐式转换）");
    if (op === "+") return a + b;
    if (op === "-") return a - b;
    if (op === "*") return a * b;
    if (op === "/") {
      if (b === 0) throw new Error("浮点除零");
      return a / b;
    }
    throw new Error("% 不能用于 float");
  }
  throw new Error(langGoTypeName(a) + " 不支持运算 " + op);
}
function langGoBits(op, a, b) {
  if (typeof a !== "bigint" || typeof b !== "bigint") {
    throw new Error(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可位运算（须为 int）");
  }
  if (op === "&") return a & b;
  if (op === "|") return a | b;
  if (op === "^") return a ^ b;
  if (b < 0n || b >= 64n) throw new Error("移位位数须在 0..63，得到 " + b.toString());
  if (op === "<<") return langGoWrap64(a << b);
  return a >> b;
}
function langGoNeg(v) {
  if (typeof v === "bigint" || typeof v === "number") return -v;
  throw new Error(langGoTypeName(v) + " 不支持运算 -");
}
function langGoNot(v) {
  if (typeof v !== "boolean") throw new Error(langGoTypeName(v) + " 不支持运算 !");
  return !v;
}
function langGoEq(a, b) {
  if (typeof a === "bigint" || typeof a === "number" || typeof a === "boolean" || typeof a === "string") {
    if (typeof a !== typeof b) throw new Error(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较（无隐式转换）");
    return a === b;
  }
  if (a && a.__langGoEnum && b && b.__langGoEnum) {
    if (a.tag !== b.tag || a.vals.length !== b.vals.length) return false;
    return a.vals.every((x, i) => langGoEq(x, b.vals[i]));
  }
  throw new Error(langGoTypeName(a) + " 不可用 == 比较");
}
function langGoCompare(a, b) {
  if ((typeof a === "bigint" || typeof a === "number") && (typeof b === "bigint" || typeof b === "number")) {
    return a < b ? -1 : a > b ? 1 : 0;
  }
  if (typeof a === "string" && typeof b === "string") {
    return a < b ? -1 : a > b ? 1 : 0;
  }
  throw new Error(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小（无隐式转换）");
}
function langGoTruthy(v) {
  if (typeof v !== "boolean") throw new Error("条件须为 bool，得到 " + langGoTypeName(v));
  return v;
}

// ———— 索引/切片/字段/枚举载荷（读出皆副本） ————

// langGoUnref 解引用包装：装箱槽直压的地址组件携带引用（与 IrMachine
// deref_value 同位点；Ref 不嵌套，单层足够，上限防御引用环）
function langGoUnref(v) {
  for (let i = 0; i < 64 && v && v.__langGoRef; i++) v = v.V;
  if (v && v.__langGoRef) throw new Error("引用穿透过深（疑似引用环）");
  return v;
}

function langGoIndex(obj, idx) {
  obj = langGoUnref(obj);
  if (Array.isArray(obj)) {
    const i = Number(idx);
    if (i < 0 || i >= obj.length) throw new Error("索引 " + i + " 越界（长度 " + obj.length + "）");
    return langGoCopy(obj[i]);
  }
  if (obj instanceof Map) return langGoMapGet(obj, idx);
  if (typeof obj === "string") {
    const chars = [...obj];
    const i = Number(idx);
    if (i < 0 || i >= chars.length) throw new Error("索引 " + i + " 越界（长度 " + chars.length + "）");
    return chars[i];
  }
  throw new Error(langGoTypeName(obj) + " 不可索引");
}
// 写链地址组件的 Ref 读形：不落副本（与 IrMachine 的 Place 溯源操作数同
// 语义）——中间组件读出底层存储本体，最终 set 在原对象上写穿。仅供 codegen
// 的赋值链发射使用；普通读走 langGoIndex（读出皆副本）。
function langGoIndexRef(obj, idx) {
  obj = langGoUnref(obj);
  if (Array.isArray(obj)) {
    const i = Number(idx);
    if (i < 0 || i >= obj.length) throw new Error("索引 " + i + " 越界（长度 " + obj.length + "）");
    return obj[i];
  }
  if (obj instanceof Map) {
    const ks = langGoKeyString(idx);
    if (!obj.has(ks)) throw new Error("映射无键 " + langGoFmt(idx, true) + "（用 has() 探测）");
    return obj.get(ks);
  }
  throw new Error(langGoTypeName(obj) + " 不可索引");
}
function langGoSetIndex(obj, idx, v) {
  obj = langGoUnref(obj);
  if (Array.isArray(obj)) {
    const i = Number(idx);
    if (i < 0 || i >= obj.length) throw new Error("索引 " + i + " 越界（长度 " + obj.length + "）");
    obj[i] = langGoCopy(v);
    return;
  }
  if (obj instanceof Map) { langGoMapSet(obj, idx, v); return; }
  if (typeof obj === "string") throw new Error("字符串不可变");
  throw new Error(langGoTypeName(obj) + " 不可索引赋值");
}
function langGoSlice(obj, lo, hi) {
  const i = Number(lo), j = Number(hi);
  if (Array.isArray(obj)) {
    if (i < 0 || j < i || j > obj.length) throw new Error("切片 [" + i + ":" + j + "] 越界（长度 " + obj.length + "）");
    return obj.slice(i, j);
  }
  if (typeof obj === "string") {
    const chars = [...obj];
    if (i < 0 || j < i || j > chars.length) throw new Error("切片 [" + i + ":" + j + "] 越界（长度 " + chars.length + "）");
    return chars.slice(i, j).join("");
  }
  throw new Error(langGoTypeName(obj) + " 不可切片");
}
function langGoField(obj, name) {
  obj = langGoUnref(obj);
  if (obj && obj.__langGoStruct) {
    if (!Object.prototype.hasOwnProperty.call(obj, name)) throw new Error(obj.__langGoStruct + " 没有字段 " + name);
    return langGoCopy(obj[name]);
  }
  if (obj && obj.__langGoErr) {
    if (name !== "msg") throw new Error("error 没有字段 " + name);
    return obj.msg;
  }
  throw new Error(langGoTypeName(obj) + " 没有字段 " + name);
}
// 写链地址组件的 Ref 读形（不落副本，语义见 langGoIndexRef）。
function langGoFieldRef(obj, name) {
  obj = langGoUnref(obj);
  if (obj && obj.__langGoStruct) {
    if (!Object.prototype.hasOwnProperty.call(obj, name)) throw new Error(obj.__langGoStruct + " 没有字段 " + name);
    return obj[name];
  }
  throw new Error(langGoTypeName(obj) + " 没有字段 " + name);
}
function langGoSetField(obj, name, v) {
  obj = langGoUnref(obj);
  if (obj && obj.__langGoStruct) {
    if (!Object.prototype.hasOwnProperty.call(obj, name)) throw new Error(obj.__langGoStruct + " 没有字段 " + name);
    obj[name] = langGoCopy(v);
    return;
  }
  throw new Error(langGoTypeName(obj) + " 没有字段 " + name);
}
function langGoEnumVal(enumName, tag, ...vals) {
  return { __langGoEnum: true, enumName, tag, vals: vals.map(langGoCopy) };
}
function langGoEnumTag(v) {
  if (!v || !v.__langGoEnum) throw new Error("match 目标须为 enum，得到 " + langGoTypeName(v));
  return v.tag;
}
function langGoEnumPayload(v, i) {
  if (!v || !v.__langGoEnum) throw new Error("取载荷目标须为 enum，得到 " + langGoTypeName(v));
  if (i < 0 || i >= v.vals.length) throw new Error("载荷 " + i + " 越界（case " + v.tag + "）");
  return langGoCopy(v.vals[i]);
}

// ———— `?` 传播（Result 以 enum Tag Ok/Err 表示） ————

function langGoIsErr(v) { return !!(v && v.__langGoEnum && v.tag === "Err"); }
function langGoTryOk(v) {
  if (!v || !v.__langGoEnum || v.tag !== "Ok") throw new Error("? 须作用于 Result，得到 " + langGoTypeName(v));
  return langGoCopy(v.vals[0]);
}
function langGoTryErrOf(v) {
  if (!v || !v.__langGoEnum || v.tag !== "Err") throw new Error("? 须作用于 Result，得到 " + langGoTypeName(v));
  const e = v.vals[0];
  if (!e || !e.__langGoErr) throw new Error("Err 载荷须为 error");
  return e;
}

// ———— 引用装箱/闭包/chan/迭代（同步确定子集） ————

function langGoBox(v) { return { __langGoRef: true, V: langGoCopy(v) }; }
function langGoDeref(v) { return v && v.__langGoRef ? langGoCopy(v.V) : v; }
function langGoRefSet(r, v) {
  if (!r || !r.__langGoRef) throw new Error("写穿目标须为引用，得到 " + langGoTypeName(r));
  r.V = langGoCopy(v);
}
function langGoCallClosure(c, ...args) {
  if (!c || !c.__langGoClosure) throw new Error("调用目标须为函数值，得到 " + langGoTypeName(c));
  return c.fn(...c.env, ...args);
}
// CHAN-MAP-005：chan = 队列模拟；recv 空 = Err"channel 空"（同步子集下无人 send）
function langGoNewChan() { return { __langGoChan: true, items: [] }; }
function langGoSend(ch, v) {
  if (!ch || !ch.__langGoChan) throw new Error("send 需要通道，得到 " + langGoTypeName(ch));
  if (ch.items.length >= 1024) throw new Error("channel 缓冲已满（1024）");
  ch.items.push(langGoCopy(v));
}
function langGoRecv(ch) {
  if (!ch || !ch.__langGoChan) throw new Error("recv 需要通道，得到 " + langGoTypeName(ch));
  if (ch.items.length === 0) return langGoEnumVal("Result", "Err", langGoErr("channel 空"));
  return langGoEnumVal("Result", "Ok", ch.items.shift());
}
function langGoIterNew(v) {
  if (Array.isArray(v)) return { Items: v.slice(), I: 0 };
  if (v instanceof Map) return { Items: langGoKeys(v), I: 0 };
  if (typeof v === "string") return { Items: [...v], I: 0 };
  if (v && v.__langGoRange !== undefined) {
    const items = [];
    for (let i = v.Lo; i < v.Hi; i++) items.push(i);
    return { Items: items, I: 0 };
  }
  throw new Error(langGoTypeName(v) + " 不可迭代");
}

// ———— 标准库委托：json（RUNTIME-JSON-001/002 硬契约） ————

function langGoJSONParse(s) {
  if (typeof s !== "string") throw new Error("jsonParse 需要字符串，得到 " + langGoTypeName(s));
  let raw;
  try { raw = JSON.parse(s); } catch (e) { throw new Error("JSON 解析失败: " + langGoJSONParseErr(e)); }
  return langGoJSONFrom(raw);
}
function langGoJSONParseErr(e) {
  const msg = e instanceof Error ? e.message : String(e);
  if (msg.includes("Unexpected end") || msg.includes("unexpected end")) return "unexpected end of JSON input";
  const m = /Unexpected token (.)/.exec(msg);
  if (m !== null) return "invalid character " + m[1] + " looking for beginning of value";
  return "invalid character looking for beginning of value";
}
function langGoJSONFrom(raw) {
  if (raw === null) return langGoEnumVal("Option", "None");
  if (Array.isArray(raw)) return raw.map(langGoJSONFrom);
  if (typeof raw === "object") {
    const keys = Object.keys(raw).sort(langGoJSONKeyLess);
    const m = new Map();
    for (const k of keys) langGoMapSet(m, k, langGoJSONFrom(raw[k]));
    return m;
  }
  return raw;
}
function langGoJSONTo(v) {
  if (v === null || v === undefined) return { jsonNull: true };
  if (typeof v === "bigint") return { jsonInt: v };
  if (typeof v === "number") return { jsonFloat: v };
  if (typeof v === "boolean" || typeof v === "string") return v;
  if (Array.isArray(v)) return v.map(langGoJSONTo);
  if (v instanceof Map) {
    const out = {};
    for (const k of langGoKeys(v)) {
      if (typeof k !== "string") throw new Error("JSON 对象键须为 string，得到 " + langGoTypeName(k));
      out[k] = langGoJSONTo(langGoMapGet(v, k));
    }
    return out;
  }
  if (v && v.__langGoEnum) {
    if (v.tag === "None") return { jsonNull: true };
    throw new Error(v.tag + " 不可 JSON 序列化");
  }
  if (v && v.__langGoErr) throw new Error("error 不可 JSON 序列化");
  throw new Error(langGoTypeName(v) + " 不可 JSON 序列化");
}
// Go encoding/json 形：abs ∈ [1e-6,1e21) 定点否则 e 形负指数去前导零；-0 → 0
function langGoJSONFloat(f) {
  if (Number.isNaN(f) || !Number.isFinite(f)) {
    throw new Error("JSON 序列化失败: json: unsupported value: " + (Number.isNaN(f) ? "NaN" : f > 0 ? "+Inf" : "-Inf"));
  }
  if (f === 0) return "0";
  const a = Math.abs(f);
  if (a < 1e-6 || a >= 1e21) {
    const m = /^(\d)(?:\.(\d+))?[eE]([+-]\d+)$/.exec(f.toExponential());
    const digits = (m[1] + (m[2] || "")).replace(/0+$/, "");
    const exp = Number(m[3]);
    const mant = digits[0] + (digits.length > 1 ? "." + digits.slice(1) : "");
    return mant + "e" + (exp < 0 ? "-" : "+") + String(Math.abs(exp));
  }
  return langGoFloatStr(f);
}
// 键码点序（非 JS UTF-16 序）
function langGoJSONKeyLess(a, b) {
  const ca = [...a], cb = [...b];
  for (let i = 0; i < ca.length && i < cb.length; i++) {
    if (ca[i] !== cb[i]) return ca[i].codePointAt(0) < cb[i].codePointAt(0) ? -1 : 1;
  }
  return ca.length - cb.length;
}
// HTML 转义开启（< > & → \u003c \u003e \u0026）
function langGoJSONStr(s) {
  let out = '"';
  for (const r of s) {
    const cp = r.codePointAt(0);
    if (cp === 34) out += '\\"';
    else if (cp === 92) out += "\\\\";
    else if (cp === 8) out += "\\b";
    else if (cp === 12) out += "\\f";
    else if (cp === 10) out += "\\n";
    else if (cp === 13) out += "\\r";
    else if (cp === 9) out += "\\t";
    else if (cp < 32) out += "\\u" + cp.toString(16).padStart(4, "0");
    else if (cp === 60) out += "\\u003c";
    else if (cp === 62) out += "\\u003e";
    else if (cp === 38) out += "\\u0026";
    else out += r;
  }
  return out + '"';
}
function langGoJSONMarshal(j) {
  if (j === null) return "null";
  if (typeof j === "object" && "jsonNull" in j) return "null";
  if (typeof j === "object" && "jsonInt" in j) return j.jsonInt.toString(10);
  if (typeof j === "object" && "jsonFloat" in j) return langGoJSONFloat(j.jsonFloat);
  if (typeof j === "boolean") return j ? "true" : "false";
  if (typeof j === "string") return langGoJSONStr(j);
  if (Array.isArray(j)) return "[" + j.map(langGoJSONMarshal).join(",") + "]";
  if (typeof j === "object") {
    const keys = Object.keys(j).sort(langGoJSONKeyLess);
    const parts = keys.map(k => langGoJSONStr(k) + ":" + langGoJSONMarshal(j[k]));
    return "{" + parts.join(",") + "}";
  }
  return "null";
}
function langGoJSONStringify(v) { return langGoJSONMarshal(langGoJSONTo(v)); }
"##;
