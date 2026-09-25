// extern 自动绑定最小切片（decisions #61）：
// 解析目录内顶层非测试 .go 文件，提取导出函数签名，生成 extern "go" 声明块草案。
// TS 侧以轻量文本扫描实现（Go 参考实现用 go/parser）；本面不进 Conformance（FFI-001）。
//
// 类型映射：int/uint 系→int、float 系→float、string、bool、error 直映；
// []T → [T]；(T, error) 双返回 → Result[T, error]；指针/struct/接口/map/chan/func → 跳过。

import * as fs from "node:fs";
import * as path from "node:path";

interface GoFn {
  readonly name: string;
  readonly paramsText: string;
  readonly resultsText: string;
}

/** 解析目录内顶层非测试 .go 文件，返回 extern "go" 声明块草案 */
export function bindDir(dir: string): string {
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  const files: string[] = [];
  for (const e of entries) {
    const name = e.name;
    if (e.isDirectory() || !name.endsWith(".go") || name.endsWith("_test.go")) continue;
    files.push(path.join(dir, name));
  }
  files.sort();
  if (files.length === 0) throw new Error(`${dir}: 无 .go 文件`);

  const fns: { name: string; sig: string }[] = [];
  const skipped: string[] = [];
  for (const f of files) {
    const src = fs.readFileSync(f, "utf8");
    for (const gf of scanExportedFuncs(src)) {
      const r = goFuncToLangGo(gf);
      if (r.skip !== "") {
        skipped.push(`${gf.name}(${gf.paramsText}): ${r.skip}`);
        continue;
      }
      fns.push({ name: gf.name, sig: r.sig });
    }
  }
  if (fns.length === 0) throw new Error(`${dir}: 无可绑定的导出函数`);

  let b = "";
  b += '// extern "go" 声明草案 —— 由 langGo bind 生成，签名需人工审核后使用\n';
  b += "// 绑定面仅是声明；实现需在 prelude/宿主侧另行提供\n";
  if (skipped.length > 0) {
    b += "// 跳过:\n";
    for (const sk of skipped) b += `//   ${sk}\n`;
  }
  b += 'extern "go" {\n';
  for (const f of fns) b += `    ${f.sig}\n`;
  b += "}\n";
  return b;
}

/** 顶层导出函数扫描：func Name( ... ) ( ... ) { —— 基于括号配平的文本切分 */
function scanExportedFuncs(src: string): GoFn[] {
  const out: GoFn[] = [];
  const lines = src.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const m = /^func\s+([A-Z][A-Za-z0-9_]*)\s*\(/.exec(lines[i] ?? "");
    if (m === null) continue;
    // 方法（func (r T) Name）与无返回形式回退为文本收集
    const name = m[1]!;
    // 收集到 `{` 为止的签名文本（括号配平；跨行拼接）
    let sigText = "";
    let j = i;
    let depth = 0;
    let done = false;
    while (j < lines.length && !done) {
      const line = lines[j] ?? "";
      for (let k = 0; k < line.length; k++) {
        const ch = line[k]!;
        if (ch === "(") depth++;
        else if (ch === ")") depth--;
        else if (ch === "{" && depth === 0) {
          done = true;
          break;
        }
        sigText += ch;
      }
      if (!done) sigText += " ";
      j++;
    }
    if (!done) continue;
    // params = 第一个括号组；results = 其后括号组（可无）
    const p0 = sigText.indexOf("(");
    let p1 = -1;
    let d = 0;
    for (let k = p0; k < sigText.length; k++) {
      if (sigText[k] === "(") d++;
      else if (sigText[k] === ")") {
        d--;
        if (d === 0) {
          p1 = k;
          break;
        }
      }
    }
    if (p0 < 0 || p1 < 0) continue;
    const paramsText = sigText.slice(p0 + 1, p1).trim();
    let resultsText = sigText.slice(p1 + 1).trim();
    if (resultsText.startsWith("(")) {
      const r0 = resultsText.indexOf("(");
      let rd = 0;
      let r1 = -1;
      for (let k = r0; k < resultsText.length; k++) {
        if (resultsText[k] === "(") rd++;
        else if (resultsText[k] === ")") {
          rd--;
          if (rd === 0) {
            r1 = k;
            break;
          }
        }
      }
      resultsText = r1 >= 0 ? resultsText.slice(r0 + 1, r1).trim() : "";
    }
    out.push({ name, paramsText, resultsText });
    i = j - 1;
  }
  return out;
}

