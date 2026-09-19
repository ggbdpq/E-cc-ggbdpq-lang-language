package types

// 表达式类型推断。所有路径保守：Any/Unknown 参与 → 结果 Any/Unknown，不报错。

import (
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
)

// deref 引用自动解引用（&T 的字段/索引/方法访问与 interp 的 PtrVal 语义一致）
func deref(t *Type) *Type {
	for t.Kind == KRef {
		t = t.Elem
	}
	return t
}

func (c *checker) pushScope() { c.scopes = append(c.scopes, map[string]*Type{}) }
func (c *checker) popScope()  { c.scopes = c.scopes[:len(c.scopes)-1] }

func (c *checker) lookupVar(name string) (*Type, bool) {
	for i := len(c.scopes) - 1; i >= 0; i-- {
		if t, ok := c.scopes[i][name]; ok {
			return t, true
		}
	}
	return Unknown, false
}

var typeNames = map[string]*Type{
	"int": Int, "float": Float, "string": String, "bool": Bool, "error": ErrorT, "any": Any,
}

// lookupTypeSym 用户 struct/enum 名 → 符号（含预载 error/HttpResp）
func (c *checker) lookupTypeSym(name string) *semantic.Symbol {
	if s := c.res.Builtin[name]; s != nil && s.Kind == semantic.KStruct {
		return s
	}
	for _, s := range c.res.Symbols {
		if (s.Kind == semantic.KStruct || s.Kind == semantic.KEnum) && s.Name == name {
			return s
		}
	}
	return nil
}

// resolveTypeEx 源码注解 → 语义类型。tps 为当前泛型参数名集。
func (c *checker) resolveTypeEx(t syntax.TypeEx, tps map[string]bool) *Type {
	switch tt := t.(type) {
	case *syntax.NamedType:
		switch tt.Name {
		case "int", "float", "string", "bool", "error", "any":
			return typeNames[tt.Name]
		case "chan":
			elem := Unknown
			if len(tt.Args) == 1 {
				elem = c.resolveTypeEx(tt.Args[0], tps)
			}
			return ChanOf(elem)
		case "Option":
			if len(tt.Args) == 1 {
				return OptionOf(c.resolveTypeEx(tt.Args[0], tps))
			}
			c.errf(0, E3001, "Option 需要一个类型参数")
			return Invalid
		case "Result":
			if len(tt.Args) != 2 {
				c.errf(0, E3001, "Result 需要两个类型参数")
				return Invalid
			}
			if en, ok := tt.Args[1].(*syntax.NamedType); !ok || en.Name != "error" {
				c.errf(0, E3009, "Result 第二类型参数须为 error")
				// 仍返回 Result 形：避免下游把 Result 函数误判为普通函数（E3006 连锁）
				return ResultOf(c.resolveTypeEx(tt.Args[0], tps), ErrorT)
			}
			return ResultOf(c.resolveTypeEx(tt.Args[0], tps), ErrorT)
		}
		if tps[tt.Name] {
			return &Type{Kind: KTypeArg, Name: tt.Name} // 泛型擦除：保留参数名供 →Go 生成
		}
		if sym := c.lookupTypeSym(tt.Name); sym != nil {
			if sym.Kind == semantic.KEnum {
				return EnumOf(sym)
			}
			return StructOf(sym)
		}
		return Unknown // 未知注解不阻塞（保守）
	case *syntax.ArrType:
		return SliceOf(c.resolveTypeEx(tt.Elem, tps))
	case *syntax.MapType:
		return MapOf(c.resolveTypeEx(tt.Key, tps), c.resolveTypeEx(tt.Val, tps))
	case *syntax.PtrType:
		return RefOf(c.resolveTypeEx(tt.Elem, tps))
	case *syntax.FnType:
		var ps []*Type
		for _, p := range tt.Params {
			ps = append(ps, c.resolveTypeEx(p, tps))
		}
		ret := Unknown
		if tt.Ret != nil {
			ret = c.resolveTypeEx(tt.Ret, tps)
		}
		return FnOf(ps, ret)
	}
	return Unknown
}

// typeExOf 注解解析入口（nil 注解 → nil）——未显式给 tparams 时用当前函数的
func (c *checker) typeExOf(t syntax.TypeEx, tps []string) *Type {
	if t == nil {
		return nil
	}
	if tps != nil {
		return c.resolveTypeEx(t, c.tparamsOf(tps))
	}
	return c.resolveTypeEx(t, c.tps)
}

