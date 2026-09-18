package syntax

// Node 语法树节点统一带行号
type Node interface{ Pos() int }

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
}

type FloatLit struct {
	Val  float64
	Line int
}

type StrLit struct {
	Val  string
	Line int
}

type BoolLit struct {
	Val  bool
	Line int
}

type Ident struct {
	Name string
	Line int
}

type Unary struct {
	Op   Kind
	X    Expr
	Line int
}

type Binary struct {
	Op   Kind
	L    Expr
	R    Expr
	Line int
}

type RangeEx struct {
	Lo, Hi Expr
	Line   int
}

type CallEx struct {
	Callee Expr
	Args   []Expr
	Line   int
}

type IndexEx struct {
	Obj  Expr
	Idx  Expr
	Line int
}

// SliceEx a[lo:hi]，lo/hi 必填
type SliceEx struct {
	Obj  Expr
	Lo   Expr
	Hi   Expr
	Line int
}

type FieldEx struct {
	Obj  Expr
	Name string
	Line int
}

// TryEx e? —— Result 传播
type TryEx struct {
	X    Expr
	Line int
}

type FieldLit struct {
	Name string
	Val  Expr
}

type StructLit struct {
	TypeName string
	Fields   []FieldLit
	Line     int
}

type MapItem struct {
	Key Expr
	Val Expr
}

type MapLit struct {
	Items []MapItem
	Line  int
}

type ArrLit struct {
	Items []Expr
	Line  int
}

type Arm struct {
	Pat  Pat
	Body Expr
	Line int
}

type MatchEx struct {
	Scrut Expr
	Arms  []Arm
	Line  int
}

// FnLit 匿名函数表达式
type FnLit struct {
	TypeParams []string
	Params     []Param
	Ret        TypeEx
	Body       []Stmt
	Line       int
}

// —— 模式 ——

type WildPat struct{ Line int }

type BindPat struct {
	Name string
	Line int
}

// LitPat 字面量模式（Int/Float/Str/Bool）
type LitPat struct {
	Val  Expr
	Line int
}

// CasePat 枚举构造模式，Subs 为载荷子模式
type CasePat struct {
	Name string
	Subs []Pat
	Line int
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
}

type AssignStmt struct {
	Target Expr
	Val    Expr
	Line   int
}

type ExprStmt struct {
	X    Expr
	Line int
}

type ReturnStmt struct {
	Val  Expr // 可空：bare return
	Line int
}

type IfStmt struct {
	Cond Expr
	Then []Stmt
	Else []Stmt // 可空；else if 展开为单元素块
	Line int
}

type WhileStmt struct {
	Cond Expr
	Body []Stmt
	Line int
}

type ForInStmt struct {
	Var  string
	Iter Expr
	Body []Stmt
	Line int
}

type BreakStmt struct{ Line int }

type ContinueStmt struct{ Line int }

// —— 声明（顶层与嵌套共用） ——

type FnDecl struct {
	Name       string
	Recv       *Param // 值接收者，v0
	TypeParams []string
	Params     []Param
	Ret        TypeEx
	Body       []Stmt
	Line       int
}

type StructDecl struct {
	Name       string
	TypeParams []string
	Fields     []Param
	Line       int
}

type EnumCase struct {
	Name     string
	Payloads []TypeEx
	Line     int
}

type EnumDecl struct {
	Name       string
	TypeParams []string
	Cases      []EnumCase
	Line       int
}

// ImportStmt import "./md5"（装载器在合并前展开，解释器不会执行到）
type ImportStmt struct {
	Path string
	Line int
}

// ExternDecl extern "go" { fn 签名 } 声明块：宿主函数手写签名（decisions #17/#35）
type ExternDecl struct {
	Host string
	Fns  []*FnDecl // 无函数体
	Line int
}

// —— 类型注解（v0 解释器不检查，供 M3 静态检查器使用） ——

type NamedType struct {
	Name string
	Args []TypeEx
}

type ArrType struct{ Elem TypeEx }

type MapType struct{ Key, Val TypeEx }

type PtrType struct{ Elem TypeEx }

// FnType 函数类型：fn(int, string): bool。v0 解释器不检查，→Go 转译需要它生成签名
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
func (n *ExternDecl) Pos() int   { return n.Line }
func (n *ExternDecl) isStmt()    {}
func (n *NamedType) isType()     {}
func (n *ArrType) isType()       {}
func (n *MapType) isType()       {}
func (n *PtrType) isType()       {}
func (n *FnType) isType()        {}
