package genjs

// TranspileJS 把 Core IR 转译为自包含、可执行的 ES Module（prelude 内联）。
//
// v0.3 起本后端消费 ir.Prog：名字解析、类型推断、语义校验已在 semantic/types
// 完成。此处只做目标平台映射：int=BigInt（64 位语义精确，decisions #47）、
// 全函数 async（await-everything 吸收染色，decisions #48）、
// struct 值语义经 langGoCopy 三时机（绑定/传参/返回，decisions #50）。

import (
	"fmt"
	"strconv"
	"strings"

	"lang-go/internal/ir"
	"lang-go/internal/semantic"
	"lang-go/internal/types"
)

// TranspileJS IR → JS 源码
func TranspileJS(p *ir.Prog) (string, error) {
	g := &jsgen{
		prog:    p,
		structs: map[string]*ir.StructDecl{},
		fns:     map[*semantic.Symbol]*ir.Fn{},
	}
	for _, s := range p.Structs {
		g.structs[s.Name] = s
	}
	for _, f := range p.Fns {
		if f.Sym != nil {
			g.fns[f.Sym] = f
		}
		g.collectNested(f.Body)
	}
	if err := g.emitProgram(p); err != nil {
		return "", err
	}
	return g.buf.String(), nil
}

type jsgen struct {
	buf     strings.Builder
	indent  int
	prog    *ir.Prog
	structs map[string]*ir.StructDecl
	fns     map[*semantic.Symbol]*ir.Fn
	tmpN    int
	curFn   *ir.Fn
}

func (g *jsgen) errf(line int, format string, args ...any) error {
	return fmt.Errorf("第%d行: %s", line, fmt.Sprintf(format, args...))
}

func (g *jsgen) newTmp() string {
	g.tmpN++
	return fmt.Sprintf("_t%d", g.tmpN)
}

func (g *jsgen) w(line string) {
	g.buf.WriteString(strings.Repeat("  ", g.indent) + line + "\n")
}

func (g *jsgen) collectNested(list []ir.Stmt) {
	for _, s := range list {
		if fd, ok := s.(*ir.FnDef); ok {
			if fd.Fn.Sym != nil {
				g.fns[fd.Fn.Sym] = fd.Fn
			}
			g.collectNested(fd.Fn.Body)
		}
	}
}

// —— 输出 ——

func (g *jsgen) emitProgram(p *ir.Prog) error {
	g.buf.WriteString(jsPrelude)
	// 用户 enum 构造器
	for _, en := range p.Enums {
		for _, c := range en.Cases {
			params := make([]string, len(c.Payloads))
			for i := range c.Payloads {
				params[i] = fmt.Sprintf("p%d", i)
			}
			vals := "[]"
			if len(params) > 0 {
				vals = "[" + strings.Join(params, ", ") + "]"
			}
			fmt.Fprintf(&g.buf, "function le_%s_%s(%s) { return langGoEnumVal(%q, %s); }\n\n",
				en.Name, c.Name, strings.Join(params, ", "), c.Name, vals)
		}
	}
	// 全局绑定
	for _, gl := range p.Globals {
		expr, err := g.expr(gl.Init)
		if err != nil {
			return err
		}
		fmt.Fprintf(&g.buf, "let %s = langGoCopy(%s);\n\n", gl.Local.Name, expr)
	}
	// 方法与函数
	for _, f := range p.Fns {
		if err := g.emitFunc(f); err != nil {
			return err
		}
	}
	if p.Main != nil {
		g.buf.WriteString("await lf_main();\n")
	}
	return nil
}

func (g *jsgen) fnName(f *ir.Fn) string {
	if f.Recv != nil {
		recvName := "unknown"
		if st := f.Recv.Typ; st != nil && st.Kind == types.KStruct && st.Sym != nil {
			recvName = st.Sym.Name
		}
		return "lf_" + recvName + "_" + f.Name
	}
	return "lf_" + f.Name
}

