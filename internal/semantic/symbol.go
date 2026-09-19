package semantic

// Package semantic 实现名字解析（v0.3 Semantic Core 第一层）：
// 词法作用域、符号定义与引用绑定、遮蔽、重复声明与未定义名检查。
// 每个符号拥有本次编译内稳定的 SymbolID（按声明/绑定顺序编号，同一源码同一结果）。

import (
	"lang-go/internal/syntax"
)

// SymbolID 编译内稳定符号编号
type SymbolID int

// Kind 符号类别
type Kind int

const (
	KFn Kind = iota // 顶层/嵌套 fn 与 extern fn
	KMethod
	KVar   // let/var 绑定（全局与局部）
	KParam // 函数参数与方法接收者
	KStruct
	KEnum
	KCtor // enum case 构造器（含预载 Some/None/Ok/Err）
)

// Symbol 静态符号。TypeEx 为源码注解原文（可空），语义类型由 types 包赋予。
type Symbol struct {
	ID      SymbolID
	Kind    Kind
	Name    string
	Line    int
	Mutable bool

	TypeEx    syntax.TypeEx   // 声明注解（fn 返回值 / let 注解）
	Params    []syntax.TypeEx // 参数注解（可含 nil）
	RecvName  string          // KMethod：接收者 struct 名
	Arity     int
	TypeParams []string

	StructName string          // KCtor：所属 enum 名
	CaseIndex  int             // KCtor：case 序号
	Payloads   []syntax.TypeEx // KCtor：载荷类型

	Fields     []string        // KStruct：字段名（保序）
	FieldTypes []syntax.TypeEx // KStruct：字段类型（与 Fields 对齐）
	Methods    map[string]*Symbol

	Extern bool // KFn：extern 声明（无函数体）

	BuiltinMark bool // 预载符号（内建函数/预载 struct/http/Some 等），非用户源码声明
	Decl        any  // 源 AST 声明节点（*syntax.FnDecl 等，checker/HIR 复用；预载符号为 nil）
}

// IsCtor 是否枚举构造器
func (s *Symbol) IsCtor() bool { return s.Kind == KCtor }

// Result 解析产物：符号清单 + 节点引用绑定 + 诊断。
// Refs/PatRefs 以 AST 节点指针为键，仅在单次编译内有效。
type Result struct {
	Symbols []*Symbol
	Refs     map[syntax.Expr]*Symbol // Ident/StructLit 引用 → 符号
	PatRefs  map[syntax.Pat]*Symbol  // CasePat/BindPat → 符号
	DeclSyms map[syntax.Stmt]*Symbol // LetStmt/ForInStmt→绑定符号；FnDecl→fn 符号
	Builtin map[string]*Symbol      // 预载符号（内建函数/预载 struct/http）
	Diags   []Diagnostic
}
