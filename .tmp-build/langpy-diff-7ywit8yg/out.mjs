// ———— langGo 运行时镜像（→JS；由 langGo 转译器生成，勿手改） ————

function langGoCopy(v) {
  if (v === null || typeof v !== "object") return v;
  if (Array.isArray(v) || v instanceof Map) return v; // 数组/映射共享
  if (typeof v === "function") return v;
  if (v.__langGoEnum || v.__langGoOpt || v.__langGoRes || v.__langGoErr || v.__langGoRange) return v;
  const out = {};
  for (const k of Object.keys(v)) out[k] = langGoCopy(v[k]);
  return out;
}

function langGoTypeName(v) {
  if (typeof v === "bigint") return "int";
  if (typeof v === "number") return "float";
  if (typeof v === "boolean") return "bool";
  if (typeof v === "string") return "string";
  if (Array.isArray(v)) return "数组";
  if (v instanceof Map) return "映射";
  if (v && v.__langGoEnum) return "enum";
  if (v && v.__langGoErr) return "error";
  if (v && v.__langGoRange) return "区间";
  if (v && v.__langGoStruct) return v.__langGoStruct;
  return "未知值";
}

function langGoFmt(v, top) {
  if (typeof v === "bigint") return v.toString();
  if (typeof v === "number") return String(v);
  if (typeof v === "boolean") return String(v);
  if (typeof v === "string") return top ? v : JSON.stringify(v);
  if (v === null || v === undefined) return "()";
  if (typeof v === "function") return v.__langGoName ? "<fn " + v.__langGoName + ">" : "<fn 匿名>";
  if (Array.isArray(v)) return "[" + v.map(x => langGoFmt(x, false)).join(", ") + "]";
  if (v instanceof Map) {
    const parts = [];
    for (const k of langGoKeys(v)) parts.push(langGoFmt(k, false) + ": " + langGoFmt(langGoMapGet(v, k), false));
    return "{" + parts.join(", ") + "}";
  }
  if (v.__langGoRange !== undefined) return v.lo.toString() + ".." + v.hi.toString();
  if (v.__langGoEnum) {
    if (v.vals.length === 0) return v.tag;
    return v.tag + "(" + v.vals.map(x => langGoFmt(x, false)).join(", ") + ")";
  }
  if (v.__langGoOpt) return v.has ? "Some(" + langGoFmt(v.v, false) + ")" : "None";
  if (v.__langGoRes) return v.ok ? "Ok(" + langGoFmt(v.v, false) + ")" : "Err(" + langGoFmt(v.e, false) + ")";
  if (v.__langGoErr) return "error{msg: " + JSON.stringify(v.msg) + "}";
  if (v.__langGoStruct) {
    const parts = Object.keys(v).filter(k => k !== "__langGoStruct").map(k => k + ": " + langGoFmt(v[k], false));
    return v.__langGoStruct + "{" + parts.join(", ") + "}";
  }
  return "?";
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
  if (a && a.__langGoOpt && b && b.__langGoOpt) {
    if (a.has !== b.has) return false;
    return !a.has || langGoEq(a.v, b.v);
  }
  if (a && a.__langGoRes && b && b.__langGoRes) {
    if (a.ok !== b.ok) return false;
    return a.ok ? langGoEq(a.v, b.v) : langGoEq(a.e, b.e);
  }
  throw new Error(langGoTypeName(a) + " 不可用 == 比较");
}

