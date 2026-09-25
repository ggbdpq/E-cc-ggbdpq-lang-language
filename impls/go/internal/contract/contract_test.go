package contract

// 契约层测试缝：JSON golden、确定性、效果推导、位置定位。
// golden 变更 = 协议变更，须显式 UPDATE_GOLDEN=1 再生并同步 specs。

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"lang-go/internal/graph"
	"lang-go/internal/pipeline"
)

const sampleSrc = `struct Point {
    x: int
    y: int
}

enum Shape {
    Circle(float)
    Unit
}

fn (p Point).dist2(): int {
    return p.x * p.x + p.y * p.y
}

fn parseAge(s: string): Result[int, error] {
    if s == "" {
        return Err(err("空输入"))
    }
    return Ok(int(s))
}

fn emit(ch: chan[string], who: string) {
    send(ch, "hi " + who)
}

fn main() {
    let p = Point{x: 1, y: 2}
    println("d =", p.dist2())
    let ch = makeChan()
    go emit(ch, "lang-go")
    println(recv(ch))
    let age = match parseAge("28") {
        Ok(v) => v
        Err(e) => 0
    }
    println("age =", age)
}
`

// aImpact 以 sampleSrc 的 area 为影响分析目标
func aImpact(a *pipeline.Analyzed) graph.Impact {
	g := graph.Build(a.AST, a.Res, a.Ty, a.HIR, a.Prog, nil)
	for _, sym := range a.Res.Symbols {
		if !sym.BuiltinMark && sym.Name == "dist2" {
			return g.Impact(int(sym.ID), -1)
		}
	}
	return g.Impact(-1, -1)
}

func analyze(t *testing.T, src string) *pipeline.Analyzed {
	t.Helper()
	a, err := pipeline.Analyze(src)
	if err != nil {
		t.Fatalf("管线失败: %v", err)
	}
	return a
}

func mustJSON(t *testing.T, v any) []byte {
	t.Helper()
	bs, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	return bs
}

func TestGolden(t *testing.T) {
	a := analyze(t, sampleSrc)
	cases := map[string]any{
		"ast":          DumpEnvelope(KindDumpAST, ASTJSON(a.AST, a.Res, a.Ty)),
		"hir":          DumpEnvelope(KindDumpHIR, HIRJSON(a.HIR)),
		"ir":           DumpEnvelope(KindDumpIR, IRJSON(a.Prog)),
		"symbols":      SymbolsEnvelope(a.AST, a.Res, a.Ty),
		"refs":         ReferencesEnvelope(a.AST, a.Res, -1),
		"effects":      EffectsEnvelope(Effects(a.Prog)),
		"dependencies": DependenciesEnvelope(nil),
		"impact":       ImpactEnvelope(aImpact(a)),
	}
	for name, payload := range cases {
		name, payload := name, payload
		t.Run(name, func(t *testing.T) {
			got := mustJSON(t, payload)
			golden := filepath.Join("testdata", "golden", name+".json")
			if os.Getenv("UPDATE_GOLDEN") != "" {
				if err := os.MkdirAll(filepath.Dir(golden), 0o755); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(golden, got, 0o644); err != nil {
					t.Fatal(err)
				}
				return
			}
			want, err := os.ReadFile(golden)
			if err != nil {
				t.Fatalf("缺少 golden（UPDATE_GOLDEN=1 生成）: %v", err)
			}
			if !bytes.Equal(got, want) {
				t.Fatalf("与 golden 不一致\n=== got ===\n%s\n=== want ===\n%s", got, want)
			}
		})
	}
}

