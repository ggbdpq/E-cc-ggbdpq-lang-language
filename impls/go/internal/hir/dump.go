package hir

// Dump 把 HIR 渲染为缩进 S-表达式文本（snapshot 测试与 v0.4 dump hir 协议共用）。
// 稳定性：格式变更视为协议变更，须同步 snapshot。

import (
	"fmt"
	"strconv"
	"strings"

	"lang-go/internal/semantic"
	"lang-go/internal/types"
)

// Dump 渲染 HIR.
func Dump(p *Prog) string {
	d := &dumper{}
	d.w("prog")

	for _, s := range p.Structs {
		d.dumpStruct(s, 1)
	}

	for _, e := range p.Enums {
		d.dumpEnum(e, 1)
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

func symTag(s *semantic.Symbol) string {
	if s == nil {
		return ""
	}

	return fmt.Sprintf(" sym#%d", int(s.ID))
}

func tt(t *types.Type) string {
	if t == nil {
		return "?"
	}

	return t.String()
}

func (d *dumper) dumpStruct(s *StructDecl, ind int) {
	pad := strings.Repeat("  ", ind)

	tp := ""
	if len(s.TypeParams) > 0 {
		tp = "[" + strings.Join(s.TypeParams, ", ") + "]"
	}

	d.w(pad + fmt.Sprintf("struct %s%s %s", s.Name, tp, symTag(s.Sym)))

	for _, f := range s.Fields {
		d.w(pad + "  field " + f.Name + " @" + tt(f.Typ))
	}
}

func (d *dumper) dumpEnum(e *EnumDecl, ind int) {
	pad := strings.Repeat("  ", ind)

	tp := ""
	if len(e.TypeParams) > 0 {
		tp = "[" + strings.Join(e.TypeParams, ", ") + "]"
	}

	d.w(pad + fmt.Sprintf("enum %s%s %s", e.Name, tp, symTag(e.Sym)))

	for _, c := range e.Cases {
		var ps []string
		for _, p := range c.Payloads {
			ps = append(ps, "@"+tt(p))
		}

		d.w(pad + "  case " + c.Name + "(" + strings.Join(ps, ", ") + ")" + symTag(c.Sym))
	}
}

func (d *dumper) fnHeader(f *Fn, ind int) {
	pad := strings.Repeat("  ", ind)

	tp := ""
	if len(f.TypeParams) > 0 {
		tp = "[" + strings.Join(f.TypeParams, ", ") + "]"
	}

	ret := ""
	if f.Ret != nil {
		ret = " @" + tt(f.Ret)
	}

	head := "fn "
	if f.Recv != nil {
		head += "(" + f.Recv.Name + " @" + tt(f.Recv.Typ) + ") "
	}

	var ps []string
	for _, p := range f.Params {
		ps = append(ps, p.Name+" @"+tt(p.Typ))
	}

	d.w(pad + fmt.Sprintf("%s%s%s(%s)%s %s", head, f.Name, tp, strings.Join(ps, ", "), ret, symTag(f.Sym)))

	if f.Extern {
		d.w(pad + "  extern " + strconv.Quote(f.ExternHost))
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
		kw := "let"
		if st.Mutable {
			kw = "var"
		}

		d.w(pad + "(" + kw + " " + st.Name + " @" + tt(Typ(st.Init)) + symTag(st.Sym) + ")")
		d.expr(st.Init, ind+1)
	case *Assign:
		op := opStr(st.Op)
		d.w(pad + "(assign " + op + " @" + tt(Typ(st.RHS)) + ")")
		d.expr(st.Target, ind+1)
		d.expr(st.RHS, ind+1)
	case *Eval:
		d.w(pad + "(eval)")
		d.expr(st.X, ind+1)
	case *Return:
		if st.Val == nil {
			d.w(pad + "(return)")
			return
		}

		d.w(pad + "(return @" + tt(Typ(st.Val)) + ")")
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
		d.w(pad + "(for " + st.Var + " @" + tt(st.VarType) + symTag(st.Sym) + ")")
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
	case *FnStmt:
		d.fnHeader(st.Fn, ind)
		d.stmts(st.Fn.Body, ind+1)
	}
}

func opStr(k interface{ String() string }) string { return k.String() }

func (d *dumper) expr(e Expr, ind int) {
	pad := strings.Repeat("  ", ind)

	switch x := e.(type) {
	case *Lit:
		var v string

		switch x.Kind {
		case LitInt:
			v = strconv.FormatInt(x.I64, 10)
		case LitFloat:
			v = strconv.FormatFloat(x.F64, 'g', -1, 64)
		case LitStr:
			v = strconv.Quote(x.S)
		case LitBool:
			v = strconv.FormatBool(x.B)
		}

		d.w(pad + "(lit " + v + " @" + tt(Typ(x)) + ")")
	case *LocalRef:
		d.w(pad + "(local " + x.Name + symTag(x.Sym) + " @" + tt(Typ(x)) + ")")
	case *FnRef:
		d.w(pad + "(fnref " + x.Name + symTag(x.Sym) + " @" + tt(Typ(x)) + ")")
	case *BuiltinRef:
		d.w(pad + "(builtin-ref " + x.Name + " @" + tt(Typ(x)) + ")")
	case *Ctor:
		d.w(pad + "(ctor " + x.CtorSym.Name + symTag(x.CtorSym) + " @" + tt(Typ(x)) + ")")

		for _, a := range x.Args {
			d.expr(a, ind+1)
		}
	case *Call:
		switch {
		case x.Fn != nil:
			d.w(pad + "(call " + x.Fn.Name + symTag(x.Fn) + " @" + tt(Typ(x)) + ")")
		case x.Builtin != "":
			d.w(pad + "(builtin " + x.Builtin + " @" + tt(Typ(x)) + ")")
		default:
			d.w(pad + "(callv @" + tt(Typ(x)) + ")")
			d.expr(x.Callee, ind+1)
		}

		for _, a := range x.Args {
			d.expr(a, ind+1)
		}
	case *MethodCall:
		if x.Method != nil {
			d.w(pad + "(method " + x.Method.Name + symTag(x.Method) + " @" + tt(Typ(x)) + ")")
		} else {
			d.w(pad + "(callfield ." + x.Field + " @" + tt(Typ(x)) + ")")
		}

		d.expr(x.Recv, ind+1)

		for _, a := range x.Args {
			d.expr(a, ind+1)
		}
	case *Unary:
		d.w(pad + "(unary " + x.Op.String() + " @" + tt(Typ(x)) + ")")
		d.expr(x.X, ind+1)
	case *Binary:
		d.w(pad + "(binary " + x.Op.String() + " @" + tt(Typ(x)) + ")")
		d.expr(x.L, ind+1)
		d.expr(x.R, ind+1)
	case *AndThen:
		d.w(pad + "(and @" + tt(Typ(x)) + ")")
		d.expr(x.L, ind+1)
		d.expr(x.R, ind+1)
	case *OrElse:
		d.w(pad + "(or @" + tt(Typ(x)) + ")")
		d.expr(x.L, ind+1)
		d.expr(x.R, ind+1)
	case *Range:
		d.w(pad + "(range @" + tt(Typ(x)) + ")")
		d.expr(x.Lo, ind+1)
		d.expr(x.Hi, ind+1)
	case *Index:
		d.w(pad + "(index @" + tt(Typ(x)) + ")")
		d.expr(x.Obj, ind+1)
		d.expr(x.Idx, ind+1)
	case *Slice:
		d.w(pad + "(slice @" + tt(Typ(x)) + ")")
		d.expr(x.Obj, ind+1)
		d.expr(x.Lo, ind+1)
		d.expr(x.Hi, ind+1)
	case *Field:
		d.w(pad + "(field ." + x.Name + " @" + tt(Typ(x)) + ")")
		d.expr(x.Obj, ind+1)
	case *Try:
		d.w(pad + "(try @" + tt(Typ(x)) + ")")
		d.expr(x.X, ind+1)
	case *StructLit:
		name := ""
		if x.Sym != nil {
			name = x.Sym.Name
		}

		d.w(pad + "(struct " + name + symTag(x.Sym) + " @" + tt(Typ(x)) + ")")

		for _, f := range x.Fields {
			d.w(pad + "  ." + f.Name)
			d.expr(f.Val, ind+1)
		}
	case *MapLit:
		d.w(pad + "(map @" + tt(Typ(x)) + ")")

		for i := range x.Keys {
			d.expr(x.Keys[i], ind+1)
			d.expr(x.Vals[i], ind+1)
		}
	case *SliceLit:
		d.w(pad + "(arr @" + tt(Typ(x)) + ")")

		for _, el := range x.Elems {
			d.expr(el, ind+1)
		}
	case *Match:
		kind := "literal"

		switch x.Kind {
		case 1:
			kind = "enum"
		case 2:
			kind = "result"
		case 3:
			kind = "option"
		}

		d.w(pad + "(match " + kind + " @" + tt(Typ(x)) + ")")
		d.expr(x.Scrut, ind+1)

		for _, arm := range x.Arms {
			d.w(pad + "  arm @" + tt(Typ(arm.Body)))
			d.pat(arm.Pat, ind+2)
			d.expr(arm.Body, ind+2)
		}
	case *FnLit:
		var ps []string
		for _, p := range x.Params {
			ps = append(ps, p.Name+" @"+tt(p.Typ))
		}

		ret := ""
		if x.Ret != nil {
			ret = " @" + tt(x.Ret)
		}

		d.w(pad + "(fnlit(" + strings.Join(ps, ", ") + ")" + ret + ")")
		d.stmts(x.Body, ind+1)
	}
}

func (d *dumper) pat(p Pat, ind int) {
	pad := strings.Repeat("  ", ind)

	switch pt := p.(type) {
	case *WildPat:
		d.w(pad + "_")
	case *BindPat:
		d.w(pad + "(bind " + pt.Name + " @" + tt(pt.Typ) + ")")
	case *LitPat:
		d.w(pad + "(litpat)")
		d.expr(pt.Val, ind+1)
	case *CtorPat:
		name := ""
		if pt.Ctor != nil {
			name = pt.Ctor.Name
		}

		d.w(pad + "(ctorpat " + name + symTag(pt.Ctor) + ")")

		for _, sp := range pt.Subs {
			d.pat(sp, ind+1)
		}
	}
}
