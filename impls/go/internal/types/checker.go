package types

// Checker：对 AST 做语义类型检查，产出每表达式的类型（HIR/IR 与后端共用）。
// 遍历结构与 semantic resolver 同构；名字解析结果复用 semantic.Result
// （Refs/PatRefs/DeclSyms），绑定类型按名字作用域栈跟踪。
//
// 本检查器收编了 →Go/→JS 转译器的全部前置约束（参数注解、? 位置与约束、
// Result 函数 return 形态、match 分类与穷尽性），后端不再重复实现。

import (
	"fmt"

	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
)

// MatchKind match 的语义分类（checker 判定，HIR/IR/后端直接消费结论）.
type MatchKind uint8

const (
	MatchLiteral MatchKind = iota
	MatchEnum
	MatchResult
	MatchOption
)

// Result 检查产物.
type Result struct {
	ExprTy    map[syntax.Expr]*Type       // 表达式 → 类型
	PatTy     map[syntax.Pat]*Type        // 绑定模式 → 捕获类型
	SymTy     map[semantic.SymbolID]*Type // 绑定/参数符号 → 类型（HIR/协议用）
	FnTy      map[semantic.SymbolID]*Type // fn/方法符号 → 函数类型
	MatchKind map[*syntax.MatchEx]MatchKind
	Diags     []Diagnostic
}

// HasErrors 是否有诊断.
func (res *Result) HasErrors() bool { return len(res.Diags) > 0 }

// Errs 诊断聚合.
func (res *Result) Errs() error { return ErrorList(res.Diags) }

type checker struct {
	res    *semantic.Result
	out    *Result
	scopes []map[string]*Type // 绑定类型作用域栈（名字 → 类型）
	fnRet  *Type              // 当前函数返回注解（nil = 无注解）
	tps    map[string]bool    // 当前函数的泛型参数名集（注解解析用）
	inFn   bool
}

// Check 运行类型检查（res 需先通过 semantic.Resolve）.
func Check(prog []syntax.Stmt, res *semantic.Result) *Result {
	c := &checker{
		res: res,
		out: &Result{
			ExprTy:    map[syntax.Expr]*Type{},
			PatTy:     map[syntax.Pat]*Type{},
			SymTy:     map[semantic.SymbolID]*Type{},
			FnTy:      map[semantic.SymbolID]*Type{},
			MatchKind: map[*syntax.MatchEx]MatchKind{},
		},
	}
	c.walk(prog)

	return c.out
}

func (c *checker) errf(line int, code, format string, args ...any) {
	c.out.Diags = append(c.out.Diags, Diagnostic{Code: code, Msg: fmt.Sprintf(format, args...), Line: line})
}

// ———— 程序遍历：与 resolver 同构 ————

func (c *checker) walk(prog []syntax.Stmt) {
	c.pushScope()
	// 1. 顶层绑定依序（前向引用已被 resolver 拦）
	for _, s := range prog {
		if d, ok := s.(*syntax.LetStmt); ok {
			c.letStmt(d)
		}
	}
	// 2. fn 与方法体
	for _, s := range prog {
		if d, ok := s.(*syntax.FnDecl); ok && d.Recv == nil {
			c.fnBody(d, c.res.DeclSyms[d], nil)
		}
	}

	for _, s := range prog {
		if d, ok := s.(*syntax.FnDecl); ok && d.Recv != nil {
			c.fnBody(d, c.methodSym(d), c.methodStructType(d))
		}
	}
	// 3. 顶层游离语句（脚本模式；interp 可执行，转译不支持——只做类型走查）
	for _, s := range prog {
		switch s.(type) {
		case *syntax.LetStmt, *syntax.FnDecl, *syntax.StructDecl, *syntax.EnumDecl,
			*syntax.ExternDecl, *syntax.ImportStmt:
			continue
		}

		c.stmt(s)
	}

	c.popScope()
}

