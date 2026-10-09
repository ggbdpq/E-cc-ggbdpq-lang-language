package graph

// build.go：引用/写入/类型边 + 文件级 depends_on 的 AST 走查。
// call/spawn/method 边来自 HIR（graph.go），此处不重复建。

import (
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
)

type refCtx struct {
	g        *Graph
	res      *semantic.Result
	fnID     int    // 当前所属函数 SymbolID（0 = 模块作用域）
	file     string // 当前文件
	writeSet map[*syntax.Ident]bool
	callsSet map[*syntax.Ident]bool
	depSeen  map[string]bool // depends_on 去重（file\x00module）
}

// collectRefs 引用/写入/类型边 + 文件级 depends_on.
func (g *Graph) collectRefs(prog []syntax.Stmt, res *semantic.Result) {
	c := &refCtx{g: g, res: res, fnID: 0}

	// 预扫：赋值目标集合（write）与调用位置集合（call）
	c.writeSet = map[*syntax.Ident]bool{}
	c.callsSet = map[*syntax.Ident]bool{}
	var scanStmt func(syntax.Stmt)
	var scanExpr func(syntax.Expr)
	scanStmt = func(s syntax.Stmt) {
		if x, ok := s.(*syntax.AssignStmt); ok {
			if id, ok := x.Target.(*syntax.Ident); ok {
				c.writeSet[id] = true
			}
		}
	}
	scanExpr = func(e syntax.Expr) {
		if x, ok := e.(*syntax.CallEx); ok {
			if id, ok := x.Callee.(*syntax.Ident); ok {
				c.callsSet[id] = true
			}
		}
	}
	for _, st := range prog {
		syntax.WalkStmt(st, scanStmt, scanExpr, nil)
	}

	for _, st := range prog {
		c.stmt(st)
	}
}

func (c *refCtx) stmt(s syntax.Stmt) {
	if sp, ok := s.(syntax.Spanned); ok {
		if f := sp.NodeSpan().File; f != "" {
			c.file = f
		}
	}
	switch x := s.(type) {
	case *syntax.LetStmt:
		c.expr(x.Init)
	case *syntax.AssignStmt:
		c.expr(x.Target)
		c.expr(x.Val)
	case *syntax.ExprStmt:
		c.expr(x.X)
	case *syntax.ReturnStmt:
		if x.Val != nil {
			c.expr(x.Val)
		}
	case *syntax.IfStmt:
		c.expr(x.Cond)
		c.block(x.Then)
		c.block(x.Else)
	case *syntax.WhileStmt:
		c.expr(x.Cond)
		c.block(x.Body)
	case *syntax.ForInStmt:
		c.expr(x.Iter)
		c.block(x.Body)
	case *syntax.GoStmt:
		c.expr(x.Call)
	case *syntax.FnDecl:
		// 嵌套 fn：切换所属函数上下文（符号来自 resolver DeclSyms）
		saved := c.fnID
		if sym, ok := c.res.DeclSyms[x]; ok {
			c.fnID = int(sym.ID)
		}
		for _, st := range x.Body {
			c.stmt(st)
		}
		c.fnID = saved
	case *syntax.ExternDecl, *syntax.StructDecl, *syntax.EnumDecl, *syntax.ImportStmt:
	}
}

func (c *refCtx) block(list []syntax.Stmt) {
	for _, s := range list {
		c.stmt(s)
	}
}

func (c *refCtx) expr(e syntax.Expr) {
	syntax.WalkExpr(e, nil, func(x syntax.Expr) {
		switch t := x.(type) {
		case *syntax.Ident:
			sym := c.res.Refs[t]
			if sym == nil {
				return
			}
			// 直接调用的 fn 名：call 边由 HIR 收集（HIR 已解析方法符号），此处跳过
			if c.callsSet[t] && sym.Kind == semantic.KFn {
				return
			}
			kind := EdgeReference
			if c.writeSet[t] {
				kind = EdgeWrite
			}
			sp := t.Sp
			c.g.addEdge(Edge{From: c.fnID, To: int(sym.ID), Kind: kind, Span: &sp})
		case *syntax.StructLit:
			if sym := c.res.Refs[t]; sym != nil {
				sp := t.Sp
				c.g.addEdge(Edge{From: c.fnID, To: int(sym.ID), Kind: EdgeType, Span: &sp})
			}
		}
	}, nil)
}

// addEdge 统一入口（同时记录文件级 depends_on）.
func (g *Graph) addEdge(e Edge) {
	g.Edges = append(g.Edges, e)
	idx := len(g.Edges) - 1
	g.in[e.To] = append(g.in[e.To], idx)
	g.out[e.From] = append(g.out[e.From], idx)
	if e.From > 0 && e.To > 0 {
		from := g.nodeModule(e.From)
		to := g.nodeModule(e.To)
		if from != "" && to != "" && from != to {
			key := from + "\x00" + to
			if !g.depSeen[key] {
				g.depSeen[key] = true
				g.FileDependsOn[from] = append(g.FileDependsOn[from], to)
			}
		}
	}
}

func (g *Graph) nodeModule(id int) string {
	if n, ok := g.Nodes[id]; ok {
		return n.Module
	}
	return ""
}
