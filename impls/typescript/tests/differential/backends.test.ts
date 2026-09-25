// 三路执行差分冒烟：解释器 / →Go 产物（go run）/ →JS 产物（node）
// 前置：宿主有 go 与 node 工具链；缺失时跳过（runner contract 的 skipped 语义）。

import { expect, it } from "vitest";
import { execFileSync } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { parseSource, analyzeStmts } from "../../src/pipeline/pipeline.js";
import { Interp } from "../../src/interp/interp.js";
import { transpile } from "../../src/backends/gen.js";
import { transpileJS } from "../../src/backends/genjs.js";

const hasGo = (() => {
  try {
    execFileSync("go", ["version"], { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
})();

class Buf {
  s = "";
  write(x: string): void {
    this.s += x;
  }
}

interface ThreeWay {
  interp: string;
  go?: string;
  js: string;
}

function threeWay(src: string): ThreeWay {
  const prog = parseSource(src, "main.lang-go");
  const buf = new Buf();
  new Interp(buf).run(prog);
  const ir = analyzeStmts(prog).prog;
  const jsSrc = transpileJS(ir);
  const tmp = mkdtempSync(join(tmpdir(), "langgo-ts-"));
  try {
    writeFileSync(join(tmp, "main.mjs"), jsSrc, "utf8");
    const js = execFileSync(process.execPath, [join(tmp, "main.mjs")], { encoding: "utf8" });
    let go: string | undefined;
    if (hasGo) {
      writeFileSync(join(tmp, "main.go"), transpile(ir), "utf8");
      writeFileSync(join(tmp, "go.mod"), "module langgogen\n\ngo 1.24\n", "utf8");
      go = execFileSync("go", ["run", "main.go"], { encoding: "utf8", cwd: tmp });
    }
    return { interp: buf.s, go, js };
  } finally {
    rmSync(tmp, { recursive: true, force: true });
  }
}

it("three-way hello", () => {
  const r = threeWay(`fn main() {
    println("hello, conformance")
}`);
  expect(r.interp).toBe("hello, conformance\n");
  expect(r.js).toBe("hello, conformance\n");
  if (r.go !== undefined) expect(r.go).toBe("hello, conformance\n");
});

it("three-way struct + enum + match + closures", () => {
  const src = `
struct P {
    x: int
    y: int
}
fn (p P).sum(): int {
    return p.x + p.y
}
enum Shape {
    Circle(float)
    Unit
}
fn describe(s: Shape): string {
    return match s {
        Circle(r) => "圆 r=" + str(r)
        Unit => "空"
    }
}
fn main() {
    let p = P{x: 1, y: 2}
    let q = p
    q.x = 99
    println(p.sum(), q.sum(), p.x)
    println(describe(Circle(2.5)), describe(Unit))
    var n = 10
    let bump = fn(): int { n += 1
        return n }
    println(bump(), n)
    let nums = [1, 2, 3]
    var total = 0
    for x in nums {
        total += x
    }
    println(total)
    let words = {"b": 2, "a": 1}
    for k in words {
        println(k, words[k])
    }
    println(1.0, 2.5, 0.00001, 123456789.5)
}
`;
  const r = threeWay(src);
  expect(r.js).toBe(r.interp);
  if (r.go !== undefined) expect(r.go).toBe(r.interp);
});

it("three-way result propagation", () => {
  const src = `
fn parse(v: int): Result[int, error] {
    if v < 0 {
        return Err(err("negative"))
    }
    return Ok(v * 2)
}
fn chain(v: int): Result[int, error] {
    let a = parse(v)?
    let b = parse(a)?
    return Ok(b + 1)
}
fn main() {
    match chain(3) {
        Ok(v) => println("ok", v)
        Err(e) => println("err", e.msg)
    }
    match chain(-1) {
        Ok(v) => println("ok", v)
        Err(e) => println("err", e.msg)
    }
}
`;
  const r = threeWay(src);
  expect(r.interp).toBe("ok 13\nerr negative\n");
  expect(r.js).toBe(r.interp);
  if (r.go !== undefined) expect(r.go).toBe(r.interp);
});

it("three-way concurrency sync subset", () => {
  const src = `
fn emit(ch: chan[int]) {
    send(ch, 42)
}
fn main() {
    let ch = makeChan()
    go emit(ch)
    let r = recv(ch)
    println(r)
}
`;
  const r = threeWay(src);
  expect(r.interp).toBe("Ok(42)\n");
  expect(r.js).toBe(r.interp);
  if (r.go !== undefined) expect(r.go).toBe(r.interp);
});