// ———— 推断主入口 ————

// infer 推断并记录表达式类型
func (c *checker) infer(e syntax.Expr) *Type {
	if t, ok := c.out.ExprTy[e]; ok {
		return t
	}
	t := c.compute(e)
	c.out.ExprTy[e] = t
	return t
}

func (c *checker) compute(e syntax.Expr) *Type {
	switch x := e.(type) {
	case *syntax.IntLit:
		return Int
	case *syntax.FloatLit:
		return Float
	case *syntax.StrLit:
		return String
	case *syntax.BoolLit:
		return Bool

	case *syntax.Ident:
		return c.identType(x)

	case *syntax.Unary:
		return c.inferUnary(x)

	case *syntax.Binary:
		return c.inferBinary(x)

	case *syntax.RangeEx:
		lo, hi := c.infer(x.Lo), c.infer(x.Hi)
		if IsConcrete(lo) && lo.Kind != KInt || IsConcrete(hi) && hi.Kind != KInt {
			c.errf(x.Line, E3001, "区间两端须为 int，得到 %s..%s", lo.String(), hi.String())
			return Invalid
		}
		return Range

	case *syntax.CallEx:
		return c.inferCall(x)

	case *syntax.IndexEx:
		obj := c.infer(x.Obj)
		idx := c.infer(x.Idx)
		c.checkIndexKey(obj, idx, x.Line)
		return c.indexElemTy(obj, x.Line)

	case *syntax.SliceEx:
		obj := deref(c.infer(x.Obj))
		lo, hi := c.infer(x.Lo), c.infer(x.Hi)
		for _, t := range []*Type{lo, hi} {
			if IsConcrete(t) && t.Kind != KInt {
				c.errf(x.Line, E3013, "切片端点须为 int，得到 %s", t.String())
			}
		}
		switch obj.Kind {
		case KSlice:
			return obj
		case KString:
			return String
		case KAny, KUnknown, KInvalid:
			return Unknown
		}
		c.errf(x.Line, E3001, "%s 不可切片", obj.String())
		return Invalid

	case *syntax.FieldEx:
		return c.fieldType(c.infer(x.Obj), x.Name, x.Line)

	case *syntax.StructLit:
		return c.inferStructLit(x)

	case *syntax.MapLit:
		return c.inferMapLit(x)

	case *syntax.ArrLit:
		return c.inferArrLit(x)

	case *syntax.MatchEx:
		return c.inferMatch(x)

	case *syntax.FnLit:
		return c.inferFnLit(x)

	case *syntax.TryEx:
		// 非语句顶层位置（let/return/赋值已在语句层处理）
		c.errf(x.Line, E3005, "? 只能出现在 let/return/赋值的顶层")
		return c.inferTryOperand(x)
	}
	return Unknown
}

// identType Ident 类型：作用域栈（参数/绑定/臂捕获）优先，再走符号路径
func (c *checker) identType(x *syntax.Ident) *Type {
	if t, ok := c.lookupVar(x.Name); ok && t != nil && t.Kind != KInvalid {
		return t
	}
	if sym := c.res.Refs[x]; sym != nil {
		switch sym.Kind {
		case semantic.KFn:
			if t := c.out.FnTy[sym.ID]; t != nil {
				return t
			}
			if d, ok := sym.Decl.(*syntax.FnDecl); ok {
				t := c.fnSignature(d)
				c.out.FnTy[sym.ID] = t
				return t
			}
			return Unknown
		case semantic.KCtor:
			return c.ctorValueType(sym, x.Line)
		default:
			if t := c.out.SymTy[sym.ID]; t != nil {
				return t
			}
			return Unknown
		}
	}
	return Unknown // 内建裸引用 / 未定义名（已报）→ 保守
}

// ctorValueType 裸构造器引用（零参才合法，如 None/Unit）
func (c *checker) ctorValueType(sym *semantic.Symbol, line int) *Type {
	if sym.Arity != 0 {
		c.errf(line, E3003, "构造器 %s 须直接调用", sym.Name)
		return Invalid
	}
	return c.ctorResultType(sym, nil)
}

// ctorResultType 构造器调用的结果类型
func (c *checker) ctorResultType(sym *semantic.Symbol, args []*Type) *Type {
	switch sym.StructName {
	case "Option":
		elem := Unknown
		if len(args) == 1 {
			elem = args[0]
		}
		return OptionOf(elem)
	case "Result":
		ok := Unknown
		if len(args) == 1 {
			ok = args[0]
		}
		return ResultOf(ok, ErrorT)
	default:
		if en := c.lookupTypeSym(sym.StructName); en != nil {
			return EnumOf(en)
		}
		return Unknown
	}
}