/** 单个导出函数 → langGo 声明；skip 非空表示不可绑定及原因 */
function goFuncToLangGo(gf: GoFn): { sig: string; skip: string } {
  const params: string[] = [];
  // Go 参数组形态：`a, b int` / `a int, s string` / `int`（无名）
  const groups = splitTop(gf.paramsText, ",");
  for (const g of groups) {
    const seg = g.trim();
    if (seg === "") continue;
    const parts = splitTop(seg, " ").filter((x) => x !== "");
    if (parts.length === 0) continue;
    const tn = goType(parts[parts.length - 1]!);
    if (tn.skip !== "") return { sig: "", skip: "参数类型不支持: " + parts[parts.length - 1]! };
    // 名在前、类型在尾（或整段为类型=无名）
    const names = parts.length > 1 ? parts.slice(0, -1) : [""];
    for (const nm of names) {
      const name = nm === "" ? `a${params.length + 1}` : nm;
      params.push(`${name}: ${tn.t}`);
    }
  }

  if (gf.resultsText === "") return { sig: fnSig(gf.name, params, ""), skip: "" };
  const results = splitTop(gf.resultsText, ",");
  if (results.length === 1) {
    const tn = goType(results[0]!.trim());
    if (tn.skip !== "") return { sig: "", skip: "返回类型不支持: " + results[0]!.trim() };
    return { sig: fnSig(gf.name, params, tn.t), skip: "" };
  }
  if (results.length === 2) {
    // (T, error) 惯例 → Result[T, error]
    if (results[1]!.trim() === "error") {
      const tn = goType(results[0]!.trim());
      if (tn.skip !== "") return { sig: "", skip: "返回类型不支持: " + results[0]!.trim() };
      return { sig: fnSig(gf.name, params, `Result[${tn.t}, error]`), skip: "" };
    }
    return { sig: "", skip: "双返回值仅支持 (T, error)" };
  }
  return { sig: "", skip: "多返回值（>2）" };
}

function fnSig(name: string, params: readonly string[], ret: string): string {
  let sig = `fn ${name}(${params.join(", ")})`;
  if (ret !== "") sig += `: ${ret}`;
  return sig;
}

interface TypeMapped {
  readonly t: string;
  readonly skip: string;
}

/** Go 类型文本 → langGo 类型名；不支持时给 skip 原因 */
function goType(t: string): TypeMapped {
  switch (t) {
    case "int":
    case "int8":
    case "int16":
    case "int32":
    case "int64":
    case "uint":
    case "uint8":
    case "uint16":
    case "uint32":
    case "uint64":
    case "byte":
    case "rune":
      return { t: "int", skip: "" };
    case "float32":
    case "float64":
      return { t: "float", skip: "" };
    case "string":
      return { t: "string", skip: "" };
    case "bool":
      return { t: "bool", skip: "" };
    case "error":
      return { t: "error", skip: "" };
    case "any":
    case "interface{}":
      return { t: "any", skip: "" };
  }
  if (t.startsWith("[]")) {
    const elem = goType(t.slice(2));
    if (elem.skip !== "") return elem;
    return { t: `[${elem.t}]`, skip: "" };
  }
  if (t.startsWith("*")) return { t: "", skip: "指针" };
  if (t.startsWith("map[")) return { t: "", skip: "map" };
  if (t.startsWith("chan ")) return { t: "", skip: "chan" };
  if (t.startsWith("func(") || t.startsWith("func ")) return { t: "", skip: "func" };
  if (t.startsWith("struct{")) return { t: "", skip: "struct" };
  if (t.startsWith("interface{")) return { t: "", skip: "interface" };
  if (t.includes(".")) return { t: "", skip: "命名类型" };
  return { t: "", skip: "未知类型" };
}

/** 顶层分隔切分（忽略括号/尖括号内） */
function splitTop(s: string, sep: string): string[] {
  const out: string[] = [];
  let depth = 0;
  let cur = "";
  for (const ch of s) {
    if (ch === "(" || ch === "[" || ch === "{") depth++;
    else if (ch === ")" || ch === "]" || ch === "}") depth--;
    if (ch === sep && depth === 0) {
      out.push(cur);
      cur = "";
      continue;
    }
    cur += ch;
  }
  out.push(cur);
  return out;
}
