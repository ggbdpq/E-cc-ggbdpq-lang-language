package refactor

// remove.go：safe remove——仅当 Semantic Graph 证明目标无可达引用时才允许删除。

import (
	"errors"
	"fmt"

	"lang-go/internal/edit"
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
)

// RemoveCheck safe remove 的可行性检查结果.
type RemoveCheck struct {
	Removable bool
	Reason    string
	RefCount  int
	RefSites  []string // 引用位置描述
}

// SafeRemoveCheck 顶层 fn / 全局变量的删除可行性检查.
func SafeRemoveCheck(prog []syntax.Stmt, res *semantic.Result, target *semantic.Symbol) (RemoveCheck, error) {
	if target.Kind != semantic.KFn && target.Kind != semantic.KVar {
		return RemoveCheck{}, fmt.Errorf("safe remove 仅支持顶层 fn 与全局变量（目标 kind=%s）", kindName(target.Kind))
	}

	if target.Extern {
		return RemoveCheck{}, errors.New("extern 声明不支持 safe remove")
	}

	// 声明节点必须可定位（fn / 全局 let）
	var found bool

	switch target.Decl.(type) {
	case *syntax.FnDecl, *syntax.LetStmt:
		found = true
	}

	if !found {
		return RemoveCheck{}, errors.New("未找到声明节点")
	}

	// 引用计数：除定义外的全部 Ident 引用
	refCount := 0

	var sites []string
	for _, st := range prog {
		collectRemoveRefs(st, res, target, &refCount, &sites)
	}

	return RemoveCheck{
		Removable: refCount == 0,
		Reason:    fmt.Sprintf("%d 处引用", refCount),
		RefCount:  refCount,
		RefSites:  sites,
	}, nil
}

func collectRemoveRefs(st syntax.Stmt, res *semantic.Result, target *semantic.Symbol, count *int, sites *[]string) {
	switch x := st.(type) {
	case *syntax.LetStmt:
		walkRemoveExpr(x.Init, res, target, count, sites)
	case *syntax.AssignStmt:
		walkRemoveExpr(x.Target, res, target, count, sites)
		walkRemoveExpr(x.Val, res, target, count, sites)
	case *syntax.ExprStmt:
		walkRemoveExpr(x.X, res, target, count, sites)
	case *syntax.ReturnStmt:
		if x.Val != nil {
			walkRemoveExpr(x.Val, res, target, count, sites)
		}
	case *syntax.IfStmt:
		walkRemoveExpr(x.Cond, res, target, count, sites)

		for _, s := range x.Then {
			collectRemoveRefs(s, res, target, count, sites)
		}

		for _, s := range x.Else {
			collectRemoveRefs(s, res, target, count, sites)
		}
	case *syntax.WhileStmt:
		walkRemoveExpr(x.Cond, res, target, count, sites)

		for _, s := range x.Body {
			collectRemoveRefs(s, res, target, count, sites)
		}
	case *syntax.ForInStmt:
		walkRemoveExpr(x.Iter, res, target, count, sites)

		for _, s := range x.Body {
			collectRemoveRefs(s, res, target, count, sites)
		}
	case *syntax.FnDecl:
		for _, s := range x.Body {
			collectRemoveRefs(s, res, target, count, sites)
		}
	case *syntax.GoStmt:
		for _, a := range x.Call.Args {
			walkRemoveExpr(a, res, target, count, sites)
		}
	}
}

func walkRemoveExpr(e syntax.Expr, res *semantic.Result, target *semantic.Symbol, count *int, sites *[]string) {
	syntax.WalkExpr(e, nil, func(x syntax.Expr) {
		if id, ok := x.(*syntax.Ident); ok {
			if sym := res.Refs[id]; sym != nil && sym.ID == target.ID {
				*count++

				*sites = append(*sites, formatRefSite(id.Sp))
			}
		}
	}, nil)
}

// SafeRemoveSpan 可删除时返回声明节点的完整删除区间.
func SafeRemoveSpan(prog []syntax.Stmt, res *semantic.Result, target *semantic.Symbol) (syntax.Span, error) {
	switch target.Kind {
	case semantic.KFn:
		d, ok := target.Decl.(*syntax.FnDecl)
		if !ok {
			return syntax.Span{}, errors.New("未找到函数声明节点")
		}

		return d.Sp, nil
	case semantic.KVar:
		st := findLetStmt(prog, res, target)
		if st == nil {
			return syntax.Span{}, errors.New("未找到绑定声明")
		}

		return st.Sp, nil
	}

	return syntax.Span{}, errors.New("不支持的定义定位")
}

// SafeRemoveEdits 可删除时给出删除编辑：区间吞掉声明后的单个换行，避免残留空行.
func SafeRemoveEdits(prog []syntax.Stmt, srcs Sources, res *semantic.Result, target *semantic.Symbol) ([]edit.TextEdit, error) {
	span, err := SafeRemoveSpan(prog, res, target)
	if err != nil {
		return nil, err
	}

	end := span.End.Offset
	if r := []rune(srcs.Source(span.File)); end < len(r) && r[end] == '\n' {
		end++
	}

	return []edit.TextEdit{{
		File:        span.File,
		StartOffset: span.Start.Offset,
		EndOffset:   end,
		Replacement: "",
	}}, nil
}

func formatRefSite(sp syntax.Span) string {
	if sp.File == "" {
		return fmt.Sprintf("%d:%d", sp.Start.Line, sp.Start.Col)
	}

	return fmt.Sprintf("%s:%d:%d", sp.File, sp.Start.Line, sp.Start.Col)
}
