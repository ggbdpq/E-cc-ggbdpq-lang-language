package format

// ling 格式化器：AST 规范重排（4 空格缩进），整行注释按行号挂回最近的后续构造。
// 限制（spec §9）：行尾注释与 match 臂内注释会丢失；块尾（最后一条语句之后）
// 的注释会移出到块的后面——均为注释位置移动，不影响语义。

import (
	"fmt"
	"strconv"
	"strings"

	"ling/internal/syntax"
)

// Format 把源码格式化为规范形态
func Format(src string) (string, error) {
	prog, err := syntax.Parse(src)
	if err != nil {
		return "", err
	}
	f := &fmtter{lines: strings.Split(src, "\n")}
	f.stmts(prog, 0, 1)
	return f.b.String(), nil
}

type fmtter struct {
	b      strings.Builder
	lines  []string
	lastLn int // 已消费的源码行（1 起）
}

// comments emits the whole-line comments + blank lines between lastLn and line (exclusive), and advances lastLn
func (f *fmtter) comments(line, indent int) {
	if line <= f.lastLn {
		return
	}
	sawBlank := false
	for ln := f.lastLn + 1; ln < line; ln++ {
		text := ""
		if ln-1 < len(f.lines) {
			text = f.lines[ln-1]
		}
		trimmed := strings.TrimSpace(text)
		switch {
		case trimmed == "":
			sawBlank = true
		case strings.HasPrefix(trimmed, "//"):
			if sawBlank {
				f.b.WriteString("\n")
				sawBlank = false
			}
			f.b.WriteString(strings.Repeat("    ", indent) + trimmed + "\n")
		}
		// 行尾注释（代码后的 //）不处理，避免误伤字符串里的 //
	}
	f.lastLn = line - 1
}

func (f *fmtter) w(indent int, s string) {
	f.b.WriteString(strings.Repeat("    ", indent) + s + "\n")
}

func (f *fmtter) stmts(list []syntax.Stmt, indent, line int) {
	for _, s := range list {
		f.stmt(s, indent)
	}
	_ = line
}

func (f *fmtter) stmt(s syntax.Stmt, indent int) {
	f.comments(s.Pos(), indent)
	switch st := s.(type) {
	case *syntax.LetStmt:
		kw := "let"
		if st.Mutable {
			kw = "var"
		}
		anno := ""
		if st.Typ != nil {
			anno = ": " + typ(st.Typ)
		}
		f.w(indent, kw+" "+st.Name+anno+" = "+expr(st.Init, 0, indent))

	case *syntax.AssignStmt:
		op := "="
		switch st.Op {
		case syntax.PlusAssign:
			op = "+="
		case syntax.MinusAssign:
			op = "-="
		case syntax.StarAssign:
			op = "*="
		case syntax.SlashAssign:
			op = "/="
		}
		f.w(indent, expr(st.Target, 0, indent)+" "+op+" "+expr(st.Val, 0, indent))

	case *syntax.ExprStmt:
		f.w(indent, expr(st.X, 0, indent))

	case *syntax.ReturnStmt:
		if st.Val == nil {
			f.w(indent, "return")
		} else {
			f.w(indent, "return "+expr(st.Val, 0, indent))
		}

	case *syntax.IfStmt:
		f.ifChain(st, indent)

	case *syntax.WhileStmt:
		f.w(indent, "while "+expr(st.Cond, 0, indent)+" {")
		f.stmts(st.Body, indent+1, 0)
		f.w(indent, "}")

	case *syntax.ForInStmt:
		f.w(indent, "for "+st.Var+" in "+expr(st.Iter, 0, indent)+" {")
		f.stmts(st.Body, indent+1, 0)
		f.w(indent, "}")

	case *syntax.BreakStmt:
		f.w(indent, "break")
	case *syntax.ContinueStmt:
		f.w(indent, "continue")

	case *syntax.FnDecl:
		f.fnDecl(st, indent)

	case *syntax.StructDecl:
		generic := genericParams(st.TypeParams)
		f.w(indent, "struct "+st.Name+generic+" {")
		for _, fd := range st.Fields {
			f.w(indent+1, fd.Name+": "+typ(fd.Typ))
		}
		f.w(indent, "}")

	case *syntax.EnumDecl:
		generic := genericParams(st.TypeParams)
		f.w(indent, "enum "+st.Name+generic+" {")
		for _, c := range st.Cases {
			if len(c.Payloads) == 0 {
				f.w(indent+1, c.Name)
			} else {
				ps := make([]string, len(c.Payloads))
				for i, p := range c.Payloads {
					ps[i] = typ(p)
				}
				f.w(indent+1, c.Name+"("+strings.Join(ps, ", ")+")")
			}
		}
		f.w(indent, "}")

	case *syntax.ImportStmt:
		f.w(indent, "import "+strconv.Quote(st.Path))

	case *syntax.ExternDecl:
		f.w(indent, "extern "+strconv.Quote(st.Host)+" {")
		for _, fn := range st.Fns {
			f.w(indent+1, fnSig(fn))
		}
		f.w(indent, "}")

	default:
		f.w(indent, fmt.Sprintf("/* 格式化器不支持该语句 */"))
	}
}

