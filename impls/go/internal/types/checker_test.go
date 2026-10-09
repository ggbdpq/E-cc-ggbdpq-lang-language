package types

// Checker 测试缝：Check(prog, resolve(prog)) 的诊断码 + 全 corpus 零诊断。
// corpus（examples + m4 + selftest）是「现有合法程序零误杀」的回归门。

import (
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func checkSrc(t *testing.T, src string) (*Result, *semantic.Result) {
	t.Helper()

	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}

	res := semantic.Resolve(prog)
	if res.HasErrors() {
		t.Fatalf("resolver 意外诊断: %v", res.Diags)
	}

	return Check(prog, res), res
}

func wantDiagCodes(t *testing.T, res *Result, want ...string) {
	t.Helper()

	var got []string
	for _, d := range res.Diags {
		got = append(got, d.Code)
	}

	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("诊断码 = %v, 期望 %v\n诊断: %v", got, want, res.Diags)
	}
}

func Test赋值类型不匹配(t *testing.T) {
	res, _ := checkSrc(t, "fn main() {\nlet x: int = \"s\"\n}")
	wantDiagCodes(t, res, E3001)

	res, _ = checkSrc(t, "fn main() {\nlet x: int = 1\n}")
	wantDiagCodes(t, res)
}

func TestIntFloat无隐式转换(t *testing.T) {
	res, _ := checkSrc(t, "fn main() {\nlet x = 1 + 2.0\nprintln(x)\n}")
	wantDiagCodes(t, res, E3002)

	res, _ = checkSrc(t, "fn main() {\nprintln(float(1) + 2.0)\nprintln(1 + 2)\n}")
	wantDiagCodes(t, res)
}

func Test调用个数与类型(t *testing.T) {
	res, _ := checkSrc(t, "fn f(a: int): int { return a }\nfn main() {\nprintln(f(1, 2))\n}")
	wantDiagCodes(t, res, E3003)

	res, _ = checkSrc(t, "fn f(a: int): int { return a }\nfn main() {\nprintln(f(\"s\"))\n}")
	wantDiagCodes(t, res, E3001)

	res, _ = checkSrc(t, "fn f(a: int): int { return a }\nfn main() {\nprintln(f(2))\n}")
	wantDiagCodes(t, res)
}

func Test参数缺注解(t *testing.T) {
	res, _ := checkSrc(t, "fn f(a) { }\nfn main() { }")
	wantDiagCodes(t, res, E3007)

	res, _ = checkSrc(t, "fn main() {\nlet g = fn(a: int): int { return a }\nprintln(g(1))\n}")
	wantDiagCodes(t, res)

	res, _ = checkSrc(t, "fn main() {\nlet g = fn(a): int { return 1 }\nprintln(g(1))\n}")
	wantDiagCodes(t, res, E3007)
}

func Test问号约束(t *testing.T) {
	// ? 所在函数未注解 Result → E3005
	res, _ := checkSrc(t, "fn f(): Result[int, error] { return Ok(1) }\nfn g(): int {\nlet v = f()?\nreturn v\n}\nfn main() { }")
	wantDiagCodes(t, res, E3005)

	// 合法：? + Result 注解
	res, _ = checkSrc(t, "fn f(): Result[int, error] { return Ok(1) }\nfn g(): Result[int, error] {\nlet v = f()?\nreturn Ok(v + 1)\n}\nfn main() { }")
	wantDiagCodes(t, res)

	// ? 位置：嵌套表达式 → E3005
	res, _ = checkSrc(t, "fn f(): Result[int, error] { return Ok(1) }\nfn g(): Result[int, error] {\nreturn Ok(f()? + 1)\n}\nfn main() { }")
	wantDiagCodes(t, res, E3005)

	// ? 操作数非 Result → E3008
	res, _ = checkSrc(t, "fn g(): Result[int, error] {\nlet v = Some(1)?\nreturn Ok(1)\n}\nfn main() { }")
	wantDiagCodes(t, res, E3008)
}

func TestResultReturn形态(t *testing.T) {
	res, _ := checkSrc(t, "fn g(): Result[int, error] {\nreturn 1\n}\nfn main() { }")
	wantDiagCodes(t, res, E3006)

	res, _ = checkSrc(t, "fn g(): Result[int, error] {\nlet x = 1\nreturn Ok(x)\n}\nfn main() { }")
	wantDiagCodes(t, res)

	// Ok 不可用于非 return 位置（迁自 →Go/→JS 约束）
	res, _ = checkSrc(t, "fn main() {\nlet r = Ok(1)\nprintln(r)\n}")
	wantDiagCodes(t, res, E3006)

	// Ok 值类型不匹配
	res, _ = checkSrc(t, "fn g(): Result[int, error] {\nreturn Ok(\"s\")\n}\nfn main() { }")
	wantDiagCodes(t, res, E3001)
}

