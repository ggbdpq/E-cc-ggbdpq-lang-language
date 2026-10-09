package contract

// check.go：check --json 负载（诊断稳定码 + 行级 Span）

import (
	"lang-go/internal/pipeline"
	"lang-go/internal/syntax"
)

// Check 源码检查 → 契约负载.
func Check(src string) CheckJSON {
	diags, _ := pipeline.Check(src)
	return diagsJSON(diags)
}

// CheckStmts 已解析语句序列检查（装载器合并程序）.
func CheckStmts(prog []syntax.Stmt) CheckJSON {
	diags, _ := pipeline.CheckStmts(prog)
	return diagsJSON(diags)
}

func diagsJSON(diags []pipeline.Diagnostic) CheckJSON {
	out := CheckJSON{
		SchemaVersion: SchemaVersion,
		ToolVersion:   ToolVersion,
		Kind:          KindCheck,
		Success:       len(diags) == 0,
		Diagnostics:   make([]Diagnostic, 0, len(diags)),
	}
	for _, d := range diags {
		out.Diagnostics = append(out.Diagnostics, Diagnostic{
			Code:         d.Code,
			RuleID:       RuleOf(d.Code),
			Severity:     "error",
			Message:      d.Msg,
			PrimarySpan:  lineSpan(d.Line),
			RelatedSpans: []*Span{},
			Help:         "",
		})
	}

	return out
}

// lineSpan 行级定位（v0.4：诊断到行，列恒为 1；文档化于 specs/source-span.md）.
func lineSpan(line int) *Span {
	if line < 1 {
		line = 0
	}

	return &Span{
		Start: Position{Line: line, Column: 1},
		End:   Position{Line: line, Column: 1},
	}
}
