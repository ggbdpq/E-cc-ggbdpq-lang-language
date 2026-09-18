package gen

import (
	"fmt"
	"go/format"
	"strconv"
	"strings"

	"ling/internal/syntax"
)

// Transpile 把 ling 程序转译为自包含、可编译的 Go 源码（package main，prelude 内联）。
//
// →Go 约束（spec §6，违反即返回错误）：
//   - 顶层函数与方法、函数字面量的参数必须有类型注解；
//   - ? 只能出现在 let/return/赋值的顶层；
//   - 带 ? 的函数必须注解为 Result[T, error]，其 return 须为 Ok(...)/Err(...)；
//   - Result 只支持第二参数为 error；
//   - enum 的 match 只支持构造/通配模式，字面量模式的 match 只支持非 enum 值。
//
// 编译期检查：match 穷尽性在此前移（解释器侧仍是运行时检查）。
func Transpile(prog []syntax.Stmt) (string, error) {
	g := &gen{
		enums:    map[string]*syntax.EnumDecl{},
		caseEnum: map[string]string{},
		structs:  map[string]*syntax.StructDecl{},
		fns:      map[string]*fnInfo{},
		globals:  map[string]string{},
	}
	if err := g.collect(prog); err != nil {
		return "", err
	}
	g.buf.WriteString(prelude)
	if err := g.emitProgram(prog); err != nil {
		return "", err
	}
	out, err := format.Source([]byte(g.buf.String()))
	if err != nil {
		return "", fmt.Errorf("生成的 Go 源码无法通过 gofmt（转译器缺陷）: %w\n=== 源码 ===\n%s", err, g.buf.String())
	}
	return string(out), nil
}

type param struct {
	name string
	typ  string // Go 类型
}

type fnInfo struct {
	goName  string
	params  []param
	retGo   string        // "" 表示 void
	retLing syntax.TypeEx // Result 注解时用于 Ok/Err/? 展开
}

type gen struct {
	buf        strings.Builder
	indent     int
	enums      map[string]*syntax.EnumDecl
	caseEnum   map[string]string
	structs    map[string]*syntax.StructDecl
	fns        map[string]*fnInfo
	globals    map[string]string
	scopes     []map[string]string
	tmpN       int
	curRetGo   string
	curRetLing syntax.TypeEx
	curTparams map[string]bool
	curFn      string
}

func (g *gen) errf(line int, format string, args ...any) error {
	return fmt.Errorf("第%d行: %s", line, fmt.Sprintf(format, args...))
}

func (g *gen) newTmp() string {
	g.tmpN++
	return fmt.Sprintf("_t%d", g.tmpN)
}

// ———— 收集与命名 ————

var goKeywords = map[string]bool{
	"break": true, "case": true, "chan": true, "const": true, "continue": true,
	"default": true, "defer": true, "else": true, "fallthrough": true, "for": true,
	"func": true, "go": true, "goto": true, "if": true, "import": true,
	"interface": true, "map": true, "package": true, "range": true, "return": true,
	"select": true, "struct": true, "switch": true, "type": true, "var": true,
}

func nameClashes(name string) bool {
	return goKeywords[name] || strings.HasPrefix(name, "Ling") ||
		strings.HasPrefix(name, "lf_") || strings.HasPrefix(name, "le_")
}

