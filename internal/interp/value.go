package interp

import (
	"fmt"
	"strconv"
	"strings"

	"ling/internal/syntax"
)

// Value 的具体类型：
// IntVal/FloatVal/BoolVal/StrVal/UnitVal/RangeVal 为值类型；
// *ArrVal/*MapVal/*StructVal/*EnumVal/*Closure/*Builtin/*PtrVal 为指针（共享/引用语义）。
type Value interface{}

type IntVal int64
type FloatVal float64
type BoolVal bool
type StrVal string
type UnitVal struct{}

type RangeVal struct{ Lo, Hi int64 }

// ArrVal 切片：引用语义
type ArrVal struct{ Items []Value }

// MapVal 保插入序的映射
type MapVal struct {
	ks []Value
	m  map[string]Value
}

func keyString(v Value) (string, bool) {
	switch x := v.(type) {
	case IntVal:
		return fmt.Sprintf("i:%d", int64(x)), true
	case StrVal:
		return "s:" + string(x), true
	case BoolVal:
		return fmt.Sprintf("b:%v", bool(x)), true
	}
	return "", false
}

func (m *MapVal) Get(k Value) (Value, bool) {
	ks, ok := keyString(k)
	if !ok {
		return nil, false
	}
	v, ok := m.m[ks]
	return v, ok
}

func (m *MapVal) Set(k, v Value) error {
	ks, ok := keyString(k)
	if !ok {
		return fmt.Errorf("映射键须为 int/string/bool，得到 %s", typeName(k))
	}
	if m.m == nil {
		m.m = map[string]Value{}
	}
	if _, exists := m.m[ks]; !exists {
		m.ks = append(m.ks, k)
	}
	m.m[ks] = v
	return nil
}

func (m *MapVal) Keys() []Value { return append([]Value(nil), m.ks...) }
func (m *MapVal) Len() int      { return len(m.ks) }

type StructDef struct {
	Name    string
	Fields  []string
	Methods map[string]*Closure
}

type EnumDef struct {
	Name  string
	Cases []string
}

func (d *EnumDef) caseIndex(name string) int {
	for i, c := range d.Cases {
		if c == name {
			return i
		}
	}
	return -1
}

type StructVal struct {
	Def    *StructDef
	Fields []Value
}

type EnumVal struct {
	Def      *EnumDef
	Case     int
	Payloads []Value
}

type PtrVal struct{ Cell *Value }

type Closure struct {
	Name string
	Decl *syntax.FnDecl
	Env  *Env
}

type Builtin struct {
	Name string
	Fn   func(it *Interp, args []Value) (Value, error)
}

// —— 环境 ——

type cell struct {
	val     Value
	mutable bool
}

type Env struct {
	vars   map[string]*cell
	parent *Env
}

func newEnv(parent *Env) *Env {
	return &Env{vars: map[string]*cell{}, parent: parent}
}

func (e *Env) define(name string, v Value, mutable bool) error {
	if _, exists := e.vars[name]; exists {
		return fmt.Errorf("%s 在此作用域重复定义", name)
	}
	e.vars[name] = &cell{val: v, mutable: mutable}
	return nil
}

// redefine 声明式定义，允许覆盖（顶层 fn 在 REPL 里反复声明用）
func (e *Env) redefine(name string, v Value) {
	e.vars[name] = &cell{val: v, mutable: false}
}

func (e *Env) lookup(name string) (*cell, bool) {
	for s := e; s != nil; s = s.parent {
		if c, ok := s.vars[name]; ok {
			return c, true
		}
	}
	return nil, false
}

// —— 值语义与比较 ——

// copyValue 落实 struct 值语义：struct 递归拷贝、enum 载荷递归拷贝；slice/map 共享（spec §3.7）
func copyValue(v Value) Value {
	switch x := v.(type) {
	case *StructVal:
		fs := make([]Value, len(x.Fields))
		for i, f := range x.Fields {
			fs[i] = copyValue(f)
		}
		return &StructVal{Def: x.Def, Fields: fs}
	case *EnumVal:
		ps := make([]Value, len(x.Payloads))
		for i, p := range x.Payloads {
			ps[i] = copyValue(p)
		}
		return &EnumVal{Def: x.Def, Case: x.Case, Payloads: ps}
	}
	return v
}

