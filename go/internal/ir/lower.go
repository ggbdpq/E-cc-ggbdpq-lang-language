package ir

// Lower：HIR → Core IR。机械降级——局部槽位化、迭代协议归类、元数据剥离。
// 前置条件：HIR 构建成功（semantic/types 无诊断）。

import (
	"lang-go/internal/hir"
	"lang-go/internal/semantic"
	"lang-go/internal/types"
)

// Lower 降级整个程序
func Lower(p *hir.Prog) *Prog {
	l := &lowerer{out: &Prog{}, scopes: &scope{vars: map[string]*Local{}}}
	for _, s := range p.Structs {
		sd := &StructDecl{Name: s.Name, TypeParams: s.TypeParams}
		for _, f := range s.Fields {
			sd.Fields = append(sd.Fields, FieldDecl{Name: f.Name, Typ: f.Typ})
		}
		l.out.Structs = append(l.out.Structs, sd)
	}
	for _, e := range p.Enums {
		ed := &EnumDecl{Name: e.Name, TypeParams: e.TypeParams}
		for _, c := range e.Cases {
			ed.Cases = append(ed.Cases, Case{Name: c.Name, Payloads: c.Payloads})
		}
		l.out.Enums = append(l.out.Enums, ed)
	}
	for _, g := range p.Globals {
		lt := l.lowerLet(g)
		l.out.Globals = append(l.out.Globals, lt)
	}
	for _, f := range p.Fns {
		fn := l.lowerFn(f)
		if f == p.Main {
			l.out.Main = fn
		}
		l.out.Fns = append(l.out.Fns, fn)
	}
	return l.out
}

type scope struct {
	vars   map[string]*Local
	parent *scope
}

func (s *scope) lookup(name string) *Local {
	for c := s; c != nil; c = c.parent {
		if v, ok := c.vars[name]; ok {
			return v
		}
	}
	return nil
}

type lowerer struct {
	out    *Prog
	scopes *scope
	locals []*Local // 当前 fn 的体局部（声明序）
	inFn   bool     // 函数体下降期间才收集 locals
}

func (l *lowerer) define(v *Local) {
	l.scopes.vars[v.Name] = v
	if l.inFn {
		l.locals = append(l.locals, v)
	}
}

func (l *lowerer) push() { l.scopes = &scope{vars: map[string]*Local{}, parent: l.scopes} }
func (l *lowerer) pop()  { l.scopes = l.scopes.parent }

func (l *lowerer) local(hn *hir.Node, name string, mutable bool, t *types.Type) *Local {
	if t == nil {
		t = types.Unknown
	}
	var sym *semantic.Symbol
	if hn != nil {
		sym = hn.Sym
	}
	return &Local{Name: name, Mutable: mutable, Typ: t, Sym: sym}
}

func (l *lowerer) lowerFn(hf *hir.Fn) *Fn {
	savedScopes, savedLocals, savedInFn := l.scopes, l.locals, l.inFn
	l.scopes = &scope{vars: map[string]*Local{}}
	l.locals = nil
	l.inFn = true
	fn := &Fn{
		Name:       hf.Name,
		Sym:        hf.Sym,
		TypeParams: hf.TypeParams,
		Ret:        hf.Ret,
		Extern:     hf.Extern,
		ExternHost: hf.ExternHost,
	}
	// 参数/接收者局部不带 fn 节点的符号（Sym=nil）：
	// 后端按名引用，错误关联 fn 符号会把参数误当 fn 值绑定
	if hf.Recv != nil {
		recv := l.local(nil, hf.Recv.Name, false, hf.Recv.Typ)
		l.scopes.vars[recv.Name] = recv
		fn.Recv = recv
	}
	for _, p := range hf.Params {
		pl := l.local(nil, p.Name, false, p.Typ)
		l.scopes.vars[p.Name] = pl
		fn.Params = append(fn.Params, pl)
	}
	fn.Body = l.lowerStmts(hf.Body)
	fn.Locals = l.locals
	l.scopes, l.locals, l.inFn = savedScopes, savedLocals, savedInFn
	return fn
}

func (l *lowerer) lowerLet(hl *hir.Let) *Let {
	init := l.lowerExpr(hl.Init)
	name := hl.Name
	// 绑定类型 = Let 节点类型（checker 已把注解并入；无注解时为 init 类型）
	t := hl.Typ
	if t == nil || t.Kind == types.KUnknown {
		t = hir.Typ(hl.Init)
	}
	v := l.local(&hl.Node, name, hl.Mutable, t)
	if name != "_" {
		l.define(v)
	} else {
		v.Name = "_"
	}
	return &Let{Local: v, Init: init}
}

