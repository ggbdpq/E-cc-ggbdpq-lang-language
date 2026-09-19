package hir

// Package hir 构建 Typed HIR（v0.3 Semantic Core 第三层）：
// AST → 已解析、已标注类型的语义树。每个节点携带
// NodeID（编译内稳定）、源码行、ResolvedSymbol、ResolvedType。
//
// HIR 消除的语法糖：裸构造器引用 → Ctor(0 参)、obj.m(args) → MethodCall、
// go <调用> → Spawn。? 以 Try 节点表达（checker 已约束位置）。
// AST 负责保留用户写法；HIR 表达程序语义；后端不再回读 AST。

import (
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
	"lang-go/internal/types"
)

// NodeID 编译内稳定节点编号（先序递增）
type NodeID int

// Node 元数据嵌入块
type Node struct {
	ID   NodeID
	Line int
	Sym  *semantic.Symbol
	Typ  *types.Type
}

// —— 程序 ——

type Prog struct {
	Structs []*StructDecl
	Enums   []*EnumDecl
	Globals []*Let
	Fns     []*Fn // 含方法（Recv 非 nil）与 extern
	Main    *Fn   // 顶层 main（可空）
}

// Fn 函数/方法/extern 声明
type Fn struct {
	Node
	Name       string
	Sym        *semantic.Symbol
	Recv       *Param // 值接收者（方法）
	TypeParams []string
	Params     []Param
	Ret        *types.Type
	Body       []Stmt
	Extern     bool
	ExternHost string
}

type Param struct {
	Name string
	Typ  *types.Type
}

type StructDecl struct {
	Node
	Name       string
	TypeParams []string
	Fields     []FieldDecl
}

type FieldDecl struct {
	Name string
	Typ  *types.Type
}

type EnumDecl struct {
	Node
	Name       string
	TypeParams []string
	Cases      []Case
}

type Case struct {
	Sym      *semantic.Symbol
	Name     string
	Payloads []*types.Type
}

// —— 语句 ——

type Stmt interface{ isStmt() }

// Expr 携带元数据访问器
type Expr interface {
	isExpr()
	Meta() *Node
}

// Typ 表达式语义类型
func Typ(e Expr) *types.Type {
	if e == nil || e.Meta() == nil || e.Meta().Typ == nil {
		return types.Unknown
	}
	return e.Meta().Typ
}

// Let let/var 绑定
type Let struct {
	Node
	Name    string
	Mutable bool
	Init    Expr
}

// Assign 赋值/复合赋值；Target 为写位置（LocalRef / Field / Index）
type Assign struct {
	Node
	Target Expr
	Op     syntax.Kind // syntax.Assign 或复合运算符
	RHS    Expr
}

// Eval 表达式语句（求值后丢弃）
type Eval struct {
	Node
	X Expr
}

// Return return；Val 为 nil 表示裸 return
type Return struct {
	Node
	Val Expr
}

type If struct {
	Node
	Cond Expr
	Then []Stmt
	Else []Stmt
}

type While struct {
	Node
	Cond Expr
	Body []Stmt
}

// ForIn for x in iter；VarType 为迭代元素类型
type ForIn struct {
	Node
	Var     string
	VarType *types.Type
	Iter    Expr
	Body    []Stmt
}

type Break struct{ Node }
type Continue struct{ Node }

// Spawn go <fn 调用>（目标已由 resolver 约束为顶层 fn）
type Spawn struct {
	Node
	Fn   *semantic.Symbol
	Args []Expr
}

// FnStmt 函数体内嵌套 fn 声明
type FnStmt struct {
	Node
	Fn *Fn
}

// —— 表达式 ——

type LitKind uint8

const (
	LitInt LitKind = iota
	LitFloat
	LitStr
	LitBool
)

type Lit struct {
	Node
	Kind LitKind
	I64  int64
	F64  float64
	S    string
	B    bool
}

// LocalRef 局部/全局绑定读取（写目标复用此节点）
type LocalRef struct {
	Node
	Name string
}

// FnRef fn 引用（函数值）
type FnRef struct {
	Node
	Name string
}

// BuiltinRef 内建函数值引用
type BuiltinRef struct {
	Node
	Name string
}

// Ctor 构造器调用（裸零参引用即空参调用）
type Ctor struct {
	Node
	CtorSym *semantic.Symbol
	Args    []Expr
}

