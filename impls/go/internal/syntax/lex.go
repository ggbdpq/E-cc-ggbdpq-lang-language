package syntax

import (
	"fmt"
	"strconv"
	"strings"
	"unicode"
)

// Kind 记号类别.
type Kind int

const (
	EOF Kind = iota
	Newline
	TokIdent
	TokInt
	TokFloat
	TokStr
	TokUnderscore

	KwLet
	KwVar
	KwFn
	KwReturn
	KwIf
	KwElse
	KwWhile
	KwFor
	KwIn
	KwMatch
	KwStruct
	KwEnum
	KwTrue
	KwFalse
	KwBreak
	KwContinue

	// 保留字：v0 使用即报"尚未启用".
	KwGo
	KwChan
	KwImport
	KwExtern
	KwInterface

	Plus
	Minus
	Star
	Slash
	Percent
	Assign // =
	Eq     // ==
	Neq    // !=
	Lt
	Le
	Gt
	Ge
	And // &&
	Or  // ||
	Not // !
	Amp // &
	BitOr
	Xor
	Shl
	Shr
	PlusAssign
	MinusAssign
	StarAssign
	SlashAssign
	Question
	Dot
	Dot2 // ..
	Comma
	Colon
	Arrow // =>
	LParen
	RParen
	LBrace
	RBrace
	LBracket
	RBracket
)

var kindName = map[Kind]string{
	EOF: "文件尾", Newline: "换行", TokIdent: "标识符",
	TokInt: "整数", TokFloat: "浮点数", TokStr: "字符串", TokUnderscore: "_",
	KwLet: "let", KwVar: "var", KwFn: "fn", KwReturn: "return",
	KwIf: "if", KwElse: "else", KwWhile: "while", KwFor: "for", KwIn: "in",
	KwMatch: "match", KwStruct: "struct", KwEnum: "enum",
	KwTrue: "true", KwFalse: "false", KwBreak: "break", KwContinue: "continue",
	KwGo: "go", KwChan: "chan", KwImport: "import", KwExtern: "extern", KwInterface: "interface",
	Plus: "+", Minus: "-", Star: "*", Slash: "/", Percent: "%",
	Assign: "=", Eq: "==", Neq: "!=", Lt: "<", Le: "<=", Gt: ">", Ge: ">=",
	And: "&&", Or: "||", Not: "!", Amp: "&",
	BitOr: "|", Xor: "^", Shl: "<<", Shr: ">>",
	PlusAssign: "+=", MinusAssign: "-=", StarAssign: "*=", SlashAssign: "/=",
	Question: "?",
	Dot:      ".", Dot2: "..", Comma: ",", Colon: ":", Arrow: "=>",
	LParen: "(", RParen: ")", LBrace: "{", RBrace: "}", LBracket: "[", RBracket: "]",
}

func (k Kind) String() string {
	if s, ok := kindName[k]; ok {
		return s
	}
	return fmt.Sprintf("Kind(%d)", int(k))
}

var keywords = map[string]Kind{
	"let": KwLet, "var": KwVar, "fn": KwFn, "return": KwReturn,
	"if": KwIf, "else": KwElse, "while": KwWhile, "for": KwFor, "in": KwIn,
	"match": KwMatch, "struct": KwStruct, "enum": KwEnum,
	"true": KwTrue, "false": KwFalse, "break": KwBreak, "continue": KwContinue,
	"go": KwGo, "chan": KwChan, "import": KwImport, "extern": KwExtern, "interface": KwInterface,
}

// Token 单个记号；字符串字面量的 Lit 为解码后的值.
type Token struct {
	Kind  Kind
	Lit   string
	Line  int
	Start int
	End   int
}

// isCont 该类记号之后换行视为续行（spec §2）.
func isCont(k Kind) bool {
	switch k {
	case Plus, Minus, Star, Slash, Percent,
		Assign, Eq, Neq, Lt, Le, Gt, Ge,
		And, Or, Comma, Colon, Arrow, Dot, Dot2,
		LParen, LBracket, LBrace:
		return true
	}
	return false
}

type lexError struct {
	msg     string
	line    int
	snippet string
}

func (e *lexError) Error() string {
	if e.snippet == "" {
		return fmt.Sprintf("第%d行: %s", e.line, e.msg)
	}
	return fmt.Sprintf("第%d行: %s\n  | %s", e.line, e.msg, e.snippet)
}

