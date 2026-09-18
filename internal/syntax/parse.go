package syntax

import (
	"fmt"
	"strconv"
	"strings"
	"unicode"
)

// Error 解析错误。Incomplete 为 true 表示输入在语法中途截断，REPL 据此续行；
// Snippet 为出错行的源码摘录（空白行回退前一行）。
type Error struct {
	Msg        string
	Line       int
	Incomplete bool
	Snippet    string
}

func (e *Error) Error() string {
	if e.Snippet == "" {
		return fmt.Sprintf("第%d行: %s", e.Line, e.Msg)
	}
	return fmt.Sprintf("第%d行: %s\n  | %s", e.Line, e.Msg, e.Snippet)
}

// lineSnippet 取 1-based 行号的内容（trim 后）；空行回退前一行
func lineSnippet(lines []string, line int) string {
	if line < 1 {
		line = 1
	}
	if line > len(lines) {
		line = len(lines)
	}
	if line < 1 {
		return ""
	}
	if t := strings.TrimSpace(lines[line-1]); t != "" {
		return t
	}
	if line > 1 {
		return strings.TrimSpace(lines[line-2])
	}
	return ""
}

type Parser struct {
	toks     []Token
	i        int
	noStruct int      // >0 时处于控制头位置，禁止裸 struct 字面量（spec §3.4）
	lines    []string // 源码行（错误摘录用）
}

// Parse 解析整个源文件/REPL 片段，返回语句序列
func Parse(src string) ([]Stmt, error) {
	toks, err := Lex(src)
	if err != nil {
		return nil, err
	}
	p := &Parser{toks: toks, lines: strings.Split(src, "\n")}
	var prog []Stmt
	for {
		p.skipNl()
		if p.at(EOF) {
			return prog, nil
		}
		s, err := p.parseTop()
		if err != nil {
			return nil, err
		}
		prog = append(prog, s)
	}
}

func (p *Parser) cur() Token { return p.toks[p.i] }

func (p *Parser) next() Token {
	t := p.toks[p.i]
	if p.i < len(p.toks)-1 {
		p.i++
	}
	return t
}

func (p *Parser) at(k Kind) bool { return p.toks[p.i].Kind == k }

func (p *Parser) skipNl() {
	for p.at(Newline) {
		p.i++
	}
}

func (p *Parser) accept(k Kind) bool {
	if p.at(k) {
		p.i++
		return true
	}
	return false
}

func (p *Parser) errAt(t Token, format string, args ...any) *Error {
	return &Error{
		Msg:        fmt.Sprintf(format, args...),
		Line:       t.Line,
		Incomplete: t.Kind == EOF,
		Snippet:    lineSnippet(p.lines, t.Line),
	}
}

func (p *Parser) expect(k Kind) (Token, error) {
	if p.at(k) {
		return p.next(), nil
	}
	t := p.cur()
	return Token{}, p.errAt(t, "期望 %s，得到 %s", k, t.Kind)
}

func (p *Parser) expectName() (string, error) {
	if p.at(TokIdent) {
		return p.next().Lit, nil
	}
	if p.at(TokUnderscore) {
		p.next()
		return "_", nil
	}
	t := p.cur()
	return "", p.errAt(t, "期望标识符，得到 %s", t.Kind)
}

// —— 顶层 ——

var reserved = map[Kind]string{
	KwGo: "go", KwChan: "chan", KwInterface: "interface",
}

func (p *Parser) parseTop() (Stmt, error) {
	t := p.cur()
	if msg, ok := reserved[t.Kind]; ok {
		return nil, p.errAt(t, "保留字 %s 尚未启用（规划于 M3+）", msg)
	}
	switch t.Kind {
	case KwImport:
		p.next()
		path, err := p.expect(TokStr)
		if err != nil {
			return nil, err
		}
		return &ImportStmt{Path: path.Lit, Line: t.Line}, nil
	case KwExtern:
		return p.parseExtern()
	}
	return p.parseStmt()
}

