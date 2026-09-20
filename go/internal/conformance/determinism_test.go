package conformance

// Determinism 契约测试（v0.9 Hardening，总纲 §16）：同一源码、同一版本下，
// 多次运行的输出必须逐字节稳定。覆盖：HIR/IR dump、check 诊断序、format、
// →Go/→JS 产物、四路执行输出。

import (
	"bytes"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"lang-go/internal/format"
	"lang-go/internal/gen"
	"lang-go/internal/genjs"
	"lang-go/internal/hir"
	"lang-go/internal/interp"
	"lang-go/internal/ir"
	"lang-go/internal/irinterp"
	"lang-go/internal/load"
	"lang-go/internal/pipeline"
)

func conformanceRoot(t *testing.T) string {
	t.Helper()
	_, thisFile, _, _ := runtime.Caller(0)
	root := filepath.Join(filepath.Dir(thisFile), "..", "..", "..", "conformance")
	if _, err := os.Stat(root); err != nil {
		t.Fatalf("conformance 目录缺失: %v", err)
	}
	return root
}

func snapshotProgram(t *testing.T, dir string) string {
	t.Helper()
	prog, _, err := load.Program(dir, false)
	if err != nil {
		t.Fatalf("装载 %s: %v", dir, err)
	}
	a, err := pipeline.AnalyzeStmts(prog)
	if err != nil {
		// 非法程序（diagnostics 类）：快照记录错误文本，确定性依然可比
		return "ANALYZE-ERR: " + err.Error() + "\n"
	}
	var b strings.Builder
	b.WriteString("== check ==\n")
	diags, _ := pipeline.CheckStmts(prog)
	b.WriteString(pipeline.FormatDiags(diags))
	b.WriteString("\n== hir ==\n")
	b.WriteString(hir.Dump(a.HIR))
	b.WriteString("\n== ir ==\n")
	b.WriteString(ir.Dump(a.Prog))
	b.WriteString("\n")
	b.WriteString("== interp ==\n")
	var ib bytes.Buffer
	if err := interp.New(&ib).Run(prog); err != nil {
		b.WriteString("ERR: " + err.Error() + "\n")
	} else {
		b.WriteString(ib.String())
	}
	b.WriteString("== irinterp ==\n")
	var rb bytes.Buffer
	if err := irinterp.New(&rb).Run(a.Prog); err != nil {
		b.WriteString("ERR: " + err.Error() + "\n")
	} else {
		b.WriteString(rb.String())
	}
	b.WriteString("== gen ==\n")
	goSrc, err := gen.Transpile(a.Prog)
	if err != nil {
		b.WriteString("ERR: " + err.Error() + "\n")
	} else {
		b.WriteString(goSrc)
	}
	b.WriteString("== genjs ==\n")
	jsSrc, err := genjs.TranspileJS(a.Prog)
	if err != nil {
		b.WriteString("ERR: " + err.Error() + "\n")
	} else {
		b.WriteString(jsSrc)
	}
	return b.String()
}

func TestDeterminism两次运行快照一致(t *testing.T) {
	root := conformanceRoot(t)
	cats, err := os.ReadDir(root)
	if err != nil {
		t.Fatal(err)
	}
	checked := 0
	for _, cat := range cats {
		if !cat.IsDir() {
			continue
		}
		cases, err := os.ReadDir(filepath.Join(root, cat.Name()))
		if err != nil {
			t.Fatal(err)
		}
		for _, c := range cases {
			if !c.IsDir() {
				continue
			}
			dir := filepath.Join(root, cat.Name(), c.Name())
			first := snapshotProgram(t, dir)
			second := snapshotProgram(t, dir)
			if first != second {
				t.Errorf("非确定性: %s/%s 两次快照不一致", cat.Name(), c.Name())
			}
			checked++
		}
	}
	if checked == 0 {
		t.Fatal("未检查任何 case")
	}
	t.Logf("确定性检查 %d case × 2 次快照全部一致", checked)
}

func TestDeterminism格式化幂等全语料(t *testing.T) {
	root := conformanceRoot(t)
	var files []string
	filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
		if err == nil && strings.HasSuffix(path, ".lang-go") {
			files = append(files, path)
		}
		return nil
	})
	if len(files) == 0 {
		t.Fatal("语料为空")
	}
	for _, f := range files {
		bs, err := os.ReadFile(f)
		if err != nil {
			t.Fatal(err)
		}
		once, err := format.Format(string(bs))
		if err != nil {
			t.Errorf("%s 格式化失败: %v", f, err)
			continue
		}
		twice, err := format.Format(once)
		if err != nil {
			t.Errorf("%s 二次格式化失败: %v", f, err)
			continue
		}
		if once != twice {
			t.Errorf("格式化非幂等: %s", f)
		}
	}
}
