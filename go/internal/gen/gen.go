package gen

// Transpile 把 Core IR 转译为自包含、可编译的 Go 源码（package main，prelude 内联）。
//
// v0.3 起本后端消费 ir.Prog：名字解析、类型推断、语义校验（穷尽性、? 约束、
// 参数注解）已在 semantic/types 完成。此处只做 lang-go 类型 → Go 类型的映射
// 与 any 边界的收窄插桩（langGoTo），不再实现任何语言语义判定。

import (
	"fmt"
	"go/format"
	"strconv"
	"strings"

	"lang-go/internal/ir"
	"lang-go/internal/semantic"
	"lang-go/internal/types"
)

// Transpile IR → Go 源码
func Transpile(p *ir.Prog) (string, error) {
	g := &gen{
		localGo:  map[*ir.Local]string{},
		prog:     p,
		structs:  map[string]*ir.StructDecl{},
		enums:    map[string]*ir.EnumDecl{},
		caseEnum: map[string]string{},
		fns:      map[*semantic.Symbol]*ir.Fn{},
	}
	for _, s := range p.Structs {
		g.structs[s.Name] = s
	}
	for _, e := range p.Enums {
		g.enums[e.Name] = e
		for _, c := range e.Cases {
			g.caseEnum[c.Name] = e.Name
		}
	}
	var walkFns func(list []ir.Stmt)
	walkStmtFns := func(s ir.Stmt) {
		if fd, ok := s.(*ir.FnDef); ok {
			g.fns[fd.Fn.Sym] = fd.Fn
			walkFns(fd.Fn.Body)
		}
	}
	walkFns = func(list []ir.Stmt) {
		for _, s := range list {
			walkStmtFns(s)
		}
	}
	for _, f := range p.Fns {
		if f.Sym != nil {
			g.fns[f.Sym] = f
		}
		walkFns(f.Body)
	}
	if err := g.emitProgram(p); err != nil {
		return "", err
	}
	out, err := format.Source([]byte(g.buf.String()))
	if err != nil {
		return "", fmt.Errorf("生成的 Go 源码无法通过 gofmt（转译器缺陷）: %w\n=== 源码 ===\n%s", err, g.buf.String())
	}
	return string(out), nil
}

type gen struct {
	buf         strings.Builder
	indent      int
	prog        *ir.Prog
	structs     map[string]*ir.StructDecl
	enums       map[string]*ir.EnumDecl
	caseEnum    map[string]string
	fns         map[*semantic.Symbol]*ir.Fn
	tmpN        int
	curFn       *ir.Fn
	curFnName   string
	curMatchTyp *types.Type          // 表达式位置 match 的结果类型（臂体收窄用）
	localGo     map[*ir.Local]string // 局部变量的实际 Go 类型（:= 推断结果）
}

// gt 局部表达式的「生成侧 Go 类型」。语义类型 ≠ Go 运行时形态：
// langGoIndex/langGoSlice/langGoField 返回 any；langGoTo 收窄后的局部是目标类型；
// 未注解绑定 := 后保持 RHS 的 Go 类型。此函数对应旧版 infer() 的角色。
func (g *gen) gt(e ir.Expr) string {
	switch x := e.(type) {
	case *ir.LocalGet:
		if s, ok := g.localGo[x.Local]; ok {
			return s
		}
		return goTypeOf(x.Local.Typ)
	case *ir.Index, *ir.Slice:
		return "any" // langGoIndex/langGoSlice 返回 interface
	case *ir.Field:
		if isAny(x.Obj.Typ()) {
			return "any" // langGoField
		}
		if x.Obj.Typ().Kind == types.KError {
			return "string" // langGoErrMsg
		}
	case *ir.Call:
		if x.Builtin != "" {
			if r, ok := goBuiltinRet[x.Builtin]; ok {
				return r
			}
		}
		if x.Fn != nil {
			if fi := g.fns[x.Fn]; fi != nil {
				if fi.Ret != nil {
					return goTypeOf(fi.Ret)
				}
				return "any"
			}
		}
	case *ir.Ctor:
		return goTypeOf(x.Typ())
	case *ir.Match:
		return g.matchRetType(x)
	case *ir.Binary:
		// Go 形态传播：+ 只要一侧是 string 结果就是 string；
		// 任一侧 any 则运行时形态 any（语义层 String+Any=Any 在此被细化）
		if x.Op == ir.OpPlus {
			li, ri := g.gt(x.L), g.gt(x.R)
			if li == "string" || ri == "string" {
				return "string"
			}
			if li == "any" || ri == "any" {
				return "any"
			}
			return li
		}
		if x.Op == ir.OpAndAnd || x.Op == ir.OpOrOr {
			return "bool"
		}
	case *ir.Unary:
		if x.Op == ir.OpNot {
			return "bool"
		}
	}
	return goTypeOf(e.Typ())
}

// matchRetType 表达式位置 match 的有效返回形态：
// 任一臂的 Go 形态与统一类型不一致（如 [] 臂 vs [Ref]）→ any（旧行为）
func (g *gen) matchRetType(m *ir.Match) string {
	ret := goTypeOf(m.Typ())
	for _, arm := range m.Arms {
		if arm.Body != nil && goTypeOf(arm.Body.Typ()) != ret {
			return "any"
		}
	}
	return ret
}

// goBuiltinRet 部分内建在 →Go prelude 里的实际返回形态
// （与语义类型不同：装箱为 any 元素/any 结果——对应旧版 infer 的内建表）
var goBuiltinRet = map[string]string{
	"bytes":     "*LangGoArr[any]",
	"keys":      "*LangGoArr[any]",
	"values":    "*LangGoArr[any]",
	"recv":      "LangGoRes[any]",
	"makeChan":  "chan any",
	"jsonParse": "any",
}

// needWrap 目标具体而右侧是动态 any → 需要 langGoTo 收窄
func (g *gen) needWrap(target *types.Type, e ir.Expr) bool {
	return target != nil && !isAny(target) && g.gt(e) == "any"
}

func (g *gen) errf(line int, format string, args ...any) error {
	return fmt.Errorf("第%d行: %s", line, fmt.Sprintf(format, args...))
}

func (g *gen) newTmp() string {
	g.tmpN++
	return fmt.Sprintf("_t%d", g.tmpN)
}

func (g *gen) w(line string) {
	g.buf.WriteString(strings.Repeat("\t", g.indent) + line + "\n")
}

// ———— 类型映射 ————