func typeName(v Value) string {
	switch x := v.(type) {
	case IntVal:
		return "int"
	case FloatVal:
		return "float"
	case BoolVal:
		return "bool"
	case StrVal:
		return "string"
	case UnitVal:
		return "()"
	case *ArrVal:
		return "数组"
	case *MapVal:
		return "映射"
	case *RangeVal:
		return "区间"
	case *StructVal:
		return x.Def.Name
	case *EnumVal:
		return x.Def.Name
	case *Closure:
		return "函数"
	case *Builtin:
		return "内建函数"
	case *PtrVal:
		return "引用"
	}
	return "未知值"
}

// equals == 语义：标量同型比较、enum 同 case 递归比较；容器不可比（spec §3.3）
func equals(a, b Value) (bool, error) {
	switch x := a.(type) {
	case IntVal:
		y, ok := b.(IntVal)
		if !ok {
			return false, typeErr(a, b)
		}
		return x == y, nil
	case FloatVal:
		y, ok := b.(FloatVal)
		if !ok {
			return false, typeErr(a, b)
		}
		return x == y, nil
	case BoolVal:
		y, ok := b.(BoolVal)
		if !ok {
			return false, typeErr(a, b)
		}
		return x == y, nil
	case StrVal:
		y, ok := b.(StrVal)
		if !ok {
			return false, typeErr(a, b)
		}
		return x == y, nil
	case *EnumVal:
		y, ok := b.(*EnumVal)
		if !ok || y.Def != x.Def || y.Case != x.Case {
			return false, nil
		}
		for i := range x.Payloads {
			eq, err := equals(x.Payloads[i], y.Payloads[i])
			if err != nil {
				return false, err
			}
			if !eq {
				return false, nil
			}
		}
		return true, nil
	default:
		return false, fmt.Errorf("%s 不可用 == 比较", typeName(a))
	}
}

func typeErr(a, b Value) error {
	return fmt.Errorf("%s 与 %s 不可比较（无隐式转换）", typeName(a), typeName(b))
}

func truthy(v Value) (bool, error) {
	b, ok := v.(BoolVal)
	if !ok {
		return false, fmt.Errorf("条件须为 bool，得到 %s", typeName(v))
	}
	return bool(b), nil
}

// Fmt 导出的值格式化（REPL 与工具用）
func Fmt(v Value) string { return fmtVal(v, true) }

// —— 输出格式 ——

func fmtVal(v Value, top bool) string {
	switch x := v.(type) {
	case IntVal:
		return strconv.FormatInt(int64(x), 10)
	case FloatVal:
		return strconv.FormatFloat(float64(x), 'g', -1, 64)
	case BoolVal:
		return strconv.FormatBool(bool(x))
	case StrVal:
		if top {
			return string(x)
		}
		return strconv.Quote(string(x))
	case UnitVal:
		return "()"
	case *ArrVal:
		parts := make([]string, len(x.Items))
		for i, it := range x.Items {
			parts[i] = fmtVal(it, false)
		}
		return "[" + strings.Join(parts, ", ") + "]"
	case *MapVal:
		parts := make([]string, 0, x.Len())
		for _, k := range x.ks {
			kv, _ := x.Get(k)
			parts = append(parts, fmtVal(k, false)+": "+fmtVal(kv, false))
		}
		return "{" + strings.Join(parts, ", ") + "}"
	case *StructVal:
		parts := make([]string, len(x.Fields))
		for i, f := range x.Fields {
			parts[i] = x.Def.Fields[i] + ": " + fmtVal(f, false)
		}
		return x.Def.Name + "{" + strings.Join(parts, ", ") + "}"
	case *EnumVal:
		name := x.Def.Cases[x.Case]
		if len(x.Payloads) == 0 {
			return name
		}
		parts := make([]string, len(x.Payloads))
		for i, p := range x.Payloads {
			parts[i] = fmtVal(p, false)
		}
		return name + "(" + strings.Join(parts, ", ") + ")"
	case *RangeVal:
		return fmtVal(IntVal(x.Lo), true) + ".." + fmtVal(IntVal(x.Hi), true)
	case *Closure:
		if x.Name != "" {
			return "<fn " + x.Name + ">"
		}
		return "<fn 匿名>"
	case *Builtin:
		return "<内建 " + x.Name + ">"
	case *PtrVal:
		return "&" + fmtVal(*x.Cell, false)
	}
	return "?"
}