// parseExtern extern "host" { fn 签名（无函数体）... }
func (p *Parser) parseExtern() (Stmt, error) {
	t := p.next() // extern
	host, err := p.expect(TokStr)
	if err != nil {
		return nil, p.errAt(t, "extern 后须是宿主名字符串，如 extern \"go\"")
	}
	if _, err := p.expect(LBrace); err != nil {
		return nil, err
	}
	decl := &ExternDecl{Host: host.Lit, Line: t.Line}
	for {
		p.skipNl()
		if p.accept(RBrace) {
			return decl, nil
		}
		if p.at(EOF) {
			return nil, p.errAt(p.cur(), "extern 块未闭合")
		}
		if _, err := p.expect(KwFn); err != nil {
			return nil, err
		}
		name, err := p.expectName()
		if err != nil {
			return nil, err
		}
		tps, err := p.parseTypeParams()
		if err != nil {
			return nil, err
		}
		params, err := p.parseParams()
		if err != nil {
			return nil, err
		}
		var ret TypeEx
		if p.accept(Colon) {
			ret, err = p.parseType()
			if err != nil {
				return nil, err
			}
		}
		decl.Fns = append(decl.Fns, &FnDecl{
			Name: name, TypeParams: tps, Params: params, Ret: ret, Line: t.Line,
		})
	}
}

// —— 语句 ——

func (p *Parser) parseStmt() (Stmt, error) {
	t := p.cur()
	switch t.Kind {
	case KwLet, KwVar:
		p.next()
		name, err := p.expectName()
		if err != nil {
			return nil, err
		}
		var typ TypeEx
		if p.accept(Colon) {
			typ, err = p.parseType()
			if err != nil {
				return nil, err
			}
		}
		if _, err := p.expect(Assign); err != nil {
			return nil, err
		}
		init, err := p.parseExpr(0)
		if err != nil {
			return nil, err
		}
		return &LetStmt{Name: name, Mutable: t.Kind == KwVar, Typ: typ, Init: init, Line: t.Line}, nil

	case KwReturn:
		p.next()
		var val Expr
		if !p.at(Newline) && !p.at(RBrace) && !p.at(EOF) {
			var err error
			val, err = p.parseExpr(0)
			if err != nil {
				return nil, err
			}
		}
		return &ReturnStmt{Val: val, Line: t.Line}, nil

	case KwIf:
		return p.parseIf()

	case KwWhile:
		p.next()
		p.noStruct++
		cond, err := p.parseExpr(0)
		p.noStruct--
		if err != nil {
			return nil, err
		}
		body, err := p.parseBlock()
		if err != nil {
			return nil, err
		}
		return &WhileStmt{Cond: cond, Body: body, Line: t.Line}, nil

	case KwFor:
		p.next()
		name, err := p.expectName()
		if err != nil {
			return nil, err
		}
		if _, err := p.expect(KwIn); err != nil {
			return nil, err
		}
		p.noStruct++
		iter, err := p.parseExpr(0)
		p.noStruct--
		if err != nil {
			return nil, err
		}
		body, err := p.parseBlock()
		if err != nil {
			return nil, err
		}
		return &ForInStmt{Var: name, Iter: iter, Body: body, Line: t.Line}, nil

	case KwBreak, KwContinue:
		p.next()
		if t.Kind == KwBreak {
			return &BreakStmt{Line: t.Line}, nil
		}
		return &ContinueStmt{Line: t.Line}, nil

	case KwFn:
		return p.parseFnDecl()

	case KwStruct:
		return p.parseStruct()

	case KwEnum:
		return p.parseEnum()

	default:
		e, err := p.parseExpr(0)
		if err != nil {
			return nil, err
		}
		if p.accept(Assign) {
			if err := checkTarget(e); err != nil {
				return nil, err
			}
			v, err := p.parseExpr(0)
			if err != nil {
				return nil, err
			}
			return &AssignStmt{Target: e, Val: v, Line: t.Line}, nil
		}
		return &ExprStmt{X: e, Line: t.Line}, nil
	}
}

// checkTarget 赋值目标只能是变量、字段链或索引
func checkTarget(e Expr) error {
	switch t := e.(type) {
	case *Ident:
		return nil
	case *FieldEx:
		return checkTarget(t.Obj)
	case *IndexEx:
		return checkTarget(t.Obj)
	default:
		return &Error{Msg: "赋值目标不合法", Line: e.Pos()}
	}
}

func (p *Parser) parseIf() (Stmt, error) {
	t := p.next() // if
	p.noStruct++
	cond, err := p.parseExpr(0)
	p.noStruct--
	if err != nil {
		return nil, err
	}
	then, err := p.parseBlock()
	if err != nil {
		return nil, err
	}
	st := &IfStmt{Cond: cond, Then: then, Line: t.Line}
	p.skipNl()
	if p.accept(KwElse) {
		if p.at(KwIf) {
			els, err := p.parseIf()
			if err != nil {
				return nil, err
			}
			st.Else = []Stmt{els}
		} else {
			els, err := p.parseBlock()
			if err != nil {
				return nil, err
			}
			st.Else = els
		}
	}
	return st, nil
}

