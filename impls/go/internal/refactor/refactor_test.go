package refactor

// refactor 测试缝：rename 语义正确性（SymbolId 区分 shadowing/同名）、冲突拒绝、
// organize imports、safe remove 可行性。

import (
	"lang-go/internal/edit"
	"lang-go/internal/hir"
	"lang-go/internal/pipeline"
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"sort"
	"strings"
	"testing"
)

type renameCtx struct {
	prog []syntax.Stmt
	res  *semantic.Result
	h    *hir.Prog
	src  string
}

func analyzeSrc(t *testing.T, src string) *renameCtx {
	t.Helper()

	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}

	a, err := pipeline.AnalyzeStmts(prog)
	if err != nil {
		t.Fatalf("管线失败: %v", err)
	}

	return &renameCtx{prog: prog, res: a.Res, h: a.HIR, src: src}
}

func (c *renameCtx) symByName(name string) *semantic.Symbol {
	for _, s := range c.res.Symbols {
		if !s.BuiltinMark && s.Name == name {
			return s
		}
	}

	return nil
}

func (c *renameCtx) rename(sym *semantic.Symbol, newName string) ([]edit.TextEdit, error) {
	return Rename(c.prog, Sources{Default: c.src}, c.res, c.h, sym, newName)
}

func TestRenameFn跨引用(t *testing.T) {
	c := analyzeSrc(t, `
fn area(): int { return 1 }
fn main() {
    println(area())
    println(area())
}`)
	target := c.symByName("area")

	edits, err := c.rename(target, "surface")
	if err != nil {
		t.Fatal(err)
	}

	if len(edits) != 3 {
		t.Fatalf("应有 3 处编辑（定义+两处调用）: %d", len(edits))
	}
}

func TestRenameShadowing(t *testing.T) {
	c := analyzeSrc(t, `
let tag = "全局"
fn main() {
    let tag = "局部"
    println(tag)
}`)

	var local *semantic.Symbol

	for _, s := range c.res.Symbols {
		if !s.BuiltinMark && s.Name == "tag" && s.Kind == semantic.KVar {
			local = s
		}
	}

	if local == nil {
		t.Fatal("未找到局部 tag")
	}

	edits, err := c.rename(local, "localTag")
	if err != nil {
		t.Fatal(err)
	}

	if len(edits) != 2 {
		t.Fatalf("局部 tag rename 应 2 处编辑（声明+引用）: %d", len(edits))
	}
}

func TestRename冲突拒绝(t *testing.T) {
	c := analyzeSrc(t, `
let a = 1
let b = 2
fn main() { println(a, b) }`)

	target := c.symByName("a")
	if _, err := c.rename(target, "b"); err == nil {
		t.Fatal("与已有符号同名应拒绝")
	}
}

func TestRename非法名拒绝(t *testing.T) {
	c := analyzeSrc(t, "fn main() {\nprintln(1)\n}")

	target := c.symByName("main")
	for _, bad := range []string{"", "let", "1abc", "a-b"} {
		if _, err := c.rename(target, bad); err == nil {
			t.Fatalf("非法名 %q 应拒绝", bad)
		}
	}
}

func TestRenameParam(t *testing.T) {
	c := analyzeSrc(t, `
fn double(n: int): int {
    return n * 2
}
fn main() {
    println(double(4))
}`)
	target := c.symByName("double")
	_ = target
	// 找参数符号 n（double 的参数）
	var nSym *semantic.Symbol

	for _, s := range c.res.Symbols {
		if s.Kind == semantic.KParam && s.Name == "n" {
			nSym = s
		}
	}

	if nSym == nil {
		t.Fatal("未找到参数 n")
	}

	edits, err := c.rename(nSym, "num")
	if err != nil {
		t.Fatal(err)
	}

	if len(edits) < 2 {
		t.Fatalf("参数 rename 应至少 2 处编辑（声明+引用）: %d", len(edits))
	}
}

func TestSafeRemove有引用拒绝(t *testing.T) {
	c := analyzeSrc(t, `
fn helper(): int { return 1 }
fn main() {
    println(helper())
}`)
	target := c.symByName("helper")

	chk, err := SafeRemoveCheck(c.prog, c.res, target)
	if err != nil {
		t.Fatal(err)
	}

	if chk.Removable {
		t.Fatalf("有引用的 fn 不可删除: %+v", chk)
	}

	if chk.RefCount == 0 {
		t.Fatal("应报告引用计数")
	}
}

func TestSafeRemove零引用可行(t *testing.T) {
	c := analyzeSrc(t, `
fn unused(): int { return 1 }
fn main() {
    println(1)
}`)
	target := c.symByName("unused")

	chk, err := SafeRemoveCheck(c.prog, c.res, target)
	if err != nil {
		t.Fatal(err)
	}

	if !chk.Removable {
		t.Fatalf("零引用的 fn 应可删除: %+v", chk)
	}
}

func TestOrganizeImports排序去重(t *testing.T) {
	// 路径最后段（b、a）在非 import 行中出现 → 视为使用，保留并排序
	src := "import \"./b\"\nimport \"./a\"\nimport \"./b\"\nfn main() {\n    println(b)\n    println(a)\n}\n"

	out, changed, err := OrganizeImports(src)
	if err != nil {
		t.Fatal(err)
	}

	if !changed {
		t.Fatal("应有变化")
	}

	if strings.Count(out, "import") != 2 {
		t.Fatalf("重复 import 应去重: %q", out)
	}

	aIdx := strings.Index(out, "\"./a\"")

	bIdx := strings.Index(out, "\"./b\"")
	if aIdx > bIdx {
		t.Fatalf("import 应按路径排序")
	}
}