func TestResult第二参数须error(t *testing.T) {
	res, _ := checkSrc(t, "fn g(): Result[int, int] {\nreturn Ok(1)\n}\nfn main() { }")
	wantDiagCodes(t, res, E3009)
}

func Test字段访问(t *testing.T) {
	res, _ := checkSrc(t, "struct P { x: int }\nfn main() {\nlet p = P{x: 1}\nprintln(p.y)\n}")
	wantDiagCodes(t, res, E3004)

	res, _ = checkSrc(t, "struct P { x: int }\nfn main() {\nlet p = P{x: 1}\nprintln(p.x)\n}")
	wantDiagCodes(t, res)

	// 字段类型注解错位
	res, _ = checkSrc(t, "struct P { x: int }\nfn main() {\nlet p = P{x: \"s\"}\n}")
	wantDiagCodes(t, res, E3001)

	// 字段顺序须与声明一致（interp 运行时同规则）；两个错位各报一条
	res, _ = checkSrc(t, "struct P { x: int\n    y: int }\nfn main() {\nlet p = P{y: 1, x: 2}\n}")
	wantDiagCodes(t, res, E3001, E3001)
}

func TestMatch穷尽(t *testing.T) {
	res, _ := checkSrc(t, `
enum Shape { Circle(float)
    Unit
}
fn f(s: Shape): float {
    return match s {
        Circle(r) => r
    }
}
fn main() { }`)
	wantDiagCodes(t, res, E4001)

	res, _ = checkSrc(t, `
enum Shape { Circle(float)
    Unit
}
fn f(s: Shape): float {
    return match s {
        Circle(r) => r
        Unit => 0.0
    }
}
fn main() { }`)
	wantDiagCodes(t, res)

	// Result 单臂 → E4001（三路分歧收口：interp 运行时错、→Go 语句位置静默）
	res, _ = checkSrc(t, "fn g(r: Result[int, error]) {\nmatch r {\nOk(v) => println(v)\n}\n}\nfn main() { }")
	wantDiagCodes(t, res, E4001)

	// enum match 混字面量臂
	res, _ = checkSrc(t, `
enum Shape { Circle(float)
    Unit
}
fn f(s: Shape): float {
    return match s {
        Circle(r) => r
        1.0 => 0.0
    }
}
fn main() { }`)
	wantDiagCodes(t, res, E4002)
}

func Test条件须bool(t *testing.T) {
	res, _ := checkSrc(t, "fn main() {\nif 1 {\nprintln(1)\n}\n}")
	wantDiagCodes(t, res, E3011)

	res, _ = checkSrc(t, "fn main() {\nif true {\nprintln(1)\n}\n}")
	wantDiagCodes(t, res)
}

func Test迭代与索引(t *testing.T) {
	res, _ := checkSrc(t, "fn main() {\nfor x in 42 {\nprintln(x)\n}\n}")
	wantDiagCodes(t, res, E3010)

	res, _ = checkSrc(t, "fn main() {\nlet xs = [1, 2]\nprintln(xs[\"a\"])\n}")
	wantDiagCodes(t, res, E3013)

	res, _ = checkSrc(t, "fn main() {\nlet m = {\"a\": 1}\nprintln(m[true])\n}")
	wantDiagCodes(t, res)

	res, _ = checkSrc(t, "fn main() {\nlet xs = [1, 2]\nfor x in xs {\nprintln(x)\n}\nfor c in \"abc\" {\nprintln(c)\n}\n}")
	wantDiagCodes(t, res)
}

func Test动态值Any保守放行(t *testing.T) {
	res, _ := checkSrc(t, `fn main() {
    let v = jsonParse("{\"a\": [1, null]}")
    println(v["a"][0] + 1)
    let n = isNone(v["a"][1])
    println(n)
    for k in keys(v) {
        println(k)
    }
}`)
	wantDiagCodes(t, res)
}

func Test方法与struct(t *testing.T) {
	res, _ := checkSrc(t, `
struct Point { x: int
    y: int
}
fn (p Point).dist2(): int {
    return p.x * p.x + p.y * p.y
}
fn main() {
    let p = Point{x: 1, y: 2}
    println(p.dist2())
}`)
	wantDiagCodes(t, res)

	res, _ = checkSrc(t, `
struct Point { x: int
    y: int
}
fn (p Point).dist2(): int {
    return p.x * p.x + p.y * p.y
}
fn main() {
    let p = Point{x: 1, y: 2}
    println(p.nope())
}`)
	wantDiagCodes(t, res, E3004)
}

