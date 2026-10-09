package genjs

// 测试缝（与 →Go 后端同一判据）：同一 langGo 程序，解释器输出与
// Node 执行 →JS 产物的 stdout 逐字节一致。
// 命令参数全部为字面量，被测代码隔离在 t.TempDir() 中。

import (
	"bytes"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"lang-go/internal/interp"
	"lang-go/internal/load"
	"lang-go/internal/pipeline"
	"lang-go/internal/syntax"
)

func transpileJS(t *testing.T, prog []syntax.Stmt) string {
	t.Helper()
	p, err := pipeline.CompileStmts(prog)
	if err != nil {
		t.Fatalf("前端管线失败: %v", err)
	}
	out, err := TranspileJS(p)
	if err != nil {
		t.Fatalf("转译失败: %v", err)
	}
	return out
}

func interpRunJS(t *testing.T, prog []syntax.Stmt) string {
	t.Helper()
	var out bytes.Buffer
	if err := interp.New(&out).Run(prog); err != nil {
		t.Fatalf("解释器运行失败: %v", err)
	}
	return out.String()
}

// nodeRun 把产物写入临时目录并以 ESM 执行
func nodeRun(t *testing.T, js string) string {
	t.Helper()
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "out.mjs"), []byte(js), 0o644); err != nil {
		t.Fatal(err)
	}
	run := exec.Command("node", "out.mjs")
	run.Dir = dir
	var stdout bytes.Buffer
	run.Stdout = &stdout
	run.Stderr = &stdout
	if err := run.Run(); err != nil {
		t.Fatalf("Node 执行失败: %v\n%s", err, stdout.String())
	}
	return stdout.String()
}

func loadDir(t *testing.T, rel string) []syntax.Stmt {
	t.Helper()
	prog, _, err := load.Program(filepath.Join("..", "..", rel), false)
	if err != nil {
		t.Fatalf("装载 %s: %v", rel, err)
	}
	return prog
}

func Test等价JS_hello(t *testing.T) {
	prog := loadDir(t, "examples/hello")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Node ===\n%q", want, got)
	}
}

func Test等价JS_shapes(t *testing.T) {
	prog := loadDir(t, "examples/shapes")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Node ===\n%q", want, got)
	}
}

func Test等价JS_result(t *testing.T) {
	prog := loadDir(t, "examples/result")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Node ===\n%q", want, got)
	}
}

func Test等价JS_closures(t *testing.T) {
	prog := loadDir(t, "examples/closures")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Node ===\n%q", want, got)
	}
}

func Test等价JS_json(t *testing.T) {
	prog := loadDir(t, "examples/json")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Node ===\n%q", want, got)
	}
}

func Test等价JS_compound(t *testing.T) {
	prog := loadDir(t, "examples/compound")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Node ===\n%q", want, got)
	}
}

func Test等价JS_tour(t *testing.T) {
	prog := loadDir(t, "examples/tour")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("tour 输出不一致: 解释器 %q Node %q", want, got)
	}
}

func Test等价JS_goroutine(t *testing.T) {
	prog := loadDir(t, "examples/goroutine")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("goroutine 输出不一致: 解释器 %q Node %q", want, got)
	}
}

func Test等价JS_channel(t *testing.T) {
	prog := loadDir(t, "examples/channel")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("channel 输出不一致: 解释器 %q Node %q", want, got)
	}
}

func Test等价JS_M4移植(t *testing.T) {
	prog := loadDir(t, "m4")
	want := interpRunJS(t, prog)
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Node ===\n%q", want, got)
	}
	// 与 gofixture（Go 原版）对齐
	fixtureBytes, err := os.ReadFile(filepath.Join("..", "..", "m4", "gofixture.expected.txt"))
	if err != nil {
		t.Fatal(err)
	}
	var fixtureLines []string
	started := false
	for _, line := range splitLines(string(fixtureBytes)) {
		if hasPrefix(line, "MD5|") {
			started = true
		}
		if started {
			fixtureLines = append(fixtureLines, line)
		}
	}
	fixture := strings.TrimRight(joinLines(fixtureLines), "\n")
	got = strings.TrimRight(got, "\n")
	if got != fixture {
		t.Fatalf("与 Go 原版 fixture 不一致\n=== gofixture ===\n%q\n=== langGo（→JS）===\n%q", fixture, got)
	}
}

func splitLines(s string) []string {
	var out []string
	start := 0
	for i := 0; i < len(s); i++ {
		if s[i] == '\n' {
			out = append(out, s[start:i])
			start = i + 1
		}
	}
	if start < len(s) {
		out = append(out, s[start:])
	}
	return out
}

func joinLines(lines []string) string {
	return strings.Join(lines, "\n")
}

func hasPrefix(s, p string) bool { return len(s) >= len(p) && s[:len(p)] == p }

// —— ADR-0006 语义点：interp 与 →JS 产物逐字节等价（RS-006/RS-007/RS-003）——

func Test等价JS_NaN比较与int饱和(t *testing.T) {
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
	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}
	want := interpRunJS(t, prog)
	// 期望值手算（RS-006：NaN→0、±Inf 饱和；RS-007：NaN 比较全 false、!= 恒 true）
	if want != "0 9223372036854775807 -9223372036854775808\nfalse true\nfalse false false false\nfalse false\n" {
		t.Fatalf("解释器输出与 ADR-0006 手算口径不符，实际 %q", want)
	}
	got := nodeRun(t, transpileJS(t, prog))
	if got != want {
		t.Fatalf("输出不一致\n=== 解释器 ===\n%q\n=== Node ===\n%q", want, got)
	}
}

func Test等价JS_jsonStringify非字符串键报错(t *testing.T) {
	// RS-003：非字符串映射键 = 受控运行时错误（不再按 String(k) 直出）
	src := `fn main() {
    println(jsonStringify({1: "x"}))
}`
	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v", err)
	}
	js := transpileJS(t, prog)
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "out.mjs"), []byte(js), 0o644); err != nil {
		t.Fatal(err)
	}
	run := exec.Command("node", "out.mjs")
	run.Dir = dir
	var out bytes.Buffer
	run.Stdout = &out
	run.Stderr = &out
	err = run.Run()
	if err == nil {
		t.Fatalf("期望受控报错但成功退出\n输出: %s", out.String())
	}
	if !strings.Contains(out.String(), "JSON 对象键须为 string，得到 int") {
		t.Fatalf("错误消息不符\n实际输出: %s", out.String())
	}
}

func TestTranspileJSPropagatesBlockError(t *testing.T) {
	t.Parallel()
	tests := []struct{ name, source string }{
		{name: "top_level", source: `fn main() { let p = println }`},
		{name: "if_then", source: `fn main() { if true { let p = println } }`},
		{name: "if_else", source: `fn main() { if false { println("ok") } else { let p = println } }`},
		{name: "while_body", source: `fn main() { while false { let p = println } }`},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			prog, err := pipeline.Compile(tt.source)
			if err != nil {
				t.Fatal(err)
			}
			out, err := TranspileJS(prog)
			if err == nil || !strings.Contains(err.Error(), "内建 println 须直接调用") {
				t.Fatalf("TranspileJS() error = %v, want 内建 println 须直接调用", err)
			}
			if out != "" {
				t.Fatalf("failed transpilation returned partial output")
			}
		})
	}
}
