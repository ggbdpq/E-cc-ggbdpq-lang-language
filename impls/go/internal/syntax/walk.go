package syntax

// Walk：AST 统一遍历器（文件归属盖章、dump ast JSON 共用）。
// 回调序：父节点先于子节点；nil 回调跳过。

// WalkStmts 遍历语句序列
func WalkStmts(list []Stmt, s func(Stmt), e func(Expr), pat func(Pat)) {
	for _, st := range list {
		WalkStmt(st, s, e, pat)
	}
}

// WalkStmt 遍历语句及其子树
func WalkStmt(st Stmt, s func(Stmt), e func(Expr), pat func(Pat)) {
	if s != nil {
		s(st)
	}
	switch x := st.(type) {
	case *LetStmt:
		WalkExpr(x.Init, s, e, pat)
	case *AssignStmt:
		WalkExpr(x.Target, s, e, pat)
		WalkExpr(x.Val, s, e, pat)
	case *ExprStmt:
		WalkExpr(x.X, s, e, pat)
	case *ReturnStmt:
		if x.Val != nil {
			WalkExpr(x.Val, s, e, pat)
		}
	case *IfStmt:
		WalkExpr(x.Cond, s, e, pat)
		WalkStmts(x.Then, s, e, pat)
		WalkStmts(x.Else, s, e, pat)
	case *WhileStmt:
		WalkExpr(x.Cond, s, e, pat)
		WalkStmts(x.Body, s, e, pat)
	case *ForInStmt:
		WalkExpr(x.Iter, s, e, pat)
		WalkStmts(x.Body, s, e, pat)
	case *GoStmt:
		WalkExpr(x.Call, s, e, pat)
	case *FnDecl:
		WalkStmts(x.Body, s, e, pat)
	case *ExternDecl:
		for _, fn := range x.Fns {
			WalkStmt(fn, s, e, pat)
		}
	}
}

// WalkExpr 遍历表达式及其子树
func WalkExpr(ex Expr, s func(Stmt), e func(Expr), pat func(Pat)) {
	if e != nil {
		e(ex)
	}
	switch x := ex.(type) {
	case *IntLit, *FloatLit, *StrLit, *BoolLit, *Ident:
	case *Unary:
		WalkExpr(x.X, s, e, pat)
	case *Binary:
		WalkExpr(x.L, s, e, pat)
		WalkExpr(x.R, s, e, pat)
	case *RangeEx:
		WalkExpr(x.Lo, s, e, pat)
		WalkExpr(x.Hi, s, e, pat)
	case *CallEx:
		WalkExpr(x.Callee, s, e, pat)
		for _, a := range x.Args {
			WalkExpr(a, s, e, pat)
		}
	case *IndexEx:
		WalkExpr(x.Obj, s, e, pat)
		WalkExpr(x.Idx, s, e, pat)
	case *SliceEx:
		WalkExpr(x.Obj, s, e, pat)
		WalkExpr(x.Lo, s, e, pat)
		WalkExpr(x.Hi, s, e, pat)
	case *FieldEx:
		WalkExpr(x.Obj, s, e, pat)
	case *TryEx:
		WalkExpr(x.X, s, e, pat)
	case *StructLit:
		for _, f := range x.Fields {
			WalkExpr(f.Val, s, e, pat)
		}
	case *MapLit:
		for _, it := range x.Items {
			WalkExpr(it.Key, s, e, pat)
			WalkExpr(it.Val, s, e, pat)
		}
	case *ArrLit:
		for _, el := range x.Items {
			WalkExpr(el, s, e, pat)
		}
	case *MatchEx:
		WalkExpr(x.Scrut, s, e, pat)
		for _, arm := range x.Arms {
			WalkPat(arm.Pat, s, e, pat)
			WalkExpr(arm.Body, s, e, pat)
		}
	case *FnLit:
		WalkStmts(x.Body, s, e, pat)
	}
}

// WalkPat 遍历模式及其子模式
func WalkPat(p Pat, s func(Stmt), e func(Expr), pat func(Pat)) {
	if pat != nil {
		pat(p)
	}
	switch x := p.(type) {
	case *LitPat:
		WalkExpr(x.Val, s, e, pat)
	case *CasePat:
		for _, sp := range x.Subs {
			WalkPat(sp, s, e, pat)
		}
	}
}