// goTypeOf lang-go 类型 → Go 类型（映射，非推断）
func goTypeOf(t *types.Type) string {
	if t == nil {
		return "any"
	}
	switch t.Kind {
	case types.KInt:
		return "int64"
	case types.KFloat:
		return "float64"
	case types.KString:
		return "string"
	case types.KBool:
		return "bool"
	case types.KError:
		return "error"
	case types.KChan:
		return "chan any" // v0 装箱：元素经 any 收窄（decisions #59）
	case types.KSlice:
		return "*LangGoArr[" + goTypeOf(t.Elem) + "]"
	case types.KMap:
		return "*LangGoMap"
	case types.KRef:
		return "*" + goTypeOf(t.Elem)
	case types.KOption:
		return "LangGoOpt[" + goTypeOf(t.Elem) + "]"
	case types.KResult:
		return "LangGoRes[" + goTypeOf(types.ResultOk(t)) + "]"
	case types.KEnum:
		return "LangGoEnum"
	case types.KStruct:
		if t.Sym != nil {
			return t.Sym.Name
		}
		return "any"
	case types.KRange:
		return "langGoRange"
	case types.KTypeArg:
		if t.Name != "" {
			return t.Name
		}
		return "any"
	case types.KFn:
		var ps []string
		for _, p := range t.Params {
			ps = append(ps, goTypeOf(p))
		}
		sig := "func(" + strings.Join(ps, ", ") + ")"
		if t.Ret != nil && t.Ret.Kind != types.KUnit {
			sig += " " + goTypeOf(t.Ret)
		}
		return sig
	}
	return "any"
}

func isAny(t *types.Type) bool {
	return t == nil || t.Kind == types.KAny || t.Kind == types.KUnknown ||
		t.Kind == types.KInvalid || t.Kind == types.KTypeArg
}

// maybeWrap 在 any 与具体类型交界处插 langGoTo 断言
func (g *gen) maybeWrap(target *types.Type, got string, expr string) string {
	if target != nil && !isAny(target) && got == "any" {
		return "langGoTo[" + goTypeOf(target) + "](" + expr + ")"
	}
	return expr
}

// wrapArg 按目标类型收窄实参/右值
func (g *gen) wrapArg(target *types.Type, e ir.Expr, expr string) string {
	return g.maybeWrap(target, g.gt(e), expr)
}

// ———— 程序输出 ————

func (g *gen) emitProgram(p *ir.Prog) error {
	g.buf.WriteString(prelude)
	// struct 声明 + 格式化方法（镜像解释器 fmtVal 的 struct 分支）
	for _, s := range p.Structs {
		var fields []string
		var parts []string
		for _, f := range s.Fields {
			fields = append(fields, f.Name+" "+goTypeOf(f.Typ))
			parts = append(parts, fmt.Sprintf("%q + \": \" + langGoFmt(v.%s, false)", f.Name, f.Name))
		}
		g.w("type " + s.Name + " struct {")
		for _, fl := range fields {
			g.w(fl)
		}
		g.w("}")
		g.w("")
		g.w("func (v " + s.Name + ") langGoString(top bool) string {")
		g.w("return " + strconv.Quote(s.Name) + " + \"{\" + strings.Join([]string{" + strings.Join(parts, ", ") + "}, \", \") + \"}\"")
		g.w("}")
		g.w("func (v " + s.Name + ") LangGoField(name string) any {")
		g.w("switch name {")
		for _, f := range s.Fields {
			g.w(`case "` + f.Name + `":`)
			g.w("return v." + f.Name)
		}
		g.w("}")
		g.w(`panic("` + s.Name + ` 没有字段 " + name)`)
		g.w("}")
		g.w("")
	}
	// 用户 enum 构造器
	for _, en := range p.Enums {
		for _, c := range en.Cases {
			var ps []string
			var vals []string
			for i, pt := range c.Payloads {
				pn := "p" + strconv.Itoa(i)
				ps = append(ps, pn+" "+goTypeOf(pt))
				vals = append(vals, pn)
			}
			valsExpr := "nil"
			if len(vals) > 0 {
				valsExpr = "[]any{" + strings.Join(vals, ", ") + "}"
			}
			g.w("func le_" + en.Name + "_" + c.Name + "(" + strings.Join(ps, ", ") + ") LangGoEnum { return LangGoEnum{" + strconv.Quote(c.Name) + ", " + valsExpr + "} }")
			g.w("")
		}
	}
	// 全局绑定
	for _, gl := range p.Globals {
		expr, err := g.expr(gl.Init)
		if err != nil {
			return err
		}
		g.w("var " + gl.Local.Name + " = " + expr)
		g.w("")
	}
	// 方法与函数
	for _, f := range p.Fns {
		if err := g.emitFunc(f); err != nil {
			return err
		}
	}
	// main
	if p.Main != nil {
		g.w("func main() {")
		g.w("\tlf_main()")
		g.w("\tlangGoWaitAll()")
		g.w("}")
	} else {
		g.w("func main() {}")
	}
	return nil
}

func (g *gen) goName(f *ir.Fn) string {
	if f.Recv != nil {
		return f.Name // 原生 Go 方法
	}
	return "lf_" + f.Name
}

// builtinMap 内建 → prelude 运行时函数
var builtinMap = map[string]string{
	"println": "langGoPrintln", "print": "langGoPrint", "len": "langGoLen",
	"str": "langGoStr", "int": "langGoInt", "float": "langGoFloat",
	"keys": "langGoKeys", "values": "langGoValues", "has": "langGoHas",
	"push": "langGoPush", "assert": "langGoAssert", "err": "langGoErr",
	"bytes": "langGoBytes", "char": "langGoChar",
	"makeChan": "langGoNewChan", "send": "langGoSend", "recv": "langGoRecv",
	"jsonParse": "langGoJSONParse", "jsonStringify": "langGoJSONStringify",
	"isNone": "langGoIsNone",
}

func (g *gen) emitFunc(f *ir.Fn) error {
	var generic string
	if len(f.TypeParams) > 0 {
		var gs []string
		for _, tp := range f.TypeParams {
			gs = append(gs, tp+" any")
		}
		generic = "[" + strings.Join(gs, ", ") + "]"
	}
	recvPrefix := ""
	if f.Recv != nil {
		recvPrefix = "(" + f.Recv.Name + " " + goTypeOf(f.Recv.Typ) + ") "
		g.localGo[f.Recv] = goTypeOf(f.Recv.Typ)
	}
	var ps []string
	for _, p := range f.Params {
		ps = append(ps, p.Name+" "+goTypeOf(p.Typ))
		g.localGo[p] = goTypeOf(p.Typ)
	}
	retDecl := ""
	if f.Ret != nil {
		retDecl = " " + goTypeOf(f.Ret)
	}
	g.w("func " + recvPrefix + g.goName(f) + generic + "(" + strings.Join(ps, ", ") + ")" + retDecl + " {")
	savedFn, savedName := g.curFn, g.curFnName
	g.curFn, g.curFnName = f, f.Name
	err := g.emitBody(f.Body)
	// 函数体内有 go 语句：函数边界 join（保证调用方读到任务结果）
	if err == nil && bodyHasGo(f.Body) {
		g.w("	langGoWaitAll()")
	}
	g.curFn, g.curFnName = savedFn, savedName
	if err != nil {
		return err
	}
	g.w("}")
	g.w("")
	return nil
}

// bodyHasGo 递归检查语句序列是否包含 go 语句
func bodyHasGo(stmts []ir.Stmt) bool {
	for _, st := range stmts {
		switch x := st.(type) {
		case *ir.Spawn:
			return true
		case *ir.If:
			if bodyHasGo(x.Then) || bodyHasGo(x.Else) {
				return true
			}
		case *ir.While:
			if bodyHasGo(x.Body) {
				return true
			}
		case *ir.ForIn:
			if bodyHasGo(x.Body) {
				return true
			}
		case *ir.FnDef:
		}
	}
	return false
}