// ifChain if / else if / else 扁平化输出
func (f *fmtter) ifChain(st *syntax.IfStmt, indent int) {
	f.w(indent, "if "+expr(st.Cond, 0, indent)+" {")
	f.stmts(st.Then, indent+1, 0)
	if len(st.Else) == 1 {
		if nested, ok := st.Else[0].(*syntax.IfStmt); ok {
			f.b.WriteString(strings.Repeat("    ", indent) + "} else ")
			// 复用链式输出：把嵌套 if 打在同一起始行
			f.ifChainInline(nested, indent)
			return
		}
	}
	if len(st.Else) > 0 {
		f.w(indent, "} else {")
		f.stmts(st.Else, indent+1, 0)
	}
	f.w(indent, "}")
}

// ifChainInline 输出已被消费了 "} else " 前缀的链式 if
func (f *fmtter) ifChainInline(st *syntax.IfStmt, indent int) {
	f.b.WriteString("if " + expr(st.Cond, 0, indent) + " {\n")
	f.stmts(st.Then, indent+1, 0)
	if len(st.Else) == 1 {
		if nested, ok := st.Else[0].(*syntax.IfStmt); ok {
			f.b.WriteString(strings.Repeat("    ", indent) + "} else ")
			f.ifChainInline(nested, indent)
			return
		}
	}
	if len(st.Else) > 0 {
		f.w(indent, "} else {")
		f.stmts(st.Else, indent+1, 0)
	}
	f.w(indent, "}")
}

func genericParams(tps []string) string {
	if len(tps) == 0 {
		return ""
	}
	return "[" + strings.Join(tps, ", ") + "]"
}

func fnSig(d *syntax.FnDecl) string {
	var ps []string
	for _, p := range d.Params {
		if p.Typ == nil {
			ps = append(ps, p.Name)
		} else {
			ps = append(ps, p.Name+": "+typ(p.Typ))
		}
	}
	sig := "fn " + d.Name + genericParams(d.TypeParams) + "(" + strings.Join(ps, ", ") + ")"
	if d.Ret != nil {
		sig += ": " + typ(d.Ret)
	}
	return sig
}

func (f *fmtter) fnDecl(st *syntax.FnDecl, indent int) {
	if st.Recv != nil {
		// 接收者无冒号（Go 风格，spec §3.1）
		recv := "(" + st.Recv.Name + " " + typ(st.Recv.Typ) + ")"
		var ps []string
		for _, p := range st.Params {
			if p.Typ == nil {
				ps = append(ps, p.Name)
			} else {
				ps = append(ps, p.Name+": "+typ(p.Typ))
			}
		}
		sig := "fn " + recv + "." + st.Name + "(" + strings.Join(ps, ", ") + ")"
		if st.Ret != nil {
			sig += ": " + typ(st.Ret)
		}
		f.w(indent, sig+" {")
	} else {
		f.w(indent, fnSig(st)+" {")
	}
	f.stmts(st.Body, indent+1, 0)
	f.w(indent, "}")
}

