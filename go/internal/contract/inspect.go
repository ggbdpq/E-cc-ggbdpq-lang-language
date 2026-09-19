package contract

// inspect.go：symbols / refs / type 查询（数据源 semantic + types + AST）

import (
	"sort"

	"lang-go/internal/load"
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
)

// kindStr 符号类别 → 契约字符串
func kindStr(k semantic.Kind) string {
	switch k {
	case semantic.KFn:
		return "function"
	case semantic.KMethod:
		return "method"
	case semantic.KVar:
		return "variable"
	case semantic.KParam:
		return "parameter"
	case semantic.KStruct:
		return "struct"
	case semantic.KEnum:
		return "enum"
	case semantic.KCtor:
		return "constructor"
	}
	return "unknown"
}

// Symbols 符号清单（ID 序 = 声明/绑定序，天然稳定；预载符号不输出）
func Symbols(prog []syntax.Stmt, res *semantic.Result, ty *types.Result) []Symbol {
	// 反查绑定节点的定义 Span（var/for 变量）
	stmtSpan := map[semantic.SymbolID]syntax.Span{}
	for st, sym := range res.DeclSyms {
		if sp, ok := st.(syntax.Spanned); ok {
			stmtSpan[sym.ID] = *sp.NodeSpan()
		}
	}
	// 类型表
	typeOf := func(sym *semantic.Symbol) string {
		if ty == nil {
			return ""
		}
		if t := ty.SymTy[sym.ID]; t != nil {
			return t.String()
		}
		if t := ty.FnTy[sym.ID]; t != nil {
			return t.String()
		}
		return ""
	}
	defSpan := func(sym *semantic.Symbol) *Span {
		if d, ok := sym.Decl.(syntax.Spanned); ok {
			return SpanJSON(*d.NodeSpan())
		}
		if sp, ok := stmtSpan[sym.ID]; ok {
			return SpanJSON(sp)
		}
		return nil
	}

	out := make([]Symbol, 0, len(res.Symbols))
	for _, sym := range res.Symbols {
		if sym.BuiltinMark {
			continue
		}
		visibility := "local"
		if sym.Line > 0 && !sym.Mutable && isTopLevel(sym) {
			visibility = "public"
		}
		module := ""
		if sp := defSpan(sym); sp != nil {
			module = sp.File
		}
		out = append(out, Symbol{
			SymbolID:   int(sym.ID),
			Name:       sym.Name,
			Kind:       kindStr(sym.Kind),
			Definition: defSpan(sym),
			Type:       typeOf(sym),
			Visibility: visibility,
			Module:     module,
		})
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].SymbolID < out[j].SymbolID })
	return out
}

// isTopLevel 顶层声明的近似判定：fn/struct/enum/方法/ctor 视为顶层可见
func isTopLevel(sym *semantic.Symbol) bool {
	switch sym.Kind {
	case semantic.KFn, semantic.KMethod, semantic.KStruct, semantic.KEnum, semantic.KCtor:
		return true
	}
	return false
}

// References 引用清单：read/write/call/pattern/type。
// 回调只做分类记录，递归完全交给 syntax.Walk（回调内再 Walk 会无穷递归）。
func References(prog []syntax.Stmt, res *semantic.Result) []Reference {
	writes := map[*syntax.Ident]bool{}
	calls := map[*syntax.Ident]bool{}
	passA := func(st syntax.Stmt, e syntax.Expr) {
		if x, ok := st.(*syntax.AssignStmt); ok {
			if id, ok := x.Target.(*syntax.Ident); ok {
				writes[id] = true
			}
		}
		if x, ok := e.(*syntax.CallEx); ok {
			if id, ok := x.Callee.(*syntax.Ident); ok {
				calls[id] = true
			}
		}
	}
	syntax.WalkStmts(prog, func(st syntax.Stmt) { passA(st, nil) }, func(e syntax.Expr) { passA(nil, e) }, nil)

	out := make([]Reference, 0, len(res.Refs))
	add := func(sp syntax.Span, sym *semantic.Symbol, kind string) {
		if sym == nil {
			return
		}
		out = append(out, Reference{SymbolID: int(sym.ID), Span: SpanJSON(sp), ReferenceKind: kind})
	}
	onStmt := func(st syntax.Stmt) {}
	onExpr := func(e syntax.Expr) {
		switch x := e.(type) {
		case *syntax.Ident:
			if sym := res.Refs[x]; sym != nil {
				kind := "read"
				switch {
				case writes[x]:
					kind = "write"
				case calls[x]:
					kind = "call"
				}
				add(x.Sp, sym, kind)
			}
		case *syntax.StructLit:
			if sym := res.Refs[x]; sym != nil {
				add(x.Sp, sym, "type")
			}
		}
	}
	onPat := func(p syntax.Pat) {
		if cp, ok := p.(*syntax.CasePat); ok {
			if sym := res.PatRefs[cp]; sym != nil {
				add(cp.Sp, sym, "pattern")
			}
		}
	}
	syntax.WalkStmts(prog, onStmt, onExpr, onPat)

	sort.SliceStable(out, func(i, j int) bool {
		a, b := out[i], out[j]
		if a.SymbolID != b.SymbolID {
			return a.SymbolID < b.SymbolID
		}
		if a.Span == nil || b.Span == nil {
			return b.Span != nil
		}
		if a.Span.File != b.Span.File {
			return a.Span.File < b.Span.File
		}
		return a.Span.Start.Offset < b.Span.Start.Offset
	})
	return out
}