func (g *jsgen) emitFunc(f *ir.Fn) error {
	saved := g.curFn
	g.curFn = f
	var ps []string
	if f.Recv != nil {
		ps = append(ps, f.Recv.Name)
	}
	for _, p := range f.Params {
		ps = append(ps, p.Name)
	}
	fmt.Fprintf(&g.buf, "async function %s(%s) {\n", g.fnName(f), strings.Join(ps, ", "))
	// 值语义：参数进入函数体即拷贝
	g.indent = 1
	if f.Recv != nil {
		fmt.Fprintf(&g.buf, "  %s = langGoCopy(%s);\n", f.Recv.Name, f.Recv.Name)
	}
	for _, p := range f.Params {
		fmt.Fprintf(&g.buf, "  %s = langGoCopy(%s);\n", p.Name, p.Name)
	}
	err := g.emitBody(f.Body)
	g.indent = 0
	g.curFn = saved
	if err != nil {
		return err
	}
	g.buf.WriteString("}\n\n")
	return nil
}

func (g *jsgen) emitBody(stmts []ir.Stmt) error {
	for _, st := range stmts {
		if err := g.emitStmt(st); err != nil {
			return err
		}
	}
	return nil
}

// —— 语句 ——

func (g *jsgen) emitStmt(s ir.Stmt) error {
	switch st := s.(type) {
	case *ir.Let:
		return g.emitLet(st)

	case *ir.Assign:
		return g.emitAssign(st)

	case *ir.Eval:
		if _, isMatch := st.X.(*ir.Match); !isMatch {
			expr, err := g.expr(st.X)
			if err != nil {
				return err
			}
			g.w(expr + ";")
			return nil
		}
		expr, err := g.expr(st.X)
		if err != nil {
			return err
		}
		g.w("await " + expr + ";")
		return nil

	case *ir.Return:
		return g.emitReturn(st)

	case *ir.If:
		cond, err := g.expr(st.Cond)
		if err != nil {
			return err
		}
		g.w("if (" + cond + ") {")
		g.emitBlock(st.Then)
		if len(st.Else) > 0 {
			g.w("} else {")
			g.emitBlock(st.Else)
		}
		g.w("}")
		return nil

	case *ir.While:
		cond, err := g.expr(st.Cond)
		if err != nil {
			return err
		}
		g.w("while (" + cond + ") {")
		g.emitBlock(st.Body)
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
		// →JS 单线程：go 同步立即执行（顺序确定；decisions #57）
		fi := g.fns[st.Fn]
		if fi == nil {
			return g.errf(0, "内部错误：go 目标未注册 %s", st.Fn.Name)
		}
		var args []string
		for _, a := range st.Args {
			arg, err := g.expr(a)
			if err != nil {
				return err
			}
			args = append(args, "langGoCopy("+arg+")")
		}
		g.w("await " + g.fnName(fi) + "(" + strings.Join(args, ", ") + ");")
		return nil

	case *ir.FnDef:
		// 嵌套 fn：JS 提升，递归可用
		saved := g.curFn
		g.curFn = st.Fn
		var ps []string
		if st.Fn.Recv != nil {
			ps = append(ps, st.Fn.Recv.Name)
		}
		for _, p := range st.Fn.Params {
			ps = append(ps, p.Name)
		}
		savedBuf := g.buf
		savedIndent := g.indent
		g.buf = strings.Builder{}
		g.indent = 0
		fmt.Fprintf(&g.buf, "async function %s(%s) {\n", g.fnName(st.Fn), strings.Join(ps, ", "))
		g.indent = 1
		if st.Fn.Recv != nil {
			fmt.Fprintf(&g.buf, "  %s = langGoCopy(%s);\n", st.Fn.Recv.Name, st.Fn.Recv.Name)
		}
		for _, p := range st.Fn.Params {
			fmt.Fprintf(&g.buf, "  %s = langGoCopy(%s);\n", p.Name, p.Name)
		}
		err := g.emitBody(st.Fn.Body)
		if err == nil {
			g.buf.WriteString("}\n")
		}
		g.curFn = saved
		g.indent = savedIndent
		out := g.buf.String()
		g.buf = savedBuf
		if err != nil {
			return err
		}
		g.w(strings.TrimRight(out, "\n"))
		return nil
	}
	return g.errf(0, "→JS 暂不支持该语句")
}