func (p *Parser) parseBlock() ([]Stmt, error) {
	if _, err := p.expect(LBrace); err != nil {
		return nil, err
	}
	var stmts []Stmt
	for {
		p.skipNl()
		if p.accept(RBrace) {
			return stmts, nil
		}
		if p.at(EOF) {
			return nil, p.errAt(p.cur(), "块未闭合，缺少 }")
		}
		s, err := p.parseStmt()
		if err != nil {
			return nil, err
		}
		stmts = append(stmts, s)
	}
}

// —— 声明 ——

func (p *Parser) parseFnDecl() (Stmt, error) {
	t := p.next() // fn
	var recv *Param
	if p.accept(LParen) {
		rname, err := p.expectName()
		if err != nil {
			return nil, err
		}
		if p.at(Amp) {
			return nil, p.errAt(p.cur(), "v0 暂不支持指针接收者")
		}
		rtyp, err := p.parseType()
		if err != nil {
			return nil, err
		}
		if _, err := p.expect(RParen); err != nil {
			return nil, err
		}
		if _, err := p.expect(Dot); err != nil {
			return nil, err
		}
		recv = &Param{Name: rname, Typ: rtyp}
	}
	name, err := p.expectName()
	if err != nil {
		return nil, err
	}
	decl, err := p.parseFuncRest(t.Line, recv, name)
	if err != nil {
		return nil, err
	}
	return decl, nil
}

func (p *Parser) parseFuncRest(line int, recv *Param, name string) (*FnDecl, error) {
	tps, err := p.parseTypeParams()
	if err != nil {
		return nil, err
	}
	params, err := p.parseParams()
	if err != nil {
		return nil, err
	}
	var ret TypeEx
	if p.accept(Colon) {
		ret, err = p.parseType()
		if err != nil {
			return nil, err
		}
	}
	body, err := p.parseBlock()
	if err != nil {
		return nil, err
	}
	return &FnDecl{Name: name, Recv: recv, TypeParams: tps, Params: params, Ret: ret, Body: body, Line: line}, nil
}

func (p *Parser) parseTypeParams() ([]string, error) {
	if !p.accept(LBracket) {
		return nil, nil
	}
	var tps []string
	for {
		name, err := p.expectName()
		if err != nil {
			return nil, err
		}
		tps = append(tps, name)
		if !p.accept(Comma) {
			break
		}
	}
	if _, err := p.expect(RBracket); err != nil {
		return nil, err
	}
	return tps, nil
}

func (p *Parser) parseParams() ([]Param, error) {
	if _, err := p.expect(LParen); err != nil {
		return nil, err
	}
	var params []Param
	for !p.at(RParen) {
		name, err := p.expectName()
		if err != nil {
			return nil, err
		}
		// 注解可省略：v0 无静态检查，注解为文档性（spec §1）
		var typ TypeEx
		if p.accept(Colon) {
			typ, err = p.parseType()
			if err != nil {
				return nil, err
			}
		}
		params = append(params, Param{Name: name, Typ: typ})
		if !p.accept(Comma) {
			break
		}
	}
	if _, err := p.expect(RParen); err != nil {
		return nil, err
	}
	return params, nil
}

func (p *Parser) parseStruct() (Stmt, error) {
	t := p.next() // struct
	name, err := p.expectName()
	if err != nil {
		return nil, err
	}
	tps, err := p.parseTypeParams()
	if err != nil {
		return nil, err
	}
	if _, err := p.expect(LBrace); err != nil {
		return nil, err
	}
	var fields []Param
	for {
		p.skipNl()
		if p.accept(RBrace) {
			break
		}
		if p.at(EOF) {
			return nil, p.errAt(p.cur(), "struct 未闭合")
		}
		fname, err := p.expectName()
		if err != nil {
			return nil, err
		}
		if _, err := p.expect(Colon); err != nil {
			return nil, err
		}
		typ, err := p.parseType()
		if err != nil {
			return nil, err
		}
		fields = append(fields, Param{Name: fname, Typ: typ})
		p.skipNl()
		p.accept(Comma)
	}
	return &StructDecl{Name: name, TypeParams: tps, Fields: fields, Line: t.Line}, nil
}

