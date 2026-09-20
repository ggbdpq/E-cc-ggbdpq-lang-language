// CLI 全动词冒烟：fmt / fix / refactor ×3 / bind / test / REPL
// 每个动词在临时 fixture 上跑一遍并断言关键输出；exit code 非 0 即失败。
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const CLI = new URL("../dist/cli/main.js", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
let failures = 0;

function run(args: string[], cwd: string): { code: number; out: string } {
  const r = spawnSync(process.execPath, [CLI, ...args], { cwd, encoding: "utf8", timeout: 120000 });
  return { code: r.status ?? 1, out: (r.stdout ?? "") + (r.stderr ?? "") };
}

function check(name: string, cond: boolean, detail = ""): void {
  if (cond) console.log(`PASS ${name}`);
  else {
    console.log(`FAIL ${name} ${detail}`);
    failures++;
  }
}

const tmp = mkdtempSync(join(tmpdir(), "langts-verbs-"));

// —— 1. fmt：三格式 + 注释保留 + 幂等 ——
const dFmt = join(tmp, "fmt");
mkdirSync(dFmt);
writeFileSync(
  join(dFmt, "a.lang-go"),
  '// 头注释\nfn main() {\n// 行首\n    let x   =    1\n    println( x )  // 行尾\n}\n',
);
const rFmt1 = run(["fmt"], dFmt);
const fmtOnce = readFileSync(join(dFmt, "a.lang-go"), "utf8");
const rFmt2 = run(["fmt"], dFmt);
const fmtTwice = readFileSync(join(dFmt, "a.lang-go"), "utf8");
check("fmt 报告改动", rFmt1.out.includes("1 个文件已格式化"), rFmt1.out);
check("fmt 二次无改动", rFmt2.out.includes("0 个文件已格式化"), rFmt2.out);
check("fmt 保留全部注释", fmtOnce.includes("// 头注释") && fmtOnce.includes("// 行首") && fmtOnce.includes("// 行尾"));
check("fmt 幂等", fmtOnce === fmtTwice);

let r: { code: number; out: string };
// —— 2. fix --safe：EOF 换行 + 行尾空白，--dry-run 与 --json ——
const dFix = join(tmp, "fix");
mkdirSync(dFix);
writeFileSync(join(dFix, "main.lang-go"), "fn main() {\n    println(1)   \n}");
const rFixDry = run(["fix", "--safe", "--dry-run", "--json"], dFix);
check("fix dry-run 报编辑集", rFixDry.out.includes('"dryRun": true') && rFixDry.out.includes('"startOffset"'));
check("fix dry-run 不落盘", readFileSync(join(dFix, "main.lang-go"), "utf8").includes("println(1)   "));
const rFixApply = run(["fix", "--safe", "--json"], dFix);
check("fix 应用并验证", rFixApply.out.includes('"applied": true') && rFixApply.code === 0);
check("fix 结果正确", readFileSync(join(dFix, "main.lang-go"), "utf8") === "fn main() {\n    println(1)\n}\n");

// —— 3. refactor rename：dry-run → apply → 验证闭环 ——
const dRef = join(tmp, "refactor");
mkdirSync(dRef);
writeFileSync(
  join(dRef, "main.lang-go"),
  "fn greet(): string {\n    return \"hi\"\n}\nfn main() {\n    println(greet())\n}\n",
);
r = run(["refactor", "rename", "--symbol", "greet", "--to", "hello"], dRef);
check("rename dry-run 出编辑集", r.out.includes('"applied": false') && r.out.includes('"editCount": 2'), r.out.slice(0, 200));
r = run(["refactor", "rename", "--symbol", "greet", "--to", "hello", "--apply"], dRef);
check("rename apply 成功", r.out.includes('"applied": true') && r.code === 0);
check("rename 语义生效", readFileSync(join(dRef, "main.lang-go"), "utf8").includes("hello()"));
r = run(["refactor", "rename", "--symbol", "greet", "--to", "hello"], dRef);
check("rename 未知符号报错", r.out.includes("未知符号") && r.code === 1);

// —— 4. refactor safe-remove：有引用拒绝 / 无引用删除 ——
writeFileSync(
  join(dRef, "main.lang-go"),
  "fn used(): int {\n    return 1\n}\nfn unused(): int {\n    return 2\n}\nfn main() {\n    println(used())\n}\n",
);
r = run(["refactor", "safe-remove", "--symbol", "unused", "--apply"], dRef);
check("safe-remove 删除零引用 fn", r.code === 0 && !readFileSync(join(dRef, "main.lang-go"), "utf8").includes("unused"));
writeFileSync(
  join(dRef, "main.lang-go"),
  "fn used(): int {\n    return 1\n}\nfn main() {\n    println(used())\n}\n",
);
r = run(["refactor", "safe-remove", "--symbol", "used"], dRef);
check("safe-remove 有引用拒绝并列证据", r.out.includes('"removable": false') && r.out.includes("refSites") && r.code === 1);

// —— 5. refactor organize-imports ——
writeFileSync(
  join(dRef, "main.lang-go"),
  'import "./b"\nimport "./a"\nfn main() {\n    println(av + bv)\n}\n',
);
writeFileSync(join(dRef, "a.lang-go"), 'let av = 1\n');
writeFileSync(join(dRef, "b.lang-go"), 'let bv = 2\n');
r = run(["refactor", "organize-imports"], dRef);
check("organize-imports 排序", r.out.includes('"editCount": 1'), r.out.slice(0, 200));

// —— 6. bind：从 Go 源生成 extern 草案 ——
const dProj = join(tmp, "proj");
const dBind = join(dProj, "bind.d");
mkdirSync(dBind, { recursive: true });
writeFileSync(
  join(dBind, "lib.go"),
  "package lib\n\nfunc Add(a int, b int) int {\n\treturn a + b\n}\n\nfunc Fail() (int, error) {\n\treturn 0, nil\n}\n",
);
r = run(["bind"], dProj);
check("bind 生成草案", r.out.includes('extern "go"') && r.out.includes("fn Add(a: int, b: int): int") && r.out.includes("Result[int, error]"), r.out.slice(0, 200));

// —— 7. test：*_test.lang-go 装载与执行 ——
const dTest = join(tmp, "testpkg");
mkdirSync(dTest);
writeFileSync(dTest + "/main.lang-go", "fn main() {\n    println(\"main\")\n}\n");
writeFileSync(dTest + "/main_test.lang-go", "fn testAdd() {\n    assert(1 + 1 == 2)\n}\nfn testBad() {\n    assert(false, \"故意的\")\n}\n");
r = run(["test"], dTest);
check("test 报 PASS/FAIL 汇总", r.out.includes("PASS testAdd") && r.out.includes("FAIL testBad") && r.out.includes("1 通过, 1 失败"), r.out);
check("test 退出码 1", r.code === 1);

// —— 8. REPL：表达式求值 + 值回显 ——
const rRepl = spawnSync(process.execPath, [CLI], {
  input: "1 + 2\nlet s = \"a\" + \"b\"\ns\n:quit\n",
  encoding: "utf8",
  timeout: 60000,
});
// 顶层字符串按 RUNTIME-OUTPUT-003 不带引号（fmtVal top=true）
check("REPL 求值回显", rRepl.stdout.includes("=> 3") && rRepl.stdout.includes("=> ab"), JSON.stringify(rRepl.stdout.slice(0, 200)));
check("REPL 横幅", rRepl.stdout.includes("lang-ts v1.0.0"));

// —— 9. build：go/js 双产物 + 未知目标 ——
const dBuild = join(tmp, "buildpkg");
mkdirSync(dBuild);
writeFileSync(dBuild + "/main.lang-go", 'fn main() {\n    println("built")\n}\n');
r = run(["build"], dBuild);
check("build go 写 out/main.go", r.code === 0 && existsSync(join(dBuild, "out", "main.go")));
r = run(["build", "js"], dBuild);
check("build js 写 out/main.mjs", r.code === 0 && existsSync(join(dBuild, "out", "main.mjs")));
r = run(["build", "wasm"], dBuild);
check("build 未知目标退出码 2", r.code === 2);

console.log(`\n动词冒烟: ${failures === 0 ? "全部通过" : failures + " 失败"}`);
rmSync(tmp, { recursive: true, force: true });
process.exit(failures === 0 ? 0 : 1);
