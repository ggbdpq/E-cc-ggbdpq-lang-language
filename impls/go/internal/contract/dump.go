package contract

// dump.go：Typed HIR / Core IR → 契约 JSON 节点树。
// HIR 使用编译器 NodeID（先序稳定）与 Span；IR 节点无内部 ID，
// 由契约层按先序编号，且不含 span（span 见 dump.hir，文档化于 agent-protocol）。

import (
	"lang-go/internal/hir"
	"lang-go/internal/ir"
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
	"strconv"
)

func typeStr(t interface{ String() string }) string {
	if t == nil {
		return ""
	}

	return t.String()
}

func symStr(s *semantic.Symbol) string {
	if s == nil {
		return ""
	}

	return s.Name
}

// —— HIR ——

type hirBuilder struct{}

// HIRJSON 整个 HIR 程序.
func HIRJSON(p *hir.Prog) []*Node {
	b := &hirBuilder{}

	out := make([]*Node, 0, len(p.Structs)+len(p.Enums)+len(p.Globals)+len(p.Fns))
	for _, s := range p.Structs {
		n := stmtNode(&s.Node, "struct")

		n.Name = s.Name
		for _, f := range s.Fields {
			n.Children = append(n.Children, &Node{NodeID: int(s.ID), Kind: "field", Name: f.Name, Type: typeStr(f.Typ)})
		}

		out = append(out, n)
	}

	for _, e := range p.Enums {
		n := stmtNode(&e.Node, "enum")

		n.Name = e.Name
		for _, c := range e.Cases {
			n.Children = append(n.Children, &Node{NodeID: int(e.ID), Kind: "case", Name: c.Name})
		}

		out = append(out, n)
	}

	for _, g := range p.Globals {
		out = append(out, b.stmt(g))
	}

	for _, f := range p.Fns {
		out = append(out, b.fn(f))
	}

	return out
}

// hirNode 携带编译器元数据（NodeID/Span/Type/Symbol）.
func hirNode(n *hir.Node, kind string) *Node {
	cn := &Node{NodeID: int(n.ID), Kind: kind, Span: SpanJSON(n.Sp), Type: typeStr(n.Typ)}
	return cn
}

// stmtNode 无类型容器节点壳.
func stmtNode(n *hir.Node, kind string) *Node {
	return &Node{NodeID: int(n.ID), Kind: kind, Span: SpanJSON(n.Sp)}
}

func (b *hirBuilder) fn(f *hir.Fn) *Node {
	n := hirNode(&f.Node, "fn")

	n.Name = f.Name
	if f.Sym != nil {
		n.Name = f.Sym.Name
	}

	if f.Recv != nil {
		recv := hirNode(&f.Node, "recv")
		recv.Name = f.Recv.Name
		recv.Type = typeStr(f.Recv.Typ)
		n.Children = append(n.Children, recv)
	}

	for _, p := range f.Params {
		pn := hirNode(&f.Node, "param")
		pn.Name = p.Name
		pn.Type = typeStr(p.Typ)
		n.Children = append(n.Children, pn)
	}

	n.Children = append(n.Children, b.stmts(f.Body)...)

	return n
}

func (b *hirBuilder) stmts(list []hir.Stmt) []*Node {
	out := make([]*Node, 0, len(list))
	for _, st := range list {
		out = append(out, b.stmt(st))
	}

	return out
}

