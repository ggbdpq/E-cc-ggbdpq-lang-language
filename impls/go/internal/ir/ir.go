package ir

// Package ir 定义 Core IR（v0.3 Semantic Core 第四层）：
// HIR 降级而成的最小后端友好节点集。三个执行后端（interpreter/→Go/→JS）
// 优先消费 IR；后端只做目标平台映射，不再做名字解析、类型推断或语义校验
// ——这些已由 semantic/types 完成，结论固化在节点上。
//
// v0.3 不做 SSA：IR 为带类型的树形中间表示，与 HIR 的差异是
// 「去元数据 + 局部槽位化 + 迭代协议显式化」。

import (
	"lang-go/internal/semantic"
	"lang-go/internal/types"
)

// Prog 后端可执行/可转译的程序.
type Prog struct {
	Structs []*StructDecl
	Enums   []*EnumDecl
	Globals []*Let
	Fns     []*Fn
	Main    *Fn // 可空
}

type StructDecl struct {
	Name       string
	TypeParams []string
	Fields     []FieldDecl
}

type FieldDecl struct {
	Name string
	Typ  *types.Type
}

type EnumDecl struct {
	Name       string
	TypeParams []string
	Cases      []Case
}

type Case struct {
	Name     string
	Payloads []*types.Type
}

// Local 具名局部槽位（参数/绑定/for 变量）.
type Local struct {
	Name    string
	Mutable bool
	Typ     *types.Type
	Sym     *semantic.Symbol
}

type Fn struct {
	Name       string
	Sym        *semantic.Symbol
	Recv       *Local
	TypeParams []string
	Params     []*Local
	Ret        *types.Type
	Locals     []*Local // 函数体声明的局部（参数/接收者之外的），按声明序
	Body       []Stmt
	Extern     bool
	ExternHost string
}

// —— 语句 ——

type Stmt interface{ isStmt() }

type Let struct {
	Local *Local
	Init  Expr
}

// Assign 赋值/复合赋值；Target 为读位节点复用（LocalGet/Field/Index）.
type Assign struct {
	Target Expr
	Op     Op
	Val    Expr
}

// Op 运算符（宿主无关的字符串形态，来自 syntax.Kind 归一）.
type Op string

const (
	OpAssign  Op = "="
	OpPlus    Op = "+"
	OpMinus   Op = "-"
	OpStar    Op = "*"
	OpSlash   Op = "/"
	OpPercent Op = "%"
	OpPlusA   Op = "+="
	OpMinusA  Op = "-="
	OpStarA   Op = "*="
	OpSlashA  Op = "/="
	OpEq      Op = "=="
	OpNeq     Op = "!="
	OpLt      Op = "<"
	OpLe      Op = "<="
	OpGt      Op = ">"
	OpGe      Op = ">="
	OpAnd     Op = "&"
	OpBitOr   Op = "|"
	OpXor     Op = "^"
	OpShl     Op = "<<"
	OpShr     Op = ">>"
	OpAndAnd  Op = "&&"
	OpOrOr    Op = "||"
	OpNeg     Op = "-" // 一元
	OpNot     Op = "!"
	OpRef     Op = "&"
)

type Eval struct{ X Expr }

type Return struct{ Val Expr } // Val 可空

type If struct {
	Cond Expr
	Then []Stmt
	Else []Stmt
}

type While struct {
	Cond Expr
	Body []Stmt
}

// IterKind for-in 迭代协议（HIR 降级时按被迭代类型归类）.
type IterKind uint8

const (
	IterUnknown IterKind = iota
	IterSlice
	IterMap // 迭代键
	IterString
	IterRange
)

type ForIn struct {
	Var  *Local
	Kind IterKind
	Iter Expr
	Body []Stmt
}

type Break struct{}
type Continue struct{}

// Spawn go 语句（目标为顶层 fn）.
type Spawn struct {
	Fn   *semantic.Symbol
	Args []Expr
}

// FnDef 嵌套 fn 声明.
type FnDef struct{ Fn *Fn }

func (*Let) isStmt()      {}
func (*Assign) isStmt()   {}
func (*Eval) isStmt()     {}
func (*Return) isStmt()   {}
func (*If) isStmt()       {}
func (*While) isStmt()    {}
func (*ForIn) isStmt()    {}
func (*Break) isStmt()    {}
func (*Continue) isStmt() {}
func (*Spawn) isStmt()    {}
func (*FnDef) isStmt()    {}

// —— 表达式 ——

type Expr interface {
	isExpr()
	Typ() *types.Type
	typPtr() *types.Type
}

type ConstKind uint8

const (
	KInt ConstKind = iota
	KFloat
	KStr
	KBool
)

type Const struct {
	Kind ConstKind
	I64  int64
	F64  float64
	S    string
	B    bool
	typ  types.Type
}

type LocalGet struct {
	Local *Local
	typ   types.Type
}

// FnGet fn 值引用.
type FnGet struct {
	Name string
	Sym  *semantic.Symbol
	typ  types.Type
}

// BuiltinGet 内建函数值引用.
type BuiltinGet struct {
	Name string
	typ  types.Type
}

// Ctor 构造器调用（零参引用即空参）.
type Ctor struct {
	Sym  *semantic.Symbol
	Args []Expr
	typ  types.Type
}

