// lang-go-ts CLI：run / check --json / conformance <dir>
// 目录即包（specs/modules.md）；诊断契约（code/ruleId/severity/line）与 Go 参考实现同源。

import * as fs from "node:fs";
import * as path from "node:path";
import { loadProgram } from "../loader/loader.js";
import { checkStmts } from "../pipeline/pipeline.js";
import { Interp } from "../interp/interp.js";
import { runSuite } from "../conformance/manifest.js";
import { ruleOf } from "../contract/rules.js";

const TOOL_VERSION = "1.0.0";

function main(): number {
  const args = process.argv.slice(2);
  const cmd = args[0] ?? "run";
  const dir = ".";
  try {
    switch (cmd) {
      case "run": {
        const { stmts } = loadProgram(dir, false);
        const diags = checkStmts(stmts);
        if (diags.length > 0) {
          for (const d of diags) console.error(`第${d.line}行: [${d.code}] ${d.msg}`);
          return 1;
        }
        new Interp({ write: (s) => process.stdout.write(s) }).run(stmts);
        return 0;
      }
      case "check": {
        const { stmts } = loadProgram(dir, false);
        const diags = checkStmts(stmts);
        if (args.includes("--json")) {
          const payload = {
            schemaVersion: "1",
            toolVersion: TOOL_VERSION,
            kind: "check",
            success: diags.length === 0,
            diagnostics: diags.map((d) => ({
              code: d.code,
              ruleId: ruleOf(d.code),
              severity: "error",
              message: d.msg,
              primarySpan: { file: "", start: { offset: 0, line: d.line, column: 1 }, end: { offset: 0, line: d.line, column: 1 } },
              relatedSpans: [],
              help: "",
            })),
          };
          console.log(JSON.stringify(payload, null, 2));
        } else {
          for (const d of diags) console.error(`第${d.line}行: [${d.code}] ${d.msg}`);
        }
        return diags.length > 0 ? 1 : 0;
      }
      case "conformance": {
        const root = path.resolve(args[1] ?? "../../conformance");
        const { results, fails } = runSuite(root);
        for (const r of results) {
          if (!r.ok) console.error(`  FAIL ${r.category}/${r.name}: ${r.note ?? ""}`);
        }
        console.error(`conformance: ${results.length - fails}/${results.length} 通过`);
        return fails > 0 ? 1 : 0;
      }
      default:
        console.error(`未知命令 ${cmd}（run | check --json | conformance <dir>）`);
        return 2;
    }
  } catch (e) {
    console.error("错误:", String(e));
    return 1;
  }
}

process.exit(main());