func (b *hirBuilder) stmt(st hir.Stmt) *Node {
	switch x := st.(type) {
	case *hir.Let:
		n := hirNode(&x.Node, "let")
		if x.Mutable {
			n.Kind = "var"
		}

		n.Name = x.Name
		n.Children = []*Node{b.expr(x.Init)}

		return n

	case *hir.Assign:
		n := hirNode(&x.Node, "assign")
		n.Name = x.Op.String()
		n.Children = []*Node{b.expr(x.Target), b.expr(x.RHS)}

		return n

	case *hir.Eval:
		n := hirNode(&x.Node, "eval")
		n.Children = []*Node{b.expr(x.X)}

		return n

	case *hir.Return:
		n := hirNode(&x.Node, "return")
		if x.Val != nil {
			n.Children = []*Node{b.expr(x.Val)}
		}

		return n

	case *hir.If:
		n := hirNode(&x.Node, "if")
		n.Children = []*Node{b.expr(x.Cond)}

		n.Children = append(n.Children, b.stmts(x.Then)...)
		if len(x.Else) > 0 {
			els := stmtNode(&x.Node, "else")
			els.Children = b.stmts(x.Else)
			n.Children = append(n.Children, els)
		}

		return n

	case *hir.While:
		n := hirNode(&x.Node, "while")
		n.Children = []*Node{b.expr(x.Cond)}
		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n

	case *hir.ForIn:
		n := hirNode(&x.Node, "forin")
		n.Name = x.Var
		n.Type = typeStr(x.VarType)
		n.Children = []*Node{b.expr(x.Iter)}
		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n

	case *hir.Break:
		return hirNode(&x.Node, "break")
	case *hir.Continue:
		return hirNode(&x.Node, "continue")

	case *hir.Spawn:
		n := hirNode(&x.Node, "spawn")

		n.Name = symStr(x.Fn)
		for _, a := range x.Args {
			n.Children = append(n.Children, b.expr(a))
		}

		return n

	case *hir.FnStmt:
		return b.fn(x.Fn)
	}

	return &Node{Kind: "unknown"}
}

