package gen

// 测试缝两层：
// 1) Transpile 的约束报错（编译期检查：穷尽性、? 位置、注解要求）
// 2) 端到端等价：同一 ling 程序，解释器输出与 →Go 编译执行输出逐字节一致
//    ——这是 →Go 后端"跑通"的机械判据，也是 M4 的验收方式
//
// 命令执行安全：go build / 产物运行的命令参数全部为字面量，
// 被测代码经 cmd.Dir 隔离在 t.TempDir() 临时目录中，无任何拼接输入。

import (
	"bytes"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"ling/internal/interp"
	"ling/internal/load"
	"ling/internal/syntax"
)

func transpile(t *testing.T, src string) string {
	t.Helper()
	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}
	return transpileSrc(t, prog)
}

func transpileSrc(t *testing.T, prog []syntax.Stmt) string {
	t.Helper()
	out, err := Transpile(prog)
	if err != nil {
		t.Fatalf("转译失败: %v", err)
	}
	return out
}

func interpRun(t *testing.T, src string) string {
	t.Helper()
	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}
	var out bytes.Buffer
	if err := interp.New(&out).Run(prog); err != nil {
		t.Fatalf("解释器运行失败: %v", err)
	}
	return out.String()
}

func goRun(t *testing.T, goSrc string) string {
	t.Helper()
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "main.go"), []byte(goSrc), 0o644); err != nil {
		t.Fatalf("写文件失败: %v", err)
	}
	build := exec.Command("go", "build", "-o", "out.exe", "main.go")
	build.Dir = dir
	if bt, err := build.CombinedOutput(); err != nil {
		t.Fatalf("生成的 Go 代码编译失败: %v\n%s\n=== 生成的源码 ===\n%s", err, bt, goSrc)
	}
	run := exec.Command("./out.exe")
	run.Dir = dir
	var stdout bytes.Buffer
	run.Stdout = &stdout
	run.Stderr = os.Stderr
	if err := run.Run(); err != nil {
		t.Fatalf("生成的程序运行失败: %v\n=== 生成的源码 ===\n%s", err, goSrc)
	}
	return stdout.String()
}

func Test转译约束_参数必须注解(t *testing.T) {
	prog, _ := syntax.Parse("fn f(a) { }")
	_, err := Transpile(prog)
	if err == nil || !strings.Contains(err.Error(), "注解") {
		t.Fatalf("无注解参数应报错，得到: %v", err)
	}
}

func Test转译约束_问号只能在语句顶层(t *testing.T) {
	prog, _ := syntax.Parse(`fn f(): Result[int, error] {
    return Ok(1)
}
fn g(): Result[int, error] {
    return Ok(f()? + 1)
}
fn main() { }`)
	_, err := Transpile(prog)
	if err == nil || !strings.Contains(err.Error(), "顶层") {
		t.Fatalf("? 嵌在表达式里应报错，得到: %v", err)
	}
}

func Test转译约束_带问号的函数须Result注解(t *testing.T) {
	prog, _ := syntax.Parse(`fn f(): Result[int, error] {
    return Ok(1)
}
fn g() {
    let n = f()?
    println(n)
}
fn main() { }`)
	_, err := Transpile(prog)
	if err == nil || !strings.Contains(err.Error(), "Result") {
		t.Fatalf("void 函数用 ? 应报错，得到: %v", err)
	}
}

func Test编译期穷尽检查(t *testing.T) {
	prog, _ := syntax.Parse(`enum E {
    A(int)
    B
}
fn f(e: E): int {
    return match e {
        A(v) => v
    }
}
fn main() { }`)
	_, err := Transpile(prog)
	if err == nil || !strings.Contains(err.Error(), "穷尽") {
		t.Fatalf("缺 case 应在编译期报错，得到: %v", err)
	}
	// 补上通配臂则通过
	_ = transpile(t, `enum E {
    A(int)
    B
}
fn f(e: E): int {
    return match e {
        A(v) => v
        _ => 0
    }
}
fn main() { }`)
}

// Test等价_M4移植 m4 目录经 load.Program 装载（含 video/list 移植的全部文件），
// 解释器与 →Go 编译执行输出比对，并与 gofixture（Go 原版逐字拷贝）的
// 实测输出对齐（跳过其 K 表段）。
func Test等价_M4移植(t *testing.T) {
	if testing.Short() {
		t.Skip("short 模式跳过 go build")
	}
	prog, _, err := load.Program(filepath.Join("..", "..", "m4"), false)
	if err != nil {
		t.Fatalf("装载 m4 失败: %v", err)
	}
	var out bytes.Buffer
	if err := interp.New(&out).Run(prog); err != nil {
		t.Fatalf("解释器运行失败: %v", err)
	}
	want := out.String()
	got := goRun(t, transpileSrc(t, prog))
	if want != got {
		t.Fatalf("解释器与 →Go 输出不一致\n=== 解释器 ===\n%q\n=== Go 编译执行 ===\n%q", want, got)
	}
	fixtureBytes, err := os.ReadFile(filepath.Join("..", "..", "m4", "gofixture.expected.txt"))
	if err != nil {
		t.Fatalf("读 gofixture.expected.txt: %v", err)
	}
	var fixtureLines []string
	started := false
	for _, line := range strings.Split(string(fixtureBytes), "\n") {
		if strings.HasPrefix(line, "MD5|") {
			started = true
		}
		if started {
			fixtureLines = append(fixtureLines, line)
		}
	}
	fixture := strings.Join(fixtureLines, "\n")
	if got != fixture {
		t.Fatalf("与 Go 原版 fixture 不一致\n=== gofixture ===\n%q\n=== ling（→Go）===\n%q", fixture, got)
	}
}

// Test等价_示例程序 用 examples/ 下的真实程序做端到端等价验证
func Test等价_示例程序(t *testing.T) {
	if testing.Short() {
		t.Skip("short 模式跳过 go build")
	}
	for _, name := range []string{"hello", "shapes", "result", "closures", "json", "compound"} {
		name := name
		t.Run(name, func(t *testing.T) {
			srcBytes, err := os.ReadFile(filepath.Join("..", "..", "examples", name, "main.ling"))
			if err != nil {
				t.Fatalf("读示例失败: %v", err)
			}
			src := string(srcBytes)
			want := interpRun(t, src)
			got := goRun(t, transpile(t, src))
			if got != want {
				t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Go 编译执行 ===\n%q", want, got)
			}
		})
	}
}