// Call 三形态：用户 fn（Fn）/内建（Builtin）/方法（Method+Recv）/函数值（Callee）.
type Call struct {
	Fn      *semantic.Symbol
	Builtin string
	Method  *semantic.Symbol // 方法调用：Recv 为接收者
	Recv    Expr
	Callee  Expr
	Args    []Expr
	typ     types.Type
}

type Unary struct {
	Op  Op
	X   Expr
	typ types.Type
}

type Binary struct {
	Op   Op
	L, R Expr
	typ  types.Type
}

type Range struct {
	Lo, Hi Expr
	typ    types.Type
}

type Index struct {
	Obj, Idx Expr
	typ      types.Type
}

type Slice struct {
	Obj    Expr
	Lo, Hi Expr
	typ    types.Type
}

type Field struct {
	Obj  Expr
	Name string
	typ  types.Type
}

// Try ? 传播（checker 已约束函数上下文）.
type Try struct {
	X   Expr
	typ types.Type
}

type StructLit struct {
	Struct *semantic.Symbol
	Fields []FieldValue
	typ    types.Type
}

type FieldValue struct {
	Name string
	Val  Expr
}

type MapLit struct {
	Keys, Vals []Expr
	typ        types.Type
}

type SliceLit struct {
	Elems []Expr
	typ   types.Type
}

// MatchKind match 分类（checker 结论，后端直接消费）.
type MatchKind uint8

const (
	MatchLiteral MatchKind = iota
	MatchEnum
	MatchResult
	MatchOption
)

type Match struct {
	Scrut Expr
	Kind  MatchKind
	Arms  []Arm
	typ   types.Type
}

type Arm struct {
	Pat  Pat
	Body Expr
}

type Pat interface{ isPat() }

type WildPat struct{}

type BindPat struct {
	Name  string
	Typ   *types.Type
	Local *Local // 臂绑定的局部槽位（lowering 建立并注册进作用域）
}

type LitPat struct {
	Val Expr
}

type CtorPat struct {
	Sym  *semantic.Symbol
	Subs []Pat
}

type FnLit struct {
	Params []*Local
	Ret    *types.Type
	Locals []*Local
	Body   []Stmt
	typ    types.Type
}

func (*Const) isExpr()      {}
func (*LocalGet) isExpr()   {}
func (*FnGet) isExpr()      {}
func (*BuiltinGet) isExpr() {}
func (*Ctor) isExpr()       {}
func (*Call) isExpr()       {}
func (*Unary) isExpr()      {}
func (*Binary) isExpr()     {}
func (*Range) isExpr()      {}
func (*Index) isExpr()      {}
func (*Slice) isExpr()      {}
func (*Field) isExpr()      {}
func (*Try) isExpr()        {}
func (*StructLit) isExpr()  {}
func (*MapLit) isExpr()     {}
func (*SliceLit) isExpr()   {}
func (*Match) isExpr()      {}
func (*FnLit) isExpr()      {}

func (*WildPat) isPat() {}
func (*BindPat) isPat() {}
func (*LitPat) isPat()  {}
func (*CtorPat) isPat() {}

func (n *Const) Typ() *types.Type      { return &n.typ }
func (n *LocalGet) Typ() *types.Type   { return &n.typ }
func (n *FnGet) Typ() *types.Type      { return &n.typ }
func (n *BuiltinGet) Typ() *types.Type { return &n.typ }
func (n *Ctor) Typ() *types.Type       { return &n.typ }
func (n *Call) Typ() *types.Type       { return &n.typ }
func (n *Unary) Typ() *types.Type      { return &n.typ }
func (n *Binary) Typ() *types.Type     { return &n.typ }
func (n *Range) Typ() *types.Type      { return &n.typ }
func (n *Index) Typ() *types.Type      { return &n.typ }
func (n *Slice) Typ() *types.Type      { return &n.typ }
func (n *Field) Typ() *types.Type      { return &n.typ }
func (n *Try) Typ() *types.Type        { return &n.typ }
func (n *StructLit) Typ() *types.Type  { return &n.typ }
func (n *MapLit) Typ() *types.Type     { return &n.typ }
func (n *SliceLit) Typ() *types.Type   { return &n.typ }
func (n *Match) Typ() *types.Type      { return &n.typ }
func (n *FnLit) Typ() *types.Type      { return &n.typ }

func (n *Const) typPtr() *types.Type      { return &n.typ }
func (n *LocalGet) typPtr() *types.Type   { return &n.typ }
func (n *FnGet) typPtr() *types.Type      { return &n.typ }
func (n *BuiltinGet) typPtr() *types.Type { return &n.typ }
func (n *Ctor) typPtr() *types.Type       { return &n.typ }
func (n *Call) typPtr() *types.Type       { return &n.typ }
func (n *Unary) typPtr() *types.Type      { return &n.typ }
func (n *Binary) typPtr() *types.Type     { return &n.typ }
func (n *Range) typPtr() *types.Type      { return &n.typ }
func (n *Index) typPtr() *types.Type      { return &n.typ }
func (n *Slice) typPtr() *types.Type      { return &n.typ }
func (n *Field) typPtr() *types.Type      { return &n.typ }
func (n *Try) typPtr() *types.Type        { return &n.typ }
func (n *StructLit) typPtr() *types.Type  { return &n.typ }
func (n *MapLit) typPtr() *types.Type     { return &n.typ }
func (n *SliceLit) typPtr() *types.Type   { return &n.typ }
func (n *Match) typPtr() *types.Type      { return &n.typ }
func (n *FnLit) typPtr() *types.Type      { return &n.typ }
