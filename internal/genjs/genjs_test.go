package genjs

// 测试缝（与 →Go 后端同一判据）：同一 ling 程序，解释器输出与
// Node 执行 →JS 产物的 stdout 逐字节一致。
// 命令参数全部为字面量，被测代码隔离在 t.TempDir() 中。

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

func transpileJS(t *testing.T, prog []syntax.Stmt) string {
	t.Helper()
	out, err := TranspileJS(prog)
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
		t.Fatalf("与 Go 原版 fixture 不一致\n=== gofixture ===\n%q\n=== ling（→JS）===\n%q", fixture, got)
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
	out := ""
	for i, l := range lines {
		if i > 0 {
			out += "\n"
		}
		out += l
	}
	return out
}

func hasPrefix(s, p string) bool { return len(s) >= len(p) && s[:len(p)] == p }
