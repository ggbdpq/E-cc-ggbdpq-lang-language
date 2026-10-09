package contract

// Package contract 是 Agent Contract 契约层（v0.4）：
// 编译器内部模型 → 稳定 JSON 协议的唯一转换边界。
//
// 规则：
//   - 本包禁止 import gen/genjs/interp（后端与解释器不是契约数据源）；
//   - 输出必须 deterministic（数组显式排序，禁用 map 迭代序）；
//   - schemaVersion "1" 内字段只加不改不删（specs/agent-protocol.md）。

import (
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
	"lang-go/internal/version"
)

// SchemaVersion 契约版本（破坏性变更才升位）.
const SchemaVersion = "1"

// ToolVersion 工具版本（与 CLI version 同源）.
const ToolVersion = version.Version // 唯一版本源（specs/versioning.md §1）

// Kind 输出类别.
const (
	KindInspectSymbols      = "inspect.symbols"
	KindInspectRefs         = "inspect.refs"
	KindInspectType         = "inspect.type"
	KindInspectDependencies = "inspect.dependencies"
	KindInspectEffects      = "inspect.effects"
	KindInspectCallers      = "inspect.callers"
	KindInspectCallees      = "inspect.callees"
	KindInspectImpact       = "inspect.impact"
	KindInspectDependency   = "inspect.dependency"
	KindDumpAST             = "dump.ast"
	KindDumpHIR             = "dump.hir"
	KindDumpIR              = "dump.ir"
	KindCheck               = "check"
)

// Position JSON 位置：offset 0 基 rune、line/column 1 基.
type Position struct {
	Offset int `json:"offset"`
	Line   int `json:"line"`
	Column int `json:"column"`
}

// Span JSON 源码区间.
type Span struct {
	File  string   `json:"file"`
	Start Position `json:"start"`
	End   Position `json:"end"`
}

// SpanJSON syntax.Span → 契约 Span.
func SpanJSON(sp syntax.Span) *Span {
	if sp.File == "" && sp.Start.Offset == 0 && sp.End.Offset == 0 {
		return nil // 未盖章节点不输出
	}

	return &Span{
		File:  sp.File,
		Start: Position{Offset: sp.Start.Offset, Line: sp.Start.Line, Column: sp.Start.Col},
		End:   Position{Offset: sp.End.Offset, Line: sp.End.Line, Column: sp.End.Col},
	}
}

// Diagnostic JSON 诊断（码稳定；severity v0.4 恒为 error；ruleId v0.9 起携带）.
type Diagnostic struct {
	Code         string  `json:"code"`
	RuleID       string  `json:"ruleId,omitempty"`
	Severity     string  `json:"severity"`
	Message      string  `json:"message"`
	PrimarySpan  *Span   `json:"primarySpan"`
	RelatedSpans []*Span `json:"relatedSpans"`
	Help         string  `json:"help"`
}

// Symbol JSON 符号.
type Symbol struct {
	SymbolID   int    `json:"symbolId"`
	Name       string `json:"name"`
	Kind       string `json:"kind"`
	Definition *Span  `json:"definition"`
	Type       string `json:"type"`
	Visibility string `json:"visibility"` // public | local
	Module     string `json:"module"`
}

// Reference JSON 引用.
type Reference struct {
	SymbolID      int    `json:"symbolId"`
	Span          *Span  `json:"span"`
	ReferenceKind string `json:"referenceKind"` // read | write | call | pattern
}

// Dependency 模块依赖（文件粒度）.
type Dependency struct {
	Module  string   `json:"module"`
	Imports []string `json:"imports"`
}

// Node dump 输出的统一节点（AST/HIR/IR 共用形态）.
type Node struct {
	NodeID   int     `json:"nodeId"`
	Kind     string  `json:"kind"`
	Span     *Span   `json:"span,omitempty"`
	Type     string  `json:"type,omitempty"`
	Name     string  `json:"name,omitempty"`
	Value    string  `json:"value,omitempty"`
	Children []*Node `json:"children,omitempty"`
}

// Envelope 所有机器输出的统一顶层.
type Envelope struct {
	SchemaVersion string           `json:"schemaVersion"`
	ToolVersion   string           `json:"toolVersion"`
	Kind          string           `json:"kind"`
	Success       *bool            `json:"success,omitempty"`
	Error         string           `json:"error,omitempty"`
	Diagnostics   []Diagnostic     `json:"diagnostics,omitempty"`
	Symbols       []Symbol         `json:"symbols,omitempty"`
	References    []Reference      `json:"references,omitempty"`
	Dependencies  []Dependency     `json:"dependencies,omitempty"`
	Effects       []Effect         `json:"effects,omitempty"`
	Nodes         []*Node          `json:"nodes,omitempty"`
	Callers       []ImpactItemJSON `json:"callers,omitempty"`
	Callees       []ImpactItemJSON `json:"callees,omitempty"`
	Impact        *ImpactJSON      `json:"impact,omitempty"`
	Dependency    *FileDepJSON     `json:"dependency,omitempty"`
}

// ErrorEnvelope 结构化错误输出.
func ErrorEnvelope(kind, msg string) Envelope {
	return Envelope{SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: kind, Error: msg}
}

// CheckJSON check 的负载.
type CheckJSON struct {
	SchemaVersion string       `json:"schemaVersion"`
	ToolVersion   string       `json:"toolVersion"`
	Kind          string       `json:"kind"`
	Success       bool         `json:"success"`
	Diagnostics   []Diagnostic `json:"diagnostics"`
}

// —— 转换助手 ——

// diagJSON semantic 诊断 → 契约诊断（行级 Span：整行区间）.
func diagJSON(d semantic.Diagnostic) Diagnostic {
	span := Span{File: "", Start: Position{Offset: 0, Line: d.Line, Column: 1}, End: Position{Offset: 0, Line: d.Line, Column: 1}}

	return Diagnostic{
		Code:         d.Code,
		RuleID:       RuleOf(d.Code),
		Severity:     "error",
		Message:      d.Msg,
		PrimarySpan:  &span,
		RelatedSpans: []*Span{},
	}
}

// typesDiagJSON types 诊断 → 契约诊断.
func typesDiagJSON(d types.Diagnostic) Diagnostic {
	span := Span{Start: Position{Offset: 0, Line: d.Line, Column: 1}, End: Position{Offset: 0, Line: d.Line, Column: 1}}

	return Diagnostic{
		Code:         d.Code,
		RuleID:       RuleOf(d.Code),
		Severity:     "error",
		Message:      d.Msg,
		PrimarySpan:  &span,
		RelatedSpans: []*Span{},
	}
}