func TestOrganizeImports未使用删除(t *testing.T) {
	src := "import \"./unused\"\nfn main() {\n    println(1)\n}\n"

	out, changed, err := OrganizeImports(src)
	if err != nil {
		t.Fatal(err)
	}

	if !changed {
		t.Fatal("应有变化")
	}

	if strings.Contains(out, "import") {
		t.Fatalf("未使用 import 应删除: %q", out)
	}
}

// applyToSrc 按编辑集改造源码（rune 偏移，降序拼接），验证偏移正确性的关键缝.
func applyToSrc(src string, edits []edit.TextEdit) string {
	rs := []rune(src)

	es := append([]edit.TextEdit(nil), edits...)
	sort.Slice(es, func(i, j int) bool { return es[i].StartOffset > es[j].StartOffset })

	for _, e := range es {
		rs = append(rs[:e.StartOffset], append([]rune(e.Replacement), rs[e.EndOffset:]...)...)
	}

	return string(rs)
}

func TestRename应用往返(t *testing.T) {
	c := analyzeSrc(t, `
fn area(): int { return 1 }
fn main() {
    println(area())
    println(area())
}`)
	target := c.symByName("area")

	edits, err := c.rename(target, "surface")
	if err != nil {
		t.Fatal(err)
	}

	next := applyToSrc(c.src, edits)
	// 新源码可解析、可类型检查，且旧名消失
	prog, err := syntax.Parse(next)
	if err != nil {
		t.Fatalf("rename 后解析失败: %v\n%s", err, next)
	}

	if _, err := pipeline.AnalyzeStmts(prog); err != nil {
		t.Fatalf("rename 后分析失败: %v\n%s", err, next)
	}

	if strings.Contains(next, "area") {
		t.Fatalf("旧名应全部消失:\n%s", next)
	}

	if strings.Count(next, "surface") != 3 {
		t.Fatalf("新名应出现 3 次:\n%s", next)
	}
}

func TestRenameMethod(t *testing.T) {
	c := analyzeSrc(t, `
struct P {
    x: int
}
fn (p P).get(): int {
    return p.x
}
fn main() {
    println(P{x: 1}.get())
}`)

	target := c.symByName("get")
	if target == nil {
		t.Fatal("未找到方法 get")
	}

	edits, err := c.rename(target, "label")
	if err != nil {
		t.Fatal(err)
	}

	if len(edits) != 2 {
		t.Fatalf("方法 rename 应 2 处编辑（定义+调用点）: %d", len(edits))
	}

	next := applyToSrc(c.src, edits)

	prog, err := syntax.Parse(next)
	if err != nil {
		t.Fatalf("rename 后解析失败: %v\n%s", err, next)
	}

	if _, err := pipeline.AnalyzeStmts(prog); err != nil {
		t.Fatalf("rename 后分析失败: %v\n%s", err, next)
	}

	if !strings.Contains(next, ".label()") || !strings.Contains(next, ".label():") {
		t.Fatalf("定义与调用点都应改名:\n%s", next)
	}
}

func TestSafeRemove应用往返(t *testing.T) {
	c := analyzeSrc(t, `
fn unused(): int { return 1 }
fn main() {
    println(1)
}`)
	target := c.symByName("unused")

	chk, err := SafeRemoveCheck(c.prog, c.res, target)
	if err != nil {
		t.Fatal(err)
	}

	if !chk.Removable {
		t.Fatalf("应可删除: %+v", chk)
	}

	edits, err := SafeRemoveEdits(c.prog, Sources{Default: c.src}, c.res, target)
	if err != nil {
		t.Fatal(err)
	}

	next := applyToSrc(c.src, edits)

	prog, err := syntax.Parse(next)
	if err != nil {
		t.Fatalf("删除后解析失败: %v\n%s", err, next)
	}

	if _, err := pipeline.AnalyzeStmts(prog); err != nil {
		t.Fatalf("删除后分析失败: %v\n%s", err, next)
	}

	if strings.Contains(next, "unused") {
		t.Fatalf("unused 应被删除:\n%s", next)
	}
}

func TestRename往返语义保持(t *testing.T) {
	// Agent Property（总纲 §15.5）：rename → rename 回来，源码语义与解释器输出保持
	src := `fn area(): int { return 1 }
fn main() {
    println(area())
    println(area())
}`
	c := analyzeSrc(t, src)
	target := c.symByName("area")

	edits, err := c.rename(target, "surface")
	if err != nil {
		t.Fatal(err)
	}

	renamed := applyToSrc(src, edits)
	c2 := analyzeSrc(t, renamed)

	t2 := c2.symByName("surface")
	if t2 == nil {
		t.Fatal("rename 后新名符号应存在")
	}

	back, err := c2.rename(t2, "area")
	if err != nil {
		t.Fatal(err)
	}

	restored := applyToSrc(renamed, back)
	if restored != src {
		t.Fatalf("往返后源码应还原:\n%s", restored)
	}
}
