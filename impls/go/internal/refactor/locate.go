package refactor

// locate.go：符号定义「名字 token」与 HIR 方法调用点的区间定位。
// 全部基于 token 扫描（非正则）：声明 Span 内的记号流按词法类别匹配。

import (
	"errors"
	"fmt"

	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
)

// Sources 声明所在文件的源码供给：多文件包的 span 偏移是文件内的，
// token 扫描必须按 Sp.File 取对应源码；单文件场景（File 为空）用 Default。
type Sources struct {
	Default string            // 单文件场景源码（Sp.File 为空时使用）
	ByFile  map[string]string // loader 相对路径 → 源码
}

// of span 所属文件的源码.
func (s Sources) Source(file string) string {
	if file == "" {
		return s.Default
	}

	if c, ok := s.ByFile[file]; ok {
		return c
	}

	return s.Default
}

// declInfo 函数声明 + 符号.
type declInfo struct {
	Decl *syntax.FnDecl
	Sym  *semantic.Symbol
}

// collectFnDecls 收集全部函数声明（顶层/嵌套/方法）.
func collectFnDecls(prog []syntax.Stmt, res *semantic.Result) []declInfo {
	var out []declInfo

	seen := map[syntax.Stmt]bool{}

	var walkS func(s syntax.Stmt)

	walkS = func(s syntax.Stmt) {
		if seen[s] {
			return
		}

		seen[s] = true
		if d, ok := s.(*syntax.FnDecl); ok {
			if sym, ok := res.DeclSyms[d]; ok {
				out = append(out, declInfo{d, sym})
			}
		}

		syntax.WalkStmt(s, walkS, nil, nil)
	}
	syntax.WalkStmts(prog, walkS, nil, nil)

	return out
}

// declNameSpan 定位符号声明中「名字 token」的绝对区间.
func declNameSpan(prog []syntax.Stmt, srcs Sources, res *semantic.Result, target *semantic.Symbol) (syntax.Span, error) {
	switch target.Kind {
	case semantic.KFn:
		d, ok := target.Decl.(*syntax.FnDecl)
		if !ok {
			return syntax.Span{}, errors.New("未找到函数声明节点")
		}

		return scanNameAfterFn(d.Sp, srcs, false)
	case semantic.KMethod:
		d, ok := target.Decl.(*syntax.FnDecl)
		if !ok {
			return syntax.Span{}, errors.New("未找到方法声明节点")
		}

		return scanNameAfterFn(d.Sp, srcs, true)
	case semantic.KVar:
		st := findLetStmt(prog, res, target)
		if st == nil {
			return syntax.Span{}, errors.New("未找到绑定声明")
		}

		return firstIdentSpan(st.Sp, srcs)
	case semantic.KParam:
		ownerSym, idx, ok := paramOwner(res, target)
		if !ok {
			return syntax.Span{}, errors.New("未找到参数所属函数")
		}

		d := findFnDeclBySym(prog, res, ownerSym)
		if d == nil {
			return syntax.Span{}, errors.New("未找到函数声明节点")
		}

		hasRecv := d.Recv != nil
		group := 0
		seg := idx

		if hasRecv {
			group = 1
			seg = idx
		}

		return paramIdentSpan(d.Sp, srcs, group, seg)
	}

	return syntax.Span{}, errors.New("不支持的定义定位")
}

// findLetStmt 按符号找 LetStmt.
func findLetStmt(prog []syntax.Stmt, res *semantic.Result, target *semantic.Symbol) *syntax.LetStmt {
	var found *syntax.LetStmt

	walkS := func(s syntax.Stmt) {
		if st, ok := s.(*syntax.LetStmt); ok && found == nil {
			if sym := res.DeclSyms[st]; sym == target {
				found = st
			}
		}
	}
	syntax.WalkStmts(prog, walkS, nil, nil)

	return found
}

// paramOwner 参数所属函数符号与参数序号（FnParams 内下标；方法含接收者 0 位）.
func paramOwner(res *semantic.Result, target *semantic.Symbol) (*semantic.Symbol, int, bool) {
	for fnID, params := range res.FnParams {
		for i, p := range params {
			if p.ID == target.ID {
				fn := findSymByID(res, fnID)
				if fn == nil {
					return nil, 0, false
				}

				return fn, i, true
			}
		}
	}

	return nil, 0, false
}

func findSymByID(res *semantic.Result, id semantic.SymbolID) *semantic.Symbol {
	for _, s := range res.Symbols {
		if s.ID == id {
			return s
		}
	}

	return nil
}

// findFnDeclBySym 按函数符号找声明节点.
func findFnDeclBySym(prog []syntax.Stmt, res *semantic.Result, owner *semantic.Symbol) *syntax.FnDecl {
	for _, d := range collectFnDecls(prog, res) {
		if d.Sym == owner {
			return d.Decl
		}
	}

	if owner.Decl != nil {
		if d, ok := owner.Decl.(*syntax.FnDecl); ok {
			return d
		}
	}

	return nil
}

// —— token 扫描 ——

type spanTok struct {
	kind     syntax.Kind
	absStart int // 绝对 rune 偏移
	absEnd   int
	absLine  int // 绝对行（1 基）
	absCol   int // 绝对列（1 基）
}