func (g *gen) emitBody(stmts []ir.Stmt) error {
	for _, st := range stmts {
		if err := g.emitStmt(st); err != nil {
			return err
		}
	}
	return nil
}

// ———— 语句 ————

func (g *gen) emitStmt(s ir.Stmt) error {
	switch st := s.(type) {
	case *ir.Let:
		return g.emitLet(st)

	case *ir.Assign:
		return g.emitAssign(st)

	case *ir.Eval:
		if m, ok := st.X.(*ir.Match); ok {
			return g.emitMatchStmt(m)
		}
		expr, err := g.expr(st.X)
		if err != nil {
			return err
		}
		if g.isVoidCall(st.X) {
			g.w(expr)
		} else {
			g.w("_ = " + expr)
		}
		return nil

	case *ir.Return:
		return g.emitReturn(st)

	case *ir.If:
		cond, err := g.condExpr(st.Cond)
		if err != nil {
			return err
		}
		g.w("if " + cond + " {")
		if err := g.emitBlock(st.Then); err != nil {
			return err
		}
		if len(st.Else) > 0 {
			g.w("} else {")
			if err := g.emitBlock(st.Else); err != nil {
				return err
			}
		}
		g.w("}")
		return nil

	case *ir.While:
		cond, err := g.condExpr(st.Cond)
		if err != nil {
			return err
		}
		g.w("for " + cond + " {")
		if err := g.emitBlock(st.Body); err != nil {
			return err
		}
		g.w("}")
		return nil

	case *ir.ForIn:
		return g.emitForIn(st)

	case *ir.Break:
		g.w("break")
		return nil
	case *ir.Continue:
		g.w("continue")
		return nil

	case *ir.Spawn:
		fi := g.fns[st.Fn]
		if fi == nil {
			return g.errf(0, "内部错误：go 目标未注册 %s", st.Fn.Name)
		}
		// EVAL-GO-007：实参在 go 语句处就地求值并快照（临时变量），
		// goroutine 闭包不得按引用捕获调用方可变局部
		var args []string
		for i, a := range st.Args {
			arg, err := g.expr(a)
			if err != nil {
				return err
			}
			if i < len(fi.Params) {
				arg = g.wrapArg(fi.Params[i].Typ, a, arg)
			}
			tmp := g.newTmp()
			g.w(tmp + " := " + arg)
			g.w("_ = " + tmp)
			args = append(args, tmp)
		}
		g.w("langGoGo(func() { " + g.goName(fi) + "(" + strings.Join(args, ", ") + ") })")
		return nil

	case *ir.FnDef:
		return g.emitFunc(st.Fn)
	}
	return g.errf(0, "→Go 暂不支持该语句")
}

// emitLet let/var：? 在初始化顶层时先展开传播
func (g *gen) emitLet(st *ir.Let) error {
	name := st.Local.Name
	if t, ok := st.Init.(*ir.Try); ok {
		tmp, err := g.genTry(t)
		if err != nil {
			return err
		}
		if name != "_" {
			// 收窄源是 ? 操作数的 Result T（recv → any；用户函数按其注解）
			bindType := "any"
			if rt := t.X.Typ(); rt.Kind == types.KResult {
				bindType = goTypeOf(types.ResultOk(rt))
			}
			rhs := tmp + ".v"
			if bindType != "any" {
				rhs = "langGoTo[" + bindType + "](" + rhs + ")"
			}
			g.w(name + " := " + rhs)
		}
		return nil
	}
	expr, err := g.expr(st.Init)
	if err != nil {
		return err
	}
	if name == "_" {
		g.w("_ = " + expr)
		return nil
	}
	bindType := goTypeOf(st.Local.Typ)
	bindGo := g.gt(st.Init)
	// 空数组字面量继承注解类型：var out: [U] = []
	if al, ok := st.Init.(*ir.SliceLit); ok && len(al.Elems) == 0 && strings.HasPrefix(bindType, "*LangGoArr[") {
		expr = "&" + bindType[1:] + "{}"
		bindGo = bindType
	} else {
		if bindType != "any" && g.gt(st.Init) == "any" {
			bindGo = bindType // 有注解（或收窄目标）→ 局部 Go 类型即注解
		}
		expr = g.wrapArg(st.Local.Typ, st.Init, expr)
	}
	g.localGo[st.Local] = bindGo
	g.w(name + " := " + expr)
	// lang-go 无“局部绑定必须使用”规则，Go 有——统一消 unused 编译错
	g.w("_ = " + name)
	// 函数值绑定注册参数/返回类型，调用点据此收窄
	if fl, ok := st.Init.(*ir.FnLit); ok {
		fi := &ir.Fn{Name: name}
		fi.Params = fl.Params
		fi.Ret = fl.Ret
		if st.Local.Sym != nil {
			g.fns[st.Local.Sym] = fi
		}
	}
	return nil
}

func (g *gen) emitAssign(st *ir.Assign) error {
	if st.Op != ir.OpAssign {
		return g.emitCompoundAssign(st)
	}
	switch tgt := st.Target.(type) {
	case *ir.LocalGet:
		if t, ok := st.Val.(*ir.Try); ok {
			tmp, err := g.genTry(t)
			if err != nil {
				return err
			}
			g.w(tgt.Local.Name + " = " + tmp + ".v")
			return nil
		}
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		expr = g.wrapArg(tgt.Local.Typ, st.Val, expr)
		g.w(tgt.Local.Name + " = " + expr)
		return nil
	case *ir.Field:
		obj, err := g.expr(tgt.Obj)
		if err != nil {
			return err
		}
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		if idx, ok := tgt.Obj.(*ir.Index); ok {
			// 容器元素字段链 xs[i].f = v：读改写（VALUE-ELEM-009）
			cobj, err := g.expr(idx.Obj)
			if err != nil {
				return err
			}
			cidx, err := g.expr(idx.Idx)
			if err != nil {
				return err
			}
			elemT := goTypeOf(idx.Typ())
			tmp := g.newTmp()
			g.w(tmp + " := langGoIndex[" + elemT + "](" + cobj + ", " + cidx + ")")
			g.w(tmp + "." + tgt.Name + " = " + expr)
			g.w("langGoSetIndex(" + cobj + ", " + cidx + ", " + tmp + ")")
			return nil
		}
		g.w(obj + "." + tgt.Name + " = " + expr)
		return nil
	case *ir.Index:
		obj, err := g.expr(tgt.Obj)
		if err != nil {
			return err
		}
		idx, err := g.expr(tgt.Idx)
		if err != nil {
			return err
		}
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		g.w("langGoSetIndex(" + obj + ", " + idx + ", " + expr + ")")
		return nil
	}
	return g.errf(0, "赋值目标不合法")
}