func (g *gen) collect(prog []syntax.Stmt) error {
	for _, s := range prog {
		switch d := s.(type) {
		case *syntax.EnumDecl:
			if nameClashes(d.Name) {
				return g.errf(d.Line, "名字 %s 与 Go 关键字或运行时前缀冲突", d.Name)
			}
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
			if nameClashes(d.Name) {
				return g.errf(d.Line, "名字 %s 与 Go 关键字或运行时前缀冲突", d.Name)
			}
			if _, dup := g.structs[d.Name]; dup {
				return g.errf(d.Line, "struct %s 重复定义", d.Name)
			}
			g.structs[d.Name] = d
		case *syntax.FnDecl:
			if d.Recv != nil {
				continue
			}
			// 函数名会加 lf_ 前缀，Go 关键字不构成冲突；只拒运行时前缀
			if strings.HasPrefix(d.Name, "lf_") {
				return g.errf(d.Line, "函数名 %s 与运行时前缀冲突", d.Name)
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
			// extern 签名：注册为可调用的宿主函数（产物里是 panic 桩，链接前调用即崩）
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
		case *syntax.LetStmt:
			if d.Typ != nil {
				t, err := g.goTypeOf(d.Typ)
				if err != nil {
					return g.errf(d.Line, "%v", err)
				}
				g.globals[d.Name] = t
			} else {
				// 无注解全局按初始化表达式推断（HEXU/MD5K 等数据表依赖此记录）
				g.globals[d.Name] = g.infer(d.Init)
			}
		}
	}
	return nil
}

func (g *gen) fnInfoOf(d *syntax.FnDecl) (*fnInfo, error) {
	info := &fnInfo{goName: "lf_" + d.Name}
	tps := map[string]bool{}
	for _, tp := range d.TypeParams {
		tps[tp] = true
	}
	for _, p := range d.Params {
		if p.Typ == nil {
			return nil, g.errf(d.Line, "→Go 要求参数 %s 有类型注解", p.Name)
		}
		t, err := g.goTypeWith(tps, p.Typ)
		if err != nil {
			return nil, g.errf(d.Line, "参数 %s: %v", p.Name, err)
		}
		info.params = append(info.params, param{name: p.Name, typ: t})
	}
	if d.Ret != nil {
		t, err := g.goTypeWith(tps, d.Ret)
		if err != nil {
			return nil, g.errf(d.Line, "返回类型: %v", err)
		}
		info.retGo = t
		info.retLing = d.Ret
	}
	return info, nil
}

// ———— 类型映射 ————

func (g *gen) goTypeOf(t syntax.TypeEx) (string, error) {
	return g.goTypeWith(map[string]bool{}, t)
}

func (g *gen) goTypeWith(tps map[string]bool, t syntax.TypeEx) (string, error) {
	switch tt := t.(type) {
	case *syntax.NamedType:
		switch tt.Name {
		case "int":
			return "int64", nil
		case "float":
			return "float64", nil
		case "string":
			return "string", nil
		case "bool":
			return "bool", nil
		case "error":
			return "error", nil
		}
		if tt.Name == "any" {
			return "any", nil
		}
		if tt.Name == "Result" {
			if len(tt.Args) != 2 {
				return "", fmt.Errorf("Result 需要两个类型参数")
			}
			if en, ok := tt.Args[1].(*syntax.NamedType); !ok || en.Name != "error" {
				return "", fmt.Errorf("→Go 仅支持 Result[T, error]")
			}
			et, err := g.goTypeWith(tps, tt.Args[0])
			if err != nil {
				return "", err
			}
			return "LingRes[" + et + "]", nil
		}
		if tt.Name == "Option" {
			if len(tt.Args) != 1 {
				return "", fmt.Errorf("Option 需要一个类型参数")
			}
			et, err := g.goTypeWith(tps, tt.Args[0])
			if err != nil {
				return "", err
			}
			return "LingOpt[" + et + "]", nil
		}
		if _, isEnum := g.enums[tt.Name]; isEnum {
			return "LingEnum", nil
		}
		if _, isStruct := g.structs[tt.Name]; isStruct {
			return tt.Name, nil
		}
		if tps[tt.Name] {
			return tt.Name, nil
		}
		return "", fmt.Errorf("未知类型 %s", tt.Name)
	case *syntax.ArrType:
		elem, err := g.goTypeWith(tps, tt.Elem)
		if err != nil {
			return "", err
		}
		return "*LingArr[" + elem + "]", nil
	case *syntax.MapType:
		return "*LingMap", nil
	case *syntax.PtrType:
		elem, err := g.goTypeWith(tps, tt.Elem)
		if err != nil {
			return "", err
		}
		return "*" + elem, nil
	case *syntax.FnType:
		var ps []string
		for _, pt := range tt.Params {
			p, err := g.goTypeWith(tps, pt)
			if err != nil {
				return "", err
			}
			ps = append(ps, p)
		}
		sig := "func(" + strings.Join(ps, ", ") + ")"
		if tt.Ret != nil {
			r, err := g.goTypeWith(tps, tt.Ret)
			if err != nil {
				return "", err
			}
			sig += " " + r
		}
		return sig, nil
	}
	return "", fmt.Errorf("→Go 不支持该类型注解")
}

// infer 尽力推断表达式的 Go 类型；推断不出返回 "any"
func (g *gen) infer(e syntax.Expr) string {
	switch x := e.(type) {
	case *syntax.IntLit:
		return "int64"
	case *syntax.FloatLit:
		return "float64"
	case *syntax.StrLit:
		return "string"
	case *syntax.BoolLit:
		return "bool"
	case *syntax.Ident:
		// 零参构造器裸写（Unit 等）时按 enum 值推断
		if _, isCtor := g.caseEnum[x.Name]; isCtor {
			return "LingEnum"
		}
		return g.lookup(x.Name)
	case *syntax.Unary:
		if x.Op == syntax.Minus {
			return g.infer(x.X)
		}
		return "bool"
	case *syntax.Binary:
		switch x.Op {
		case syntax.And, syntax.Or, syntax.Eq, syntax.Neq, syntax.Lt, syntax.Le, syntax.Gt, syntax.Ge:
			return "bool"
		}
		return g.infer(x.L)
	case *syntax.CallEx:
		if id, ok := x.Callee.(*syntax.Ident); ok {
			switch id.Name {
			case "len":
				return "int64"
			case "str":
				return "string"
			case "int":
				return "int64"
			case "float":
				return "float64"
			case "has", "assert":
				return "bool"
			case "err":
				return "error"
			case "jsonParse":
				return "any"
			case "jsonStringify":
				return "string"
			case "isNone":
				return "bool"
			case "bytes":
				return "*LingArr[any]"
			case "char":
				return "string"
			case "keys", "values":
				return "*LingArr[any]"
			case "push":
				if len(x.Args) > 0 {
					return g.infer(x.Args[0])
				}
			case "Some":
				if len(x.Args) == 1 {
					return "LingOpt[" + g.infer(x.Args[0]) + "]"
				}
			case "None":
				return "LingOpt[any]"
			case "Ok":
				if len(x.Args) == 1 {
					return "LingRes[" + g.infer(x.Args[0]) + "]"
				}
			case "Err":
				return "LingRes[any]"
			default:
				if _, isUser := g.caseEnum[id.Name]; isUser {
					return "LingEnum"
				}
				if fi, ok := g.fns[id.Name]; ok && fi.retGo != "" {
					return fi.retGo
				}
			}
		}
		return "any"
	case *syntax.StructLit:
		return x.TypeName
	case *syntax.ArrLit:
		var first string
		uniform := len(x.Items) > 0
		for i, el := range x.Items {
			t := g.infer(el)
			if i == 0 {
				first = t
			} else if t != first {
				uniform = false
			}
		}
		if uniform && first != "any" {
			return "*LingArr[" + first + "]"
		}
		return "*LingArr[any]"
	case *syntax.MapLit:
		return "*LingMap"
	case *syntax.MatchEx:
		if len(x.Arms) > 0 {
			return g.infer(x.Arms[0].Body)
		}
		return "any"
	case *syntax.FnLit:
		// 推断完整签名 func(params) ret（供函数值调用路径识别）
		var ps []string
		for _, p := range x.Params {
			if p.Typ == nil {
				return "any"
			}
			t, err := g.goTypeWith(g.curTparams, p.Typ)
			if err != nil {
				return "any"
			}
			ps = append(ps, t)
		}
		sig := "func(" + strings.Join(ps, ", ") + ")"
		if x.Ret != nil {
			if t, err := g.goTypeWith(g.curTparams, x.Ret); err == nil {
				sig += " " + t
			}
		}
		return sig
	case *syntax.FieldEx:
		if id, ok := x.Obj.(*syntax.Ident); ok {
			ot := g.lookup(id.Name)
			if sd, isStruct := g.structs[ot]; isStruct {
				for _, f := range sd.Fields {
					if f.Name == x.Name && f.Typ != nil {
						if t, err := g.goTypeOf(f.Typ); err == nil {
							return t
						}
					}
				}
			}
		}
		return "any"
	case *syntax.IndexEx:
		// 运行时 lingIndex 恒返回 any（接口类型），推断须与之对齐
		return "any"
	case *syntax.SliceEx:
		return "any"
	case *syntax.RangeEx:
		return "lingRange"
	}
	return "any"
}

func (g *gen) lookup(name string) string {
	for i := len(g.scopes) - 1; i >= 0; i-- {
		if t, ok := g.scopes[i][name]; ok {
			return t
		}
	}
	if t, ok := g.globals[name]; ok {
		return t
	}
	if fi, ok := g.fns[name]; ok {
		sig := "func("
		var ps []string
		for _, p := range fi.params {
			ps = append(ps, p.typ)
		}
		sig += strings.Join(ps, ", ") + ")"
		if fi.retGo != "" {
			sig += " " + fi.retGo
		}
		return sig
	}
	return "any"
}

// maybeWrap 在 any 与具体类型交界处插 lingTo 断言
func (g *gen) maybeWrap(target, gotType, expr string) string {
	if target != "any" && target != "" && gotType == "any" {
		return "lingTo[" + target + "](" + expr + ")"
	}
	return expr
}

// ———— 程序输出 ————

func (g *gen) emitProgram(prog []syntax.Stmt) error {
	// struct 声明 + 格式化方法（镜像 fmtVal 的 struct 分支）
	for _, d := range prog {
		sd, ok := d.(*syntax.StructDecl)
		if !ok {
			continue
		}
		var fields []string
		for _, f := range sd.Fields {
			t, err := g.goTypeOf(f.Typ)
			if err != nil {
				return g.errf(sd.Line, "字段 %s: %v", f.Name, err)
			}
			fields = append(fields, f.Name+" "+t)
		}
		g.w("type " + sd.Name + " struct {")
		for _, fl := range fields {
			g.w(fl)
		}
		g.w("}")
		g.w("")
		var parts []string
		for _, f := range sd.Fields {
			parts = append(parts, fmt.Sprintf("%q + \": \" + lingFmt(v.%s, false)", f.Name, f.Name))
		}
		g.w("func (v " + sd.Name + ") lingString(top bool) string {")
		g.w("return " + strconv.Quote(sd.Name) + " + \"{\" + strings.Join([]string{" + strings.Join(parts, ", ") + "}, \", \") + \"}\"")
		g.w("}")
		// LingField：装箱值（any）取字段时的接口分派目标
		g.w("func (v " + sd.Name + ") LingField(name string) any {")
		g.w("switch name {")
		for _, f := range sd.Fields {
			g.w(`case "` + f.Name + `":`)
			g.w("return v." + f.Name)
		}
		g.w("}")
		g.w(`panic("` + sd.Name + ` 没有字段 " + name)`)
		g.w("}")
		g.w("")
	}
	// 用户 enum 构造器
	for _, en := range g.enums {
		for _, c := range en.Cases {
			var ps []string
			var vals []string
			for i, pt := range c.Payloads {
				t, err := g.goTypeOf(pt)
				if err != nil {
					return g.errf(c.Line, "case %s 载荷: %v", c.Name, err)
				}
				pn := "p" + strconv.Itoa(i)
				ps = append(ps, pn+" "+t)
				vals = append(vals, pn)
			}
			valsExpr := "nil"
			if len(vals) > 0 {
				valsExpr = "[]any{" + strings.Join(vals, ", ") + "}"
			}
			g.w("func le_" + en.Name + "_" + c.Name + "(" + strings.Join(ps, ", ") + ") LingEnum { return LingEnum{" + strconv.Quote(c.Name) + ", " + valsExpr + "} }")
			g.w("")
		}
	}
	// 全局绑定
	for _, s := range prog {
		if ls, ok := s.(*syntax.LetStmt); ok {
			g.scopes = []map[string]string{{}}
			expr, err := g.expr(ls.Init)
			if err != nil {
				return err
			}
			g.w("var " + ls.Name + " = " + expr)
			g.w("")
		}
	}
	// 方法与函数
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
			if err := g.emitExternStubs(d); err != nil {
				return err
			}
		case *syntax.ImportStmt:
			// import 由装载器展开，转译期忽略
		}
	}
	// main
	if _, has := g.fns["main"]; has {
		g.w("func main() {")
		g.w("\tlf_main()")
		g.w("}")
	} else {
		g.w("func main() {}")
	}
	return nil
}