// lexErrAt 构造带当前行摘录的词法错误（输出末尾报错时摘录前一行）.
func (l *Lexer) lexErrAt(msg string) error {
	line := l.line
	if line > 1 && l.i >= len(l.runes) {
		line--
	}
	text := ""
	if line >= 1 {
		start, cur := 0, 1
		for i := 0; i < len(l.runes) && cur < line; i++ {
			if l.runes[i] == '\n' {
				cur++
				start = i + 1
			}
		}
		end := len(l.runes)
		for i := start; i < len(l.runes); i++ {
			if l.runes[i] == '\n' {
				end = i
				break
			}
		}
		text = strings.TrimSpace(string(l.runes[start:end]))
	}
	return &lexError{msg: msg, line: l.line, snippet: text}
}

type Lexer struct {
	runes []rune
	i     int
	line  int
	last  Token // 上一记号（含换行）
	toks  []Token
}

// Lex 把源码切成记号流，末尾追加 EOF。
// 换行即语句结束；续行只看上一记号（运算符/逗号/开括号等，见 isCont），
// 因此 match 分支、match 作实参等场景里换行都能存活。
func Lex(src string) ([]Token, error) {
	l := &Lexer{runes: []rune(src), line: 1}
	for l.i < len(l.runes) {
		start := l.i
		r := l.runes[l.i]
		switch {
		case r == ' ' || r == '\t' || r == '\r':
			l.i++
		case r == '\n':
			l.i++
			l.line++
			l.emitNewline()
		case r == '/' && l.peek(1) == '/':
			for l.i < len(l.runes) && l.runes[l.i] != '\n' {
				l.i++
			}
		case unicode.IsDigit(r):
			if err := l.number(start); err != nil {
				return nil, err
			}
		case r == '"':
			if err := l.stringLit(start); err != nil {
				return nil, err
			}
		case isIdentStart(r):
			l.ident(start)
		default:
			if err := l.op(start); err != nil {
				return nil, err
			}
		}
	}
	l.toks = append(l.toks, Token{Kind: EOF, Line: l.line, Start: l.i, End: l.i})
	return l.toks, nil
}

func isIdentStart(r rune) bool { return r == '_' || unicode.IsLetter(r) }

func isHexDigit(r rune) bool {
	return (r >= '0' && r <= '9') || (r >= 'a' && r <= 'f') || (r >= 'A' && r <= 'F')
}

func (l *Lexer) peek(n int) rune {
	if l.i+n < len(l.runes) {
		return l.runes[l.i+n]
	}
	return 0
}

func (l *Lexer) emitAt(k Kind, lit string, start int) {
	t := Token{Kind: k, Lit: lit, Line: l.line, Start: start, End: l.i}
	l.toks = append(l.toks, t)
	l.last = t
}

// emitNewline 落实续行规则：续行记号之后、行首的换行不产记号.
func (l *Lexer) emitNewline() {
	if len(l.toks) == 0 || isCont(l.last.Kind) {
		return
	}
	if l.last.Kind == Newline {
		return
	}
	l.toks = append(l.toks, Token{Kind: Newline, Lit: "\n", Line: l.line - 1, Start: l.i - 1, End: l.i})
	l.last = Token{Kind: Newline, Lit: "\n", Line: l.line, Start: l.i, End: l.i}
}

func (l *Lexer) number(start int) error {

	// 十六进制 0x/0X 前缀（Q28）：记号文本统一为十进制，fmt 无损输出
	if l.runes[l.i] == '0' && l.i+1 < len(l.runes) && (l.runes[l.i+1] == 'x' || l.runes[l.i+1] == 'X') {
		l.i += 2
		digits := l.i
		for l.i < len(l.runes) && isHexDigit(l.runes[l.i]) {
			l.i++
		}
		if l.i == digits {
			return l.lexErrAt("十六进制字面量缺少数字（如 0xFF）")
		}
		text := string(l.runes[start:l.i])
		val, err := strconv.ParseInt(text, 0, 64)
		if err != nil {
			return l.lexErrAt("十六进制字面量超出 int 范围: " + text)
		}
		l.emitAt(TokInt, strconv.FormatInt(val, 10), start)
		return nil
	}

	for l.i < len(l.runes) && unicode.IsDigit(l.runes[l.i]) {
		l.i++
	}
	isFloat := false
	// "1..3" 里第一个 '.' 不属于数字
	if l.i < len(l.runes) && l.runes[l.i] == '.' && unicode.IsDigit(l.peek(1)) {
		isFloat = true
		l.i++
		for l.i < len(l.runes) && unicode.IsDigit(l.runes[l.i]) {
			l.i++
		}
	}
	lit := string(l.runes[start:l.i])
	if isFloat {
		l.emitAt(TokFloat, lit, start)
	} else {
		l.emitAt(TokInt, lit, start)
	}
	return nil
}