// spanTokens 声明区间的词法切分（坐标换算为绝对行列）.
func spanTokens(sp syntax.Span, src string) []spanTok {
	runes := []rune(src)

	lo, hi := sp.Start.Offset, sp.End.Offset
	if lo < 0 || hi > len(runes) || lo >= hi {
		return nil
	}

	lineStarts := []int{lo}
	for i := lo; i < hi; i++ {
		if runes[i] == '\n' {
			lineStarts = append(lineStarts, i+1)
		}
	}

	toks, err := syntax.Lex(string(runes[lo:hi]))
	if err != nil {
		return nil
	}

	out := make([]spanTok, 0, len(toks))
	for _, t := range toks {
		if t.Kind == syntax.EOF || t.Kind == syntax.Newline {
			continue
		}

		lineIdx := t.Line - 1
		if lineIdx < 0 || lineIdx >= len(lineStarts) {
			continue
		}

		off := lo + t.Start
		out = append(out, spanTok{
			kind:     t.Kind,
			absStart: lo + t.Start,
			absEnd:   lo + t.End,
			absLine:  sp.Start.Line + (t.Line - 1),
			absCol:   off - lineStarts[lineIdx] + 1,
		})
	}

	return out
}

// scanNameAfterFn fn 声明内的名字 token：fn 后（方法则越过接收者括号与 `.`）的第一个标识符.
func scanNameAfterFn(sp syntax.Span, srcs Sources, hasRecv bool) (syntax.Span, error) {
	toks := spanTokens(sp, srcs.Source(sp.File))

	i := 0
	for i < len(toks) && toks[i].kind != syntax.KwFn {
		i++
	}

	if i >= len(toks) {
		return syntax.Span{}, errors.New("未找到 fn")
	}

	i++ // 越过 fn

	if hasRecv {
		depth := 1

		i++
		for i < len(toks) && depth > 0 {
			switch toks[i].kind {
			case syntax.LParen:
				depth++
			case syntax.RParen:
				depth--
			}

			if depth > 0 {
				i++
			}
		}

		i++
		for i < len(toks) && toks[i].kind == syntax.Dot {
			i++
		}
	}

	if i >= len(toks) || toks[i].kind != syntax.TokIdent {
		return syntax.Span{}, errors.New("未找到名字 token")
	}

	return spanTokAbs(sp, toks[i]), nil
}

// firstIdentSpan 区间内第一个标识符 token.
func firstIdentSpan(sp syntax.Span, srcs Sources) (syntax.Span, error) {
	toks := spanTokens(sp, srcs.Source(sp.File))
	for _, t := range toks {
		if t.kind == syntax.TokIdent {
			return spanTokAbs(sp, t), nil
		}
	}

	return syntax.Span{}, errors.New("未找到标识符")
}

// paramIdentSpan 第 group 组括号内第 seg 段的首个标识符（参数名）.
func paramIdentSpan(sp syntax.Span, srcs Sources, group, seg int) (syntax.Span, error) {
	toks := spanTokens(sp, srcs.Source(sp.File))
	gi := 0

	i := 0
	for i < len(toks) {
		if toks[i].kind != syntax.LParen {
			i++
			continue
		}

		if gi == group {
			return segIdent(sp, toks, i, seg)
		}

		depth := 1

		i++
		for i < len(toks) && depth > 0 {
			switch toks[i].kind {
			case syntax.LParen, syntax.LBracket:
				depth++
			case syntax.RParen, syntax.RBracket:
				depth--
			}

			if depth > 0 {
				i++
			}
		}
	}

	return syntax.Span{}, errors.New("未找到参数组")
}

func segIdent(sp syntax.Span, toks []spanTok, lp, seg int) (syntax.Span, error) {
	depth := 1
	i := lp + 1
	segIdx := 0

	start := i
	for i < len(toks) && depth > 0 {
		switch toks[i].kind {
		case syntax.LParen, syntax.LBracket:
			depth++
		case syntax.RParen, syntax.RBracket:
			depth--
		}

		if depth == 0 {
			break
		}

		if toks[i].kind == syntax.Comma && depth == 1 {
			if segIdx == seg {
				return firstIdentInSeg(sp, toks[start:i])
			}

			segIdx++
			start = i + 1
		}

		i++
	}

	if segIdx == seg {
		return firstIdentInSeg(sp, toks[start:i])
	}

	return syntax.Span{}, fmt.Errorf("参数段 %d 不存在", seg)
}

func firstIdentInSeg(sp syntax.Span, toks []spanTok) (syntax.Span, error) {
	for _, t := range toks {
		if t.kind == syntax.TokIdent {
			return spanTokAbs(sp, t), nil
		}
	}

	return syntax.Span{}, errors.New("参数段无标识符")
}

// spanTokAbs token → 绝对 Span（spanTokens 已换算为绝对坐标）.
func spanTokAbs(sp syntax.Span, t spanTok) syntax.Span {
	return syntax.Span{
		File:  sp.File,
		Start: syntax.Position{Offset: t.absStart, Line: t.absLine, Col: t.absCol},
		End:   syntax.Position{Offset: t.absEnd, Line: t.absLine, Col: t.absCol + (t.absEnd - t.absStart)},
	}
}