// Call 用户 fn（Fn 非 nil）/ 内建（Builtin 非空）/ 表达式 callee（Callee 非 nil）
type Call struct {
	Node
	Fn      *semantic.Symbol
	Builtin string
	Callee  Expr
	Args    []Expr
}

// MethodCall 方法调用（脱糖自 obj.m(args)）或字段函数值调用（Method 为 nil）
type MethodCall struct {
	Node
	Recv   Expr
	Method *semantic.Symbol
	Field  string
	Args   []Expr
}

// Unary 一元运算（- ! &）
type Unary struct {
	Node
	Op syntax.Kind
	X  Expr
}

// Binary 二元运算（不含短路）
type Binary struct {
	Node
	Op syntax.Kind
	L  Expr
	R  Expr
}

// AndThen a && b（短路）
type AndThen struct {
	Node
	L, R Expr
}

// OrElse a || b（短路）
type OrElse struct {
	Node
	L, R Expr
}

type Range struct {
	Node
	Lo, Hi Expr
}

type Index struct {
	Node
	Obj Expr
	Idx Expr
}

type Slice struct {
	Node
	Obj    Expr
	Lo, Hi Expr
}

// Field 字段读取（/写目标）
type Field struct {
	Node
	Obj  Expr
	Name string
}

// Try e? —— Result 解包与传播（checker 已约束所在函数返回 Result）
type Try struct {
	Node
	X Expr
}

type StructLit struct {
	Node
	Fields []FieldValue
}

type FieldValue struct {
	Name string
	Val  Expr
}

type MapLit struct {
	Node
	Keys []Expr
	Vals []Expr
}

type SliceLit struct {
	Node
	Elems []Expr
}

// Match match 表达式；Kind 为 checker 分类结论
type Match struct {
	Node
	Scrut Expr
	Kind  types.MatchKind
	Arms  []Arm
}

type Arm struct {
	Line int
	Pat  Pat
	Body Expr
}

type Pat interface{ isPat() }

type WildPat struct{ Line int }

type BindPat struct {
	Line int
	Name string
	Typ  *types.Type
}

type LitPat struct {
	Line int
	Val  Expr
}

type CtorPat struct {
	Line int
	Ctor *semantic.Symbol
	Subs []Pat
}

// FnLit 匿名函数
type FnLit struct {
	Node
	Params []Param
	Ret    *types.Type
	Body   []Stmt
}

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
func (*FnStmt) isStmt()   {}

func (*Lit) isExpr()        {}
func (*LocalRef) isExpr()   {}
func (*FnRef) isExpr()      {}
func (*BuiltinRef) isExpr() {}
func (*Ctor) isExpr()       {}
func (*Call) isExpr()       {}
func (*MethodCall) isExpr() {}
func (*Unary) isExpr()      {}
func (*Binary) isExpr()     {}
func (*AndThen) isExpr()    {}
func (*OrElse) isExpr()     {}
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

// —— Expr 元数据访问器（机械实现） ——

func (n *Lit) Meta() *Node        { return &n.Node }
func (n *LocalRef) Meta() *Node   { return &n.Node }
func (n *FnRef) Meta() *Node      { return &n.Node }
func (n *BuiltinRef) Meta() *Node { return &n.Node }
func (n *Ctor) Meta() *Node       { return &n.Node }
func (n *Call) Meta() *Node       { return &n.Node }
func (n *MethodCall) Meta() *Node { return &n.Node }
func (n *Unary) Meta() *Node      { return &n.Node }
func (n *Binary) Meta() *Node     { return &n.Node }
func (n *AndThen) Meta() *Node    { return &n.Node }
func (n *OrElse) Meta() *Node     { return &n.Node }
func (n *Range) Meta() *Node      { return &n.Node }
func (n *Index) Meta() *Node      { return &n.Node }
func (n *Slice) Meta() *Node      { return &n.Node }
func (n *Field) Meta() *Node      { return &n.Node }
func (n *Try) Meta() *Node        { return &n.Node }
func (n *StructLit) Meta() *Node  { return &n.Node }
func (n *MapLit) Meta() *Node     { return &n.Node }
func (n *SliceLit) Meta() *Node   { return &n.Node }
func (n *Match) Meta() *Node      { return &n.Node }
func (n *FnLit) Meta() *Node      { return &n.Node }