func (l *Lexer) stringLit(start int) error {
	l.i++
	var b strings.Builder
	for {
		if l.i >= len(l.runes) || l.runes[l.i] == '\n' {
			return l.lexErrAt("字符串未闭合")
		}
		r := l.runes[l.i]
		if r == '"' {
			l.i++
			break
		}
		if r == '\\' {
			l.i++
			if l.i >= len(l.runes) {
				return l.lexErrAt("字符串未闭合")
			}
			switch l.runes[l.i] {
			case 'n':
				b.WriteByte('\n')
			case 't':
				b.WriteByte('\t')
			case 'r':
				b.WriteByte('\r')
			case '"':
				b.WriteByte('"')
			case '\\':
				b.WriteByte('\\')
			default:
				return l.lexErrAt(fmt.Sprintf("不支持的转义 \\%c", l.runes[l.i]))
			}
			l.i++
			continue
		}
		b.WriteRune(r)
		l.i++
	}
	l.emitAt(TokStr, b.String(), start)
	return nil
}

func (l *Lexer) ident(start int) {
	for l.i < len(l.runes) && (isIdentStart(l.runes[l.i]) || unicode.IsDigit(l.runes[l.i])) {
		l.i++
	}
	name := string(l.runes[start:l.i])
	// 单独的下划线是通配符记号
	if name == "_" {
		l.emitAt(TokUnderscore, name, start)
		return
	}
	if k, ok := keywords[name]; ok {
		l.emitAt(k, name, start)
		return
	}
	l.emitAt(TokIdent, name, start)
}

func (l *Lexer) op(start int) error {
	r := l.runes[l.i]
	two := func(next rune, k Kind, lit string) bool {
		if l.peek(1) == next {
			l.i += 2
			l.emitAt(k, lit, start)
			return true
		}
		return false
	}
	switch r {
	case '&':
		if two('&', And, "&&") {
			return nil
		}
		l.i++
		l.emitAt(Amp, "&", start)
	case '|':
		if two('|', Or, "||") {
			return nil
		}
		l.i++
		l.emitAt(BitOr, "|", start)
	case '^':
		l.i++
		l.emitAt(Xor, "^", start)
	case '+':
		if two('=', PlusAssign, "+=") {
			return nil
		}
		l.i++
		l.emitAt(Plus, "+", start)
	case '-':
		if two('=', MinusAssign, "-=") {
			return nil
		}
		l.i++
		l.emitAt(Minus, "-", start)
	case '*':
		if two('=', StarAssign, "*=") {
			return nil
		}
		l.i++
		l.emitAt(Star, "*", start)
	case '/':
		if two('=', SlashAssign, "/=") {
			return nil
		}
		l.i++
		l.emitAt(Slash, "/", start)
	case '=':
		if two('=', Eq, "==") {
			return nil
		}
		if two('>', Arrow, "=>") {
			return nil
		}
		l.i++
		l.emitAt(Assign, "=", start)
	case '!':
		if two('=', Neq, "!=") {
			return nil
		}
		l.i++
		l.emitAt(Not, "!", start)
	case '<':
		if two('<', Shl, "<<") {
			return nil
		}
		if two('=', Le, "<=") {
			return nil
		}
		l.i++
		l.emitAt(Lt, "<", start)
	case '>':
		if two('>', Shr, ">>") {
			return nil
		}
		if two('=', Ge, ">=") {
			return nil
		}
		l.i++
		l.emitAt(Gt, ">", start)
	case '.':
		if two('.', Dot2, "..") {
			return nil
		}
		l.i++
		l.emitAt(Dot, ".", start)
	case '%', '(', ')', '{', '}', '[', ']', ',', ':', '?':
		l.i++
		var k Kind
		switch r {
		case '%':
			k = Percent
		case '(':
			k = LParen
		case ')':
			k = RParen
		case '{':
			k = LBrace
		case '}':
			k = RBrace
		case '[':
			k = LBracket
		case ']':
			k = RBracket
		case ',':
			k = Comma
		case ':':
			k = Colon
		case '?':
			k = Question
		}
		l.emitAt(k, string(r), start)
	default:
		return l.lexErrAt(fmt.Sprintf("非法字符 %c", r))
	}
	return nil
}
