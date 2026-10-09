package conformance

// runner.go：case 执行器。run 模式 = 三路差分（解释器 / →Go 产物 / →JS 产物）
// stdout 逐字节一致 + exitCode 0 + stderr 空；check 模式 = 诊断断言
// （code + severity + primarySpan.line 多重集匹配，message 允许漂移）。
//
// 后端执行依赖宿主工具链（go build / node）：缺失时结果为 skipped——
// 套件不把 skip 计入通过，调用方（runner_test / verify）必须显式呈现。

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"lang-go/internal/contract"
	"lang-go/internal/gen"
	"lang-go/internal/genjs"
	"lang-go/internal/interp"
	"lang-go/internal/irinterp"
	"lang-go/internal/load"
	"lang-go/internal/pipeline"
	"lang-go/internal/syntax"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
)

// CaseResult 单 case 执行结果.
type CaseResult struct {
	Name     string   `json:"name"`
	Category string   `json:"category"`
	Mode     string   `json:"mode"`
	RuleIDs  []string `json:"ruleIds,omitempty"`
	OK       bool     `json:"ok"`
	Skipped  bool     `json:"skipped,omitempty"` // 工具链缺失等环境性跳过（不计入通过）
	Note     string   `json:"note,omitempty"`    // 失败细节或跳过原因
}

// RunCase 执行单个 case 目录（内含 case.yaml 与程序文件）.
func RunCase(dir string) CaseResult {
	bs, err := os.ReadFile(filepath.Join(dir, "case.yaml"))
	if err != nil {
		return CaseResult{Name: filepath.Base(dir), Note: "读 case.yaml: " + err.Error()}
	}

	c, err := ParseManifest(string(bs))
	if err != nil {
		return CaseResult{Name: filepath.Base(dir), Note: err.Error()}
	}

	res := CaseResult{Name: c.Name, Category: c.Category, Mode: c.Mode, RuleIDs: c.RuleIDs}

	prog, _, err := load.Program(dir, false)
	if err != nil {
		res.Note = "装载失败: " + err.Error()
		return res
	}

	switch c.Mode {
	case "run":
		return runDifferential(c, prog, res)
	case "check":
		return runDiagnostic(c, prog, res)
	}

	res.Note = "未知 mode " + c.Mode

	return res
}

// runDifferential 三路执行 + 一致性断言
func runDifferential(c *Case, prog []syntax.Stmt, res CaseResult) CaseResult {
	// 正确程序前提：前端必须零诊断
	diags, _ := pipeline.CheckStmts(prog)
	if len(diags) > 0 {
		res.Note = "run 模式 case 前端诊断: " + pipeline.FormatDiags(diags)
		return res
	}

	a, err := pipeline.AnalyzeStmts(prog)
	if err != nil {
		res.Note = "分析失败: " + err.Error()
		return res
	}

	// 1) AST 参考解释器（进程内；writer 注入即 stdout；err 非空 = 退出码非 0 的进程内等价物）
	var outInterp bytes.Buffer
	if err := interp.New(&outInterp).Run(prog); err != nil {
		res.Note = "解释器执行失败: " + err.Error()
		return res
	}

	// 1b) IR 解释器（进程内，无工具链依赖）：证明 IR 语义 = AST 语义
	var outIRI bytes.Buffer
	if err := irinterp.New(&outIRI).Run(a.Prog); err != nil {
		res.Note = "IR 解释器执行失败: " + err.Error()
		return res
	}

	// 2) →Go 产物（go build + 执行）
	outGo, err := execBackend(c, "go", func(dir string) (string, string, error) {
		src, err := gen.Transpile(a.Prog)
		if err != nil {
			return "", "", err
		}

		if err := os.WriteFile(filepath.Join(dir, "main.go"), []byte(src), 0o644); err != nil {
			return "", "", err
		}

		if err := tool(dir, 30*time.Second, "go", "build", "-o", "prog.exe", "main.go"); err != nil {
			return "", "", fmt.Errorf("go build: %w", err)
		}

		return runCapture(dir, filepath.Join(dir, "prog.exe"))
	})
	if backendFail(&res, "go", err) {
		return res
	}

	// 3) →JS 产物（node ESM）
	outJS, err := execBackend(c, "node", func(dir string) (string, string, error) {
		src, err := genjs.TranspileJS(a.Prog)
		if err != nil {
			return "", "", err
		}

		if err := os.WriteFile(filepath.Join(dir, "out.mjs"), []byte(src), 0o644); err != nil {
			return "", "", err
		}

		return runCapture(dir, "node", filepath.Join(dir, "out.mjs"))
	})
	if backendFail(&res, "node", err) {
		return res
	}

	// 一致性断言：三方 stdout 逐字节一致且等于期望
	for name, got := range map[string]string{
		"解释器": outInterp.String(),
		"→Go": outGo,
		"→JS": outJS,
	} {
		if got != c.Expect.Stdout {
			res.Note = fmt.Sprintf("%s stdout 不一致\n期望: %q\n实际: %q", name, c.Expect.Stdout, got)
			return res
		}
	}

	res.OK = true

	return res
}