function langGoCompare(a, b) {
  if (typeof a === "bigint" || typeof a === "number") {
    if (typeof b !== "bigint" && typeof b !== "number") throw new Error("比较类型不符");
    return a < b ? -1 : a > b ? 1 : 0;
  }
  if (typeof a === "string") {
    if (typeof b !== "string") throw new Error("比较类型不符");
    return a < b ? -1 : a > b ? 1 : 0;
  }
  throw new Error(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小");
}

function langGoStrVal(v) {
  if (typeof v !== "string") throw new Error("+ 需要字符串，得到 " + langGoTypeName(v));
  return v;
}

function langGoTruthy(v) {
  if (typeof v !== "boolean") throw new Error("条件须为 bool，得到 " + langGoTypeName(v));
  return v;
}

function langGoFmtArgs(args) { return args.map(a => langGoFmt(a, true)).join(" "); }
function langGoPrintln(...args) { console.log(langGoFmtArgs(args)); }
function langGoPrint(...args) { process.stdout.write(langGoFmtArgs(args)); }

function langGoLen(v) {
  if (typeof v === "string") return BigInt([...v].length);
  if (Array.isArray(v)) return BigInt(v.length);
  if (v instanceof Map) {
    let n = 0;
    for (const k of v.keys()) if (!k.startsWith("__langGoOrig:")) n++;
    return BigInt(n);
  }
  if (v && v.__langGoRange !== undefined) return v.hi > v.lo ? v.hi - v.lo : 0n;
  throw new Error("len 不支持 " + langGoTypeName(v));
}

function langGoStr(v) { return langGoFmt(v, true); }
function langGoInt(v) {
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
  throw new Error("int() 不支持 " + langGoTypeName(v));
}
function langGoFloat(v) {
  if (typeof v === "number") return v;
  if (typeof v === "bigint") return Number(v);
  if (typeof v === "string") {
    const f = Number(v.trim());
    if (v.trim() === "" || Number.isNaN(f)) throw new Error("无法把 " + JSON.stringify(v) + " 转为 float");
    return f;
  }
  throw new Error("float() 不支持 " + langGoTypeName(v));
}

function langGoNewMap(...kvs) {
  const m = new Map();
  for (const [k, val] of kvs) langGoMapSet(m, k, val);
  return m;
}
function langGoKeyString(k) {
  if (typeof k === "bigint" || typeof k === "number" || typeof k === "boolean" || typeof k === "string") return String(k);
  throw new Error("映射键须为 int/string/bool");
}
function langGoMapSet(m, k, v) {
  if (!(m instanceof Map)) throw new Error("只能对映射赋值");
  m.set(langGoKeyString(k), v);
  m.set("__langGoOrig:" + langGoKeyString(k), k); // 保留原始键供迭代
}
function langGoMapGet(m, k) {
  if (!(m instanceof Map)) throw new Error(langGoTypeName(m) + " 不可索引");
  const ks = langGoKeyString(k);
  if (!m.has(ks)) throw new Error("映射无键 " + langGoFmt(k, true) + "（用 has() 探测）");
  return m.get(ks);
}
function langGoKeys(m) {
  if (!(m instanceof Map)) throw new Error("keys 需要映射");
  const out = [];
  for (const k of m.keys()) if (!k.startsWith("__langGoOrig:")) out.push(m.get("__langGoOrig:" + k));
  return out;
}
function langGoHas(m, k) {
  if (!(m instanceof Map)) throw new Error("has 需要映射");
  return m.has(langGoKeyString(k));
}
function langGoPush(arr, ...vs) {
  if (!Array.isArray(arr)) throw new Error("push 需要数组");
  arr.push(...vs);
}
function langGoAssert(cond, ...msg) {
  if (cond !== true) throw new Error(msg.length ? "assert 失败: " + langGoFmt(msg[0], true) : "assert 失败");
}
function langGoErr(msg) { return { __langGoErr: true, msg }; }
function langGoEnumVal(tag, vals) { return { __langGoEnum: true, tag, vals }; }
function langGoErrMsg(e) { return e.msg; }
function langGoNone() { return { __langGoOpt: true, has: false, v: undefined }; }

// ———— 运算与索引 ————

// langGoDiv 整数截断除法（BigInt / 向零）；float 真除
function langGoDiv(a, b) {
  if (typeof a === "bigint") {
    if (b === 0n) throw new Error("整数除零");
    return a / b;
  }
  if (b === 0) throw new Error("浮点除零");
  return a / b;
}
function langGoIndex(obj, idx) {
  if (Array.isArray(obj)) {
    const i = Number(idx);
    if (i < 0 || i >= obj.length) throw new Error("索引 " + i + " 越界（长度 " + obj.length + "）");
    return obj[i];
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
function langGoSetIndex(obj, idx, v) {
  if (Array.isArray(obj)) {
    const i = Number(idx);
    if (i < 0 || i >= obj.length) throw new Error("索引 " + i + " 越界（长度 " + obj.length + "）");
    obj[i] = v;
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
function langGoValues(m) {
  if (!(m instanceof Map)) throw new Error("values 需要映射");
  const out = [];
  for (const k of langGoKeys(m)) out.push(langGoMapGet(m, k));
  return out;
}

function langGoBytes(v) {
  if (typeof v !== "string") throw new Error("bytes 需要字符串");
  return [...new TextEncoder().encode(v)].map(b => BigInt(b));
}
function langGoChar(v) { return String.fromCodePoint(Number(v)); }
function langGoAddIndex(obj, idx, v, op) {
  if (obj instanceof Map) {
    const ks = langGoKeyString(idx);
    const old = obj.get(ks);
    let nv;
    switch (op) {
      case "+=": nv = langGoAdd(old, v); break;
      default: throw new Error("映射仅支持 +=");
    }
    langGoMapSet(obj, idx, nv);
    return;
  }
  if (Array.isArray(obj)) {
    const i = Number(idx);
    switch (op) {
      case "+=": obj[i] = langGoAdd(obj[i], v); break;
      case "-=": obj[i] = langGoSub(obj[i], v); break;
      case "*=": obj[i] = langGoMul(obj[i], v); break;
      case "/=": obj[i] = langGoDiv(obj[i], v); break;
    }
    return;
  }
  throw new Error("该类型不可索引复合赋值");
}
function langGoAdd(a, b) {
  if (typeof a === 'bigint' && typeof b === 'bigint') return a + b;
  if (typeof a === 'number' && typeof b === 'number') return a + b;
  if (typeof a === 'string' && typeof b === 'string') return a + b;
  throw new Error("+= 类型不符");
}
function langGoSub(a, b) {
  if (typeof a === 'bigint' && typeof b === 'bigint') return a - b;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  throw new Error("-= 类型不符");
}
function langGoMul(a, b) {
  if (typeof a === 'bigint' && typeof b === 'bigint') return a * b;
  if (typeof a === 'number' && typeof b === 'number') return a * b;
  throw new Error("*= 类型不符");
}
// —— 并发：chan（decisions #59/#63）。recv 语义为阻塞直到有值；→JS 的 go 在语句处
// 同步执行完（decisions #57），故 recv 执行时要么有值、要么永远无人 send（死锁 → Err）
function langGoNewChan() { return { __langGoChan: true, items: [] }; }
function langGoChanSend(ch, v) {
  if (!ch || !ch.__langGoChan) throw new Error("send 需要通道");
  ch.items.push(v);
}
function langGoChanRecv(ch) {
  if (!ch || !ch.__langGoChan) throw new Error("recv 需要通道");
  if (ch.items.length === 0) return { __langGoRes: true, ok: false, v: undefined, e: langGoErr("channel 空") };
  return { __langGoRes: true, ok: true, v: ch.items.shift(), e: undefined };
}
function langGoIsNone(v) { return !!(v && v.__langGoOpt && !v.has); }

// ———— 标准库委托：json ————

function langGoJSONParse(s) {
  if (typeof s !== "string") throw new Error("jsonParse 需要字符串");
  let raw;
  try { raw = JSON.parse(s); } catch (e) { throw new Error("JSON 解析失败: " + e.message); }
  return langGoJSONFrom(raw);
}
function langGoJSONFrom(raw) {
  if (raw === null) return langGoNone();
  if (Array.isArray(raw)) return raw.map(langGoJSONFrom);
  if (typeof raw === "object") {
    const keys = Object.keys(raw).sort();
    const m = new Map();
    for (const k of keys) langGoMapSet(m, k, langGoJSONFrom(raw[k]));
    return m;
  }
  return raw; // number/boolean/string 原样
}
function langGoJSONTo(v) {
  if (v === null || v === undefined) return null;
  if (typeof v === "bigint") return Number(v);
  if (typeof v === "number" || typeof v === "boolean" || typeof v === "string") return v;
  if (Array.isArray(v)) return v.map(langGoJSONTo);
  if (v instanceof Map) {
    const out = {};
    const keys = langGoKeys(v).slice().sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
    for (const k of keys) out[typeof k === "string" ? k : langGoFmt(k, true)] = langGoJSONTo(langGoMapGet(v, k));
    return out;
  }
  if (v.__langGoOpt && !v.has) return null;
  throw new Error(langGoTypeName(v) + " 不可 JSON 序列化");
}
function langGoJSONStringify(v) { return JSON.stringify(langGoJSONTo(v)); }

// ———— 标准库委托：http（Node fetch；4xx/5xx 也是 Ok） ————

const langGoHTTP = {
  get: async (url, headers) => {
    if (typeof url !== "string") throw new Error("http.get 的 url 须为 string");
    let resp;
    try {
      const init = { headers: {} };
      if (headers instanceof Map) for (const k of langGoKeys(headers)) init.headers[k] = langGoMapGet(headers, k);
      resp = await fetch(url, init);
    } catch (e) {
      return { __langGoRes: true, ok: false, v: undefined, e: langGoErr(String(e.message || e)) };
    }
    const body = await resp.text();
    return { __langGoRes: true, ok: true, v: { __langGoStruct: "HttpResp", status: BigInt(resp.status), body }, e: undefined };
  },
};

// ———— 动态迭代 ————

function langGoIter(v) {
  if (Array.isArray(v)) return v;
  if (v instanceof Map) return langGoKeys(v);
  if (typeof v === "string") return [...v];
  if (v && v.__langGoRange !== undefined) {
    const out = [];
    for (let i = v.lo; i < v.hi; i++) out.push(i);
    return out;
  }
  throw new Error("该类型不可迭代");
}
async function lf_main() {
  let x = langGoCopy(await (1n + 2n));
  langGoPrintln(x);
  langGoPrintln([langGoCopy(1n), langGoCopy(2n), langGoCopy(3n)]);
}

await lf_main();