func (c *checker) methodSym(d *syntax.FnDecl) *semantic.Symbol {
	nt, ok := d.Recv.Typ.(*syntax.NamedType)
	if !ok {
		return nil
	}

	if st := c.lookupTypeSym(nt.Name); st != nil {
		return st.Methods[d.Name]
	}

	return nil
}

func (c *checker) methodStructType(d *syntax.FnDecl) *Type {
	nt, ok := d.Recv.Typ.(*syntax.NamedType)
	if !ok {
		return Unknown
	}

	if st := c.lookupTypeSym(nt.Name); st != nil {
		return StructOf(st)
	}

	return Unknown
}

// fnBody 函数体检查：参数注解必填（E3007，迁自转译约束），建立返回上下文。
func (c *checker) fnBody(d *syntax.FnDecl, sym *semantic.Symbol, recvTy *Type) {
	savedRet, savedInFn, savedScopes, savedTps := c.fnRet, c.inFn, c.scopes, c.tps
	c.scopes = []map[string]*Type{{}}
	c.tps = c.tparamsOf(d.TypeParams)
	sig := c.fnSignature(d)

	c.fnRet = nil
	if d.Ret != nil {
		c.fnRet = sig.Ret // 注解只解析一次（fnSignature 内），错误不重复报
	}

	c.inFn = true

	if d.Recv != nil {
		c.scopes[0][d.Recv.Name] = recvTy
	}

	c.declareParams(d, sym, recvTy)

	if sym != nil {
		c.out.FnTy[sym.ID] = sig
	}

	for _, s := range d.Body {
		c.stmt(s)
	}

	c.fnRet, c.inFn, c.scopes, c.tps = savedRet, savedInFn, savedScopes, savedTps
}

// declareParams 解析参数注解（缺注解 → E3007 + Unknown）入作用域，
// 并把类型按序落到参数符号的 SymTy（接收者在 FnParams[0]）.
func (c *checker) declareParams(d *syntax.FnDecl, sym *semantic.Symbol, recvTy *Type) {
	var psyms []*semantic.Symbol
	if sym != nil {
		psyms = c.res.FnParams[sym.ID]
	}

	pi := 0
	if d.Recv != nil {
		if pi < len(psyms) {
			c.out.SymTy[psyms[pi].ID] = recvTy
			pi++
		}
	}

	for _, p := range d.Params {
		var pt *Type

		if p.Typ == nil {
			c.errf(d.Line, E3007, "参数 %s 缺少类型注解", p.Name)

			pt = Unknown
		} else {
			pt = c.resolveTypeEx(p.Typ, c.tparamsOf(d.TypeParams))
		}

		c.scopes[0][p.Name] = pt
		if p.Name != "_" && pi < len(psyms) {
			c.out.SymTy[psyms[pi].ID] = pt
			pi++
		}
	}
}

// fnSignature fn/方法的函数类型（参数注解缺失 → Unknown 占位）.
func (c *checker) fnSignature(d *syntax.FnDecl) *Type {
	tps := c.tparamsOf(d.TypeParams)

	var ps []*Type

	for _, p := range d.Params {
		if p.Typ == nil {
			ps = append(ps, Unknown)
			continue
		}

		ps = append(ps, c.resolveTypeEx(p.Typ, tps))
	}

	var ret *Type = Unknown
	if d.Ret != nil {
		ret = c.resolveTypeEx(d.Ret, tps)
	}

	return FnOf(ps, ret)
}

func (c *checker) tparamsOf(names []string) map[string]bool {
	m := map[string]bool{}
	for _, n := range names {
		m[n] = true
	}

	return m
}

