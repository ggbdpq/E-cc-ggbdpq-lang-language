package hir

// HIR 测试缝：Build 产出的 dump golden snapshot + 关键脱糖行为 + NodeID 稳定性。

import (
	"os"
	"testing"

	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
)

func buildSrc(t *testing.T, src string) *Prog {
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

	return Build(prog, res, ty)
}

// TestSnapshot golden 测试：dump 变更 = 协议变更，必须显式更新 testdata.
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
		"match": `
enum Shape {
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
    println(area(Circle(2.0)))
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
    println(xs, m, p.x, len(0..3))
}`,
	}
	for name, src := range cases {
		name, src := name, src
		t.Run(name, func(t *testing.T) {
			got := Dump(buildSrc(t, src))
			golden := "testdata/" + name + ".hirdump"

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

func Test脱糖(t *testing.T) {
	// 裸零参构造器 → Ctor 节点
	p := buildSrc(t, "fn main() {\nlet n = None\nprintln(n)\n}")

	var ctor *Ctor

	WalkProg(p, nil, func(e Expr) {
		if c, ok := e.(*Ctor); ok && ctor == nil {
			ctor = c
		}
	}, nil)

	if ctor == nil || ctor.CtorSym.Name != "None" {
		t.Fatalf("None 应脱糖为 Ctor 节点")
	}

	// 方法调用 → MethodCall
	p = buildSrc(t, `
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

	var mc *MethodCall

	WalkProg(p, nil, func(e Expr) {
		if m, ok := e.(*MethodCall); ok && mc == nil && m.Method != nil {
			mc = m
		}
	}, nil)

	if mc == nil || mc.Method.Name != "get" {
		t.Fatalf("obj.get() 应脱糖为 MethodCall")
	}

	// go 语句 → Spawn
	p = buildSrc(t, `
fn emit(ch: chan[int]) {
    send(ch, 1)
}
fn main() {
    let ch = makeChan()
    go emit(ch)
    println(recv(ch))
}`)

	var spawn bool

	WalkProg(p, func(s Stmt) {
		if _, ok := s.(*Spawn); ok {
			spawn = true
		}
	}, nil, nil)

	if !spawn {
		t.Fatalf("go emit(ch) 应变为 Spawn")
	}

	// && || → AndThen/OrElse
	p = buildSrc(t, "fn main() {\nif true && false || true {\nprintln(1)\n}\n}")

	var and, or bool

	WalkProg(p, nil, func(e Expr) {
		switch e.(type) {
		case *AndThen:
			and = true
		case *OrElse:
			or = true
		}
	}, nil)

	if !and || !or {
		t.Fatalf("&&/|| 应分别为 AndThen/OrElse")
	}
}

func TestNodeId稳定且先序(t *testing.T) {
	src := "fn main() {\nlet a = 1\nlet b = a + 2\nprintln(b)\n}"
	collect := func(p *Prog) []NodeID {
		var ids []NodeID

		WalkProg(p, nil, func(e Expr) { ids = append(ids, e.Meta().ID) }, nil)

		return ids
	}
	ids1 := collect(buildSrc(t, src))

	ids2 := collect(buildSrc(t, src))
	if len(ids1) < 5 {
		t.Fatalf("节点数异常: %d", len(ids1))
	}

	for i := range ids1 {
		if ids1[i] != ids2[i] {
			t.Fatalf("NodeID 不稳定: %v vs %v", ids1, ids2)
		}

		if i > 0 && ids1[i] <= ids1[i-1] {
			t.Fatalf("NodeID 非严格递增: %v", ids1)
		}
	}
}

func Test节点元数据齐全(t *testing.T) {
	p := buildSrc(t, `
fn f(a: int): int {
    return a + 1
}
fn main() {
    println(f(2))
}`)
	count := 0

	WalkProg(p, nil, func(e Expr) {
		n := e.Meta()
		if n.ID == 0 {
			t.Fatalf("节点缺 ID")
		}

		if n.Typ == nil {
			t.Fatalf("节点 %d 缺类型", n.ID)
		}

		count++
	}, nil)

	if count < 6 {
		t.Fatalf("遍历节点过少: %d", count)
	}
}
