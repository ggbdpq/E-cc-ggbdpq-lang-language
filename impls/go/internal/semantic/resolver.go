package semantic

// Resolver：两遍扫描——先收集顶层声明（struct/enum → fn/method/global），
// 再做作用域绑定。查名顺序与解释器运行时一致：作用域栈 → 构造器 → 内建。

import (
	"fmt"

	"lang-go/internal/syntax"
)

// 内建函数名单（与 interp/gen/genjs 三方现行实现一致的唯一清单）.
var builtinNames = []string{
	"println", "print", "len", "str", "int", "float", "keys", "values",
	"has", "push", "assert", "err", "bytes", "char",
	"jsonParse", "jsonStringify", "isNone", "makeChan", "send", "recv",
}

// preludeStructNames 预载 struct（interp prelude 的 error/HttpResp）.
var preludeStructNames = []string{"error", "HttpResp"}

type resolver struct {
	res        *Result
	nextID     SymbolID
	scopes     []map[string]*Symbol // 值命名空间：fn/绑定/参数
	typeScopes []map[string]*Symbol // 类型命名空间：struct/enum（与值空间分离，struct 名不可作值）
	ctorScope  map[string]*Symbol   // 构造器全局命名空间（含预载）
	loopDepth  int
	fnDepth    int
}

// Resolve 解析整个程序（装载器合并后的语句序列）。
// 有诊断时仍返回 Result（部分产物可用），调用方以 HasErrors 判定。
//
// 四遍结构：全局绑定依序解析（与 interp 顺序执行、→Go var 初始化的顺序语义一致，
// 前向引用全局即 E2002）；fn/方法体最后走——函数体对全局的可见性与声明顺序无关。
func Resolve(prog []syntax.Stmt) *Result {
	r := &resolver{
		res: &Result{
			Refs:     map[syntax.Expr]*Symbol{},
			PatRefs:  map[syntax.Pat]*Symbol{},
			DeclSyms: map[syntax.Stmt]*Symbol{},
			FnParams: map[SymbolID][]*Symbol{},
			Builtin:  map[string]*Symbol{},
		},
		ctorScope: map[string]*Symbol{},
	}
	r.prelude()
	r.scopes = []map[string]*Symbol{{}}
	r.typeScopes = []map[string]*Symbol{{}}
	r.collectTypes(prog)
	r.collectFns(prog)
	r.bindGlobals(prog)
	r.bindFns(prog)
	r.popScope()

	return r.res
}

// HasErrors 是否存在诊断.
func (res *Result) HasErrors() bool { return len(res.Diags) > 0 }

// Errs 诊断聚合为 error.
func (res *Result) Errs() error { return ErrorList(res.Diags) }

// Builtin 按名取内建符号（println/Some/error/http 等）

func (r *resolver) prelude() {
	for _, n := range builtinNames {
		r.res.Builtin[n] = r.newSym(&Symbol{Kind: KFn, Name: n, BuiltinMark: true})
	}

	for _, n := range preludeStructNames {
		r.res.Builtin[n] = r.newSym(&Symbol{Kind: KStruct, Name: n, BuiltinMark: true})
	}

	r.res.Builtin["http"] = r.newSym(&Symbol{Kind: KVar, Name: "http", BuiltinMark: true})

	// 预载构造器：Some/None/Ok/Err（与 interp prelude 的 case 序号一致）
	opt := "Option"

	res_ := "Result"
	for _, c := range []struct {
		name     string
		enum     string
		idx      int
		arity    int
		payloads []syntax.TypeEx
	}{
		{"Some", opt, 0, 1, []syntax.TypeEx{nil}},
		{"None", opt, 1, 0, nil},
		{"Ok", res_, 0, 1, []syntax.TypeEx{nil}},
		{"Err", res_, 1, 1, []syntax.TypeEx{nil}},
	} {
		r.ctorScope[c.name] = r.newSym(&Symbol{
			Kind: KCtor, Name: c.name, StructName: c.enum, CaseIndex: c.idx,
			Arity: c.arity, Payloads: c.payloads, BuiltinMark: true,
		})
	}
}

func (r *resolver) newSym(s *Symbol) *Symbol {
	r.nextID++
	s.ID = r.nextID
	r.res.Symbols = append(r.res.Symbols, s)

	return s
}

func (r *resolver) errf(line int, code, format string, args ...any) {
	r.res.Diags = append(r.res.Diags, Diagnostic{Code: code, Msg: fmt.Sprintf(format, args...), Line: line})
}