func (c *checker) letStmt(d *syntax.LetStmt) {
	sym := c.res.DeclSyms[d]

	var ty *Type
	if t, ok := d.Init.(*syntax.TryEx); ok {
		ty = c.inferTry(t)
	} else {
		ty = c.infer(d.Init)
	}

	if d.Typ != nil {
		anno := c.typeExOf(d.Typ, nil)
		if !Assignable(anno, ty) {
			c.errf(d.Line, E3001, "不能把 %s 绑定为注解类型 %s", ty.String(), anno.String())
		}

		ty = anno
	}

	if sym != nil {
		c.out.SymTy[sym.ID] = ty
	}

	c.defineVar(d.Name, ty)
}

// ———— 语句 ————

func (c *checker) stmt(s syntax.Stmt) {
	switch st := s.(type) {
	case *syntax.LetStmt:
		c.letStmt(st)

	case *syntax.AssignStmt:
		c.assign(st)

	case *syntax.ExprStmt:
		if t, ok := st.X.(*syntax.TryEx); ok {
			// ADR-0005 扩展：表达式语句位置的顶层 ? 合法，走与 let/return/赋值相同的传播检查
			c.inferTry(t)
		} else {
			c.infer(st.X)
		}

	case *syntax.ReturnStmt:
		c.returnStmt(st)

	case *syntax.IfStmt:
		c.requireBool(st.Cond, "if 条件")
		c.block(st.Then)

		if st.Else != nil {
			c.block(st.Else)
		}

	case *syntax.WhileStmt:
		c.requireBool(st.Cond, "while 条件")
		c.block(st.Body)

	case *syntax.ForInStmt:
		ty := c.infer(st.Iter)

		var elem *Type

		switch {
		case ty.Kind == KSlice:
			elem = ty.Elem
		case ty.Kind == KMap:
			elem = ty.Key
		case ty.Kind == KString:
			elem = String
		case ty.Kind == KRange:
			elem = Int
		case !IsConcrete(ty):
			elem = Unknown
		default:
			c.errf(st.Line, E3010, "%s 不可迭代", ty.String())

			elem = Unknown
		}

		if sym := c.res.DeclSyms[st]; sym != nil {
			c.out.SymTy[sym.ID] = elem
		}

		c.pushScope()
		c.defineVar(st.Var, elem)
		c.stmts(st.Body)
		c.popScope()

	case *syntax.GoStmt:
		c.inferCall(st.Call)

	case *syntax.FnDecl:
		if st.Recv == nil {
			c.fnBody(st, c.res.DeclSyms[st], nil)
		}

	case *syntax.StructDecl, *syntax.EnumDecl:
		// 嵌套类型声明由 resolver 报 E2010

	case *syntax.BreakStmt, *syntax.ContinueStmt, *syntax.ImportStmt, *syntax.ExternDecl:
		// 位置合法性由 resolver 管；extern 无类型面
	}
}

func (c *checker) block(list []syntax.Stmt) {
	c.pushScope()
	c.stmts(list)
	c.popScope()
}

func (c *checker) stmts(list []syntax.Stmt) {
	for _, s := range list {
		c.stmt(s)
	}
}

func (c *checker) assign(st *syntax.AssignStmt) {
	var valTy *Type
	if t, ok := st.Val.(*syntax.TryEx); ok {
		// ADR-0005：赋值右值位置的顶层 ? 合法，走与 let/return 相同的传播检查
		valTy = c.inferTry(t)
	} else {
		valTy = c.infer(st.Val)
	}

	switch tgt := st.Target.(type) {
	case *syntax.Ident:
		tgtTy, _ := c.lookupVar(tgt.Name)
		if st.Op != syntax.Assign {
			c.checkCompound(st, tgtTy, valTy)
			return
		}

		if !Assignable(tgtTy, valTy) {
			c.errf(st.Line, E3001, "不能把 %s 赋给 %s", valTy.String(), tgtTy.String())
		}
	case *syntax.FieldEx:
		objTy := c.infer(tgt.Obj)

		fTy := c.fieldType(objTy, tgt.Name, tgt.Line)
		if st.Op != syntax.Assign {
			c.checkCompound(st, fTy, valTy)
			return
		}

		if !Assignable(fTy, valTy) {
			c.errf(st.Line, E3001, "不能把 %s 赋给字段 %s（%s）", valTy.String(), tgt.Name, fTy.String())
		}
	case *syntax.IndexEx:
		objTy := c.infer(tgt.Obj)
		idxTy := c.infer(tgt.Idx)
		c.checkIndexKey(objTy, idxTy, tgt.Line)

		elemTy := c.indexElemTy(objTy, tgt.Line)
		if st.Op != syntax.Assign {
			c.checkCompound(st, elemTy, valTy)
			return
		}

		if !Assignable(elemTy, valTy) {
			c.errf(st.Line, E3001, "不能把 %s 赋给容器元素（%s）", valTy.String(), elemTy.String())
		}
	}
}