func (b *hirBuilder) expr(e hir.Expr) *Node {
	switch x := e.(type) {
	case *hir.Lit:
		n := hirNode(&x.Node, "int_lit")
		switch x.Kind {
		case hir.LitInt:
			n.Value = strconv.FormatInt(x.I64, 10)
		case hir.LitFloat:
			n.Kind = "float_lit"
			n.Value = strconv.FormatFloat(x.F64, 'g', -1, 64)
		case hir.LitStr:
			n.Kind = "str_lit"
			n.Value = x.S
		case hir.LitBool:
			n.Kind = "bool_lit"
			n.Value = strconv.FormatBool(x.B)
		}

		return n

	case *hir.LocalRef:
		n := hirNode(&x.Node, "local")
		n.Name = x.Name

		return n

	case *hir.FnRef:
		n := hirNode(&x.Node, "fnref")
		n.Name = x.Name

		return n

	case *hir.BuiltinRef:
		n := hirNode(&x.Node, "builtin_ref")
		n.Name = x.Name

		return n

	case *hir.Ctor:
		n := hirNode(&x.Node, "ctor")

		n.Name = symStr(x.CtorSym)
		for _, a := range x.Args {
			n.Children = append(n.Children, b.expr(a))
		}

		return n

	case *hir.Call:
		n := hirNode(&x.Node, "call")
		switch {
		case x.Fn != nil:
			n.Name = x.Fn.Name
		case x.Builtin != "":
			n.Kind = "builtin_call"
			n.Name = x.Builtin
		default:
			n.Kind = "call_value"
			n.Children = append(n.Children, b.expr(x.Callee))
		}

		for _, a := range x.Args {
			n.Children = append(n.Children, b.expr(a))
		}

		return n

	case *hir.MethodCall:
		n := hirNode(&x.Node, "method")
		if x.Method != nil {
			n.Name = x.Method.Name
			n.Children = append(n.Children, b.expr(x.Recv))
		} else {
			n.Kind = "call_field"
			n.Name = x.Field
			n.Children = append(n.Children, b.expr(x.Recv))
		}

		for _, a := range x.Args {
			n.Children = append(n.Children, b.expr(a))
		}

		return n

	case *hir.Unary:
		n := hirNode(&x.Node, "unary")
		n.Name = x.Op.String()
		n.Children = []*Node{b.expr(x.X)}

		return n

	case *hir.Binary:
		n := hirNode(&x.Node, "binary")
		n.Name = x.Op.String()
		n.Children = []*Node{b.expr(x.L), b.expr(x.R)}

		return n

	case *hir.AndThen:
		n := hirNode(&x.Node, "and")
		n.Children = []*Node{b.expr(x.L), b.expr(x.R)}

		return n

	case *hir.OrElse:
		n := hirNode(&x.Node, "or")
		n.Children = []*Node{b.expr(x.L), b.expr(x.R)}

		return n

	case *hir.Range:
		n := hirNode(&x.Node, "range")
		n.Children = []*Node{b.expr(x.Lo), b.expr(x.Hi)}

		return n

	case *hir.Index:
		n := hirNode(&x.Node, "index")
		n.Children = []*Node{b.expr(x.Obj), b.expr(x.Idx)}

		return n

	case *hir.Slice:
		n := hirNode(&x.Node, "slice")
		n.Children = []*Node{b.expr(x.Obj), b.expr(x.Lo), b.expr(x.Hi)}

		return n

	case *hir.Field:
		n := hirNode(&x.Node, "field_access")
		n.Name = x.Name
		n.Children = []*Node{b.expr(x.Obj)}

		return n

	case *hir.Try:
		n := hirNode(&x.Node, "try")
		n.Children = []*Node{b.expr(x.X)}

		return n

	case *hir.StructLit:
		n := hirNode(&x.Node, "struct_lit")

		n.Name = symStr(x.Sym)
		for _, f := range x.Fields {
			fn := &Node{NodeID: int(x.ID), Kind: "field_init", Name: f.Name}
			fn.Children = []*Node{b.expr(f.Val)}
			n.Children = append(n.Children, fn)
		}

		return n

	case *hir.MapLit:
		n := hirNode(&x.Node, "map_lit")
		for i := range x.Keys {
			item := &Node{NodeID: int(x.ID), Kind: "item"}
			item.Children = []*Node{b.expr(x.Keys[i]), b.expr(x.Vals[i])}
			n.Children = append(n.Children, item)
		}

		return n

	case *hir.SliceLit:
		n := hirNode(&x.Node, "arr_lit")
		for _, el := range x.Elems {
			n.Children = append(n.Children, b.expr(el))
		}

		return n

	case *hir.Match:
		kind := "literal"

		switch x.Kind {
		case types.MatchEnum:
			kind = "enum"
		case types.MatchResult:
			kind = "result"
		case types.MatchOption:
			kind = "option"
		}

		n := hirNode(&x.Node, "match")
		n.Name = kind

		n.Children = []*Node{b.expr(x.Scrut)}
		for _, arm := range x.Arms {
			an := &Node{NodeID: int(x.ID), Kind: "arm"}
			an.Children = []*Node{b.pat(arm.Pat), b.expr(arm.Body)}
			n.Children = append(n.Children, an)
		}

		return n

	case *hir.FnLit:
		n := hirNode(&x.Node, "fnlit")
		for _, p := range x.Params {
			pn := hirNode(&x.Node, "param")
			pn.Name = p.Name
			pn.Type = typeStr(p.Typ)
			n.Children = append(n.Children, pn)
		}

		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n
	}

	return &Node{Kind: "unknown"}
}

func (b *hirBuilder) pat(p hir.Pat) *Node {
	switch x := p.(type) {
	case *hir.WildPat:
		n := &Node{Kind: "pattern_wild"}
		return n
	case *hir.BindPat:
		n := &Node{Kind: "pattern_bind", Name: x.Name, Type: typeStr(x.Typ)}
		return n
	case *hir.LitPat:
		n := &Node{Kind: "pattern_lit"}
		n.Children = []*Node{b.expr(x.Val)}

		return n
	case *hir.CtorPat:
		n := &Node{Kind: "pattern_ctor", Name: symStr(x.Ctor)}
		for _, sp := range x.Subs {
			n.Children = append(n.Children, b.pat(sp))
		}

		return n
	}

	return &Node{Kind: "pattern_wild"}
}