// emitLet let/var：? 在初始化顶层时先展开传播
func (g *jsgen) emitLet(st *ir.Let) error {
	name := st.Local.Name
	if t, ok := st.Init.(*ir.Try); ok {
		tmp, err := g.genTry(t)
		if err != nil {
			return err
		}
		if name != "_" {
			g.w("let " + name + " = langGoCopy(" + tmp + ".v);")
		}
		return nil
	}
	expr, err := g.expr(st.Init)
	if err != nil {
		return err
	}
	if name == "_" {
		g.w(expr + ";")
		return nil
	}
	if isRef(st.Init) {
		g.w("let " + name + " = " + expr + ";") // & 引用赋值：不拷贝
	} else {
		g.w("let " + name + " = langGoCopy(await " + expr + ");")
	}
	return nil
}

// isRef & 取引用表达式（赋值即共享，不拷贝）
func isRef(e ir.Expr) bool {
	u, ok := e.(*ir.Unary)
	return ok && u.Op == ir.OpRef
}

func (g *jsgen) emitAssign(st *ir.Assign) error {
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
			g.w(tgt.Local.Name + " = langGoCopy(" + tmp + ".v);")
			return nil
		}
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		if isRef(st.Val) {
			g.w(tgt.Local.Name + " = " + expr + ";")
		} else {
			g.w(tgt.Local.Name + " = langGoCopy(await " + expr + ");")
		}
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
		g.w(obj + "." + tgt.Name + " = langGoCopy(await " + expr + ");")
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
		g.w("langGoSetIndex(" + obj + ", " + idx + ", langGoCopy(await " + expr + "));")
		return nil
	}
	return g.errf(0, "赋值目标不合法")
}

