package pipeline

// Package pipeline 串起 v0.3 前端管线：parse → resolve → typecheck → HIR → IR。
// CLI（check/build）与测试共用；诊断带稳定码（E1xxx 解析 / E2xxx 解析名 /
// E3xxx-E4xxx 类型与模式）。

import (
	"fmt"
	"strings"

	"lang-go/internal/hir"
	"lang-go/internal/ir"
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
)

// Diagnostic 统一诊断形态
type Diagnostic struct {
	Code string
	Msg  string
	Line int
}

func (d Diagnostic) Error() string {
	return fmt.Sprintf("第%d行: [%s] %s", d.Line, d.Code, d.Msg)
}

// ErrorList 多诊断聚合（首个诊断即 Error() 文本）
type ErrorList []Diagnostic

func (l ErrorList) Error() string {
	if len(l) == 0 {
		return "无诊断"
	}
	return l[0].Error()
}

// Diagnostics 将聚合转为切片
func Diagnostics(err error) []Diagnostic {
	if l, ok := err.(ErrorList); ok {
		return l
	}
	return nil
}

// Parse 解析（独立导出供 dump ast 用）
func Parse(src string) ([]syntax.Stmt, error) {
	prog, err := syntax.Parse(src)
	if err != nil {
		return nil, ErrorList{parseDiag(err)}
	}
	return prog, nil
}

func parseDiag(err error) Diagnostic {
	if pe, ok := err.(*syntax.Error); ok {
		return Diagnostic{Code: "E1001", Msg: pe.Msg, Line: pe.Line}
	}
	// 词法错误（syntax 内部类型）：取整段文本
	return Diagnostic{Code: "E1001", Msg: err.Error(), Line: 0}
}

// Check 前端检查：parse → resolve → typecheck。合法返回 nil 诊断。
func Check(src string) ([]Diagnostic, error) {
	prog, err := syntax.Parse(src)
	if err != nil {
		return []Diagnostic{parseDiag(err)}, ErrorList{parseDiag(err)}
	}
	res := semantic.Resolve(prog)
	if res.HasErrors() {
		diags := convertSemantic(res.Diags)
		return diags, ErrorList(diags)
	}
	ty := types.Check(prog, res)
	if ty.HasErrors() {
		diags := convertTypes(ty.Diags)
		return diags, ErrorList(diags)
	}
	return nil, nil
}

// Analyzed 全前端产物（HIR/IR/符号/类型），契约层一次性取用
type Analyzed struct {
	AST  []syntax.Stmt
	Res  *semantic.Result
	Ty   *types.Result
	HIR  *hir.Prog
	Prog *ir.Prog
}

// Analyze 源码 → 全前端产物
func Analyze(src string) (*Analyzed, error) {
	prog, err := syntax.Parse(src)
	if err != nil {
		d := parseDiag(err)
		return nil, ErrorList{d}
	}
	return AnalyzeStmts(prog)
}

// AnalyzeStmts 已解析语句 → 全前端产物
func AnalyzeStmts(prog []syntax.Stmt) (*Analyzed, error) {
	res := semantic.Resolve(prog)
	if res.HasErrors() {
		return nil, ErrorList(convertSemantic(res.Diags))
	}
	ty := types.Check(prog, res)
	if ty.HasErrors() {
		return nil, ErrorList(convertTypes(ty.Diags))
	}
	h := hir.Build(prog, res, ty)
	return &Analyzed{AST: prog, Res: res, Ty: ty, HIR: h, Prog: ir.Lower(h)}, nil
}

// Compile 源码 → IR（检查全过才降级）
func Compile(src string) (*ir.Prog, error) {
	a, err := Analyze(src)
	if err != nil {
		return nil, err
	}
	return a.Prog, nil
}

// CheckStmts 前端检查（已解析语句序列）
func CheckStmts(prog []syntax.Stmt) ([]Diagnostic, error) {
	res := semantic.Resolve(prog)
	if res.HasErrors() {
		diags := convertSemantic(res.Diags)
		return diags, ErrorList(diags)
	}
	ty := types.Check(prog, res)
	if ty.HasErrors() {
		diags := convertTypes(ty.Diags)
		return diags, ErrorList(diags)
	}
	return nil, nil
}

// CompileStmts 已解析语句序列 → IR（装载器合并后的程序用）
func CompileStmts(prog []syntax.Stmt) (*ir.Prog, error) {
	a, err := AnalyzeStmts(prog)
	if err != nil {
		return nil, err
	}
	return a.Prog, nil
}

func convertSemantic(list []semantic.Diagnostic) []Diagnostic {
	out := make([]Diagnostic, 0, len(list))
	for _, d := range list {
		out = append(out, Diagnostic{Code: d.Code, Msg: d.Msg, Line: d.Line})
	}
	return out
}

func convertTypes(list []types.Diagnostic) []Diagnostic {
	out := make([]Diagnostic, 0, len(list))
	for _, d := range list {
		out = append(out, Diagnostic{Code: d.Code, Msg: d.Msg, Line: d.Line})
	}
	return out
}

// FormatDiags 多行渲染
func FormatDiags(diags []Diagnostic) string {
	var b strings.Builder
	for _, d := range diags {
		b.WriteString(d.Error())
		b.WriteString("\n")
	}
	return strings.TrimRight(b.String(), "\n")
}