// emitExternStubs extern 声明生成 panic 桩：保持产物可编译，链接前调用即崩
func (g *gen) emitExternStubs(d *syntax.ExternDecl) error {
	for _, fn := range d.Fns {
		fi, ok := g.fns[fn.Name]
		if !ok {
			return g.errf(d.Line, "内部错误：extern fn %s 未收集", fn.Name)
		}
		var ps []string
		for _, p := range fi.params {
			ps = append(ps, p.name+" "+p.typ)
		}
		ret := ""
		if fi.retGo != "" {
			ret = " " + fi.retGo
		}
		g.w("func " + fi.goName + "(" + strings.Join(ps, ", ") + ")" + ret + " {")
		g.w(`panic("extern 未链接: ` + fn.Name + `（宿主 ` + d.Host + `）")`)
		g.w("}")
		g.w("")
	}
	return nil
}

func (g *gen) emitFunc(d *syntax.FnDecl) error {
	fi := g.fns[d.Name]
	tps := map[string]bool{}
	for _, tp := range d.TypeParams {
		tps[tp] = true
	}
	var generic string
	if len(d.TypeParams) > 0 {
		var gs []string
		for _, tp := range d.TypeParams {
			gs = append(gs, tp+" any")
		}
		generic = "[" + strings.Join(gs, ", ") + "]"
	}
	var ps []string
	scope := map[string]string{}
	for _, p := range fi.params {
		ps = append(ps, p.name+" "+p.typ)
		scope[p.name] = p.typ
	}
	retDecl := ""
	if fi.retGo != "" {
		retDecl = " " + fi.retGo
	}
	g.w("func " + fi.goName + generic + "(" + strings.Join(ps, ", ") + ")" + retDecl + " {")
	savedFn := g.curFn
	g.curFn = d.Name
	saved := g.pushCtx(retDecl, d.Ret, tps, scope)
	err := g.emitBody(d.Body)
	g.popCtx(saved)
	g.curFn = savedFn
	if err != nil {
		return err
	}
	g.w("}")
	g.w("")
	return nil
}

func (g *gen) emitMethod(d *syntax.FnDecl) error {
	rt, err := g.goTypeOf(d.Recv.Typ)
	if err != nil {
		return g.errf(d.Line, "接收者: %v", err)
	}
	var ps []string
	scope := map[string]string{d.Recv.Name: rt}
	for _, p := range d.Params {
		if p.Typ == nil {
			return g.errf(d.Line, "→Go 要求参数 %s 有类型注解", p.Name)
		}
		t, err := g.goTypeWith(map[string]bool{}, p.Typ)
		if err != nil {
			return g.errf(d.Line, "参数 %s: %v", p.Name, err)
		}
		ps = append(ps, p.Name+" "+t)
		scope[p.Name] = t
	}
	retDecl := ""
	if d.Ret != nil {
		t, err := g.goTypeOf(d.Ret)
		if err != nil {
			return g.errf(d.Line, "返回类型: %v", err)
		}
		retDecl = " " + t
	}
	g.w("func (" + d.Recv.Name + " " + rt + ") " + d.Name + "(" + strings.Join(ps, ", ") + ")" + retDecl + " {")
	saved := g.pushCtx(retDecl, d.Ret, map[string]bool{}, scope)
	err = g.emitBody(d.Body)
	g.popCtx(saved)
	if err != nil {
		return err
	}
	g.w("}")
	g.w("")
	return nil
}

type ctxSave struct {
	scopes  []map[string]string
	indent  int
	retGo   string
	retLing syntax.TypeEx
	tps     map[string]bool
}

func (g *gen) pushCtx(retGo string, retLing syntax.TypeEx, tps map[string]bool, scope map[string]string) ctxSave {
	s := ctxSave{g.scopes, g.indent, g.curRetGo, g.curRetLing, g.curTparams}
	g.scopes = []map[string]string{scope}
	g.curRetGo = retGo
	g.curRetLing = retLing
	g.curTparams = tps
	return s
}

func (g *gen) popCtx(s ctxSave) {
	g.scopes = s.scopes
	g.indent = s.indent
	g.curRetGo = s.retGo
	g.curRetLing = s.retLing
	g.curTparams = s.tps
}

func (g *gen) emitBody(stmts []syntax.Stmt) error {
	for _, st := range stmts {
		if err := g.emitStmt(st); err != nil {
			return err
		}
	}
	return nil
}

func (g *gen) w(line string) {
	g.buf.WriteString(strings.Repeat("\t", g.indent) + line + "\n")
}

// ———— 语句 ————