func (c *checker) inferUnary(x *syntax.Unary) *Type {
	t := c.infer(x.X)
	switch x.Op {
	case syntax.Minus:
		switch {
		case !IsConcrete(t):
			return t
		case IsNumeric(t):
			return t
		}
		c.errf(x.Line, E3011, "- 不能用于 %s", t.String())
		return Invalid
	case syntax.Not:
		if IsConcrete(t) && t.Kind != KBool {
			c.errf(x.Line, E3011, "! 不能用于 %s（须为 bool）", t.String())
			return Invalid
		}
		return Bool
	case syntax.Amp:
		if _, ok := x.X.(*syntax.Ident); !ok {
			c.errf(x.Line, E3012, "v0 只能对变量取引用")
			return Invalid
		}
		return RefOf(t)
	}
	return Unknown
}

func (c *checker) inferBinary(x *syntax.Binary) *Type {
	// 短路逻辑：操作数须 bool（与 interp truthy 一致）
	if x.Op == syntax.And || x.Op == syntax.Or {
		c.requireBool(x.L, "逻辑运算")
		c.requireBool(x.R, "逻辑运算")
		return Bool
	}
	l, r := c.infer(x.L), c.infer(x.R)

	switch x.Op {
	case syntax.Eq, syntax.Neq:
		if IsConcrete(l) && IsConcrete(r) {
			sameScalar := l.Kind == r.Kind && (IsNumeric(l) || l.Kind == KString || l.Kind == KBool)
			sameEnum := l.Kind == KEnum && r.Kind == KEnum && l.Sym == r.Sym
			if !sameScalar && !sameEnum {
				c.errf(x.Line, E3001, "%s 与 %s 不可用 == 比较", l.String(), r.String())
				return Invalid
			}
		}
		return Bool
	case syntax.Lt, syntax.Le, syntax.Gt, syntax.Ge:
		if IsConcrete(l) && IsConcrete(r) {
			ok := (l.Kind == r.Kind) && (IsNumeric(l) || l.Kind == KString)
			if !ok {
				c.errf(x.Line, E3001, "%s 与 %s 不可比较大小（无隐式转换）", l.String(), r.String())
				return Invalid
			}
		}
		return Bool
	case syntax.Amp, syntax.BitOr, syntax.Xor, syntax.Shl, syntax.Shr:
		if IsConcrete(l) && IsConcrete(r) {
			if l.Kind != KInt || r.Kind != KInt {
				c.errf(x.Line, E3001, "%s 与 %s 不可位运算（须为 int）", l.String(), r.String())
				return Invalid
			}
			return Int
		}
		return Any
	}

	// 算术
	switch x.Op {
	case syntax.Plus:
		if IsConcrete(l) && l.Kind == KString && IsConcrete(r) && r.Kind == KString {
			return String
		}
		return c.arithType(x, l, r)
	case syntax.Minus, syntax.Star, syntax.Slash:
		return c.arithType(x, l, r)
	case syntax.Percent:
		if IsConcrete(l) && l.Kind == KFloat {
			c.errf(x.Line, E3001, "%% 不能用于 float")
			return Invalid
		}
		return c.arithType(x, l, r)
	}
	return Unknown
}

// arithType 数值运算类型：int⊗int→int、float⊗float→float、混用→E3002、动态→Any
func (c *checker) arithType(x *syntax.Binary, l, r *Type) *Type {
	if !IsConcrete(l) || !IsConcrete(r) {
		return Any
	}
	if l.Kind == r.Kind && IsNumeric(l) {
		return l
	}
	c.errf(x.Line, E3002, "%s 与 %s 不可做 %s（无隐式转换）", l.String(), r.String(), opName(x.Op))
	return Invalid
}

func (c *checker) requireBool(e syntax.Expr, what string) {
	t := c.infer(e)
	if IsConcrete(t) && t.Kind != KBool {
		c.errf(e.Pos(), E3011, "%s须为 bool，得到 %s", what, t.String())
	}
}

// ———— 调用 ————

