package syntax

// Fuzz 词法/解析健壮性（v0.9 Hardening）：任意字节输入不得 panic——
// 诊断可以报错，但崩溃即实现缺陷。`go test` 跑 seed corpus；
// `go test -fuzz FuzzLexParse -fuzztime 60s` 深度模糊（手动/CI 可选）。

import (
	"testing"
)

func FuzzLexParse(f *testing.F) {
	seeds := []string{
		"fn main() {\n    println(1)\n}\n",
		"let x = 1 +\n2\n",
		"struct P { x: int }\nfn main() { let p = P{x: 1}\nprintln(p.x) }\n",
		"fn f(n: int): int {\n    return match n {\n        0 => 1\n        _ => 2\n    }\n}\n",
		"import \"./x\"\n",
		"let s = \"a\\\"b\\n\"\n",
		"println(0xFF, 1..3)\n",
		"fn main() { let m = {\"k\": [1, 2.5, true]} }\n",
		"@!#$%^& malformed",
		"fn main() {",
		"\"\n",
		"0xZZ",
		"let",
	}
	for _, s := range seeds {
		f.Add([]byte(s))
	}
	f.Fuzz(func(t *testing.T, data []byte) {
		src := string(data)
		_, _ = Lex(src)   // 不 panic 即可（错误合法）
		_, _ = Parse(src) // 不 panic 即可
	})
}
