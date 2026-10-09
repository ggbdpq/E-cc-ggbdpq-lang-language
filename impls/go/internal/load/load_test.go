package load

// 测试缝：Program 的装载行为——import 展开、菱形去重、环检测、containment。

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"lang-go/internal/syntax"
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
	write(t, dir, "main.lang-go", `import "./lib"
fn main() {
    println(double(21), greet("langGo"))
}`)
	write(t, filepath.Join(dir, "lib"), "util.lang-go", "fn double(x: int): int {\n    return x * 2\n}")
	write(t, filepath.Join(dir, "lib"), "extra.lang-go", "fn greet(s: string): string {\n    return \"hi, \" + s\n}")

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
	write(t, dir, "main.lang-go", `import "./a"
import "./b"
fn main() {
    println(shared())
}`)
	write(t, dir, "a.lang-go", `import "./c"
fn aFn(): int {
    return 1
}`)
	write(t, dir, "b.lang-go", `import "./c"
fn bFn(): int {
    return 2
}`)
	write(t, dir, "c.lang-go", "fn shared(): int {\n    return 42\n}")

	_, _, err := Program(dir, false)
	if err != nil {
		t.Fatalf("菱形导入应静默去重: %v", err)
	}
}

func Test循环导入报错(t *testing.T) {
	// 循环环在装载链内（子目录文件互引）——根目录文件自动装载，
	// 根文件间冗余互引属无害去重（v0.5 done 键归一化后静默跳过）
	dir := t.TempDir()
	if err := os.MkdirAll(filepath.Join(dir, "sub"), 0o755); err != nil {
		t.Fatal(err)
	}

	write(t, dir, "main.lang-go", "import \"./sub/x\"")
	write(t, filepath.Join(dir, "sub"), "x.lang-go", "import \"./y\"")
	write(t, filepath.Join(dir, "sub"), "y.lang-go", "import \"./x\"")

	_, _, err := Program(dir, false)
	if err == nil || !strings.Contains(err.Error(), "循环") {
		t.Fatalf("装载链内循环导入应报错，得到: %v", err)
	}
}

// Test根文件冗余互引去重 根目录文件互相 import：自动装载各一次，冗余声明静默去重.
func Test根文件冗余互引去重(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.lang-go", "import \"./a\"\nfn main() {\n    println(1)\n}")
	write(t, dir, "a.lang-go", "import \"./main\"\nfn fa(): int {\n    return 1\n}")

	prog, _, err := Program(dir, false)
	if err != nil {
		t.Fatalf("根文件冗余互引应去重而非报错: %v", err)
	}

	if len(prog) == 0 {
		t.Fatal("程序为空")
	}
}

func Test逃出根目录报错(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.lang-go", "import \"./../../outside\"")

	_, _, err := Program(dir, false)
	if err == nil || !strings.Contains(err.Error(), "逃出") {
		t.Fatalf("越界 import 应报错，得到: %v", err)
	}
}

func Test非相对路径报错(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.lang-go", "import \"std/http\"")

	_, _, err := Program(dir, false)
	if err == nil || !strings.Contains(err.Error(), "相对路径") {
		t.Fatalf("非 ./ 前缀应报错，得到: %v", err)
	}
}

func Test测试函数收集(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "main.lang-go", "fn main() {\n}")
	write(t, dir, "x_test.lang-go", "fn testOne() {\n}\nfn testTwo() {\n}")

	_, tests, err := Program(dir, true)
	if err != nil {
		t.Fatal(err)
	}

	if len(tests) != 2 || tests[0] != "testOne" {
		t.Fatalf("应收集 testOne/testTwo，得到 %v", tests)
	}
}