// builtinSig 内建函数签名（参数为 nil 表示不检查该位）
var builtinSigs = map[string]func(c *checker, args []*Type) *Type{
	"println": func(c *checker, a []*Type) *Type { return Unit },
	"print":   func(c *checker, a []*Type) *Type { return Unit },
	"len":     func(c *checker, a []*Type) *Type { return Int },
	"str":     func(c *checker, a []*Type) *Type { return String },
	"int":     func(c *checker, a []*Type) *Type { return Int },
	"float":   func(c *checker, a []*Type) *Type { return Float },
	"keys":    func(c *checker, a []*Type) *Type { return SliceOf(Unknown) },
	"values":  func(c *checker, a []*Type) *Type { return SliceOf(Unknown) },
	"has":     func(c *checker, a []*Type) *Type { return Bool },
	"push": func(c *checker, a []*Type) *Type {
		if len(a) > 0 {
			return a[0]
		}
		return Unknown
	},
	"assert":        func(c *checker, a []*Type) *Type { return Unit },
	"err":           func(c *checker, a []*Type) *Type { return ErrorT },
	"bytes":         func(c *checker, a []*Type) *Type { return SliceOf(Int) },
	"char":          func(c *checker, a []*Type) *Type { return String },
	"jsonParse":     func(c *checker, a []*Type) *Type { return Any },
	"jsonStringify": func(c *checker, a []*Type) *Type { return String },
	"isNone":        func(c *checker, a []*Type) *Type { return Bool },
	"makeChan":      func(c *checker, a []*Type) *Type { return ChanOf(Unknown) },
	"send":          func(c *checker, a []*Type) *Type { return Unit },
	"recv": func(c *checker, a []*Type) *Type {
		if len(a) == 1 && a[0].Kind == KChan {
			return ResultOf(a[0].Elem, ErrorT)
		}
		return ResultOf(Unknown, ErrorT)
	},
}

// inferCall 调用推断
func (c *checker) inferCall(x *syntax.CallEx) *Type {
	// 构造器 / 内建 / 用户 fn 的 Ident 调用
	if id, ok := x.Callee.(*syntax.Ident); ok {
		sym := c.res.Refs[id]
		if sym != nil && sym.Kind == semantic.KCtor {
			var args []*Type
			for _, a := range x.Args {
				args = append(args, c.infer(a))
			}
			if len(args) != sym.Arity {
				c.errf(x.Line, E3003, "%s 需要 %d 个参数，得到 %d 个", id.Name, sym.Arity, len(args))
				return Invalid
			}
			if sym.Name == "Ok" || sym.Name == "Err" {
				// Ok/Err 只能出现在 Result 函数的 return（returnStmt 已处理该路径）
				c.errf(x.Line, E3006, "Ok/Err 只能出现在 Result 函数的 return")
			}
			return c.ctorResultType(sym, args)
		}
		// 内建调用（内建不进 Refs，裸引用已由 resolver 放行给调用路径）
		if sym == nil {
			if fn, ok := builtinSigs[id.Name]; ok && c.res.Builtin[id.Name] != nil {
				var args []*Type
				for _, a := range x.Args {
					args = append(args, c.infer(a))
				}
				return fn(c, args)
			}
		}
		// 用户 fn
		if sym != nil && sym.Kind == semantic.KFn {
			if d, ok := sym.Decl.(*syntax.FnDecl); ok {
				c.checkArgsVsDecl(d, x.Args, x.Line)
			}
			if t := c.out.FnTy[sym.ID]; t != nil {
				return t.Ret
			}
			return Unknown
		}
	}

	// 方法/字段函数值调用：obj.name(args)
	if fe, ok := x.Callee.(*syntax.FieldEx); ok {
		return c.inferMethodCall(fe, x)
	}

	// 表达式 callee（函数字面量/索引）：按函数类型检查
	calleeTy := c.infer(x.Callee)
	var args []*Type
	for _, a := range x.Args {
		args = append(args, c.infer(a))
	}
	if calleeTy.Kind == KFn {
		c.checkArgsVsFn(calleeTy, args, x.Line)
		return calleeTy.Ret
	}
	return Unknown
}