// ———— 类型 ————

func typ(t syntax.TypeEx) string {
	switch tt := t.(type) {
	case *syntax.NamedType:
		if len(tt.Args) == 0 {
			return tt.Name
		}
		as := make([]string, len(tt.Args))
		for i, a := range tt.Args {
			as[i] = typ(a)
		}
		return tt.Name + "[" + strings.Join(as, ", ") + "]"
	case *syntax.ArrType:
		return "[" + typ(tt.Elem) + "]"
	case *syntax.MapType:
		return "map[" + typ(tt.Key) + "]" + typ(tt.Val)
	case *syntax.PtrType:
		return "&" + typ(tt.Elem)
	case *syntax.FnType:
		var ps []string
		for _, p := range tt.Params {
			ps = append(ps, typ(p))
		}
		sig := "fn(" + strings.Join(ps, ", ") + ")"
		if tt.Ret != nil {
			sig += ": " + typ(tt.Ret)
		}
		return sig
	}
	return "?"
}

// ———— 表达式（优先级感知加括号） ————

func precOf(k syntax.Kind) int {
	switch k {
	case syntax.Or:
		return 1
	case syntax.And:
		return 2
	case syntax.Eq, syntax.Neq:
		return 3
	case syntax.Lt, syntax.Le, syntax.Gt, syntax.Ge:
		return 4
	case syntax.Dot2:
		return 5
	case syntax.Plus, syntax.Minus, syntax.BitOr, syntax.Xor:
		return 6
	case syntax.Star, syntax.Slash, syntax.Percent, syntax.Amp, syntax.Shl, syntax.Shr:
		return 7
	}
	return 0
}

func exprPrec(e syntax.Expr) int {
	switch x := e.(type) {
	case *syntax.Binary:
		return precOf(x.Op)
	case *syntax.Unary:
		return 8
	}
	return 9
}

// expr 以 minPrec 门槛输出表达式；低于门槛的子式加括号
func expr(e syntax.Expr, minPrec, indent int) string {
	s := exprRaw(e, indent)
	if exprPrec(e) < minPrec {
		return "(" + s + ")"
	}
	return s
}

