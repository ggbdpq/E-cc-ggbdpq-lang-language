package syntax

// Node 语法树节点统一带行号.
type Node interface{ Pos() int }

// Spanned 携带源码区间的节点（v0.4 Agent Contract 用；Span 与 Line 同源于解析器）.
type Spanned interface {
	NodeSpan() *Span
}

// Position 源码位置：Offset 为 0 基 rune 偏移，Line/Col 为 1 基.
type Position struct {
	Offset int
	Line   int
	Col    int
}

// Span 源码区间 [Start, End).
type Span struct {
	File  string
	Start Position
	End   Position
}

type Expr interface {
	Node
	isExpr()
}
type Stmt interface {
	Node
	isStmt()
}
type Pat interface {
	Node
	isPat()
}
type TypeEx interface{ isType() }

// —— 表达式 ——

type IntLit struct {
	Val  int64
	Line int
	Sp   Span
}

type FloatLit struct {
	Val  float64
	Line int
	Sp   Span
}

type StrLit struct {
	Val  string
	Line int
	Sp   Span
}

type BoolLit struct {
	Val  bool
	Line int
	Sp   Span
}

type Ident struct {
	Name string
	Line int
	Sp   Span
}

type Unary struct {
	Op   Kind
	X    Expr
	Line int
	Sp   Span
}

type Binary struct {
	Op   Kind
	L    Expr
	R    Expr
	Line int
	Sp   Span
}

type RangeEx struct {
	Lo, Hi Expr
	Line   int
	Sp     Span
}

type CallEx struct {
	Callee Expr
	Args   []Expr
	Line   int
	Sp     Span
}

type IndexEx struct {
	Obj  Expr
	Idx  Expr
	Line int
	Sp   Span
}

// SliceEx a[lo:hi]，lo/hi 必填.
type SliceEx struct {
	Obj  Expr
	Lo   Expr
	Hi   Expr
	Line int
	Sp   Span
}

type FieldEx struct {
	Obj  Expr
	Name string
	Line int
	Sp   Span
}

// TryEx e? —— Result 传播.
type TryEx struct {
	X    Expr
	Line int
	Sp   Span
}

type FieldLit struct {
	Name string
	Val  Expr
}

type StructLit struct {
	TypeName string
	Fields   []FieldLit
	Line     int
	Sp       Span
}

type MapItem struct {
	Key Expr
	Val Expr
}

type MapLit struct {
	Items []MapItem
	Line  int
	Sp    Span
}

type ArrLit struct {
	Items []Expr
	Line  int
	Sp    Span
}

type Arm struct {
	Pat  Pat
	Body Expr
	Line int
	Sp   Span
}

type MatchEx struct {
	Scrut Expr
	Arms  []Arm
	Line  int
	Sp    Span
}

// FnLit 匿名函数表达式.
type FnLit struct {
	TypeParams []string
	Params     []Param
	Ret        TypeEx
	Body       []Stmt
	Line       int
	Sp         Span
}

// —— 模式 ——

type WildPat struct {
	Line int
	Sp   Span
}

type BindPat struct {
	Name string
	Line int
	Sp   Span
}

// LitPat 字面量模式（Int/Float/Str/Bool）.
type LitPat struct {
	Val  Expr
	Line int
	Sp   Span
}

// CasePat 枚举构造模式，Subs 为载荷子模式.
type CasePat struct {
	Name string
	Subs []Pat
	Line int
	Sp   Span
}

// —— 语句 ——

type Param struct {
	Name string
	Typ  TypeEx
}

type LetStmt struct {
	Name    string
	Mutable bool
	Typ     TypeEx
	Init    Expr
	Line    int
	Sp      Span
}

type AssignStmt struct {
	Target Expr
	Val    Expr
	Op     Kind // Assign（=）或 PlusAssign/MinusAssign/StarAssign/SlashAssign
	Line   int
	Sp     Span
}

type ExprStmt struct {
	X    Expr
	Line int
	Sp   Span
}

