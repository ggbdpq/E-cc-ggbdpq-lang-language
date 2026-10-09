package fix

// safe fix 测试缝：每类修复的正反用例 + 事务闭环（plan → apply → 验证）。

import (
	"lang-go/internal/edit"
	"lang-go/internal/syntax"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func apply(t *testing.T, src string) string {
	t.Helper()

	edits := PlanFile(src)
	rs := []rune(src)
	// PlanFile 已按 StartOffset 降序
	for _, e := range edits {
		rs = append(rs[:e.StartOffset], append([]rune(e.Replacement), rs[e.EndOffset:]...)...)
	}

	return string(rs)
}

func Test重复import去重(t *testing.T) {
	src := "import \"./util\"\nimport \"./util\"\nfn main() {\n    println(1)\n}\n"

	out := apply(t, src)
	if strings.Contains(out, "import") {
		t.Fatalf("未使用的重复 import 应全部删除: %q", out)
	}

	if _, err := syntax.Parse(out); err != nil {
		t.Fatalf("修复后不可解析: %v", err)
	}
}

func Test未使用import删除(t *testing.T) {
	// main 未引用 util 的任何名字 → 删除
	src := "import \"./util\"\nfn main() {\n    println(1)\n}\n"

	out := apply(t, src)
	if strings.Contains(out, "import") {
		t.Fatalf("未使用 import 应删除: %q", out)
	}
}

func Test使用中import保留(t *testing.T) {
	// main 引用 utilHelp（util 文件的名字）→ 保留
	src := "import \"./util\"\nfn main() {\n    println(utilHelp())\n}\n"

	out := apply(t, src)
	if !strings.Contains(out, "import") {
		t.Fatalf("使用中的 import 不得删除: %q", out)
	}
}

func Test行尾空白与EOF换行(t *testing.T) {
	src := "fn main() {\n    println(1)   \n}"

	out := apply(t, src)
	if strings.Contains(out, " \n") || !strings.HasSuffix(out, "\n") {
		t.Fatalf("应去行尾空白并补 EOF 换行: %q", out)
	}
}

func TestPlanDir端到端(t *testing.T) {
	dir := t.TempDir()
	os.WriteFile(filepath.Join(dir, "main.lang-go"),
		[]byte("import \"./util\"\nimport \"./util\"\nfn main() {\n    let n = 1   \n    println(n)\n}\n"), 0o644)
	os.WriteFile(filepath.Join(dir, "util.lang-go"),
		[]byte("fn utilHelp(): int {\n    return 7\n}\n"), 0o644)

	ws, err := PlanDir(dir)
	if err != nil {
		t.Fatal(err)
	}

	if len(ws.Edits) == 0 || len(ws.Preconditions) == 0 {
		t.Fatalf("应有编辑与前置校验: %+v", ws)
	}

	if err := edit.Apply(ws, dir); err != nil {
		t.Fatal(err)
	}

	bs, _ := os.ReadFile(filepath.Join(dir, "main.lang-go"))
	if strings.Contains(string(bs), " \n") {
		t.Fatalf("行尾空白未清除: %q", string(bs))
	}

	if strings.Contains(string(bs), "import") {
		t.Fatalf("未使用的重复 import 应全部删除: %q", string(bs))
	}
}
