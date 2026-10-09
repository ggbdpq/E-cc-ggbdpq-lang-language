package format

// v0.5 Safe Editing 门禁测试：注释 100% 保留、幂等、语义等价。
// 语料 = 仓库全部 .lang-go + 对抗样本（行尾/块尾/连续/纯注释/Unicode/CRLF/空文件）。

import (
	"bytes"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"

	"lang-go/internal/interp"
	"lang-go/internal/syntax"
)

// corpus 仓库全部 .lang-go（相对 internal/format）.
func corpus(t *testing.T) map[string]string {
	t.Helper()

	files := map[string]string{}

	roots := []string{"../../../../examples", "../../../../m4"}
	for _, root := range roots {
		err := filepath.Walk(root, func(p string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(p, ".lang-go") {
				return nil
			}

			bs, err := os.ReadFile(p)
			if err != nil {
				return err
			}

			files[p] = string(bs)

			return nil
		})
		if err != nil {
			t.Fatal(err)
		}
	}

	return files
}

// adversarial 对抗样本（注释边界全形状）.
func adversarial() map[string]string {
	return map[string]string{
		"行尾注释":      "fn main() {\n    let x = 1 // 行尾注释\n    println(x)\n}",
		"连续注释":      "// 头注释 A\n// 头注释 B\nfn main() {\n    println(1)\n}",
		"注释间空行":     "fn main() {\n    println(1)\n}\n\n// 尾注释组 A\n\n// 尾注释组 B\n",
		"块尾注释":      "fn main() {\n    println(1)\n    // 块尾注释\n}\n",
		"空行分组注释":    "fn f() {\n    let a = 1\n\n    // 分组说明\n    let b = 2\n    println(a, b)\n}\n",
		"纯注释文件":     "// 只有注释\n// 第二行\n",
		"空文件":       "",
		"Unicode注释": "fn main() {\n    // 中文注释 émoji 🎉\n    println(\"字符串中文\")\n}\n",
		"import注释":  "// 顶部说明\nimport \"./util\" // 引入工具\nfn main() {\n    println(1)\n}\n",
		"match臂注释":  "fn f(s: int): int {\n    return match s {\n        1 => 10 // 一\n        _ => 0 // 其他\n    }\n}\nfn main() {\n    println(f(1))\n}\n",
		"结构体字段注释":   "struct P {\n    x: int // 横坐标\n    y: int\n}\nfn main() {\n    println(P{x: 1, y: 2})\n}\n",
		"连续行尾注释同一行": "fn main() {\n    let a = 1\n    println(a) // 尾随\n}\n",
		"CRLF 行尾":   "fn main() {\r\n    let x = 1 // CRLF 注释\r\n    println(x)\r\n}\r\n",
		"CRLF 纯注释":  "// CRLF 注释文件\r\n// 第二行\r\n",
	}
}

func commentsSorted(src string) []string {
	var out []string
	for _, c := range syntax.ScanComments(src) {
		out = append(out, c.Text)
	}

	sort.Strings(out)

	return out
}

// Test注释100保留 全语料：格式化前后注释文本多重集一致（位置允许规范移动）.
func Test注释100保留(t *testing.T) {
	all := corpus(t)
	for name, src := range adversarial() {
		all["对抗/"+name] = src
	}

	for name, src := range all {
		name, src := name, src
		t.Run(name, func(t *testing.T) {
			out, err := Format(src)
			if err != nil {
				t.Fatalf("格式化失败: %v", err)
			}

			a, b := commentsSorted(src), commentsSorted(out)
			if strings.Join(a, "\x00") != strings.Join(b, "\x00") {
				t.Fatalf("注释丢失或改变\n=== 原 ===\n%v\n=== 格式化后 ===\n%v\n--- 输出 ---\n%s", a, b, out)
			}
		})
	}
}

// Test格式化幂等对抗 全语料：format(format(x)) == format(x).
func Test格式化幂等对抗(t *testing.T) {
	all := corpus(t)
	for name, src := range adversarial() {
		all["对抗/"+name] = src
	}

	for name, src := range all {
		name, src := name, src
		t.Run(name, func(t *testing.T) {
			once, err := Format(src)
			if err != nil {
				t.Fatalf("第一次格式化失败: %v", err)
			}

			twice, err := Format(once)
			if err != nil {
				t.Fatalf("第二次格式化失败: %v", err)
			}

			if once != twice {
				t.Fatalf("不幂等\n=== 一次 ===\n%q\n=== 两次 ===\n%q", once, twice)
			}
		})
	}
}

// Test行尾注释原位保留 行尾注释必须留在同一代码行行尾（最小 diff 的关键样本）.
func Test行尾注释原位保留(t *testing.T) {
	src := "fn main() {\n    let x = 1 // 行尾注释\n    println(x)\n}"

	out, err := Format(src)
	if err != nil {
		t.Fatal(err)
	}
	// 规范化形态：语句行 + 两空格 + 注释
	if !strings.Contains(out, "let x = 1  // 行尾注释") {
		t.Fatalf("行尾注释应保留在原代码行（两空格规范分隔）: %q", out)
	}
}

// Test语义等价 corpus 程序格式化前后解释器 stdout 逐字节一致.
func Test语义等价(t *testing.T) {
	for name, src := range corpus(t) {
		name, src := name, src
		if strings.Contains(name, "_test") {
			continue
		}

		t.Run(name, func(t *testing.T) {
			run := func() (string, error) {
				prog, err := syntax.Parse(src)
				if err != nil {
					return "", err
				}

				var out bytes.Buffer
				if err := interp.New(&out).Run(prog); err != nil {
					return "", err
				}

				return out.String(), nil
			}

			want, err := run()
			if err != nil {
				t.Skipf("原程序运行失败（跳过）: %v", err)
			}

			formatted, err := Format(src)
			if err != nil {
				t.Fatal(err)
			}

			got, err := run2(formatted)
			if err != nil {
				t.Fatal(err)
			}

			if got != want {
				t.Fatalf("格式化改变输出\n=== 前 ===\n%q\n=== 后 ===\n%q", want, got)
			}
		})
	}
}

func run2(src string) (string, error) {
	prog, err := syntax.Parse(src)
	if err != nil {
		return "", err
	}

	var out bytes.Buffer
	if err := interp.New(&out).Run(prog); err != nil {
		return "", err
	}

	return out.String(), nil
}
