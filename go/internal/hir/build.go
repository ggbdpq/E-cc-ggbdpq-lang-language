package hir

// Builder：AST + resolver 产物 + checker 产物 → Typed HIR。
// 构建前提：semantic/types 两阶段均无诊断（调用方负责）。

import (
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
)

type builder struct {
	res   *semantic.Result
	ty    *types.Result
	next  NodeID
	ctors map[string][]*semantic.Symbol // enum 名 → case 符号
	out   *Prog
}

// Build 构建 HIR
func Build(prog []syntax.Stmt, res *semantic.Result, ty *types.Result) *Prog {
	b := &builder{res: res, ty: ty, ctors: map[string][]*semantic.Symbol{}, out: &Prog{}}
	for _, s := range res.Symbols {
		if s.Kind == semantic.KCtor && !s.BuiltinMark {
			b.ctors[s.StructName] = append(b.ctors[s.StructName], s)
		}
	}
	b.collectDecls(prog)
	return b.out
}

func (b *builder) node(sp syntax.Span, line int, sym *semantic.Symbol, t *types.Type) Node {
	b.next++
	if t == nil {
		t = types.Unknown
	}
	return Node{ID: b.next, Line: line, Sp: sp, Sym: sym, Typ: t}
}

func (b *builder) anno(t syntax.TypeEx, tparams []string) *types.Type {
	if t == nil {
		return types.Unknown
	}
	return types.ResolveAnno(b.res, t, tparams)
}

// —— 顶层收集 ——

func (b *builder) collectDecls(prog []syntax.Stmt) {
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.StructDecl:
			b.out.Structs = append(b.out.Structs, b.structDecl(d))
		case *syntax.EnumDecl:
			b.out.Enums = append(b.out.Enums, b.enumDecl(d))
		}
	}
	for _, s := range prog {
		if d, ok := s.(*syntax.LetStmt); ok {
			b.out.Globals = append(b.out.Globals, b.let(d))
		}
	}
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.FnDecl:
			if d.Recv != nil {
				continue
			}
			fn := b.fnDecl(d, b.res.DeclSyms[d])
			if d.Name == "main" && b.out.Main == nil {
				b.out.Main = fn
			}
			b.out.Fns = append(b.out.Fns, fn)
		case *syntax.ExternDecl:
			for _, f := range d.Fns {
				fn := b.fnDecl(f, b.externSym(d.Host, f))
				fn.Extern = true
				fn.ExternHost = d.Host
				fn.Body = nil
				b.out.Fns = append(b.out.Fns, fn)
			}
		}
	}
	for _, s := range prog {
		if d, ok := s.(*syntax.FnDecl); ok && d.Recv != nil {
			if st := b.recvStructSym(d); st != nil {
				b.out.Fns = append(b.out.Fns, b.fnDecl(d, st.Methods[d.Name]))
			}
		}
	}
}

func (b *builder) externSym(host string, f *syntax.FnDecl) *semantic.Symbol {
	for _, s := range b.res.Symbols {
		if s.Kind == semantic.KFn && s.Extern && s.Name == f.Name {
			return s
		}
	}
	return nil
}

func (b *builder) recvStructSym(d *syntax.FnDecl) *semantic.Symbol {
	nt, ok := d.Recv.Typ.(*syntax.NamedType)
	if !ok {
		return nil
	}
	if s := b.res.Builtin[nt.Name]; s != nil && s.Kind == semantic.KStruct {
		return s
	}
	return b.typeSymByName(nt.Name)
}

func (b *builder) structDecl(d *syntax.StructDecl) *StructDecl {
	sym := b.typeSymByName(d.Name)
	sd := &StructDecl{
		Node:       b.node(d.Sp, d.Line, sym, nil),
		Name:       d.Name,
		TypeParams: d.TypeParams,
	}
	for _, f := range d.Fields {
		sd.Fields = append(sd.Fields, FieldDecl{
			Name: f.Name,
			Typ:  b.anno(f.Typ, d.TypeParams),
		})
	}
	return sd
}