func (p *Parser) parseEnum() (Stmt, error) {
	t := p.next() // enum
	name, err := p.expectName()
	if err != nil {
		return nil, err
	}
	tps, err := p.parseTypeParams()
	if err != nil {
		return nil, err
	}
	if _, err := p.expect(LBrace); err != nil {
		return nil, err
	}
	var cases []EnumCase
	for {
		p.skipNl()
		if p.accept(RBrace) {
			break
		}
		if p.at(EOF) {
			return nil, p.errAt(p.cur(), "enum 未闭合")
		}
		cn, err := p.expectName()
		if err != nil {
			return nil, err
		}
		case_ := EnumCase{Name: cn, Line: p.cur().Line}
		if p.accept(LParen) {
			for !p.at(RParen) {
				typ, err := p.parseType()
				if err != nil {
					return nil, err
				}
				case_.Payloads = append(case_.Payloads, typ)
				if !p.accept(Comma) {
					break
				}
			}
			if _, err := p.expect(RParen); err != nil {
				return nil, err
			}
		}
		cases = append(cases, case_)
		p.skipNl()
		p.accept(Comma)
	}
	return &EnumDecl{Name: name, TypeParams: tps, Cases: cases, Line: t.Line}, nil
}

// —— 类型注解 ——

func (p *Parser) parseType() (TypeEx, error) {
	t := p.cur()
	switch t.Kind {
	case Amp:
		p.next()
		elem, err := p.parseType()
		if err != nil {
			return nil, err
		}
		return &PtrType{Elem: elem}, nil
	case KwFn: // 函数类型：fn(int, string): bool
		p.next()
		if _, err := p.expect(LParen); err != nil {
			return nil, err
		}
		ft := &FnType{}
		for !p.at(RParen) {
			pt, err := p.parseType()
			if err != nil {
				return nil, err
			}
			ft.Params = append(ft.Params, pt)
			if !p.accept(Comma) {
				break
			}
		}
		if _, err := p.expect(RParen); err != nil {
			return nil, err
		}
		if p.accept(Colon) {
			ret, err := p.parseType()
			if err != nil {
				return nil, err
			}
			ft.Ret = ret
		}
		return ft, nil
	case LBracket:
		p.next()
		elem, err := p.parseType()
		if err != nil {
			return nil, err
		}
		if _, err := p.expect(RBracket); err != nil {
			return nil, err
		}
		return &ArrType{Elem: elem}, nil
	case TokIdent:
		p.next()
		if t.Lit == "map" && p.at(LBracket) {
			p.next()
			k, err := p.parseType()
			if err != nil {
				return nil, err
			}
			if _, err := p.expect(RBracket); err != nil {
				return nil, err
			}
			v, err := p.parseType()
			if err != nil {
				return nil, err
			}
			return &MapType{Key: k, Val: v}, nil
		}
		nt := &NamedType{Name: t.Lit}
		if p.accept(LBracket) {
			for !p.at(RBracket) {
				a, err := p.parseType()
				if err != nil {
					return nil, err
				}
				nt.Args = append(nt.Args, a)
				if !p.accept(Comma) {
					break
				}
			}
			if _, err := p.expect(RBracket); err != nil {
				return nil, err
			}
		}
		return nt, nil
	default:
		return nil, p.errAt(t, "期望类型，得到 %s", t.Kind)
	}
}

// —— 表达式（Pratt） ——

func precOf(k Kind) int {
	switch k {
	case Or:
		return 1
	case And:
		return 2
	case Eq, Neq:
		return 3
	case Lt, Le, Gt, Ge:
		return 4
	case Dot2:
		return 5
	case Plus, Minus, BitOr, Xor:
		// 对齐 Go：+ - | ^ 同级
		return 6
	case Star, Slash, Percent, Amp, Shl, Shr:
		// 对齐 Go：* / % << >> & 同级
		return 7
	}
	return 0
}

func (p *Parser) parseExpr(minPrec int) (Expr, error) {
	left, err := p.parseUnary()
	if err != nil {
		return nil, err
	}
	for {
		t := p.cur()
		prec := precOf(t.Kind)
		if prec == 0 || prec < minPrec {
			return left, nil
		}
		p.next()
		right, err := p.parseExpr(prec + 1)
		if err != nil {
			return nil, err
		}
		if t.Kind == Dot2 {
			left = &RangeEx{Lo: left, Hi: right, Line: t.Line}
		} else {
			left = &Binary{Op: t.Kind, L: left, R: right, Line: t.Line}
		}
	}
}

