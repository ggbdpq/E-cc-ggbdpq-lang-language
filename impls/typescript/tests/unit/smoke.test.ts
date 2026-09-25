// 冒烟测试：解释器基本执行面

import { describe, expect, it } from "vitest";
import { Interp } from "../../src/interp/interp.js";
import { parseSource } from "../../src/pipeline/pipeline.js";
import { checkSource } from "../../src/pipeline/pipeline.js";

class Buf {
  s = "";
  write(x: string): void {
    this.s += x;
  }
}

function run(src: string): { out: string; err?: string } {
  const buf = new Buf();
  const it = new Interp(buf);
  try {
    it.run(parseSource(src, "main.lang-go"));
    return { out: buf.s };
  } catch (e) {
    return { out: buf.s, err: e instanceof Error ? e.message : String(e) };
  }
}

describe("interp smoke", () => {
  it("hello world", () => {
    expect(run(`fn main() {\n    println("hello, conformance")\n}`).out).toBe("hello, conformance\n");
  });

  it("arithmetic precedence", () => {
    expect(run(`fn main() { println(1 + 2 * 3, (1 + 2) * 3) }`).out).toBe("7 9\n");
  });

  it("struct value semantics", () => {
    const src = `
struct P { x: int }
fn main() {
    let a = P{x: 1}
    let b = a
    b.x = 99
    println(a.x, b.x)
}
`;
    expect(run(src).out).toBe("1 99\n");
  });

  it("array reference semantics + push alias", () => {
    const src = `
fn main() {
    let a = [1, 2]
    let b = a
    push(b, 3)
    println(a)
    a += [4]
    println(len(a), a[3])
}
`;
    expect(run(src).out).toBe("[1, 2, 3]\n4 4\n");
  });

  it("match enum exhaustive", () => {
    const src = `
enum Shape { Circle(int) Square(int) }
fn area(s: Shape): int {
    return match s {
        Circle(r) => 3 * r * r
        Square(a) => a * a
    }
}
fn main() {
    println(area(Circle(2)), area(Square(4)))
}
`;
    expect(run(src).out).toBe("12 16\n");
  });

  it("closures capture by reference", () => {
    const src = `
fn main() {
    var n = 0
    let inc = fn() { n += 1 }
    inc()
    inc()
    println(n)
}
`;
    expect(run(src).out).toBe("2\n");
  });

  it("option/result and try propagation", () => {
    const src = `
fn find(v: int): Result[int, error] {
    if v > 0 {
        return Ok(v)
    }
    return Err(err("non-positive"))
}
fn run(v: int): Result[int, error] {
    let x = find(v)?
    return Ok(x * 2)
}
fn main() {
    match run(5) {
        Ok(v) => println("ok", v)
        Err(e) => println("err", e.msg)
    }
    match run(-1) {
        Ok(v) => println("ok", v)
        Err(e) => println("err", e.msg)
    }
}
`;
    expect(run(src).out).toBe("ok 10\nerr non-positive\n");
  });

  it("string index/slice by code points", () => {
    expect(run(`fn main() { let s = "中文ab" println(len(s), s[0], s[1:3]) }`).out).toBe("4 中 文a\n");
  });

  it("json roundtrip basics", () => {
    const src = `
fn main() {
    let v = jsonParse("{\\"b\\": 2, \\"a\\": [1, 2.5, null]}")
    println(jsonStringify(v))
    println(isNone(v["a"][2]))
}
`;
    expect(run(src).out).toBe('{"a":[1,2.5,null],"b":2}\ntrue\n');
  });

  it("go statement runs synchronously", () => {
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
    expect(run(src).out).toBe("Ok(42)\n");
  });

  it("runtime error: divide by zero", () => {
    expect(run(`fn main() { let a = 1 / 0 }`).err).toBe("第1行: 整数除零");
  });

  it("check: static diagnostics", () => {
    expect(checkSource(`fn main() { let x = 1 + 2.5 }`).map((d) => d.code)).toEqual(["E3002"]);
  });

  it("int64 wraparound arithmetic", () => {
    const src = `fn main() {
    let big = 9223372036854775807
    println(big + 1)
    println(-9223372036854775807 - 2)
}`;
    expect(run(src).out).toBe("-9223372036854775808\n9223372036854775807\n");
  });

  it("float formatting matches Go %v", () => {
    const src = `fn main() {
    println(1.0)
    println(2.5)
    println(0.1)
    println(float("1e21"))
    println(0.00001)
    println(100000000000000000000.0)
    println(1000000.0)
    println(999999.0)
    println(123456.0)
    println(1234567.0)
    println(123456789.5)
    println(-0.0 + 0.0)
}`;
    expect(run(src).out).toBe("1\n2.5\n0.1\n1e+21\n1e-05\n1e+20\n1e+06\n999999\n123456\n1.234567e+06\n1.234567895e+08\n0\n");
  });
});
