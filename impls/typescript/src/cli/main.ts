// lang-ts CLI：run/test/REPL + fmt/check/inspect/dump/fix/refactor/verify/bind/build。
// 路径安全：不接收任何路径参数——固定运行当前目录的 main.lang-go（目录即包）。
// 命名与 go/ 的 lang-go 同构：目录叫 typescript，工具自称 lang-ts；
// runner contract 的可执行名 lang-go-ts 由 package.json bin 提供。

import * as fs from "node:fs";
import * as path from "node:path";
import * as readline from "node:readline";
import { Interp } from "../interp/interp.js";
import { fmt } from "../interp/value.js";
import { parse as syntaxParse } from "../syntax/parser.js";
import { loadProgram, ENTRY } from "../loader/loader.js";
import { checkStmts, analyzeStmts, compileStmts, formatDiags, DiagList } from "../pipeline/pipeline.js";
import type { Stmt } from "../syntax/ast.js";
import { format as fmtSource } from "../format/format.js";
import { planDir } from "../fix/fix.js";
import { apply, hashBytes, type WorkspaceEdit } from "../edit/edit.js";
import {
  errorEnvelope, emitJSON, checkStmtsJSON, astJSON, hirJSON, irJSON,
  symbolsJSON, referencesJSON, typeAtJSON, effectsJSON,
  errorEnvelope as errEnv, KindCheck,
} from "../contract/contract.js";
import { buildGraph, graphCallers, graphCallees, graphImpact, graphFileDep } from "../graph/graph.js";
import { resolveTarget, rename, safeRemoveCheck, safeRemoveEdits, organizeImports } from "../refactor/refactor.js";
import { bindDir } from "../bind/bind.js";
import { verifyDir } from "../conformance/verify.js";
import { transpile } from "../backends/gen.js";
import { transpileJS } from "../backends/genjs.js";
import { runSuite } from "../conformance/manifest.js";
import { version } from "../version/version.js";

const printJSON = (v: unknown): void => {
  process.stdout.write(emitJSON(v) + "\n");
};

const hasFlag = (args: readonly string[], flag: string): boolean => args.includes(flag);

function usage(): void {
  process.stdout.write(`lang-ts ${version} — 三路执行个人语言（TypeScript 实现：解释器 / →Go / →JS）

用法:
  lang-go-ts            启动 REPL（:quit 退出，:help 帮助）
  lang-go-ts run        运行当前目录的 main.lang-go
  lang-go-ts test       运行当前目录 *_test.lang-go 里的 test* 函数
  lang-go-ts check      语义检查（resolver + 类型检查，稳定诊断码）
  lang-go-ts inspect    symbols|refs|type|dependencies|effects|callers|callees|impact|references|dependency --json 语义查询
  lang-go-ts dump       ast|hir|ir --json 输出编译器内部树
  lang-go-ts fix --safe [--dry-run] [--json]
                        机械安全修复（重复/未使用 import、行尾空白、EOF 换行）+ 验证闭环
  lang-go-ts refactor rename|organize-imports|safe-remove [--symbol N] [--to NAME]
                        [--apply] [--json]
                        语义级重构（SymbolId + 引用集，禁止 Regex）；默认只报编辑，--apply 落盘+验证闭环
  lang-go-ts verify [--json]
                        全管线验证（parse→…→三路差分一致）
  lang-go-ts fmt        就地格式化当前目录 .lang-go
  lang-go-ts build      语义检查后写 out/main.go（build js 写 out/main.mjs）
  lang-go-ts bind       生成 extern "go" 声明草案（bind.d/）
  lang-go-ts conformance [dir]
                        跑跨实现一致性套件（默认 ../../conformance，相对实现目录）
  lang-go-ts version    版本

文档: specs/（语言规范）  docs/（实现侧文档）
`);
}