// inferMethodCall obj.name(args)：用户方法 / http.get / 字段函数值
func (c *checker) inferMethodCall(fe *syntax.FieldEx, x *syntax.CallEx) *Type {
	objTy := deref(c.infer(fe.Obj))
	if id, ok := fe.Obj.(*syntax.Ident); ok && objTy.Kind == KUnknown && fe.Obj != nil {
		// http.get：预载服务值
		if id.Name == "http" && c.res.Builtin["http"] != nil && fe.Name == "get" {
			for _, a := range x.Args {
				c.infer(a)
			}
			if len(x.Args) != 2 {
				c.errf(x.Line, E3003, "http.get 需要 2 个参数（url, headers），得到 %d 个", len(x.Args))
			}
			if resp := c.lookupTypeSym("HttpResp"); resp != nil {
				return ResultOf(StructOf(resp), ErrorT)
			}
			return ResultOf(Unknown, ErrorT)
		}
	}
	switch {
	case objTy.Kind == KStruct && objTy.Sym != nil:
		if m := objTy.Sym.Methods[fe.Name]; m != nil {
			if d, ok := m.Decl.(*syntax.FnDecl); ok {
				c.checkArgsVsDecl(d, x.Args, x.Line)
				ret := Unknown
				if d.Ret != nil {
					ret = c.typeExOf(d.Ret, d.TypeParams)
				}
				return ret
			}
			return Unknown
		}
		// 字段持有的函数值
		fTy := c.fieldType(objTy, fe.Name, fe.Line)
		var args []*Type
		for _, a := range x.Args {
			args = append(args, c.infer(a))
		}
		if fTy.Kind == KFn {
			c.checkArgsVsFn(fTy, args, x.Line)
			return fTy.Ret
		}
		if !IsConcrete(fTy) || fTy.Kind == KInvalid {
			return Unknown
		}
		c.errf(x.Line, E3004, "%s 不是方法也不是函数值字段", fe.Name)
		return Invalid
	case !IsConcrete(objTy):
		for _, a := range x.Args {
			c.infer(a)
		}
		return Unknown
	default:
		c.errf(x.Line, E3004, "%s 没有方法 %s", objTy.String(), fe.Name)
		return Invalid
	}
}

// checkArgsVsDecl 实参 ↔ 声明参数（个数 + 已注解参数的兼容性）
func (c *checker) checkArgsVsDecl(d *syntax.FnDecl, args []syntax.Expr, line int) {
	if len(args) != len(d.Params) {
		c.errf(line, E3003, "%s 需要 %d 个参数，得到 %d 个", d.Name, len(d.Params), len(args))
		for _, a := range args {
			c.infer(a)
		}
		return
	}
	tps := c.tparamsOf(d.TypeParams)
	for i, a := range args {
		at := c.infer(a)
		if d.Params[i].Typ == nil {
			continue
		}
		pt := c.resolveTypeEx(d.Params[i].Typ, tps)
		if !Assignable(pt, at) {
			c.errf(a.Pos(), E3001, "参数 %s 需要 %s，得到 %s", d.Params[i].Name, pt.String(), at.String())
		}
	}
}

func (c *checker) checkArgsVsSym(sym *semantic.Symbol, args []syntax.Expr, line int) {
	if d, ok := sym.Decl.(*syntax.FnDecl); ok {
		c.checkArgsVsDecl(d, args, line)
		return
	}
	for _, a := range args {
		c.infer(a)
	}
}

func (c *checker) checkArgsVsFn(fnTy *Type, args []*Type, line int) {
	if len(args) != len(fnTy.Params) {
		c.errf(line, E3003, "需要 %d 个参数，得到 %d 个", len(fnTy.Params), len(args))
		return
	}
	for i := range args {
		if !Assignable(fnTy.Params[i], args[i]) {
			c.errf(line, E3001, "第 %d 个参数需要 %s，得到 %s", i+1, fnTy.Params[i].String(), args[i].String())
		}
	}
}

// ———— 字面量与容器 ————

func (c *checker) inferStructLit(x *syntax.StructLit) *Type {
	sym := c.res.Refs[x] // resolver 已绑 struct 符号
	if sym == nil {
		for _, f := range x.Fields {
			c.infer(f.Val)
		}
		return Unknown
	}
	if len(x.Fields) != len(sym.Fields) {
		c.errf(x.Line, E3001, "%s 需要 %d 个字段，得到 %d 个", sym.Name, len(sym.Fields), len(x.Fields))
	}
	for i, f := range x.Fields {
		ft := c.infer(f.Val)
		if i < len(sym.Fields) {
			if f.Name != sym.Fields[i] {
				c.errf(x.Line, E3001, "%s 第 %d 个字段应为 %s，得到 %s", sym.Name, i+1, sym.Fields[i], f.Name)
			}
			if i < len(sym.FieldTypes) && sym.FieldTypes[i] != nil {
				anno := c.typeExOf(sym.FieldTypes[i], sym.TypeParams)
				if !Assignable(anno, ft) {
					c.errf(f.Val.Pos(), E3001, "字段 %s 需要 %s，得到 %s", f.Name, anno.String(), ft.String())
				}
			}
		}
	}
	return StructOf(sym)
}