func TestDeterministic(t *testing.T) {
	a1 := analyze(t, sampleSrc)
	a2 := analyze(t, sampleSrc)
	b1 := mustJSON(t, SymbolsEnvelope(a1.AST, a1.Res, a1.Ty))
	b2 := mustJSON(t, SymbolsEnvelope(a2.AST, a2.Res, a2.Ty))
	if !bytes.Equal(b1, b2) {
		t.Fatal("symbols 两次运行不一致")
	}
	e1 := mustJSON(t, EffectsEnvelope(Effects(a1.Prog)))
	e2 := mustJSON(t, EffectsEnvelope(Effects(a2.Prog)))
	if !bytes.Equal(e1, e2) {
		t.Fatal("effects 两次运行不一致")
	}
	d1 := mustJSON(t, DumpEnvelope(KindDumpIR, IRJSON(a1.Prog)))
	d2 := mustJSON(t, DumpEnvelope(KindDumpIR, IRJSON(a2.Prog)))
	if !bytes.Equal(d1, d2) {
		t.Fatal("IR dump 两次运行不一致")
	}
}

func TestEffects推导(t *testing.T) {
	a := analyze(t, sampleSrc)
	effs := Effects(a.Prog)
	byName := map[string][]string{}
	for _, e := range effs {
		byName[e.Name] = e.Effects
	}
	has := func(effs []string, e string) bool {
		for _, x := range effs {
			if x == e {
				return true
			}
		}
		return false
	}
	// emit：send → CHANNEL
	if got := byName["emit"]; len(got) != 1 || got[0] != EffCHANNEL {
		t.Fatalf("emit effects = %v, 期望 [CHANNEL]", got)
	}
	// main：println → IO；chan 内建 → CHANNEL；go emit → SPAWN + 传播 CHANNEL
	main := byName["main"]
	if !has(main, EffIO) || !has(main, EffCHANNEL) || !has(main, EffSPAWN) {
		t.Fatalf("main effects = %v, 期望含 IO/CHANNEL/SPAWN", main)
	}
	// 纯函数：无效果生产者 → PURE
	if got := byName["dist2"]; len(got) != 1 || got[0] != EffPure {
		t.Fatalf("dist2 effects = %v, 期望 [PURE]", got)
	}
	if got := byName["parseAge"]; len(got) != 1 || got[0] != EffPure {
		t.Fatalf("parseAge effects = %v, 期望 [PURE]", got)
	}
}

func TestTypeAt(t *testing.T) {
	a := analyze(t, "fn main() {\n    let n = 42\n    println(n)\n}")
	// "    let n = 42"：42 占列 13..14，取列 14 命中字面量内部
	n := TypeAt(a.AST, a.Res, a.Ty, "", 2, 14)
	if n == nil || n.Kind != "int_lit" || n.Type != "int" {
		t.Fatalf("type-at 应命中 int_lit/int, 得到 %+v", n)
	}
	if got := TypeAt(a.AST, a.Res, a.Ty, "", 1, 1); got != nil && got.Kind == "int_lit" {
		t.Fatalf("fn 头不应命中 int_lit")
	}
}

func TestCheckJSON负载(t *testing.T) {
	out := Check("fn main() {\nprintln(nope)\n}")
	if out.Success {
		t.Fatal("非法程序应 success=false")
	}
	if len(out.Diagnostics) != 1 || out.Diagnostics[0].Code != "E2002" {
		t.Fatalf("期望 [E2002], 得到 %+v", out.Diagnostics)
	}
	if out.Diagnostics[0].PrimarySpan == nil || out.Diagnostics[0].PrimarySpan.Start.Line != 2 {
		t.Fatalf("primarySpan 行错误: %+v", out.Diagnostics[0].PrimarySpan)
	}
	okOut := Check("fn main() {\nprintln(1)\n}")
	if !okOut.Success || len(okOut.Diagnostics) != 0 {
		t.Fatalf("合法程序应零诊断: %+v", okOut.Diagnostics)
	}
}

func Test无效输入(t *testing.T) {
	if _, err := pipeline.Analyze("fn main() {"); err == nil {
		t.Fatal("解析失败应返回错误")
	}
	out := Check("let x: int = \"s\"\nfn main() { }")
	if out.Success || out.Diagnostics[0].Code != "E3001" {
		t.Fatalf("期望 E3001, 得到 %+v", out.Diagnostics)
	}
}
