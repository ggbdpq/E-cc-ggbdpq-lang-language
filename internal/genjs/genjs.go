package genjs

// TranspileJS 把 ling 程序转译为自包含、可执行的 ES Module（prelude 内联）。
// 约束与 →Go 后端一致（spec §6.2）：参数注解必填、? 只在语句顶层、
// Result 函数 return 须为 Ok(...)/Err(...)、enum match 只支持构造/通配模式。
// 类型映射见 prelude.go 头注（decisions #47：int=BigInt 保 64 位语义）。
// 所有函数生成 async（http.get 为异步委托；同步调用 await 无害）。

import (
	"fmt"
	"strconv"
	"strings"

	"ling/internal/syntax"
)

func TranspileJS(prog []syntax.Stmt) (string, error) {
	g := &jsgen{
		enums:    map[string]*syntax.EnumDecl{},
		caseEnum: map[string]string{},
		structs:  map[string]*syntax.StructDecl{},
		fns:      map[string]*jsFn{},
		methods:  map[string]string{},
	}
	if err := g.collect(prog); err != nil {
		return "", err
	}
	g.buf.WriteString(jsPrelude)
	if err := g.emitProgram(prog); err != nil {
		return "", err
	}
	return g.buf.String(), nil
}

type jsParam struct {
	name string
}

type jsFn struct {
	goName  string
	params  []jsParam
	hasRet  bool // 有无返回注解（→JS 无类型，仅用于 Result 约束）
	retLing syntax.TypeEx
}

type jsgen struct {
	buf        strings.Builder
	indent     int
	enums      map[string]*syntax.EnumDecl
	caseEnum   map[string]string
	structs    map[string]*syntax.StructDecl
	fns        map[string]*jsFn
	methods    map[string]string // 方法名 → 产物函数名（跨 struct 重名在 collect 报错）
	scopes     []map[string]bool // 已声明名字（不参与代码生成，仅占位）
	tmpN       int
	curRetLing syntax.TypeEx
	curFn      string
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

func (g *jsgen) pushScope() { g.scopes = append(g.scopes, map[string]bool{}) }

func (g *jsgen) inScope(name string) bool {
	for i := len(g.scopes) - 1; i >= 0; i-- {
		if g.scopes[i][name] {
			return true
		}
	}
	return false
}
func (g *jsgen) popScope() { g.scopes = g.scopes[:len(g.scopes)-1] }

// ———— 收集 ————

func (g *jsgen) collect(prog []syntax.Stmt) error {
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.EnumDecl:
			if _, dup := g.enums[d.Name]; dup {
				return g.errf(d.Line, "enum %s 重复定义", d.Name)
			}
			g.enums[d.Name] = d
			for _, c := range d.Cases {
				if _, clash := g.caseEnum[c.Name]; clash {
					return g.errf(d.Line, "构造器 %s 重名", c.Name)
				}
				g.caseEnum[c.Name] = d.Name
			}
		case *syntax.StructDecl:
			if _, dup := g.structs[d.Name]; dup {
				return g.errf(d.Line, "struct %s 重复定义", d.Name)
			}
			g.structs[d.Name] = d
		case *syntax.FnDecl:
			if d.Recv != nil {
				// 方法名全局唯一（跨 struct 重名即报错；m4 与既有语料满足）
				if _, dup := g.methods[d.Name]; dup {
					return g.errf(d.Line, "方法名 %s 重复（→JS 方法名须全局唯一）", d.Name)
				}
				rt := "unknown"
				if nt, ok := d.Recv.Typ.(*syntax.NamedType); ok {
					rt = nt.Name
				}
				g.methods[d.Name] = "lf_" + rt + "_" + d.Name
				continue
			}
			if _, dup := g.fns[d.Name]; dup {
				return g.errf(d.Line, "fn %s 重复定义", d.Name)
			}
			info, err := g.fnInfoOf(d)
			if err != nil {
				return err
			}
			g.fns[d.Name] = info
		case *syntax.ExternDecl:
			for _, fn := range d.Fns {
				if _, dup := g.fns[fn.Name]; dup {
					return g.errf(d.Line, "fn %s 重复定义", fn.Name)
				}
				info, err := g.fnInfoOf(fn)
				if err != nil {
					return err
				}
				g.fns[fn.Name] = info
			}
		}
	}
	return nil
}

func (g *jsgen) fnInfoOf(d *syntax.FnDecl) (*jsFn, error) {
	info := &jsFn{goName: "lf_" + d.Name, hasRet: d.Ret != nil, retLing: d.Ret}
	for _, p := range d.Params {
		if p.Typ == nil {
			return nil, g.errf(d.Line, "→JS 要求参数 %s 有类型注解", p.Name)
		}
		info.params = append(info.params, jsParam{name: p.Name})
	}
	return info, nil
}

// ———— 输出 ————