func (r *resolver) pushScope() {
	r.scopes = append(r.scopes, map[string]*Symbol{})
	r.typeScopes = append(r.typeScopes, map[string]*Symbol{})
}

func (r *resolver) popScope() {
	r.scopes = r.scopes[:len(r.scopes)-1]
	r.typeScopes = r.typeScopes[:len(r.typeScopes)-1]
}

func (r *resolver) define(s *Symbol) {
	top := r.scopes[len(r.scopes)-1]
	if old, dup := top[s.Name]; dup {
		_ = old

		r.errf(s.Line, E2001, "%s %s 在此作用域重复定义", kindName(s.Kind), s.Name)

		return
	}

	top[s.Name] = s
}

func (r *resolver) defineCtor(s *Symbol) {
	if old, dup := r.ctorScope[s.Name]; dup {
		r.errf(s.Line, E2003, "构造器 %s 重名（已被 %s 使用）", s.Name, ownerName(old))
		return
	}

	r.ctorScope[s.Name] = s
}

func ownerName(s *Symbol) string {
	if s.BuiltinMark {
		return "内建"
	}

	return s.StructName
}

func kindName(k Kind) string {
	switch k {
	case KFn:
		return "fn"
	case KMethod:
		return "方法"
	case KVar:
		return "绑定"
	case KParam:
		return "参数"
	case KStruct:
		return "struct"
	case KEnum:
		return "enum"
	case KCtor:
		return "构造器"
	}

	return "符号"
}

// lookupIdent 与 interp eval(Ident) 的查名顺序一致.
func (r *resolver) lookup(name string) *Symbol {
	for i := len(r.scopes) - 1; i >= 0; i-- {
		if s, ok := r.scopes[i][name]; ok {
			return s
		}
	}

	return nil
}

func (r *resolver) lookupCtor(name string) *Symbol { return r.ctorScope[name] }

// lookupType 类型命名空间查名（struct/enum；供 checker 解析注解）.
func (r *resolver) lookupType(name string) *Symbol {
	for i := len(r.typeScopes) - 1; i >= 0; i-- {
		if s, ok := r.typeScopes[i][name]; ok {
			return s
		}
	}

	return nil
}

// ———— 第一遍：收集 struct/enum ————

func (r *resolver) collectTypes(prog []syntax.Stmt) {
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.StructDecl:
			if _, ok := r.typeScopes[0][d.Name]; ok {
				r.errf(d.Line, E2001, "struct %s 重复定义", d.Name)
				continue
			}

			sym := r.newSym(&Symbol{Kind: KStruct, Name: d.Name, Line: d.Line, TypeParams: d.TypeParams, Decl: d})
			for _, f := range d.Fields {
				sym.Fields = append(sym.Fields, f.Name)
				sym.FieldTypes = append(sym.FieldTypes, f.Typ)
			}

			r.typeScopes[0][d.Name] = sym
		case *syntax.EnumDecl:
			if _, ok := r.typeScopes[0][d.Name]; ok {
				r.errf(d.Line, E2001, "enum %s 重复定义", d.Name)
				continue
			}

			sym := r.newSym(&Symbol{Kind: KEnum, Name: d.Name, Line: d.Line, TypeParams: d.TypeParams, Decl: d})

			r.typeScopes[0][d.Name] = sym
			for i, c := range d.Cases {
				if _, used := r.res.Builtin[c.Name]; used {
					r.errf(c.Line, E2006, "case %s 与内建名冲突", c.Name)
					continue
				}

				cs := r.newSym(&Symbol{
					Kind: KCtor, Name: c.Name, Line: c.Line,
					StructName: d.Name, CaseIndex: i,
					Arity: len(c.Payloads), Payloads: c.Payloads,
				})
				r.defineCtor(cs)
			}
		}
	}
}

// ———— 第二遍：收集 fn/extern/method 符号（体在第四遍走） ————

func (r *resolver) collectFns(prog []syntax.Stmt) {
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.FnDecl:
			if d.Recv != nil {
				r.collectMethod(d)
				continue
			}

			if old, dup := r.lookupTop(d.Name); dup {
				r.errf(d.Line, E2001, "fn %s 重复定义（先前定义于第 %d 行）", d.Name, old.Line)
				continue
			}

			r.scopes[0][d.Name] = r.fnSymbol(d)
		case *syntax.ExternDecl:
			for _, fn := range d.Fns {
				if old, dup := r.lookupTop(fn.Name); dup {
					r.errf(d.Line, E2001, "fn %s 重复定义（先前定义于第 %d 行）", fn.Name, old.Line)
					continue
				}

				sym := r.fnSymbol(fn)
				sym.Extern = true
				r.scopes[0][fn.Name] = sym
			}
		}
	}
}