async function main(): Promise<number> {
  const args = process.argv.slice(2);
  if (args.length === 0) return repl();
  switch (args[0]) {
    case "run":
      return runFile();
    case "test":
      return runTests();
    case "fmt":
      return runFmt();
    case "check": {
      const rest = args.slice(1);
      if (hasFlag(rest, "--json")) return runCheckJSON();
      return runCheck();
    }
    case "inspect":
      return runInspect(args.slice(1));
    case "dump":
      return runDump(args.slice(1));
    case "fix":
      return runFix(args.slice(1));
    case "refactor":
      return runRefactor(args.slice(1));
    case "verify":
      return runVerify(args.slice(1));
    case "bind":
      return runBind();
    case "build": {
      const target = args.length >= 2 ? args[1]! : "go";
      return runBuild(target);
    }
    case "conformance": {
      const root = args.length >= 2 ? args[1]! : "../../conformance";
      return runConformance(path.resolve(root));
    }
    case "version":
    case "--version":
    case "-v":
      process.stdout.write(`lang-ts ${version}\n`);
      return 0;
    case "help":
    case "--help":
    case "-h":
      usage();
      return 0;
    default:
      process.stderr.write(`未知命令 ${JSON.stringify(args[0] ?? "")}\n\n`);
      usage();
      return 2;
  }
}

function loadOr2(): { stmts: Stmt[] } | number {
  try {
    return { stmts: loadProgram(".", false).stmts };
  } catch (e) {
    process.stderr.write("装载失败: " + String(e) + "\n");
    return 1;
  }
}

function runFile(): number {
  const r = loadOr2();
  if (typeof r === "number") return r;
  try {
    new Interp({ write: (s) => process.stdout.write(s) }).run(r.stmts);
    return 0;
  } catch (e) {
    process.stderr.write("错误: " + String(e) + "\n");
    return 1;
  }
}

function runTests(): number {
  try {
    const { stmts, tests } = loadProgram(".", true);
    const it = new Interp({ write: (s) => process.stdout.write(s) });
    try {
      it.evalChunk(stmts);
    } catch (e) {
      process.stderr.write("装载失败: " + String(e) + "\n");
      return 1;
    }
    if (tests.length === 0) {
      process.stdout.write("未发现 *_test.lang-go\n");
      return 0;
    }
    let pass = 0;
    let fail = 0;
    for (const name of tests) {
      try {
        it.callByName(name);
        pass++;
        process.stdout.write(`PASS ${name}\n`);
      } catch (e) {
        fail++;
        process.stdout.write(`FAIL ${name}: ${String(e)}\n`);
      }
    }
    process.stdout.write(`\n${pass} 通过, ${fail} 失败\n`);
    return fail > 0 ? 1 : 0;
  } catch (e) {
    process.stderr.write("装载失败: " + String(e) + "\n");
    return 1;
  }
}

function runFmt(): number {
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(".", { withFileTypes: true });
  } catch {
    process.stderr.write("读目录失败\n");
    return 2;
  }
  let changed = 0;
  for (const e of entries) {
    const name = e.name;
    if (e.isDirectory() || !name.endsWith(".lang-go")) continue;
    const src = fs.readFileSync(name, "utf8");
    let out: string;
    try {
      out = fmtSource(src);
    } catch (err) {
      process.stderr.write(`格式化 ${name} 失败: ${String(err)}\n`);
      return 1;
    }
    if (out !== src) {
      fs.writeFileSync(name, out, "utf8");
      process.stdout.write(name + "\n");
      changed++;
    }
  }
  process.stdout.write(`${changed} 个文件已格式化\n`);
  return 0;
}

function runCheck(): number {
  const r = loadOr2();
  if (typeof r === "number") return r;
  const diags = checkStmts(r.stmts);
  if (diags.length > 0) {
    process.stderr.write("检查未通过:\n");
    process.stderr.write(formatDiags(diags) + "\n");
    return 1;
  }
  process.stdout.write("检查通过\n");
  return 0;
}

function runCheckJSON(): number {
  let prog: Stmt[];
  try {
    prog = loadProgram(".", false).stmts;
  } catch (e) {
    printJSON(errorEnvelope(KindCheck, "装载失败: " + String(e)));
    return 1;
  }
  const out = checkStmtsJSON(checkStmts(prog));
  printJSON(out);
  return out.success ? 0 : 1;
}

interface Analyzed {
  stmts: Stmt[];
  graph: ReturnType<typeof buildGraph>;
  analyzed: ReturnType<typeof analyzeStmts>;
  files: { path: string; imports: string[] }[];
}

