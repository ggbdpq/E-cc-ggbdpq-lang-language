package contract

// dumpast.go：AST → 契约 JSON 节点树。
// nodeId 为契约层按先序分配的稳定编号（同一源码同一结果）。

import (
	"strconv"

	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
)

type astBuilder struct {
	id     int
	ty     *types.Result
	res    *semantic.Result
	enable bool // 有 types 产物时才标 type
}

func (b *astBuilder) next() int {
	b.id++
	return b.id
}

func (b *astBuilder) node(sp syntax.Span) *Node {
	return &Node{NodeID: b.next(), Span: SpanJSON(sp)}
}

func (b *astBuilder) typ(e syntax.Expr) string {
	if !b.enable || b.ty == nil {
		return ""
	}

	if t := b.ty.ExprTy[e]; t != nil {
		return t.String()
	}

	return ""
}

func (b *astBuilder) symType(st *syntax.LetStmt) string {
	if !b.enable {
		return ""
	}

	if sym := b.res.DeclSyms[st]; sym != nil {
		if t := b.ty.SymTy[sym.ID]; t != nil {
			return t.String()
		}
	}

	return ""
}

func (b *astBuilder) forVarType(st *syntax.ForInStmt) string {
	if !b.enable {
		return ""
	}

	if sym := b.res.DeclSyms[st]; sym != nil {
		if t := b.ty.SymTy[sym.ID]; t != nil {
			return t.String()
		}
	}

	return ""
}

// ASTJSON 整棵 AST → 契约节点（ty/res 可为 nil：仅 dump 语法树时不标类型）.
func ASTJSON(prog []syntax.Stmt, res *semantic.Result, ty *types.Result) []*Node {
	b := &astBuilder{ty: ty, res: res, enable: ty != nil}
	return b.stmts(prog)
}

func (b *astBuilder) stmts(list []syntax.Stmt) []*Node {
	out := make([]*Node, 0, len(list))
	for _, st := range list {
		out = append(out, b.stmt(st))
	}

	return out
}