// execBackend 在临时目录执行后端产物；skipped=true 表示宿主缺工具链（环境性跳过）.
func execBackend(c *Case, toolName string, fn func(dir string) (string, string, error)) (string, error) {
	if _, err := exec.LookPath(toolName); err != nil {
		return "", errSkipped(fmt.Sprintf("宿主缺 %s，差分跳过（不计入通过）", toolName))
	}

	dir, err := os.MkdirTemp("", "conformance-"+c.Name)
	if err != nil {
		return "", err
	}
	defer os.RemoveAll(dir)

	stdout, stderr, err := fn(dir)
	if err != nil {
		return "", fmt.Errorf("%s: %w", toolName, err)
	}

	if stderr != "" {
		return "", fmt.Errorf("%s stderr 非空: %s", toolName, stderr)
	}

	return stdout, nil
}

// errSkipped 环境性跳过（非 case 失败）.
type errSkipped string

func (e errSkipped) Error() string { return string(e) }

// backendFail 把后端失败写进结果；返回 true 表示应终止.
func backendFail(res *CaseResult, toolName string, err error) bool {
	if err == nil {
		return false
	}

	res.Note = err.Error()
	if _, ok := errors.AsType[errSkipped](err); ok {
		res.Skipped = true
	}

	return true
}

// runDiagnostic check 模式：诊断断言（code + severity + line 多重集）.
func runDiagnostic(c *Case, prog []syntax.Stmt, res CaseResult) CaseResult {
	diags, _ := pipeline.CheckStmts(prog)

	got := map[string]int{}
	for _, d := range diags {
		got[d.Code+"|"+contract.RuleOf(d.Code)+"|error|"+strconv.Itoa(d.Line)]++
	}
	// 期望声明了 ruleId 则精确比对；未声明（v0.8 旧 case）回填映射表值参与匹配
	want := map[string]int{}

	for _, d := range c.Expect.Diagnostics {
		rule := d.RuleID
		if rule == "" {
			rule = contract.RuleOf(d.Code)
		}

		want[d.Code+"|"+rule+"|"+d.Severity+"|"+strconv.Itoa(d.Line)]++
	}

	var missing, extra []string

	for k, n := range want {
		if got[k] < n {
			missing = append(missing, fmt.Sprintf("%s×%d", k, n-got[k]))
		}
	}

	for k, n := range got {
		if want[k] < n {
			extra = append(extra, fmt.Sprintf("%s×%d", k, n-want[k]))
		}
	}

	if len(missing) > 0 || len(extra) > 0 {
		sort.Strings(missing)
		sort.Strings(extra)
		res.Note = fmt.Sprintf("诊断不匹配 缺%s 多%s", strings.Join(missing, ","), strings.Join(extra, ","))

		return res
	}

	res.OK = true

	return res
}

// RunSuite 遍历 root 下 <category>/<case>/ 全部 case（无 case.yaml 的目录视为套件错误）.
func RunSuite(root string) ([]CaseResult, int, error) {
	cats, err := os.ReadDir(root)
	if err != nil {
		return nil, 0, err
	}

	var all []CaseResult

	for _, cat := range cats {
		if !cat.IsDir() {
			continue
		}

		cases, err := os.ReadDir(filepath.Join(root, cat.Name()))
		if err != nil {
			return nil, 0, err
		}

		for _, cd := range cases {
			if !cd.IsDir() {
				continue
			}

			dir := filepath.Join(root, cat.Name(), cd.Name())
			if _, err := os.Stat(filepath.Join(dir, "case.yaml")); err != nil {
				return nil, 0, fmt.Errorf("套件目录 %s 缺 case.yaml", dir)
			}

			r := RunCase(dir)
			if r.Name == "" {
				r.Name = cd.Name()
			}

			all = append(all, r)
		}
	}

	fails := 0

	for _, r := range all {
		if !r.OK {
			fails++
		}
	}

	return all, fails, nil
}

// tool 在 dir 内执行命令（合并 stderr，超时保护）.
func tool(dir string, timeout time.Duration, name string, args ...string) error {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()

	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = dir

	bs, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("%w: %s", err, strings.TrimSpace(string(bs)))
	}

	return nil
}

// runCapture 执行命令并分别捕获 stdout/stderr（exitCode 非 0 视为失败）.
func runCapture(dir, name string, args ...string) (string, string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = dir

	var out, errBuf bytes.Buffer

	cmd.Stdout = &out

	cmd.Stderr = &errBuf
	if err := cmd.Run(); err != nil {
		return "", "", fmt.Errorf("%w: %s", err, strings.TrimSpace(errBuf.String()))
	}

	return out.String(), errBuf.String(), nil
}