type ReturnStmt struct {
	Val  Expr // 可空：bare return
	Line int
	Sp   Span
}

type IfStmt struct {
	Cond Expr
	Then []Stmt
	Else []Stmt // 可空；else if 展开为单元素块
	Line int
	Sp   Span
}

type WhileStmt struct {
	Cond Expr
	Body []Stmt
	Line int
	Sp   Span
}

type ForInStmt struct {
	Var  string
	Iter Expr
	Body []Stmt
	Line int
	Sp   Span
}

type BreakStmt struct {
	Line int
	Sp   Span
}

type ContinueStmt struct {
	Line int
	Sp   Span
}

// —— 声明（顶层与嵌套共用） ——

type FnDecl struct {
	Name       string
	Recv       *Param // 值接收者，v0
	TypeParams []string
	Params     []Param
	Ret        TypeEx
	Body       []Stmt
	Line       int
	Sp         Span
}

type StructDecl struct {
	Name       string
	TypeParams []string
	Fields     []Param
	Line       int
	Sp         Span
}

type EnumCase struct {
	Name     string
	Payloads []TypeEx
	Line     int
	Sp       Span
}

type EnumDecl struct {
	Name       string
	TypeParams []string
	Cases      []EnumCase
	Line       int
	Sp         Span
}

// GoStmt go <调用>：后台任务，主函数结束前 join（decisions #57）.
type GoStmt struct {
	Call *CallEx
	Line int
	Sp   Span
}

// ImportStmt import "./md5"（装载器在合并前展开，解释器不会执行到）.
type ImportStmt struct {
	Path string
	Line int
	Sp   Span
}

// ExternDecl extern "go" { fn 签名 } 声明块：宿主函数手写签名（decisions #17/#35）.
type ExternDecl struct {
	Host string
	Fns  []*FnDecl // 无函数体
	Line int
	Sp   Span
}

// —— 类型注解（v0 解释器不检查，供 M3 静态检查器使用） ——

type NamedType struct {
	Name string
	Args []TypeEx
}

type ArrType struct{ Elem TypeEx }

type MapType struct{ Key, Val TypeEx }

type PtrType struct{ Elem TypeEx }

// FnType 函数类型：fn(int, string): bool。v0 解释器不检查，→Go 转译需要它生成签名.
type FnType struct {
	Params []TypeEx
	Ret    TypeEx
}

// —— 接口实现（机械部分） ——