func (b *astBuilder) stmt(st syntax.Stmt) *Node {
	switch x := st.(type) {
	case *syntax.LetStmt:
		kind := "let"
		if x.Mutable {
			kind = "var"
		}

		n := b.node(x.Sp)
		n.Kind = kind
		n.Name = x.Name
		n.Type = b.symType(x)
		n.Children = []*Node{b.expr(x.Init)}

		return n

	case *syntax.AssignStmt:
		n := b.node(x.Sp)
		n.Kind = "assign"
		n.Name = assignOp(x.Op)
		n.Children = []*Node{b.expr(x.Target), b.expr(x.Val)}

		return n

	case *syntax.ExprStmt:
		n := b.node(x.Sp)
		n.Kind = "eval"
		n.Children = []*Node{b.expr(x.X)}

		return n

	case *syntax.ReturnStmt:
		n := b.node(x.Sp)

		n.Kind = "return"
		if x.Val != nil {
			n.Children = []*Node{b.expr(x.Val)}
		}

		return n

	case *syntax.IfStmt:
		n := b.node(x.Sp)
		n.Kind = "if"
		n.Children = []*Node{b.expr(x.Cond)}

		n.Children = append(n.Children, b.stmts(x.Then)...)
		if len(x.Else) > 0 {
			els := &Node{NodeID: b.next(), Kind: "else"}
			els.Children = b.stmts(x.Else)
			n.Children = append(n.Children, els)
		}

		return n

	case *syntax.WhileStmt:
		n := b.node(x.Sp)
		n.Kind = "while"
		n.Children = []*Node{b.expr(x.Cond)}
		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n

	case *syntax.ForInStmt:
		n := b.node(x.Sp)
		n.Kind = "forin"
		n.Name = x.Var
		n.Type = b.forVarType(x)
		n.Children = []*Node{b.expr(x.Iter)}
		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n

	case *syntax.BreakStmt:
		n := b.node(x.Sp)
		n.Kind = "break"

		return n

	case *syntax.ContinueStmt:
		n := b.node(x.Sp)
		n.Kind = "continue"

		return n

	case *syntax.FnDecl:
		n := b.node(x.Sp)

		n.Kind = "fn"
		if x.Recv != nil {
			n.Name = "(" + x.Recv.Name + ") " + x.Name
		} else {
			n.Name = x.Name
		}

		for _, p := range x.Params {
			n.Children = append(n.Children, b.param(p))
		}

		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n

	case *syntax.StructDecl:
		n := b.node(x.Sp)
		n.Kind = "struct"

		n.Name = x.Name
		for _, f := range x.Fields {
			fn := b.node(x.Sp)
			fn.Kind = "field"
			fn.Name = f.Name
			fn.Type = b.annoType(f.Typ)
			n.Children = append(n.Children, fn)
		}

		return n

	case *syntax.EnumDecl:
		n := b.node(x.Sp)
		n.Kind = "enum"

		n.Name = x.Name
		for _, c := range x.Cases {
			cn := b.node(c.Sp)
			cn.Kind = "case"

			cn.Name = c.Name
			for i, pt := range c.Payloads {
				pn := b.node(c.Sp)
				pn.Kind = "payload"
				pn.Name = strconv.Itoa(i)
				pn.Type = b.annoType(pt)
				cn.Children = append(cn.Children, pn)
			}

			n.Children = append(n.Children, cn)
		}

		return n

	case *syntax.ExternDecl:
		n := b.node(x.Sp)
		n.Kind = "extern"

		n.Name = x.Host
		for _, fn := range x.Fns {
			fnN := b.node(fn.Sp)
			fnN.Kind = "fn"

			fnN.Name = fn.Name
			for _, p := range fn.Params {
				fnN.Children = append(fnN.Children, b.param(p))
			}

			n.Children = append(n.Children, fnN)
		}

		return n

	case *syntax.GoStmt:
		n := b.node(x.Sp)
		n.Kind = "spawn"
		n.Children = []*Node{b.expr(x.Call)}

		return n

	case *syntax.ImportStmt:
		n := b.node(x.Sp)
		n.Kind = "import"
		n.Name = x.Path

		return n
	}

	return nil
}

func (b *astBuilder) param(p syntax.Param) *Node {
	n := &Node{NodeID: b.next(), Kind: "param", Name: p.Name, Type: b.annoType(p.Typ)}
	return n
}

func (b *astBuilder) annoType(t syntax.TypeEx) string {
	if t == nil {
		return ""
	}

	return "annotated"
}

func assignOp(k interface{ String() string }) string { return k.String() }