func (l *lowerer) lowerStmts(list []hir.Stmt) []Stmt {
	var out []Stmt
	for _, s := range list {
		out = append(out, l.lowerStmt(s))
	}
	return out
}

func (l *lowerer) lowerStmt(s hir.Stmt) Stmt {
	switch st := s.(type) {
	case *hir.Let:
		return l.lowerLet(st)

	case *hir.Assign:
		return &Assign{
			Target: l.lowerExpr(st.Target),
			Op:     hirOp(st.Op),
			Val:    l.lowerExpr(st.RHS),
		}

	case *hir.Eval:
		return &Eval{X: l.lowerExpr(st.X)}

	case *hir.Return:
		r := &Return{}
		if st.Val != nil {
			r.Val = l.lowerExpr(st.Val)
		}
		return r

	case *hir.If:
		return &If{
			Cond: l.lowerExpr(st.Cond),
			Then: l.lowerBlock(st.Then),
			Else: l.lowerBlock(st.Else),
		}

	case *hir.While:
		return &While{
			Cond: l.lowerExpr(st.Cond),
			Body: l.lowerBlock(st.Body),
		}

	case *hir.ForIn:
		l.push()
		v := l.local(&st.Node, st.Var, false, st.VarType)
		if st.Var != "_" {
			l.scopes.vars[st.Var] = v
		}
		fn := &ForIn{
			Var:  v,
			Kind: iterKind(hir.Typ(st.Iter)),
			Iter: l.lowerExpr(st.Iter),
			Body: l.lowerStmts(st.Body),
		}
		l.pop()
		return fn

	case *hir.Break:
		return &Break{}
	case *hir.Continue:
		return &Continue{}

	case *hir.Spawn:
		sp := &Spawn{Fn: st.Fn}
		for _, a := range st.Args {
			sp.Args = append(sp.Args, l.lowerExpr(a))
		}
		return sp

	case *hir.FnStmt:
		saved := l.saveFnCtx()
		fn := l.lowerFn(st.Fn)
		l.restoreFnCtx(saved)
		return &FnDef{Fn: fn}
	}
	return nil
}

func (l *lowerer) lowerBlock(list []hir.Stmt) []Stmt {
	l.push()
	out := l.lowerStmts(list)
	l.pop()
	return out
}

// fnCtx 保存/恢复函数级状态（嵌套 fn 有自己的局部表）
type fnCtx struct {
	scopes *scope
	locals []*Local
	inFn   bool
}

func (l *lowerer) saveFnCtx() fnCtx { return fnCtx{l.scopes, l.locals, l.inFn} }
func (l *lowerer) restoreFnCtx(c fnCtx) {
	l.scopes, l.locals, l.inFn = c.scopes, c.locals, c.inFn
}

func iterKind(t *types.Type) IterKind {
	if t == nil {
		return IterUnknown
	}
	switch t.Kind {
	case types.KSlice:
		return IterSlice
	case types.KMap:
		return IterMap
	case types.KString:
		return IterString
	case types.KRange:
		return IterRange
	}
	return IterUnknown
}

func hirOp(k interface{ String() string }) Op {
	return Op(k.String())
}

// —— 表达式 ——

