package types

// Package types 实现语义类型检查器（v0.3 Semantic Core 第二层）：
// 统一 Type 模型 + 推断 + 兼容性判定 + 前置语义约束（迁移自 →Go/→JS 转译器的重复检查）。
//
// 保守性铁律：任一侧为 Any/Unknown 时不报错（Any 来自 jsonParse/extern 等动态边界，
// Unknown 来自无注解参数——保证现有合法程序零误杀）。

import (
	"strings"

	"lang-go/internal/semantic"
)

// Kind 类型构造器类别
type Kind uint8

const (
	KInvalid Kind = iota // 错误传播占位（已有诊断，下游静默）
	KInt
	KFloat
	KBool
	KString
	KError  // error struct
	KUnit   // ()
	KRange  // lo..hi
	KAny    // 动态值（jsonParse/extern 边界）
	KUnknown // 无注解推断不出
	KNever
	KSlice   // [T]
	KMap     // map[K]V
	KRef     // &T
	KChan    // chan[T]
	KFn      // fn(...): ...
	KStruct  // 名义：按符号同一
	KEnum    // 名义：按符号同一
	KOption  // Option[T]
	KResult  // Result[T, E]
	KTypeArg // 泛型类型参数（如 fn[T] 里的 T）
)

// Type 语义类型。字段按 Kind 选用：
//
//	Slice/Ref/Chan/Option → Elem；Map → Key+Elem；
//	Fn → Params+Ret；Result → Params[0]=T、Ret=E；
//	Struct/Enum/TypeArg → Sym。
type Type struct {
	Kind   Kind
	Elem   *Type
	Key    *Type
	Params []*Type
	Ret    *Type
	Sym    *semantic.Symbol
}

// 预载原始类型单例
var (
	Int      = &Type{Kind: KInt}
	Float    = &Type{Kind: KFloat}
	Bool     = &Type{Kind: KBool}
	String   = &Type{Kind: KString}
	ErrorT   = &Type{Kind: KError}
	Unit     = &Type{Kind: KUnit}
	Range    = &Type{Kind: KRange}
	Any      = &Type{Kind: KAny}
	Unknown  = &Type{Kind: KUnknown}
	Never    = &Type{Kind: KNever}
	Invalid  = &Type{Kind: KInvalid}
)

func SliceOf(elem *Type) *Type    { return &Type{Kind: KSlice, Elem: elem} }
func MapOf(key, val *Type) *Type  { return &Type{Kind: KMap, Key: key, Elem: val} }
func RefOf(elem *Type) *Type      { return &Type{Kind: KRef, Elem: elem} }
func ChanOf(elem *Type) *Type     { return &Type{Kind: KChan, Elem: elem} }
func OptionOf(elem *Type) *Type   { return &Type{Kind: KOption, Elem: elem} }
func ResultOf(ok, err *Type) *Type { return &Type{Kind: KResult, Params: []*Type{ok}, Ret: err} }
func FnOf(params []*Type, ret *Type) *Type {
	return &Type{Kind: KFn, Params: params, Ret: ret}
}
func StructOf(sym *semantic.Symbol) *Type { return &Type{Kind: KStruct, Sym: sym} }
func EnumOf(sym *semantic.Symbol) *Type   { return &Type{Kind: KEnum, Sym: sym} }
func TypeArgOf(sym *semantic.Symbol) *Type { return &Type{Kind: KTypeArg, Sym: sym} }

// ResultOk / ResultErr Result 的 T / E 侧
func ResultOk(t *Type) *Type {
	if t.Kind == KResult && len(t.Params) == 1 {
		return t.Params[0]
	}
	return Unknown
}
func ResultErr(t *Type) *Type {
	if t.Kind == KResult && t.Ret != nil {
		return t.Ret
	}
	return Unknown
}

