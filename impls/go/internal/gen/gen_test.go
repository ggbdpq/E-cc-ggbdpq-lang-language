package gen

// 测试缝两层：
// 1) Transpile 的约束报错（编译期检查：穷尽性、? 位置、注解要求）
// 2) 端到端等价：同一 langGo 程序，解释器输出与 →Go 编译执行输出逐字节一致
//    ——这是 →Go 后端"跑通"的机械判据，也是 M4 的验收方式
//
// 命令执行安全：go build / 产物运行的命令参数全部为字面量，
// 被测代码经 cmd.Dir 隔离在 t.TempDir() 临时目录中，无任何拼接输入。

import (
	"bytes"
	"lang-go/internal/interp"
	"lang-go/internal/load"
	"lang-go/internal/pipeline"
	"lang-go/internal/syntax"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

func transpile(t *testing.T, src string) string {
	t.Helper()

	p, err := pipeline.Compile(src)
	if err != nil {
		t.Fatalf("前端管线失败: %v", err)
	}

	out, err := Transpile(p)
	if err != nil {
		t.Fatalf("转译失败: %v", err)
	}

	return out
}

func transpileStmts(t *testing.T, prog []syntax.Stmt) string {
	t.Helper()

	p, err := pipeline.CompileStmts(prog)
	if err != nil {
		t.Fatalf("前端管线失败: %v", err)
	}

	out, err := Transpile(p)
	if err != nil {
		t.Fatalf("转译失败: %v", err)
	}

	return out
}

func sortLines(s string) string {
	lines := strings.Split(strings.TrimRight(s, "\n"), "\n")
	sort.Strings(lines)

	return strings.Join(lines, "\n")
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

// 转译约束自 v0.3 起前移至前端管线（semantic E2xxx + types E3xxx/E4xxx，
// 逐码正反用例见 internal/types/checker_test.go）；此处验证「backend 前拒绝」
// 这一 gate 本身：管线必须在这些程序进入转译之前失败。

func Test前端约束_参数必须注解(t *testing.T) {
	_, err := pipeline.Compile("fn f(a) { }")

	diags := pipeline.Diagnostics(err)
	if len(diags) == 0 || diags[0].Code != "E3007" {
		t.Fatalf("无注解参数应报 E3007，得到: %v", err)
	}
}

func Test前端约束_问号只能在语句顶层(t *testing.T) {
	_, err := pipeline.Compile(`fn f(): Result[int, error] {
    return Ok(1)
}
fn g(): Result[int, error] {
    return Ok(f()? + 1)
}
fn main() { }`)

	diags := pipeline.Diagnostics(err)
	if len(diags) == 0 || diags[0].Code != "E3005" {
		t.Fatalf("? 嵌在表达式里应报 E3005，得到: %v", err)
	}
}

func Test前端约束_带问号的函数须Result注解(t *testing.T) {
	_, err := pipeline.Compile(`fn f(): Result[int, error] {
    return Ok(1)
}
fn g() {
    let n = f()?
    println(n)
}
fn main() { }`)

	diags := pipeline.Diagnostics(err)
	if len(diags) == 0 || diags[0].Code != "E3005" {
		t.Fatalf("void 函数用 ? 应报 E3005，得到: %v", err)
	}
}

func Test前端约束_编译期穷尽检查(t *testing.T) {
	_, err := pipeline.Compile(`enum E {
    A(int)
    B
}
fn f(e: E): int {
    return match e {
        A(v) => v
    }
}
fn main() { }`)

	diags := pipeline.Diagnostics(err)
	if len(diags) == 0 || diags[0].Code != "E4001" {
		t.Fatalf("缺 case 应报 E4001，得到: %v", err)
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

	got := goRun(t, transpileStmts(t, prog))
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
		t.Fatalf("与 Go 原版 fixture 不一致\n=== gofixture ===\n%q\n=== langGo（→Go）===\n%q", fixture, got)
	}
}

// Test等价_示例程序 用 examples/ 下的真实程序做端到端等价验证.
func Test等价_示例程序(t *testing.T) {
	if testing.Short() {
		t.Skip("short 模式跳过 go build")
	}

	for _, name := range []string{"hello", "shapes", "result", "closures", "json", "compound", "tour", "channel"} {
		name := name
		t.Run(name, func(t *testing.T) {
			srcBytes, err := os.ReadFile(filepath.Join("..", "..", "examples", name, "main.lang-go"))
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
	// goroutine 例外：→Go 真并行行序不定，只比排序后的行集合
	t.Run("goroutine", func(t *testing.T) {
		srcBytes, err := os.ReadFile(filepath.Join("..", "..", "examples", "goroutine", "main.lang-go"))
		if err != nil {
			t.Fatal(err)
		}

		src := string(srcBytes)
		want := sortLines(interpRun(t, src))

		got := sortLines(goRun(t, transpile(t, src)))
		if got != want {
			t.Fatalf("行集合不一致\n=== 解释器 ===\n%q\n=== Go 编译执行 ===\n%q", want, got)
		}
	})
}

// —— recv 阻塞语义（decisions #63）：recv 等待直到有值 ————

// 确定性判据：send 延迟 50ms 到达，recv 必须等到它。
// 非阻塞旧实现（select-default）会立即返回 Err——此测试即为红绿判定点。
// prelude 是字符串模板，故经 goRun 执行「prelude + 手写 main」的完整产物验证。
func TestRecv阻塞语义(t *testing.T) {
	if testing.Short() {
		t.Skip("short 模式跳过 go build")
	}

	src := prelude + `
func main() {
	ch := langGoNewChan()
	go func() {
		time.Sleep(50 * time.Millisecond)
		langGoSend(ch, 42)
	}()
	r := langGoRecv(ch)
	if !r.ok {
		fmt.Println("ERR")
		return
	}
	fmt.Println("OK", r.v)
}
`

	got := goRun(t, src)
	if got != "OK 42\n" {
		t.Fatalf("recv 应阻塞等到延迟 send 的值，实际输出 %q（ERR=非阻塞旧实现）", got)
	}
}

// —— ADR-0006 语义点：interp 与 →Go 产物逐字节等价（RS-006/RS-007）——

func Test等价_NaN比较与int饱和(t *testing.T) {
	if testing.Short() {
		t.Skip("short 模式跳过 go build")
	}

	src := `fn main() {
    var big = 1.0
    for i in 0..400 {
        big = big * 10.0
    }
    let nan = big - big
    println(int(nan), int(big), int(0.0 - big))
    println(nan == nan, nan != nan)
    println(nan < 1.0, nan <= 1.0, nan > 1.0, nan >= 1.0)
    println(1.0 < nan, 1.0 >= nan)
}`
	want := interpRun(t, src)
	// 期望值手算（RS-006：NaN→0、±Inf 饱和；RS-007：NaN 比较全 false、!= 恒 true）
	if want != "0 9223372036854775807 -9223372036854775808\nfalse true\nfalse false false false\nfalse false\n" {
		t.Fatalf("解释器输出与 ADR-0006 手算口径不符，实际 %q", want)
	}

	got := goRun(t, transpile(t, src))
	if got != want {
		t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Go 编译执行 ===\n%q", want, got)
	}
}

// —— →Go prelude 直测（RS-006/RS-007）：langGoInt 饱和语义与 langGoRel* NaN 行为 ——

func TestPrelude_langGoInt饱和与RelNaN(t *testing.T) {
	if testing.Short() {
		t.Skip("short 模式跳过 go build")
	}

	src := prelude + `
func main() {
	nan := math.NaN()
	inf := math.Inf(1)
	one := 1.0
	fmt.Println(langGoInt(nan), langGoInt(inf), langGoInt(-inf), langGoInt(9.3e18), langGoInt(0.5), langGoInt(2.9))
	fmt.Println(langGoRelLt(nan, one), langGoRelLe(nan, one), langGoRelGt(nan, one), langGoRelGe(nan, one))
	fmt.Println(langGoRelLt(one, nan), langGoRelGe(one, nan))
	fmt.Println(langGoRelLe(one, one), langGoRelGe(one, one))
}
`
	got := goRun(t, src)

	want := "0 9223372036854775807 -9223372036854775808 9223372036854775807 0 2\n" +
		"false false false false\nfalse false\ntrue true\n"
	if got != want {
		t.Fatalf("prelude 行为与 ADR-0006 口径不符\n=== 期望 ===\n%q\n=== 实际 ===\n%q", want, got)
	}
}