func (n *IntLit) Pos() int       { return n.Line }
func (n *IntLit) isExpr()        {}
func (n *FloatLit) Pos() int     { return n.Line }
func (n *FloatLit) isExpr()      {}
func (n *StrLit) Pos() int       { return n.Line }
func (n *StrLit) isExpr()        {}
func (n *BoolLit) Pos() int      { return n.Line }
func (n *BoolLit) isExpr()       {}
func (n *Ident) Pos() int        { return n.Line }
func (n *Ident) isExpr()         {}
func (n *Unary) Pos() int        { return n.Line }
func (n *Unary) isExpr()         {}
func (n *Binary) Pos() int       { return n.Line }
func (n *Binary) isExpr()        {}
func (n *RangeEx) Pos() int      { return n.Line }
func (n *RangeEx) isExpr()       {}
func (n *CallEx) Pos() int       { return n.Line }
func (n *CallEx) isExpr()        {}
func (n *IndexEx) Pos() int      { return n.Line }
func (n *IndexEx) isExpr()       {}
func (n *SliceEx) Pos() int      { return n.Line }
func (n *SliceEx) isExpr()       {}
func (n *FieldEx) Pos() int      { return n.Line }
func (n *FieldEx) isExpr()       {}
func (n *TryEx) Pos() int        { return n.Line }
func (n *TryEx) isExpr()         {}
func (n *StructLit) Pos() int    { return n.Line }
func (n *StructLit) isExpr()     {}
func (n *MapLit) Pos() int       { return n.Line }
func (n *MapLit) isExpr()        {}
func (n *ArrLit) Pos() int       { return n.Line }
func (n *ArrLit) isExpr()        {}
func (n *MatchEx) Pos() int      { return n.Line }
func (n *MatchEx) isExpr()       {}
func (n *FnLit) Pos() int        { return n.Line }
func (n *FnLit) isExpr()         {}
func (n *WildPat) Pos() int      { return n.Line }
func (n *WildPat) isPat()        {}
func (n *BindPat) Pos() int      { return n.Line }
func (n *BindPat) isPat()        {}
func (n *LitPat) Pos() int       { return n.Line }
func (n *LitPat) isPat()         {}
func (n *CasePat) Pos() int      { return n.Line }
func (n *CasePat) isPat()        {}
func (n *LetStmt) Pos() int      { return n.Line }
func (n *LetStmt) isStmt()       {}
func (n *AssignStmt) Pos() int   { return n.Line }
func (n *AssignStmt) isStmt()    {}
func (n *ExprStmt) Pos() int     { return n.Line }
func (n *ExprStmt) isStmt()      {}
func (n *ReturnStmt) Pos() int   { return n.Line }
func (n *ReturnStmt) isStmt()    {}
func (n *IfStmt) Pos() int       { return n.Line }
func (n *IfStmt) isStmt()        {}
func (n *WhileStmt) Pos() int    { return n.Line }
func (n *WhileStmt) isStmt()     {}
func (n *ForInStmt) Pos() int    { return n.Line }
func (n *ForInStmt) isStmt()     {}
func (n *BreakStmt) Pos() int    { return n.Line }
func (n *BreakStmt) isStmt()     {}
func (n *ContinueStmt) Pos() int { return n.Line }
func (n *ContinueStmt) isStmt()  {}
func (n *FnDecl) Pos() int       { return n.Line }
func (n *FnDecl) isStmt()        {}
func (n *StructDecl) Pos() int   { return n.Line }
func (n *StructDecl) isStmt()    {}
func (n *EnumDecl) Pos() int     { return n.Line }
func (n *EnumDecl) isStmt()      {}
func (n *ImportStmt) Pos() int   { return n.Line }
func (n *ImportStmt) isStmt()    {}
func (n *GoStmt) Pos() int       { return n.Line }
func (n *GoStmt) isStmt()        {}
func (n *ExternDecl) Pos() int   { return n.Line }
func (n *ExternDecl) isStmt()    {}
func (n *NamedType) isType()     {}
func (n *ArrType) isType()       {}
func (n *MapType) isType()       {}
func (n *PtrType) isType()       {}
func (n *FnType) isType()        {}

// —— Spanned 接口实现（机械） ——