func (g *jsgen) emitProgram(prog []syntax.Stmt) error {
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.EnumDecl:
			for _, c := range d.Cases {
				params := make([]string, len(c.Payloads))
				for i := range c.Payloads {
					params[i] = fmt.Sprintf("p%d", i)
				}
				vals := "[]"
				if len(params) > 0 {
					vals = "[" + strings.Join(params, ", ") + "]"
				}
				fmt.Fprintf(&g.buf, "function le_%s_%s(%s) { return lingEnumVal(%q, %s); }\n\n",
					d.Name, c.Name, strings.Join(params, ", "), c.Name, vals)
			}
		}
	}
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.LetStmt:
			expr, err := g.expr(d.Init)
			if err != nil {
				return err
			}
			fmt.Fprintf(&g.buf, "let %s = lingCopy(%s);\n\n", d.Name, expr)
		}
	}
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.FnDecl:
			var err error
			if d.Recv != nil {
				err = g.emitMethod(d)
			} else {
				err = g.emitFunc(d)
			}
			if err != nil {
				return err
			}
		case *syntax.ExternDecl:
			for _, fn := range d.Fns {
				fi := g.fns[fn.Name]
				ps := make([]string, len(fi.params))
				for i, p := range fi.params {
					ps[i] = p.name
				}
				fmt.Fprintf(&g.buf, "async function %s(%s) { throw new Error(\"extern 未链接: %s（宿主 %s）\"); }\n\n",
					fi.goName, strings.Join(ps, ", "), fn.Name, d.Host)
			}
		case *syntax.ImportStmt:
			// 装载器已展开
		}
	}
	if _, has := g.fns["main"]; has {
		g.buf.WriteString("await lf_main();\n")
	}
	return nil
}

func lingEnumVal(tag string, vals string) string {
	return fmt.Sprintf("{ __lingEnum: true, tag: %q, vals: %s }", tag, vals)
}

func (g *jsgen) emitFunc(d *syntax.FnDecl) error {
	fi := g.fns[d.Name]
	return g.emitFuncBody(d, fi.goName, fi)
}

func (g *jsgen) emitMethod(d *syntax.FnDecl) error {
	goName := g.methods[d.Name]
	fi := &jsFn{goName: goName, hasRet: d.Ret != nil, retLing: d.Ret,
		params: append([]jsParam{{name: d.Recv.Name}}, methodParams(d)...)}
	return g.emitFuncBody(d, goName, fi)
}

func methodParams(d *syntax.FnDecl) []jsParam {
	out := make([]jsParam, len(d.Params))
	for i, p := range d.Params {
		out[i] = jsParam{name: p.Name}
	}
	return out
}

func (g *jsgen) emitFuncBody(d *syntax.FnDecl, goName string, fi *jsFn) error {
	var ps []string
	for _, p := range fi.params {
		ps = append(ps, p.name)
	}
	fmt.Fprintf(&g.buf, "async function %s(%s) {\n", goName, strings.Join(ps, ", "))
	saved := g.pushCtx(d, fi)
	g.pushScope()
	// 值语义：参数进入函数体即拷贝；名字进 scopes（函数值参数直通调用）
	for _, p := range fi.params {
		g.scopes[len(g.scopes)-1][p.name] = true
		fmt.Fprintf(&g.buf, "  %s = lingCopy(%s);\n", p.name, p.name)
	}
	err := g.emitBody(d.Body)
	g.popCtx(saved)
	if err != nil {
		return err
	}
	g.buf.WriteString("}\n\n")
	return nil
}

type jsCtx struct {
	indent     int
	curRetLing syntax.TypeEx
	curFn      string
}

func (g *jsgen) pushCtx(d *syntax.FnDecl, fi *jsFn) jsCtx {
	s := jsCtx{g.indent, g.curRetLing, g.curFn}
	g.indent = 1
	g.curRetLing = d.Ret
	g.curFn = d.Name
	g.scopes = []map[string]bool{{}}
	return s
}

func (g *jsgen) popCtx(s jsCtx) {
	g.indent = s.indent
	g.curRetLing = s.curRetLing
	g.curFn = s.curFn
}

func (g *jsgen) emitBody(stmts []syntax.Stmt) error {
	for _, st := range stmts {
		if err := g.emitStmt(st); err != nil {
			return err
		}
	}
	return nil
}

// ———— 语句 ————