function analyzeOr(kind: string): Analyzed | number {
  try {
    const { stmts, files } = loadProgram(".", false);
    const a = analyzeStmts(stmts);
    return {
      stmts,
      analyzed: a,
      files,
      graph: buildGraph(stmts, a.res, a.ty, a.hir, a.prog, files),
    };
  } catch (e) {
    if (e instanceof DiagList) {
      printJSON(errEnv(kind, formatDiags(e.diags)));
    } else {
      printJSON(errEnv(kind, String(e)));
    }
    return 1;
  }
}

function resolveSymbolArg(a: Analyzed, arg: string): number {
  if (arg === "") return -1;
  if (/^[0-9]+$/.test(arg)) {
    const n = Number.parseInt(arg, 10);
    if (a.graph.nodes.has(n)) return n;
    return -1;
  }
  for (const sym of a.analyzed.res.symbols) {
    if (sym.builtinMark) continue;
    if (sym.name === arg) return sym.id;
  }
  return -1;
}

function runInspect(args: string[]): number {
  if (args.length === 0) {
    printJSON(errorEnvelope("inspect", "缺少子命令：symbols|refs|type|dependencies|effects|callers|callees|impact|references"));
    return 2;
  }
  const sub = args[0]!;
  const kind = "inspect." + sub;
  if (!hasFlag(args, "--json")) {
    process.stderr.write("inspect 仅支持 --json 输出（v0.4+）\n");
    return 2;
  }
  let file = "";
  let line = 0;
  let col = 0;
  let depth = -1;
  let target = "";
  for (let i = 0; i < args.length; i++) {
    const a = args[i]!;
    switch (a) {
      case "--file":
        if (i + 1 < args.length) file = args[i + 1]!;
        break;
      case "--line":
        if (i + 1 < args.length) line = Number.parseInt(args[i + 1]!, 10) || 0;
        break;
      case "--column":
        if (i + 1 < args.length) col = Number.parseInt(args[i + 1]!, 10) || 0;
        break;
      case "--depth":
        if (i + 1 < args.length) depth = Number.parseInt(args[i + 1]!, 10) || 0;
        break;
      case "--symbol":
        if (i + 1 < args.length) target = args[i + 1]!;
        break;
      default:
        if (!a.startsWith("-") && a !== sub && target === "" && sub !== "symbols" && sub !== "dependencies" && sub !== "effects") {
          target = a;
        }
        break;
    }
  }

  const a = analyzeOr(kind);
  if (typeof a === "number") return a;
  switch (sub) {
    case "symbols":
      printJSON({ schemaVersion: "1", toolVersion: version, kind, symbols: symbolsJSON(a.stmts, a.analyzed.res, a.analyzed.ty) });
      return 0;
    case "refs":
    case "references": {
      const symID = target !== "" ? resolveSymbolArg(a, target) : -1;
      if (target !== "" && symID < 0) {
        printJSON(errorEnvelope(kind, "未知符号 " + target));
        return 1;
      }
      printJSON({ schemaVersion: "1", toolVersion: version, kind, references: referencesJSON(a.stmts, a.analyzed.res, symID) });
      return 0;
    }
    case "callers":
    case "callees":
    case "impact": {
      const id = resolveSymbolArg(a, target);
      if (id <= 0) {
        printJSON(errorEnvelope(kind, "未知符号 " + target));
        return 1;
      }
      if (sub === "callers") {
        printJSON({ schemaVersion: "1", toolVersion: version, kind, callers: impactItemsJSON(a.graph, graphCallers(a.graph, id, depth)) });
      } else if (sub === "callees") {
        printJSON({ schemaVersion: "1", toolVersion: version, kind, callees: impactItemsJSON(a.graph, graphCallees(a.graph, id, depth)) });
      } else {
        printJSON({ schemaVersion: "1", toolVersion: version, kind, impact: impactJSON(a.graph, graphImpact(a.graph, id, depth)) });
      }
      return 0;
    }
    case "type": {
      if (line === 0) {
        printJSON(errorEnvelope(kind, "缺少 --line/--column 参数"));
        return 2;
      }
      const n = typeAtJSON(a.stmts, a.analyzed.res, a.analyzed.ty, file, line, col);
      if (n === undefined) {
        printJSON(errorEnvelope(kind, "指定位置没有表达式节点"));
        return 1;
      }
      printJSON({ schemaVersion: "1", toolVersion: version, kind, nodes: [n] });
      return 0;
    }
    case "dependencies":
      printJSON({
        schemaVersion: "1",
        toolVersion: version,
        kind,
        dependencies: a.files.map((f) => ({ module: f.path, imports: [...f.imports] })),
      });
      return 0;
    case "dependency": {
      let tgt = "";
      for (let i = 0; i < args.length; i++) {
        if (args[i] === "--file" && i + 1 < args.length) tgt = args[i + 1]!;
      }
      if (tgt === "") tgt = ENTRY;
      const dep = graphFileDep(a.graph, tgt);
      if (dep === undefined) {
        printJSON(errorEnvelope(kind, "未知文件 " + tgt));
        return 1;
      }
      printJSON({
        schemaVersion: "1",
        toolVersion: version,
        kind,
        dependency: { module: dep.module, imports: [...dep.imports], dependsOn: [...dep.dependsOn] },
      });
      return 0;
    }
    case "effects":
      printJSON({
        schemaVersion: "1",
        toolVersion: version,
        kind,
        effects: effectsJSON(a.analyzed.prog).map((e) => ({ symbolId: e.symbolId, name: e.name, effects: [...e.effects] })),
      });
      return 0;
    default:
      printJSON(errorEnvelope(kind, "未知子命令 " + sub));
      return 2;
  }
}

