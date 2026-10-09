package graph

// Package graph 构建语义关系图（v0.6 Semantic Graph）。
// 真值源：semantic 符号表 + types 产物 + HIR 调用/方法边 + IR 效果推导 + load 文件依赖。
// 纯内存图；节点键 = SymbolID（编译内稳定）；查询为 BFS（访问集防环）。

import (
	"lang-go/internal/hir"
	"lang-go/internal/ir"
	"lang-go/internal/load"
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
)

// Node 图节点（Symbol 粒度；字段/枚举 case 以 struct/enum 节点的 Fields 列表表达）.
type Node struct {
	ID        int
	Kind      string
	Name      string
	Module    string
	Span      syntax.Span
	Type      string
	Effects   []string
	Fields    []string
	Extern    bool
	Immutable bool
}

// EdgeKind 边类别.
type EdgeKind string

const (
	EdgeCall      EdgeKind = "call"
	EdgeReference EdgeKind = "references"
	EdgeWrite     EdgeKind = "writes"
	EdgeSpawn     EdgeKind = "spawn"
	EdgeType      EdgeKind = "type"
	EdgePattern   EdgeKind = "pattern"
	EdgeDependsOn EdgeKind = "depends_on" // 文件级
	EdgeImports   EdgeKind = "imports"    // 文件级（声明）
)

// Edge 有向边：From → To（SymbolID）。文件级边（imports/depends_on）To = -1、File = 目标。
type Edge struct {
	From int
	To   int
	Kind EdgeKind
	Span *syntax.Span
	File string
}

// Graph 内存语义图.
type Graph struct {
	Nodes map[int]*Node
	Order []int // 节点 ID（构建序）
	Edges []Edge

	in  map[int][]int // 入边索引（按 To）
	out map[int][]int // 出边索引（按 From）

	FileImports   map[string][]string
	FileDependsOn map[string][]string
	Files         []string

	depSeen map[string]bool // depends_on 去重
}

// In 返回指向符号的边.
func (g *Graph) In(id int) []Edge {
	var out []Edge
	for _, i := range g.in[id] {
		out = append(out, g.Edges[i])
	}

	return out
}

// Out 返回符号指出的边.
func (g *Graph) Out(id int) []Edge {
	var out []Edge
	for _, i := range g.out[id] {
		out = append(out, g.Edges[i])
	}

	return out
}

// Node 按符号 ID 取节点.
func (g *Graph) Node(id int) (*Node, bool) {
	n, ok := g.Nodes[id]
	return n, ok
}

// Build 从全前端产物构建语义图.
func Build(prog []syntax.Stmt, res *semantic.Result, ty *types.Result, h *hir.Prog, irp *ir.Prog, files []load.FileInfo) *Graph {
	g := &Graph{
		Nodes:         map[int]*Node{},
		in:            map[int][]int{},
		out:           map[int][]int{},
		FileImports:   map[string][]string{},
		FileDependsOn: map[string][]string{},
		depSeen:       map[string]bool{},
	}
	for _, f := range files {
		g.Files = append(g.Files, f.Path)
		g.FileImports[f.Path] = append([]string(nil), f.Imports...)
	}

	// 1) 节点（用户符号）
	for _, sym := range res.Symbols {
		if sym.BuiltinMark {
			continue
		}

		n := &Node{
			ID:        int(sym.ID),
			Kind:      kindStr(sym.Kind),
			Name:      sym.Name,
			Extern:    sym.Extern,
			Immutable: !sym.Mutable,
		}
		if d, ok := sym.Decl.(syntax.Spanned); ok {
			n.Span = *d.NodeSpan()
			n.Module = n.Span.File
		}

		if ty != nil {
			if t := ty.SymTy[sym.ID]; t != nil {
				n.Type = t.String()
			} else if t := ty.FnTy[sym.ID]; t != nil {
				n.Type = t.String()
			}
		}

		if sym.Kind == semantic.KStruct {
			n.Fields = append(n.Fields, sym.Fields...)
		}

		g.Nodes[n.ID] = n
		g.Order = append(g.Order, n.ID)
	}

	// 2) 效果（IR 推导）附着到函数/方法节点
	if irp != nil {
		for _, e := range Effects(irp) {
			if n := g.Nodes[e.SymbolID]; n != nil {
				n.Effects = e.Effects
			}
		}
	}

	// 3) call / spawn / method 边（HIR：已解析符号 + Span）
	if h != nil {
		for _, gl := range h.Globals {
			collectHIRStmt(gl, 0, g)
		}

		for _, f := range h.Fns {
			owner := 0
			if f.Sym != nil {
				owner = int(f.Sym.ID)
			}

			collectHIRStmts(f.Body, owner, g)
		}
	}

	// 4) reference / write / type 边（AST + resolver 引用表）+ 文件级 depends_on
	g.collectRefs(prog, res)

	return g
}