func (g *gen) emitStmt(s syntax.Stmt) error {
	switch st := s.(type) {
	case *syntax.LetStmt:
		return g.emitLet(st.Name, st.Typ, st.Init, st.Line)

	case *syntax.AssignStmt:
		return g.emitAssign(st)

	case *syntax.ExprStmt:
		// match 在语句位置直接展开 if/switch
		if m, ok := st.X.(*syntax.MatchEx); ok {
			return g.emitMatchStmt(m)
		}
		expr, err := g.expr(st.X)
		if err != nil {
			return err
		}
		if isVoidCall(st.X, g) {
			g.w(expr)
		} else {
			g.w("_ = " + expr)
		}
		return nil

	case *syntax.ReturnStmt:
		return g.emitReturn(st)

	case *syntax.IfStmt:
		cond, err := g.condExpr(st.Cond)
		if err != nil {
			return err
		}
		g.w("if " + cond + " {")
		if err := g.emitBlock(st.Then); err != nil {
			return err
		}
		if st.Else != nil {
			g.w("} else {")
			if err := g.emitBlock(st.Else); err != nil {
				return err
			}
		}
		g.w("}")
		return nil

	case *syntax.WhileStmt:
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

	case *syntax.ForInStmt:
		return g.emitForIn(st)

	case *syntax.BreakStmt:
		g.w("break")
		return nil
	case *syntax.ContinueStmt:
		g.w("continue")
		return nil

	case *syntax.FnDecl:
		return g.emitNestedFn(st)
	}
	return g.errf(s.Pos(), "→Go 暂不支持该语句")
}

// emitLet let/var 通用：? 在初始化顶层时先展开传播
func (g *gen) emitLet(name string, typ syntax.TypeEx, init syntax.Expr, line int) error {
	if t, ok := init.(*syntax.TryEx); ok {
		tmp, err := g.genTry(t)
		if err != nil {
			return err
		}
		if name != "_" {
			bindType := "any"
			if vt, err := g.resultT(); err == nil {
				bindType = vt
			}
			g.w(name + " := " + tmp + ".v")
			g.scopes[len(g.scopes)-1][name] = bindType
		}
		return nil
	}
	expr, err := g.expr(init)
	if err != nil {
		return err
	}
	if name == "_" {
		g.w("_ = " + expr)
		return nil
	}
	bindType := ""
	if typ != nil {
		t, err := g.goTypeWith(g.curTparams, typ)
		if err != nil {
			return g.errf(line, "%v", err)
		}
		// 空数组字面量继承注解类型：var out: [U] = []
		if al, ok := init.(*syntax.ArrLit); ok && len(al.Items) == 0 && strings.HasPrefix(t, "*LingArr[") {
			expr = "&" + t[1:] + "{}"
		} else {
			expr = g.maybeWrap(t, g.infer(init), expr)
		}
		bindType = t
	} else {
		bindType = g.infer(init)
	}
	g.w(name + " := " + expr)
	g.scopes[len(g.scopes)-1][name] = bindType
	// 函数值绑定：注册参数/返回类型，调用点据此收窄实参
	if fl, ok := init.(*syntax.FnLit); ok {
		fi := &fnInfo{goName: name}
		for _, p := range fl.Params {
			if p.Typ == nil {
				continue
			}
			if t, err := g.goTypeWith(g.curTparams, p.Typ); err == nil {
				fi.params = append(fi.params, param{name: p.Name, typ: t})
			}
		}
		if fl.Ret != nil {
			if t, err := g.goTypeWith(g.curTparams, fl.Ret); err == nil {
				fi.retGo = t
			}
		}
		g.fns[name] = fi
	}
	return nil
}

func (g *gen) emitAssign(st *syntax.AssignStmt) error {
	switch tgt := st.Target.(type) {
	case *syntax.Ident:
		if t, ok := st.Val.(*syntax.TryEx); ok {
			tmp, err := g.genTry(t)
			if err != nil {
				return err
			}
			g.w(tgt.Name + " = " + tmp + ".v")
			return nil
		}
		expr, err := g.expr(st.Val)
		if err != nil {
			return err
		}
		expr = g.maybeWrap(g.lookup(tgt.Name), g.infer(st.Val), expr)
		g.w(tgt.Name + " = " + expr)
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
		g.w(obj + "." + tgt.Name + " = " + expr)
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
		g.w("lingSetIndex(" + obj + ", " + idx + ", " + expr + ")")
		return nil
	}
	return g.errf(st.Line, "赋值目标不合法")
}

// isVoidCall 判定"无返回值的调用"（语句位置不加 _ =）
func isVoidCall(e syntax.Expr, g *gen) bool {
	call, ok := e.(*syntax.CallEx)
	if !ok {
		return false
	}
	id, ok := call.Callee.(*syntax.Ident)
	if !ok {
		return false
	}
	switch id.Name {
	case "println", "print", "push", "assert":
		// →Go 里这些调用无返回值，语句位置不能加 _ =
		return true
	}
	if fi, ok := g.fns[id.Name]; ok && fi.retGo == "" {
		return true
	}
	return false
}

func (g *gen) isResultFn() bool {
	rt, ok := g.curRetLing.(*syntax.NamedType)
	return ok && rt.Name == "Result"
}

// coerceAlgebraic 装箱的 Result/Option（如 http.get 返回 any）在解包前收窄
func (g *gen) coerceAlgebraic(scrut string, m *syntax.MatchEx, kind string) string {
	if g.infer(m.Scrut) != "any" {
		return scrut
	}
	switch kind {
	case "result":
		return "lingTo[LingRes[any]](" + scrut + ")"
	case "option":
		return "lingTo[LingOpt[any]](" + scrut + ")"
	}
	return scrut
}

func (g *gen) resultT() (string, error) {
	rt, ok := g.curRetLing.(*syntax.NamedType)
	if !ok || rt.Name != "Result" || len(rt.Args) != 2 {
		return "", fmt.Errorf("带 ? 的函数须注解为 Result[T, error]")
	}
	return g.goTypeWith(g.curTparams, rt.Args[0])
}

func (g *gen) emitReturn(st *syntax.ReturnStmt) error {
	if st.Val == nil {
		g.w("return")
		return nil
	}
	if t, ok := st.Val.(*syntax.TryEx); ok {
		tmp, err := g.genTry(t)
		if err != nil {
			return err
		}
		g.w("return " + tmp + ".v")
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
		t, err := g.resultT()
		if err != nil {
			return g.errf(st.Line, "%v", err)
		}
		expr, err := g.expr(call.Args[0])
		if err != nil {
			return err
		}
		if id.Name == "Ok" {
			g.w("return LingOk[" + t + "](" + expr + ")")
		} else {
			g.w("return LingResErr[" + t + "](" + expr + ")")
		}
		return nil
	}
	expr, err := g.expr(st.Val)
	if err != nil {
		return err
	}
	if g.curRetGo == "" {
		return g.errf(st.Line, "void 函数不应返回值")
	}
	// 返回值按签名收窄（如 jsonStr 的 any → string）
	if rt := strings.TrimSpace(g.curRetGo); rt != "" && rt != "any" && g.infer(st.Val) == "any" {
		expr = "lingTo[" + rt + "](" + expr + ")"
	}
	// match 作返回值：包成立即调用函数字面量
	if _, ok := st.Val.(*syntax.MatchEx); ok {
		g.w("return " + expr)
		return nil
	}
	g.w("return " + expr)
	return nil
}

