// conformance manifest（YAML 受限子集）解析 + case runner（specs/conformance/README.md）。
// 支持且仅支持：顶层标量键、ruleIds/expect 一层嵌套、stdout 块标量、diagnostics 列表项；
// 任何超集形态一律报错——套件拒绝模糊输入。

import * as fs from "node:fs";
import * as path from "node:path";
import { loadProgram } from "../loader/loader.js";
import { checkStmts } from "../pipeline/pipeline.js";
import { Interp } from "../interp/interp.js";
import { ruleOf } from "../contract/rules.js";

export interface DiagExpect {
  code: string;
  ruleId: string;
  severity: string;
  line: number;
}

export interface Expect {
  exitCode: number;
  stdout: string;
  stderr: string;
  diagnostics: DiagExpect[];
}

export interface Case {
  name: string;
  category: string;
  mode: "run" | "check";
  note: string;
  ruleIds: string[];
  expect: Expect;
}

export interface CaseResult {
  name: string;
  category: string;
  mode: string;
  ruleIds: string[];
  ok: boolean;
  skipped?: boolean;
  note?: string;
}

const ERR = "manifest 第";

export function parseManifest(src: string): Case {
  const c: Case = {
    name: "",
    category: "",
    mode: "run",
    note: "",
    ruleIds: [],
    expect: { exitCode: 0, stdout: "", stderr: "", diagnostics: [] },
  };
  type Ctx = "top" | "expect" | "stdoutBlock" | "diag" | "rules";
  let ctx: Ctx = "top";
  let diag: DiagExpect | null = null;
  let blockIndent = -1;
  const lines = src.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i]!.replace(/\r$/, "");
    if (raw.trim() === "" || raw.trim().startsWith("#")) continue;
    const indent = raw.length - raw.trimStart().length;
    const line = raw.trim();

    if (ctx === "stdoutBlock") {
      if (indent >= blockIndent) {
        c.expect.stdout += raw.slice(blockIndent) + "\n";
        continue;
      }
      ctx = "expect";
    }
    if (ctx === "rules") {
      if (line.startsWith("- ")) {
        c.ruleIds.push(line.slice(2));
        continue;
      }
      ctx = "top";
    }

    const ci = line.indexOf(":");
    if (ci <= 0) throw new Error(`${ERR}${i + 1}: 形态不支持 ${JSON.stringify(line)}`);
    const key = line.slice(0, ci).trim();
    let val = line.slice(ci + 1).trim();
    const hash = val.indexOf(" #");
    if (hash >= 0 && !val.startsWith('"')) val = val.slice(0, hash).trim();
    if (val.startsWith('"') && val.endsWith('"') && val.length >= 2) {
      const inner = val.slice(1, -1);
      if (inner.includes("\\")) throw new Error(`${ERR}${i + 1}: 标量不支持转义`);
      val = inner;
    }

    switch (ctx) {
      case "top":
        if (key === "name") c.name = val;
        else if (key === "category") c.category = val;
        else if (key === "mode") c.mode = val === "check" ? "check" : val === "run" ? "run" : bail(`${ERR}${i + 1}: mode 须为 run|check`);
        else if (key === "note") c.note = val;
        else if (key === "ruleIds") {
          if (val !== "") throw new Error(`${ERR}${i + 1}: ruleIds 不接受内联值`);
          ctx = "rules";
        } else if (key === "expect") {
          if (val !== "") throw new Error(`${ERR}${i + 1}: expect 不接受内联值`);
          ctx = "expect";
        } else throw new Error(`${ERR}${i + 1}: 未知键 ${JSON.stringify(key)}`);
        break;
      case "expect":
        if (key === "exitCode") c.expect.exitCode = Number.parseInt(val, 10);
        else if (key === "stdout") {
          if (val !== "|") {
            c.expect.stdout = val;
            continue;
          }
          ctx = "stdoutBlock";
          blockIndent = -1;
          for (let j = i + 1; j < lines.length; j++) {
            const r2 = lines[j]!.replace(/\r$/, "");
            if (r2.trim() === "") continue;
            blockIndent = r2.length - r2.trimStart().length;
            break;
          }
          if (blockIndent < 0) throw new Error(`${ERR}${i + 1}: stdout 块标量为空`);
        } else if (key === "stderr") c.expect.stderr = val;
        else if (key === "diagnostics") {
          if (val !== "") throw new Error(`${ERR}${i + 1}: diagnostics 不接受内联值`);
          ctx = "diag";
        } else throw new Error(`${ERR}${i + 1}: expect 下未知键 ${JSON.stringify(key)}`);
        break;
      case "diag":
        if (!line.startsWith("- ")) {
          if (diag !== null) {
            diagField(diag, key, val, i + 1);
            continue;
          }
          throw new Error(`${ERR}${i + 1}: diagnostics 项须以 "- " 开头`);
        }
        if (diag !== null) c.expect.diagnostics.push(diag);
        diag = { code: "", ruleId: "", severity: "", line: 0 };
        diagField(diag, line.slice(2).split(":")[0]!.trim(), line.slice(line.indexOf(":") + 1).trim(), i + 1);
        break;
      default:
        throw new Error(`${ERR}${i + 1}: 形态不支持`);
    }
  }
  if (ctx === "diag" && diag !== null) c.expect.diagnostics.push(diag);

  if (c.name === "" || c.category === "") throw new Error("manifest 缺 name/category");
  if (c.mode === "run") {
    if (c.expect.exitCode !== 0) throw new Error("run 模式 exitCode 必须为 0");
    if (c.expect.stderr !== "") throw new Error("run 模式 stderr 必须为空串");
    if (!c.expect.stdout.endsWith("\n") || c.expect.stdout.includes("\n\n")) {
      throw new Error("run 模式 stdout 须为块标量且无空行");
    }
  } else if (c.expect.diagnostics.length === 0) {
    throw new Error("check 模式至少声明一条诊断");
  }
  return c;
}