// emitCompoundAssign 复合赋值：ident/field 原生 +=；数组 += 走 langGoPushFrom；
// index 复合走 langGoAddIndex（Map 值读改写）
func (g *gen) emitCompoundAssign(st *ir.Assign) error {
	op := string(st.Op)
	switch tgt := st.Target.(type) {
	case *ir.LocalGet:
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		lt := goTypeOf(tgt.Local.Typ)
		if st.Op == ir.OpPlusA && strings.HasPrefix(lt, "*LangGoArr[") {
			g.w(tgt.Local.Name + " = langGoPushFrom(" + tgt.Local.Name + ", " + expr + ")")
			return nil
		}
		// any 右值（如 recv(ch)? 解包）按目标类型收窄，Go 原生 += 不接受 any
		g.w(tgt.Local.Name + " " + op + " " + g.wrapArg(tgt.Local.Typ, st.Val, expr))
		return nil
	case *ir.Field:
		obj, err := g.expr(tgt.Obj)
		if err != nil {
			return err
		}
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		g.w(obj + "." + tgt.Name + " " + op + " " + expr)
		return nil
	case *ir.Index:
		obj, err := g.expr(tgt.Obj)
		if err != nil {
			return err
		}
		idx, err := g.expr(tgt.Idx)
		if err != nil {
			return err
		}
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		g.w("langGoAddIndex(" + obj + ", " + idx + ", " + expr + ", " + strconv.Quote(op) + ")")
		return nil
	}
	return g.errf(0, "赋值目标不合法")
}

// isVoidCall 判定"无返回值的调用"（语句位置不加 _ =）
func (g *gen) isVoidCall(e ir.Expr) bool {
	call, ok := e.(*ir.Call)
	if !ok {
		return false
	}
	switch {
	case call.Fn != nil:
		if fi := g.fns[call.Fn]; fi != nil {
			return fi.Ret == nil
		}
		return false
	case call.Method != nil:
		if fi := g.fns[call.Method]; fi != nil {
			return fi.Ret == nil
		}
		return false
	case call.Builtin != "":
		switch call.Builtin {
		case "println", "print", "push", "assert", "send":
			return true
		}
		return false
	}
	return false
}

func (g *gen) emitReturn(st *ir.Return) error {
	if st.Val == nil {
		g.w("return")
		return nil
	}
	if t, ok := st.Val.(*ir.Try); ok {
		tmp, err := g.genTry(t)
		if err != nil {
			return err
		}
		g.w("return " + tmp + ".v")
		return nil
	}
	if g.curFn != nil && g.curFn.Ret != nil && g.curFn.Ret.Kind == types.KResult {
		// Result 函数的 return 必为 Ok(...)/Err(...)（checker 已约束）
		ctor, ok := st.Val.(*ir.Ctor)
		if !ok || (ctor.Sym.Name != "Ok" && ctor.Sym.Name != "Err") || len(ctor.Args) != 1 {
			return g.errf(0, "内部错误：Result return 形态异常")
		}
		t := goTypeOf(types.ResultOk(g.curFn.Ret))
		expr, err := g.expr(ctor.Args[0])
		if err != nil {
			return err
		}
		if ctor.Sym.Name == "Ok" {
			g.w("return LangGoOk[" + t + "](" + expr + ")")
		} else {
			g.w("return LangGoResErr[" + t + "](" + expr + ")")
		}
		return nil
	}
	expr, err := g.expr(st.Val)
	if err != nil {
		return err
	}
	if g.curFn == nil || g.curFn.Ret == nil {
		return g.errf(0, "void 函数不应返回值")
	}
	// 返回值按签名收窄（如 any → string）
	rt := goTypeOf(g.curFn.Ret)
	if rt != "any" && g.gt(st.Val) == "any" {
		expr = "langGoTo[" + rt + "](" + expr + ")"
	}
	g.w("return " + expr)
	return nil
}

// genTry 展开 ? 传播；返回持有 Result 的临时变量名
func (g *gen) genTry(t *ir.Try) (string, error) {
	if g.curFn == nil || g.curFn.Ret == nil || g.curFn.Ret.Kind != types.KResult {
		return "", g.errf(0, "带 ? 的函数须注解为 Result[T, error]")
	}
	tmp := g.newTmp()
	expr, err := g.expr(t.X)
	if err != nil {
		return "", err
	}
	if g.gt(t.X) == "any" {
		// 装箱 Result（如 http.get）先收窄再解包
		expr = "langGoTo[LangGoRes[any]](" + expr + ")"
	}
	tt := goTypeOf(types.ResultOk(g.curFn.Ret))
	g.w(tmp + " := " + expr)
	g.w("if !" + tmp + ".ok {")
	g.indent++
	g.w("return LangGoResErr[" + tt + "](" + tmp + ".e)")
	g.indent--
	g.w("}")
	return tmp, nil
}

func (g *gen) condExpr(e ir.Expr) (string, error) {
	expr, err := g.expr(e)
	if err != nil {
		return "", err
	}
	if g.gt(e) != "bool" {
		expr = "langGoTruthy(" + expr + ")"
	}
	return expr, nil
}

func (g *gen) emitBlock(stmts []ir.Stmt) error {
	g.indent++
	err := g.emitBody(stmts)
	g.indent--
	return err
}

func (g *gen) emitForIn(st *ir.ForIn) error {
	if st.Kind == ir.IterRange {
		g.localGo[st.Var] = "int64"
		// 区间经绑定（let r = a..b; for i in r）与字面量两条路径
		if rg, ok := st.Iter.(*ir.Range); ok {
			lo, err := g.expr(rg.Lo)
			if err != nil {
				return err
			}
			hi, err := g.expr(rg.Hi)
			if err != nil {
				return err
			}
			g.w("for " + st.Var.Name + " := " + lo + "; " + st.Var.Name + " < " + hi + "; " + st.Var.Name + "++ {")
			if err := g.emitBlockTyped(st.Body, st.Var); err != nil {
				return err
			}
			g.w("}")
			return nil
		}
		rv, err := g.expr(st.Iter)
		if err != nil {
			return err
		}
		tmp := g.newTmp()
		g.w(tmp + " := " + rv)
		g.w("for " + st.Var.Name + " := " + tmp + ".Lo; " + st.Var.Name + " < " + tmp + ".Hi; " + st.Var.Name + "++ {")
		if err := g.emitBlockTyped(st.Body, st.Var); err != nil {
			return err
		}
		g.w("}")
		return nil
	}
	tmp := g.newTmp()
	it, err := g.expr(st.Iter)
	if err != nil {
		return err
	}
	name := st.Var.Name
	iterGo := g.gt(st.Iter)
	switch {
	case st.Kind == ir.IterSlice && iterGo != "any":
		g.localGo[st.Var] = iterGo[len("*LangGoArr[") : len(iterGo)-1]
		g.w(tmp + " := " + it)
		g.w("for _, " + name + " := range " + tmp + ".Items {")
		if err := g.emitBlockTyped(st.Body, st.Var); err != nil {
			return err
		}
		g.w("}")
	case st.Kind == ir.IterString:
		// lang-go 语义：按码点迭代，绑定单字符字符串
		g.localGo[st.Var] = "string"
		g.w("for _, _r" + tmp + " := range []rune(" + it + ") {")
		g.indent++
		g.w(name + " := string(_r" + tmp + ")")
		if err := g.emitBlock(st.Body); err != nil {
			return err
		}
		g.indent--
		g.w("}")
	default:
		// 动态值（JSON 结果/映射键/any 局部）：运行时分派迭代（langGoIter 做快照）
		g.localGo[st.Var] = "any"
		g.w(tmp + " := langGoIter(" + it + ")")
		g.w("for _, " + name + " := range " + tmp + ".Items {")
		if err := g.emitBlockTyped(st.Body, st.Var); err != nil {
			return err
		}
		g.w("}")
	}
	return nil
}

