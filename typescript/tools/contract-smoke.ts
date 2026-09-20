// Agent Contract 全查询面冒烟：inspect ×9 / dump ×3 / check --json / verify
// 在一个含跨调用、字段、enum、extern 的 fixture 上逐项断言关键结构。
import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

const CLI = new URL("../dist/cli/main.js", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
const d = join(tmpdir(), "langts-contract-");
rmSync(d, { recursive: true, force: true });
mkdirSync(d, { recursive: true });

writeFileSync(
  join(d, "main.lang-go"),
  `import "./lib"

fn (p P).area(): int {
    return p.w * p.h
}

fn surface(sh: Shape): int {
    return match sh {
        Circle(r) => 3 * r * r
        Rect(w, h) => w * h
    }
}

fn helper(v: int): int {
    return surface(Circle(v))
}

fn emitCh(ch: chan[int]) {
    send(ch, 7)
}

fn main() {
    let p = P{w: 2, h: 3}
    println(p.area())
    println(helper(2))
    let ch = makeChan()
    go emitCh(ch)
    println(isNone(recv(ch)))
    println(isNone(None))
}
`,
);
writeFileSync(
  join(d, "lib.lang-go"),
  `struct P {
    w: int
    h: int
}

enum Shape {
    Circle(int)
    Rect(int, int)
}
`,
);

let failures = 0;
function run(args: string[]): { code: number; out: string } {
  const r = spawnSync(process.execPath, [CLI, ...args], { cwd: d, encoding: "utf8", timeout: 120000 });
  return { code: r.status ?? 1, out: (r.stdout ?? "") + (r.stderr ?? "") };
}
function json(args: string[]): { code: number; o: any } {
  const r = run(args);
  try {
    return { code: r.code, o: JSON.parse(r.out) };
  } catch {
    return { code: r.code, o: null };
  }
}
function check(name: string, cond: boolean, detail = ""): void {
  if (cond) console.log(`PASS ${name}`);
  else {
    console.log(`FAIL ${name} ${detail}`);
    failures++;
  }
}

// —— 信封通用 ——
const env = json(["inspect", "symbols", "--json"]);
check("信封三字段", env.o?.schemaVersion === "1" && env.o?.toolVersion === "1.0.0-rc.1" && env.o?.kind === "inspect.symbols");

// —— inspect symbols ——
const syms = env.o?.symbols ?? [];
check("symbols 含 P/Shape/area/surface/helper", ["P", "Shape", "area", "surface", "helper"].every((n) => syms.some((s: any) => s.name === n)));
check("symbols 预载不输出", !syms.some((s: any) => s.name === "println" || s.name === "Some"));
check("method 可见性 public", syms.find((s: any) => s.name === "area")?.visibility === "public");

// —— inspect refs ——
const refs = json(["inspect", "refs", "--json"]).o?.references ?? [];
check("refs 含 pattern/type/read 类别", ["pattern", "type", "read"].every((k) => refs.some((r: any) => r.referenceKind === k)));
check("refs 排序 symbolId 升序", refs.every((r: any, i: number) => i === 0 || refs[i - 1].symbolId <= r.symbolId));

// —— inspect type ——
const ty = json(["inspect", "type", "--file", "main.lang-go", "--line", "4", "--column", "13", "--json"]);
check("type 命中节点", ty.code === 0 && ty.o?.nodes?.[0]?.type === "int", JSON.stringify(ty.o?.nodes?.[0]));

// —— inspect dependencies ——
const deps = json(["inspect", "dependencies", "--json"]).o?.dependencies ?? [];
check("dependencies 含 lib", deps.some((x: any) => x.module === "main.lang-go" && x.imports.includes("./lib")));

// —— inspect effects ——
const effs = json(["inspect", "effects", "--json"]).o?.effects ?? [];
const mainEff = effs.find((e: any) => e.name === "main");
check("effects main 含 CHANNEL", mainEff?.effects?.includes("CHANNEL") === true);
check("effects PURE 兜底存在", effs.some((e: any) => e.effects?.[0] === "PURE"));

// —— inspect callers / callees / impact ——
const helperSym = syms.find((s: any) => s.name === "helper");
const callers = json(["inspect", "callers", "--symbol", String(helperSym?.symbolId), "--json"]).o?.callers ?? [];
check("callers main→helper", callers.some((c: any) => c.name === "main"));
const surfaceSym = syms.find((s: any) => s.name === "surface");
const callees = json(["inspect", "callees", "--symbol", String(helperSym?.symbolId), "--json"]).o?.callees ?? [];
check("callees helper→surface", callees.some((c: any) => c.name === "surface"));
const impact = json(["inspect", "impact", "--symbol", String(surfaceSym?.symbolId), "--json"]).o?.impact;
check("impact 有 note 与 direct", typeof impact?.note === "string" && (impact?.direct?.length ?? 0) >= 1);

// —— inspect references / dependency ——
const refsFiltered = json(["inspect", "references", "--symbol", String(surfaceSym?.symbolId), "--json"]).o?.references ?? [];
check("references 按符号过滤", refsFiltered.every((r: any) => r.symbolId === surfaceSym?.symbolId) && refsFiltered.length >= 1);
const dep = json(["inspect", "dependency", "--json"]).o?.dependency;
check("dependency 默认 main.lang-go", dep?.module === "main.lang-go" && dep?.imports?.includes("./lib"));

// —— dump ast/hir/ir ——
for (const k of ["ast", "hir", "ir"]) {
  const dd = json(["dump", k, "--json"]);
  check(`dump ${k} 出节点`, (dd.o?.nodes?.length ?? 0) > 0);
}
const irNodes = json(["dump", "ir", "--json"]).o?.nodes ?? [];
check("dump ir 节点无 span（协议约定）", irNodes.every((n: any) => n.span === undefined));

// —— check --json 成功/失败两态 ——
const okCheck = json(["check", "--json"]);
check("check --json 成功态", okCheck.o?.success === true && okCheck.o?.diagnostics?.length === 0);
writeFileSync(join(d, "bad.lang-go"), "fn broken() {\n    let x = 1 + 2.5\n}\n");
const badCheck = json(["check", "--json"]);
const badDiag = badCheck.o?.diagnostics?.[0];
check(
  "check --json 失败态带 ruleId",
  badCheck.o?.success === false && badDiag?.code === "E3002" && badDiag?.ruleId === "TYPE-ASSIGN-001" && badDiag?.severity === "error" && badDiag?.primarySpan?.start?.line === 2,
  JSON.stringify(badDiag),
);

// —— verify（九阶段） ——
rmSync(join(d, "bad.lang-go"));
const v = run(["verify", "--json"]);
let vOk = false;
try {
  const vo = JSON.parse(v.out);
  vOk = vo.success === true && vo.stages?.length >= 9 && vo.stages[vo.stages.length - 1]?.stage === "differential";
} catch {}
check("verify 九阶段全过", vOk, v.out.slice(0, 200));

console.log(`\n契约面冒烟: ${failures === 0 ? "全部通过" : failures + " 失败"}`);
rmSync(d, { recursive: true, force: true });
process.exit(failures === 0 ? 0 : 1);