func (p *Parser) parseUnary() (Expr, error) {
	t := p.cur()
	switch t.Kind {
	case Minus, Not, Amp:
		p.next()
		x, err := p.parseUnary()
		if err != nil {
			return nil, err
		}
		return &Unary{Op: t.Kind, X: x, Line: t.Line}, nil
	}
	return p.parsePostfix()
}

func (p *Parser) parsePostfix() (Expr, error) {
	x, err := p.parsePrimary()
	if err != nil {
		return nil, err
	}
	for {
		t := p.cur()
		switch t.Kind {
		case LParen: // 调用
			p.next()
			var args []Expr
			for {
				p.skipNl()
				if p.at(RParen) {
					break
				}
				a, err := p.parseExpr(0)
				if err != nil {
					return nil, err
				}
				args = append(args, a)
				p.skipNl()
				if !p.accept(Comma) {
					break
				}
			}
			p.skipNl()
			if _, err := p.expect(RParen); err != nil {
				return nil, err
			}
			x = &CallEx{Callee: x, Args: args, Line: t.Line}
		case LBracket: // 索引或切片
			p.next()
			p.skipNl()
			lo, err := p.parseExpr(0)
			if err != nil {
				return nil, err
			}
			if p.accept(Colon) {
				if p.at(RBracket) {
					return nil, p.errAt(p.cur(), "切片需要上界（v0 不支持开区间切片）")
				}
				hi, err := p.parseExpr(0)
				if err != nil {
					return nil, err
				}
				p.skipNl()
				if _, err := p.expect(RBracket); err != nil {
					return nil, err
				}
				x = &SliceEx{Obj: x, Lo: lo, Hi: hi, Line: t.Line}
			} else {
				p.skipNl()
				if _, err := p.expect(RBracket); err != nil {
					return nil, err
				}
				x = &IndexEx{Obj: x, Idx: lo, Line: t.Line}
			}
		case Dot:
			p.next()
			name, err := p.expect(TokIdent)
			if err != nil {
				return nil, err
			}
			x = &FieldEx{Obj: x, Name: name.Lit, Line: t.Line}
		case Question:
			p.next()
			x = &TryEx{X: x, Line: t.Line}
		default:
			return x, nil
		}
	}
}

func (p *Parser) parsePrimary() (Expr, error) {
	t := p.cur()
	switch t.Kind {
	case TokInt:
		p.next()
		v, err := strconv.ParseInt(t.Lit, 10, 64)
		if err != nil {
			return nil, p.errAt(t, "整数溢出: %s", t.Lit)
		}
		return &IntLit{Val: v, Line: t.Line}, nil
	case TokFloat:
		p.next()
		v, err := strconv.ParseFloat(t.Lit, 64)
		if err != nil {
			return nil, p.errAt(t, "非法浮点数: %s", t.Lit)
		}
		return &FloatLit{Val: v, Line: t.Line}, nil
	case TokStr:
		p.next()
		return &StrLit{Val: t.Lit, Line: t.Line}, nil
	case KwTrue, KwFalse:
		p.next()
		return &BoolLit{Val: t.Kind == KwTrue, Line: t.Line}, nil
	case TokIdent:
		p.next()
		if p.at(LBrace) && p.noStruct == 0 {
			return p.parseStructLit(t.Lit, t.Line)
		}
		return &Ident{Name: t.Lit, Line: t.Line}, nil
	case LParen:
		p.next()
		saved := p.noStruct // 括号内恢复 struct 字面量
		p.noStruct = 0
		x, err := p.parseExpr(0)
		p.noStruct = saved
		if err != nil {
			return nil, err
		}
		p.skipNl()
		if _, err := p.expect(RParen); err != nil {
			return nil, err
		}
		return x, nil
	case LBracket: // 数组字面量
		p.next()
		var items []Expr
		for {
			p.skipNl()
			if p.at(RBracket) {
				break
			}
			e, err := p.parseExpr(0)
			if err != nil {
				return nil, err
			}
			items = append(items, e)
			p.skipNl()
			if !p.accept(Comma) {
				break
			}
		}
		p.skipNl()
		if _, err := p.expect(RBracket); err != nil {
			return nil, err
		}
		return &ArrLit{Items: items, Line: t.Line}, nil
	case LBrace: // 映射字面量
		p.next()
		var items []MapItem
		for {
			p.skipNl()
			if p.accept(RBrace) {
				break
			}
			if p.at(EOF) {
				return nil, p.errAt(p.cur(), "映射字面量未闭合")
			}
			key, err := p.parseExpr(0)
			if err != nil {
				return nil, err
			}
			if _, err := p.expect(Colon); err != nil {
				return nil, err
			}
			val, err := p.parseExpr(0)
			if err != nil {
				return nil, err
			}
			items = append(items, MapItem{Key: key, Val: val})
			p.skipNl()
			p.accept(Comma)
		}
		return &MapLit{Items: items, Line: t.Line}, nil
	case KwFn: // 匿名函数
		p.next()
		fn, err := p.parseFuncRest(t.Line, nil, "")
		if err != nil {
			return nil, err
		}
		return &FnLit{TypeParams: fn.TypeParams, Params: fn.Params, Ret: fn.Ret, Body: fn.Body, Line: t.Line}, nil
	case KwMatch:
		return p.parseMatch()
	case EOF:
		return nil, p.errAt(t, "表达式意外结束")
	default:
		return nil, p.errAt(t, "意外的记号 %s", t.Kind)
	}
}

