package hir

// Walk：HIR 的统一遍历器（测试校验、IR 降级共用）。
// 回调序：父节点先于子节点；nil 回调跳过。

// WalkProg 遍历整个程序.
func WalkProg(p *Prog, s func(Stmt), e func(Expr), pat func(Pat)) {
	for _, g := range p.Globals {
		WalkStmt(g, s, e, pat)
	}

	for _, f := range p.Fns {
		WalkStmts(f.Body, s, e, pat)
	}
}

// WalkStmts 遍历语句序列.
func WalkStmts(list []Stmt, s func(Stmt), e func(Expr), pat func(Pat)) {
	for _, st := range list {
		WalkStmt(st, s, e, pat)
	}
}

// WalkStmt 遍历单条语句及其子树.
func WalkStmt(st Stmt, s func(Stmt), e func(Expr), pat func(Pat)) {
	if s != nil {
		s(st)
	}

	switch x := st.(type) {
	case *Let:
		WalkExpr(x.Init, s, e, pat)
	case *Assign:
		WalkExpr(x.Target, s, e, pat)
		WalkExpr(x.RHS, s, e, pat)
	case *Eval:
		WalkExpr(x.X, s, e, pat)
	case *Return:
		if x.Val != nil {
			WalkExpr(x.Val, s, e, pat)
		}
	case *If:
		WalkExpr(x.Cond, s, e, pat)
		WalkStmts(x.Then, s, e, pat)
		WalkStmts(x.Else, s, e, pat)
	case *While:
		WalkExpr(x.Cond, s, e, pat)
		WalkStmts(x.Body, s, e, pat)
	case *ForIn:
		WalkExpr(x.Iter, s, e, pat)
		WalkStmts(x.Body, s, e, pat)
	case *Spawn:
		for _, a := range x.Args {
			WalkExpr(a, s, e, pat)
		}
	case *FnStmt:
		WalkStmts(x.Fn.Body, s, e, pat)
	}
}

// WalkExpr 遍历表达式及其子树.
func WalkExpr(ex Expr, s func(Stmt), e func(Expr), pat func(Pat)) {
	if e != nil {
		e(ex)
	}

	switch x := ex.(type) {
	case *Lit, *LocalRef, *FnRef, *BuiltinRef:
	case *Ctor:
		for _, a := range x.Args {
			WalkExpr(a, s, e, pat)
		}
	case *Call:
		if x.Callee != nil {
			WalkExpr(x.Callee, s, e, pat)
		}

		for _, a := range x.Args {
			WalkExpr(a, s, e, pat)
		}
	case *MethodCall:
		WalkExpr(x.Recv, s, e, pat)

		for _, a := range x.Args {
			WalkExpr(a, s, e, pat)
		}
	case *Unary:
		WalkExpr(x.X, s, e, pat)
	case *Binary:
		WalkExpr(x.L, s, e, pat)
		WalkExpr(x.R, s, e, pat)
	case *AndThen:
		WalkExpr(x.L, s, e, pat)
		WalkExpr(x.R, s, e, pat)
	case *OrElse:
		WalkExpr(x.L, s, e, pat)
		WalkExpr(x.R, s, e, pat)
	case *Range:
		WalkExpr(x.Lo, s, e, pat)
		WalkExpr(x.Hi, s, e, pat)
	case *Index:
		WalkExpr(x.Obj, s, e, pat)
		WalkExpr(x.Idx, s, e, pat)
	case *Slice:
		WalkExpr(x.Obj, s, e, pat)
		WalkExpr(x.Lo, s, e, pat)
		WalkExpr(x.Hi, s, e, pat)
	case *Field:
		WalkExpr(x.Obj, s, e, pat)
	case *Try:
		WalkExpr(x.X, s, e, pat)
	case *StructLit:
		for _, f := range x.Fields {
			WalkExpr(f.Val, s, e, pat)
		}
	case *MapLit:
		for i := range x.Keys {
			WalkExpr(x.Keys[i], s, e, pat)
			WalkExpr(x.Vals[i], s, e, pat)
		}
	case *SliceLit:
		for _, el := range x.Elems {
			WalkExpr(el, s, e, pat)
		}
	case *Match:
		WalkExpr(x.Scrut, s, e, pat)

		for _, arm := range x.Arms {
			WalkPat(arm.Pat, s, e, pat)
			WalkExpr(arm.Body, s, e, pat)
		}
	case *FnLit:
		WalkStmts(x.Body, s, e, pat)
	}
}

// WalkPat 遍历模式及其子模式.
func WalkPat(p Pat, s func(Stmt), e func(Expr), pat func(Pat)) {
	if pat != nil {
		pat(p)
	}

	switch x := p.(type) {
	case *LitPat:
		WalkExpr(x.Val, s, e, pat)
	case *CtorPat:
		for _, sp := range x.Subs {
			WalkPat(sp, s, e, pat)
		}
	}
}
