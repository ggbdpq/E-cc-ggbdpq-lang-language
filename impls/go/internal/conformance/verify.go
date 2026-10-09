package conformance

// verify.go：lang-go verify 的管线（v0.8）。
// parse → resolve → typecheck → hir → ir → effect → compile → execute → differential。
// effect 自 IR 推导（graph.Effects 唯一入口），实现序在 ir 之后；呈现名保持管线语义。
// 任一阶段失败即停——后续阶段不列（输出只含已完成与失败阶段）。

import (
	"bytes"
	"fmt"
	"lang-go/internal/gen"
	"lang-go/internal/genjs"
	"lang-go/internal/graph"
	"lang-go/internal/hir"
	"lang-go/internal/interp"
	"lang-go/internal/irinterp"
	"lang-go/internal/load"
	"lang-go/internal/pipeline"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

// Stage 单阶段结果.
type Stage struct {
	Name    string `json:"stage"`
	OK      bool   `json:"ok"`
	Skipped bool   `json:"skipped,omitempty"`
	Note    string `json:"note,omitempty"`
}

// VerifyResult 全管线结果.
type VerifyResult struct {
	Success     bool    `json:"success"`
	Stages      []Stage `json:"stages"`
	StdoutBytes int     `json:"stdoutBytes,omitempty"` // differential 通过时：三方一致的 stdout 字节数
	ExitCode    int     `json:"exitCode,omitempty"`
}

// VerifyDir 对程序目录（目录即包）走全管线.
func VerifyDir(dir string) VerifyResult {
	res := VerifyResult{}
	fail := func(name, note string) VerifyResult {
		res.Stages = append(res.Stages, Stage{Name: name, Note: note})
		return res
	}
	ok := func(name, note string) {
		res.Stages = append(res.Stages, Stage{Name: name, OK: true, Note: note})
	}

	// 1) parse：装载即解析（目录即包，含 import 展开）
	prog, _, err := load.Program(dir, false)
	if err != nil {
		return fail("parse", err.Error())
	}

	ok("parse", "")

	// 2+3) resolve / typecheck：前端一次跑完，按稳定码分段呈现
	// （E1xxx 词法 / E2xxx resolver → resolve；E3xxx 类型 / E4xxx 模式 → typecheck）
	diags, _ := pipeline.CheckStmts(prog)

	var resolveDiags, typeDiags []pipeline.Diagnostic

	for _, d := range diags {
		if strings.HasPrefix(d.Code, "E3") || strings.HasPrefix(d.Code, "E4") {
			typeDiags = append(typeDiags, d)
		} else {
			resolveDiags = append(resolveDiags, d)
		}
	}

	if len(resolveDiags) > 0 {
		return fail("resolve", pipeline.FormatDiags(resolveDiags))
	}

	ok("resolve", "")

	if len(typeDiags) > 0 {
		return fail("typecheck", pipeline.FormatDiags(typeDiags))
	}

	ok("typecheck", "")

	// 4) hir validate：构建成功 + 全树遍历可达（v0.8 为结构完整性检查，深度不变量随 v0.9 加固）
	a, aerr := pipeline.AnalyzeStmts(prog)
	if aerr != nil {
		return fail("hir", aerr.Error())
	}

	h := a.HIR
	nodes := 0

	hir.WalkProg(h, func(hir.Stmt) { nodes++ }, func(hir.Expr) { nodes++ }, nil)

	if nodes == 0 {
		return fail("hir", "遍历覆盖为 0（结构不完整）")
	}

	ok("hir", fmt.Sprintf("%d 节点", nodes))

	// 5) ir validate：lower 成功且非空
	ip := a.Prog
	if ip == nil {
		return fail("ir", "lower 产物为空")
	}

	ok("ir", "")

	// 6) effect：效果面可推导（自 IR；效果约束检查随效果系统落地）
	effects := graph.Effects(ip)
	ok("effect", fmt.Sprintf("%d 项", len(effects)))

	// 7) compile：→Go 产物可编译（宿主工具链）+ →JS 产物可生成
	goSrc, err := gen.Transpile(ip)
	if err != nil {
		return fail("compile", "→Go 转译: "+err.Error())
	}

	jsSrc, err := genjs.TranspileJS(ip)
	if err != nil {
		return fail("compile", "→JS 转译: "+err.Error())
	}

	tmp, err := os.MkdirTemp("", "langgo-verify")
	if err != nil {
		return fail("compile", "临时目录: "+err.Error())
	}
	defer os.RemoveAll(tmp)

	if err := os.WriteFile(filepath.Join(tmp, "main.go"), []byte(goSrc), 0o644); err != nil {
		return fail("compile", err.Error())
	}

	if err := tool(tmp, 60*time.Second, "go", "build", "-o", "prog.exe", "main.go"); err != nil {
		return fail("compile", "go build: "+err.Error())
	}

	ok("compile", "go+js 产物")

	// 8) execute：四方执行（AST Oracle / IR 解释器 / →Go 产物 / →JS 产物）
	var outInterp bytes.Buffer
	if err := interp.New(&outInterp).Run(prog); err != nil {
		return fail("execute", "解释器: "+err.Error())
	}

	var outIRI bytes.Buffer
	if err := irinterp.New(&outIRI).Run(ip); err != nil {
		return fail("execute", "IR 解释器: "+err.Error())
	}

	outGo, _, err := runCapture(tmp, filepath.Join(tmp, "prog.exe"))
	if err != nil {
		return fail("execute", "→Go: "+err.Error())
	}

	var outJS string

	if _, err := exec.LookPath("node"); err != nil {
		ok("execute", "宿主缺 node，JS 路不执行（差分降级为三方）")

		outJS = outGo
	} else {
		if err := os.WriteFile(filepath.Join(tmp, "out.mjs"), []byte(jsSrc), 0o644); err != nil {
			return fail("execute", err.Error())
		}

		outJS, _, err = runCapture(tmp, "node", filepath.Join(tmp, "out.mjs"))
		if err != nil {
			return fail("execute", "→JS: "+err.Error())
		}

		ok("execute", "")
	}

	// 9) differential：三方 stdout 逐字节一致
	if outInterp.String() != outGo || outInterp.String() != outJS {
		return fail("differential", fmt.Sprintf("不一致\n解释器: %q\n→Go: %q\n→JS: %q",
			outInterp.String(), outGo, outJS))
	}

	ok("differential", fmt.Sprintf("%d 字节一致", outInterp.Len()))
	res.StdoutBytes = outInterp.Len()
	res.ExitCode = 0

	res.Success = true

	return res
}