func (p *Parser) parseStructLit(name string, line int) (Expr, error) {
	p.next() // {
	var fields []FieldLit
	for {
		p.skipNl()
		if p.accept(RBrace) {
			break
		}
		if p.at(EOF) {
			return nil, p.errAt(p.cur(), "struct 字面量未闭合")
		}
		fname, err := p.expect(TokIdent)
		if err != nil {
			return nil, err
		}
		if _, err := p.expect(Colon); err != nil {
			return nil, err
		}
		val, err := p.parseExpr(0)
		if err != nil {
			return nil, err
		}
		fields = append(fields, FieldLit{Name: fname.Lit, Val: val})
		p.skipNl()
		p.accept(Comma)
	}
	return &StructLit{TypeName: name, Fields: fields, Line: line}, nil
}

func (p *Parser) parseMatch() (Expr, error) {
	t := p.next() // match
	p.noStruct++
	scrut, err := p.parseExpr(0)
	p.noStruct--
	if err != nil {
		return nil, err
	}
	if _, err := p.expect(LBrace); err != nil {
		return nil, err
	}
	var arms []Arm
	for {
		p.skipNl()
		if p.accept(RBrace) {
			break
		}
		if p.at(EOF) {
			return nil, p.errAt(p.cur(), "match 未闭合")
		}
		armLine := p.cur().Line
		pat, err := p.parsePattern()
		if err != nil {
			return nil, err
		}
		if _, err := p.expect(Arrow); err != nil {
			return nil, err
		}
		body, err := p.parseExpr(0)
		if err != nil {
			return nil, err
		}
		arms = append(arms, Arm{Pat: pat, Body: body, Line: armLine})
		if p.at(Newline) || p.at(RBrace) {
			continue
		}
		return nil, p.errAt(p.cur(), "match 分支之间需要换行")
	}
	return &MatchEx{Scrut: scrut, Arms: arms, Line: t.Line}, nil
}

func (p *Parser) parsePattern() (Pat, error) {
	t := p.cur()
	switch t.Kind {
	case TokUnderscore:
		p.next()
		return &WildPat{Line: t.Line}, nil
	case TokInt, TokFloat, TokStr, KwTrue, KwFalse:
		e, err := p.parsePrimary()
		if err != nil {
			return nil, err
		}
		return &LitPat{Val: e, Line: t.Line}, nil
	case TokIdent:
		p.next()
		if p.accept(LParen) {
			var subs []Pat
			for !p.at(RParen) {
				sp, err := p.parsePattern()
				if err != nil {
					return nil, err
				}
				subs = append(subs, sp)
				if !p.accept(Comma) {
					break
				}
			}
			if _, err := p.expect(RParen); err != nil {
				return nil, err
			}
			return &CasePat{Name: t.Lit, Subs: subs, Line: t.Line}, nil
		}
		// 大写开头按 case 引用，小写为绑定（spec §3.5）
		if unicode.IsUpper(rune(t.Lit[0])) {
			return &CasePat{Name: t.Lit, Line: t.Line}, nil
		}
		return &BindPat{Name: t.Lit, Line: t.Line}, nil
	default:
		return nil, p.errAt(t, "非法模式，得到 %s", t.Kind)
	}
}