// graph JSON 形态（与 contract/graph.go 对齐）
function impactItemsJSON(
  g: ReturnType<typeof buildGraph>,
  items: readonly { from: number; kind: string; span: unknown; transitive: boolean }[],
): unknown[] {
  return items.map((it) => {
    const n = g.nodes.get(it.from);
    return {
      symbolId: it.from,
      name: n?.name ?? "",
      kind: n?.kind ?? "",
      ...(n?.module ? { module: n.module } : {}),
      transitive: it.transitive,
      ...(it.span !== undefined ? { span: spanOut(it.span) } : {}),
    };
  });
}

function spanOut(sp: unknown): unknown {
  const s = sp as { file: string; start: { offset: number; line: number; col: number }; end: { offset: number; line: number; col: number } };
  if (s.file === "" && s.start.offset === 0 && s.end.offset === 0) return null;
  return {
    file: s.file,
    start: { offset: s.start.offset, line: s.start.line, column: s.start.col },
    end: { offset: s.end.offset, line: s.end.line, column: s.end.col },
  };
}

function nodeJSON(n: NonNullable<ReturnType<typeof buildGraph>["nodes"] extends Map<number, infer N> ? N : never>): unknown {
  return {
    symbolId: n.id,
    kind: n.kind,
    name: n.name,
    module: n.module,
    ...(n.type !== "" ? { type: n.type } : {}),
    effects: [...n.effects],
    ...(n.span !== undefined ? { span: spanOut(n.span) } : {}),
  };
}

function impactJSON(g: ReturnType<typeof buildGraph>, imp: ReturnType<typeof graphImpact>): unknown {
  return {
    symbol: imp.symbol !== undefined ? nodeJSON(imp.symbol) : undefined,
    direct: imp.direct.map((it) => ({
      symbolId: it.from,
      kind: it.kind,
      transitive: it.transitive,
      ...(it.span !== undefined ? { span: spanOut(it.span) } : {}),
    })),
    transitive: imp.transitive.map((it) => ({
      symbolId: it.from,
      kind: it.kind,
      transitive: it.transitive,
      ...(it.span !== undefined ? { span: spanOut(it.span) } : {}),
    })),
    effectWatch: [...imp.effectWatch],
    modules: [...imp.modules],
    note: "保守证据集：列出可能受影响的符号与位置，不构成完整影响证明",
  };
}