// TypeAt 行列定位最内层表达式节点
func TypeAt(prog []syntax.Stmt, res *semantic.Result, ty *types.Result, file string, line, col int) *Node {
	best := &Node{Kind: "unknown"}
	found := false
	bestSize := 1 << 60
	consider := func(e syntax.Expr) {
		sp, ok := e.(syntax.Spanned)
		if !ok {
			return
		}
		s := *sp.NodeSpan()
		if file != "" && s.File != file {
			return
		}
		if !containsPos(s, line, col) {
			return
		}
		size := spanSize(s)
		if !found || size < bestSize {
			ab := &astBuilder{ty: ty, res: res, enable: ty != nil}
			best = ab.expr(e)
			found = true
			bestSize = size
		}
	}
	syntax.WalkStmts(prog, nil, consider, nil)
	if !found {
		return nil
	}
	return best
}

func containsPos(s syntax.Span, line, col int) bool {
	if line < s.Start.Line || line > s.End.Line {
		return false
	}
	if line == s.Start.Line && col < s.Start.Col {
		return false
	}
	if line == s.End.Line && col > s.End.Col {
		return false
	}
	return true
}

func spanSize(s syntax.Span) int {
	dl := (s.End.Line - s.Start.Line) * 100000
	return dl + s.End.Col - s.Start.Col
}

// —— Envelope 组装 ——

// SymbolsEnvelope inspect symbols --json
func SymbolsEnvelope(prog []syntax.Stmt, res *semantic.Result, ty *types.Result) Envelope {
	return Envelope{SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: KindInspectSymbols, Symbols: Symbols(prog, res, ty)}
}

// ReferencesEnvelope inspect refs --json
func ReferencesEnvelope(prog []syntax.Stmt, res *semantic.Result) Envelope {
	return Envelope{SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: KindInspectRefs, References: References(prog, res)}
}

// TypeEnvelope inspect type --json；未命中 ok=false（负载为结构化 error）
func TypeEnvelope(prog []syntax.Stmt, res *semantic.Result, ty *types.Result, file string, line, col int) (Envelope, bool) {
	n := TypeAt(prog, res, ty, file, line, col)
	if n == nil {
		return ErrorEnvelope(KindInspectType, "指定位置没有表达式节点"), false
	}
	return Envelope{SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: KindInspectType, Nodes: []*Node{n}}, true
}

// DependenciesEnvelope inspect dependencies --json
func DependenciesEnvelope(files []load.FileInfo) Envelope {
	deps := make([]Dependency, 0, len(files))
	for _, f := range files {
		imports := f.Imports
		if imports == nil {
			imports = []string{}
		}
		deps = append(deps, Dependency{Module: f.Path, Imports: imports})
	}
	return Envelope{SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: KindInspectDependencies, Dependencies: deps}
}

// EffectsEnvelope inspect effects --json
func EffectsEnvelope(effs []Effect) Envelope {
	return Envelope{SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: KindInspectEffects, Effects: effs}
}

// DumpEnvelope dump ast/hir/ir --json
func DumpEnvelope(kind string, nodes []*Node) Envelope {
	return Envelope{SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: kind, Nodes: nodes}
}