// emitCompoundAssign 复合赋值：ident/field 原生 +=；数组 += 元素拼接；
// index 复合走 langGoAddIndex（Map 读改写）
func (g *jsgen) emitCompoundAssign(st *ir.Assign) error {
	op := string(st.Op)
	switch tgt := st.Target.(type) {
	case *ir.LocalGet:
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		if st.Op == ir.OpPlusA {
			// 数组 += 是元素拼接；数值/字符串原生 += 对 BigInt/number/string 均正确
			g.w("if (Array.isArray(" + tgt.Local.Name + ")) { " + tgt.Local.Name + ".push(...langGoIter(" + expr + ")) } else { " + tgt.Local.Name + " " + op + " " + expr + " }")
			return nil
		}
		g.w(tgt.Local.Name + " " + op + " " + expr + ";")
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
		g.w(obj + "." + tgt.Name + " " + op + " " + expr + ";")
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

func (g *jsgen) emitReturn(st *ir.Return) error {
	if st.Val == nil {
		g.w("return;")
		return nil
	}
	if t, ok := st.Val.(*ir.Try); ok {
		tmp, err := g.genTry(t)
		if err != nil {
			return err
		}
		g.w("return langGoCopy(" + tmp + ".v);")
		return nil
	}
	if g.curFn != nil && g.curFn.Ret != nil && g.curFn.Ret.Kind == types.KResult {
		// Result 函数的 return 必为 Ok(...)/Err(...)（checker 已约束）
		ctor, ok := st.Val.(*ir.Ctor)
		if !ok || (ctor.Sym.Name != "Ok" && ctor.Sym.Name != "Err") || len(ctor.Args) != 1 {
			return g.errf(0, "内部错误：Result return 形态异常")
		}
		expr, err := g.expr(ctor.Args[0])
		if err != nil {
			return err
		}
		if ctor.Sym.Name == "Ok" {
			g.w("return { __langGoRes: true, ok: true, v: langGoCopy(await " + expr + "), e: undefined };")
		} else {
			g.w("return { __langGoRes: true, ok: false, v: undefined, e: langGoCopy(" + expr + ") };")
		}
		return nil
	}
	expr, err := g.expr(st.Val)
	if err != nil {
		return err
	}
	g.w("return langGoCopy(await " + expr + ");")
	return nil
}

// genTry 展开 ? 传播
func (g *jsgen) genTry(t *ir.Try) (string, error) {
	if g.curFn == nil || g.curFn.Ret == nil || g.curFn.Ret.Kind != types.KResult {
		return "", g.errf(0, "带 ? 的函数须注解为 Result[T, error]")
	}
	tmp := g.newTmp()
	expr, err := g.expr(t.X)
	if err != nil {
		return "", err
	}
	g.w("const " + tmp + " = await " + expr + ";")
	g.w("if (!" + tmp + ".ok) {")
	g.indent++
	g.w("return { __langGoRes: true, ok: false, v: undefined, e: " + tmp + ".e };")
	g.indent--
	g.w("}")
	return tmp, nil
}

func (g *jsgen) emitForIn(st *ir.ForIn) error {
	name := st.Var.Name
	if st.Kind == ir.IterRange {
		// 区间字面量与经绑定变量两条路径（VALUE：for-in 区间半开）
		if rg, ok := st.Iter.(*ir.Range); ok {
			lo, err := g.expr(rg.Lo)
			if err != nil {
				return err
			}
			hi, err := g.expr(rg.Hi)
			if err != nil {
				return err
			}
			g.w("for (let " + name + " = " + lo + "; " + name + " < " + hi + "; " + name + "++) {")
			if err := g.emitBlockTyped(st.Body, name); err != nil {
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
		g.w("const " + tmp + " = " + rv + ";")
		g.w("for (let " + name + " = " + tmp + ".lo; " + name + " < " + tmp + ".hi; " + name + "++) {")
		if err := g.emitBlockTyped(st.Body, name); err != nil {
			return err
		}
		g.w("}")
		return nil
	}
	it, err := g.expr(st.Iter)
	if err != nil {
		return err
	}
	g.w("for (let " + name + " of langGoIter(" + it + ")) {")
	if err := g.emitBlockTyped(st.Body, name); err != nil {
		return err
	}
	g.w("}")
	return nil
}

func (g *jsgen) emitBlockTyped(stmts []ir.Stmt, name string) error {
	g.indent++
	g.w(name + " = langGoCopy(" + name + ");")
	err := g.emitBody(stmts)
	g.indent--
	return err
}

func (g *jsgen) emitBlock(stmts []ir.Stmt) error {
	g.indent++
	err := g.emitBody(stmts)
	g.indent--
	return err
}

// —— match ——

func (g *jsgen) emitMatchStmt(m *ir.Match) error {
	tmp := g.newTmp()
	scrut, err := g.expr(m.Scrut)
	if err != nil {
		return err
	}
	g.w("const " + tmp + " = " + scrut + ";")
	switch m.Kind {
	case ir.MatchResult:
		okArm, errArm := splitAlgebraic(m, "Ok", "Err")
		g.w("if (" + tmp + ".ok) {")
		g.indent++
		if okArm != nil {
			g.bindArmValue(okArm, tmp+".v")
			if err := g.emitArmStmt(okArm); err != nil {
				return err
			}
		}
		g.indent--
		g.w("} else {")
		g.indent++
		if errArm != nil {
			g.bindArmValue(errArm, tmp+".e")
			if err := g.emitArmStmt(errArm); err != nil {
				return err
			}
		}
		g.indent--
		g.w("}")
	case ir.MatchOption:
		someArm, noneArm := splitAlgebraic(m, "Some", "None")
		g.w("if (" + tmp + ".has) {")
		g.indent++
		if someArm != nil {
			g.bindArmValue(someArm, tmp+".v")
			if err := g.emitArmStmt(someArm); err != nil {
				return err
			}
		}
		g.indent--
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
		g.w("switch (" + tmp + ".tag) {")
		g.indent++
		hasDefault := false
		for i := range m.Arms {
			arm := &m.Arms[i]
			if p, ok := arm.Pat.(*ir.CtorPat); ok {
				g.w(`case "` + p.Sym.Name + `": {`)
				g.indent++
				if err := g.bindEnumPayloads(p, tmp); err != nil {
					return err
				}
				if err := g.emitArmStmt(arm); err != nil {
					return err
				}
				g.indent--
				g.w("}")
			} else {
				hasDefault = true
				g.w("default: {")
				g.indent++
				if bp, ok := arm.Pat.(*ir.BindPat); ok {
					g.w("const " + bp.Name + " = " + tmp + ";")
				}
				if err := g.emitArmStmt(arm); err != nil {
					return err
				}
				g.indent--
				g.w("}")
			}
		}
		if !hasDefault {
			g.w(`default: throw new Error("match 无匹配分支");`)
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

func armBindName(arm *ir.Arm) string {
	if p, ok := arm.Pat.(*ir.CtorPat); ok && len(p.Subs) == 1 {
		if bp, ok := p.Subs[0].(*ir.BindPat); ok {
			return bp.Name
		}
	}
	return "_"
}

func (g *jsgen) bindArmValue(arm *ir.Arm, valueExpr string) {
	if name := armBindName(arm); name != "_" {
		g.w("const " + name + " = langGoCopy(" + valueExpr + ");")
	}
}

func (g *jsgen) bindEnumPayloads(p *ir.CtorPat, tmp string) error {
	for j, sp := range p.Subs {
		if bp, ok := sp.(*ir.BindPat); ok {
			g.w("const " + bp.Name + " = langGoCopy(" + tmp + ".vals[" + strconv.Itoa(j) + "]);")
		}
	}
	return nil
}

func (g *jsgen) emitArmStmt(arm *ir.Arm) error {
	expr, err := g.expr(arm.Body)
	if err != nil {
		return err
	}
	g.w(expr + ";")
	return nil
}

func (g *jsgen) emitLiteralMatchStmt(m *ir.Match, tmp string) error {
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
			g.w(kw + " (langGoEq(" + tmp + ", " + lit + ")) {")
			if err := g.emitBlock([]ir.Stmt{&ir.Eval{X: arm.Body}}); err != nil {
				return err
			}
		case *ir.BindPat:
			g.w("} else {")
			g.indent++
			g.w("const " + p.Name + " = " + tmp + ";")
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

// —— 表达式 ——

func (g *jsgen) expr(e ir.Expr) (string, error) {
	switch x := e.(type) {
	case *ir.Const:
		switch x.Kind {
		case ir.KInt:
			return strconv.FormatInt(x.I64, 10) + "n", nil
		case ir.KFloat:
			return strconv.FormatFloat(x.F64, 'g', -1, 64), nil
		case ir.KStr:
			return strconv.Quote(x.S), nil
		case ir.KBool:
			return strconv.FormatBool(x.B), nil
		}

	case *ir.LocalGet:
		return x.Local.Name, nil

	case *ir.FnGet:
		if fi := g.fns[x.Sym]; fi != nil {
			return g.fnName(fi), nil
		}
		return "lf_" + x.Name, nil

	case *ir.BuiltinGet:
		return "", g.errf(0, "内建 %s 须直接调用", x.Name)

	case *ir.Ctor:
		return g.emitCtor(x)

	case *ir.Call:
		return g.emitCall(x)

	case *ir.Unary:
		inner, err := g.expr(x.X)
		if err != nil {
			return "", err
		}
		switch x.Op {
		case ir.OpNeg:
			return "-" + inner, nil
		case ir.OpNot:
			return "!" + inner, nil
		case ir.OpRef:
			// JS 对象赋值即共享引用，与 lang-go 的 & 语义天然对齐
			return inner, nil
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
		return "{ __langGoRange: true, lo: " + lo + ", hi: " + hi + " }", nil

	case *ir.Index:
		obj, err := g.expr(x.Obj)
		if err != nil {
			return "", err
		}
		idx, err := g.expr(x.Idx)
		if err != nil {
			return "", err
		}
		return "langGoIndex(" + obj + ", " + idx + ")", nil

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
		return obj + "." + x.Name, nil

	case *ir.Try:
		return "", g.errf(0, "? 只能出现在 let/return/赋值的顶层（→JS 约束）")

	case *ir.StructLit:
		name := ""
		if x.Struct != nil {
			name = x.Struct.Name
		}
		var fs []string
		fs = append(fs, "__langGoStruct: "+strconv.Quote(name))
		for _, f := range x.Fields {
			v, err := g.expr(f.Val)
			if err != nil {
				return "", err
			}
			fs = append(fs, f.Name+": langGoCopy("+v+")")
		}
		return "{ " + strings.Join(fs, ", ") + " }", nil

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
			kvs = append(kvs, "["+k+", langGoCopy("+v+")]")
		}
		return "langGoNewMap(" + strings.Join(kvs, ", ") + ")", nil

	case *ir.SliceLit:
		var items []string
		for _, el := range x.Elems {
			v, err := g.expr(el)
			if err != nil {
				return "", err
			}
			items = append(items, "langGoCopy("+v+")")
		}
		return "[" + strings.Join(items, ", ") + "]", nil

	case *ir.Match:
		return g.exprMatch(x)

	case *ir.FnLit:
		return g.emitFnLit(x)
	}
	return "", g.errf(0, "→JS 暂不支持该表达式")
}

// emitCtor 构造器
func (g *jsgen) emitCtor(x *ir.Ctor) (string, error) {
	switch x.Sym.Name {
	case "None":
		return "{ __langGoOpt: true, has: false, v: undefined }", nil
	case "Some":
		if len(x.Args) != 1 {
			return "", g.errf(0, "Some 需要 1 个参数")
		}
		arg, err := g.expr(x.Args[0])
		if err != nil {
			return "", err
		}
		return "{ __langGoOpt: true, has: true, v: langGoCopy(" + arg + ") }", nil
	case "Ok", "Err":
		return "", g.errf(0, "Ok/Err 只能出现在 Result 函数的 return（→JS 约束）")
	}
	en := ""
	for _, ed := range g.prog.Enums {
		for _, c := range ed.Cases {
			if c.Name == x.Sym.Name {
				en = ed.Name
			}
		}
	}
	var args []string
	for _, a := range x.Args {
		arg, err := g.expr(a)
		if err != nil {
			return "", err
		}
		args = append(args, "langGoCopy("+arg+")")
	}
	return "le_" + en + "_" + x.Sym.Name + "(" + strings.Join(args, ", ") + ")", nil
}

func (g *jsgen) emitBinary(x *ir.Binary) (string, error) {
	l, err := g.expr(x.L)
	if err != nil {
		return "", err
	}
	r, err := g.expr(x.R)
	if err != nil {
		return "", err
	}
	switch x.Op {
	case ir.OpAndAnd:
		return "(" + l + " && " + r + ")", nil
	case ir.OpOrOr:
		return "(" + l + " || " + r + ")", nil
	case ir.OpEq:
		return "langGoEq(" + l + ", " + r + ")", nil
	case ir.OpNeq:
		return "!langGoEq(" + l + ", " + r + ")", nil
	case ir.OpSlash:
		return "langGoDiv(" + l + ", " + r + ")", nil
	}
	return "(" + l + " " + string(x.Op) + " " + r + ")", nil
}

// emitCall 用户 fn / 内建 / 方法 / 函数值
func (g *jsgen) emitCall(x *ir.Call) (string, error) {
	// 内建
	if x.Builtin != "" {
		if x.Builtin == "http.get" {
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
		bf, ok := builtins[x.Builtin]
		if !ok {
			return "", g.errf(0, "未知内建 %s", x.Builtin)
		}
		var args []string
		for _, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			if x.Builtin == "push" {
				arg = "langGoCopy(" + arg + ")"
			}
			args = append(args, arg)
		}
		return bf + "(" + strings.Join(args, ", ") + ")", nil
	}
	// 方法：obj.m(args) → lf_<Struct>_<name>(obj, ...)（方法名全局唯一）
	if x.Method != nil {
		obj, err := g.expr(x.Recv)
		if err != nil {
			return "", err
		}
		fi := g.fns[x.Method]
		if fi == nil {
			return "", g.errf(0, "内部错误：方法未注册 %s", x.Method.Name)
		}
		var args []string
		for _, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			args = append(args, "langGoCopy("+arg+")")
		}
		return "await " + g.fnName(fi) + "(" + obj + ", " + strings.Join(args, ", ") + ")", nil
	}
	// 用户 fn
	if x.Fn != nil {
		fi := g.fns[x.Fn]
		if fi == nil {
			return "", g.errf(0, "内部错误：fn 未注册 %s", x.Fn.Name)
		}
		var args []string
		for _, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			args = append(args, "langGoCopy("+arg+")")
		}
		return "await " + g.fnName(fi) + "(" + strings.Join(args, ", ") + ")", nil
	}
	// 函数值（fnlit 绑定直通；字段函数值经 obj.f）
	var callee string
	var err error
	if lg, ok := x.Callee.(*ir.LocalGet); ok {
		callee = lg.Local.Name
	} else {
		callee, err = g.expr(x.Callee)
		if err != nil {
			return "", err
		}
	}
	var args []string
	for _, a := range x.Args {
		arg, err := g.expr(a)
		if err != nil {
			return "", err
		}
		args = append(args, arg)
	}
	return "await " + callee + "(" + strings.Join(args, ", ") + ")", nil
}

var builtins = map[string]string{
	"println": "langGoPrintln", "print": "langGoPrint", "len": "langGoLen",
	"str": "langGoStr", "int": "langGoInt", "float": "langGoFloat",
	"keys": "langGoKeys", "values": "langGoValues", "has": "langGoHas",
	"push": "langGoPush", "assert": "langGoAssert", "err": "langGoErr",
	"bytes": "langGoBytes", "char": "langGoChar",
	"jsonParse": "langGoJSONParse", "jsonStringify": "langGoJSONStringify",
	"isNone": "langGoIsNone", "makeChan": "langGoNewChan",
	"send": "langGoChanSend", "recv": "langGoChanRecv",
}

// exprMatch 表达式位置 match → 立即调用的 async 函数（调用侧 await）
func (g *jsgen) exprMatch(m *ir.Match) (string, error) {
	savedBuf := g.buf
	savedIndent := g.indent
	g.buf = strings.Builder{}
	g.indent = 1

	tmp := g.newTmp()
	scrut, err := g.expr(m.Scrut)
	if err != nil {
		g.buf = savedBuf
		g.indent = savedIndent
		return "", err
	}
	g.w("(async () => {")
	g.indent++
	g.w("const " + tmp + " = " + scrut + ";")
	var emitErr error
	switch m.Kind {
	case ir.MatchResult:
		okArm, errArm := splitAlgebraic(m, "Ok", "Err")
		g.w("if (" + tmp + ".ok) {")
		g.indent++
		if okArm != nil {
			g.bindArmValue(okArm, tmp+".v")
			emitErr = g.emitArmReturn(okArm)
		}
		g.indent--
		if emitErr == nil {
			g.w("} else {")
			g.indent++
			if errArm != nil {
				g.bindArmValue(errArm, tmp+".e")
				emitErr = g.emitArmReturn(errArm)
			}
			g.indent--
			g.w("}")
		}
	case ir.MatchOption:
		someArm, noneArm := splitAlgebraic(m, "Some", "None")
		g.w("if (" + tmp + ".has) {")
		g.indent++
		if someArm != nil {
			g.bindArmValue(someArm, tmp+".v")
			emitErr = g.emitArmReturn(someArm)
		}
		g.indent--
		if emitErr == nil && noneArm != nil {
			g.w("} else {")
			g.indent++
			emitErr = g.emitArmReturn(noneArm)
			g.indent--
			g.w("}")
		} else if emitErr == nil {
			g.w("}")
		}
	case ir.MatchEnum:
		g.w("switch (" + tmp + ".tag) {")
		g.indent++
		for i := range m.Arms {
			arm := &m.Arms[i]
			if p, ok := arm.Pat.(*ir.CtorPat); ok {
				g.w(`case "` + p.Sym.Name + `": {`)
				g.indent++
				emitErr = g.bindEnumPayloads(p, tmp)
				if emitErr == nil {
					emitErr = g.emitArmReturn(arm)
				}
				g.indent--
				g.w("}")
			} else {
				g.w("default: {")
				g.indent++
				if bp, ok := arm.Pat.(*ir.BindPat); ok {
					g.w("const " + bp.Name + " = " + tmp + ";")
				}
				emitErr = g.emitArmReturn(arm)
				g.indent--
				g.w("}")
			}
			if emitErr != nil {
				break
			}
		}
		g.indent--
		g.w("}")
	default:
		emitErr = g.emitLiteralMatchReturn(m, tmp)
	}
	g.indent--
	g.w("})()")
	out := g.buf.String()
	g.buf = savedBuf
	g.indent = savedIndent
	if emitErr != nil {
		return "", emitErr
	}
	// 片段自带 await：match 可出现在任意嵌套表达式位置（实参/操作数），
	// 而部分消费上下文（如调用实参拼接）不会再补 await。
	// await 对非 thenable 值透传，已有的 langGoCopy(await …) 上下文变成 await await，语义不变。
	return "await " + out, nil
}

func (g *jsgen) emitLiteralMatchReturn(m *ir.Match, tmp string) error {
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
			g.w(kw + " (langGoEq(" + tmp + ", " + lit + ")) {")
			g.indent++
			err = g.emitArmReturn(arm)
			g.indent--
			if err != nil {
				return err
			}
		case *ir.BindPat:
			g.w("} else {")
			g.indent++
			g.w("const " + p.Name + " = " + tmp + ";")
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

func (g *jsgen) emitArmReturn(arm *ir.Arm) error {
	expr, err := g.expr(arm.Body)
	if err != nil {
		return err
	}
	g.w("return langGoCopy(await " + expr + ");")
	return nil
}

// emitFnLit 函数字面量
func (g *jsgen) emitFnLit(x *ir.FnLit) (string, error) {
	var ps []string
	for _, p := range x.Params {
		ps = append(ps, p.Name)
	}
	savedBuf := g.buf
	savedIndent := g.indent
	savedFn := g.curFn
	g.curFn = &ir.Fn{Name: "匿名函数", Ret: x.Ret}
	g.buf = strings.Builder{}
	g.indent = 0
	g.w("async function(" + strings.Join(ps, ", ") + ") {")
	g.indent++
	for _, p := range x.Params {
		g.w(p.Name + " = langGoCopy(" + p.Name + ");")
	}
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
	return strings.TrimRight(out, "\n"), nil
}
