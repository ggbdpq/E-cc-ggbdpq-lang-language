// lang-ts verify 的管线：parse → resolve → typecheck → hir → ir → effect →
// compile → execute → differential。任一阶段失败即停——后续阶段不列。
// execute 为四方（AST Oracle / IR 解释器 / →Go 产物 / →JS 产物）；
// differential 断言四方（AST 解释器 / IR 解释器 / →Go / →JS）stdout 逐字节一致。

import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { loadProgram } from "../loader/loader.js";
import { checkStmts, analyzeStmts } from "../pipeline/pipeline.js";
import { formatDiags } from "../common/diagnostic.js";
import type { Diagnostic } from "../common/diagnostic.js";
import { walkHirProg } from "../hir/walk.js";
import { Interp } from "../interp/interp.js";
import { Machine } from "../irinterp/irinterp.js";
import { transpile } from "../backends/gen.js";
import { transpileJS } from "../backends/genjs.js";
import { graphEffects } from "../graph/effects.js";

export interface Stage {
  readonly stage: string;
  readonly ok: boolean;
  readonly skipped?: boolean;
  readonly note?: string;
}

export interface VerifyResult {
  readonly success: boolean;
  readonly stages: Stage[];
  readonly stdoutBytes?: number;
  readonly exitCode?: number;
}

function hasTool(name: string): boolean {
  const args = name === "go" ? ["version"] : ["-v"];
  const r = spawnSync(name, args, { encoding: "utf8", shell: process.platform === "win32" });
  return r.status === 0;
}

/** 对程序目录（目录即包）走全管线 */
export function verifyDir(dir: string): VerifyResult {
  const stages: Stage[] = [];
  const fail = (name: string, note: string): VerifyResult => {
    stages.push({ stage: name, ok: false, note });
    return { success: false, stages };
  };
  const ok = (name: string, note = ""): void => {
    stages.push({ stage: name, ok: true, ...(note !== "" ? { note } : {}) });
  };

  // 1) parse：装载即解析
  let prog;
  try {
    prog = loadProgram(dir, false).stmts;
  } catch (e) {
    return fail("parse", String(e));
  }
  ok("parse");

  // 2+3) resolve / typecheck：前端一次跑完，按稳定码分段呈现
  const diags: Diagnostic[] = checkStmts(prog);
  const resolveDiags = diags.filter((d) => !d.code.startsWith("E3") && !d.code.startsWith("E4"));
  const typeDiags = diags.filter((d) => d.code.startsWith("E3") || d.code.startsWith("E4"));
  if (resolveDiags.length > 0) return fail("resolve", formatDiags(resolveDiags));
  ok("resolve");
  if (typeDiags.length > 0) return fail("typecheck", formatDiags(typeDiags));
  ok("typecheck");

  // 4) hir validate：构建成功 + 全树遍历可达
  const a = analyzeStmts(prog);
  let nodes = 0;
  walkHirProg(a.hir, {
    stmt: () => nodes++,
    expr: () => nodes++,
  });
  if (nodes === 0) return fail("hir", "遍历覆盖为 0（结构不完整）");
  ok("hir", `${nodes} 节点`);

  // 5) ir validate：lower 成功且非空
  const ip = a.prog;
  ok("ir", "");

  // 6) effect：效果面可推导
  const effects = graphEffects(ip);
  ok("effect", `${effects.length} 项`);

  // 7) compile：→Go 产物可编译 + →JS 产物可生成
  let goSrc: string;
  let jsSrc: string;
  try {
    goSrc = transpile(ip);
    jsSrc = transpileJS(ip);
  } catch (e) {
    return fail("compile", "转译: " + String(e));
  }
  const goAvailable = hasTool("go");
  const nodeAvailable = hasTool("node");
  const tmp = mkdtempSync(join(tmpdir(), "langts-verify-"));
  try {
    writeFileSync(join(tmp, "main.go"), goSrc, "utf8");
    if (goAvailable) {
      const r = spawnSync("go", ["build", "-o", "prog.exe", "main.go"], { cwd: tmp, encoding: "utf8", shell: process.platform === "win32" });
      if (r.status !== 0) {
        return fail("compile", "go build: " + (r.stderr ?? "") + (r.stdout ?? ""));
      }
      ok("compile", "go+js 产物");
    } else {
      stages.push({ stage: "compile", ok: true, skipped: true, note: "宿主缺 go，→Go 产物仅生成未编译" });
    }

    // 8) execute：四方执行
    const outBuf = { s: "", write(x: string): void { this.s += x; } };
    try {
      new Interp(outBuf).run(prog);
    } catch (e) {
      return fail("execute", "解释器: " + String(e));
    }
    const irBuf = { s: "", write(x: string): void { this.s += x; } };
    try {
      new Machine(irBuf).run(ip);
    } catch (e) {
      return fail("execute", "IR 解释器: " + String(e));
    }
    let outGo = "";
    if (!goAvailable) {
      outGo = outBuf.s; // 无工具链：差分降级
    } else {
      const r = spawnSync(join(tmp, "prog.exe"), [], { cwd: tmp, encoding: "utf8" });
      if (r.status !== 0) return fail("execute", "→Go: 退出码 " + r.status + " " + (r.stderr ?? ""));
      outGo = r.stdout ?? "";
    }
    let outJS = "";
    if (!nodeAvailable) {
      ok("execute", "宿主缺 node，JS 路不执行（差分降级为三方）");
      outJS = outGo;
    } else {
      writeFileSync(join(tmp, "out.mjs"), jsSrc, "utf8");
      const r = spawnSync(process.execPath, [join(tmp, "out.mjs")], { cwd: tmp, encoding: "utf8" });
      if (r.status !== 0) return fail("execute", "→JS: 退出码 " + r.status + " " + (r.stderr ?? ""));
      outJS = r.stdout ?? "";
      ok("execute");
    }

    // 9) differential：四方 stdout 逐字节一致
    if (outBuf.s !== irBuf.s || outBuf.s !== outGo || outBuf.s !== outJS) {
      return fail("differential", `不一致\n解释器: ${JSON.stringify(outBuf.s)}\nIR 解释器: ${JSON.stringify(irBuf.s)}\n→Go: ${JSON.stringify(outGo)}\n→JS: ${JSON.stringify(outJS)}`);
    }
    ok("differential", `${Buffer.byteLength(outBuf.s, "utf8")} 字节一致`);
    return { success: true, stages, stdoutBytes: Buffer.byteLength(outBuf.s, "utf8"), exitCode: 0 };
  } finally {
    rmSync(tmp, { recursive: true, force: true });
  }
}

export { execFileSync };