// checkCompound 复合赋值（+= -= *= /=）：同型数值/字符串；数组 += 数组.
func (c *checker) checkCompound(st *syntax.AssignStmt, tgtTy, valTy *Type) {
	if !IsConcrete(tgtTy) || !IsConcrete(valTy) {
		return
	}

	if tgtTy.Kind == KSlice {
		if st.Op == syntax.PlusAssign && valTy.Kind == KSlice {
			return
		}

		c.errf(st.Line, E3001, "数组 %s 需要数组右值，得到 %s", opName(st.Op), valTy.String())

		return
	}

	if tgtTy.Kind == valTy.Kind && (IsNumeric(tgtTy) || tgtTy.Kind == KString) {
		return
	}

	c.errf(st.Line, E3002, "%s 与 %s 不可做 %s（无隐式转换）", tgtTy.String(), valTy.String(), opName(st.Op))
}

func (c *checker) returnStmt(st *syntax.ReturnStmt) {
	if !c.inFn {
		return // resolver 已报 E2008
	}

	if st.Val == nil {
		if c.fnRet != nil && c.fnRet.Kind == KResult {
			c.errf(st.Line, E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)")
		} else if c.fnRet != nil && IsConcrete(c.fnRet) {
			c.errf(st.Line, E3001, "注解为 %s 的函数不能裸 return", c.fnRet.String())
		}

		return
	}

	if t, ok := st.Val.(*syntax.TryEx); ok {
		c.inferTry(t)
		return
	}

	if c.fnRet != nil && c.fnRet.Kind == KResult {
		c.inferResultReturn(st, c.fnRet)
		return
	}

	valTy := c.infer(st.Val)
	if c.fnRet != nil && IsConcrete(c.fnRet) && !Assignable(c.fnRet, valTy) {
		c.errf(st.Line, E3001, "返回 %s 与注解 %s 不匹配", valTy.String(), c.fnRet.String())
	}
}

func (c *checker) inferResultReturn(st *syntax.ReturnStmt, ret *Type) {
	call, ok := st.Val.(*syntax.CallEx)
	if !ok {
		c.errf(st.Line, E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)")
		c.infer(st.Val)

		return
	}

	id, ok := call.Callee.(*syntax.Ident)
	if !ok || (id.Name != "Ok" && id.Name != "Err") || len(call.Args) != 1 {
		c.errf(st.Line, E3006, "Result 函数的 return 须为 Ok(...) 或 Err(...)")
		c.infer(st.Val)

		return
	}

	argTy := c.infer(call.Args[0])
	if id.Name == "Ok" {
		if !Assignable(ResultOk(ret), argTy) {
			c.errf(st.Line, E3001, "Ok(%s) 与返回类型 %s 不匹配", argTy.String(), ret.String())
		}
	} else if !Assignable(ResultErr(ret), argTy) {
		c.errf(st.Line, E3001, "Err(%s) 与返回类型 %s 不匹配", argTy.String(), ret.String())
	}

	c.out.ExprTy[st.Val] = ret
}