// genTry 展开 ? 传播；返回持有 Result 的临时变量名
func (g *gen) genTry(t *syntax.TryEx) (string, error) {
	if !g.isResultFn() {
		return "", g.errf(t.Line, "带 ? 的函数须注解为 Result[T, error]")
	}
	tmp := g.newTmp()
	expr, err := g.expr(t.X)
	if err != nil {
		return "", err
	}
	if g.infer(t.X) == "any" {
		// 装箱 Result（如 http.get）先收窄再解包
		expr = "lingTo[LingRes[any]](" + expr + ")"
	}
	tt, err := g.resultT()
	if err != nil {
		return "", g.errf(t.Line, "%v", err)
	}
	g.w(tmp + " := " + expr)
	g.w("if !" + tmp + ".ok {")
	g.indent++
	g.w("return LingResErr[" + tt + "](" + tmp + ".e)")
	g.indent--
	g.w("}")
	return tmp, nil
}

func (g *gen) condExpr(e syntax.Expr) (string, error) {
	expr, err := g.expr(e)
	if err != nil {
		return "", err
	}
	if g.infer(e) != "bool" {
		expr = "lingTruthy(" + expr + ")"
	}
	return expr, nil
}

func (g *gen) emitBlock(stmts []syntax.Stmt) error {
	g.indent++
	g.pushScope()
	err := g.emitBody(stmts)
	g.popScope()
	g.indent--
	return err
}

func (g *gen) pushScope() { g.scopes = append(g.scopes, map[string]string{}) }
func (g *gen) popScope()  { g.scopes = g.scopes[:len(g.scopes)-1] }