function diagField(d: DiagExpect, key: string, val: string, lineNo: number): void {
  if (key === "code") d.code = val;
  else if (key === "ruleId") d.ruleId = val;
  else if (key === "severity") d.severity = val;
  else if (key === "line") d.line = Number.parseInt(val, 10);
  else throw new Error(`${ERR}${lineNo}: 诊断项未知键 ${JSON.stringify(key)}`);
}

function bail(msg: string): never {
  throw new Error(msg);
}

// runCase 执行单个 case 目录
export function runCase(dir: string): CaseResult {
  const src = fs.readFileSync(path.join(dir, "case.yaml"), "utf8");
  let c: Case;
  try {
    c = parseManifest(src);
  } catch (e) {
    return { name: path.basename(dir), category: "", mode: "", ruleIds: [], ok: false, note: String(e) };
  }
  const res: CaseResult = { name: c.name, category: c.category, mode: c.mode, ruleIds: c.ruleIds, ok: false };
  try {
    const { stmts } = loadProgram(dir, false);
    if (c.mode === "run") return runDifferential(c, stmts, res);
    return runDiagnostic(c, stmts, res);
  } catch (e) {
    res.note = String(e);
    return res;
  }
}

function runDifferential(c: Case, stmts: readonly unknown[], res: CaseResult): CaseResult {
  // 前端必须零诊断
  const diags = checkStmts(stmts as never);
  if (diags.length > 0) {
    res.note = `run 模式 case 前端诊断: ${diags.map((d) => `第${d.line}行: [${d.code}] ${d.msg}`).join("; ")}`;
    return res;
  }
  const out: { text: string; write(s: string): void } = {
    text: "",
    write(s: string) {
      this.text += s;
    },
  };
  const interp = new Interp(out);
  try {
    interp.run(stmts as never);
  } catch (e) {
    res.note = "解释器执行失败: " + String(e);
    return res;
  }
  if (out.text !== c.expect.stdout) {
    res.note = `解释器 stdout 不一致\n期望: ${JSON.stringify(c.expect.stdout)}\n实际: ${JSON.stringify(out.text)}`;
    return res;
  }
  res.ok = true;
  return res;
}

function runDiagnostic(c: Case, stmts: readonly unknown[], res: CaseResult): CaseResult {
  const diags = checkStmts(stmts as never);
  const got = new Map<string, number>();
  for (const d of diags) {
    const k = `${d.code}|${ruleOf(d.code)}|error|${d.line}`;
    got.set(k, (got.get(k) ?? 0) + 1);
  }
  const want = new Map<string, number>();
  for (const d of c.expect.diagnostics) {
    const rule = d.ruleId !== "" ? d.ruleId : ruleOf(d.code);
    const k = `${d.code}|${rule}|${d.severity}|${d.line}`;
    want.set(k, (want.get(k) ?? 0) + 1);
  }
  const missing: string[] = [];
  const extra: string[] = [];
  for (const [k, n] of want) if ((got.get(k) ?? 0) < n) missing.push(`${k}×${n - (got.get(k) ?? 0)}`);
  for (const [k, n] of got) if ((want.get(k) ?? 0) < n) extra.push(`${k}×${n - (want.get(k) ?? 0)}`);
  if (missing.length > 0 || extra.length > 0) {
    res.note = `诊断不匹配 缺${missing.join(",")} 多${extra.join(",")}`;
    return res;
  }
  res.ok = true;
  return res;
}

export function runSuite(root: string): { results: CaseResult[]; fails: number } {
  const results: CaseResult[] = [];
  for (const cat of fs.readdirSync(root, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    if (!cat.isDirectory()) continue; // 跳过 README.md 等文件
    for (const cd of fs.readdirSync(path.join(root, cat.name), { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      if (!cd.isDirectory()) continue;
      const dir = path.join(root, cat.name, cd.name);
      if (!fs.existsSync(path.join(dir, "case.yaml"))) {
        throw new Error(`套件目录 ${dir} 缺 case.yaml`);
      }
      const r = runCase(dir);
      if (r.name === "") r.name = cd.name;
      results.push(r);
    }
  }
  return { results, fails: results.filter((r) => !r.ok).length };
}