// —— IR ——

type irBuilder struct {
	id  int
	out []*Node
}

func (b *irBuilder) next() int {
	b.id++
	return b.id
}

// IRJSON 整个 IR 程序.
func IRJSON(p *ir.Prog) []*Node {
	b := &irBuilder{}
	for _, s := range p.Structs {
		n := &Node{NodeID: b.next(), Kind: "struct", Name: s.Name}
		for _, f := range s.Fields {
			n.Children = append(n.Children, &Node{NodeID: b.next(), Kind: "field", Name: f.Name, Type: typeStr(f.Typ)})
		}

		b.out = append(b.out, n)
	}

	for _, e := range p.Enums {
		n := &Node{NodeID: b.next(), Kind: "enum", Name: e.Name}
		for _, c := range e.Cases {
			n.Children = append(n.Children, &Node{NodeID: b.next(), Kind: "case", Name: c.Name})
		}

		b.out = append(b.out, n)
	}

	for _, g := range p.Globals {
		n := &Node{NodeID: b.next(), Kind: "let", Name: g.Local.Name, Type: typeStr(g.Local.Typ)}
		if g.Local.Mutable {
			n.Kind = "var"
		}

		n.Children = []*Node{b.expr(g.Init)}
		b.out = append(b.out, n)
	}

	for _, f := range p.Fns {
		b.out = append(b.out, b.fn(f))
	}

	return b.out
}

func (b *irBuilder) fn(f *ir.Fn) *Node {
	n := &Node{NodeID: b.next(), Kind: "fn", Name: f.Name, Type: typeStr(f.Ret)}
	if f.Recv != nil {
		n.Children = append(n.Children, &Node{NodeID: b.next(), Kind: "recv", Name: f.Recv.Name, Type: typeStr(f.Recv.Typ)})
	}

	for _, p := range f.Params {
		n.Children = append(n.Children, &Node{NodeID: b.next(), Kind: "param", Name: p.Name, Type: typeStr(p.Typ)})
	}

	n.Children = append(n.Children, b.stmts(f.Body)...)

	return n
}

func (b *irBuilder) stmts(list []ir.Stmt) []*Node {
	out := make([]*Node, 0, len(list))
	for _, st := range list {
		out = append(out, b.stmt(st))
	}

	return out
}

func (b *irBuilder) stmt(st ir.Stmt) *Node {
	switch x := st.(type) {
	case *ir.Let:
		n := &Node{NodeID: b.next(), Kind: "let", Name: x.Local.Name, Type: typeStr(x.Local.Typ)}
		if x.Local.Mutable {
			n.Kind = "var"
		}

		n.Children = []*Node{b.expr(x.Init)}

		return n
	case *ir.Assign:
		n := &Node{NodeID: b.next(), Kind: "assign", Name: string(x.Op)}
		n.Children = []*Node{b.expr(x.Target), b.expr(x.Val)}

		return n
	case *ir.Eval:
		n := &Node{NodeID: b.next(), Kind: "eval"}
		n.Children = []*Node{b.expr(x.X)}

		return n
	case *ir.Return:
		n := &Node{NodeID: b.next(), Kind: "return"}
		if x.Val != nil {
			n.Children = []*Node{b.expr(x.Val)}
		}

		return n
	case *ir.If:
		n := &Node{NodeID: b.next(), Kind: "if"}
		n.Children = []*Node{b.expr(x.Cond)}

		n.Children = append(n.Children, b.stmts(x.Then)...)
		if len(x.Else) > 0 {
			els := &Node{NodeID: b.next(), Kind: "else"}
			els.Children = b.stmts(x.Else)
			n.Children = append(n.Children, els)
		}

		return n
	case *ir.While:
		n := &Node{NodeID: b.next(), Kind: "while"}
		n.Children = []*Node{b.expr(x.Cond)}
		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n
	case *ir.ForIn:
		n := &Node{NodeID: b.next(), Kind: "forin", Name: x.Var.Name, Type: typeStr(x.Var.Typ)}
		n.Children = []*Node{b.expr(x.Iter)}
		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n
	case *ir.Break:
		return &Node{NodeID: b.next(), Kind: "break"}
	case *ir.Continue:
		return &Node{NodeID: b.next(), Kind: "continue"}
	case *ir.Spawn:
		n := &Node{NodeID: b.next(), Kind: "spawn", Name: symStr(x.Fn)}
		for _, a := range x.Args {
			n.Children = append(n.Children, b.expr(a))
		}

		return n
	case *ir.FnDef:
		return b.fn(x.Fn)
	}

	return &Node{NodeID: b.next(), Kind: "unknown"}
}