function runDump(args: string[]): number {
  if (args.length === 0) {
    printJSON(errorEnvelope("dump", "缺少子命令：ast|hir|ir"));
    return 2;
  }
  const sub = args[0]!;
  if (!hasFlag(args, "--json")) {
    process.stderr.write("dump 仅支持 --json 输出（v0.4）\n");
    return 2;
  }
  const kind = "dump." + sub;
  try {
    const { stmts } = loadProgram(".", false);
    switch (sub) {
      case "ast": {
        const a = analyzeStmts(stmts);
        printJSON({ schemaVersion: "1", toolVersion: version, kind, nodes: astJSON(stmts, a.res, a.ty) });
        break;
      }
      case "hir": {
        const a = analyzeStmts(stmts);
        printJSON({ schemaVersion: "1", toolVersion: version, kind, nodes: hirJSON(a.hir) });
        break;
      }
      case "ir": {
        const p = compileStmts(stmts);
        printJSON({ schemaVersion: "1", toolVersion: version, kind, nodes: irJSON(p) });
        break;
      }
      default:
        printJSON(errorEnvelope(kind, "未知子命令 " + sub));
        return 2;
    }
  } catch (e) {
    if (e instanceof DiagList) {
      printJSON(errorEnvelope(kind, formatDiags(e.diags)));
    } else {
      printJSON(errorEnvelope(kind, "装载失败: " + String(e)));
    }
    return 1;
  }
  return 0;
}

function fixStop(stage: string, msg: string): unknown {
  return { schemaVersion: "1", toolVersion: version, kind: "fix", success: false, stage, error: msg };
}

function verifyAfterEdit(kind: string): number {
  try {
    const { stmts } = loadProgram(".", false);
    const diags = checkStmts(stmts);
    if (diags.length > 0) {
      const msg = "typecheck: " + formatDiags(diags);
      printJSON(fixStop("verify", msg));
      process.stderr.write("STOP——编辑后验证失败: " + msg + "\n");
      return 1;
    }
    return 0;
  } catch (e) {
    const msg = "parse/resolve: " + String(e);
    printJSON(fixStop("verify", msg));
    process.stderr.write("STOP——编辑后验证失败: " + msg + "\n");
    return 1;
  }
}

function runFix(args: string[]): number {
  const kind = "fix";
  if (!hasFlag(args, "--safe")) {
    printJSON(errorEnvelope(kind, "v0.5 仅支持 --safe（机械可证明安全修复）"));
    return 2;
  }
  const dry = hasFlag(args, "--dry-run");
  const asJSON = hasFlag(args, "--json");

  let ws: WorkspaceEdit;
  try {
    ws = planDir(".");
  } catch (e) {
    printJSON(errorEnvelope(kind, String(e)));
    return 1;
  }
  if (asJSON || dry) {
    printJSON({
      schemaVersion: "1",
      toolVersion: version,
      kind,
      dryRun: dry,
      applied: false,
      workspaceEdit: wsOut(ws),
    });
    if (dry) return 0;
  }

  try {
    apply(ws, ".");
  } catch (e) {
    const msg = "apply: " + String(e);
    printJSON(fixStop("apply", msg));
    process.stderr.write("STOP——应用编辑失败: " + String(e) + "\n");
    return 1;
  }

  if (verifyAfterEdit(kind) !== 0) return 1;

  if (asJSON) {
    printJSON({ schemaVersion: "1", toolVersion: version, kind, success: true, applied: true, editCount: ws.edits.length });
  } else {
    process.stdout.write(`safe fix 完成（${ws.edits.length} 处编辑），验证通过\n`);
  }
  return 0;
}

function wsOut(ws: WorkspaceEdit): unknown {
  return {
    edits: ws.edits.map((e) => ({ file: e.file, startOffset: e.startOffset, endOffset: e.endOffset, replacement: e.replacement })),
    preconditions: ws.preconditions.map((p) => ({ file: p.file, fileHash: p.fileHash })),
  };
}

