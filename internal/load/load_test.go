package load

// 测试缝：Program 的装载行为——import 展开、菱形去重、环检测、containment。

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"ling/internal/syntax"
)

func write(t *testing.T, dir, name, content string) {
	t.Helper()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, name), []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestImport文件与目录(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.ling", `import "./lib"
fn main() {
    println(double(21), greet("ling"))
}`)
	write(t, filepath.Join(dir, "lib"), "util.ling", "fn double(x: int): int {\n    return x * 2\n}")
	write(t, filepath.Join(dir, "lib"), "extra.ling", "fn greet(s: string): string {\n    return \"hi, \" + s\n}")
	prog, tests, err := Program(dir, true)
	if err != nil {
		t.Fatalf("装载失败: %v", err)
	}
	if len(tests) != 0 {
		t.Fatalf("不应发现测试: %v", tests)
	}
	// import 展开后共 3 个 fn 声明（main/double/greet）
	fns := 0
	for _, s := range prog {
		if _, ok := s.(*syntax.FnDecl); ok {
			fns++
		}
	}
	if fns != 3 {
		t.Fatalf("应合并 3 个 fn，得到 %d", fns)
	}
}

func Test菱形导入去重(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.ling", `import "./a"
import "./b"
fn main() {
    println(shared())
}`)
	write(t, dir, "a.ling", `import "./c"
fn aFn(): int {
    return 1
}`)
	write(t, dir, "b.ling", `import "./c"
fn bFn(): int {
    return 2
}`)
	write(t, dir, "c.ling", "fn shared(): int {\n    return 42\n}")
	_, _, err := Program(dir, false)
	if err != nil {
		t.Fatalf("菱形导入应静默去重: %v", err)
	}
}

func Test循环导入报错(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.ling", "import \"./a\"")
	write(t, dir, "a.ling", "import \"./b\"")
	write(t, dir, "b.ling", "import \"./a\"")
	_, _, err := Program(dir, false)
	if err == nil || !strings.Contains(err.Error(), "循环") {
		t.Fatalf("循环导入应报错，得到: %v", err)
	}
}

func Test逃出根目录报错(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.ling", "import \"./../../outside\"")
	_, _, err := Program(dir, false)
	if err == nil || !strings.Contains(err.Error(), "逃出") {
		t.Fatalf("越界 import 应报错，得到: %v", err)
	}
}

func Test非相对路径报错(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.ling", "import \"std/http\"")
	_, _, err := Program(dir, false)
	if err == nil || !strings.Contains(err.Error(), "相对路径") {
		t.Fatalf("非 ./ 前缀应报错，得到: %v", err)
	}
}

func Test测试函数收集(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.ling", "fn main() {\n}")
	write(t, dir, "x_test.ling", "fn testOne() {\n}\nfn testTwo() {\n}")
	_, tests, err := Program(dir, true)
	if err != nil {
		t.Fatal(err)
	}
	if len(tests) != 2 || tests[0] != "testOne" {
		t.Fatalf("应收集 testOne/testTwo，得到 %v", tests)
	}
}