func (g *gen) emitForIn(st *syntax.ForInStmt) error {
	if rg, ok := st.Iter.(*syntax.RangeEx); ok {
		lo, err := g.expr(rg.Lo)
		if err != nil {
			return err
		}
		hi, err := g.expr(rg.Hi)
		if err != nil {
			return err
		}
		g.w("for " + st.Var + " := " + lo + "; " + st.Var + " < " + hi + "; " + st.Var + "++ {")
		if err := g.emitBlock(st.Body); err != nil {
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
	t := g.infer(st.Iter)
	name := st.Var
	switch {
	case strings.HasPrefix(t, "*LingArr["):
		elem := t[len("*LingArr[") : len(t)-1]
		g.w(tmp + " := " + it)
		g.w("for _, " + name + " := range " + tmp + ".Items {")
		if err := g.emitBlockTyped(st.Body, name, elem); err != nil {
			return err
		}
		g.w("}")
	case t == "[]any":
		g.w("for _, " + name + " := range " + it + " {")
		if err := g.emitBlockTyped(st.Body, name, "any"); err != nil {
			return err
		}
		g.w("}")
	case t == "string":
		// ling 语义：按码点迭代，绑定单字符字符串
		g.w("for _, _r" + tmp + " := range []rune(" + it + ") {")
		g.indent++
		g.pushScope()
		g.w(name + " := string(_r" + tmp + ")")
		g.scopes[len(g.scopes)-1][name] = "string"
		if err := g.emitBody(st.Body); err != nil {
			return err
		}
		g.popScope()
		g.indent--
		g.w("}")
	default:
		// 动态值（JSON 结果等）：运行时分派迭代（lingIter 做快照）
		if t != "any" {
			return g.errf(st.Line, "类型 %s 不可迭代（→Go）", t)
		}
		g.w(tmp + " := lingIter(" + it + ")")
		g.w("for _, " + name + " := range " + tmp + ".Items {")
		if err := g.emitBlockTyped(st.Body, name, "any"); err != nil {
			return err
		}
		g.w("}")
	}
	return nil
}

// emitBlockTyped 开块并预置一个绑定
func (g *gen) emitBlockTyped(stmts []syntax.Stmt, name, typ string) error {
	g.indent++
	g.pushScope()
	g.scopes[len(g.scopes)-1][name] = typ
	err := g.emitBody(stmts)
	g.popScope()
	g.indent--
	return err
}

func (g *gen) emitNestedFn(d *syntax.FnDecl) error {
	info := &fnInfo{goName: "lf_" + d.Name}
	// 注册进 fns 表使同名调用解析到 lf_ 前缀产物（同名遮蔽场景暂不支持）
	g.fns[d.Name] = info
	for _, p := range d.Params {
		if p.Typ == nil {
			return g.errf(d.Line, "→Go 要求参数 %s 有类型注解", p.Name)
		}
		t, err := g.goTypeWith(g.curTparams, p.Typ)
		if err != nil {
			return g.errf(d.Line, "参数 %s: %v", p.Name, err)
		}
		info.params = append(info.params, param{name: p.Name, typ: t})
	}
	if d.Ret != nil {
		t, err := g.goTypeWith(g.curTparams, d.Ret)
		if err != nil {
			return g.errf(d.Line, "返回类型: %v", err)
		}
		info.retGo = t
		info.retLing = d.Ret
	}
	tpsAll := map[string]bool{}
	for tp := range g.curTparams {
		tpsAll[tp] = true
	}
	for _, tp := range d.TypeParams {
		tpsAll[tp] = true
	}
	var generic string
	if len(d.TypeParams) > 0 {
		var gs []string
		for _, tp := range d.TypeParams {
			gs = append(gs, tp+" any")
		}
		generic = "[" + strings.Join(gs, ", ") + "]"
	}
	var ps []string
	scope := map[string]string{}
	for _, p := range info.params {
		ps = append(ps, p.name+" "+p.typ)
		scope[p.name] = p.typ
	}
	retDecl := ""
	if info.retGo != "" {
		retDecl = " " + info.retGo
	}
	g.w("func " + info.goName + generic + "(" + strings.Join(ps, ", ") + ")" + retDecl + " {")
	saved := g.pushCtx(retDecl, d.Ret, tpsAll, scope)
	err := g.emitBody(d.Body)
	g.popCtx(saved)
	if err != nil {
		return err
	}
	g.w("}")
	return nil
}

// ———— match ————

func (g *gen) classifyMatch(m *syntax.MatchEx) (string, error) {
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
		// Ok/Err 与 Some/None 混用 → 归为冲突
		if (flags["Ok"] || flags["Err"]) && (flags["Some"] || flags["None"]) {
			return "", g.errf(m.Line, "Result 与 Option 的模式混用")
		}
		if flags["Ok"] || flags["Err"] {
			return "result", nil
		}
		return "option", nil
	case flags["__lit"]:
		if hasCasePat(m) {
			return "", g.errf(m.Line, "enum 的 match 不能混入字面量模式（→Go）")
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

// checkExhaustive 编译期穷尽检查：enum match 须覆盖全部 case 或有通配/绑定臂
func (g *gen) checkExhaustive(m *syntax.MatchEx) error {
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

func (g *gen) emitMatchStmt(m *syntax.MatchEx) error {
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
	scrut = g.coerceAlgebraic(scrut, m, kind)
	g.w(tmp + " := " + scrut)
	switch kind {
	case "result":
		okArm, errArm := splitAlgebraic(m, "Ok", "Err")
		g.w("if " + tmp + ".ok {")
		if err := g.emitResultArmBlock(okArm, tmp); err != nil {
			return err
		}
		g.w("} else {")
		if err := g.emitResultErrBlock(errArm, tmp); err != nil {
			return err
		}
		g.w("}")
	case "option":
		someArm, noneArm := splitAlgebraic(m, "Some", "None")
		g.w("if " + tmp + ".has {")
		if err := g.emitResultArmBlock(someArm, tmp); err != nil {
			return err
		}
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
		g.w("switch " + tmp + ".Tag {")
		g.indent++
		for i := range m.Arms {
			arm := &m.Arms[i]
			if p, ok := arm.Pat.(*syntax.CasePat); ok {
				g.w(`case "` + p.Name + `":`)
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
			} else {
				g.w("default:")
				g.indent++
				g.pushScope()
				if bp, ok := arm.Pat.(*syntax.BindPat); ok {
					g.w(bp.Name + " := " + tmp)
					g.scopes[len(g.scopes)-1][bp.Name] = "LingEnum"
				}
				if err := g.emitArmStmt(arm); err != nil {
					return err
				}
				g.popScope()
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

func (g *gen) emitResultArmBlock(arm *syntax.Arm, tmp string) error {
	g.indent++
	g.pushScope()
	if arm != nil {
		if name := armBindName(arm); name != "_" {
			g.w(name + " := " + tmp + ".v")
			g.w("_ = " + name) // 臂体未用时消 Go unused
			g.scopes[len(g.scopes)-1][name] = "any"
		}
		if err := g.emitArmStmt(arm); err != nil {
			return err
		}
	}
	g.popScope()
	g.indent--
	return nil
}

func (g *gen) emitResultErrBlock(arm *syntax.Arm, tmp string) error {
	g.indent++
	g.pushScope()
	if arm != nil {
		if name := armBindName(arm); name != "_" {
			g.w(name + " := " + tmp + ".e")
			g.w("_ = " + name) // 臂体未用时消 Go unused
			g.scopes[len(g.scopes)-1][name] = "error"
		}
		if err := g.emitArmStmt(arm); err != nil {
			return err
		}
	}
	g.popScope()
	g.indent--
	return nil
}

func armBindName(arm *syntax.Arm) string {
	if p, ok := arm.Pat.(*syntax.CasePat); ok && len(p.Subs) == 1 {
		if bp, ok := p.Subs[0].(*syntax.BindPat); ok {
			return bp.Name
		}
	}
	return "_"
}

func (g *gen) bindEnumPayloads(p *syntax.CasePat, tmp string) error {
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
				pt := "any"
				if j < len(cd.Payloads) && cd.Payloads[j] != nil {
					if got, err := g.goTypeWith(g.curTparams, cd.Payloads[j]); err == nil {
						pt = got
					}
				}
				g.w(bp.Name + " := lingTo[" + pt + "](" + tmp + ".Vals[" + strconv.Itoa(j) + "])")
				g.scopes[len(g.scopes)-1][bp.Name] = pt
			}
		}
	}
	return nil
}

// emitArmStmt 臂体在语句位置（ExprStmt 展开）的输出
func (g *gen) emitArmStmt(arm *syntax.Arm) error {
	if m, ok := arm.Body.(*syntax.MatchEx); ok {
		return g.emitMatchStmt(m)
	}
	expr, err := g.expr(arm.Body)
	if err != nil {
		return err
	}
	if isVoidCall(arm.Body, g) {
		g.w(expr)
	} else {
		g.w("_ = " + expr)
	}
	return nil
}

// emitLiteralMatchStmt 字面量/通配 match（语句位置）→ if/else 链
func (g *gen) emitLiteralMatchStmt(m *syntax.MatchEx, tmp string) error {
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
			g.w(kw + " lingEq(" + tmp + ", " + lit + ") {")
			if err := g.emitBlock([]syntax.Stmt{&syntax.ExprStmt{X: arm.Body, Line: arm.Line}}); err != nil {
				return err
			}
		case *syntax.BindPat:
			g.w("} else {")
			g.indent++
			g.pushScope()
			g.w(p.Name + " := " + tmp)
			g.scopes[len(g.scopes)-1][p.Name] = g.infer(m.Scrut)
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
			return g.errf(arm.Line, "该模式不支持（→Go）")
		}
	}
	g.w("}")
	return nil
}

// ———— 表达式 ————

// operand 生成二元/一元运算的操作数：子式本身是运算时强制加括号，
// 消除 ling 与 Go 优先级表的残留差异（如 % 在 Go 里高于 +）
func (g *gen) operand(e syntax.Expr) (string, error) {
	s, err := g.expr(e)
	if err != nil {
		return "", err
	}
	switch e.(type) {
	case *syntax.Binary, *syntax.Unary:
		return "(" + s + ")", nil
	}
	return s, nil
}

func (g *gen) expr(e syntax.Expr) (string, error) {
	switch x := e.(type) {
	case *syntax.IntLit:
		return "int64(" + strconv.FormatInt(x.Val, 10) + ")", nil
	case *syntax.FloatLit:
		return "float64(" + strconv.FormatFloat(x.Val, 'g', -1, 64) + ")", nil
	case *syntax.StrLit:
		return strconv.Quote(x.Val), nil
	case *syntax.BoolLit:
		return strconv.FormatBool(x.Val), nil

	case *syntax.Ident:
		if g.isBuiltinFn(x.Name) {
			return "", g.errf(x.Line, "内建 %s 须直接调用", x.Name)
		}
		// 零参构造器裸写：生成构造器调用
		if en, isCtor := g.caseEnum[x.Name]; isCtor {
			return "le_" + en + "_" + x.Name + "()", nil
		}
		if x.Name == "None" {
			return "LingNone[any]()", nil
		}
		if x.Name == "http" {
			// 预载 HTTP 服务值（prelude 的 lingHTTP，避让 net/http 包名）
			return "lingHTTP", nil
		}
		if fi, ok := g.fns[x.Name]; ok {
			return fi.goName, nil
		}
		return x.Name, nil

	case *syntax.Unary:
		inner, err := g.operand(x.X)
		if err != nil {
			return "", err
		}
		switch x.Op {
		case syntax.Minus:
			return "-" + inner, nil
		case syntax.Not:
			return "!" + inner, nil
		case syntax.Amp:
			return "&" + inner, nil
		}
		return "", g.errf(x.Line, "未知一元运算")

	case *syntax.Binary:
		l, err := g.operand(x.L)
		if err != nil {
			return "", err
		}
		r, err := g.operand(x.R)
		if err != nil {
			return "", err
		}
		switch x.Op {
		case syntax.And:
			return l + " && " + r, nil
		case syntax.Or:
			return l + " || " + r, nil
		case syntax.Eq:
			return "lingEq(" + l + ", " + r + ")", nil
		case syntax.Neq:
			return "!lingEq(" + l + ", " + r + ")", nil
		case syntax.Lt, syntax.Le, syntax.Gt, syntax.Ge:
			// 排序比较统一走 lingCompare（任一侧 any 也能运行时分派）
			cmp := "lingCompare(" + l + ", " + r + ")"
			switch x.Op {
			case syntax.Lt:
				return cmp + " < 0", nil
			case syntax.Le:
				return cmp + " <= 0", nil
			case syntax.Gt:
				return cmp + " > 0", nil
			default:
				return cmp + " >= 0", nil
			}
		case syntax.Plus:
			// 任一侧为 any（如字符串索引/切片）时按 ling 语义收窄：
			// 已知字符串走拼接，否则按 int 收窄（非该类型运行时 panic，镜像解释器）
			li, ri := g.infer(x.L), g.infer(x.R)
			if li == "any" || ri == "any" {
				if li == "string" || ri == "string" {
					return "lingStrVal(" + l + ") + lingStrVal(" + r + ")", nil
				}
				return "lingInt(" + l + ") + lingInt(" + r + ")", nil
			}
		case syntax.Minus, syntax.Star, syntax.Slash, syntax.Percent,
			syntax.Amp, syntax.BitOr, syntax.Xor, syntax.Shl, syntax.Shr:
			// 数值/位运算：any 侧按 int 收窄
			li, ri := g.infer(x.L), g.infer(x.R)
			if li == "any" {
				l = "lingInt(" + l + ")"
			}
			if ri == "any" {
				r = "lingInt(" + r + ")"
			}
		}
		op := ""
		switch x.Op {
		case syntax.Lt:
			op = "<"
		case syntax.Le:
			op = "<="
		case syntax.Gt:
			op = ">"
		case syntax.Ge:
			op = ">="
		case syntax.Plus:
			op = "+"
		case syntax.Minus:
			op = "-"
		case syntax.Star:
			op = "*"
		case syntax.Slash:
			op = "/"
		case syntax.Percent:
			op = "%"
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
		}
		return l + " " + op + " " + r, nil

	case *syntax.RangeEx:
		lo, err := g.expr(x.Lo)
		if err != nil {
			return "", err
		}
		hi, err := g.expr(x.Hi)
		if err != nil {
			return "", err
		}
		return "lingRange{Lo: " + lo + ", Hi: " + hi + "}", nil

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
		if g.infer(x.Obj) == "error" {
			if x.Name == "msg" {
				return "lingErrMsg(" + obj + ")", nil
			}
			return "", g.errf(x.Line, "error 只有 msg 字段")
		}
		// 装箱值（如 http 响应）经接口分派取字段
		if g.infer(x.Obj) == "any" {
			return "lingField(" + obj + ", " + strconv.Quote(x.Name) + ")", nil
		}
		return obj + "." + x.Name, nil

	case *syntax.TryEx:
		return "", g.errf(x.Line, "? 只能出现在 let/return/赋值的顶层（→Go 约束）")

	case *syntax.StructLit:
		sd := g.structs[x.TypeName]
		var fs []string
		for i, f := range x.Fields {
			v, err := g.expr(f.Val)
			if err != nil {
				return "", err
			}
			if sd != nil && i < len(sd.Fields) && sd.Fields[i].Typ != nil {
				if t, err := g.goTypeOf(sd.Fields[i].Typ); err == nil {
					v = g.maybeWrap(t, g.infer(f.Val), v)
				}
			}
			fs = append(fs, f.Name+": "+v)
		}
		return x.TypeName + "{" + strings.Join(fs, ", ") + "}", nil

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
			kvs = append(kvs, "lingKV{"+k+", "+v+"}")
		}
		return "lingNewMap(" + strings.Join(kvs, ", ") + ")", nil

	case *syntax.ArrLit:
		t := g.infer(x)
		elem := "any"
		if strings.HasPrefix(t, "*LingArr[") {
			elem = t[len("*LingArr[") : len(t)-1]
		}
		var items []string
		for _, el := range x.Items {
			v, err := g.expr(el)
			if err != nil {
				return "", err
			}
			items = append(items, g.maybeWrap(elem, g.infer(el), v))
		}
		if len(items) == 0 {
			return "&LingArr[" + elem + "]{}", nil
		}
		return "&LingArr[" + elem + "]{Items: []" + elem + "{" + strings.Join(items, ", ") + "}}", nil

	case *syntax.MatchEx:
		return g.exprMatch(x)

	case *syntax.FnLit:
		return g.emitFnLit(x)
	}
	return "", g.errf(e.Pos(), "→Go 暂不支持该表达式")
}

// exprMatch 表达式位置的 match：包成立即调用的函数字面量（独立缓冲渲染）
func (g *gen) exprMatch(m *syntax.MatchEx) (string, error) {
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
	g.indent = 0

	retType := "any"
	if len(m.Arms) > 0 {
		retType = g.infer(m.Arms[0].Body)
	}
	tmp := g.newTmp()
	scrut, err := g.expr(m.Scrut)
	if err != nil {
		g.buf = savedBuf
		g.indent = savedIndent
		return "", err
	}
	scrut = g.coerceAlgebraic(scrut, m, kind)
	g.w("func() " + retType + " {")
	g.indent++
	g.pushScope()
	g.w(tmp + " := " + scrut)
	var emitErr error
	switch kind {
	case "result":
		okArm, errArm := splitAlgebraic(m, "Ok", "Err")
		g.w("if " + tmp + ".ok {")
		if err := g.emitResultArmReturn(okArm, tmp, ".v", "any"); err != nil {
			emitErr = err
		}
		if emitErr == nil {
			g.w("} else {")
			if err := g.emitResultArmReturn(errArm, tmp, ".e", "error"); err != nil {
				emitErr = err
			}
		}
		if emitErr == nil {
			g.w("}")
		}
	case "option":
		someArm, noneArm := splitAlgebraic(m, "Some", "None")
		g.w("if " + tmp + ".has {")
		if err := g.emitResultArmReturn(someArm, tmp, ".v", "any"); err != nil {
			emitErr = err
		}
		if emitErr == nil && noneArm != nil {
			g.w("} else {")
			if err := g.emitResultArmReturn(noneArm, tmp, ".e", "any"); err != nil {
				emitErr = err
			}
		}
		if emitErr == nil {
			g.w("}")
		}
	case "enum":
		g.w("switch " + tmp + ".Tag {")
		g.indent++
		hasDefault := false
		for i := range m.Arms {
			arm := &m.Arms[i]
			if p, ok := arm.Pat.(*syntax.CasePat); ok {
				g.w(`case "` + p.Name + `":`)
				g.indent++
				g.pushScope()
				if err := g.bindEnumPayloads(p, tmp); err != nil {
					emitErr = err
				}
				if emitErr == nil {
					if err := g.emitArmReturn(arm); err != nil {
						emitErr = err
					}
				}
				g.popScope()
				g.indent--
			} else {
				hasDefault = true
				g.w("default:")
				g.indent++
				g.pushScope()
				if bp, ok := arm.Pat.(*syntax.BindPat); ok {
					g.w(bp.Name + " := " + tmp)
					g.scopes[len(g.scopes)-1][bp.Name] = "LingEnum"
				}
				if err := g.emitArmReturn(arm); err != nil {
					emitErr = err
				}
				g.popScope()
				g.indent--
			}
		}
		if !hasDefault {
			// 编译期穷尽检查已保证不可达；仅为满足 Go 的 missing return
			g.w("default:")
			g.indent++
			g.w(`panic("match 无匹配分支")`)
			g.indent--
		}
		g.indent--
		g.w("}")
	default:
		if err := g.emitLiteralMatchReturn(m, tmp); err != nil {
			emitErr = err
		}
	}
	g.popScope()
	g.indent--
	g.w("}()")
	out := g.buf.String()
	g.buf = savedBuf
	g.indent = savedIndent
	if emitErr != nil {
		return "", emitErr
	}
	return strings.TrimRight(out, "\n"), nil
}

func (g *gen) emitResultArmReturn(arm *syntax.Arm, tmp, field, bindType string) error {
	if arm == nil {
		return nil
	}
	g.indent++
	g.pushScope()
	if name := armBindName(arm); name != "_" {
		g.w(name + " := " + tmp + field)
		g.w("_ = " + name) // 臂体未用时消 Go unused
		g.scopes[len(g.scopes)-1][name] = bindType
	}
	if err := g.emitArmReturn(arm); err != nil {
		return err
	}
	g.popScope()
	g.indent--
	return nil
}

func (g *gen) emitLiteralMatchReturn(m *syntax.MatchEx, tmp string) error {
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
			g.w(kw + " lingEq(" + tmp + ", " + lit + ") {")
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
			g.w(p.Name + " := " + tmp)
			g.scopes[len(g.scopes)-1][p.Name] = g.infer(m.Scrut)
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
			return g.errf(arm.Line, "该模式不支持（→Go）")
		}
	}
	g.w("}")
	return nil
}

func (g *gen) emitArmReturn(arm *syntax.Arm) error {
	if m, ok := arm.Body.(*syntax.MatchEx); ok {
		expr, err := g.expr(m)
		if err != nil {
			return err
		}
		g.w("return " + expr)
		return nil
	}
	expr, err := g.expr(arm.Body)
	if err != nil {
		return err
	}
	g.w("return " + expr)
	return nil
}

// emitFnLit 函数字面量：独立缓冲渲染后作为表达式内嵌
func (g *gen) emitFnLit(x *syntax.FnLit) (string, error) {
	var ps []string
	scope := map[string]string{}
	tps := map[string]bool{}
	for _, p := range x.Params {
		if p.Typ == nil {
			return "", g.errf(x.Line, "→Go 要求函数字面量参数 %s 有类型注解", p.Name)
		}
		t, err := g.goTypeWith(tps, p.Typ)
		if err != nil {
			return "", g.errf(x.Line, "参数 %s: %v", p.Name, err)
		}
		ps = append(ps, p.Name+" "+t)
		scope[p.Name] = t
	}
	retDecl := ""
	if x.Ret != nil {
		t, err := g.goTypeWith(tps, x.Ret)
		if err != nil {
			return "", g.errf(x.Line, "返回类型: %v", err)
		}
		retDecl = " " + t
	}
	savedBuf := g.buf
	savedIndent := g.indent
	g.buf = strings.Builder{}
	g.indent = 0
	// 闭包语义：字面量可见外层作用域（类型环境随之继承）
	savedScopes := g.scopes
	g.scopes = append(append([]map[string]string{}, savedScopes...), scope)
	savedRetGo, savedRetLing, savedTps := g.curRetGo, g.curRetLing, g.curTparams
	g.curRetGo, g.curRetLing, g.curTparams = retDecl, x.Ret, tps
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
	g.scopes = savedScopes
	g.curRetGo, g.curRetLing, g.curTparams = savedRetGo, savedRetLing, savedTps
	out := g.buf.String()
	g.buf = savedBuf
	g.indent = savedIndent
	if bodyErr != nil {
		return "", bodyErr
	}
	// 内嵌进实参表时不能带尾随换行（Go 实参表内换行须跟逗号）
	return strings.TrimRight(out, "\n"), nil
}

func (g *gen) isBuiltinFn(name string) bool {
	switch name {
	case "println", "print", "len", "str", "int", "float", "keys", "values", "has", "push", "assert", "err", "bytes", "char", "jsonParse", "jsonStringify", "isNone":
		return true
	}
	return false
}

var builtinMap = map[string]string{
	"println": "lingPrintln", "print": "lingPrint", "len": "lingLen",
	"str": "lingStr", "int": "lingInt", "float": "lingFloat",
	"keys": "lingKeys", "values": "lingValues", "has": "lingHas",
	"push": "lingPush", "assert": "lingAssert", "err": "lingErr",
	"bytes": "lingBytes", "char": "lingChar",
	"jsonParse": "lingJSONParse", "jsonStringify": "lingJSONStringify",
	"isNone": "lingIsNone",
}

func (g *gen) emitCall(x *syntax.CallEx) (string, error) {
	id, isIdent := x.Callee.(*syntax.Ident)
	if !isIdent {
		return g.emitNonIdentCall(x)
	}

	// 用户 enum 构造器
	if _, isUser := g.caseEnum[id.Name]; isUser {
		var args []string
		for _, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			args = append(args, arg)
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
		return "LingSome(" + arg + ")", nil
	case "None":
		return "LingNone[any]()", nil
	case "Ok", "Err":
		return "", g.errf(x.Line, "Ok/Err 只能出现在 Result 函数的 return（→Go 约束）")
	}

	if bf, ok := builtinMap[id.Name]; ok {
		return g.emitBuiltinCall(x, id.Name, bf)
	}

	if fi, ok := g.fns[id.Name]; ok {
		var args []string
		for i, a := range x.Args {
			arg, err := g.expr(a)
			if err != nil {
				return "", err
			}
			if i < len(fi.params) {
				arg = g.maybeWrap(fi.params[i].typ, g.infer(a), arg)
			}
			args = append(args, arg)
		}
		return fi.goName + "(" + strings.Join(args, ", ") + ")", nil
	}

	// 函数值调用：参数/绑定持有函数（如泛型 fn 的 f: fn(T): U）
	if lt := g.lookup(id.Name); strings.HasPrefix(lt, "func(") {
		return g.emitNonIdentCall(x)
	}

	return "", g.errf(x.Line, "未定义 %s（当前函数 %s）", id.Name, g.curFn)
}

func (g *gen) emitBuiltinCall(x *syntax.CallEx, name, bf string) (string, error) {
	switch name {
	case "assert":
		if len(x.Args) < 1 || len(x.Args) > 2 {
			return "", g.errf(x.Line, "assert 需要 1~2 个参数")
		}
		cond, err := g.expr(x.Args[0])
		if err != nil {
			return "", err
		}
		if g.infer(x.Args[0]) != "bool" {
			cond = "lingTruthy(" + cond + ")"
		}
		if len(x.Args) == 2 {
			msg, err := g.expr(x.Args[1])
			if err != nil {
				return "", err
			}
			return "lingAssert(" + cond + ", " + msg + ")", nil
		}
		return "lingAssert(" + cond + ")", nil
	case "push":
		if len(x.Args) < 2 {
			return "", g.errf(x.Line, "push 需要至少 2 个参数")
		}
		arr, err := g.expr(x.Args[0])
		if err != nil {
			return "", err
		}
		at := g.infer(x.Args[0])
		if !strings.HasPrefix(at, "*LingArr[") {
			return "", g.errf(x.Line, "push 目标类型未知（→Go 需可推断的数组）")
		}
		elem := at[len("*LingArr[") : len(at)-1]
		var vs []string
		for _, a := range x.Args[1:] {
			v, err := g.expr(a)
			if err != nil {
				return "", err
			}
			// 带元素类型的数组在 push 边界收窄（如 [string]）
			vs = append(vs, g.maybeWrap(elem, g.infer(a), v))
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

func (g *gen) emitNonIdentCall(x *syntax.CallEx) (string, error) {
	// 方法调用：obj.name(args)
	if fe, ok := x.Callee.(*syntax.FieldEx); ok {
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
		return obj + "." + fe.Name + "(" + strings.Join(args, ", ") + ")", nil
	}
	// 函数值调用
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