func exprRaw(e syntax.Expr, indent int) string {
	switch x := e.(type) {
	case *syntax.IntLit:
		return strconv.FormatInt(x.Val, 10)
	case *syntax.FloatLit:
		s := strconv.FormatFloat(x.Val, 'g', -1, 64)
		// 保住 float 身份：整数值的浮点字面量补 ".0"（否则重解析变 int）
		if !strings.ContainsAny(s, ".eE") {
			s += ".0"
		}
		return s
	case *syntax.StrLit:
		return quoteStr(x.Val)
	case *syntax.BoolLit:
		return strconv.FormatBool(x.Val)
	case *syntax.Ident:
		return x.Name
	case *syntax.Unary:
		op := map[syntax.Kind]string{syntax.Minus: "-", syntax.Not: "!", syntax.Amp: "&"}[x.Op]
		return op + expr(x.X, 8, indent)
	case *syntax.Binary:
		p := precOf(x.Op)
		op := map[syntax.Kind]string{
			syntax.Plus: "+", syntax.Minus: "-", syntax.Star: "*", syntax.Slash: "/",
			syntax.Percent: "%", syntax.Eq: "==", syntax.Neq: "!=", syntax.Lt: "<",
			syntax.Le: "<=", syntax.Gt: ">", syntax.Ge: ">=", syntax.And: "&&",
			syntax.Or: "||", syntax.Amp: "&", syntax.BitOr: "|", syntax.Xor: "^",
			syntax.Shl: "<<", syntax.Shr: ">>", syntax.Dot2: "..",
		}[x.Op]
		return expr(x.L, p, indent) + " " + op + " " + expr(x.R, p+1, indent)
	case *syntax.RangeEx:
		return expr(x.Lo, 5, indent) + ".." + expr(x.Hi, 6, indent)
	case *syntax.CallEx:
		var as []string
		for _, a := range x.Args {
			as = append(as, expr(a, 0, indent))
		}
		return expr(x.Callee, 9, indent) + "(" + strings.Join(as, ", ") + ")"
	case *syntax.IndexEx:
		return expr(x.Obj, 9, indent) + "[" + expr(x.Idx, 0, indent) + "]"
	case *syntax.SliceEx:
		return expr(x.Obj, 9, indent) + "[" + expr(x.Lo, 0, indent) + ":" + expr(x.Hi, 0, indent) + "]"
	case *syntax.FieldEx:
		return expr(x.Obj, 9, indent) + "." + x.Name
	case *syntax.TryEx:
		return expr(x.X, 9, indent) + "?"
	case *syntax.StructLit:
		var fs []string
		for _, fl := range x.Fields {
			fs = append(fs, fl.Name+": "+expr(fl.Val, 0, indent))
		}
		return x.TypeName + "{" + strings.Join(fs, ", ") + "}"
	case *syntax.MapLit:
		var kvs []string
		for _, it := range x.Items {
			kvs = append(kvs, expr(it.Key, 0, indent)+": "+expr(it.Val, 0, indent))
		}
		return "{" + strings.Join(kvs, ", ") + "}"
	case *syntax.ArrLit:
		var is []string
		for _, el := range x.Items {
			is = append(is, expr(el, 0, indent))
		}
		return "[" + strings.Join(is, ", ") + "]"
	case *syntax.MatchEx:
		var b strings.Builder
		b.WriteString("match " + expr(x.Scrut, 0, indent) + " {\n")
		for _, arm := range x.Arms {
			b.WriteString(strings.Repeat("    ", indent+1) + pat(arm.Pat) + " => " + expr(arm.Body, 0, indent+1) + "\n")
		}
		b.WriteString(strings.Repeat("    ", indent) + "}")
		return b.String()
	case *syntax.FnLit:
		var ps []string
		for _, p := range x.Params {
			if p.Typ == nil {
				ps = append(ps, p.Name)
			} else {
				ps = append(ps, p.Name+": "+typ(p.Typ))
			}
		}
		sig := "fn(" + strings.Join(ps, ", ") + ")"
		if x.Ret != nil {
			sig += ": " + typ(x.Ret)
		}
		f := &fmtter{lines: []string{}}
		f.b.WriteString(sig + " {\n")
		f.stmts(x.Body, indent+1, 0)
		f.b.WriteString(strings.Repeat("    ", indent) + "}")
		return f.b.String()
	}
	return "?"
}

func pat(p syntax.Pat) string {
	switch pp := p.(type) {
	case *syntax.WildPat:
		return "_"
	case *syntax.BindPat:
		return pp.Name
	case *syntax.LitPat:
		return expr(pp.Val, 0, 0)
	case *syntax.CasePat:
		if len(pp.Subs) == 0 {
			return pp.Name
		}
		ss := make([]string, len(pp.Subs))
		for i, s := range pp.Subs {
			ss[i] = pat(s)
		}
		return pp.Name + "(" + strings.Join(ss, ", ") + ")"
	}
	return "?"
}

// quoteStr 按 ling 转义规则输出字符串字面量（仅 \n \t \r \" \\ 五种）
func quoteStr(s string) string {
	var b strings.Builder
	b.WriteByte('"')
	for _, r := range s {
		switch r {
		case '\n':
			b.WriteString(`\n`)
		case '\t':
			b.WriteString(`\t`)
		case '\r':
			b.WriteString(`\r`)
		case '"':
			b.WriteString(`\"`)
		case '\\':
			b.WriteString(`\\`)
		default:
			b.WriteRune(r)
		}
	}
	b.WriteByte('"')
	return b.String()
}
