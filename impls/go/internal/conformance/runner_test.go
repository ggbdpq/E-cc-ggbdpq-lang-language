package conformance

// 套件执行入口：遍历仓库顶层 conformance/（跨实现共同真值），全部 case 必须通过。
// 环境性跳过（缺 go/node 工具链）会以 Skipped 显式呈现，测试失败并列出——不静默吞掉。

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func TestConformanceSuite(t *testing.T) {
	_, thisFile, _, _ := runtime.Caller(0)

	root := filepath.Join(filepath.Dir(thisFile), "..", "..", "..", "..", "conformance")
	if _, err := os.Stat(root); err != nil {
		t.Fatalf("conformance 套件目录缺失: %s（%v）", root, err)
	}

	results, fails, err := RunSuite(root)
	if err != nil {
		t.Fatal(err)
	}

	if len(results) == 0 {
		t.Fatal("套件为空——case 未入库")
	}

	var bad []string

	for _, r := range results {
		if !r.OK {
			status := "FAIL"
			if r.Skipped {
				status = "SKIP"
			}

			bad = append(bad, "  "+status+" "+r.Category+"/"+r.Name+": "+r.Note)
		}
	}
	// skip 与 fail 都要暴露：工具链缺失是环境事实，必须在输出中明示
	if fails > 0 {
		t.Fatalf("套件 %d/%d 未通过:\n%s", fails, len(results), strings.Join(bad, "\n"))
	}

	for _, r := range results {
		if r.Skipped {
			t.Logf("SKIP %s/%s: %s", r.Category, r.Name, r.Note)
		}
	}

	t.Logf("conformance 套件 %d case 全部通过", len(results))
}

func TestParseManifest受限子集拒绝(t *testing.T) {
	for _, bad := range []string{
		"name: x\ncategory: y\nmode: wat\nexpect:\n  exitCode: 0\n  stdout: |\n    a\n  stderr: \"\"\n",
		"name: x\ncategory: y\nunknown: 1\n",
		"name: x\ncategory: y\nexpect:\n  nested:\n    deep: 1\n",
		"name: x\ncategory: y\nmode: run\nexpect:\n  exitCode: 1\n  stdout: |\n    a\n  stderr: \"\"\n",
		"name: x\ncategory: y\nmode: run\nexpect:\n  exitCode: 0\n  stderr: \"\"\n",
	} {
		if _, err := ParseManifest(bad); err == nil {
			t.Errorf("应拒绝: %q", bad)
		}
	}

	ok := "name: x\ncategory: y\nmode: run\nexpect:\n  exitCode: 0\n  stdout: |\n    hello\n  stderr: \"\"\n"
	if _, err := ParseManifest(ok); err != nil {
		t.Errorf("合法 manifest 应通过: %v", err)
	}
}

// CTRL-WHILE-002：break 只退出最近的循环，后续语句与外层迭代仍须执行。
func TestWhileBreakKeepsOuterLoopRunning(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	src := `fn main() {
    for x in [1, 2] {
        while true {
            break
        }
        println(x)
    }
    println("after")
}
`

	manifest := `name: while-break-nearest-loop
category: expressions
ruleIds:
  - CTRL-WHILE-002
expect:
  exitCode: 0
  stdout: |
    1
    2
    after
  stderr: ""
`
	for name, text := range map[string]string{"main.lang-go": src, "case.yaml": manifest} {
		if err := os.WriteFile(filepath.Join(dir, name), []byte(text), 0o600); err != nil {
			t.Fatal(err)
		}
	}

	r := RunCase(dir)
	if !r.OK {
		t.Fatalf("while break 应只退出内层循环: %s", r.Note)
	}
}