func (c *checker) inferMapLit(x *syntax.MapLit) *Type {
	var keyT, valT *Type
	mixed := func(cur, next *Type) *Type {
		if cur == nil {
			return next
		}
		if Equal(cur, next) {
			return cur
		}
		if !IsConcrete(next) {
			return cur
		}
		if !IsConcrete(cur) {
			return next
		}
		return Unknown // 混型键/值：interp 合法，动态处理
	}
	for _, item := range x.Items {
		keyT = mixed(keyT, c.infer(item.Key))
		valT = mixed(valT, c.infer(item.Val))
	}
	if keyT == nil {
		keyT = Unknown
	}
	if valT == nil {
		valT = Unknown
	}
	if IsConcrete(keyT) && keyT.Kind != KInt && keyT.Kind != KString && keyT.Kind != KBool {
		c.errf(x.Line, E3014, "映射键须为 int/string/bool，得到 %s", keyT.String())
		return MapOf(Unknown, valT)
	}
	return MapOf(keyT, valT)
}

func (c *checker) inferArrLit(x *syntax.ArrLit) *Type {
	var elem *Type
	for _, el := range x.Items {
		t := c.infer(el)
		if elem == nil {
			elem = t
			continue
		}
		if Equal(elem, t) {
			continue
		}
		if !IsConcrete(t) {
			continue
		}
		if !IsConcrete(elem) {
			elem = t
			continue
		}
		elem = Unknown // 混型数组：interp 合法
	}
	if elem == nil {
		elem = Unknown
	}
	return SliceOf(elem)
}

// ———— match ————

func (c *checker) inferMatch(x *syntax.MatchEx) *Type {
	scrut := c.infer(x.Scrut)
	kind, ok := c.classifyMatch(x)
	c.out.MatchKind[x] = kind
	if !ok {
		return Unknown // 分类已报错（混用/字面量臂），不再叠加穷尽诊断
	}

	if kind == MatchEnum {
		c.checkEnumExhaustive(x)
	} else if kind == MatchResult || kind == MatchOption {
		c.checkAlgebraicExhaustive(x)
	}

	// 臂绑定类型与臂体类型
	var body *Type
	for _, arm := range x.Arms {
		bindTypes := c.armBindTypes(arm.Pat, kind, scrut)
		c.pushScope()
		for name, t := range bindTypes {
			c.scopes[0][name] = t
		}
		bt := c.infer(arm.Body)
		c.popScope()
		if body == nil {
			body = bt
		} else {
			switch {
			case Equal(body, bt):
			case Assignable(bt, body):
				// 臂体可归入已定类型 → 保持（如 [] 归入 [Ref]）
			case Assignable(body, bt):
				body = bt
			default:
				if IsConcrete(body) && IsConcrete(bt) {
					c.errf(arm.Line, E3001, "match 臂类型不一致：%s 与 %s", body.String(), bt.String())
				} else if !IsConcrete(body) {
					body = bt
				}
			}
		}
	}
	if body == nil {
		body = Unknown
	}
	return body
}

// armBindTypes 模式绑定的捕获类型
func (c *checker) armBindTypes(p syntax.Pat, kind MatchKind, scrut *Type) map[string]*Type {
	out := map[string]*Type{}
	var walk func(p syntax.Pat, t *Type)
	walk = func(p syntax.Pat, t *Type) {
		switch pt := p.(type) {
		case *syntax.BindPat:
			out[pt.Name] = t
			c.out.PatTy[pt] = t
		case *syntax.CasePat:
			sym := c.res.PatRefs[pt]
			if sym == nil {
				return
			}
			for i, sp := range pt.Subs {
				var subT *Type
				switch sym.StructName {
				case "Result":
					if sym.Name == "Ok" {
						subT = ResultOk(scrut)
					} else {
						subT = ErrorT
					}
				case "Option":
					subT = scrut.Elem
				default:
					if en := c.lookupTypeSym(sym.StructName); en != nil && i < len(pt.Subs) {
						subT = c.casePayloadType(en, sym.CaseIndex, i)
					}
				}
				if subT == nil {
					subT = Unknown
				}
				walk(sp, subT)
			}
		}
	}
	walk(p, Unknown)
	return out
}

