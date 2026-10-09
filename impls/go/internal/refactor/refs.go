package refactor

// refs.go：HIR 方法调用点的名字编辑收集。
// 方法调用点（HIR MethodCall）遍历属于 hir 包的职责，此处从每个函数的 Body 出发递归收集。

import (
	"lang-go/internal/edit"
	"lang-go/internal/hir"
	"lang-go/internal/semantic"
)

// collectHIRMethodRefs 收集 HIR 语句中指向 target 的方法调用编辑（newName 为替换文本）.
func collectHIRMethodRefs(st hir.Stmt, res *semantic.Result, target *semantic.Symbol, newName string, edits *[]edit.TextEdit) {
	switch x := st.(type) {
	case *hir.Let:
		collectHIREdit(x.Init, res, target, newName, edits)
	case *hir.Assign:
		collectHIREdit(x.Target, res, target, newName, edits)
		collectHIREdit(x.RHS, res, target, newName, edits)
	case *hir.Eval:
		collectHIREdit(x.X, res, target, newName, edits)
	case *hir.Return:
		if x.Val != nil {
			collectHIREdit(x.Val, res, target, newName, edits)
		}
	case *hir.If:
		collectHIREdit(x.Cond, res, target, newName, edits)
		for _, s2 := range x.Then {
			collectHIRMethodRefs(s2, res, target, newName, edits)
		}
		for _, s2 := range x.Else {
			collectHIRMethodRefs(s2, res, target, newName, edits)
		}
	case *hir.While:
		collectHIREdit(x.Cond, res, target, newName, edits)
		for _, s2 := range x.Body {
			collectHIRMethodRefs(s2, res, target, newName, edits)
		}
	case *hir.ForIn:
		collectHIREdit(x.Iter, res, target, newName, edits)
		for _, s2 := range x.Body {
			collectHIRMethodRefs(s2, res, target, newName, edits)
		}
	case *hir.Spawn:
		for _, a := range x.Args {
			collectHIREdit(a, res, target, newName, edits)
		}
	case *hir.FnStmt:
		for _, s2 := range x.Fn.Body {
			collectHIRMethodRefs(s2, res, target, newName, edits)
		}
	}
}

// collectHIREdit 递归收集 HIR 表达式中的方法调用编辑.
func collectHIREdit(e hir.Expr, res *semantic.Result, target *semantic.Symbol, newName string, edits *[]edit.TextEdit) {
	switch x := e.(type) {
	case *hir.MethodCall:
		if x.Method != nil && x.Method.ID == target.ID {
			*edits = append(*edits, edit.TextEdit{
				File:        x.NameSp.File,
				StartOffset: x.NameSp.Start.Offset,
				EndOffset:   x.NameSp.End.Offset,
				Replacement: newName,
			})
		}
		collectHIREdit(x.Recv, res, target, newName, edits)
		for _, a := range x.Args {
			collectHIREdit(a, res, target, newName, edits)
		}
	case *hir.Call:
		if x.Callee != nil {
			collectHIREdit(x.Callee, res, target, newName, edits)
		}
		for _, a := range x.Args {
			collectHIREdit(a, res, target, newName, edits)
		}
	case *hir.Ctor:
		for _, a := range x.Args {
			collectHIREdit(a, res, target, newName, edits)
		}
	case *hir.Unary:
		collectHIREdit(x.X, res, target, newName, edits)
	case *hir.Binary:
		collectHIREdit(x.L, res, target, newName, edits)
		collectHIREdit(x.R, res, target, newName, edits)
	case *hir.AndThen:
		collectHIREdit(x.L, res, target, newName, edits)
		collectHIREdit(x.R, res, target, newName, edits)
	case *hir.OrElse:
		collectHIREdit(x.L, res, target, newName, edits)
		collectHIREdit(x.R, res, target, newName, edits)
	case *hir.Range:
		collectHIREdit(x.Lo, res, target, newName, edits)
		collectHIREdit(x.Hi, res, target, newName, edits)
	case *hir.Index:
		collectHIREdit(x.Obj, res, target, newName, edits)
		collectHIREdit(x.Idx, res, target, newName, edits)
	case *hir.Slice:
		collectHIREdit(x.Obj, res, target, newName, edits)
		collectHIREdit(x.Lo, res, target, newName, edits)
		collectHIREdit(x.Hi, res, target, newName, edits)
	case *hir.Field:
		collectHIREdit(x.Obj, res, target, newName, edits)
	case *hir.Try:
		collectHIREdit(x.X, res, target, newName, edits)
	case *hir.StructLit:
		for _, f := range x.Fields {
			collectHIREdit(f.Val, res, target, newName, edits)
		}
	case *hir.MapLit:
		for i := range x.Keys {
			collectHIREdit(x.Keys[i], res, target, newName, edits)
			collectHIREdit(x.Vals[i], res, target, newName, edits)
		}
	case *hir.SliceLit:
		for _, el := range x.Elems {
			collectHIREdit(el, res, target, newName, edits)
		}
	case *hir.Match:
		collectHIREdit(x.Scrut, res, target, newName, edits)
		for _, arm := range x.Arms {
			collectHIREdit(arm.Body, res, target, newName, edits)
		}
	case *hir.FnLit:
		for _, st := range x.Body {
			collectHIRMethodRefs(st, res, target, newName, edits)
		}
	}
}
