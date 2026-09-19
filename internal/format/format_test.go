package format

// 测试缝：Format 的三个行为契约——
// 1) 幂等：format(format(x)) == format(x)（全仓 .ling 语料）；
// 2) 语义等价：格式化前后解释器输出逐字节一致（m4 与 examples 实程序）；
// 3) 注释保留：整行注释在输出中存活。

import (
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"ling/internal/interp"
	"ling/internal/load"
	"ling/internal/syntax"
)

func corpusDirs(t *testing.T) []string {
	t.Helper()
	return []string{
		filepath.Join("..", "..", "m4"),
		filepath.Join("..", "..", "examples"),
	}
}

func lingFiles(t *testing.T, root string) []string {
	t.Helper()
	var out []string
	err := filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if !d.IsDir() && strings.HasSuffix(path, ".ling") {
			out = append(out, path)
		}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	return out
}

func Test格式化幂等(t *testing.T) {
	n := 0
	for _, dir := range corpusDirs(t) {
		for _, f := range lingFiles(t, dir) {
			src, err := os.ReadFile(f)
			if err != nil {
				t.Fatal(err)
			}
			once, err := Format(string(src))
			if err != nil {
				t.Fatalf("%s: %v", f, err)
			}
			twice, err := Format(once)
			if err != nil {
				t.Fatalf("%s 二次格式化失败: %v\n=== 一次产物 ===\n%s", f, err, once)
			}
			if once != twice {
				t.Fatalf("%s 格式化不幂等\n=== 一次 ===\n%s\n=== 二次 ===\n%s", f, once, twice)
			}
			n++
		}
	}
	if n < 10 {
		t.Fatalf("语料不足：%d 个文件", n)
	}
}

// copyLingDir 把 root 下全部 .ling 按相对路径拷到 dst（保留子目录结构）
func copyLingDir(t *testing.T, root, dst string) {
	t.Helper()
	for _, f := range lingFiles(t, root) {
		rel, err := filepath.Rel(root, f)
		if err != nil {
			t.Fatal(err)
		}
		target := filepath.Join(dst, rel)
		if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
			t.Fatal(err)
		}
		src, err := os.ReadFile(f)
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(target, src, 0o644); err != nil {
			t.Fatal(err)
		}
	}
}

func runProgram(t *testing.T, dir string) string {
	t.Helper()
	prog, _, err := load.Program(dir, false)
	if err != nil {
		t.Fatalf("装载 %s: %v", dir, err)
	}
	var out strings.Builder
	if err := interp.New(&out).Run(prog); err != nil {
		t.Fatalf("运行 %s: %v", dir, err)
	}
	return out.String()
}

func Test语义等价_格式化前后(t *testing.T) {
	dirs := []string{filepath.Join("..", "..", "m4")}
	for _, name := range []string{"hello", "shapes", "result", "closures", "json"} {
		dirs = append(dirs, filepath.Join("..", "..", "examples", name))
	}
	for _, dir := range dirs {
		before := runProgram(t, dir)
		tmp := t.TempDir()
		copyLingDir(t, dir, tmp)
		for _, f := range lingFiles(t, tmp) {
			src, err := os.ReadFile(f)
			if err != nil {
				t.Fatal(err)
			}
			formatted, err := Format(string(src))
			if err != nil {
				t.Fatalf("%s: %v", f, err)
			}
			if err := os.WriteFile(f, []byte(formatted), 0o644); err != nil {
				t.Fatal(err)
			}
		}
		after := runProgram(t, tmp)
		if before != after {
			t.Fatalf("%s 格式化后语义漂移\n=== 前 ===\n%q\n=== 后 ===\n%q", dir, before, after)
		}
	}
}

func Test整行注释保留(t *testing.T) {
	src := `// 文件头注释
fn add(a: int, b: int): int {
    // 求和
    return a + b
}

// 主入口
fn main() {
    println(add(1, 2))
}
`
	out, err := Format(src)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"// 文件头注释", "// 求和", "// 主入口"} {
		if !strings.Contains(out, want) {
			t.Fatalf("注释丢失 %q\n=== 输出 ===\n%s", want, out)
		}
	}
	// 格式化产物必须能重新解析
	if _, err := syntax.Parse(out); err != nil {
		t.Fatalf("格式化产物不可解析: %v\n%s", err, out)
	}
}

func TestImport与Extern保留(t *testing.T) {
	src := `import "./lib"
extern "go" {
    fn hostFn(x: int): int
}
fn main() {
    println(1)
}
`
	out, err := Format(src)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{`import "./lib"`, `extern "go" {`, "fn hostFn(x: int): int"} {
		if !strings.Contains(out, want) {
			t.Fatalf("丢失 %q\n=== 输出 ===\n%s", want, out)
		}
	}
	once, err := Format(out)
	if err != nil {
		t.Fatalf("二次格式化失败: %v\n%s", err, out)
	}
	if once != out {
		t.Fatalf("extern/import 场景不幂等\n=== 一次 ===\n%s\n=== 二次 ===\n%s", out, once)
	}
}

func Test缩进规范化(t *testing.T) {
	out, err := Format("fn main() {\nprintln(1)\nif true {\nprintln(2)\n}\n}\n")
	if err != nil {
		t.Fatal(err)
	}
	want := "fn main() {\n    println(1)\n    if true {\n        println(2)\n    }\n}\n"
	if out != want {
		t.Fatalf("缩进不符\n=== 期望 ===\n%q\n=== 实际 ===\n%q", want, out)
	}
}

// —— chan 类型注解输出（decisions #59/#63）：chan[T] 经 NamedType 路径原样输出 ——

func TestChan注解输出(t *testing.T) {
	out, err := Format("fn prod(ch: chan[int], n: int) {\nsend(ch, n)\n}\n")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out, "fn prod(ch: chan[int], n: int)") {
		t.Fatalf("chan 注解输出不符\n=== 输出 ===\n%s", out)
	}
	once, err := Format(out)
	if err != nil {
		t.Fatal(err)
	}
	if once != out {
		t.Fatalf("chan 注解场景不幂等\n=== 一次 ===\n%s\n=== 二次 ===\n%s", out, once)
	}
}