func Test嵌套类型声明拒绝(t *testing.T) {
	// E2010 在 resolver 层报（checkSrc 要求 resolver 干净，故直测 resolver）
	prog, err := syntax.Parse("fn main() {\nstruct P { x: int }\nprintln(1)\n}")
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}

	res := semantic.Resolve(prog)
	if len(res.Diags) != 1 || res.Diags[0].Code != "E2010" {
		t.Fatalf("期望 [E2010]，得到 %v", res.Diags)
	}
}

func Test泛型与chan(t *testing.T) {
	res, _ := checkSrc(t, `
fn first[T](xs: [T]): Option[T] {
    if len(xs) == 0 {
        return None
    }
    return Some(xs[0])
}
fn emit(ch: chan[string]) {
    send(ch, "hi")
}
fn main() {
    let ch = makeChan()
    go emit(ch)
    println(recv(ch))
    println(first([1, 2]))
}`)
	wantDiagCodes(t, res)
}

func Test闭包与函数值(t *testing.T) {
	res, _ := checkSrc(t, `
fn apply(f: fn(int): int, v: int): int {
    return f(v)
}
fn main() {
    println(apply(fn(x: int): int { return x * 2 }, 21))
    let g = fn(a: int): int { return a + 1 }
    println(g(1))
}`)
	wantDiagCodes(t, res)
}

// ———— 契约点：ADR-0005 / NAME-SCOPE-001 ————

func Test赋值顶层问号放行(t *testing.T) {
	// ADR-0005：赋值右值位置的顶层 ? 合法（所在函数注解 Result 时零诊断）
	res, _ := checkSrc(t, "fn f(): Result[int, error] { return Ok(1) }\nfn g(): Result[int, error] {\nvar x = 0\nx = f()?\nreturn Ok(x)\n}\nfn main() { }")
	wantDiagCodes(t, res)

	// 复合赋值右值同规则
	res, _ = checkSrc(t, "fn f(): Result[int, error] { return Ok(1) }\nfn g(): Result[int, error] {\nvar x = 0\nx += f()?\nreturn Ok(x)\n}\nfn main() { }")
	wantDiagCodes(t, res)

	// 所在函数未注解 Result 仍按 E3005 拒绝
	res, _ = checkSrc(t, "fn f(): Result[int, error] { return Ok(1) }\nfn g() {\nvar x = 0\nx = f()?\n}\nfn main() { }")
	wantDiagCodes(t, res, E3005)
}

// ———— 契约点：ADR-0005 扩展（2026-09-27）：表达式语句顶层 ? ————

func Test表达式语句顶层问号放行(t *testing.T) {
	// ADR-0005 扩展：表达式语句位置的顶层 ? 合法（所在函数注解 Result 时零诊断）
	res, _ := checkSrc(t, "fn f(): Result[int, error] { return Ok(1) }\nfn g(): Result[int, error] {\nf()?\nreturn Ok(0)\n}\nfn main() { }")
	wantDiagCodes(t, res)

	// 所在函数未注解 Result 仍按 E3005 拒绝
	res, _ = checkSrc(t, "fn f(): Result[int, error] { return Ok(1) }\nfn g() {\nf()?\n}\nfn main() { }")
	wantDiagCodes(t, res, E3005)

	// 语句内嵌套（非顶层）仍按位置非法拒绝
	res, _ = checkSrc(t, "fn f(): Result[int, error] { return Ok(1) }\nfn g(): Result[int, error] {\nf()? + 1\nreturn Ok(0)\n}\nfn main() { }")
	wantDiagCodes(t, res, E3005)
}

// findLetStmt 按 name 定位第一个 let（测试辅助）.
func findLetStmt(stmts []syntax.Stmt, name string) *syntax.LetStmt {
	for _, s := range stmts {
		switch st := s.(type) {
		case *syntax.LetStmt:
			if st.Name == name {
				return st
			}
		case *syntax.FnDecl:
			if r := findLetStmt(st.Body, name); r != nil {
				return r
			}
		case *syntax.IfStmt:
			if r := findLetStmt(st.Then, name); r != nil {
				return r
			}

			if r := findLetStmt(st.Else, name); r != nil {
				return r
			}
		case *syntax.WhileStmt:
			if r := findLetStmt(st.Body, name); r != nil {
				return r
			}
		case *syntax.ForInStmt:
			if r := findLetStmt(st.Body, name); r != nil {
				return r
			}
		}
	}

	return nil
}