func (r *resolver) lookupTop(name string) (*Symbol, bool) {
	s, ok := r.scopes[0][name]
	return s, ok
}

func (r *resolver) fnSymbol(d *syntax.FnDecl) *Symbol {
	sym := r.newSym(&Symbol{
		Kind: KFn, Name: d.Name, Line: d.Line, Decl: d,
		TypeEx: d.Ret, TypeParams: d.TypeParams, Arity: len(d.Params),
	})
	for _, p := range d.Params {
		sym.Params = append(sym.Params, p.Typ)
	}

	return sym
}

func (r *resolver) collectMethod(d *syntax.FnDecl) {
	nt, ok := d.Recv.Typ.(*syntax.NamedType)
	if !ok {
		r.errf(d.Line, E2004, "方法接收者须为具名 struct 类型")
		return
	}

	st, ok := r.typeScopes[0][nt.Name]
	if !ok || st.Kind != KStruct {
		r.errf(d.Line, E2004, "为未知 struct %s 定义方法", nt.Name)
		return
	}

	if _, dup := st.Methods[d.Name]; dup {
		r.errf(d.Line, E2001, "方法 %s 在 struct %s 上重复定义", d.Name, nt.Name)
		return
	}

	sym := r.newSym(&Symbol{
		Kind: KMethod, Name: d.Name, Line: d.Line, RecvName: nt.Name,
		TypeEx: d.Ret, Arity: len(d.Params),
	})
	for _, p := range d.Params {
		sym.Params = append(sym.Params, p.Typ)
	}

	if st.Methods == nil {
		st.Methods = map[string]*Symbol{}
	}

	st.Methods[d.Name] = sym
}

// ———— 第三遍：顶层绑定依序解析 ————

// bindGlobals 顶层 let/var 按源码顺序：先解析 init，再注册符号（前向引用即未定义）。
// 顶层游离语句（脚本模式）在此顺带走查以产出边界诊断（return/break 位置等）。
func (r *resolver) bindGlobals(prog []syntax.Stmt) {
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.LetStmt:
			r.expr(d.Init)

			if d.Name == "_" {
				continue
			}

			if old, dup := r.lookupTop(d.Name); dup {
				r.errf(d.Line, E2001, "绑定 %s 在此作用域重复定义（先前定义于第 %d 行）", d.Name, old.Line)
				continue
			}

			sym := r.newSym(&Symbol{
				Kind: KVar, Name: d.Name, Line: d.Line, Mutable: d.Mutable, TypeEx: d.Typ,
			})
			r.scopes[0][d.Name] = sym
			r.res.DeclSyms[d] = sym
		case *syntax.FnDecl, *syntax.StructDecl, *syntax.EnumDecl, *syntax.ExternDecl, *syntax.ImportStmt:
			// 声明在其余遍处理
		default:
			r.stmt(s)
		}
	}
}

// ———— 第四遍：函数体 ————

// bindFns 走 fn 与方法体（此时全部全局可见——与 interp 运行时/→Go 包级可见性一致）.
func (r *resolver) bindFns(prog []syntax.Stmt) {
	for _, s := range prog {
		if d, ok := s.(*syntax.FnDecl); ok && d.Recv == nil {
			sym := r.scopes[0][d.Name]
			if sym != nil {
				r.res.DeclSyms[d] = sym
			}

			r.fnScope(d, sym)
		}
	}

	for _, s := range prog {
		d, ok := s.(*syntax.FnDecl)
		if !ok || d.Recv == nil {
			continue
		}

		if nt, ok := d.Recv.Typ.(*syntax.NamedType); ok {
			if st, ok := r.typeScopes[0][nt.Name]; ok && st.Kind == KStruct {
				if sym := st.Methods[d.Name]; sym != nil {
					sym.Decl = d
					r.fnScope(d, sym)
				}
			}
		}
	}
}