// emitBlockTyped 开块并预置一个绑定（局部信息已入 IR，无需作用域表）
func (g *gen) emitBlockTyped(stmts []ir.Stmt, v *ir.Local) error {
	g.indent++
	err := g.emitBody(stmts)
	g.indent--
	return err
}

// ———— match ————

func (g *gen) emitMatchStmt(m *ir.Match) error {
	tmp := g.newTmp()
	scrut, err := g.expr(m.Scrut)
	if err != nil {
		return err
	}
	scrut = g.coerceAlgebraic(scrut, m)
	g.w(tmp + " := " + scrut)
	switch m.Kind {
	case ir.MatchResult:
		okArm, errArm := splitAlgebraic(m, "Ok", "Err")
		g.w("if " + tmp + ".ok {")
		if err := g.emitResultArmBlock(okArm, tmp, ".v", "any"); err != nil {
			return err
		}
		g.w("} else {")
		if err := g.emitResultArmBlock(errArm, tmp, ".e", "error"); err != nil {
			return err
		}
		g.w("}")
	case ir.MatchOption:
		someArm, noneArm := splitAlgebraic(m, "Some", "None")
		g.w("if " + tmp + ".has {")
		if err := g.emitResultArmBlock(someArm, tmp, ".v", "any"); err != nil {
			return err
		}
		if noneArm != nil {
			g.w("} else {")
			g.indent++
			if err := g.emitArmStmt(noneArm); err != nil {
				return err
			}
			g.indent--
		}
		g.w("}")
	case ir.MatchEnum:
		g.w("switch " + tmp + ".Tag {")
		g.indent++
		for i := range m.Arms {
			arm := &m.Arms[i]
			if p, ok := arm.Pat.(*ir.CtorPat); ok {
				g.w(`case "` + p.Sym.Name + `":`)
				g.indent++
				if err := g.bindEnumPayloads(p, tmp); err != nil {
					return err
				}
				if err := g.emitArmStmt(arm); err != nil {
					return err
				}
				g.indent--
			} else {
				g.w("default:")
				g.indent++
				if bp, ok := arm.Pat.(*ir.BindPat); ok {
					g.w(bp.Name + " := " + tmp)
					g.w("_ = " + bp.Name)
				}
				if err := g.emitArmStmt(arm); err != nil {
					return err
				}
				g.indent--
			}
		}
		g.indent--
		g.w("}")
	default:
		if err := g.emitLiteralMatchStmt(m, tmp); err != nil {
			return err
		}
	}
	return nil
}

// coerceAlgebraic 装箱的 Result/Option（如 http.get 返回 any）在解包前收窄
func (g *gen) coerceAlgebraic(scrut string, m *ir.Match) string {
	if !isAny(m.Scrut.Typ()) {
		return scrut
	}
	switch m.Kind {
	case ir.MatchResult:
		return "langGoTo[LangGoRes[any]](" + scrut + ")"
	case ir.MatchOption:
		return "langGoTo[LangGoOpt[any]](" + scrut + ")"
	}
	return scrut
}

func splitAlgebraic(m *ir.Match, posName, negName string) (pos, neg *ir.Arm) {
	for i := range m.Arms {
		if p, ok := m.Arms[i].Pat.(*ir.CtorPat); ok {
			if p.Sym.Name == posName {
				pos = &m.Arms[i]
			} else if p.Sym.Name == negName {
				neg = &m.Arms[i]
			}
		}
	}
	return pos, neg
}

func (g *gen) emitResultArmBlock(arm *ir.Arm, tmp, field, bindType string) error {
	g.indent++
	if arm != nil {
		if name := armBindName(arm); name != "_" {
			g.w(name + " := " + tmp + field)
			g.w("_ = " + name) // 臂体未用时消 Go unused
			g.recordBind(arm, name, bindType)
		}
		if err := g.emitArmStmt(arm); err != nil {
			return err
		}
	}
	g.indent--
	return nil
}

// recordBind 臂绑定声明后的局部 Go 类型（Result/Option 解包 → any）
func (g *gen) recordBind(arm *ir.Arm, name, typ string) {
	if p, ok := arm.Pat.(*ir.CtorPat); ok {
		for _, sp := range p.Subs {
			if bp, ok := sp.(*ir.BindPat); ok {
				_ = bp
			}
		}
	}
	_ = name
	_ = typ
}

func armBindName(arm *ir.Arm) string {
	if p, ok := arm.Pat.(*ir.CtorPat); ok && len(p.Subs) == 1 {
		if bp, ok := p.Subs[0].(*ir.BindPat); ok {
			return bp.Name
		}
	}
	return "_"
}

func (g *gen) bindEnumPayloads(p *ir.CtorPat, tmp string) error {
	for j, sp := range p.Subs {
		if bp, ok := sp.(*ir.BindPat); ok {
			pt := "any"
			if bp.Typ != nil && !isAny(bp.Typ) {
				pt = goTypeOf(bp.Typ)
			}
			g.w(bp.Name + " := langGoTo[" + pt + "](" + tmp + ".Vals[" + strconv.Itoa(j) + "])")
		}
	}
	return nil
}

// emitArmStmt 臂体在语句位置（ExprStmt 展开）的输出
func (g *gen) emitArmStmt(arm *ir.Arm) error {
	if m, ok := arm.Body.(*ir.Match); ok {
		return g.emitMatchStmt(m)
	}
	expr, err := g.expr(arm.Body)
	if err != nil {
		return err
	}
	if g.isVoidCall(arm.Body) {
		g.w(expr)
	} else {
		g.w("_ = " + expr)
	}
	return nil
}

// emitLiteralMatchStmt 字面量/通配 match（语句位置）→ if/else 链
func (g *gen) emitLiteralMatchStmt(m *ir.Match, tmp string) error {
	for i := range m.Arms {
		arm := &m.Arms[i]
		switch p := arm.Pat.(type) {
		case *ir.LitPat:
			kw := "if"
			if i > 0 {
				kw = "} else if"
			}
			lit, err := g.expr(p.Val)
			if err != nil {
				return err
			}
			g.w(kw + " langGoEq(" + tmp + ", " + lit + ") {")
			if err := g.emitBlock([]ir.Stmt{&ir.Eval{X: arm.Body}}); err != nil {
				return err
			}
		case *ir.BindPat:
			g.w("} else {")
			g.indent++
			g.w(p.Name + " := " + tmp)
			g.w("_ = " + p.Name)
			if err := g.emitArmStmt(arm); err != nil {
				return err
			}
			g.indent--
		case *ir.WildPat:
			g.w("} else {")
			if err := g.emitBlock([]ir.Stmt{&ir.Eval{X: arm.Body}}); err != nil {
				return err
			}
		}
	}
	g.w("}")
	return nil
}

