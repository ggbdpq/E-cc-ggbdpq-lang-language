package genjs

// jsPrelude 内联进每个 →JS 产物（ESM 无依赖，Node 18+ / 浏览器均可）。
// lingFmt/lingEq/lingCompare 必须与解释器 value.go 逐字节镜像——E2E 等价测试依赖。
//
// 类型映射（decisions #47）：
//
//	int → BigInt（64 位语义精确，位运算/掩码惯用法与解释器一致）
//	float → number
//	string → string；数组 → Array；映射 → Map（保插入序）
//	struct → plain object（带 __lingStruct 名字；赋值/传参处 lingCopy 值拷贝）
//	enum → {__lingEnum, tag, vals}；Option → {__lingOpt, has, v}；Result → {__lingRes, ok, v, e}
//	error → {__lingErr, msg}；range → {__lingRange, lo, hi}
const jsPrelude = `// ———— ling 运行时镜像（→JS；由 ling 转译器生成，勿手改） ————

function lingCopy(v) {
  if (v === null || typeof v !== "object") return v;
  if (Array.isArray(v) || v instanceof Map) return v; // 数组/映射共享
  if (typeof v === "function") return v;
  if (v.__lingEnum || v.__lingOpt || v.__lingRes || v.__lingErr || v.__lingRange) return v;
  const out = {};
  for (const k of Object.keys(v)) out[k] = lingCopy(v[k]);
  return out;
}

function lingTypeName(v) {
  if (typeof v === "bigint") return "int";
  if (typeof v === "number") return "float";
  if (typeof v === "boolean") return "bool";
  if (typeof v === "string") return "string";
  if (Array.isArray(v)) return "数组";
  if (v instanceof Map) return "映射";
  if (v && v.__lingEnum) return "enum";
  if (v && v.__lingErr) return "error";
  if (v && v.__lingRange) return "区间";
  if (v && v.__lingStruct) return v.__lingStruct;
  return "未知值";
}

function lingFmt(v, top) {
  if (typeof v === "bigint") return v.toString();
  if (typeof v === "number") return String(v);
  if (typeof v === "boolean") return String(v);
  if (typeof v === "string") return top ? v : JSON.stringify(v);
  if (v === null || v === undefined) return "()";
  if (typeof v === "function") return v.__lingName ? "<fn " + v.__lingName + ">" : "<fn 匿名>";
  if (Array.isArray(v)) return "[" + v.map(x => lingFmt(x, false)).join(", ") + "]";
  if (v instanceof Map) {
    const parts = [];
    for (const [k, val] of v) parts.push(lingFmt(k, false) + ": " + lingFmt(val, false));
    return "{" + parts.join(", ") + "}";
  }
  if (v.__lingRange !== undefined) return v.lo.toString() + ".." + v.hi.toString();
  if (v.__lingEnum) {
    if (v.vals.length === 0) return v.tag;
    return v.tag + "(" + v.vals.map(x => lingFmt(x, false)).join(", ") + ")";
  }
  if (v.__lingOpt) return v.has ? "Some(" + lingFmt(v.v, false) + ")" : "None";
  if (v.__lingRes) return v.ok ? "Ok(" + lingFmt(v.v, false) + ")" : "Err(" + lingFmt(v.e, false) + ")";
  if (v.__lingErr) return "error{msg: " + JSON.stringify(v.msg) + "}";
  if (v.__lingStruct) {
    const parts = Object.keys(v).filter(k => k !== "__lingStruct").map(k => k + ": " + lingFmt(v[k], false));
    return v.__lingStruct + "{" + parts.join(", ") + "}";
  }
  return "?";
}

function lingEq(a, b) {
  if (typeof a === "bigint" || typeof a === "number" || typeof a === "boolean" || typeof a === "string") {
    if (typeof a !== typeof b) throw new Error(lingTypeName(a) + " 与 " + lingTypeName(b) + " 不可比较（无隐式转换）");
    return a === b;
  }
  if (a && a.__lingEnum && b && b.__lingEnum) {
    if (a.tag !== b.tag || a.vals.length !== b.vals.length) return false;
    return a.vals.every((x, i) => lingEq(x, b.vals[i]));
  }
  if (a && a.__lingOpt && b && b.__lingOpt) {
    if (a.has !== b.has) return false;
    return !a.has || lingEq(a.v, b.v);
  }
  if (a && a.__lingRes && b && b.__lingRes) {
    if (a.ok !== b.ok) return false;
    return a.ok ? lingEq(a.v, b.v) : lingEq(a.e, b.e);
  }
  throw new Error(lingTypeName(a) + " 不可用 == 比较");
}

function lingCompare(a, b) {
  if (typeof a === "bigint" || typeof a === "number") {
    if (typeof b !== "bigint" && typeof b !== "number") throw new Error("比较类型不符");
    return a < b ? -1 : a > b ? 1 : 0;
  }
  if (typeof a === "string") {
    if (typeof b !== "string") throw new Error("比较类型不符");
    return a < b ? -1 : a > b ? 1 : 0;
  }
  throw new Error(lingTypeName(a) + " 与 " + lingTypeName(b) + " 不可比较大小");
}

function lingStrVal(v) {
  if (typeof v !== "string") throw new Error("+ 需要字符串，得到 " + lingTypeName(v));
  return v;
}

function lingTruthy(v) {
  if (typeof v !== "boolean") throw new Error("条件须为 bool，得到 " + lingTypeName(v));
  return v;
}

function lingFmtArgs(args) { return args.map(a => lingFmt(a, true)).join(" "); }
function lingPrintln(...args) { console.log(lingFmtArgs(args)); }
function lingPrint(...args) { process.stdout.write(lingFmtArgs(args)); }

function lingLen(v) {
  if (typeof v === "string") return BigInt([...v].length);
  if (Array.isArray(v)) return BigInt(v.length);
  if (v instanceof Map) {
    let n = 0;
    for (const k of v.keys()) if (!k.startsWith("__lingOrig:")) n++;
    return BigInt(n);
  }
  if (v && v.__lingRange !== undefined) return v.hi > v.lo ? v.hi - v.lo : 0n;
  throw new Error("len 不支持 " + lingTypeName(v));
}

function lingStr(v) { return lingFmt(v, true); }
function lingInt(v) {
  if (typeof v === "bigint") return v;
  if (typeof v === "number") {
    if (!Number.isInteger(v)) return BigInt(Math.trunc(v));
    return BigInt(v);
  }
  if (typeof v === "string") {
    const t = v.trim();
    if (!/^-?\d+$/.test(t)) throw new Error("无法把 " + JSON.stringify(v) + " 转为 int");
    return BigInt(t);
  }
  throw new Error("int() 不支持 " + lingTypeName(v));
}
function lingFloat(v) {
  if (typeof v === "number") return v;
  if (typeof v === "bigint") return Number(v);
  if (typeof v === "string") {
    const f = Number(v.trim());
    if (v.trim() === "" || Number.isNaN(f)) throw new Error("无法把 " + JSON.stringify(v) + " 转为 float");
    return f;
  }
  throw new Error("float() 不支持 " + lingTypeName(v));
}

function lingNewMap(...kvs) {
  const m = new Map();
  for (const [k, val] of kvs) lingMapSet(m, k, val);
  return m;
}
function lingKeyString(k) {
  if (typeof k === "bigint" || typeof k === "number" || typeof k === "boolean" || typeof k === "string") return String(k);
  throw new Error("映射键须为 int/string/bool");
}
function lingMapSet(m, k, v) {
  if (!(m instanceof Map)) throw new Error("只能对映射赋值");
  m.set(lingKeyString(k), v);
  m.set("__lingOrig:" + lingKeyString(k), k); // 保留原始键供迭代
}
function lingMapGet(m, k) {
  if (!(m instanceof Map)) throw new Error(lingTypeName(m) + " 不可索引");
  const ks = lingKeyString(k);
  if (!m.has(ks)) throw new Error("映射无键 " + lingFmt(k, true) + "（用 has() 探测）");
  return m.get(ks);
}
function lingKeys(m) {
  if (!(m instanceof Map)) throw new Error("keys 需要映射");
  const out = [];
  for (const k of m.keys()) if (!k.startsWith("__lingOrig:")) out.push(m.get("__lingOrig:" + k));
  return out;
}
function lingHas(m, k) {
  if (!(m instanceof Map)) throw new Error("has 需要映射");
  return m.has(lingKeyString(k));
}
function lingPush(arr, ...vs) {
  if (!Array.isArray(arr)) throw new Error("push 需要数组");
  arr.push(...vs);
}
function lingAssert(cond, ...msg) {
  if (cond !== true) throw new Error(msg.length ? "assert 失败: " + lingFmt(msg[0], true) : "assert 失败");
}
function lingErr(msg) { return { __lingErr: true, msg }; }
function lingEnumVal(tag, vals) { return { __lingEnum: true, tag, vals }; }
function lingErrMsg(e) { return e.msg; }
function lingNone() { return { __lingOpt: true, has: false, v: undefined }; }

// ———— 运算与索引 ————

// lingDiv 整数截断除法（BigInt / 向零）；float 真除
function lingDiv(a, b) {
  if (typeof a === "bigint") {
    if (b === 0n) throw new Error("整数除零");
    return a / b;
  }
  if (b === 0) throw new Error("浮点除零");
  return a / b;
}
function lingIndex(obj, idx) {
  if (Array.isArray(obj)) {
    const i = Number(idx);
    if (i < 0 || i >= obj.length) throw new Error("索引 " + i + " 越界（长度 " + obj.length + "）");
    return obj[i];
  }
  if (obj instanceof Map) return lingMapGet(obj, idx);
  if (typeof obj === "string") {
    const chars = [...obj];
    const i = Number(idx);
    if (i < 0 || i >= chars.length) throw new Error("索引 " + i + " 越界（长度 " + chars.length + "）");
    return chars[i];
  }
  throw new Error(lingTypeName(obj) + " 不可索引");
}
function lingSetIndex(obj, idx, v) {
  if (Array.isArray(obj)) {
    const i = Number(idx);
    if (i < 0 || i >= obj.length) throw new Error("索引 " + i + " 越界（长度 " + obj.length + "）");
    obj[i] = v;
    return;
  }
  if (obj instanceof Map) { lingMapSet(obj, idx, v); return; }
  if (typeof obj === "string") throw new Error("字符串不可变");
  throw new Error(lingTypeName(obj) + " 不可索引赋值");
}
function lingSlice(obj, lo, hi) {
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
  throw new Error(lingTypeName(obj) + " 不可切片");
}
function lingValues(m) {
  if (!(m instanceof Map)) throw new Error("values 需要映射");
  const out = [];
  for (const k of lingKeys(m)) out.push(lingMapGet(m, k));
  return out;
}

function lingBytes(v) {
  if (typeof v !== "string") throw new Error("bytes 需要字符串");
  return [...new TextEncoder().encode(v)].map(b => BigInt(b));
}
function lingChar(v) { return String.fromCodePoint(Number(v)); }
function lingAddIndex(obj, idx, v, op) {
  if (obj instanceof Map) {
    const ks = lingKeyString(idx);
    const old = obj.get(ks);
    let nv;
    switch (op) {
      case "+=": nv = lingAdd(old, v); break;
      default: throw new Error("映射仅支持 +=");
    }
    lingMapSet(obj, idx, nv);
    return;
  }
  if (Array.isArray(obj)) {
    const i = Number(idx);
    switch (op) {
      case "+=": obj[i] = lingAdd(obj[i], v); break;
      case "-=": obj[i] = lingSub(obj[i], v); break;
      case "*=": obj[i] = lingMul(obj[i], v); break;
      case "/=": obj[i] = lingDiv(obj[i], v); break;
    }
    return;
  }
  throw new Error("该类型不可索引复合赋值");
}
function lingAdd(a, b) {
  if (typeof a === 'bigint' && typeof b === 'bigint') return a + b;
  if (typeof a === 'number' && typeof b === 'number') return a + b;
  if (typeof a === 'string' && typeof b === 'string') return a + b;
  throw new Error("+= 类型不符");
}
function lingSub(a, b) {
  if (typeof a === 'bigint' && typeof b === 'bigint') return a - b;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  throw new Error("-= 类型不符");
}
function lingMul(a, b) {
  if (typeof a === 'bigint' && typeof b === 'bigint') return a * b;
  if (typeof a === 'number' && typeof b === 'number') return a * b;
  throw new Error("*= 类型不符");
}
// —— 并发：chan（decisions #59/#63）。recv 语义为阻塞直到有值；→JS 的 go 在语句处
// 同步执行完（decisions #57），故 recv 执行时要么有值、要么永远无人 send（死锁 → Err）
function lingNewChan() { return { __lingChan: true, items: [] }; }
function lingChanSend(ch, v) {
  if (!ch || !ch.__lingChan) throw new Error("send 需要通道");
  ch.items.push(v);
}
function lingChanRecv(ch) {
  if (!ch || !ch.__lingChan) throw new Error("recv 需要通道");
  if (ch.items.length === 0) return { __lingRes: true, ok: false, v: undefined, e: lingErr("channel 空") };
  return { __lingRes: true, ok: true, v: ch.items.shift(), e: undefined };
}
function lingIsNone(v) { return !!(v && v.__lingOpt && !v.has); }

// ———— 标准库委托：json ————

function lingJSONParse(s) {
  if (typeof s !== "string") throw new Error("jsonParse 需要字符串");
  let raw;
  try { raw = JSON.parse(s); } catch (e) { throw new Error("JSON 解析失败: " + e.message); }
  return lingJSONFrom(raw);
}
function lingJSONFrom(raw) {
  if (raw === null) return lingNone();
  if (Array.isArray(raw)) return raw.map(lingJSONFrom);
  if (typeof raw === "object") {
    const keys = Object.keys(raw).sort();
    const m = new Map();
    for (const k of keys) lingMapSet(m, k, lingJSONFrom(raw[k]));
    return m;
  }
  return raw; // number/boolean/string 原样
}
function lingJSONTo(v) {
  if (v === null || v === undefined) return null;
  if (typeof v === "bigint") return Number(v);
  if (typeof v === "number" || typeof v === "boolean" || typeof v === "string") return v;
  if (Array.isArray(v)) return v.map(lingJSONTo);
  if (v instanceof Map) {
    const out = {};
    for (const k of lingKeys(v)) out[typeof k === "string" ? k : lingFmt(k, true)] = lingJSONTo(lingMapGet(v, k));
    return out;
  }
  if (v.__lingOpt && !v.has) return null;
  throw new Error(lingTypeName(v) + " 不可 JSON 序列化");
}
function lingJSONStringify(v) { return JSON.stringify(lingJSONTo(v)); }

// ———— 标准库委托：http（Node fetch；4xx/5xx 也是 Ok） ————

const lingHTTP = {
  get: async (url, headers) => {
    if (typeof url !== "string") throw new Error("http.get 的 url 须为 string");
    let resp;
    try {
      const init = { headers: {} };
      if (headers instanceof Map) for (const k of lingKeys(headers)) init.headers[k] = lingMapGet(headers, k);
      resp = await fetch(url, init);
    } catch (e) {
      return { __lingRes: true, ok: false, v: undefined, e: lingErr(String(e.message || e)) };
    }
    const body = await resp.text();
    return { __lingRes: true, ok: true, v: { __lingStruct: "HttpResp", status: BigInt(resp.status), body }, e: undefined };
  },
};

// ———— 动态迭代 ————

function lingIter(v) {
  if (Array.isArray(v)) return v;
  if (v instanceof Map) return lingKeys(v);
  if (typeof v === "string") return [...v];
  if (v && v.__lingRange !== undefined) {
    const out = [];
    for (let i = v.lo; i < v.hi; i++) out.push(i);
    return out;
  }
  throw new Error("该类型不可迭代");
}
`
