package contract

// CLI E2E 与 schema 结构校验。
// E2E 经 `go run` 执行真实 CLI（与 gen E2E 的 go build 模式一致），
// 参数全为字面量，工作目录隔离。

import (
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

func runCLI(t *testing.T, dir string, args ...string) (string, int) {
	t.Helper()
	bin := buildCLI(t)
	cmd := exec.Command(bin, args...)
	cmd.Dir = dir
	out, err := cmd.CombinedOutput()
	code := 0
	if err != nil {
		if ee, ok := errors.AsType[*exec.ExitError](err); ok {
			code = ee.ExitCode()
		} else {
			t.Fatalf("执行失败: %v\n%s", err, out)
		}
	}
	return string(out), code
}

// buildCLI 构建一次 CLI（模块上下文内），供全部用例复用.
func buildCLI(t *testing.T) string {
	t.Helper()
	bin := filepath.Join(t.TempDir(), "lang-go.exe")
	build := exec.Command("go", "build", "-o", bin, "../../cmd/lang-go")
	if out, err := build.CombinedOutput(); err != nil {
		t.Fatalf("构建 CLI 失败: %v\n%s", err, out)
	}
	return bin
}

func TestCLI端到端(t *testing.T) {
	if testing.Short() {
		t.Skip("short 模式跳过构建")
	}
	dir := filepath.Join("..", "..", "examples", "hello")
	envelope := func(t *testing.T, out string) map[string]any {
		t.Helper()
		var m map[string]any
		if err := json.Unmarshal([]byte(out), &m); err != nil {
			t.Fatalf("输出不是合法 JSON: %v\n%s", err, out)
		}
		for _, k := range []string{"schemaVersion", "toolVersion", "kind"} {
			if _, ok := m[k]; !ok {
				t.Fatalf("信封缺字段 %s: %s", k, out)
			}
		}
		if m["schemaVersion"] != SchemaVersion {
			t.Fatalf("schemaVersion = %v", m["schemaVersion"])
		}
		return m
	}

	for _, args := range [][]string{
		{"check", "--json"},
		{"dump", "ast", "--json"},
		{"dump", "hir", "--json"},
		{"dump", "ir", "--json"},
		{"inspect", "symbols", "--json"},
		{"inspect", "refs", "--json"},
		{"inspect", "dependencies", "--json"},
		{"inspect", "effects", "--json"},
	} {
		args := args
		t.Run(strings.Join(args, "_"), func(t *testing.T) {
			out, code := runCLI(t, dir, args...)
			if code != 0 {
				t.Fatalf("退出码 %d: %s", code, out)
			}
			envelope(t, out)
		})
	}

	// inspect type：命中 hello 的语句行
	out, code := runCLI(t, dir, "inspect", "type", "--json", "--line", "3", "--column", "18")
	if code != 0 {
		t.Fatalf("type 命中应退出 0: %s", out)
	}
	envelope(t, out)

	// 无效输入：未知子命令 → error 信封 + 非零退出码
	out, code = runCLI(t, dir, "inspect", "bogus", "--json")
	if code == 0 {
		t.Fatalf("未知子命令应非零退出: %s", out)
	}
	// CombinedOutput 会混入 stderr 的退出信息，这里只断言包含结构化 error 字段
	if !strings.Contains(out, `"`+"error"+`"`) || !strings.Contains(out, "未知子命令") {
		t.Fatalf("应输出结构化 error: %s", out)
	}

	// 非法程序：check --json 退出码 1 + 稳定码
	bad := t.TempDir()
	if err := os.WriteFile(filepath.Join(bad, "main.lang-go"),
		[]byte("fn main() {\nprintln(nope)\n}\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	out, code = runCLI(t, bad, "check", "--json")
	if code != 1 {
		t.Fatalf("非法程序应退出 1: %s", out)
	}
	if !strings.Contains(out, `"E2002"`) {
		t.Fatalf("缺稳定码 E2002: %s", out)
	}
}

// TestSchema结构校验 golden 负载必须满足 schemas/agent-protocol.schema.json
// 的 required 结构（不引第三方 JSON Schema 库，抽查 required 字段与枚举）。
func TestSchema结构校验(t *testing.T) {
	a := analyze(t, sampleSrc)
	envelopes := map[string]Envelope{
		"inspect.symbols":      SymbolsEnvelope(a.AST, a.Res, a.Ty),
		"inspect.refs":         ReferencesEnvelope(a.AST, a.Res, -1),
		"inspect.effects":      EffectsEnvelope(Effects(a.Prog)),
		"inspect.dependencies": DependenciesEnvelope(nil),
		"dump.ir":              DumpEnvelope(KindDumpIR, IRJSON(a.Prog)),
	}
	effEnum := map[string]bool{}
	for _, e := range []string{EffPure, EffIO, EffFS, EffNET, EffTIME, EffRANDOM, EffSPAWN, EffCHANNEL, EffFFI} {
		effEnum[e] = true
	}
	for kind, env := range envelopes {
		if env.SchemaVersion != "1" || env.ToolVersion == "" || env.Kind != kind {
			t.Fatalf("%s 信封字段缺失", kind)
		}
		for _, sym := range env.Symbols {
			if sym.SymbolID <= 0 || sym.Visibility == "" || sym.Kind == "" {
				t.Fatalf("%s symbol 结构违规: %+v", kind, sym)
			}
		}
		for _, r := range env.References {
			if r.ReferenceKind == "" || r.Span == nil {
				t.Fatalf("%s reference 结构违规: %+v", kind, r)
			}
		}
		for _, e := range env.Effects {
			for _, x := range e.Effects {
				if !effEnum[x] {
					t.Fatalf("效果枚举违规: %s", x)
				}
			}
		}
	}
	diag := Check("fn main() {\nprintln(nope)\n}").Diagnostics[0]
	if diag.Severity != "error" || diag.PrimarySpan == nil || diag.RelatedSpans == nil {
		t.Fatalf("诊断结构违规: %+v", diag)
	}
}
