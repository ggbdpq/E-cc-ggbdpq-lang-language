package graph

// 语义图测试缝：节点/边构建、callers/callees（含递归/互递归）、read/write、
// 方法调用、跨模块 depends_on、影子不串边。

import (
	"os"
	"testing"

	"lang-go/internal/load"
	"lang-go/internal/pipeline"
	"lang-go/internal/syntax"
)

func buildSrc(t *testing.T, src string) (*Graph, *pipeline.Analyzed) {
	t.Helper()

	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}

	a, err := pipeline.AnalyzeStmts(prog)
	if err != nil {
		t.Fatalf("管线失败: %v", err)
	}

	return Build(a.AST, a.Res, a.Ty, a.HIR, a.Prog, nil), a
}

func buildDir(t *testing.T, dir string) (*Graph, *pipeline.Analyzed) {
	t.Helper()

	prog, _, files, err := load.ProgramEx(dir, false)
	if err != nil {
		t.Fatalf("装载失败: %v", err)
	}

	a, err := pipeline.AnalyzeStmts(prog)
	if err != nil {
		t.Fatalf("管线失败: %v", err)
	}

	return Build(a.AST, a.Res, a.Ty, a.HIR, a.Prog, files), a
}

func symIDByName(t *testing.T, a *pipeline.Analyzed, name string) int {
	t.Helper()

	for _, s := range a.Res.Symbols {
		if !s.BuiltinMark && s.Name == name {
			return int(s.ID)
		}
	}

	t.Fatalf("符号未找到: %s", name)

	return -1
}

func TestCallers与Callees(t *testing.T) {
	src := `
fn leaf(): int { return 1 }
fn mid(): int { return leaf() }
fn main() {
    println(mid())
}`
	g, a := buildSrc(t, src)
	mainID := symIDByName(t, a, "main")
	midID := symIDByName(t, a, "mid")
	leafID := symIDByName(t, a, "leaf")

	callers := g.Callers(midID, -1)
	if len(callers) != 1 || callers[0].From != mainID {
		t.Fatalf("mid 的调用方应为 main: %+v", callers)
	}

	trans := g.Callers(leafID, -1)
	if len(trans) != 2 {
		t.Fatalf("leaf 的传递调用方应含 main+mid: %+v", trans)
	}

	anyMain, anyMainTrans, anyMidDirect := false, false, false

	for _, it := range trans {
		if it.From == mainID {
			anyMain = true
			anyMainTrans = it.Transitive
		}

		if it.From == midID {
			anyMidDirect = !it.Transitive
		}
	}

	if !anyMain || !anyMainTrans || !anyMidDirect {
		t.Fatalf("传递标记错误: %+v", trans)
	}

	callees := g.Callees(mainID, 1)
	if len(callees) != 1 || callees[0].From != midID {
		t.Fatalf("main 应直接调用 mid: %+v", callees)
	}
}

func Test互递归(t *testing.T) {
	src := `
fn isEven(n: int): bool {
    if n == 0 { return true }
    return isOdd(n - 1)
}
fn isOdd(n: int): bool {
    if n == 0 { return false }
    return isEven(n - 1)
}
fn main() { println(isEven(4)) }`
	g, a := buildSrc(t, src)
	odd := symIDByName(t, a, "isOdd")
	even := symIDByName(t, a, "isEven")
	mainID := symIDByName(t, a, "main")
	// 互递归：isEven 的调用方 = isOdd（直接）+ main（传递）
	callersEven := g.Callers(even, -1)
	foundOdd, foundMain := false, false

	for _, it := range callersEven {
		if it.From == odd {
			foundOdd = true
		}

		if it.From == mainID {
			foundMain = true
		}
	}

	if !foundOdd || !foundMain {
		t.Fatalf("isEven 的调用方应含 isOdd 与 main: %+v", callersEven)
	}

	calleesEven := g.Callees(even, 1)
	foundOddCallee := false

	for _, it := range calleesEven {
		if it.From == odd {
			foundOddCallee = true
		}
	}

	if !foundOddCallee {
		t.Fatalf("isEven 应调用 isOdd: %+v", calleesEven)
	}
}

func Test读写边(t *testing.T) {
	src := `
fn main() {
    var n = 0
    n = n + 1
    println(n)
}`
	g, a := buildSrc(t, src)
	nID := symIDByName(t, a, "n")

	var writes, reads int

	for _, e := range g.In(nID) {
		switch e.Kind {
		case EdgeWrite:
			writes++
		case EdgeReference:
			reads++
		}
	}

	if writes == 0 || reads == 0 {
		t.Fatalf("n 应同时有读写边: w=%d r=%d", writes, reads)
	}
}

func Test方法调用边(t *testing.T) {
	src := `
struct P {
    x: int
}
fn (p P).get(): int {
    return p.x
}
fn main() {
    let p = P{x: 1}
    println(p.get())
}`
	g, a := buildSrc(t, src)
	getID := symIDByName(t, a, "get")
	mainID := symIDByName(t, a, "main")

	var found bool

	for _, e := range g.In(getID) {
		if e.Kind == EdgeCall && e.From == mainID {
			found = true
		}
	}

	if !found {
		t.Fatalf("main → get 方法调用边缺失")
	}
}

func Test跨模块依赖(t *testing.T) {
	dir := t.TempDir()
	os.MkdirAll(dir, 0o755)
	os.WriteFile(dir+"/main.lang-go",
		[]byte("import \"./lib\"\nfn main() {\n    println(libHelp())\n}"), 0o644)
	os.WriteFile(dir+"/lib.lang-go",
		[]byte("fn libHelp(): int {\n    return 1\n}"), 0o644)
	g, a := buildDir(t, dir)
	symIDByName(t, a, "main")

	deps := g.FileDependsOn["main.lang-go"]
	if len(deps) == 0 {
		t.Fatalf("main 应有文件级依赖: %+v", g.FileDependsOn)
	}
}

const mainFile = "main.lang-go"

func Test影子不串边(t *testing.T) {
	src := `
let tag = "全局"
fn main() {
    let tag = "局部"
    println(tag)
}`
	g, a := buildSrc(t, src)

	tagID := symIDByName(t, a, "tag")
	for _, e := range g.In(tagID) {
		if e.Kind == EdgeReference {
			t.Fatalf("局部影子引用不应指向全局符号: %+v", e)
		}
	}
}
