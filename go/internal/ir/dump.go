package ir

// Dump 渲染 Core IR（snapshot 测试锁定；v0.4 dump ir 协议复用）。

import (
	"strconv"
	"strings"

	"lang-go/internal/semantic"
	"lang-go/internal/types"
)

// Dump 渲染 IR
func Dump(p *Prog) string {
	d := &dumper{}
	d.w("prog")
	for _, s := range p.Structs {
		d.w("  struct " + s.Name)
		for _, f := range s.Fields {
			d.w("    field " + f.Name + " @" + tt(f.Typ))
		}
	}
	for _, e := range p.Enums {
		d.w("  enum " + e.Name)
		for _, c := range e.Cases {
			var ps []string
			for _, pt := range c.Payloads {
				ps = append(ps, "@"+tt(pt))
			}
			d.w("    case " + c.Name + "(" + strings.Join(ps, ", ") + ")")
		}
	}
	for _, g := range p.Globals {
		d.stmt(g, 1)
	}
	for _, f := range p.Fns {
		d.fnHeader(f, 1)
		d.stmts(f.Body, 2)
	}
	return d.b.String()
}

type dumper struct {
	b strings.Builder
}

func (d *dumper) w(line string) { d.b.WriteString(line + "\n") }

func tt(t *types.Type) string {
	if t == nil {
		return "?"
	}
	return t.String()
}

func symTag(s *semantic.Symbol) string {
	if s == nil {
		return ""
	}
	return "#" + strconv.Itoa(int(s.ID))
}

func (d *dumper) fnHeader(f *Fn, ind int) {
	pad := strings.Repeat("  ", ind)
	head := "fn "
	if f.Recv != nil {
		head += "(" + f.Recv.Name + " @" + tt(f.Recv.Typ) + ") "
	}
	var ps []string
	for _, p := range f.Params {
		ps = append(ps, p.Name+" @"+tt(p.Typ))
	}
	ret := ""
	if f.Ret != nil {
		ret = " @" + tt(f.Ret)
	}
	d.w(pad + head + f.Name + "(" + strings.Join(ps, ", ") + ")" + ret + symTag(f.Sym))
	if f.Extern {
		d.w(pad + "  extern " + strconv.Quote(f.ExternHost))
	}
	for _, v := range f.Locals {
		mut := ""
		if v.Mutable {
			mut = "mut "
		}
		d.w(pad + "  local " + mut + v.Name + " @" + tt(v.Typ))
	}
}

func (d *dumper) stmts(list []Stmt, ind int) {
	for _, s := range list {
		d.stmt(s, ind)
	}
}

func (d *dumper) stmt(s Stmt, ind int) {
	pad := strings.Repeat("  ", ind)
	switch st := s.(type) {
	case *Let:
		d.w(pad + "(let " + st.Local.Name + " @" + tt(st.Local.Typ) + ")")
		d.expr(st.Init, ind+1)
	case *Assign:
		d.w(pad + "(assign " + string(st.Op) + ")")
		d.expr(st.Target, ind+1)
		d.expr(st.Val, ind+1)
	case *Eval:
		d.w(pad + "(eval)")
		d.expr(st.X, ind+1)
	case *Return:
		if st.Val == nil {
			d.w(pad + "(return)")
			return
		}
		d.w(pad + "(return @" + tt(st.Val.Typ()) + ")")
		d.expr(st.Val, ind+1)
	case *If:
		d.w(pad + "(if)")
		d.expr(st.Cond, ind+1)
		d.stmts(st.Then, ind+1)
		if len(st.Else) > 0 {
			d.w(pad + "  else")
			d.stmts(st.Else, ind+1)
		}
	case *While:
		d.w(pad + "(while)")
		d.expr(st.Cond, ind+1)
		d.stmts(st.Body, ind+1)
	case *ForIn:
		kind := "unknown"
		switch st.Kind {
		case IterSlice:
			kind = "slice"
		case IterMap:
			kind = "map"
		case IterString:
			kind = "string"
		case IterRange:
			kind = "range"
		}
		d.w(pad + "(for " + st.Var.Name + " @" + tt(st.Var.Typ) + " iter:" + kind + ")")
		d.expr(st.Iter, ind+1)
		d.stmts(st.Body, ind+1)
	case *Break:
		d.w(pad + "break")
	case *Continue:
		d.w(pad + "continue")
	case *Spawn:
		name := ""
		if st.Fn != nil {
			name = st.Fn.Name
		}
		d.w(pad + "(go " + name + symTag(st.Fn) + ")")
		for _, a := range st.Args {
			d.expr(a, ind+1)
		}
	case *FnDef:
		d.fnHeader(st.Fn, ind)
		d.stmts(st.Fn.Body, ind+1)
	}
}