func (b *builder) enumDecl(d *syntax.EnumDecl) *EnumDecl {
	sym := b.typeSymByName(d.Name)
	ed := &EnumDecl{
		Node:       b.node(d.Sp, d.Line, sym, nil),
		Name:       d.Name,
		TypeParams: d.TypeParams,
	}
	for _, c := range d.Cases {
		cs := Case{Name: c.Name}
		for _, s := range b.ctors[d.Name] {
			if s.Name == c.Name {
				cs.Sym = s
			}
		}
		for _, pt := range c.Payloads {
			cs.Payloads = append(cs.Payloads, b.anno(pt, d.TypeParams))
		}
		ed.Cases = append(ed.Cases, cs)
	}
	return ed
}

func (b *builder) let(d *syntax.LetStmt) *Let {
	sym := b.res.DeclSyms[d]
	return &Let{
		Node:    b.node(d.Sp, d.Line, sym, b.symType(sym)),
		Name:    d.Name,
		Mutable: d.Mutable,
		Init:    b.expr(d.Init),
	}
}

func (b *builder) fnDecl(d *syntax.FnDecl, sym *semantic.Symbol) *Fn {
	var ret *types.Type
	if d.Ret != nil {
		ret = b.anno(d.Ret, d.TypeParams)
	}
	fn := &Fn{
		Node:       b.node(d.Sp, d.Line, sym, ret),
		Name:       d.Name,
		Sym:        sym,
		TypeParams: d.TypeParams,
		Ret:        ret,
	}
	if d.Recv != nil {
		fn.Recv = &Param{Name: d.Recv.Name, Typ: b.anno(d.Recv.Typ, nil)}
	}
	for _, p := range d.Params {
		fn.Params = append(fn.Params, Param{Name: p.Name, Typ: b.anno(p.Typ, d.TypeParams)})
	}
	for _, s := range d.Body {
		fn.Body = append(fn.Body, b.stmt(s))
	}
	return fn
}

// —— 语句 ——

func (b *builder) stmts(list []syntax.Stmt) []Stmt {
	var out []Stmt
	for _, s := range list {
		out = append(out, b.stmt(s))
	}
	return out
}

func (b *builder) stmt(s syntax.Stmt) Stmt {
	switch st := s.(type) {
	case *syntax.LetStmt:
		return b.let(st)

	case *syntax.AssignStmt:
		return b.assign(st)

	case *syntax.ExprStmt:
		x := b.expr(st.X)
		return &Eval{Node: b.node(st.Sp, st.Line, nil, types.Unknown), X: x}

	case *syntax.ReturnStmt:
		r := &Return{Node: b.node(st.Sp, st.Line, nil, types.Unknown)}
		if st.Val != nil {
			r.Val = b.expr(st.Val)
		}
		return r

	case *syntax.IfStmt:
		n := &If{
			Node: b.node(st.Sp, st.Line, nil, types.Unknown),
			Cond: b.expr(st.Cond),
			Then: b.stmts(st.Then),
			Else: b.stmts(st.Else),
		}
		return n

	case *syntax.WhileStmt:
		return &While{
			Node: b.node(st.Sp, st.Line, nil, types.Unknown),
			Cond: b.expr(st.Cond),
			Body: b.stmts(st.Body),
		}

	case *syntax.ForInStmt:
		sym := b.res.DeclSyms[st]
		return &ForIn{
			Node:    b.node(st.Sp, st.Line, sym, types.Unknown),
			Var:     st.Var,
			VarType: b.symType(sym),
			Iter:    b.expr(st.Iter),
			Body:    b.stmts(st.Body),
		}

	case *syntax.BreakStmt:
		return &Break{Node: b.node(st.Sp, st.Line, nil, types.Unknown)}
	case *syntax.ContinueStmt:
		return &Continue{Node: b.node(st.Sp, st.Line, nil, types.Unknown)}

	case *syntax.GoStmt:
		sp := &Spawn{Node: b.node(st.Sp, st.Line, nil, types.Unknown)}
		if id, ok := st.Call.Callee.(*syntax.Ident); ok {
			sp.Fn = b.res.Refs[id]
		}
		for _, a := range st.Call.Args {
			sp.Args = append(sp.Args, b.expr(a))
		}
		return sp

	case *syntax.FnDecl:
		return &FnStmt{Node: b.node(st.Sp, st.Line, nil, types.Unknown), Fn: b.fnDecl(st, b.res.DeclSyms[st])}
	}
	return nil
}