func (r *resolver) stmt(s syntax.Stmt) {
	switch st := s.(type) {
	case *syntax.LetStmt:
		r.expr(st.Init)

		if st.Name == "_" {
			return
		}

		sym := r.newSym(&Symbol{Kind: KVar, Name: st.Name, Line: st.Line, Mutable: st.Mutable, TypeEx: st.Typ})
		r.define(sym)
		r.res.DeclSyms[st] = sym

	case *syntax.AssignStmt:
		if id, ok := st.Target.(*syntax.Ident); ok {
			sym := r.lookup(id.Name)
			if sym == nil {
				r.errf(id.Line, E2002, "未定义变量 %s", id.Name)
			} else if !sym.Mutable {
				r.errf(st.Line, E2005, "%s 由 let 绑定，不可重新赋值", id.Name)
			}

			r.res.Refs[id] = sym
		} else {
			r.assignTarget(st.Target)
		}

		r.expr(st.Val)

	case *syntax.ExprStmt:
		r.expr(st.X)

	case *syntax.ReturnStmt:
		if r.fnDepth == 0 {
			r.errf(st.Line, E2008, "return 出现在函数之外")
		}

		if st.Val != nil {
			r.expr(st.Val)
		}

	case *syntax.IfStmt:
		r.expr(st.Cond)
		r.block(st.Then)

		if st.Else != nil {
			r.block(st.Else)
		}

	case *syntax.WhileStmt:
		r.expr(st.Cond)
		r.loopDepth++
		r.block(st.Body)
		r.loopDepth--

	case *syntax.ForInStmt:
		r.expr(st.Iter)
		r.pushScope()

		if st.Var != "_" {
			sym := r.newSym(&Symbol{Kind: KVar, Name: st.Var, Line: st.Line})
			r.define(sym)
			r.res.DeclSyms[st] = sym
		}

		r.loopDepth++
		for _, b := range st.Body {
			r.stmt(b)
		}

		r.loopDepth--
		r.popScope()

	case *syntax.BreakStmt:
		if r.loopDepth == 0 {
			r.errf(st.Line, E2007, "break 出现在循环之外")
		}
	case *syntax.ContinueStmt:
		if r.loopDepth == 0 {
			r.errf(st.Line, E2007, "continue 出现在循环之外")
		}

	case *syntax.FnDecl:
		if st.Recv != nil {
			return // 方法体在 bindFns 统一走
		}
		// 嵌套 fn：定义进当前作用域
		sym := r.fnSymbol(st)
		r.define(sym)
		r.res.DeclSyms[st] = sym
		r.fnScope(st, sym)

	case *syntax.StructDecl:
		// 嵌套类型声明：E2010（interp 容忍属实现越界，转译目标无法表达）
		r.errf(st.Line, E2010, "嵌套类型声明不支持（struct %s 须在顶层声明）", st.Name)

		top := r.typeScopes[len(r.typeScopes)-1]
		if _, dup := top[st.Name]; !dup {
			sym := r.newSym(&Symbol{Kind: KStruct, Name: st.Name, Line: st.Line, TypeParams: st.TypeParams})
			for _, f := range st.Fields {
				sym.Fields = append(sym.Fields, f.Name)
				sym.FieldTypes = append(sym.FieldTypes, f.Typ)
			}

			top[st.Name] = sym
		}

	case *syntax.EnumDecl:
		r.errf(st.Line, E2010, "嵌套类型声明不支持（enum %s 须在顶层声明）", st.Name)

		top := r.typeScopes[len(r.typeScopes)-1]
		if _, dup := top[st.Name]; !dup {
			r.newSym(&Symbol{Kind: KEnum, Name: st.Name, Line: st.Line, TypeParams: st.TypeParams})
		}

	case *syntax.ExternDecl:
		for _, fn := range st.Fns {
			if r.lookup(fn.Name) == nil {
				sym := r.fnSymbol(fn)
				sym.Extern = true
				r.define(sym)
			}
		}

	case *syntax.GoStmt:
		id, ok := st.Call.Callee.(*syntax.Ident)
		if !ok {
			r.errf(st.Line, E2002, "go 的目标须是具名函数调用")
		} else if sym := r.lookup(id.Name); sym == nil || sym.Kind != KFn {
			r.errf(id.Line, E2002, "go 的目标须是顶层函数：%s", id.Name)
		} else {
			r.res.Refs[id] = sym
		}

		for _, a := range st.Call.Args {
			r.expr(a)
		}

	case *syntax.ImportStmt:
		// 装载器已展开
	}
}

// block 开新作用域走语句序列.
func (r *resolver) block(stmts []syntax.Stmt) {
	r.pushScope()

	for _, s := range stmts {
		r.stmt(s)
	}

	r.popScope()
}

