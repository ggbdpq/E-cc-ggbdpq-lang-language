// 单 case 三路差分调试器：node tools/case-diff.ts <caseDir>
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { loadProgram } from "../dist/loader/loader.js";
import { analyzeStmts, checkStmts } from "../dist/pipeline/pipeline.js";
import { Interp } from "../dist/interp/interp.js";
import { Machine } from "../dist/irinterp/irinterp.js";
import { transpile } from "../dist/backends/gen.js";
import { transpileJS } from "../dist/backends/genjs.js";

const dir = process.argv[2]!;
const { stmts } = loadProgram(dir, false);
const diags = checkStmts(stmts);
if (diags.length > 0) {
  console.log("FRONTEND:", diags.map((d) => `${d.line}:${d.code} ${d.msg}`));
}
const a = analyzeStmts(stmts);
const out1 = { s: "", write(x: string): void { this.s += x; } };
new Interp(out1).run(stmts);
console.log("interp:", JSON.stringify(out1.s));
const out2 = { s: "", write(x: string): void { this.s += x; } };
new Machine(out2).run(a.prog);
console.log("irinterp:", JSON.stringify(out2.s));
const go = transpile(a.prog);
const js = transpileJS(a.prog);
const tmp = "/e/tmp-casediff";
import { mkdirSync, writeFileSync as wfs } from "node:fs";
mkdirSync(tmp, { recursive: true });
wfs(tmp + "/main.go", go);
wfs(tmp + "/go.mod", "module langgogen\n\ngo 1.24\n");
wfs(tmp + "/out.mjs", js);
const rg = spawnSync("go", ["run", "main.go"], { cwd: tmp, encoding: "utf8" });
console.log("go:", JSON.stringify(rg.stdout), rg.status !== 0 ? "ERR " + rg.stderr?.slice(0, 400) : "");
const rj = spawnSync(process.execPath, [tmp + "/out.mjs"], { encoding: "utf8" });
console.log("js:", JSON.stringify(rj.stdout), rj.status !== 0 ? "ERR " + rj.stderr?.slice(0, 400) : "");
if (process.env.SHOW === "1") {
  console.log("=== GO SRC ===\n" + go);
  console.log("=== JS SRC ===\n" + js);
}