// Equal 结构同一（Struct/Enum 按符号同一——名义类型；TypeArg 为擦除占位，彼此同一）
func Equal(a, b *Type) bool {
	if a == b {
		return true
	}
	if a == nil || b == nil || a.Kind != b.Kind {
		return false
	}
	switch a.Kind {
	case KSlice, KRef, KChan, KOption:
		return Equal(a.Elem, b.Elem)
	case KMap:
		return Equal(a.Key, b.Key) && Equal(a.Elem, b.Elem)
	case KFn:
		if len(a.Params) != len(b.Params) {
			return false
		}
		for i := range a.Params {
			if !Equal(a.Params[i], b.Params[i]) {
				return false
			}
		}
		return Equal(a.Ret, b.Ret)
	case KResult:
		return Equal(ResultOk(a), ResultOk(b)) && Equal(ResultErr(a), ResultErr(b))
	case KStruct, KEnum:
		return a.Sym != nil && a.Sym == b.Sym
	case KTypeArg:
		return true // 泛型参数擦除：所有 TypeArg 互为同一（v0 注解为文档性）
	default:
		return true // 原始类型
	}
}

// laxAny 宽松类型：参与即放行（动态边界/推断缺口/泛型擦除/错误传播）
func laxAny(t *Type) bool {
	k := t.Kind
	return k == KAny || k == KUnknown || k == KInvalid || k == KTypeArg
}

// Assignable 赋值兼容。
// 宽松类型双向放行；容器与代数类型按元素协变（运行时容器是擦除的，
// 空字面量/动态值经注解收窄是既有合法用法）；fn 按参数逆协变 + 返回协变；
// struct/enum 名义同一。
func Assignable(to, from *Type) bool {
	if to == nil || from == nil {
		return true
	}
	if laxAny(to) || laxAny(from) {
		return true
	}
	if to.Kind != from.Kind {
		return false
	}
	switch to.Kind {
	case KSlice, KRef, KChan, KOption:
		return Assignable(to.Elem, from.Elem)
	case KMap:
		return Assignable(to.Key, from.Key) && Assignable(to.Elem, from.Elem)
	case KResult:
		return Assignable(ResultOk(to), ResultOk(from)) && Assignable(ResultErr(to), ResultErr(from))
	case KFn:
		if len(to.Params) != len(from.Params) {
			return false
		}
		for i := range to.Params {
			if !Assignable(from.Params[i], to.Params[i]) { // 参数逆协变
				return false
			}
		}
		return Assignable(to.Ret, from.Ret)
	}
	return Equal(to, from)
}

// IsNumeric 是否数值类型
func IsNumeric(t *Type) bool { return t.Kind == KInt || t.Kind == KFloat }

// IsConcrete 是否具体类型（非 Any/Unknown/Invalid）
func IsConcrete(t *Type) bool {
	return t != nil && t.Kind != KAny && t.Kind != KUnknown && t.Kind != KInvalid
}

// String 诊断与 dump 用类型名（贴近源码写法）
func (t *Type) String() string {
	if t == nil {
		return "unknown"
	}
	switch t.Kind {
	case KInvalid:
		return "invalid"
	case KInt:
		return "int"
	case KFloat:
		return "float"
	case KBool:
		return "bool"
	case KString:
		return "string"
	case KError:
		return "error"
	case KUnit:
		return "()"
	case KRange:
		return "range"
	case KAny:
		return "any"
	case KUnknown:
		return "unknown"
	case KNever:
		return "never"
	case KSlice:
		return "[" + t.Elem.String() + "]"
	case KMap:
		return "map[" + t.Key.String() + "]" + t.Elem.String()
	case KRef:
		return "&" + t.Elem.String()
	case KChan:
		return "chan[" + t.Elem.String() + "]"
	case KFn:
		var ps []string
		for _, p := range t.Params {
			ps = append(ps, p.String())
		}
		s := "fn(" + strings.Join(ps, ", ") + ")"
		if t.Ret != nil && t.Ret.Kind != KUnit {
			s += ": " + t.Ret.String()
		}
		return s
	case KStruct:
		return symName(t.Sym)
	case KEnum:
		return symName(t.Sym)
	case KOption:
		return "Option[" + t.Elem.String() + "]"
	case KResult:
		return "Result[" + ResultOk(t).String() + ", " + ResultErr(t).String() + "]"
	case KTypeArg:
		return symName(t.Sym)
	}
	return "?"
}

func symName(s *semantic.Symbol) string {
	if s == nil {
		return "?"
	}
	return s.Name
}