// fnScope 函数体作用域：接收者 + 参数，fnDepth 保护 return。
// 参数符号按序记入 FnParams[fn 符号]（接收者在前），供 checker/HIR 对位取类型。
func (r *resolver) fnScope(d *syntax.FnDecl, sym *Symbol) {
	r.pushScope()

	var psyms []*Symbol

	if d.Recv != nil {
		ps := r.newSym(&Symbol{Kind: KParam, Name: d.Recv.Name, Line: d.Line, TypeEx: d.Recv.Typ})
		r.define(ps)
		psyms = append(psyms, ps)
	}

	for _, p := range d.Params {
		if p.Name == "_" {
			continue
		}

		ps := r.newSym(&Symbol{Kind: KParam, Name: p.Name, Line: d.Line, TypeEx: p.Typ})
		r.define(ps)
		psyms = append(psyms, ps)
	}

	if sym != nil {
		r.res.FnParams[sym.ID] = psyms
	}

	r.fnDepth++
	for _, s := range d.Body {
		r.stmt(s)
	}

	r.fnDepth--
	r.popScope()
}

// assignTarget 字段/索引赋值目标：只走子表达式（形状合法性由解析器 checkTarget 保证）.
func (r *resolver) assignTarget(t syntax.Expr) {
	switch x := t.(type) {
	case *syntax.FieldEx:
		r.expr(x.Obj)
	case *syntax.IndexEx:
		r.expr(x.Obj)
		r.expr(x.Idx)
	default:
		r.expr(t)
	}
}

func (r *resolver) expr(e syntax.Expr) {
	switch x := e.(type) {
	case *syntax.Ident:
		if sym := r.lookup(x.Name); sym != nil {
			r.res.Refs[x] = sym
			return
		}

		if sym := r.lookupCtor(x.Name); sym != nil {
			r.res.Refs[x] = sym
			return
		}

		if _, isBuiltin := r.res.Builtin[x.Name]; isBuiltin {
			return // 裸内建引用是否合法（如传函数值）由 checker 判定
		}

		r.errf(x.Line, E2002, "未定义 %s", x.Name)

	case *syntax.Unary:
		r.expr(x.X)
	case *syntax.Binary:
		r.expr(x.L)
		r.expr(x.R)
	case *syntax.RangeEx:
		r.expr(x.Lo)
		r.expr(x.Hi)
	case *syntax.CallEx:
		r.expr(x.Callee)

		for _, a := range x.Args {
			r.expr(a)
		}
	case *syntax.IndexEx:
		r.expr(x.Obj)
		r.expr(x.Idx)
	case *syntax.SliceEx:
		r.expr(x.Obj)
		r.expr(x.Lo)
		r.expr(x.Hi)
	case *syntax.FieldEx:
		r.expr(x.Obj)
	case *syntax.TryEx:
		r.expr(x.X)

	case *syntax.StructLit:
		if sym := r.lookupType(x.TypeName); sym != nil {
			r.res.Refs[x] = sym
		} else {
			r.errf(x.Line, E2002, "未知 struct %s", x.TypeName)
		}

		for _, f := range x.Fields {
			r.expr(f.Val)
		}

	case *syntax.MapLit:
		for _, item := range x.Items {
			r.expr(item.Key)
			r.expr(item.Val)
		}
	case *syntax.ArrLit:
		for _, el := range x.Items {
			r.expr(el)
		}

	case *syntax.MatchEx:
		r.expr(x.Scrut)

		for _, arm := range x.Arms {
			r.pushScope()
			r.pat(arm.Pat)
			r.expr(arm.Body)
			r.popScope()
		}

	case *syntax.FnLit:
		r.pushScope()

		for _, p := range x.Params {
			if p.Name != "_" {
				r.define(r.newSym(&Symbol{Kind: KParam, Name: p.Name, Line: x.Line, TypeEx: p.Typ}))
			}
		}

		r.fnDepth++
		for _, s := range x.Body {
			r.stmt(s)
		}

		r.fnDepth--
		r.popScope()
	}
}

func (r *resolver) pat(p syntax.Pat) {
	switch x := p.(type) {
	case *syntax.WildPat:
	case *syntax.BindPat:
		sym := r.newSym(&Symbol{Kind: KVar, Name: x.Name, Line: x.Line})
		r.define(sym)
		r.res.PatRefs[x] = sym
	case *syntax.LitPat:
		r.expr(x.Val)
	case *syntax.CasePat:
		sym := r.lookupCtor(x.Name)
		if sym == nil {
			r.errf(x.Line, E2002, "未知构造器 %s", x.Name)
		} else {
			r.res.PatRefs[x] = sym
		}

		for _, sp := range x.Subs {
			r.pat(sp)
		}
	}
}
