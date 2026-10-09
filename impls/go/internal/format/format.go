package format

// format.go：langGo 格式化器（v0.5 Safe Editing）。
//
// 渲染为行缓冲；注释按 trivia 流插回（v0.5 Lossless 层）：
//   - 行首注释：独立行输出，保留注释间空行分组（最多一空行）；
//   - 行尾注释：追加到语句渲染输出的最后一行（两空格间隔）；
//   - 表达式内注释：语句边界冲刷（位置后移，文本零丢失——spec 允许）；
//   - 块尾注释：块内最后冲刷，原位保留在 `}` 之前。
//
// 硬契约（测试锁定）：100% 注释保留、幂等 format(format(x))==format(x)、
// 语义等价、确定性输出。

import (
	"strconv"
	"strings"

	"lang-go/internal/syntax"
)

// Format 把源码格式化为规范形态.
func Format(src string) (string, error) {
	prog, err := syntax.Parse(src)
	if err != nil {
		return "", err
	}

	f := &fmtter{cm: &commentTracker{all: syntax.ScanComments(src)}}
	f.stmts(prog, 0)
	f.flushRemaining(0)

	if len(f.lines) == 0 {
		return "", nil
	}

	return strings.Join(f.lines, "\n") + "\n", nil
}

type fmtter struct {
	lines       []string        // 输出行缓冲
	cm          *commentTracker // 共享注释流（fnlit 局部渲染共享同一流）
	lastSrcLine int             // 最近消费的源码行（空行分组判定）
}

// —— 注释流 ——

type commentTracker struct {
	all []syntax.Comment
	pos int
}

func (t *commentTracker) peek() (syntax.Comment, bool) {
	if t.pos < len(t.all) {
		return t.all[t.pos], true
	}

	return syntax.Comment{}, false
}

// flushLeading 冲刷 start 之前的**整行**注释（行尾注释留在流里给 flushTrailing）.
func (f *fmtter) flushLeading(start int, indent int) {
	for {
		c, ok := f.cm.peek()
		if !ok || c.Span.Start.Offset >= start {
			return
		}

		f.cm.pos++
		if c.Line()-f.lastSrcLine >= 2 {
			f.lines = append(f.lines, "")
		}

		f.lines = append(f.lines, strings.Repeat("    ", indent)+c.Text)
		f.lastSrcLine = c.Line()
	}
}

// flushTrailing 冲刷语句末行上的行尾注释（追加到最近输出行）.
func (f *fmtter) flushTrailing(endOffset, endLine int) {
	for {
		c, ok := f.cm.peek()
		if !ok || c.Line() != endLine || c.Span.Start.Offset < endOffset {
			return
		}

		f.cm.pos++
		if len(f.lines) == 0 {
			f.lines = append(f.lines, "")
		}

		f.lines[len(f.lines)-1] += "  " + c.Text
		f.lastSrcLine = c.Line()
	}
}

// flushRemaining 冲刷全部挂起注释（块尾/文件尾），整行输出.
func (f *fmtter) flushRemaining(indent int) {
	for {
		c, ok := f.cm.peek()
		if !ok {
			return
		}

		f.cm.pos++
		if c.Line()-f.lastSrcLine >= 2 {
			f.lines = append(f.lines, "")
		}

		f.lines = append(f.lines, strings.Repeat("    ", indent)+c.Text)
		f.lastSrcLine = c.Line()
	}
}

func (f *fmtter) w(indent int, s string) {
	parts := strings.Split(s, "\n")
	for i, p := range parts {
		if i == 0 {
			f.lines = append(f.lines, strings.Repeat("    ", indent)+p)
		} else {
			f.lines = append(f.lines, p)
		}
	}
}

// spanned 语句节点 → Span（全部语句都实现 Spanned）.
func spanned(st syntax.Stmt) syntax.Span {
	if sp, ok := st.(syntax.Spanned); ok {
		return *sp.NodeSpan()
	}

	return syntax.Span{}
}

func (f *fmtter) stmts(list []syntax.Stmt, indent int) {
	for _, s := range list {
		sp := spanned(s)
		f.flushLeading(sp.Start.Offset, indent)
		f.lastSrcLine = sp.End.Line
		f.stmt(s, indent)
		f.flushTrailing(sp.End.Offset, sp.End.Line)
	}

	f.flushRemaining(indent)
}

func (f *fmtter) stmt(s syntax.Stmt, indent int) {
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

		f.w(indent, kw+" "+st.Name+anno+" = "+f.expr(st.Init, 0, indent))

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

		f.w(indent, f.expr(st.Target, 0, indent)+" "+op+" "+f.expr(st.Val, 0, indent))

	case *syntax.ExprStmt:
		f.w(indent, f.expr(st.X, 0, indent))

	case *syntax.ReturnStmt:
		if st.Val == nil {
			f.w(indent, "return")
		} else {
			f.w(indent, "return "+f.expr(st.Val, 0, indent))
		}

	case *syntax.IfStmt:
		f.ifChain(st, indent)

	case *syntax.WhileStmt:
		f.w(indent, "while "+f.expr(st.Cond, 0, indent)+" {")
		f.stmts(st.Body, indent+1)
		f.w(indent, "}")

	case *syntax.ForInStmt:
		f.w(indent, "for "+st.Var+" in "+f.expr(st.Iter, 0, indent)+" {")
		f.stmts(st.Body, indent+1)
		f.w(indent, "}")

	case *syntax.BreakStmt:
		f.w(indent, "break")
	case *syntax.ContinueStmt:
		f.w(indent, "continue")

	case *syntax.GoStmt:
		f.w(indent, "go "+f.expr(st.Call, 0, indent))

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
		f.w(indent, "/* 格式化器不支持该语句 */")
	}
}