func (b *builder) assign(st *syntax.AssignStmt) *Assign {
	n := &Assign{
		Node:   b.node(st.Sp, st.Line, nil, types.Unknown),
		Op:     st.Op,
		Target: b.assignTarget(st.Target),
		RHS:    b.expr(st.Val),
	}
	return n
}

func (b *builder) assignTarget(t syntax.Expr) Expr {
	switch x := t.(type) {
	case *syntax.Ident:
		sym := b.res.Refs[x]
		return &LocalRef{Node: b.node(x.Sp, x.Line, sym, b.symType(sym)), Name: x.Name}
	case *syntax.FieldEx:
		return &Field{Node: b.node(x.Sp, x.Line, nil, types.Unknown), Obj: b.expr(x.Obj), Name: x.Name}
	case *syntax.IndexEx:
		return &Index{Node: b.node(x.Sp, x.Line, nil, types.Unknown), Obj: b.expr(x.Obj), Idx: b.expr(x.Idx)}
	}
	return nil
}

// —— 表达式 ——

func (b *builder) expr(e syntax.Expr) Expr {
	switch x := e.(type) {
	case *syntax.IntLit:
		return &Lit{Node: b.node(x.Sp, x.Line, nil, types.Int), Kind: LitInt, I64: x.Val}
	case *syntax.FloatLit:
		return &Lit{Node: b.node(x.Sp, x.Line, nil, types.Float), Kind: LitFloat, F64: x.Val}
	case *syntax.StrLit:
		return &Lit{Node: b.node(x.Sp, x.Line, nil, types.String), Kind: LitStr, S: x.Val}
	case *syntax.BoolLit:
		return &Lit{Node: b.node(x.Sp, x.Line, nil, types.Bool), Kind: LitBool, B: x.Val}

	case *syntax.Ident:
		return b.ident(x)

	case *syntax.Unary:
		return &Unary{
			Node: b.node(x.Sp, x.Line, nil, b.exprType(x)),
			Op:   x.Op,
			X:    b.expr(x.X),
		}

	case *syntax.Binary:
		if x.Op == syntax.And {
			return &AndThen{Node: b.node(x.Sp, x.Line, nil, types.Bool), L: b.expr(x.L), R: b.expr(x.R)}
		}
		if x.Op == syntax.Or {
			return &OrElse{Node: b.node(x.Sp, x.Line, nil, types.Bool), L: b.expr(x.L), R: b.expr(x.R)}
		}
		return &Binary{
			Node: b.node(x.Sp, x.Line, nil, b.exprType(x)),
			Op:   x.Op,
			L:    b.expr(x.L),
			R:    b.expr(x.R),
		}

	case *syntax.RangeEx:
		return &Range{
			Node: b.node(x.Sp, x.Line, nil, types.Range),
			Lo:   b.expr(x.Lo),
			Hi:   b.expr(x.Hi),
		}

	case *syntax.CallEx:
		return b.call(x)

	case *syntax.IndexEx:
		return &Index{
			Node: b.node(x.Sp, x.Line, nil, b.exprType(x)),
			Obj:  b.expr(x.Obj),
			Idx:  b.expr(x.Idx),
		}

	case *syntax.SliceEx:
		return &Slice{
			Node: b.node(x.Sp, x.Line, nil, b.exprType(x)),
			Obj:  b.expr(x.Obj),
			Lo:   b.expr(x.Lo),
			Hi:   b.expr(x.Hi),
		}

	case *syntax.FieldEx:
		return &Field{
			Node: b.node(x.Sp, x.Line, nil, b.exprType(x)),
			Obj:  b.expr(x.Obj),
			Name: x.Name,
		}

	case *syntax.TryEx:
		return &Try{Node: b.node(x.Sp, x.Line, nil, b.exprType(x)), X: b.expr(x.X)}

	case *syntax.StructLit:
		n := &StructLit{Node: b.node(x.Sp, x.Line, b.res.Refs[x], b.exprType(x))}
		for _, f := range x.Fields {
			n.Fields = append(n.Fields, FieldValue{Name: f.Name, Val: b.expr(f.Val)})
		}
		return n

	case *syntax.MapLit:
		n := &MapLit{Node: b.node(x.Sp, x.Line, nil, b.exprType(x))}
		for _, it := range x.Items {
			n.Keys = append(n.Keys, b.expr(it.Key))
			n.Vals = append(n.Vals, b.expr(it.Val))
		}
		return n

	case *syntax.ArrLit:
		n := &SliceLit{Node: b.node(x.Sp, x.Line, nil, b.exprType(x))}
		for _, el := range x.Items {
			n.Elems = append(n.Elems, b.expr(el))
		}
		return n

	case *syntax.MatchEx:
		return b.match(x)

	case *syntax.FnLit:
		n := &FnLit{
			Node: b.node(x.Sp, x.Line, nil, b.exprType(x)),
			Ret:  b.anno(x.Ret, x.TypeParams),
		}
		for _, p := range x.Params {
			n.Params = append(n.Params, Param{Name: p.Name, Typ: b.anno(p.Typ, x.TypeParams)})
		}
		n.Body = b.stmts(x.Body)
		return n
	}
	return nil
}

