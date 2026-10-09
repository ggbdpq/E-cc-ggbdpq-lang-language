package syntax

// Span 盖章的最小校验：位置精确到 rune 偏移与列；文件归属由 load 层写.
import "testing"

func TestSpan盖章(t *testing.T) {
	prog, err := Parse("fn main() {\n    let n = 42\n    println(n)\n}\n")
	if err != nil {
		t.Fatal(err)
	}

	if len(prog) != 1 {
		t.Fatalf("stmt 数异常")
	}

	fn := prog[0].(*FnDecl)

	sp := fn.NodeSpan()
	if sp.Start.Line != 1 || sp.Start.Col != 1 {
		t.Fatalf("fn 起始 = %v, 期望 1:1", sp.Start)
	}

	if sp.End.Line != 4 {
		t.Fatalf("fn 终止行 = %v, 期望 4（'}' 所在行）", sp.End)
	}
	// let 语句：第二行，let 起点 5 列
	let := fn.Body[0].(*LetStmt)
	if let.Sp.Start.Line != 2 || let.Sp.Start.Col != 5 {
		t.Fatalf("let 起始 = %v, 期望 2:5", let.Sp.Start)
	}

	if let.Sp.Start.Offset != 16 {
		t.Fatalf("let offset = %v, 期望 16", let.Sp.Start.Offset)
	}
	// 字面量 42 的区间
	lit := let.Init.(*IntLit)
	if lit.Sp.Start.Col != 13 || lit.Sp.End.Col != 15 {
		t.Fatalf("42 区间列 = %v..%v, 期望 13..15", lit.Sp.Start.Col, lit.Sp.End.Col)
	}
	// 文件归属可写（load 层用）
	let.Sp.File = "main.lang-go"
	if let.Sp.File != "main.lang-go" {
		t.Fatal("File 不可写")
	}
}
