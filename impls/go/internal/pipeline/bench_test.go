package pipeline

// 性能基线（v0.9 Hardening，总纲 §20）：各阶段 benchmark。
// 运行：go test ./internal/pipeline/ -bench . -benchmem
// 基线记录：specs/performance.md（仅记录量级与回归参照，不做极致优化）。

import (
	"lang-go/internal/gen"
	"lang-go/internal/genjs"
	"lang-go/internal/interp"
	"lang-go/internal/irinterp"
	"lang-go/internal/syntax"
	"strconv"
	"strings"
	"testing"
)

// 声明唯一，规模经 N 个工作函数扩展（避免重复定义 E2001）.
const benchCorpusHead = `
struct Point {
    x: int
    y: int
}
fn (p Point).norm2(): int {
    return p.x * p.x + p.y * p.y
}
fn classify(n: int): string {
    return match n {
        0 => "zero"
        1 => "one"
        _ => "many"
    }
}
fn build(i: int): Point {
    return Point{x: i, y: i * 2}
}
`

func itoa(n int) string { return strconv.Itoa(n) }

func benchCorpus(work int) string {
	var b strings.Builder
	b.WriteString(benchCorpusHead)

	for i := 0; i < work; i++ {
		b.WriteString("fn work" + itoa(i) + "(i: int): int {\n")
		b.WriteString("    let p = build(i)\n")
		b.WriteString("    var acc = p.norm2()\n")
		b.WriteString("    for j in 0..50 {\n")
		b.WriteString("        acc += match j % 3 {\n            0 => 1\n            _ => 2\n        }\n")
		b.WriteString("    }\n")
		b.WriteString("    return acc\n")
		b.WriteString("}\n")
	}

	b.WriteString("fn main() {\n    var acc = 0\n")

	for i := 0; i < work; i++ {
		b.WriteString("    acc += work" + itoa(i) + "(" + itoa(i) + ")\n")
	}

	b.WriteString("    println(acc)\n")
	b.WriteString("    let m = {\"a\": 1, \"b\": 2, \"c\": 3}\n")
	b.WriteString("    println(keys(m), values(m))\n")
	b.WriteString("    println(jsonStringify(m))\n")
	b.WriteString("}\n")

	return b.String()
}

func benchSrc() string { return benchCorpus(20) }

func benchProg(b *testing.B) []syntax.Stmt {
	b.Helper()

	prog, err := syntax.Parse(benchSrc())
	if err != nil {
		b.Fatal(err)
	}

	return prog
}

func BenchmarkLex(b *testing.B) {
	src := benchSrc()

	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		if _, err := syntax.Lex(src); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkParse(b *testing.B) {
	src := benchSrc()

	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		if _, err := syntax.Parse(src); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkAnalyze(b *testing.B) {
	prog := benchProg(b)
	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		if _, err := AnalyzeStmts(prog); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkIRInterp(b *testing.B) {
	prog := benchProg(b)

	a, err := AnalyzeStmts(prog)
	if err != nil {
		b.Fatal(err)
	}

	var sink strings.Builder

	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		sink.Reset()

		if err := irinterp.New(&sink).Run(a.Prog); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkASTInterp(b *testing.B) {
	prog := benchProg(b)

	var sink strings.Builder

	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		sink.Reset()

		if err := interp.New(&sink).Run(prog); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkGenGo(b *testing.B) {
	prog := benchProg(b)

	a, err := AnalyzeStmts(prog)
	if err != nil {
		b.Fatal(err)
	}

	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		if _, err := gen.Transpile(a.Prog); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkGenJS(b *testing.B) {
	prog := benchProg(b)

	a, err := AnalyzeStmts(prog)
	if err != nil {
		b.Fatal(err)
	}

	b.ResetTimer()

	for i := 0; i < b.N; i++ {
		if _, err := genjs.TranspileJS(a.Prog); err != nil {
			b.Fatal(err)
		}
	}
}