func fileNames(files []load.FileInfo) []string {
	out := make([]string, 0, len(files))
	for _, f := range files {
		out = append(out, f.Path)
	}

	return out
}

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

// collectHIRStmts 收集 HIR 语句中的 call/spawn/method 边.
func collectHIRStmts(list []hir.Stmt, from int, g *Graph) {
	for _, st := range list {
		collectHIRStmt(st, from, g)
	}
}

func collectHIRStmt(st hir.Stmt, from int, g *Graph) {
	switch x := st.(type) {
	case *hir.Let:
		collectHIRExpr(x.Init, from, g)
	case *hir.Assign:
		collectHIRExpr(x.Target, from, g)
		collectHIRExpr(x.RHS, from, g)
	case *hir.Eval:
		collectHIRExpr(x.X, from, g)
	case *hir.Return:
		if x.Val != nil {
			collectHIRExpr(x.Val, from, g)
		}
	case *hir.If:
		collectHIRExpr(x.Cond, from, g)
		collectHIRStmts(x.Then, from, g)
		collectHIRStmts(x.Else, from, g)
	case *hir.While:
		collectHIRExpr(x.Cond, from, g)
		collectHIRStmts(x.Body, from, g)
	case *hir.ForIn:
		collectHIRExpr(x.Iter, from, g)
		collectHIRStmts(x.Body, from, g)
	case *hir.Spawn:
		sp := syntax.Span{}

		if x.Fn != nil {
			g.addEdge(Edge{From: from, To: int(x.Fn.ID), Kind: EdgeSpawn})

			_ = sp
		}

		for _, a := range x.Args {
			collectHIRExpr(a, from, g)
		}
	case *hir.FnStmt:
		collectHIRStmts(x.Fn.Body, from, g)
	}
}

func argsOf(args []hir.Expr) []hir.Expr { return args }

// collectHIRExpr 递归收集表达式中的 call/method/spawn 边.
func collectHIRExpr(e hir.Expr, from int, g *Graph) {
	switch x := e.(type) {
	case *hir.Ctor:
		for _, a := range x.Args {
			collectHIRExpr(a, from, g)
		}
	case *hir.Call:
		if x.Fn != nil {
			g.addEdge(Edge{From: from, To: int(x.Fn.ID), Kind: EdgeCall, Span: &x.Sp})
		}

		if x.Callee != nil {
			collectHIRExpr(x.Callee, from, g)
		}

		for _, a := range x.Args {
			collectHIRExpr(a, from, g)
		}
	case *hir.MethodCall:
		if x.Method != nil {
			g.addEdge(Edge{From: from, To: int(x.Method.ID), Kind: EdgeCall, Span: &x.Sp})
		}

		collectHIRExpr(x.Recv, from, g)

		for _, a := range x.Args {
			collectHIRExpr(a, from, g)
		}
	case *hir.Unary:
		collectHIRExpr(x.X, from, g)
	case *hir.Binary:
		collectHIRExpr(x.L, from, g)
		collectHIRExpr(x.R, from, g)
	case *hir.AndThen:
		collectHIRExpr(x.L, from, g)
		collectHIRExpr(x.R, from, g)
	case *hir.OrElse:
		collectHIRExpr(x.L, from, g)
		collectHIRExpr(x.R, from, g)
	case *hir.Range:
		collectHIRExpr(x.Lo, from, g)
		collectHIRExpr(x.Hi, from, g)
	case *hir.Index:
		collectHIRExpr(x.Obj, from, g)
		collectHIRExpr(x.Idx, from, g)
	case *hir.Slice:
		collectHIRExpr(x.Obj, from, g)
		collectHIRExpr(x.Lo, from, g)
		collectHIRExpr(x.Hi, from, g)
	case *hir.Field:
		collectHIRExpr(x.Obj, from, g)
	case *hir.Try:
		collectHIRExpr(x.X, from, g)
	case *hir.StructLit:
		for _, f := range x.Fields {
			collectHIRExpr(f.Val, from, g)
		}
	case *hir.MapLit:
		for i := range x.Keys {
			collectHIRExpr(x.Keys[i], from, g)
			collectHIRExpr(x.Vals[i], from, g)
		}
	case *hir.SliceLit:
		for _, el := range x.Elems {
			collectHIRExpr(el, from, g)
		}
	case *hir.Match:
		collectHIRExpr(x.Scrut, from, g)

		for _, arm := range x.Arms {
			collectHIRExpr(arm.Body, from, g)
		}
	case *hir.FnLit:
		collectHIRStmts(x.Body, from, g)
	}
}
