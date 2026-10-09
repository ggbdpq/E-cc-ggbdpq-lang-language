package syntax

// 测试缝：Parse 的错误行为。REPL 的续行依赖 Error.Incomplete，
// 控制头禁裸 struct 字面量规则保护 if/while/match 头部解析。

import (
	"errors"
	"strings"
	"testing"
)

func parseErr(t *testing.T, src string) *Error {
	t.Helper()

	_, err := Parse(src)
	if err == nil {
		t.Fatalf("期望解析错误但成功了\n源码:\n%s", src)
	}

	var pe *Error
	if !errors.As(err, &pe) {
		t.Fatalf("错误应为 *Error，得到: %v", err)
	}

	return pe
}

func Test不完整输入标记(t *testing.T) {
	// REPL 依赖：块、调用、map 字面量未闭合 → Incomplete
	for _, src := range []string{
		"fn f() {",
		"let x = (1 +",
		"let m = {",
		"if x {",
		"println(1",
	} {
		pe := parseErr(t, src)
		if !pe.Incomplete {
			t.Fatalf("应标记为不完整\n源码: %q\n错误: %v", src, pe)
		}
	}
}

func Test完整但错误的输入不标记Incomplete(t *testing.T) {
	pe := parseErr(t, "let = 3")
	if pe.Incomplete {
		t.Fatalf("完整输入不应标记不完整: %v", pe)
	}
}

func Test保留字报错(t *testing.T) {
	for _, kw := range []string{"go", "chan", "import", "extern", "interface"} {
		_, err := Parse(kw + " x = 1")
		if err == nil {
			t.Fatalf("保留字 %s 应报错", kw)
		}
	}
}

func Test控制头禁止裸结构体字面量(t *testing.T) {
	// 若没有 noStruct 规则，`if p {` 会把块误吃成空 struct 字面量
	if _, err := Parse("if p { println(1) }"); err != nil {
		t.Fatalf("条件位置的裸绑定不应误判为 struct 字面量: %v", err)
	}

	if _, err := Parse("while p { break }"); err != nil {
		t.Fatalf("while 条件同理: %v", err)
	}
	// 括号内恢复 struct 字面量
	if _, err := Parse("if (Point{x: 1}).x > 0 { println(1) }"); err != nil {
		t.Fatalf("括号包裹的 struct 字面量应可用: %v", err)
	}
}

func TestMatch可作实参且分支换行存活(t *testing.T) {
	src := `let a = 1
println(match a {
    1 => "一"
    other => "其他"
})`
	if _, err := Parse(src); err != nil {
		t.Fatalf("match 作实参应可解析: %v", err)
	}
}

func Test多行调用实参尾随逗号(t *testing.T) {
	src := `println(
    1,
    2,
)`
	if _, err := Parse(src); err != nil {
		t.Fatalf("多行实参 + 尾随逗号应可解析: %v", err)
	}
}

func Test参数注解可省略(t *testing.T) {
	if _, err := Parse("fn f(a, b: int) { return a }"); err != nil {
		t.Fatalf("无注解参数应可解析: %v", err)
	}
}

func Test函数类型注解(t *testing.T) {
	for _, src := range []string{
		"let f = fn(a: fn(int): int) { }",
		"fn g(f: fn(string, int): bool): fn(): int { }",
		"let h = fn(): int { return 1 }",
	} {
		if _, err := Parse(src); err != nil {
			t.Fatalf("函数类型应可解析: %v\n源码: %s", err, src)
		}
	}
}

func Test错误带源码摘录(t *testing.T) {
	// Q22：错误信息附出错行内容，不用翻文件定位。
	// EOF 场景行号是输入末尾之后（正确行为），摘录回退到最后非空行。
	src := "let a = 1\nlet b = 2\nprintln(a +\n"

	_, err := Parse(src)
	if err == nil {
		t.Fatal("应报错")
	}

	msg := err.Error()
	if !strings.Contains(msg, "println(a +") {
		t.Fatalf("错误应含源码行摘录: %v", msg)
	}
	// 词法错误同样带摘录
	_, err = Parse("let s = \"未闭合")
	if err == nil {
		t.Fatal("词法错误应报错")
	}

	if !strings.Contains(err.Error(), "let s =") {
		t.Fatalf("词法错误应含源码行摘录: %v", err)
	}
	// 非结尾错误行号精确（行号修复的回归锁）
	_, err = Parse("let a = 1\nlet b = 2\nlet = 3\n")
	if err == nil || !strings.Contains(err.Error(), "第3行") {
		t.Fatalf("中间行错误应报第 3 行: %v", err)
	}
}