func (d *dumper) expr(e Expr, ind int) {
	pad := strings.Repeat("  ", ind)
	switch x := e.(type) {
	case *Const:
		var v string
		switch x.Kind {
		case KInt:
			v = strconv.FormatInt(x.I64, 10)
		case KFloat:
			v = strconv.FormatFloat(x.F64, 'g', -1, 64)
		case KStr:
			v = strconv.Quote(x.S)
		case KBool:
			v = strconv.FormatBool(x.B)
		}
		d.w(pad + "(const " + v + " @" + tt(x.Typ()) + ")")
	case *LocalGet:
		d.w(pad + "(get " + x.Local.Name + " @" + tt(x.Typ()) + ")")
	case *FnGet:
		d.w(pad + "(fnget " + x.Name + symTag(x.Sym) + " @" + tt(x.Typ()) + ")")
	case *BuiltinGet:
		d.w(pad + "(builtinget " + x.Name + " @" + tt(x.Typ()) + ")")
	case *Ctor:
		d.w(pad + "(ctor " + x.Sym.Name + symTag(x.Sym) + " @" + tt(x.Typ()) + ")")
		for _, a := range x.Args {
			d.expr(a, ind+1)
		}
	case *Call:
		switch {
		case x.Fn != nil:
			d.w(pad + "(call " + x.Fn.Name + symTag(x.Fn) + " @" + tt(x.Typ()) + ")")
		case x.Builtin != "":
			d.w(pad + "(builtin " + x.Builtin + " @" + tt(x.Typ()) + ")")
		case x.Method != nil:
			d.w(pad + "(method " + x.Method.Name + symTag(x.Method) + " @" + tt(x.Typ()) + ")")
			d.expr(x.Recv, ind+1)
		default:
			d.w(pad + "(callv @" + tt(x.Typ()) + ")")
			d.expr(x.Callee, ind+1)
		}
		for _, a := range x.Args {
			d.expr(a, ind+1)
		}
	case *Unary:
		d.w(pad + "(unary " + string(x.Op) + " @" + tt(x.Typ()) + ")")
		d.expr(x.X, ind+1)
	case *Binary:
		d.w(pad + "(binary " + string(x.Op) + " @" + tt(x.Typ()) + ")")
		d.expr(x.L, ind+1)
		d.expr(x.R, ind+1)
	case *Range:
		d.w(pad + "(range @" + tt(x.Typ()) + ")")
		d.expr(x.Lo, ind+1)
		d.expr(x.Hi, ind+1)
	case *Index:
		d.w(pad + "(index @" + tt(x.Typ()) + ")")
		d.expr(x.Obj, ind+1)
		d.expr(x.Idx, ind+1)
	case *Slice:
		d.w(pad + "(slice @" + tt(x.Typ()) + ")")
		d.expr(x.Obj, ind+1)
		d.expr(x.Lo, ind+1)
		d.expr(x.Hi, ind+1)
	case *Field:
		d.w(pad + "(field ." + x.Name + " @" + tt(x.Typ()) + ")")
		d.expr(x.Obj, ind+1)
	case *Try:
		d.w(pad + "(try @" + tt(x.Typ()) + ")")
		d.expr(x.X, ind+1)
	case *StructLit:
		name := ""
		if x.Struct != nil {
			name = x.Struct.Name
		}
		d.w(pad + "(struct " + name + " @" + tt(x.Typ()) + ")")
		for _, f := range x.Fields {
			d.w(pad + "  ." + f.Name)
			d.expr(f.Val, ind+1)
		}
	case *MapLit:
		d.w(pad + "(map @" + tt(x.Typ()) + ")")
		for i := range x.Keys {
			d.expr(x.Keys[i], ind+1)
			d.expr(x.Vals[i], ind+1)
		}
	case *SliceLit:
		d.w(pad + "(arr @" + tt(x.Typ()) + ")")
		for _, el := range x.Elems {
			d.expr(el, ind+1)
		}
	case *Match:
		kind := "literal"
		switch x.Kind {
		case MatchEnum:
			kind = "enum"
		case MatchResult:
			kind = "result"
		case MatchOption:
			kind = "option"
		}
		d.w(pad + "(match " + kind + " @" + tt(x.Typ()) + ")")
		d.expr(x.Scrut, ind+1)
		for _, arm := range x.Arms {
			d.w(pad + "  arm @" + tt(arm.Body.Typ()))
			d.pat(arm.Pat, ind+2)
			d.expr(arm.Body, ind+2)
		}
	case *FnLit:
		var ps []string
		for _, p := range x.Params {
			ps = append(ps, p.Name+" @"+tt(p.Typ))
		}
		d.w(pad + "(fnlit(" + strings.Join(ps, ", ") + ") @" + tt(x.Typ()) + ")")
		for _, v := range x.Locals {
			d.w(pad + "  local " + v.Name + " @" + tt(v.Typ))
		}
		d.stmts(x.Body, ind+1)
	}
}

func (d *dumper) pat(p Pat, ind int) {
	pad := strings.Repeat("  ", ind)
	switch x := p.(type) {
	case *WildPat:
		d.w(pad + "_")
	case *BindPat:
		d.w(pad + "(bind " + x.Name + " @" + tt(x.Typ) + ")")
	case *LitPat:
		d.w(pad + "(litpat)")
		d.expr(x.Val, ind+1)
	case *CtorPat:
		d.w(pad + "(ctorpat " + x.Sym.Name + symTag(x.Sym) + ")")
		for _, sp := range x.Subs {
			d.pat(sp, ind+1)
		}
	}
}
