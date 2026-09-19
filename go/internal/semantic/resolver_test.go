package semantic

// Resolver 测试缝：Resolve(prog) 的诊断码与符号/引用绑定（公开行为，不测内部结构）。

import (
	"strings"
	"testing"

	"lang-go/internal/syntax"
)

func resolve(t *testing.T, src string) *Result {
	t.Helper()
	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}
	return Resolve(prog)
}

func codes(res *Result) []string {
	var out []string
	for _, d := range res.Diags {
		out = append(out, d.Code)
	}
	return out
}

func wantCodes(t *testing.T, res *Result, want ...string) {
	t.Helper()
	got := codes(res)
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("诊断码 = %v, 期望 %v\n诊断: %v", got, want, res.Diags)
	}
}

func Test合法程序无诊断(t *testing.T) {
	src := `
struct Point { x: int
    y: int
}
enum Shape { Circle(float)
    Unit
}
fn (p Point).dist2(): int {
    return p.x * p.x + p.y * p.y
}
fn area(s: Shape): float {
    return match s {
        Circle(r) => r
        Unit => 0.0
    }
}
let scale = 2
fn main() {
    let p = Point{x: 1, y: 2}
    var n = scale
    n = n + p.dist2()
    if n > 0 {
        let m = n
        println(m)
    }
    for i in 0..3 {
        println(i)
    }
    let s = match Circle(1.0) {
        Circle(r) => r
        Unit => 0.0
    }
    println(s)
}`
	res := resolve(t, src)
	wantCodes(t, res)
}

func Test遮蔽合法(t *testing.T) {
	res := resolve(t, `
fn f(): int {
    let x = 1
    if true {
        let x = 2
        println(x)
    }
    return x
}
fn main() { }`)
	wantCodes(t, res)
}

func Test重复定义(t *testing.T) {
	res := resolve(t, "fn f() { }\nfn f() { }\nfn main() { }")
	wantCodes(t, res, E2001)

	res = resolve(t, "struct P { x: int }\nstruct P { y: int }\nfn main() { }")
	wantCodes(t, res, E2001)

	res = resolve(t, "fn main() {\nlet a = 1\nlet a = 2\n}")
	wantCodes(t, res, E2001)

	res = resolve(t, "struct P { x: int }\nfn (p P).a() { }\nfn (p P).a() { }\nfn main() { }")
	wantCodes(t, res, E2001)
}

func Test未定义名(t *testing.T) {
	res := resolve(t, "fn main() {\nprintln(nope)\n}")
	wantCodes(t, res, E2002)

	res = resolve(t, "fn main() {\nmissingFn(1)\n}")
	wantCodes(t, res, E2002)

	res = resolve(t, "fn main() {\nlet p = Nope{x: 1}\n}")
	wantCodes(t, res, E2002)

	res = resolve(t, "fn main() {\nlet x = match 1 { _ => Missing(1) }\n}")
	wantCodes(t, res, E2002)
}

func Test构造器冲突(t *testing.T) {
	res := resolve(t, "enum A { Some(int) }\nfn main() { }")
	wantCodes(t, res, E2003)

	res = resolve(t, "enum A { C(int) }\nenum D { C }")
	wantCodes(t, res, E2003)

	res = resolve(t, "enum A { println }\nfn main() { }")
	wantCodes(t, res, E2006)
}

func Test方法接收者未知struct(t *testing.T) {
	res := resolve(t, "fn (p Ghost).a(): int { return 1 }\nfn main() { }")
	wantCodes(t, res, E2004)
}

func Test对不可变绑定赋值(t *testing.T) {
	res := resolve(t, "fn main() {\nlet a = 1\na = 2\n}")
	wantCodes(t, res, E2005)

	res = resolve(t, "fn main() {\nvar a = 1\na = 2\n}")
	wantCodes(t, res)
}

func Test循环与函数边界(t *testing.T) {
	res := resolve(t, "fn main() {\nbreak\n}")
	wantCodes(t, res, E2007)

	res = resolve(t, "return 1")
	wantCodes(t, res, E2008)

	res = resolve(t, "fn main() {\nfor i in 0..2 {\nbreak\n}\n}")
	wantCodes(t, res)
}

func TestGo目标须为顶层函数(t *testing.T) {
	res := resolve(t, "fn main() {\ngo nope()\n}")
	wantCodes(t, res, E2002)

	res = resolve(t, "fn emit(ch: chan[int]) { }\nfn main() {\nlet ch = makeChan()\ngo emit(ch)\n}")
	wantCodes(t, res)
}

func Test全局前向引用报未定义(t *testing.T) {
	res := resolve(t, "let a = b\nlet b = 1\nfn main() { }")
	wantCodes(t, res, E2002)
}

func TestSymbolId稳定(t *testing.T) {
	src := "struct P { x: int }\nfn main() {\nlet p = P{x: 1}\nprintln(p.x)\n}"
	a := resolve(t, src)
	b := resolve(t, src)
	if len(a.Symbols) == 0 || len(a.Symbols) != len(b.Symbols) {
		t.Fatalf("符号数不稳定: %d vs %d", len(a.Symbols), len(b.Symbols))
	}
	for i := range a.Symbols {
		if a.Symbols[i].ID != b.Symbols[i].ID || a.Symbols[i].Name != b.Symbols[i].Name {
			t.Fatalf("符号 %d 不稳定: %+v vs %+v", i, a.Symbols[i], b.Symbols[i])
		}
	}
}

func Test引用绑定正确(t *testing.T) {
	res := resolve(t, "struct P { x: int }\nfn f(p: P): int { return p.x }\nfn main() {\nlet v = f(P{x: 1})\nprintln(v)\n}")
	wantCodes(t, res)
	// main 里的 f 引用应绑定到 KFn 符号
	var fSym *Symbol
	for _, s := range res.Symbols {
		if s.Name == "f" && s.Kind == KFn {
			fSym = s
		}
	}
	if fSym == nil {
		t.Fatalf("未找到 f 符号")
	}
	found := false
	for expr, sym := range res.Refs {
		if id, ok := expr.(*syntax.Ident); ok && id.Name == "f" && sym == fSym {
			found = true
		}
	}
	if !found {
		t.Fatalf("Ident f 未绑定到 fn 符号")
	}
	// CasePat + 子模式 BindPat 都应进 PatRefs（预载 Ok/Err 各带一个绑定）
	res = resolve(t, "fn g(): Result[int, error] { return Ok(1) }\nfn main() {\nmatch g() {\nOk(v) => println(v)\nErr(e) => println(e.msg)\n}\n}")
	wantCodes(t, res)
	if len(res.PatRefs) != 4 {
		t.Fatalf("PatRefs = %d, 期望 4（2 构造器 + 2 绑定）", len(res.PatRefs))
	}
}

func Test内建与预载符号可见(t *testing.T) {
	res := resolve(t, `fn main() {
    println(len("abc"))
    let n = isNone(None)
    let e = err("boom")
    println(e.msg)
}`)
	wantCodes(t, res)
	if res.Builtin["println"] == nil || res.Builtin["error"] == nil || res.Builtin["http"] == nil {
		t.Fatalf("预载符号缺失")
	}
}