func (b *astBuilder) expr(e syntax.Expr) *Node {
	switch x := e.(type) {
	case *syntax.IntLit:
		n := b.node(x.Sp)
		n.Kind = "int_lit"
		n.Value = strconv.FormatInt(x.Val, 10)
		n.Type = b.typ(x)

		return n
	case *syntax.FloatLit:
		n := b.node(x.Sp)
		n.Kind = "float_lit"
		n.Value = strconv.FormatFloat(x.Val, 'g', -1, 64)
		n.Type = b.typ(x)

		return n
	case *syntax.StrLit:
		n := b.node(x.Sp)
		n.Kind = "str_lit"
		n.Value = x.Val
		n.Type = b.typ(x)

		return n
	case *syntax.BoolLit:
		n := b.node(x.Sp)
		n.Kind = "bool_lit"
		n.Value = strconv.FormatBool(x.Val)
		n.Type = b.typ(x)

		return n

	case *syntax.Ident:
		n := b.node(x.Sp)
		n.Kind = "ident"
		n.Name = x.Name
		n.Type = b.typ(x)

		return n

	case *syntax.Unary:
		n := b.node(x.Sp)
		n.Kind = "unary"
		n.Name = x.Op.String()
		n.Type = b.typ(x)
		n.Children = []*Node{b.expr(x.X)}

		return n

	case *syntax.Binary:
		n := b.node(x.Sp)
		n.Kind = "binary"
		n.Name = x.Op.String()
		n.Type = b.typ(x)
		n.Children = []*Node{b.expr(x.L), b.expr(x.R)}

		return n

	case *syntax.RangeEx:
		n := b.node(x.Sp)
		n.Kind = "range"
		n.Type = b.typ(x)
		n.Children = []*Node{b.expr(x.Lo), b.expr(x.Hi)}

		return n

	case *syntax.CallEx:
		n := b.node(x.Sp)
		n.Kind = "call"
		n.Type = b.typ(x)

		n.Children = []*Node{b.expr(x.Callee)}
		for _, a := range x.Args {
			n.Children = append(n.Children, b.expr(a))
		}

		return n

	case *syntax.IndexEx:
		n := b.node(x.Sp)
		n.Kind = "index"
		n.Type = b.typ(x)
		n.Children = []*Node{b.expr(x.Obj), b.expr(x.Idx)}

		return n

	case *syntax.SliceEx:
		n := b.node(x.Sp)
		n.Kind = "slice"
		n.Type = b.typ(x)
		n.Children = []*Node{b.expr(x.Obj), b.expr(x.Lo), b.expr(x.Hi)}

		return n

	case *syntax.FieldEx:
		n := b.node(x.Sp)
		n.Kind = "field_access"
		n.Name = x.Name
		n.Type = b.typ(x)
		n.Children = []*Node{b.expr(x.Obj)}

		return n

	case *syntax.TryEx:
		n := b.node(x.Sp)
		n.Kind = "try"
		n.Type = b.typ(x)
		n.Children = []*Node{b.expr(x.X)}

		return n

	case *syntax.StructLit:
		n := b.node(x.Sp)
		n.Kind = "struct_lit"
		n.Name = x.TypeName

		n.Type = b.typ(x)
		for _, f := range x.Fields {
			fn := &Node{NodeID: b.next(), Kind: "field_init", Name: f.Name}
			fn.Children = []*Node{b.expr(f.Val)}
			n.Children = append(n.Children, fn)
		}

		return n

	case *syntax.MapLit:
		n := b.node(x.Sp)
		n.Kind = "map_lit"

		n.Type = b.typ(x)
		for _, it := range x.Items {
			kn := b.expr(it.Key)
			vn := b.expr(it.Val)
			item := &Node{NodeID: b.next(), Kind: "item"}
			item.Children = []*Node{kn, vn}
			n.Children = append(n.Children, item)
		}

		return n

	case *syntax.ArrLit:
		n := b.node(x.Sp)
		n.Kind = "arr_lit"

		n.Type = b.typ(x)
		for _, el := range x.Items {
			n.Children = append(n.Children, b.expr(el))
		}

		return n

	case *syntax.MatchEx:
		n := b.node(x.Sp)
		n.Kind = "match"
		n.Type = b.typ(x)

		n.Children = []*Node{b.expr(x.Scrut)}
		for _, arm := range x.Arms {
			an := &Node{NodeID: b.next(), Kind: "arm"}
			an.Children = []*Node{b.pat(arm.Pat), b.expr(arm.Body)}
			n.Children = append(n.Children, an)
		}

		return n

	case *syntax.FnLit:
		n := b.node(x.Sp)
		n.Kind = "fnlit"

		n.Type = b.typ(x)
		for _, p := range x.Params {
			n.Children = append(n.Children, b.param(p))
		}

		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n
	}

	return nil
}

func (b *astBuilder) pat(p syntax.Pat) *Node {
	switch x := p.(type) {
	case *syntax.WildPat:
		n := b.node(x.Sp)
		n.Kind = "pattern_wild"

		return n
	case *syntax.BindPat:
		n := b.node(x.Sp)
		n.Kind = "pattern_bind"
		n.Name = x.Name

		return n
	case *syntax.LitPat:
		n := b.node(x.Sp)
		n.Kind = "pattern_lit"
		n.Children = []*Node{b.expr(x.Val)}

		return n
	case *syntax.CasePat:
		n := b.node(x.Sp)
		n.Kind = "pattern_ctor"

		n.Name = x.Name
		for _, sp := range x.Subs {
			n.Children = append(n.Children, b.pat(sp))
		}

		return n
	}

	return nil
}