function runRefactor(args: string[]): number {
  if (args.length === 0) {
    printJSON(errorEnvelope("refactor", "缺少子命令：rename|organize-imports|safe-remove"));
    return 2;
  }
  const sub = args[0]!;
  const kind = "refactor." + sub;
  if (sub !== "rename" && sub !== "organize-imports" && sub !== "safe-remove") {
    printJSON(errorEnvelope(kind, "未知子命令 " + sub));
    return 2;
  }
  let symbol = "";
  let to = "";
  const applyFlag = hasFlag(args, "--apply");
  for (let i = 0; i < args.length; i++) {
    if (args[i] === "--symbol" && i + 1 < args.length) symbol = args[i + 1]!;
    if (args[i] === "--to" && i + 1 < args.length) to = args[i + 1]!;
  }

  try {
    const { stmts, files } = loadProgram(".", false);
    const byFile = new Map<string, string>();
    let def = "";
    for (const fi of files) {
      const bs = fs.readFileSync(path.join(".", fi.path), "utf8");
      byFile.set(fi.path, bs);
      if (fi.path === ENTRY || def === "") def = bs;
    }
    const srcs = { default: def, byFile };
    const a = analyzeStmts(stmts);

    let edits: { file: string; startOffset: number; endOffset: number; replacement: string }[] = [];
    let symJSON: unknown = {};
    if (sub === "rename" || sub === "safe-remove") {
      let target;
      try {
        target = resolveTarget(a.res, symbol);
      } catch (e) {
        printJSON(errorEnvelope(kind, String(e)));
        return 1;
      }
      symJSON = { id: target.id, name: target.name, kind: target.kind };
      if (sub === "rename") {
        if (to === "") {
          printJSON(errorEnvelope(kind, "缺少 --to 参数"));
          return 2;
        }
        try {
          edits = rename(stmts, srcs, a.res, a.hir, target, to);
        } catch (e) {
          printJSON(errorEnvelope(kind, String(e)));
          return 1;
        }
      } else {
        let chk;
        try {
          chk = safeRemoveCheck(stmts, a.res, target);
        } catch (e) {
          printJSON(errorEnvelope(kind, String(e)));
          return 1;
        }
        if (!chk.removable) {
          printJSON({
            schemaVersion: "1",
            toolVersion: version,
            kind,
            removable: false,
            reason: chk.reason,
            refSites: [...chk.refSites],
            symbol: symJSON,
          });
          return 1;
        }
        try {
          edits = safeRemoveEdits(stmts, srcs, a.res, target);
        } catch (e) {
          printJSON(errorEnvelope(kind, String(e)));
          return 1;
        }
      }
    } else {
      // organize-imports：每文件独立整理；变化文件以整文件替换编辑表达
      const paths = [...byFile.keys()].sort();
      for (const p of paths) {
        const [out, changed] = organizeImports(byFile.get(p)!);
        if (changed) {
          edits.push({ file: p, startOffset: 0, endOffset: Array.from(byFile.get(p)!).length, replacement: out });
        }
      }
    }

    // 前置哈希：编辑涉及的全部文件
    const pre = new Set<string>();
    const preconds: { file: string; fileHash: string }[] = [];
    for (const e of edits) {
      if (pre.has(e.file)) continue;
      pre.add(e.file);
      preconds.push({ file: e.file, fileHash: hashBytes(srcs.byFile.get(e.file) ?? srcs.default) });
    }
    const ws: WorkspaceEdit = { edits, preconditions: preconds };

    const report = (applied: boolean): number => {
      printJSON({
        schemaVersion: "1",
        toolVersion: version,
        kind,
        applied,
        editCount: edits.length,
        symbol: symJSON,
        workspaceEdit: wsOut(ws),
      });
      return 0;
    };
    if (!applyFlag) return report(false);

    try {
      apply(ws, ".");
    } catch (e) {
      printJSON(fixStop("apply", String(e)));
      process.stderr.write("STOP——应用编辑失败: " + String(e) + "\n");
      return 1;
    }
    if (verifyAfterEdit(kind) !== 0) return 1;
    return report(true);
  } catch (e) {
    printJSON(errorEnvelope(kind, "装载失败: " + String(e)));
    return 1;
  }
}

function runVerify(args: string[]): number {
  const kind = "verify";
  const asJSON = hasFlag(args, "--json");
  const r = verifyDir(".");
  if (asJSON) {
    printJSON({
      schemaVersion: "1",
      toolVersion: version,
      kind,
      success: r.success,
      stages: r.stages.map((s) => ({ stage: s.stage, ok: s.ok, ...(s.skipped === true ? { skipped: true } : {}), ...(s.note !== undefined && s.note !== "" ? { note: s.note } : {}) })),
      ...(r.stdoutBytes !== undefined ? { stdoutBytes: r.stdoutBytes } : {}),
      ...(r.exitCode !== undefined && r.exitCode !== 0 ? { exitCode: r.exitCode } : {}),
    });
  } else {
    for (const st of r.stages) {
      const mark = !st.ok ? "FAIL" : st.skipped === true ? "SKIP" : "ok  ";
      let lineOut = `[${mark}] ${st.stage}`;
      if (st.note !== undefined && st.note !== "") lineOut += " — " + st.note;
      process.stdout.write(lineOut + "\n");
    }
    if (r.success) process.stdout.write("验证通过：三路执行一致\n");
  }
  return r.success ? 0 : 1;
}