// ifChain if / else if / else 扁平化输出.
func (f *fmtter) ifChain(st *syntax.IfStmt, indent int) {
	f.w(indent, "if "+f.expr(st.Cond, 0, indent)+" {")
	f.stmts(st.Then, indent+1)
	f.elseTail(st, indent)
}

// elseTail 输出 else 分支（else if 保持同起始行）.
func (f *fmtter) elseTail(st *syntax.IfStmt, indent int) {
	if len(st.Else) == 1 {
		if nested, ok := st.Else[0].(*syntax.IfStmt); ok {
			f.w(indent, "} else if "+f.expr(nested.Cond, 0, indent)+" {")
			f.stmts(nested.Then, indent+1)
			f.elseTail(nested, indent)

			return
		}
	}

	if len(st.Else) > 0 {
		f.w(indent, "} else {")
		f.stmts(st.Else, indent+1)
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

	f.stmts(st.Body, indent+1)
	f.w(indent, "}")
}

// —— 类型 ——

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

// —— 表达式（优先级感知加括号） ——

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

// expr 以 minPrec 门槛输出表达式；低于门槛的子式加括号.
func (f *fmtter) expr(e syntax.Expr, minPrec, indent int) string {
	s := f.exprRaw(e, indent)
	if exprPrec(e) < minPrec {
		return "(" + s + ")"
	}

	return s
}

func (f *fmtter) exprRaw(e syntax.Expr, indent int) string {
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
		return op + f.expr(x.X, 8, indent)
	case *syntax.Binary:
		p := precOf(x.Op)
		op := map[syntax.Kind]string{
			syntax.Plus: "+", syntax.Minus: "-", syntax.Star: "*", syntax.Slash: "/",
			syntax.Percent: "%", syntax.Eq: "==", syntax.Neq: "!=", syntax.Lt: "<",
			syntax.Le: "<=", syntax.Gt: ">", syntax.Ge: ">=", syntax.And: "&&",
			syntax.Or: "||", syntax.Amp: "&", syntax.BitOr: "|", syntax.Xor: "^",
			syntax.Shl: "<<", syntax.Shr: ">>", syntax.Dot2: "..",
		}[x.Op]

		return f.expr(x.L, p, indent) + " " + op + " " + f.expr(x.R, p+1, indent)
	case *syntax.RangeEx:
		return f.expr(x.Lo, 5, indent) + ".." + f.expr(x.Hi, 6, indent)
	case *syntax.CallEx:
		var as []string
		for _, a := range x.Args {
			as = append(as, f.expr(a, 0, indent))
		}

		return f.expr(x.Callee, 9, indent) + "(" + strings.Join(as, ", ") + ")"
	case *syntax.IndexEx:
		return f.expr(x.Obj, 9, indent) + "[" + f.expr(x.Idx, 0, indent) + "]"
	case *syntax.SliceEx:
		return f.expr(x.Obj, 9, indent) + "[" + f.expr(x.Lo, 0, indent) + ":" + f.expr(x.Hi, 0, indent) + "]"
	case *syntax.FieldEx:
		return f.expr(x.Obj, 9, indent) + "." + x.Name
	case *syntax.TryEx:
		return f.expr(x.X, 9, indent) + "?"
	case *syntax.StructLit:
		var fs []string
		for _, fl := range x.Fields {
			fs = append(fs, fl.Name+": "+f.expr(fl.Val, 0, indent))
		}

		return x.TypeName + "{" + strings.Join(fs, ", ") + "}"
	case *syntax.MapLit:
		var kvs []string
		for _, it := range x.Items {
			kvs = append(kvs, f.expr(it.Key, 0, indent)+": "+f.expr(it.Val, 0, indent))
		}

		return "{" + strings.Join(kvs, ", ") + "}"
	case *syntax.ArrLit:
		var is []string
		for _, el := range x.Items {
			is = append(is, f.expr(el, 0, indent))
		}

		return "[" + strings.Join(is, ", ") + "]"
	case *syntax.MatchEx:
		var b strings.Builder
		b.WriteString("match " + f.expr(x.Scrut, 0, indent) + " {\n")

		for _, arm := range x.Arms {
			b.WriteString(strings.Repeat("    ", indent+1) + f.pat(arm.Pat) + " => " + f.expr(arm.Body, 0, indent+1) + "\n")
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

		f2 := &fmtter{lines: []string{}, cm: f.cm}
		f2.w(0, sig+" {")
		f2.stmts(x.Body, indent+1)
		f2.w(indent, "}")

		return strings.Join(f2.lines, "\n")
	}

	return "?"
}

func (f *fmtter) pat(p syntax.Pat) string {
	switch pp := p.(type) {
	case *syntax.WildPat:
		return "_"
	case *syntax.BindPat:
		return pp.Name
	case *syntax.LitPat:
		return f.expr(pp.Val, 0, 0)
	case *syntax.CasePat:
		if len(pp.Subs) == 0 {
			return pp.Name
		}

		ss := make([]string, len(pp.Subs))
		for i, s := range pp.Subs {
			ss[i] = f.pat(s)
		}

		return pp.Name + "(" + strings.Join(ss, ", ") + ")"
	}

	return "?"
}

// quoteStr 按 langGo 转义规则输出字符串字面量（仅 \n \t \r \" \\ 五种）.
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
