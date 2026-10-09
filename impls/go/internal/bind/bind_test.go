package bind

// 测试缝：Dir 的两层契约——
// 1) 签名映射：Go 导出函数 → langGo extern 声明（类型映射 + 跳过规则）；
// 2) 产物合法性：草案可被 syntax.Parse 解析、可被 →Go 转译器接受。

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"lang-go/internal/gen"
	"lang-go/internal/pipeline"
	"lang-go/internal/syntax"
)

func writePkg(t *testing.T, dir string, files map[string]string) {
	t.Helper()

	for name, src := range files {
		p := filepath.Join(dir, name)
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}

		if err := os.WriteFile(p, []byte(src), 0o644); err != nil {
			t.Fatal(err)
		}
	}
}

func Test签名映射与跳过(t *testing.T) {
	dir := t.TempDir()
	writePkg(t, dir, map[string]string{
		"mathx.go": `package mathx

import "strings"

func Abs(x float64) float64 { return 0 }
func Add(a, b int) int { return 0 }
func Greet(name string) (string, error) { return "", nil }
func Find(dir string) ([]string, error) { return nil, nil }
func Parse(b []byte) (int, error) { return 0, nil }
func IsGood(ok bool) bool { return false }
func Hidden() {}               // 非导出
func withPtr(p *int) {}        // 非导出
func PtrArg(p *int) {}         // 指针参数：跳过
func StructArg(t struct{ A int }) {} // 匿名 struct：跳过
func Multi(a int) (int, int) { return 0, 0 } // 双非 error 返回：跳过
func Method() {}               // 导出但下方有接收者版本：仍按普通函数收集
`,
	})

	out, err := Dir(dir)
	if err != nil {
		t.Fatal(err)
	}

	for _, want := range []string{
		"fn Abs(x: float): float",
		"fn Add(a: int, b: int): int",
		"fn Greet(name: string): Result[string, error]",
		"fn Find(dir: string): Result[[string], error]",
		"fn Parse(b: [int]): Result[int, error]",
		"fn IsGood(ok: bool): bool",
	} {
		if !strings.Contains(out, want) {
			t.Fatalf("缺少 %q\n=== 草案 ===\n%s", want, out)
		}
	}

	for _, gone := range []string{"fn PtrArg", "fn Multi"} {
		if strings.Contains(out, gone) {
			t.Fatalf("%s 应被跳过（不含声明行）\n=== 草案 ===\n%s", gone, out)
		}
	}

	if !strings.Contains(out, "跳过") {
		t.Fatalf("应列出跳过原因")
	}
}

func Test产物可解析可转译(t *testing.T) {
	dir := t.TempDir()
	writePkg(t, dir, map[string]string{
		"lib.go": `package lib

func Twice(n int) (int, error) { return 0, nil }
func Echo(s string) string { return "" }
`,
	})

	out, err := Dir(dir)
	if err != nil {
		t.Fatal(err)
	}

	prog, err := syntax.Parse(out)
	if err != nil {
		t.Fatalf("草案不可解析: %v\n%s", err, out)
	}

	irProg, err := pipeline.CompileStmts(prog)
	if err != nil {
		t.Fatalf("草案未通过前端管线: %v\n%s", err, out)
	}

	if _, err := gen.Transpile(irProg); err != nil {
		t.Fatalf("草案不可转译: %v\n%s", err, out)
	}
}

func Test跳过测试文件与非Go(t *testing.T) {
	dir := t.TempDir()
	writePkg(t, dir, map[string]string{
		"a.go":       "package lib\nfunc Show() {}\n",
		"a_test.go":  "package lib\nfunc Hide() {}\n",
		"readme.txt": "not go",
	})

	out, err := Dir(dir)
	if err != nil {
		t.Fatal(err)
	}

	if strings.Contains(out, "Hide") {
		t.Fatal("测试文件不应参与")
	}

	if !strings.Contains(out, "Show") {
		t.Fatalf("实现文件应参与\n%s", out)
	}
}

func Test空包报错(t *testing.T) {
	dir := t.TempDir()
	writePkg(t, dir, map[string]string{
		"only.go": "package lib\nfunc hidden() {}\n",
	})

	_, err := Dir(dir)
	if err == nil || !strings.Contains(err.Error(), "导出函数") {
		t.Fatalf("无导出函数应报错，得到: %v", err)
	}
}