func (n *Arm) NodeSpan() *Span          { return &n.Sp }
func (n *ArrLit) NodeSpan() *Span       { return &n.Sp }
func (n *AssignStmt) NodeSpan() *Span   { return &n.Sp }
func (n *Binary) NodeSpan() *Span       { return &n.Sp }
func (n *BindPat) NodeSpan() *Span      { return &n.Sp }
func (n *BoolLit) NodeSpan() *Span      { return &n.Sp }
func (n *BreakStmt) NodeSpan() *Span    { return &n.Sp }
func (n *CallEx) NodeSpan() *Span       { return &n.Sp }
func (n *CasePat) NodeSpan() *Span      { return &n.Sp }
func (n *ContinueStmt) NodeSpan() *Span { return &n.Sp }
func (n *EnumCase) NodeSpan() *Span     { return &n.Sp }
func (n *EnumDecl) NodeSpan() *Span     { return &n.Sp }
func (n *ExprStmt) NodeSpan() *Span     { return &n.Sp }
func (n *ExternDecl) NodeSpan() *Span   { return &n.Sp }
func (n *FieldEx) NodeSpan() *Span      { return &n.Sp }
func (n *FloatLit) NodeSpan() *Span     { return &n.Sp }
func (n *FnDecl) NodeSpan() *Span       { return &n.Sp }
func (n *FnLit) NodeSpan() *Span        { return &n.Sp }
func (n *ForInStmt) NodeSpan() *Span    { return &n.Sp }
func (n *GoStmt) NodeSpan() *Span       { return &n.Sp }
func (n *Ident) NodeSpan() *Span        { return &n.Sp }
func (n *IfStmt) NodeSpan() *Span       { return &n.Sp }
func (n *ImportStmt) NodeSpan() *Span   { return &n.Sp }
func (n *IndexEx) NodeSpan() *Span      { return &n.Sp }
func (n *IntLit) NodeSpan() *Span       { return &n.Sp }
func (n *LetStmt) NodeSpan() *Span      { return &n.Sp }
func (n *LitPat) NodeSpan() *Span       { return &n.Sp }
func (n *MapLit) NodeSpan() *Span       { return &n.Sp }
func (n *MatchEx) NodeSpan() *Span      { return &n.Sp }
func (n *RangeEx) NodeSpan() *Span      { return &n.Sp }
func (n *ReturnStmt) NodeSpan() *Span   { return &n.Sp }
func (n *SliceEx) NodeSpan() *Span      { return &n.Sp }
func (n *StrLit) NodeSpan() *Span       { return &n.Sp }
func (n *StructDecl) NodeSpan() *Span   { return &n.Sp }
func (n *StructLit) NodeSpan() *Span    { return &n.Sp }
func (n *TryEx) NodeSpan() *Span        { return &n.Sp }
func (n *Unary) NodeSpan() *Span        { return &n.Sp }
func (n *WhileStmt) NodeSpan() *Span    { return &n.Sp }
func (n *WildPat) NodeSpan() *Span      { return &n.Sp }

// 编译期保证：所有带 Sp 的节点都实现 Spanned（漏一个即编译失败）.
var (
	_ Spanned = (*Arm)(nil)
	_ Spanned = (*ArrLit)(nil)
	_ Spanned = (*AssignStmt)(nil)
	_ Spanned = (*Binary)(nil)
	_ Spanned = (*BindPat)(nil)
	_ Spanned = (*BoolLit)(nil)
	_ Spanned = (*BreakStmt)(nil)
	_ Spanned = (*CallEx)(nil)
	_ Spanned = (*CasePat)(nil)
	_ Spanned = (*ContinueStmt)(nil)
	_ Spanned = (*EnumCase)(nil)
	_ Spanned = (*EnumDecl)(nil)
	_ Spanned = (*ExprStmt)(nil)
	_ Spanned = (*ExternDecl)(nil)
	_ Spanned = (*FieldEx)(nil)
	_ Spanned = (*FloatLit)(nil)
	_ Spanned = (*FnDecl)(nil)
	_ Spanned = (*FnLit)(nil)
	_ Spanned = (*ForInStmt)(nil)
	_ Spanned = (*GoStmt)(nil)
	_ Spanned = (*Ident)(nil)
	_ Spanned = (*IfStmt)(nil)
	_ Spanned = (*ImportStmt)(nil)
	_ Spanned = (*IndexEx)(nil)
	_ Spanned = (*IntLit)(nil)
	_ Spanned = (*LetStmt)(nil)
	_ Spanned = (*LitPat)(nil)
	_ Spanned = (*MapLit)(nil)
	_ Spanned = (*MatchEx)(nil)
	_ Spanned = (*RangeEx)(nil)
	_ Spanned = (*ReturnStmt)(nil)
	_ Spanned = (*SliceEx)(nil)
	_ Spanned = (*StrLit)(nil)
	_ Spanned = (*StructDecl)(nil)
	_ Spanned = (*StructLit)(nil)
	_ Spanned = (*TryEx)(nil)
	_ Spanned = (*Unary)(nil)
	_ Spanned = (*WhileStmt)(nil)
	_ Spanned = (*WildPat)(nil)
)