// letInitType 取 let 绑定初值的推断类型（测试辅助）.
func letInitType(t *testing.T, res *Result, prog []syntax.Stmt, name string) *Type {
	t.Helper()

	d := findLetStmt(prog, name)
	if d == nil {
		t.Fatalf("未找到 let %s", name)
	}

	ty, ok := res.ExprTy[d.Init]
	if !ok {
		t.Fatalf("let %s 的初值无推断类型", name)
	}

	return ty
}

// checkSrcStrict 解析+检查，resolver 与 checker 均须零诊断（测试辅助）.
func checkSrcStrict(t *testing.T, src string) (*Result, []syntax.Stmt) {
	t.Helper()

	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}

	res := semantic.Resolve(prog)
	if res.HasErrors() {
		t.Fatalf("resolver 意外诊断: %v", res.Diags)
	}

	out := Check(prog, res)
	if out.HasErrors() {
		t.Fatalf("checker 意外诊断: %v", out.Diags)
	}

	return out, prog
}

// Test块内绑定类型不泄漏 NAME-SCOPE-001：块内 let/循环变量/match 臂绑定的类型
// 写当前块作用域，不得写入函数/全局层（跨块同名绑定不互相污染）。
func Test块内绑定类型不泄漏(t *testing.T) {
	// 块内 let 不覆盖函数层同名绑定：外层 a 仍是 int
	out, prog := checkSrcStrict(t, "fn main() {\nlet a = 1\nif true {\nlet a = \"s\"\n}\nlet c = a\nprintln(c)\n}")
	if got := letInitType(t, out, prog, "c"); got.Kind != KInt {
		t.Fatalf("块内 let 泄漏：let c = a 的类型 = %s, 期望 int", got.String())
	}

	// 循环变量写循环块层，不覆盖函数层同名绑定：循环后 x 仍是 string
	out, prog = checkSrcStrict(t, "fn main() {\nlet x = \"s\"\nfor x in [1, 2] {\nlet y = x\nprintln(y)\n}\nlet z = x\nprintln(z)\n}")
	if got := letInitType(t, out, prog, "z"); got.Kind != KString {
		t.Fatalf("循环变量泄漏：let z = x 的类型 = %s, 期望 string", got.String())
	}

	// match 臂绑定写臂层，不覆盖函数层同名绑定：match 后 v 仍是 string
	out, prog = checkSrcStrict(t, `
enum E { A(int)
    B
}
fn main() {
    let v = "s"
    var e = A(1)
    match e {
        A(v) => println(v)
        B => println(0)
    }
    let z = v
    println(z)
}`)
	if got := letInitType(t, out, prog, "z"); got.Kind != KString {
		t.Fatalf("match 臂绑定泄漏：let z = v 的类型 = %s, 期望 string", got.String())
	}
}

// TestCorpus零诊断 全部现有合法程序（examples + m4 + selftest）必须零诊断——
// 这是「现有合法程序零误杀」的机械判据。
func TestCorpus零诊断(t *testing.T) {
	// 三路合法语料（examples mains + m4）。selftest 不入此门：
	// 它是 lang-go test 专用语料，含 interp-only 构造（嵌套声明、Ok 作值比较），
	// v0.3 起这些构造在 check 时报 E2010/E3006（见 ADR-0001）。
	dirs := []string{
		"../../../../examples/hello", "../../../../examples/closures", "../../../../examples/compound",
		"../../../../examples/goroutine", "../../../../examples/json", "../../../../examples/result",
		"../../../../examples/shapes", "../../../../examples/tour", "../../../../examples/channel",
		"../../../../m4",
	}
	for _, dir := range dirs {
		dir := dir
		t.Run(dir, func(t *testing.T) {
			entries, err := os.ReadDir(dir)
			if err != nil {
				t.Fatalf("读目录失败: %v", err)
			}

			var srcs []string

			for _, e := range entries {
				if !e.IsDir() && strings.HasSuffix(e.Name(), ".lang-go") {
					bs, err := os.ReadFile(filepath.Join(dir, e.Name()))
					if err != nil {
						t.Fatalf("读文件失败: %v", err)
					}

					srcs = append(srcs, string(bs))
				}
			}

			if len(srcs) == 0 {
				t.Skip("无 .lang-go 文件")
			}

			prog, err := syntax.Parse(strings.Join(srcs, "\n"))
			if err != nil {
				t.Fatalf("解析失败: %v", err)
			}

			res := semantic.Resolve(prog)
			if res.HasErrors() {
				t.Fatalf("resolver 诊断: %v", res.Diags)
			}

			out := Check(prog, res)
			if out.HasErrors() {
				t.Fatalf("checker 诊断 %d 条:\n%v", len(out.Diags), out.Diags)
			}
		})
	}
}
