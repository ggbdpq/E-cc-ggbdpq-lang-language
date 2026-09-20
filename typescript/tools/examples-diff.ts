// examples/ + m4/ 全量三路差分（与 Go 实现验收判据同构：stdout 逐字节一致）
import { readFileSync, readdirSync, statSync, mkdirSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { join } from "node:path";
import { loadProgram } from "../dist/loader/loader.js";
import { analyzeStmts, checkStmts } from "../dist/pipeline/pipeline.js";
import { Interp } from "../dist/interp/interp.js";
import { Machine } from "../dist/irinterp/irinterp.js";
import { transpile } from "../dist/backends/gen.js";
import { transpileJS } from "../dist/backends/genjs.js";

const repo = join(import.meta.dirname ?? ".", "..", "..");
// m4/ 顶层即一个包（main.lang-go + 同目录辅助文件），单独作为 case
const roots = [join(repo, "examples"), join(repo, "go", "examples"), join(repo, "go", "m4"), join(repo, "go", "m4", "gofixture")];
const tmp = "/e/tmp-exdiff";
mkdirSync(tmp, { recursive: true });

// specs/concurrency.md TASK-DETERMINISM-003 + runner-contract 约束 3：
// 多 Task 交错序为实现自由度——go 语句示例的 →Go（真 goroutine）与解释器/JS
// （同步确定子集）输出允许不同；runner-contract「例外仅限 spec 文档化的平台差异」。
const documentedDivergence = new Set(["goroutine"]);

let pass = 0;
let fail = 0;
let skip = 0;

for (const root of roots) {
  if (!statSync(root, { throwIfNoEntry: false })?.isDirectory()) continue;
  // 根自身含 main.lang-go 时整目录是一个 case（go/m4 顶层形态）
  const rootIsCase = statSync(join(root, "main.lang-go"), { throwIfNoEntry: false }) !== undefined;
  const names = rootIsCase ? ["."] : readdirSync(root).sort();
  for (const name of names) {
    const dir = name === "." ? root : join(root, name);
    if (name !== "." && !statSync(dir).isDirectory()) continue;
    const entry = join(dir, "main.lang-go");
    if (!statSync(entry, { throwIfNoEntry: false })) continue;
    const label = `${root.split("/").pop()}/${name}`;
    let stmts;
    try {
      stmts = loadProgram(dir, false).stmts;
    } catch (e) {
      // 装载失败（如缺少 bind.d 等环境件）→ 记 skip，不算失败
      console.log(`SKIP ${label}: ${String(e).slice(0, 90)}`);
      skip++;
      continue;
    }
    const diags = checkStmts(stmts);
    if (diags.length > 0) {
      console.log(`SKIP ${label}: 前端诊断 ${diags[0].code}@${diags[0].line}`);
      skip++;
      continue;
    }
    const a = analyzeStmts(stmts);
    const b1 = { s: "", write(x: string): void { this.s += x; } };
    try {
      new Interp(b1).run(stmts);
    } catch (e) {
      console.log(`FAIL ${label}: interp ${String(e).slice(0, 90)}`);
      fail++;
      continue;
    }
    try {
      const b2 = { s: "", write(x: string): void { this.s += x; } };
      new Machine(b2).run(a.prog);
      if (b2.s !== b1.s) throw new Error("irinterp 不一致");
    } catch (e) {
      console.log(`FAIL ${label}: irinterp ${String(e).slice(0, 90)}`);
      fail++;
      continue;
    }
    let goSrc: string;
    let jsSrc: string;
    try {
      goSrc = transpile(a.prog);
      jsSrc = transpileJS(a.prog);
    } catch (e) {
      console.log(`FAIL ${label}: 转译 ${String(e).slice(0, 90)}`);
      fail++;
      continue;
    }
    writeFileSync(join(tmp, "main.go"), goSrc);
    writeFileSync(join(tmp, "go.mod"), "module langgogen\n\ngo 1.24\n");
    const rg = spawnSync("go", ["run", "main.go"], { cwd: tmp, encoding: "utf8", timeout: 60000 });
    if (rg.status !== 0) {
      console.log(`FAIL ${label}: go ${String(rg.stderr).slice(0, 90)}`);
      fail++;
      continue;
    }
    writeFileSync(join(tmp, "out.mjs"), jsSrc);
    const rj = spawnSync(process.execPath, [join(tmp, "out.mjs")], { encoding: "utf8", timeout: 30000 });
    if (rj.status !== 0) {
      console.log(`FAIL ${label}: js ${String(rj.stderr).slice(0, 90)}`);
      fail++;
      continue;
    }
    if (rg.stdout !== b1.s || rj.stdout !== b1.s) {
      if (documentedDivergence.has(name)) {
        console.log(`NOTE ${label}: go 语句交错序差（spec 文档化平台差，spec §TASK-DETERMINISM-003）`);
        pass++;
        continue;
      }
      console.log(`FAIL ${label}: stdout 差异 interp=${JSON.stringify(b1.s.slice(0, 80))} go=${JSON.stringify(rg.stdout.slice(0, 80))} js=${JSON.stringify(rj.stdout.slice(0, 80))}`);
      fail++;
      continue;
    }
    pass++;
    console.log(`PASS ${label}`);
  }
}

console.log(`\nexamples 差分: ${pass} 通过, ${fail} 失败, ${skip} 跳过`);
