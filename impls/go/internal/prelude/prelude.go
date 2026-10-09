// Package prelude 是预载代数类型（Option/Result/error）定义的单一来源
// （v1.0 Gate D1，specs/option-result.md RESULT-PRELUDE-001）。
// 纯数据表（不依赖任何解释器的值类型，避免导入环）：
// AST 解释器与 IR 解释器各自把本表转换为自己的 Def 表示，转换必须逐字段忠实。
// 定义身份约束：case 名与序、载荷个数、error 字段序以本包为准。
package prelude

// EnumDef 预载 enum 定义（纯数据）.
type EnumDef struct {
	Name  string
	Cases []string // case 名即契约序（Some/None、Ok/Err）
}

// StructDef 预载 struct 定义（纯数据）.
type StructDef struct {
	Name   string
	Fields []string
}

// CtorInfo 全局构造器表项.
type CtorInfo struct {
	Enum  string
	Index int
	Arity int
}

// 契约常量（specs/option-result.md；变更需 Major）.
const (
	OptionName  = "Option"
	OptionSome  = "Some"
	OptionNone  = "None"
	ResultName  = "Result"
	ResultOk    = "Ok"
	ResultErr   = "Err"
	ErrorStruct = "error"
	ErrorField  = "msg"
)

// Enums 预载 enum 全集.
func Enums() map[string]EnumDef {
	return map[string]EnumDef{
		OptionName: {Name: OptionName, Cases: []string{OptionSome, OptionNone}},
		ResultName: {Name: ResultName, Cases: []string{ResultOk, ResultErr}},
	}
}

// Ctors 全局构造器表（case 名全局唯一，NAME-CTOR-005）.
func Ctors() map[string]CtorInfo {
	return map[string]CtorInfo{
		OptionSome: {Enum: OptionName, Index: 0, Arity: 1},
		OptionNone: {Enum: OptionName, Index: 1, Arity: 0},
		ResultOk:   {Enum: ResultName, Index: 0, Arity: 1},
		ResultErr:  {Enum: ResultName, Index: 1, Arity: 1},
	}
}

// ErrorDef error struct 定义.
func ErrorDef() StructDef {
	return StructDef{Name: ErrorStruct, Fields: []string{ErrorField}}
}