// ———— 表达式 ————

// operand 生成二元/一元运算的操作数：子式本身是运算时强制加括号，
// 消除 lang-go 与 Go 优先级表的残留差异（如 % 在 Go 里高于 +）
func (g *gen) operand(e ir.Expr) (string, error) {
	s, err := g.expr(e)
	if err != nil {
		return "", err
	}
	switch e.(type) {
	case *ir.Binary, *ir.Unary:
		return "(" + s + ")", nil
	}
	return s, nil
}

func (g *gen) expr(e ir.Expr) (string, error) {
	switch x := e.(type) {
	case *ir.Const:
		switch x.Kind {
		case ir.KInt:
			return "int64(" + strconv.FormatInt(x.I64, 10) + ")", nil
		case ir.KFloat:
			return "float64(" + strconv.FormatFloat(x.F64, 'g', -1, 64) + ")", nil
		case ir.KStr:
			return strconv.Quote(x.S), nil
		case ir.KBool:
			return strconv.FormatBool(x.B), nil
		}

	case *ir.LocalGet:
		return x.Local.Name, nil

	case *ir.FnGet:
		if fi := g.fns[x.Sym]; fi != nil {
			return g.goName(fi), nil
		}
		return "lf_" + x.Name, nil

	case *ir.BuiltinGet:
		return "", g.errf(0, "内建 %s 须直接调用", x.Name)

	case *ir.Ctor:
		return g.emitCtor(x)

	case *ir.Call:
		return g.emitCall(x)

	case *ir.Unary:
		inner, err := g.operand(x.X)
		if err != nil {
			return "", err
		}
		switch x.Op {
		case ir.OpNeg:
			return "-" + inner, nil
		case ir.OpNot:
			return "!" + inner, nil
		case ir.OpRef:
			return "&" + inner, nil
		}
		return "", g.errf(0, "未知一元运算")

	case *ir.Binary:
		return g.emitBinary(x)

	case *ir.Range:
		lo, err := g.expr(x.Lo)
		if err != nil {
			return "", err
		}
		hi, err := g.expr(x.Hi)
		if err != nil {
			return "", err
		}
		return "langGoRange{Lo: " + lo + ", Hi: " + hi + "}", nil

	case *ir.Index:
		obj, err := g.expr(x.Obj)
		if err != nil {
			return "", err
		}
		idx, err := g.expr(x.Idx)
		if err != nil {
			return "", err
		}
		// 显式类型实参：langGoIndex 泛型化后调用点按元素类型特化（非泛型=具体类型，
		// 泛型函数内=类型参数名，均在作用域）；避免依赖返回值上下文推断
		return "langGoIndex[" + goTypeOf(x.Typ()) + "](" + obj + ", " + idx + ")", nil

	case *ir.Slice:
		obj, err := g.expr(x.Obj)
		if err != nil {
			return "", err
		}
		lo, err := g.expr(x.Lo)
		if err != nil {
			return "", err
		}
		hi, err := g.expr(x.Hi)
		if err != nil {
			return "", err
		}
		return "langGoSlice(" + obj + ", " + lo + ", " + hi + ")", nil

	case *ir.Field:
		obj, err := g.expr(x.Obj)
		if err != nil {
			return "", err
		}
		if !isAny(x.Obj.Typ()) && x.Obj.Typ().Kind == types.KError {
			if x.Name == "msg" {
				return "langGoErrMsg(" + obj + ")", nil
			}
			return "", g.errf(0, "error 只有 msg 字段")
		}
		// 装箱值（如 http 响应）经接口分派取字段
		if g.gt(x) == "any" {
			return "langGoField(" + obj + ", " + strconv.Quote(x.Name) + ")", nil
		}
		return obj + "." + x.Name, nil

	case *ir.Try:
		return "", g.errf(0, "? 只能出现在 let/return/赋值的顶层（→Go 约束）")

	case *ir.StructLit:
		name := ""
		if x.Struct != nil {
			name = x.Struct.Name
		}
		sd := g.structs[name]
		var fs []string
		for i, f := range x.Fields {
			v, err := g.expr(f.Val)
			if err != nil {
				return "", err
			}
			if sd != nil && i < len(sd.Fields) {
				v = g.wrapArg(sd.Fields[i].Typ, x.Fields[i].Val, v)
			}
			fs = append(fs, f.Name+": "+v)
		}
		return name + "{" + strings.Join(fs, ", ") + "}", nil

	case *ir.MapLit:
		var kvs []string
		for i := range x.Keys {
			k, err := g.expr(x.Keys[i])
			if err != nil {
				return "", err
			}
			v, err := g.expr(x.Vals[i])
			if err != nil {
				return "", err
			}
			kvs = append(kvs, "langGoKV{"+k+", "+v+"}")
		}
		return "langGoNewMap(" + strings.Join(kvs, ", ") + ")", nil

	case *ir.SliceLit:
		elem := "any"
		if x.Typ().Kind == types.KSlice && !isAny(x.Typ().Elem) {
			elem = goTypeOf(x.Typ().Elem)
		}
		var items []string
		for _, el := range x.Elems {
			v, err := g.expr(el)
			if err != nil {
				return "", err
			}
			if x.Typ().Elem != nil && !isAny(x.Typ().Elem) && g.gt(el) == "any" {
				v = "langGoTo[" + goTypeOf(x.Typ().Elem) + "](" + v + ")"
			}
			items = append(items, v)
		}
		if len(items) == 0 {
			return "&LangGoArr[" + elem + "]{}", nil
		}
		return "&LangGoArr[" + elem + "]{Items: []" + elem + "{" + strings.Join(items, ", ") + "}}", nil

	case *ir.Match:
		return g.exprMatch(x)

	case *ir.FnLit:
		return g.emitFnLit(x)
	}
	return "", g.errf(0, "→Go 暂不支持该表达式")
}

// exprArg 实参生成：None 无载荷无法推断，按调用方期望的 Option 元素类型特化
func (g *gen) exprArg(e ir.Expr, target *types.Type) (string, error) {
	if c, ok := e.(*ir.Ctor); ok && c.Sym.Name == "None" && target != nil && target.Kind == types.KOption && target.Elem != nil && !isAny(target.Elem) {
		return "LangGoNone[" + goTypeOf(target.Elem) + "]()", nil
	}
	return g.expr(e)
}

