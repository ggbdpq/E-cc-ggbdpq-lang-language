package ir

// IR 测试缝：Lower 产出的 dump golden snapshot + 降级不变量。

import (
	"os"
	"testing"

	"lang-go/internal/hir"
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
)

func lowerSrc(t *testing.T, src string) *Prog {
	t.Helper()
	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}
	res := semantic.Resolve(prog)
	if res.HasErrors() {
		t.Fatalf("resolver 诊断: %v", res.Diags)
	}
	ty := types.Check(prog, res)
	if ty.HasErrors() {
		t.Fatalf("checker 诊断: %v", ty.Diags)
	}
	return Lower(hir.Build(prog, res, ty))
}

func TestSnapshot(t *testing.T) {
	cases := map[string]string{
		"basics": `
fn add(a: int, b: int): int {
    return a + b
}
fn main() {
    let n = add(1, 2)
    println(n)
}`,
		"result": `
fn parse(s: string): Result[int, error] {
    if s == "" {
        return Err(err("空"))
    }
    return Ok(int(s))
}
fn run(s: string): Result[int, error] {
    let v = parse(s)?
    return Ok(v * 2)
}
fn main() {
    println(match run("21") {
        Ok(v) => v
        Err(e) => -1
    })
}`,
		"collections": `
struct Point {
    x: int
    y: int
}
fn main() {
    let xs = [1, 2, 3]
    let m = {"a": 1}
    let p = Point{x: 1, y: 2}
    xs[0] = 9
    var total = 0
    for x in xs {
        total += x
    }
    println(xs, m, p.x, total, len(0..3))
}`,
		"concurrency": `
fn emit(ch: chan[string]) {
    send(ch, "hi")
}
fn main() {
    let ch = makeChan()
    go emit(ch)
    println(recv(ch))
}`,
	}
	for name, src := range cases {
		name, src := name, src
		t.Run(name, func(t *testing.T) {
			got := Dump(lowerSrc(t, src))
			golden := "testdata/" + name + ".irdump"
			if os.Getenv("UPDATE_GOLDEN") != "" {
				if err := os.MkdirAll("testdata", 0o755); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(golden, []byte(got), 0o644); err != nil {
					t.Fatal(err)
				}
				return
			}
			want, err := os.ReadFile(golden)
			if err != nil {
				t.Fatalf("缺少 golden（UPDATE_GOLDEN=1 生成）: %v", err)
			}
			if got != string(want) {
				t.Fatalf("dump 与 golden 不一致\n=== got ===\n%s\n=== want ===\n%s", got, string(want))
			}
		})
	}
}

func Test降级不变量(t *testing.T) {
	p := lowerSrc(t, `
fn emit(ch: chan[int]) {
    send(ch, 1)
}
fn main() {
    let ch = makeChan()
    go emit(ch)
    var n = 0
    for x in [1, 2] {
        n += x
    }
    println(n, recv(ch))
}`)
	// Spawn 保留
	if p.Main == nil || len(p.Main.Body) == 0 {
		t.Fatalf("main 缺失")
	}
	var hasSpawn, hasFor bool
	var walk func(list []Stmt)
	walk = func(list []Stmt) {
		for _, s := range list {
			switch st := s.(type) {
			case *Spawn:
				hasSpawn = true
			case *ForIn:
				hasFor = true
				if st.Kind != IterSlice {
					t.Fatalf("数组 for-in 应归类 IterSlice")
				}
			case *If:
				walk(st.Then)
				walk(st.Else)
			case *While:
				walk(st.Body)
			}
		}
	}
	walk(p.Main.Body)
	if !hasSpawn || !hasFor {
		t.Fatalf("Spawn/ForIn 降级丢失")
	}
	// 可变局部进 locals 表且带类型
	var found bool
	for _, v := range p.Main.Locals {
		if v.Name == "n" && v.Mutable && v.Typ.Kind == types.KInt {
			found = true
		}
	}
	if !found {
		t.Fatalf("可变局部 n 未入表或类型丢失")
	}
}

func TestMethod降级为调用(t *testing.T) {
	p := lowerSrc(t, `
struct P {
    x: int
}
fn (p P).get(): int {
    return p.x
}
fn main() {
    let v = P{x: 1}.get()
    println(v)
}`)
	var method *Call
	var walkE func(e Expr)
	walkE = func(e Expr) {
		if c, ok := e.(*Call); ok && c.Method != nil && method == nil {
			method = c
		}
		switch n := e.(type) {
		case *Call:
			for _, a := range n.Args {
				walkE(a)
			}
			if n.Recv != nil {
				walkE(n.Recv)
			}
		case *Binary:
			walkE(n.L)
			walkE(n.R)
		}
	}

	walkS := func(list []Stmt) {
		for _, s := range list {
			switch st := s.(type) {
			case *Let:
				walkE(st.Init)
			case *Eval:
				walkE(st.X)
			case *Return:
				if st.Val != nil {
					walkE(st.Val)
				}
			}
		}
	}
	for _, f := range p.Fns {
		walkS(f.Body)
	}
	if method == nil || method.Method.Name != "get" || method.Recv == nil {
		t.Fatalf("方法调用应降级为 Call{Method, Recv}")
	}
}