func (l *lowerer) lowerExpr(e hir.Expr) Expr {
	t := hir.Typ(e)
	switch x := e.(type) {
	case *hir.Lit:
		c := &Const{typ: *t}
		switch x.Kind {
		case hir.LitInt:
			c.Kind = KInt
			c.I64 = x.I64
		case hir.LitFloat:
			c.Kind = KFloat
			c.F64 = x.F64
		case hir.LitStr:
			c.Kind = KStr
			c.S = x.S
		case hir.LitBool:
			c.Kind = KBool
			c.B = x.B
		}
		return c

	case *hir.LocalRef:
		if v := l.scopes.lookup(x.Name); v != nil {
			return &LocalGet{Local: v, typ: *t}
		}
		// 全局（跨 fn 引用）：降级为按名槽位
		return &LocalGet{Local: &Local{Name: x.Name, Typ: t, Sym: x.Sym}, typ: *t}

	case *hir.FnRef:
		return &FnGet{Name: x.Name, Sym: x.Sym, typ: *t}

	case *hir.BuiltinRef:
		return &BuiltinGet{Name: x.Name, typ: *t}

	case *hir.Ctor:
		n := &Ctor{Sym: x.CtorSym, typ: *t}
		for _, a := range x.Args {
			n.Args = append(n.Args, l.lowerExpr(a))
		}
		return n

	case *hir.Call:
		n := &Call{typ: *t}
		switch {
		case x.Fn != nil:
			n.Fn = x.Fn
		case x.Builtin != "":
			n.Builtin = x.Builtin
		default:
			n.Callee = l.lowerExpr(x.Callee)
		}
		for _, a := range x.Args {
			n.Args = append(n.Args, l.lowerExpr(a))
		}
		return n

	case *hir.MethodCall:
		n := &Call{typ: *t}
		if x.Method != nil {
			n.Method = x.Method
			n.Recv = l.lowerExpr(x.Recv)
		} else {
			// 字段函数值调用：callee = 字段读取
			n.Callee = l.lowerExpr(&hir.Field{Node: x.Node, Obj: x.Recv, Name: x.Field})
		}
		for _, a := range x.Args {
			n.Args = append(n.Args, l.lowerExpr(a))
		}
		return n

	case *hir.Unary:
		return &Unary{Op: hirOp(x.Op), X: l.lowerExpr(x.X), typ: *t}

	case *hir.Binary:
		return &Binary{Op: hirOp(x.Op), L: l.lowerExpr(x.L), R: l.lowerExpr(x.R), typ: *t}

	case *hir.AndThen:
		return &Binary{Op: OpAndAnd, L: l.lowerExpr(x.L), R: l.lowerExpr(x.R), typ: *t}
	case *hir.OrElse:
		return &Binary{Op: OpOrOr, L: l.lowerExpr(x.L), R: l.lowerExpr(x.R), typ: *t}

	case *hir.Range:
		return &Range{Lo: l.lowerExpr(x.Lo), Hi: l.lowerExpr(x.Hi), typ: *t}

	case *hir.Index:
		return &Index{Obj: l.lowerExpr(x.Obj), Idx: l.lowerExpr(x.Idx), typ: *t}

	case *hir.Slice:
		return &Slice{Obj: l.lowerExpr(x.Obj), Lo: l.lowerExpr(x.Lo), Hi: l.lowerExpr(x.Hi), typ: *t}

	case *hir.Field:
		return &Field{Obj: l.lowerExpr(x.Obj), Name: x.Name, typ: *t}

	case *hir.Try:
		return &Try{X: l.lowerExpr(x.X), typ: *t}

	case *hir.StructLit:
		n := &StructLit{Struct: x.Sym, typ: *t}
		for _, f := range x.Fields {
			n.Fields = append(n.Fields, FieldValue{Name: f.Name, Val: l.lowerExpr(f.Val)})
		}
		return n

	case *hir.MapLit:
		n := &MapLit{typ: *t}
		for i := range x.Keys {
			n.Keys = append(n.Keys, l.lowerExpr(x.Keys[i]))
			n.Vals = append(n.Vals, l.lowerExpr(x.Vals[i]))
		}
		return n

	case *hir.SliceLit:
		n := &SliceLit{typ: *t}
		for _, el := range x.Elems {
			n.Elems = append(n.Elems, l.lowerExpr(el))
		}
		return n

	case *hir.Match:
		n := &Match{Scrut: l.lowerExpr(x.Scrut), Kind: MatchKind(x.Kind), typ: *t}
		for _, arm := range x.Arms {
			l.push()
			irArm := Arm{Pat: l.lowerPat(arm.Pat), Body: l.lowerExpr(arm.Body)}
			l.pop()
			n.Arms = append(n.Arms, irArm)
		}
		return n

	case *hir.FnLit:
		saved := l.saveFnCtx()
		// 闭包作用域继承外层链（VALUE-CAPTURE-004 按引用捕获）：
		// 捕获变量解析到外层的同一 *Local 槽位指针，捕获关系显式保留在 IR 中，
		// 不再依赖各后端按名字重建词法作用域（v0.9 语义收口）
		l.push()
		l.locals = nil
		l.inFn = true
		n := &FnLit{Ret: x.Ret, typ: *t}
		for _, p := range x.Params {
			pl := l.local(&x.Node, p.Name, false, p.Typ)
			l.scopes.vars[p.Name] = pl
			n.Params = append(n.Params, pl)
		}
		n.Body = l.lowerStmts(x.Body)
		n.Locals = l.locals
		l.restoreFnCtx(saved)
		return n
	}
	return nil
}

func (l *lowerer) lowerPat(p hir.Pat) Pat {
	switch x := p.(type) {
	case *hir.WildPat:
		return &WildPat{}
	case *hir.BindPat:
		v := l.local(nil, x.Name, false, x.Typ)
		if x.Name != "_" {
			l.scopes.vars[x.Name] = v
		}
		return &BindPat{Name: x.Name, Typ: x.Typ, Local: v}
	case *hir.LitPat:
		return &LitPat{Val: l.lowerExpr(x.Val)}
	case *hir.CtorPat:
		n := &CtorPat{Sym: x.Ctor}
		for _, sp := range x.Subs {
			n.Subs = append(n.Subs, l.lowerPat(sp))
		}
		return n
	}
	return &WildPat{}
}