// emitCtor 构造器：预载 Option/Result 与用户 enum
func (g *gen) emitCtor(x *ir.Ctor) (string, error) {
	switch x.Sym.Name {
	case "None":
		// None 无载荷无法靠推断：类型实参取当前函数返回类型的元素
		// （泛型函数=类型参数 T；非泛型 Option[int] → int64），与签名一致
		tp := "any"
		if g.curFn != nil {
			if len(g.curFn.TypeParams) == 1 {
				tp = g.curFn.TypeParams[0]
			} else if g.curFn.Ret != nil && g.curFn.Ret.Kind == types.KOption && g.curFn.Ret.Elem != nil && !isAny(g.curFn.Ret.Elem) {
				tp = goTypeOf(g.curFn.Ret.Elem)
			}
		}
		return "LangGoNone[" + tp + "]()", nil
	case "Some":
		if len(x.Args) != 1 {
			return "", g.errf(0, "Some 需要 1 个参数")
		}
		arg, err := g.expr(x.Args[0])
		if err != nil {
			return "", err
		}
		return "LangGoSome(" + arg + ")", nil
	case "Ok", "Err":
		return "", g.errf(0, "Ok/Err 只能出现在 Result 函数的 return（→Go 约束）")
	}
	en := g.caseEnum[x.Sym.Name]
	var args []string
	for _, a := range x.Args {
		arg, err := g.expr(a)
		if err != nil {
			return "", err
		}
		args = append(args, arg)
	}
	return "le_" + en + "_" + x.Sym.Name + "(" + strings.Join(args, ", ") + ")", nil
}

func (g *gen) emitBinary(x *ir.Binary) (string, error) {
	l, err := g.operand(x.L)
	if err != nil {
		return "", err
	}
	r, err := g.operand(x.R)
	if err != nil {
		return "", err
	}
	switch x.Op {
	case ir.OpAndAnd:
		return l + " && " + r, nil
	case ir.OpOrOr:
		return l + " || " + r, nil
	case ir.OpEq:
		return "langGoEq(" + l + ", " + r + ")", nil
	case ir.OpNeq:
		return "!langGoEq(" + l + ", " + r + ")", nil
	case ir.OpLt, ir.OpLe, ir.OpGt, ir.OpGe:
		// 排序比较统一走 langGoCompare（任一侧 any 也能运行时分派）
		cmp := "langGoCompare(" + l + ", " + r + ")"
		switch x.Op {
		case ir.OpLt:
			return cmp + " < 0", nil
		case ir.OpLe:
			return cmp + " <= 0", nil
		case ir.OpGt:
			return cmp + " > 0", nil
		default:
			return cmp + " >= 0", nil
		}
	case ir.OpPlus:
		// 任一侧为 any（如字符串索引/切片）时按 lang-go 语义收窄
		li, ri := g.gt(x.L), g.gt(x.R)
		if li == "any" || ri == "any" {
			if li == "string" || ri == "string" {
				return "langGoStrVal(" + l + ") + langGoStrVal(" + r + ")", nil
			}
			return "langGoInt(" + l + ") + langGoInt(" + r + ")", nil
		}
	case ir.OpMinus, ir.OpStar, ir.OpSlash, ir.OpPercent,
		ir.OpAnd, ir.OpBitOr, ir.OpXor, ir.OpShl, ir.OpShr:
		// 数值/位运算：any 侧按 int 收窄
		if g.gt(x.L) == "any" {
			l = "langGoInt(" + l + ")"
		}
		if g.gt(x.R) == "any" {
			r = "langGoInt(" + r + ")"
		}
	}
	return l + " " + string(x.Op) + " " + r, nil
}

// emitCall 用户 fn / 内建 / 函数值
func (g *gen) emitCall(x *ir.Call) (string, error) {
	// 内建
	if x.Builtin != "" {
		if x.Builtin == "http.get" {
			// 预载 HTTP 服务值（prelude 的 langGoHTTP，避让 net/http 包名）
			var args []string
			for _, a := range x.Args {
				arg, err := g.expr(a)
				if err != nil {
					return "", err
				}
				args = append(args, arg)
			}
			return "langGoHTTP.get(" + strings.Join(args, ", ") + ")", nil
		}
		bf, ok := builtinMap[x.Builtin]
		if !ok {
			return "", g.errf(0, "未知内建 %s", x.Builtin)
		}
		return g.emitBuiltinCall(x, x.Builtin, bf)
	}
	// 用户 fn
	if x.Fn != nil {
		fi := g.fns[x.Fn]
		if fi == nil {
			return "", g.errf(0, "内部错误：fn 未注册 %s", x.Fn.Name)
		}
		var args []string
		for i, a := range x.Args {
			var target *types.Type
			if i < len(fi.Params) {
				target = fi.Params[i].Typ
			}
			arg, err := g.exprArg(a, target)
			if err != nil {
				return "", err
			}
			if i < len(fi.Params) {
				arg = g.wrapArg(fi.Params[i].Typ, a, arg)
			}
			args = append(args, arg)
		}
		return g.goName(fi) + "(" + strings.Join(args, ", ") + ")", nil
	}
	// 方法
	if x.Method != nil {
		obj, err := g.expr(x.Recv)
		if err != nil {
			return "", err
		}
		var args []string
		for _, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			args = append(args, arg)
		}
		return obj + "." + x.Method.Name + "(" + strings.Join(args, ", ") + ")", nil
	}
	// 函数值（含 fnlit 绑定：按注册的参数类型收窄实参）
	if lg, ok := x.Callee.(*ir.LocalGet); ok && lg.Local.Sym != nil {
		if fi := g.fns[lg.Local.Sym]; fi != nil {
			var args []string
			for i, a := range x.Args {
				arg, err := g.expr(a)
				if err != nil {
					return "", err
				}
				if i < len(fi.Params) {
					arg = g.wrapArg(fi.Params[i].Typ, a, arg)
				}
				args = append(args, arg)
			}
			return lg.Local.Name + "(" + strings.Join(args, ", ") + ")", nil
		}
	}
	callee, err := g.expr(x.Callee)
	if err != nil {
		return "", err
	}
	var args []string
	for _, a := range x.Args {
		arg, err := g.expr(a)
		if err != nil {
			return "", err
		}
		args = append(args, arg)
	}
	return callee + "(" + strings.Join(args, ", ") + ")", nil
}

func (g *gen) emitBuiltinCall(x *ir.Call, name, bf string) (string, error) {
	switch name {
	case "assert":
		if len(x.Args) < 1 || len(x.Args) > 2 {
			return "", g.errf(0, "assert 需要 1~2 个参数")
		}
		cond, err := g.expr(x.Args[0])
		if err != nil {
			return "", err
		}
		if g.gt(x.Args[0]) != "bool" {
			cond = "langGoTruthy(" + cond + ")"
		}
		if len(x.Args) == 2 {
			msg, err := g.expr(x.Args[1])
			if err != nil {
				return "", err
			}
			return "langGoAssert(" + cond + ", " + msg + ")", nil
		}
		return "langGoAssert(" + cond + ")", nil
	case "push":
		if len(x.Args) < 2 {
			return "", g.errf(0, "push 需要至少 2 个参数")
		}
		arr, err := g.expr(x.Args[0])
		if err != nil {
			return "", err
		}
		at := goTypeOf(x.Args[0].Typ())
		if !strings.HasPrefix(at, "*LangGoArr[") {
			return "", g.errf(0, "push 目标类型未知（→Go 需可推断的数组）")
		}
		elem := at[len("*LangGoArr[") : len(at)-1]
		var vs []string
		for _, a := range x.Args[1:] {
			v, err := g.expr(a)
			if err != nil {
				return "", err
			}
			// 带元素类型的数组在 push 边界收窄（如 [string]）；any 右值才需要
			if elem != "any" && g.gt(a) == "any" {
				v = "langGoTo[" + elem + "](" + v + ")"
			}
			vs = append(vs, v)
		}
		return arr + ".Push(" + strings.Join(vs, ", ") + ")", nil
	}
	var args []string
	for _, a := range x.Args {
		arg, err := g.expr(a)
		if err != nil {
			return "", err
		}
		args = append(args, arg)
	}
	return bf + "(" + strings.Join(args, ", ") + ")", nil
}