// ident 标识符 → LocalRef / FnRef / BuiltinRef / 裸构造器
func (b *builder) ident(x *syntax.Ident) Expr {
	t := b.exprType(x)
	if sym := b.res.Refs[x]; sym != nil {
		switch sym.Kind {
		case semantic.KFn:
			return &FnRef{Node: b.node(x.Sp, x.Line, sym, t), Name: x.Name}
		case semantic.KCtor:
			return &Ctor{Node: b.node(x.Sp, x.Line, sym, t), CtorSym: sym}
		default:
			return &LocalRef{Node: b.node(x.Sp, x.Line, sym, t), Name: x.Name}
		}
	}
	if b.res.Builtin[x.Name] != nil {
		return &BuiltinRef{Node: b.node(x.Sp, x.Line, nil, t), Name: x.Name}
	}
	// 未定义名（checker 已报，构建不该到这里）→ LocalRef 占位
	return &LocalRef{Node: b.node(x.Sp, x.Line, nil, t), Name: x.Name}
}

// call 调用脱糖：构造器 / 内建 / 用户 fn / 方法 / 函数值
func (b *builder) call(x *syntax.CallEx) Expr {
	t := b.exprType(x)
	if id, ok := x.Callee.(*syntax.Ident); ok {
		sym := b.res.Refs[id]
		switch {
		case sym != nil && sym.Kind == semantic.KCtor:
			n := &Ctor{Node: b.node(x.Sp, x.Line, sym, t), CtorSym: sym}
			for _, a := range x.Args {
				n.Args = append(n.Args, b.expr(a))
			}
			return n
		case sym == nil && b.res.Builtin[id.Name] != nil:
			n := &Call{Node: b.node(x.Sp, x.Line, nil, t), Builtin: id.Name}
			for _, a := range x.Args {
				n.Args = append(n.Args, b.expr(a))
			}
			return n
		case sym != nil && sym.Kind == semantic.KFn:
			n := &Call{Node: b.node(x.Sp, x.Line, sym, t), Fn: sym}
			for _, a := range x.Args {
				n.Args = append(n.Args, b.expr(a))
			}
			return n
		}
	}
	if fe, ok := x.Callee.(*syntax.FieldEx); ok {
		recv := b.expr(fe.Obj)
		if st := structOf(Typ(recv)); st != nil && st.Methods[fe.Name] != nil {
			n := &MethodCall{Node: b.node(x.Sp, x.Line, st.Methods[fe.Name], t), Recv: recv, Method: st.Methods[fe.Name]}
			for _, a := range x.Args {
				n.Args = append(n.Args, b.expr(a))
			}
			return n
		}
		// http.get：预载服务值 → 内建
		if id2, ok := fe.Obj.(*syntax.Ident); ok && id2.Name == "http" && fe.Name == "get" && b.res.Builtin["http"] != nil {
			n := &Call{Node: b.node(x.Sp, x.Line, nil, t), Builtin: "http.get"}
			for _, a := range x.Args {
				n.Args = append(n.Args, b.expr(a))
			}
			return n
		}
		// 字段持有的函数值
		n := &MethodCall{Node: b.node(x.Sp, x.Line, nil, t), Recv: recv, Field: fe.Name}
		for _, a := range x.Args {
			n.Args = append(n.Args, b.expr(a))
		}
		return n
	}
	// 表达式 callee（函数值）
	n := &Call{Node: b.node(x.Sp, x.Line, nil, t), Callee: b.expr(x.Callee)}
	for _, a := range x.Args {
		n.Args = append(n.Args, b.expr(a))
	}
	return n
}