// casePayloadType enum case 载荷类型
func (c *checker) casePayloadType(enumSym *semantic.Symbol, caseIdx, payloadIdx int) *Type {
	d, ok := enumSym.Decl.(*syntax.EnumDecl)
	if !ok || caseIdx >= len(d.Cases) || payloadIdx >= len(d.Cases[caseIdx].Payloads) {
		return Unknown
	}
	pt := d.Cases[caseIdx].Payloads[payloadIdx]
	if pt == nil {
		return Unknown
	}
	return c.resolveTypeEx(pt, c.tparamsOf(enumSym.TypeParams))
}

// classifyMatch match 分类（迁移自 →Go/→JS 转译器的重复实现，唯一判定点）。
// 第二返回值 false 表示分类阶段已报错。
func (c *checker) classifyMatch(x *syntax.MatchEx) (MatchKind, bool) {
	hasCase, hasLit := false, false
	resultCtor, optionCtor, enumName := false, false, ""
	mixedEnum := false
	for _, arm := range x.Arms {
		switch p := arm.Pat.(type) {
		case *syntax.LitPat:
			hasLit = true
		case *syntax.CasePat:
			hasCase = true
			sym := c.res.PatRefs[p]
			if sym == nil {
				continue
			}
			switch sym.StructName {
			case "Result":
				resultCtor = true
			case "Option":
				optionCtor = true
			default:
				if enumName == "" {
					enumName = sym.StructName
				} else if enumName != sym.StructName {
					mixedEnum = true
				}
			}
		}
	}
	switch {
	case resultCtor && optionCtor:
		c.errf(x.Line, E4003, "Result 与 Option 的模式混用")
		return MatchLiteral, false
	case resultCtor:
		if hasLit {
			c.errf(x.Line, E4002, "代数类型的 match 不能混入字面量模式")
			return MatchResult, false
		}
		return MatchResult, true
	case optionCtor:
		if hasLit {
			c.errf(x.Line, E4002, "代数类型的 match 不能混入字面量模式")
			return MatchOption, false
		}
		return MatchOption, true
	case hasCase:
		if hasLit {
			c.errf(x.Line, E4002, "enum 的 match 不能混入字面量模式")
			return MatchEnum, false
		}
		if mixedEnum {
			c.errf(x.Line, E4003, "match 混入了不同 enum 的构造器")
			return MatchEnum, false
		}
		return MatchEnum, true
	default:
		return MatchLiteral, true
	}
}

// checkEnumExhaustive enum match 穷尽性（迁自转译器；interp 的运行时检查由此前移）
func (c *checker) checkEnumExhaustive(x *syntax.MatchEx) {
	covered := map[string]bool{}
	hasWild := false
	var enumSym *semantic.Symbol
	for _, arm := range x.Arms {
		switch p := arm.Pat.(type) {
		case *syntax.CasePat:
			sym := c.res.PatRefs[p]
			if sym == nil {
				continue
			}
			if enumSym == nil {
				if en := c.lookupTypeSym(sym.StructName); en != nil {
					enumSym = en
				}
			}
			covered[p.Name] = true
		case *syntax.WildPat, *syntax.BindPat:
			hasWild = true
		}
	}
	if hasWild || enumSym == nil {
		return
	}
	var missing []string
	d, ok := enumSym.Decl.(*syntax.EnumDecl)
	if !ok {
		return
	}
	for _, cs := range d.Cases {
		if !covered[cs.Name] {
			missing = append(missing, cs.Name)
		}
	}
	if len(missing) > 0 {
		c.errf(x.Line, E4001, "%s", "match 不穷尽，缺少: "+joinNames(missing))
	}
}

// checkAlgebraicExhaustive Result/Option match 须两臂齐全（修正 interp 运行时才报、
// →Go 语句位置静默跳过的三路分歧）
func (c *checker) checkAlgebraicExhaustive(x *syntax.MatchEx) {
	pos, neg := false, false
	for _, arm := range x.Arms {
		if p, ok := arm.Pat.(*syntax.CasePat); ok {
			switch p.Name {
			case "Ok", "Some":
				pos = true
			case "Err", "None":
				neg = true
			}
		} else if _, ok := arm.Pat.(*syntax.WildPat); ok {
			pos, neg = true, true
		}
	}
	if !pos || !neg {
		c.errf(x.Line, E4001, "match 不穷尽：Result/Option 须覆盖两臂")
	}
}