// exprMatch 表达式位置的 match：包成立即调用的函数字面量（独立缓冲渲染）
func (g *gen) exprMatch(m *ir.Match) (string, error) {
	savedBuf := g.buf
	savedIndent := g.indent
	g.buf = strings.Builder{}
	g.indent = 0

	retType := g.matchRetType(m)
	savedMatchTyp := g.curMatchTyp
	g.curMatchTyp = m.Typ()
	tmp := g.newTmp()
	scrut, err := g.expr(m.Scrut)
	if err != nil {
		g.buf = savedBuf
		g.indent = savedIndent
		return "", err
	}
	scrut = g.coerceAlgebraic(scrut, m)
	g.w("func() " + retType + " {")
	g.indent++
	g.w(tmp + " := " + scrut)
	var emitErr error
	switch m.Kind {
	case ir.MatchResult:
		okArm, errArm := splitAlgebraic(m, "Ok", "Err")
		g.w("if " + tmp + ".ok {")
		emitErr = g.emitResultArmReturn(okArm, tmp, ".v", "any")
		if emitErr == nil {
			g.w("} else {")
			emitErr = g.emitResultArmReturn(errArm, tmp, ".e", "error")
		}
		if emitErr == nil {
			g.w("}")
		}
	case ir.MatchOption:
		someArm, noneArm := splitAlgebraic(m, "Some", "None")
		g.w("if " + tmp + ".has {")
		emitErr = g.emitResultArmReturn(someArm, tmp, ".v", "any")
		if emitErr == nil && noneArm != nil {
			g.w("} else {")
			emitErr = g.emitResultArmReturn(noneArm, tmp, ".e", "any")
		}
		if emitErr == nil {
			g.w("}")
		}
	case ir.MatchEnum:
		g.w("switch " + tmp + ".Tag {")
		g.indent++
		hasDefault := false
		for i := range m.Arms {
			arm := &m.Arms[i]
			if p, ok := arm.Pat.(*ir.CtorPat); ok {
				g.w(`case "` + p.Sym.Name + `":`)
				g.indent++
				emitErr = g.bindEnumPayloads(p, tmp)
				if emitErr == nil {
					emitErr = g.emitArmReturn(arm)
				}
				g.indent--
			} else {
				hasDefault = true
				g.w("default:")
				g.indent++
				if bp, ok := arm.Pat.(*ir.BindPat); ok {
					g.w(bp.Name + " := " + tmp)
					g.w("_ = " + bp.Name)
				}
				emitErr = g.emitArmReturn(arm)
				g.indent--
			}
			if emitErr != nil {
				break
			}
		}
		if emitErr == nil && !hasDefault {
			// 编译期穷尽检查已保证不可达；仅为满足 Go 的 missing return
			g.w("default:")
			g.indent++
			g.w(`panic("match 无匹配分支")`)
			g.indent--
		}
		g.indent--
		g.w("}")
	default:
		emitErr = g.emitLiteralMatchReturn(m, tmp)
	}
	g.indent--
	g.w("}()")
	g.curMatchTyp = savedMatchTyp
	out := g.buf.String()
	g.buf = savedBuf
	g.indent = savedIndent
	if emitErr != nil {
		return "", emitErr
	}
	return strings.TrimRight(out, "\n"), nil
}

func (g *gen) emitResultArmReturn(arm *ir.Arm, tmp, field, bindType string) error {
	if arm == nil {
		return nil
	}
	g.indent++
	if name := armBindName(arm); name != "_" {
		g.w(name + " := " + tmp + field)
		g.w("_ = " + name) // 臂体未用时消 Go unused
	}
	err := g.emitArmReturn(arm)
	g.indent--
	return err
}

// wrapTo 把 any 臂体收窄到 match 的结果类型（Go 需要一致返回类型）
func (g *gen) wrapTo(expr string, body ir.Expr, target *types.Type) string {
	if target != nil && !isAny(target) && g.gt(body) == "any" {
		return "langGoTo[" + goTypeOf(target) + "](" + expr + ")"
	}
	return expr
}

func (g *gen) emitLiteralMatchReturn(m *ir.Match, tmp string) error {
	for i := range m.Arms {
		arm := &m.Arms[i]
		switch p := arm.Pat.(type) {
		case *ir.LitPat:
			kw := "if"
			if i > 0 {
				kw = "} else if"
			}
			lit, err := g.expr(p.Val)
			if err != nil {
				return err
			}
			g.w(kw + " langGoEq(" + tmp + ", " + lit + ") {")
			g.indent++
			err = g.emitArmReturn(arm)
			g.indent--
			if err != nil {
				return err
			}
		case *ir.BindPat:
			g.w("} else {")
			g.indent++
			g.w(p.Name + " := " + tmp)
			g.w("_ = " + p.Name)
			err := g.emitArmReturn(arm)
			g.indent--
			if err != nil {
				return err
			}
		case *ir.WildPat:
			g.w("} else {")
			g.indent++
			err := g.emitArmReturn(arm)
			g.indent--
			if err != nil {
				return err
			}
		}
	}
	g.w("}")
	return nil
}

func (g *gen) emitArmReturn(arm *ir.Arm) error {
	expr, err := g.expr(arm.Body)
	if err != nil {
		return err
	}
	expr = g.wrapTo(expr, arm.Body, g.curMatchTyp)
	g.w("return " + expr)
	return nil
}

// emitFnLit 函数字面量：独立缓冲渲染后作为表达式内嵌
func (g *gen) emitFnLit(x *ir.FnLit) (string, error) {
	var ps []string
	for _, p := range x.Params {
		ps = append(ps, p.Name+" "+goTypeOf(p.Typ))
	}
	retDecl := ""
	if x.Ret != nil {
		retDecl = " " + goTypeOf(x.Ret)
	}
	savedBuf := g.buf
	savedIndent := g.indent
	savedFn := g.curFn
	g.curFn = &ir.Fn{Name: "匿名函数", Ret: x.Ret}
	g.buf = strings.Builder{}
	g.indent = 0
	g.w("func(" + strings.Join(ps, ", ") + ")" + retDecl + " {")
	var bodyErr error
	for _, st := range x.Body {
		if bodyErr = g.emitStmt(st); bodyErr != nil {
			break
		}
	}
	if bodyErr == nil {
		g.w("}")
	}
	g.curFn = savedFn
	out := g.buf.String()
	g.buf = savedBuf
	g.indent = savedIndent
	if bodyErr != nil {
		return "", bodyErr
	}
	// 内嵌进实参表时不能带尾随换行（Go 实参表内换行须跟逗号）
	return strings.TrimRight(out, "\n"), nil
}