// structOf 取类型对应的 struct 符号（引用自动解引用）
func structOf(t *types.Type) *semantic.Symbol {
	for t != nil && t.Kind == types.KRef {
		t = t.Elem
	}
	if t != nil && t.Kind == types.KStruct {
		return t.Sym
	}
	return nil
}

func (b *builder) match(x *syntax.MatchEx) Expr {
	n := &Match{
		Node:  b.node(x.Sp, x.Line, nil, b.exprType(x)),
		Kind:  b.ty.MatchKind[x],
		Scrut: b.expr(x.Scrut),
	}
	for _, arm := range x.Arms {
		n.Arms = append(n.Arms, Arm{Line: arm.Line, Pat: b.pat(arm.Pat), Body: b.expr(arm.Body)})
	}
	return n
}

func (b *builder) pat(p syntax.Pat) Pat {
	switch pt := p.(type) {
	case *syntax.WildPat:
		return &WildPat{Line: pt.Line}
	case *syntax.BindPat:
		return &BindPat{Line: pt.Line, Name: pt.Name, Typ: b.patType(pt)}
	case *syntax.LitPat:
		return &LitPat{Line: pt.Line, Val: b.expr(pt.Val)}
	case *syntax.CasePat:
		n := &CtorPat{Line: pt.Line, Ctor: b.res.PatRefs[pt]}
		for _, sp := range pt.Subs {
			n.Subs = append(n.Subs, b.pat(sp))
		}
		return n
	}
	return &WildPat{}
}

// —— 查表助手 ——

func (b *builder) exprType(e syntax.Expr) *types.Type {
	if t := b.ty.ExprTy[e]; t != nil {
		return t
	}
	return types.Unknown
}

func (b *builder) patType(p syntax.Pat) *types.Type {
	if t := b.ty.PatTy[p]; t != nil {
		return t
	}
	return types.Unknown
}

func (b *builder) symType(s *semantic.Symbol) *types.Type {
	if s == nil {
		return types.Unknown
	}
	if t := b.ty.SymTy[s.ID]; t != nil {
		return t
	}
	return types.Unknown
}

func (b *builder) typeSymByName(name string) *semantic.Symbol {
	if s := b.res.Builtin[name]; s != nil && s.Kind == semantic.KStruct {
		return s
	}
	for _, s := range b.res.Symbols {
		if (s.Kind == semantic.KStruct || s.Kind == semantic.KEnum) && s.Name == name {
			return s
		}
	}
	return nil
}