// ———— ? 传播 ————

// inferTry 语句顶层 ? 的检查：所在函数须注解 Result，操作数须 Result
func (c *checker) inferTry(t *syntax.TryEx) *Type {
	if !c.inFn || c.fnRet == nil || c.fnRet.Kind != KResult {
		c.errf(t.Line, E3005, "带 ? 的函数须注解为 Result[T, error]")
	}
	return c.inferTryOperand(t)
}

func (c *checker) inferTryOperand(t *syntax.TryEx) *Type {
	op := c.infer(t.X)
	switch op.Kind {
	case KResult:
		return ResultOk(op)
	case KAny, KUnknown, KInvalid:
		return Unknown
	case KOption:
		c.errf(t.Line, E3008, "? 的操作数须为 Result，得到 %s（Option 不可用 ?）", op.String())
		return Invalid
	default:
		c.errf(t.Line, E3008, "? 的操作数须为 Result，得到 %s", op.String())
		return Invalid
	}
}

// ———— 字段与索引 ————

// fieldType 字段访问类型检查
func (c *checker) fieldType(objTy *Type, name string, line int) *Type {
	objTy = deref(objTy)
	switch {
	case objTy.Kind == KStruct && objTy.Sym != nil:
		for i, f := range objTy.Sym.Fields {
			if f == name {
				if i < len(objTy.Sym.FieldTypes) && objTy.Sym.FieldTypes[i] != nil {
					return c.resolveTypeEx(objTy.Sym.FieldTypes[i], c.tparamsOf(objTy.Sym.TypeParams))
				}
				return Unknown
			}
		}
		c.errf(line, E3004, "%s 没有字段 %s", symName(objTy.Sym), name)
		return Invalid
	case objTy.Kind == KError:
		if name == "msg" {
			return String
		}
		c.errf(line, E3004, "error 只有 msg 字段")
		return Invalid
	case !IsConcrete(objTy):
		return Unknown
	default:
		c.errf(line, E3004, "%s 没有字段 %s", objTy.String(), name)
		return Invalid
	}
}

// indexElemTy 索引读取的元素类型
func (c *checker) indexElemTy(objTy *Type, line int) *Type {
	objTy = deref(objTy)
	switch objTy.Kind {
	case KSlice:
		return objTy.Elem
	case KMap:
		return objTy.Elem
	case KString:
		return String
	case KAny, KUnknown, KInvalid:
		return Unknown
	}
	c.errf(line, E3001, "%s 不可索引", objTy.String())
	return Invalid
}

// checkIndexKey 索引类型检查：容器须 int 下标；映射键限 int/string/bool
func (c *checker) checkIndexKey(objTy, idxTy *Type, line int) {
	if !IsConcrete(idxTy) {
		return
	}
	objTy = deref(objTy)
	switch objTy.Kind {
	case KSlice, KString:
		if idxTy.Kind != KInt {
			c.errf(line, E3013, "索引须为 int，得到 %s", idxTy.String())
		}
	case KMap:
		if idxTy.Kind != KInt && idxTy.Kind != KString && idxTy.Kind != KBool {
			c.errf(line, E3014, "映射键须为 int/string/bool，得到 %s", idxTy.String())
		}
	}
}

// ———— 函数字面量 ————

func (c *checker) inferFnLit(x *syntax.FnLit) *Type {
	savedRet, savedInFn, savedScopes := c.fnRet, c.inFn, c.scopes
	c.scopes = []map[string]*Type{{}}
	c.fnRet = c.typeExOf(x.Ret, x.TypeParams)
	c.inFn = true
	var ps []*Type
	for _, p := range x.Params {
		if p.Typ == nil {
			c.errf(x.Line, E3007, "函数字面量参数 %s 缺少类型注解", p.Name)
			ps = append(ps, Unknown)
			c.scopes[0][p.Name] = Unknown
			continue
		}
		pt := c.resolveTypeEx(p.Typ, c.tparamsOf(x.TypeParams))
		ps = append(ps, pt)
		c.scopes[0][p.Name] = pt
	}
	for _, s := range x.Body {
		c.stmt(s)
	}
	c.fnRet, c.inFn, c.scopes = savedRet, savedInFn, savedScopes
	return FnOf(ps, func() *Type {
		if x.Ret == nil {
			return Unknown
		}
		return c.resolveTypeEx(x.Ret, c.tparamsOf(x.TypeParams))
	}())
}