func (g *jsgen) emitStmt(s syntax.Stmt) error {
	switch st := s.(type) {
	case *syntax.LetStmt:
		return g.emitLet(st.Name, st.Typ, st.Init, st.Line)
	case *syntax.AssignStmt:
		return g.emitAssign(st)
	case *syntax.ExprStmt:
		if _, isMatch := st.X.(*syntax.MatchEx); !isMatch {
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
	case *syntax.ReturnStmt:
		return g.emitReturn(st)
	case *syntax.IfStmt:
		cond, err := g.expr(st.Cond)
		if err != nil {
			return err
		}
		g.w("if (" + cond + ") {")
		g.emitBlock(st.Then)
		if st.Else != nil {
			g.w("} else {")
			g.emitBlock(st.Else)
		}
		g.w("}")
		return nil
	case *syntax.WhileStmt:
		cond, err := g.expr(st.Cond)
		if err != nil {
			return err
		}
		g.w("while (" + cond + ") {")
		g.emitBlock(st.Body)
		g.w("}")
		return nil
	case *syntax.ForInStmt:
		return g.emitForIn(st)
	case *syntax.BreakStmt:
		g.w("break")
		return nil
	case *syntax.ContinueStmt:
		g.w("continue")
		return nil
	case *syntax.FnDecl:
		info := &jsFn{goName: "lf_" + st.Name, hasRet: st.Ret != nil, retLing: st.Ret}
		for _, p := range st.Params {
			if p.Typ == nil {
				return g.errf(st.Line, "→JS 要求参数 %s 有类型注解", p.Name)
			}
			info.params = append(info.params, jsParam{name: p.Name})
		}
		g.fns[st.Name] = info
		return g.emitFuncBodyInto(st, info.goName, info)
	case *syntax.ImportStmt, *syntax.ExternDecl, *syntax.StructDecl, *syntax.EnumDecl:
		return nil // 装载器展开 / 已在程序头处理
	}
	return g.errf(s.Pos(), "→JS 暂不支持该语句")
}

func (g *jsgen) emitBlock(stmts []syntax.Stmt) error {
	g.indent++
	g.pushScope()
	err := g.emitBody(stmts)
	g.popScope()
	g.indent--
	return err
}

// emitFuncBodyInto 嵌套 fn：函数内声明（JS 提升，递归可用）
func (g *jsgen) emitFuncBodyInto(d *syntax.FnDecl, goName string, fi *jsFn) error {
	savedBuf := g.buf
	g.buf = strings.Builder{}
	savedIndent := g.indent
	g.indent = 0
	savedCtx := g.pushCtx(d, fi)
	var ps []string
	for _, p := range fi.params {
		ps = append(ps, p.name)
	}
	fmt.Fprintf(&g.buf, "async function %s(%s) {\n", goName, strings.Join(ps, ", "))
	g.indent = 1
	for _, p := range fi.params {
		fmt.Fprintf(&g.buf, "  %s = lingCopy(%s);\n", p.name, p.name)
	}
	err := g.emitBody(d.Body)
	if err == nil {
		g.buf.WriteString("}\n")
	}
	g.popCtx(savedCtx)
	g.indent = savedIndent
	out := g.buf.String()
	g.buf = savedBuf
	if err != nil {
		return err
	}
	g.w(strings.TrimRight(out, "\n"))
	return nil
}

func (g *jsgen) emitLet(name string, typ syntax.TypeEx, init syntax.Expr, line int) error {
	if t, ok := init.(*syntax.TryEx); ok {
		tmp, err := g.genTry(t)
		if err != nil {
			return err
		}
		if name != "_" {
			g.w("let " + name + " = lingCopy(" + tmp + ".v);")
		}
		return nil
	}
	expr, err := g.expr(init)
	if err != nil {
		return err
	}
	if name == "_" {
		g.w(expr + ";")
		return nil
	}
	if isRef(init) {
		g.w("let " + name + " = " + expr + ";") // & 引用赋值：不拷贝
	} else {
		g.w("let " + name + " = lingCopy(await " + expr + ");")
	}
	g.registerFnValue(name, init)
	return nil
}

// isRef & 取引用表达式（赋值即共享，不拷贝）
func isRef(e syntax.Expr) bool {
	u, ok := e.(*syntax.Unary)
	return ok && u.Op == syntax.Amp
}

// registerFnValue 函数值绑定注册调用名（调用点生成 await name(...) 而非 lf_ 前缀）
func (g *jsgen) registerFnValue(name string, init syntax.Expr) {
	if fl, ok := init.(*syntax.FnLit); ok {
		g.fns[name] = &jsFn{goName: name, hasRet: fl.Ret != nil, retLing: fl.Ret}
	}
}

func (g *jsgen) emitAssign(st *syntax.AssignStmt) error {
	switch tgt := st.Target.(type) {
	case *syntax.Ident:
		if t, ok := st.Val.(*syntax.TryEx); ok {
			tmp, err := g.genTry(t)
			if err != nil {
				return err
			}
			g.w(tgt.Name + " = lingCopy(" + tmp + ".v);")
			return nil
		}
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		if isRef(st.Val) {
			g.w(tgt.Name + " = " + expr + ";")
		} else {
			g.w(tgt.Name + " = lingCopy(await " + expr + ");")
		}
		return nil
	case *syntax.FieldEx:
		obj, err := g.expr(tgt.Obj)
		if err != nil {
			return err
		}
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		g.w(obj + "." + tgt.Name + " = lingCopy(await " + expr + ");")
		return nil
	case *syntax.IndexEx:
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
		g.w("lingSetIndex(" + obj + ", " + idx + ", lingCopy(await " + expr + "));")
		return nil
	}
	return g.errf(st.Line, "赋值目标不合法")
}

func (g *jsgen) isResultFn() bool {
	rt, ok := g.curRetLing.(*syntax.NamedType)
	return ok && rt.Name == "Result"
}

func (g *jsgen) emitReturn(st *syntax.ReturnStmt) error {
	if st.Val == nil {
		g.w("return;")
		return nil
	}
	if t, ok := st.Val.(*syntax.TryEx); ok {
		tmp, err := g.genTry(t)
		if err != nil {
			return err
		}
		g.w("return lingCopy(" + tmp + ".v);")
		return nil
	}
	if g.isResultFn() {
		call, ok := st.Val.(*syntax.CallEx)
		if !ok {
			return g.errf(st.Line, "Result 函数的 return 须为 Ok(...) 或 Err(...)")
		}
		id, ok := call.Callee.(*syntax.Ident)
		if !ok || (id.Name != "Ok" && id.Name != "Err") || len(call.Args) != 1 {
			return g.errf(st.Line, "Result 函数的 return 须为 Ok(...) 或 Err(...)")
		}
		expr, err := g.expr(call.Args[0])
		if err != nil {
			return err
		}
		if id.Name == "Ok" {
			g.w("return { __lingRes: true, ok: true, v: lingCopy(await " + expr + "), e: undefined };")
		} else {
			g.w("return { __lingRes: true, ok: false, v: undefined, e: lingCopy(" + expr + ") };")
		}
		return nil
	}
	expr, err := g.expr(st.Val)
	if err != nil {
		return err
	}
	g.w("return lingCopy(await " + expr + ");")
	return nil
}

// genTry 展开 ? 传播
func (g *jsgen) genTry(t *syntax.TryEx) (string, error) {
	if !g.isResultFn() {
		return "", g.errf(t.Line, "带 ? 的函数须注解为 Result[T, error]")
	}
	tmp := g.newTmp()
	expr, err := g.expr(t.X)
	if err != nil {
		return "", err
	}
	g.w("const " + tmp + " = await " + expr + ";")
	g.w("if (!" + tmp + ".ok) {")
	g.indent++
	g.w("return { __lingRes: true, ok: false, v: undefined, e: " + tmp + ".e };")
	g.indent--
	g.w("}")
	return tmp, nil
}

func (g *jsgen) emitForIn(st *syntax.ForInStmt) error {
	name := st.Var
	if rg, ok := st.Iter.(*syntax.RangeEx); ok {
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
	it, err := g.expr(st.Iter)
	if err != nil {
		return err
	}
	g.w("for (let " + name + " of lingIter(" + it + ")) {")
	if err := g.emitBlockTyped(st.Body, name); err != nil {
		return err
	}
	g.w("}")
	return nil
}

func (g *jsgen) emitBlockTyped(stmts []syntax.Stmt, name string) error {
	g.indent++
	g.pushScope()
	g.w(name + " = lingCopy(" + name + ");")
	err := g.emitBody(stmts)
	g.popScope()
	g.indent--
	return err
}

// ———— match ————

func (g *jsgen) classifyMatch(m *syntax.MatchEx) (string, error) {
	flags := map[string]bool{}
	for _, arm := range m.Arms {
		switch p := arm.Pat.(type) {
		case *syntax.CasePat:
			flags[p.Name] = true
		case *syntax.LitPat:
			flags["__lit"] = true
		case *syntax.WildPat, *syntax.BindPat:
			flags["__wild"] = true
		}
	}
	algebraic := flags["Ok"] || flags["Err"] || flags["Some"] || flags["None"]
	switch {
	case algebraic:
		if flags["__lit"] {
			return "", g.errf(m.Line, "代数类型的 match 不能混入字面量模式")
		}
		if (flags["Ok"] || flags["Err"]) && (flags["Some"] || flags["None"]) {
			return "", g.errf(m.Line, "Result 与 Option 的模式混用")
		}
		if flags["Ok"] || flags["Err"] {
			return "result", nil
		}
		return "option", nil
	case flags["__lit"]:
		if hasCasePat(m) {
			return "", g.errf(m.Line, "enum 的 match 不能混入字面量模式（→JS）")
		}
		return "literal", nil
	case hasCasePat(m):
		return "enum", nil
	default:
		return "literal", nil
	}
}

func hasCasePat(m *syntax.MatchEx) bool {
	for _, arm := range m.Arms {
		if _, ok := arm.Pat.(*syntax.CasePat); ok {
			return true
		}
	}
	return false
}

func (g *jsgen) checkExhaustive(m *syntax.MatchEx) error {
	covered := map[string]bool{}
	hasWild := false
	var enumName string
	for _, arm := range m.Arms {
		switch p := arm.Pat.(type) {
		case *syntax.CasePat:
			en, ok := g.caseEnum[p.Name]
			if !ok {
				return g.errf(arm.Line, "未知构造器 %s", p.Name)
			}
			if enumName == "" {
				enumName = en
			}
			if en != enumName {
				return g.errf(arm.Line, "match 混入了不同 enum 的构造器")
			}
			covered[p.Name] = true
		case *syntax.WildPat, *syntax.BindPat:
			hasWild = true
		}
	}
	if hasWild || enumName == "" {
		return nil
	}
	var missing []string
	for _, c := range g.enums[enumName].Cases {
		if !covered[c.Name] {
			missing = append(missing, c.Name)
		}
	}
	if len(missing) > 0 {
		return g.errf(m.Line, "match 不穷尽，缺少: %s", strings.Join(missing, ", "))
	}
	return nil
}

func (g *jsgen) emitMatchStmt(m *syntax.MatchEx) error {
	kind, err := g.classifyMatch(m)
	if err != nil {
		return err
	}
	if kind == "enum" {
		if err := g.checkExhaustive(m); err != nil {
			return err
		}
	}
	tmp := g.newTmp()
	scrut, err := g.expr(m.Scrut)
	if err != nil {
		return err
	}
	g.w("const " + tmp + " = " + scrut + ";")
	switch kind {
	case "result":
		okArm, errArm := splitAlgebraic(m, "Ok", "Err")
		g.w("if (" + tmp + ".ok) {")
		g.indent++
		g.pushScope()
		if okArm != nil {
			g.bindArmValue(okArm, tmp+".v")
			if err := g.emitArmStmt(okArm); err != nil {
				return err
			}
		}
		g.popScope()
		g.indent--
		g.w("} else {")
		g.indent++
		g.pushScope()
		if errArm != nil {
			g.bindArmValue(errArm, tmp+".e")
			if err := g.emitArmStmt(errArm); err != nil {
				return err
			}
		}
		g.popScope()
		g.indent--
		g.w("}")
	case "option":
		someArm, noneArm := splitAlgebraic(m, "Some", "None")
		g.w("if (" + tmp + ".has) {")
		g.indent++
		g.pushScope()
		if someArm != nil {
			g.bindArmValue(someArm, tmp+".v")
			if err := g.emitArmStmt(someArm); err != nil {
				return err
			}
		}
		g.popScope()
		g.indent--
		if noneArm != nil {
			g.w("} else {")
			g.indent++
			g.pushScope()
			if err := g.emitArmStmt(noneArm); err != nil {
				return err
			}
			g.popScope()
			g.indent--
		}
		g.w("}")
	case "enum":
		g.w("switch (" + tmp + ".tag) {")
		g.indent++
		hasDefault := false
		for i := range m.Arms {
			arm := &m.Arms[i]
			if p, ok := arm.Pat.(*syntax.CasePat); ok {
				g.w(`case "` + p.Name + `": {`)
				g.indent++
				g.pushScope()
				if err := g.bindEnumPayloads(p, tmp); err != nil {
					return err
				}
				if err := g.emitArmStmt(arm); err != nil {
					return err
				}
				g.popScope()
				g.indent--
				g.w("}")
			} else {
				hasDefault = true
				g.w("default: {")
				g.indent++
				g.pushScope()
				if bp, ok := arm.Pat.(*syntax.BindPat); ok {
					g.w("const " + bp.Name + " = " + tmp + ";")
				}
				if err := g.emitArmStmt(arm); err != nil {
					return err
				}
				g.popScope()
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

func splitAlgebraic(m *syntax.MatchEx, posName, negName string) (pos, neg *syntax.Arm) {
	for i := range m.Arms {
		if p, ok := m.Arms[i].Pat.(*syntax.CasePat); ok {
			if p.Name == posName {
				pos = &m.Arms[i]
			} else if p.Name == negName {
				neg = &m.Arms[i]
			}
		}
	}
	return pos, neg
}

func armBindName(arm *syntax.Arm) string {
	if p, ok := arm.Pat.(*syntax.CasePat); ok && len(p.Subs) == 1 {
		if bp, ok := p.Subs[0].(*syntax.BindPat); ok {
			return bp.Name
		}
	}
	return "_"
}

func (g *jsgen) bindArmValue(arm *syntax.Arm, valueExpr string) {
	if name := armBindName(arm); name != "_" {
		g.w("const " + name + " = lingCopy(" + valueExpr + ");")
	}
}

func (g *jsgen) bindEnumPayloads(p *syntax.CasePat, tmp string) error {
	en, ok := g.enums[g.caseEnum[p.Name]]
	if !ok {
		return nil
	}
	for _, cd := range en.Cases {
		if cd.Name != p.Name {
			continue
		}
		for j, sp := range p.Subs {
			if bp, ok := sp.(*syntax.BindPat); ok {
				g.w("const " + bp.Name + " = lingCopy(" + tmp + ".vals[" + strconv.Itoa(j) + "]);")
			}
		}
	}
	return nil
}

func (g *jsgen) emitArmStmt(arm *syntax.Arm) error {
	if m, ok := arm.Body.(*syntax.MatchEx); ok {
		expr, err := g.expr(m)
		if err != nil {
			return err
		}
		g.w("await " + expr + ";")
		return nil
	}
	expr, err := g.expr(arm.Body)
	if err != nil {
		return err
	}
	g.w(expr + ";")
	return nil
}

func (g *jsgen) emitLiteralMatchStmt(m *syntax.MatchEx, tmp string) error {
	for i := range m.Arms {
		arm := &m.Arms[i]
		switch p := arm.Pat.(type) {
		case *syntax.LitPat:
			kw := "if"
			if i > 0 {
				kw = "} else if"
			}
			lit, err := g.expr(p.Val)
			if err != nil {
				return err
			}
			g.w(kw + " (lingEq(" + tmp + ", " + lit + ")) {")
			if err := g.emitBlock([]syntax.Stmt{&syntax.ExprStmt{X: arm.Body, Line: arm.Line}}); err != nil {
				return err
			}
		case *syntax.BindPat:
			g.w("} else {")
			g.indent++
			g.pushScope()
			g.w("const " + p.Name + " = " + tmp + ";")
			if err := g.emitArmStmt(arm); err != nil {
				return err
			}
			g.popScope()
			g.indent--
		case *syntax.WildPat:
			g.w("} else {")
			if err := g.emitBlock([]syntax.Stmt{&syntax.ExprStmt{X: arm.Body, Line: arm.Line}}); err != nil {
				return err
			}
		default:
			return g.errf(arm.Line, "该模式不支持（→JS）")
		}
	}
	g.w("}")
	return nil
}

// ———— 表达式 ————

func (g *jsgen) expr(e syntax.Expr) (string, error) {
	switch x := e.(type) {
	case *syntax.IntLit:
		return strconv.FormatInt(x.Val, 10) + "n", nil
	case *syntax.FloatLit:
		return strconv.FormatFloat(x.Val, 'g', -1, 64), nil
	case *syntax.StrLit:
		return strconv.Quote(x.Val), nil
	case *syntax.BoolLit:
		return strconv.FormatBool(x.Val), nil
	case *syntax.Ident:
		if g.isBuiltinFn(x.Name) {
			return "", g.errf(x.Line, "内建 %s 须直接调用", x.Name)
		}
		if en, isCtor := g.caseEnum[x.Name]; isCtor {
			return "le_" + en + "_" + x.Name + "()", nil
		}
		if x.Name == "None" {
			return "{ __lingOpt: true, has: false, v: undefined }", nil
		}
		if x.Name == "http" {
			return "lingHTTP", nil
		}
		if fi, isFn := g.fns[x.Name]; isFn {
			return fi.goName, nil
		}
		return x.Name, nil
	case *syntax.Unary:
		inner, err := g.expr(x.X)
		if err != nil {
			return "", err
		}
		switch x.Op {
		case syntax.Minus:
			return "-" + inner, nil
		case syntax.Not:
			return "!" + inner, nil
		case syntax.Amp:
			// JS 对象赋值即共享引用，与 ling 的 & 语义天然对齐
			return g.expr(x.X)
		}
		return "", g.errf(x.Line, "未知一元运算")
	case *syntax.Binary:
		l, err := g.expr(x.L)
		if err != nil {
			return "", err
		}
		r, err := g.expr(x.R)
		if err != nil {
			return "", err
		}
		op := ""
		switch x.Op {
		case syntax.And:
			return "(" + l + " && " + r + ")", nil
		case syntax.Or:
			return "(" + l + " || " + r + ")", nil
		case syntax.Eq:
			return "lingEq(" + l + ", " + r + ")", nil
		case syntax.Neq:
			return "!lingEq(" + l + ", " + r + ")", nil
		case syntax.Plus:
			op = "+"
		case syntax.Minus:
			op = "-"
		case syntax.Star:
			op = "*"
		case syntax.Slash:
			return "lingDiv(" + l + ", " + r + ")", nil
		case syntax.Percent:
			return "(" + l + " % " + r + ")", nil
		case syntax.Amp:
			op = "&"
		case syntax.BitOr:
			op = "|"
		case syntax.Xor:
			op = "^"
		case syntax.Shl:
			op = "<<"
		case syntax.Shr:
			op = ">>"
		case syntax.Lt:
			op = "<"
		case syntax.Le:
			op = "<="
		case syntax.Gt:
			op = ">"
		case syntax.Ge:
			op = ">="
		default:
			return "", g.errf(x.Line, "→JS 暂不支持该运算")
		}
		return "(" + l + " " + op + " " + r + ")", nil
	case *syntax.RangeEx:
		lo, err := g.expr(x.Lo)
		if err != nil {
			return "", err
		}
		hi, err := g.expr(x.Hi)
		if err != nil {
			return "", err
		}
		return "{ __lingRange: true, lo: " + lo + ", hi: " + hi + " }", nil
	case *syntax.CallEx:
		return g.emitCall(x)
	case *syntax.IndexEx:
		obj, err := g.expr(x.Obj)
		if err != nil {
			return "", err
		}
		idx, err := g.expr(x.Idx)
		if err != nil {
			return "", err
		}
		return "lingIndex(" + obj + ", " + idx + ")", nil
	case *syntax.SliceEx:
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
		return "lingSlice(" + obj + ", " + lo + ", " + hi + ")", nil
	case *syntax.FieldEx:
		obj, err := g.expr(x.Obj)
		if err != nil {
			return "", err
		}
		return obj + "." + x.Name, nil
	case *syntax.TryEx:
		return "", g.errf(x.Line, "? 只能出现在 let/return/赋值的顶层（→JS 约束）")
	case *syntax.StructLit:
		var fs []string
		fs = append(fs, "__lingStruct: "+strconv.Quote(x.TypeName))
		for _, f := range x.Fields {
			v, err := g.expr(f.Val)
			if err != nil {
				return "", err
			}
			fs = append(fs, f.Name+": lingCopy("+v+")")
		}
		return "{ " + strings.Join(fs, ", ") + " }", nil
	case *syntax.MapLit:
		var kvs []string
		for _, item := range x.Items {
			k, err := g.expr(item.Key)
			if err != nil {
				return "", err
			}
			v, err := g.expr(item.Val)
			if err != nil {
				return "", err
			}
			kvs = append(kvs, "["+k+", lingCopy("+v+")]")
		}
		return "lingNewMap(" + strings.Join(kvs, ", ") + ")", nil
	case *syntax.ArrLit:
		var items []string
		for _, el := range x.Items {
			v, err := g.expr(el)
			if err != nil {
				return "", err
			}
			items = append(items, "lingCopy("+v+")")
		}
		return "[" + strings.Join(items, ", ") + "]", nil
	case *syntax.MatchEx:
		return g.exprMatch(x)
	case *syntax.FnLit:
		return g.emitFnLit(x)
	}
	return "", g.errf(e.Pos(), "→JS 暂不支持该表达式")
}

func (g *jsgen) emitFnLit(x *syntax.FnLit) (string, error) {
	var ps []string
	for _, p := range x.Params {
		if p.Typ == nil {
			return "", g.errf(x.Line, "→JS 要求函数字面量参数 %s 有类型注解", p.Name)
		}
		ps = append(ps, p.Name)
	}
	savedBuf := g.buf
	savedIndent := g.indent
	g.buf = strings.Builder{}
	g.indent = 0
	savedRet, savedFn := g.curRetLing, g.curFn
	g.curRetLing = x.Ret
	g.curFn = "匿名函数"
	g.w("async function(" + strings.Join(ps, ", ") + ") {")
	g.indent++
	for _, p := range x.Params {
		g.w(p.Name + " = lingCopy(" + p.Name + ");")
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
	g.curRetLing, g.curFn = savedRet, savedFn
	g.indent = savedIndent
	out := g.buf.String()
	g.buf = savedBuf
	if bodyErr != nil {
		return "", bodyErr
	}
	return strings.TrimRight(out, "\n"), nil
}

// exprMatch 表达式位置 match → 立即调用的 async 函数（调用侧 await）
func (g *jsgen) exprMatch(m *syntax.MatchEx) (string, error) {
	kind, err := g.classifyMatch(m)
	if err != nil {
		return "", err
	}
	if kind == "enum" {
		if err := g.checkExhaustive(m); err != nil {
			return "", err
		}
	}
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
	g.pushScope()
	g.w("const " + tmp + " = " + scrut + ";")
	var emitErr error
	switch kind {
	case "result":
		okArm, errArm := splitAlgebraic(m, "Ok", "Err")
		g.w("if (" + tmp + ".ok) {")
		g.indent++
		g.pushScope()
		if okArm != nil {
			g.bindArmValue(okArm, tmp+".v")
			emitErr = g.emitArmReturn(okArm)
		}
		g.popScope()
		g.indent--
		if emitErr == nil {
			g.w("} else {")
			g.indent++
			g.pushScope()
			if errArm != nil {
				g.bindArmValue(errArm, tmp+".e")
				emitErr = g.emitArmReturn(errArm)
			}
			g.popScope()
			g.indent--
			g.w("}")
		}
	case "option":
		someArm, noneArm := splitAlgebraic(m, "Some", "None")
		g.w("if (" + tmp + ".has) {")
		g.indent++
		g.pushScope()
		if someArm != nil {
			g.bindArmValue(someArm, tmp+".v")
			emitErr = g.emitArmReturn(someArm)
		}
		g.popScope()
		g.indent--
		if emitErr == nil && noneArm != nil {
			g.w("} else {")
			g.indent++
			g.pushScope()
			if err := g.emitArmReturn(noneArm); err != nil {
				emitErr = err
			}
			g.popScope()
			g.indent--
			g.w("}")
		} else if emitErr == nil {
			g.w("}")
		}
	case "enum":
		g.w("switch (" + tmp + ".tag) {")
		g.indent++
		for i := range m.Arms {
			arm := &m.Arms[i]
			if p, ok := arm.Pat.(*syntax.CasePat); ok {
				g.w(`case "` + p.Name + `": {`)
				g.indent++
				g.pushScope()
				if err := g.bindEnumPayloads(p, tmp); err != nil {
					emitErr = err
				}
				if emitErr == nil {
					emitErr = g.emitArmReturn(arm)
				}
				g.popScope()
				g.indent--
				g.w("}")
			} else {
				g.w("default: {")
				g.indent++
				g.pushScope()
				if bp, ok := arm.Pat.(*syntax.BindPat); ok {
					g.w("const " + bp.Name + " = " + tmp + ";")
				}
				if err := g.emitArmReturn(arm); err != nil {
					emitErr = err
				}
				g.popScope()
				g.indent--
				g.w("}")
			}
		}
		g.indent--
		g.w("}")
	default:
		emitErr = g.emitLiteralMatchReturn(m, tmp)
	}
	g.popScope()
	g.indent--
	g.w("})()")
	out := g.buf.String()
	g.buf = savedBuf
	g.indent = savedIndent
	if emitErr != nil {
		return "", emitErr
	}
	return out, nil
}

func (g *jsgen) emitLiteralMatchReturn(m *syntax.MatchEx, tmp string) error {
	for i := range m.Arms {
		arm := &m.Arms[i]
		switch p := arm.Pat.(type) {
		case *syntax.LitPat:
			kw := "if"
			if i > 0 {
				kw = "} else if"
			}
			lit, err := g.expr(p.Val)
			if err != nil {
				return err
			}
			g.w(kw + " (lingEq(" + tmp + ", " + lit + ")) {")
			g.indent++
			g.pushScope()
			if err := g.emitArmReturn(arm); err != nil {
				return err
			}
			g.popScope()
			g.indent--
		case *syntax.BindPat:
			g.w("} else {")
			g.indent++
			g.pushScope()
			g.w("const " + p.Name + " = " + tmp + ";")
			if err := g.emitArmReturn(arm); err != nil {
				return err
			}
			g.popScope()
			g.indent--
		case *syntax.WildPat:
			g.w("} else {")
			g.indent++
			g.pushScope()
			if err := g.emitArmReturn(arm); err != nil {
				return err
			}
			g.popScope()
			g.indent--
		default:
			return g.errf(arm.Line, "该模式不支持（→JS）")
		}
	}
	g.w("}")
	return nil
}

func (g *jsgen) emitArmReturn(arm *syntax.Arm) error {
	if m, ok := arm.Body.(*syntax.MatchEx); ok {
		expr, err := g.expr(m)
		if err != nil {
			return err
		}
		g.w("return await " + expr + ";")
		return nil
	}
	expr, err := g.expr(arm.Body)
	if err != nil {
		return err
	}
	g.w("return lingCopy(await " + expr + ");")
	return nil
}

func (g *jsgen) isBuiltinFn(name string) bool {
	switch name {
	case "println", "print", "len", "str", "int", "float", "keys", "values", "has", "push", "assert", "err", "bytes", "char", "jsonParse", "jsonStringify", "isNone":
		return true
	}
	return false
}

func (g *jsgen) emitCall(x *syntax.CallEx) (string, error) {
	id, isIdent := x.Callee.(*syntax.Ident)
	if !isIdent {
		return g.emitNonIdentCall(x)
	}

	if _, isUser := g.caseEnum[id.Name]; isUser {
		var args []string
		for _, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			args = append(args, "lingCopy("+arg+")")
		}
		return "le_" + g.caseEnum[id.Name] + "_" + id.Name + "(" + strings.Join(args, ", ") + ")", nil
	}

	switch id.Name {
	case "Some":
		if len(x.Args) != 1 {
			return "", g.errf(x.Line, "Some 需要 1 个参数")
		}
		arg, err := g.expr(x.Args[0])
		if err != nil {
			return "", err
		}
		return "{ __lingOpt: true, has: true, v: lingCopy(" + arg + ") }", nil
	case "None":
		return "{ __lingOpt: true, has: false, v: undefined }", nil
	case "Ok", "Err":
		return "", g.errf(x.Line, "Ok/Err 只能出现在 Result 函数的 return（→JS 约束）")
	}

	builtins := map[string]string{
		"println": "lingPrintln", "print": "lingPrint", "len": "lingLen",
		"str": "lingStr", "int": "lingInt", "float": "lingFloat",
		"keys": "lingKeys", "has": "lingHas", "push": "lingPush",
		"assert": "lingAssert", "err": "lingErr", "bytes": "lingBytes",
		"char": "lingChar", "jsonParse": "lingJSONParse", "jsonStringify": "lingJSONStringify",
		"isNone": "lingIsNone",
	}
	if bf, ok := builtins[id.Name]; ok {
		if id.Name == "values" {
			return g.emitValues(x)
		}
		var args []string
		for _, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			if id.Name == "push" {
				arg = "lingCopy(" + arg + ")"
			}
			args = append(args, arg)
		}
		return bf + "(" + strings.Join(args, ", ") + ")", nil
	}

	if fi, ok := g.fns[id.Name]; ok {
		var args []string
		for _, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			args = append(args, "lingCopy("+arg+")")
		}
		_ = fi
		return "await " + fi.goName + "(" + strings.Join(args, ", ") + ")", nil
	}

	// 参数/局部持有的函数值（如泛型 fn 的 f: fn(T): U）
	if g.inScope(id.Name) {
		var args []string
		for _, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			args = append(args, arg)
		}
		return "await " + id.Name + "(" + strings.Join(args, ", ") + ")", nil
	}

	// 运行时函数值绑定（如 makeCounter() 的返回）：静态不可预知，直通由 JS 兜底
	var args []string
	for _, a := range x.Args {
		arg, err := g.expr(a)
		if err != nil {
			return "", err
		}
		args = append(args, arg)
	}
	return "await " + id.Name + "(" + strings.Join(args, ", ") + ")", nil
}

// emitValues 映射值列表（保插入序）
func (g *jsgen) emitValues(x *syntax.CallEx) (string, error) {
	if len(x.Args) != 1 {
		return "", g.errf(x.Line, "values 需要 1 个参数")
	}
	arg, err := g.expr(x.Args[0])
	if err != nil {
		return "", err
	}
	return "lingValues(" + arg + ")", nil
}

func (g *jsgen) emitNonIdentCall(x *syntax.CallEx) (string, error) {
	// 方法调用：obj.name(args) → lf_<Struct>_<name>(obj, ...)（方法名全局唯一）
	if fe, ok := x.Callee.(*syntax.FieldEx); ok {
		if goName, isMethod := g.methods[fe.Name]; isMethod {
			obj, err := g.expr(fe.Obj)
			if err != nil {
				return "", err
			}
			var args []string
			for _, a := range x.Args {
				arg, err := g.expr(a)
				if err != nil {
					return "", err
				}
				args = append(args, "lingCopy("+arg+")")
			}
			return "await " + goName + "(" + obj + ", " + strings.Join(args, ", ") + ")", nil
		}
		// 字段持有的函数值调用（如 http.get）
		obj, err := g.expr(fe.Obj)
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
		return "await " + obj + "." + fe.Name + "(" + strings.Join(args, ", ") + ")", nil
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
	return "await " + callee + "(" + strings.Join(args, ", ") + ")", nil
}