func (b *irBuilder) expr(e ir.Expr) *Node {
	switch x := e.(type) {
	case *ir.Const:
		n := &Node{NodeID: b.next(), Kind: "const", Type: typeStr(x.Typ())}
		switch x.Kind {
		case ir.KInt:
			n.Value = strconv.FormatInt(x.I64, 10)
		case ir.KFloat:
			n.Value = strconv.FormatFloat(x.F64, 'g', -1, 64)
		case ir.KStr:
			n.Value = x.S
		case ir.KBool:
			n.Value = strconv.FormatBool(x.B)
		}

		return n
	case *ir.LocalGet:
		return &Node{NodeID: b.next(), Kind: "local", Name: x.Local.Name, Type: typeStr(x.Typ())}
	case *ir.FnGet:
		return &Node{NodeID: b.next(), Kind: "fnref", Name: x.Name, Type: typeStr(x.Typ())}
	case *ir.BuiltinGet:
		return &Node{NodeID: b.next(), Kind: "builtin_ref", Name: x.Name}
	case *ir.Ctor:
		n := &Node{NodeID: b.next(), Kind: "ctor", Name: symStr(x.Sym), Type: typeStr(x.Typ())}
		for _, a := range x.Args {
			n.Children = append(n.Children, b.expr(a))
		}

		return n
	case *ir.Call:
		n := &Node{NodeID: b.next(), Kind: "call", Type: typeStr(x.Typ())}
		switch {
		case x.Fn != nil:
			n.Name = x.Fn.Name
		case x.Builtin != "":
			n.Kind = "builtin_call"
			n.Name = x.Builtin
		case x.Method != nil:
			n.Kind = "method"
			n.Name = x.Method.Name
			n.Children = append(n.Children, b.expr(x.Recv))
		default:
			n.Kind = "call_value"
			n.Children = append(n.Children, b.expr(x.Callee))
		}

		for _, a := range x.Args {
			n.Children = append(n.Children, b.expr(a))
		}

		return n
	case *ir.Unary:
		n := &Node{NodeID: b.next(), Kind: "unary", Name: string(x.Op), Type: typeStr(x.Typ())}
		n.Children = []*Node{b.expr(x.X)}

		return n
	case *ir.Binary:
		n := &Node{NodeID: b.next(), Kind: "binary", Name: string(x.Op), Type: typeStr(x.Typ())}
		n.Children = []*Node{b.expr(x.L), b.expr(x.R)}

		return n
	case *ir.Range:
		n := &Node{NodeID: b.next(), Kind: "range", Type: typeStr(x.Typ())}
		n.Children = []*Node{b.expr(x.Lo), b.expr(x.Hi)}

		return n
	case *ir.Index:
		n := &Node{NodeID: b.next(), Kind: "index", Type: typeStr(x.Typ())}
		n.Children = []*Node{b.expr(x.Obj), b.expr(x.Idx)}

		return n
	case *ir.Slice:
		n := &Node{NodeID: b.next(), Kind: "slice", Type: typeStr(x.Typ())}
		n.Children = []*Node{b.expr(x.Obj), b.expr(x.Lo), b.expr(x.Hi)}

		return n
	case *ir.Field:
		n := &Node{NodeID: b.next(), Kind: "field_access", Name: x.Name, Type: typeStr(x.Typ())}
		n.Children = []*Node{b.expr(x.Obj)}

		return n
	case *ir.Try:
		n := &Node{NodeID: b.next(), Kind: "try", Type: typeStr(x.Typ())}
		n.Children = []*Node{b.expr(x.X)}

		return n
	case *ir.StructLit:
		n := &Node{NodeID: b.next(), Kind: "struct_lit", Name: symStr(x.Struct), Type: typeStr(x.Typ())}
		for _, f := range x.Fields {
			fn := &Node{NodeID: b.next(), Kind: "field_init", Name: f.Name}
			fn.Children = []*Node{b.expr(f.Val)}
			n.Children = append(n.Children, fn)
		}

		return n
	case *ir.MapLit:
		n := &Node{NodeID: b.next(), Kind: "map_lit", Type: typeStr(x.Typ())}
		for i := range x.Keys {
			item := &Node{NodeID: b.next(), Kind: "item"}
			item.Children = []*Node{b.expr(x.Keys[i]), b.expr(x.Vals[i])}
			n.Children = append(n.Children, item)
		}

		return n
	case *ir.SliceLit:
		n := &Node{NodeID: b.next(), Kind: "arr_lit", Type: typeStr(x.Typ())}
		for _, el := range x.Elems {
			n.Children = append(n.Children, b.expr(el))
		}

		return n
	case *ir.Match:
		kind := "literal"

		switch x.Kind {
		case ir.MatchEnum:
			kind = "enum"
		case ir.MatchResult:
			kind = "result"
		case ir.MatchOption:
			kind = "option"
		}

		n := &Node{NodeID: b.next(), Kind: "match", Name: kind, Type: typeStr(x.Typ())}

		n.Children = []*Node{b.expr(x.Scrut)}
		for _, arm := range x.Arms {
			an := &Node{NodeID: b.next(), Kind: "arm"}
			an.Children = []*Node{b.pat(arm.Pat), b.expr(arm.Body)}
			n.Children = append(n.Children, an)
		}

		return n
	case *ir.FnLit:
		n := &Node{NodeID: b.next(), Kind: "fnlit", Type: typeStr(x.Typ())}
		for _, p := range x.Params {
			n.Children = append(n.Children, &Node{NodeID: b.next(), Kind: "param", Name: p.Name, Type: typeStr(p.Typ)})
		}

		n.Children = append(n.Children, b.stmts(x.Body)...)

		return n
	}

	return &Node{NodeID: b.next(), Kind: "unknown"}
}

func (b *irBuilder) pat(p ir.Pat) *Node {
	switch x := p.(type) {
	case *ir.WildPat:
		return &Node{NodeID: b.next(), Kind: "pattern_wild"}
	case *ir.BindPat:
		return &Node{NodeID: b.next(), Kind: "pattern_bind", Name: x.Name, Type: typeStr(x.Typ)}
	case *ir.LitPat:
		n := &Node{NodeID: b.next(), Kind: "pattern_lit"}
		n.Children = []*Node{b.expr(x.Val)}

		return n
	case *ir.CtorPat:
		n := &Node{NodeID: b.next(), Kind: "pattern_ctor", Name: symStr(x.Sym)}
		for _, sp := range x.Subs {
			n.Children = append(n.Children, b.pat(sp))
		}

		return n
	}

	return &Node{NodeID: b.next(), Kind: "pattern_wild"}
}

// spanOf 语法节点 → Span JSON（dump 辅助）.
func spanOf(sp syntax.Span) *Span { return SpanJSON(sp) }
