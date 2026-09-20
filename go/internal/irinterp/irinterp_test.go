package irinterp

// irinterp 测试缝：与 AST 参考解释器（interp）输出逐字节对齐——IR 语义 = AST 语义。
// 系统性验证由 conformance 四路差分承担；此处是快速回归面。

import (
	"bytes"
	"testing"

	"lang-go/internal/interp"
	"lang-go/internal/pipeline"
	"lang-go/internal/syntax"
)

func runBoth(t *testing.T, src string) (string, string) {
	t.Helper()
	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}
	irp, err := pipeline.CompileStmts(prog)
	if err != nil {
		t.Fatalf("编译失败: %v", err)
	}
	var ib, ab bytes.Buffer
	if err := New(&ib).Run(irp); err != nil {
		t.Fatalf("irinterp 执行失败: %v", err)
	}
	if err := interp.New(&ab).Run(prog); err != nil {
		t.Fatalf("interp 执行失败: %v", err)
	}
	return ib.String(), ab.String()
}

func TestIrInterp对齐(t *testing.T) {
	cases := []struct{ name, src string }{
		{"算术与优先级", "fn main() {\n    println(2 + 3 * 4, (2 + 3) * 4, 10 / 3, 7 % 3, 2.5 * 2.0)\n}\n"},
		{"字符串与数组", `fn main() {
    let xs = [1, 2, 3]
    push(xs, 4)
    println(len(xs), xs[0], xs)
    println("a" + "b", len("héllo"), char(65))
}
`},
		{"映射与插入序", `fn main() {
    let m = {"b": 2, "a": 1}
    println(m)
    println(keys(m), values(m))
    println(has(m, "a"), has(m, "z"))
}
`},
		{"struct与值拷贝", `struct P {
    x: int
    y: int
}
fn (p P).sum(): int {
    return p.x + p.y
}
fn main() {
    let p = P{x: 3, y: 4}
    let q = p
    q.x = 100
    println(p.sum(), q.sum())
}
`},
		{"enum与match", `enum Shape {
    Circle(float)
    Unit
}
fn area(s: Shape): float {
    return match s {
        Circle(r) => 3.0 * r
        Unit => 0.0
    }
}
fn main() {
    println(area(Circle(2.0)), area(Unit))
}
`},
		{"Option与Result", `fn divide(a: int, b: int): Result[int, error] {
    if b == 0 {
        return Err(err("div by zero"))
    }
    return Ok(a / b)
}
fn safeDiv(a: int, b: int): Result[int, error] {
    let q = divide(a, b)?
    return Ok(q)
}
fn main() {
    println(safeDiv(9, 2))
    println(safeDiv(7, 0))
    println(Some(41), None, isNone(None))
}
`},
		{"闭包与求值顺序", `fn counter(): fn(): int {
    var n = 0
    return fn(): int {
        n = n + 1
        return n
    }
}
fn main() {
    let c = counter()
    println(c(), c(), c())
    let m = {}
    let k = 1
    m[k] = 2
    println(m[1])
    let arr = [1, 2]
    arr[0] += 10
    println(arr)
}
`},
		{"chan并发同步子集", `fn producer(ch: chan[int]) {
    send(ch, 42)
}
fn main() {
    let ch = makeChan()
    go producer(ch)
    let v = recv(ch)
    println(v)
}
`},
		{"JSON确定性", `fn main() {
    let m = {"b": 2, "a": 1}
    println(jsonStringify(m))
    println(jsonParse("{\"k\": [1, 2.5, null]}"))
}
`},
		{"控制流", `fn main() {
    var total = 0
    for x in [1, 2, 3] {
        total += x
    }
    for k in ["a", "b"] {
        println(k)
    }
    let i = 0
    while total > 100 {
        total = total - 1
    }
    println(total)
    println(match 5 {
        5 => "five"
        _ => "other"
    })
}
`},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			irOut, astOut := runBoth(t, c.src)
			if irOut != astOut {
				t.Fatalf("irinterp 与 interp 输出不一致\nir:  %q\nast: %q", irOut, astOut)
			}
		})
	}
}

func TestIrInterpErr传播到边界(t *testing.T) {
	_, astOut := runBoth(t, `fn divide(a: int, b: int): Result[int, error] {
    if b == 0 {
        return Err(err("div by zero"))
    }
    return Ok(a / b)
}
fn safeDiv(a: int, b: int): Result[int, error] {
    let q = divide(a, b)?
    return Ok(q)
}
fn main() {
    println(safeDiv(7, 0))
}
`)
	if astOut != "Err(error{msg: \"div by zero\"})\n" {
		t.Fatalf("Err 传播语义漂移: %q", astOut)
	}
}