function runBind(): number {
  try {
    process.stdout.write(bindDir("bind.d"));
    return 0;
  } catch (e) {
    process.stderr.write("bind 失败: " + String(e) + "\n");
    return 1;
  }
}

function runBuild(target: string): number {
  const r = loadOr2();
  if (typeof r === "number") return r;
  try {
    fs.mkdirSync("out", { recursive: true });
  } catch {
    process.stderr.write("建 out/ 失败\n");
    return 1;
  }
  let p;
  try {
    p = compileStmts(r.stmts);
  } catch (e) {
    process.stderr.write("检查未通过:\n");
    process.stderr.write((e instanceof DiagList ? formatDiags(e.diags) : String(e)) + "\n");
    return 1;
  }
  if (target === "go") {
    let src: string;
    try {
      src = transpile(p);
    } catch (e) {
      process.stderr.write("→Go 转译失败: " + String(e) + "\n");
      return 1;
    }
    fs.writeFileSync(path.join("out", "main.go"), src, "utf8");
    process.stdout.write("out/main.go（go build ./out 或 go run ./out/main.go 执行）\n");
    return 0;
  }
  if (target === "js") {
    let src: string;
    try {
      src = transpileJS(p);
    } catch (e) {
      process.stderr.write("→JS 转译失败: " + String(e) + "\n");
      return 1;
    }
    fs.writeFileSync(path.join("out", "main.mjs"), src, "utf8");
    process.stdout.write("out/main.mjs（node out/main.mjs 执行）\n");
    return 0;
  }
  process.stderr.write("未知目标: " + target + "（可选 go / js）\n");
  return 2;
}

function runConformance(root: string): number {
  const { results, fails } = runSuite(root);
  for (const r of results) {
    if (!r.ok) {
      process.stdout.write(`FAIL ${r.category}/${r.name}${r.skipped === true ? " (skipped)" : ""}: ${r.note ?? ""}\n`);
    }
  }
  process.stdout.write(`conformance: ${results.filter((r) => r.ok).length}/${results.length} 通过，${results.filter((r) => !r.ok && r.skipped !== true).length} 失败\n`);
  return fails > 0 ? 1 : 0;
}

// —— REPL ——

async function repl(): Promise<number> {
  const it = new Interp({ write: (s) => process.stdout.write(s) });
  process.stdout.write(`lang-ts v${version} — :help 帮助, :quit 退出\n`);
  const rl = readline.createInterface({ input: process.stdin, terminal: false });
  let buf = "";
  let prompt = "lang-go> ";
  const loop = (): void => {
    process.stdout.write(prompt);
  };
  loop();
  rl.on("line", (line: string) => {
    if (buf === "") {
      const t = line.trim();
      if (t === "") {
        loop();
        return;
      }
      if (t === ":quit" || t === ":q" || t === "exit") {
        rl.close(); // close 事件统一解析退出码（等 stdout 排空）
        return;
      }
      if (t === ":help") {
        process.stdout.write("输入 lang-go 源码回车执行；未闭合的块会进入续行模式；:quit 退出\n");
        loop();
        return;
      }
    }
    buf += line + "\n";
    try {
      const prog = syntaxParse(buf);
      const { val, has } = it.evalChunk(prog);
      if (has && val !== undefined) process.stdout.write("=> " + fmt(val) + "\n");
      buf = "";
      prompt = "lang-go> ";
    } catch (e) {
      const incomplete = e instanceof Error && "incomplete" in e && (e as { incomplete?: boolean }).incomplete === true;
      if (incomplete) {
        prompt = "  ... ";
      } else {
        process.stdout.write("错误: " + String(e) + "\n");
        buf = "";
        prompt = "lang-go> ";
      }
    }
    loop();
  });
  // write 回调在待写数据全部排空后触发（管道写异步，直接 exit 会截断求值输出）
  return new Promise<number>((resolve) => {
    rl.on("close", () => {
      process.stdout.write("\n", () => resolve(0));
    });
  });
}

process.exitCode = 1;
main().then((code) => process.exit(code));
