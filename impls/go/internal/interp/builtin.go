package interp

import (
	"errors"
	"fmt"
	"io"
	"math"
	"strconv"
	"strings"

	"lang-go/internal/prelude"
)

// prelude 预载代数类型（Option/Result/error，spec §3.6）与内建函数（spec §4）.
func (it *Interp) prelude() {
	// 预载定义来自 prelude 单一来源（v1.0 Gate D1）
	for name, ci := range prelude.Ctors() {
		ed := it.enums[ci.Enum]
		if ed == nil {
			for _, pd := range prelude.Enums() {
				if pd.Name == ci.Enum {
					ed = &EnumDef{Name: pd.Name, Cases: append([]string(nil), pd.Cases...)}
					it.enums[ed.Name] = ed

					break
				}
			}
		}

		it.ctors[name] = &ctor{def: ed, idx: ci.Index, arity: ci.Arity}
	}

	it.structs[prelude.ErrorStruct] = func() *StructDef {
		sd := prelude.ErrorDef()
		return &StructDef{Name: sd.Name, Fields: append([]string(nil), sd.Fields...), Methods: map[string]*Closure{}}
	}()

	reg := func(name string, fn func(it *Interp, args []Value) (Value, error)) {
		it.Globals.redefine(name, &Builtin{Name: name, Fn: fn})
	}

	reg("println", func(it *Interp, args []Value) (Value, error) {
		return UnitVal{}, it.write(it.fmtArgs(args) + "\n")
	})
	reg("print", func(it *Interp, args []Value) (Value, error) {
		return UnitVal{}, it.write(it.fmtArgs(args))
	})
	reg("len", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("len", args, 1); err != nil {
			return nil, err
		}

		switch x := args[0].(type) {
		case StrVal:
			return IntVal(len([]rune(string(x)))), nil
		case *ArrVal:
			return IntVal(len(x.Items)), nil
		case *MapVal:
			return IntVal(x.Len()), nil
		case *RangeVal:
			if x.Hi > x.Lo {
				return IntVal(x.Hi - x.Lo), nil
			}

			return IntVal(0), nil
		}

		return nil, fmt.Errorf("len 不支持 %s", typeName(args[0]))
	})
	reg("str", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("str", args, 1); err != nil {
			return nil, err
		}

		return StrVal(fmtVal(args[0], true)), nil
	})
	reg("int", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("int", args, 1); err != nil {
			return nil, err
		}

		switch x := args[0].(type) {
		case IntVal:
			return x, nil
		case FloatVal:
			return IntVal(floatToInt64(float64(x))), nil
		case StrVal:
			n, err := strconv.ParseInt(strings.TrimSpace(string(x)), 10, 64)
			if err != nil {
				return nil, fmt.Errorf("无法把 %q 转为 int", string(x))
			}

			return IntVal(n), nil
		}

		return nil, fmt.Errorf("int() 不支持 %s", typeName(args[0]))
	})
	reg("float", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("float", args, 1); err != nil {
			return nil, err
		}

		switch x := args[0].(type) {
		case IntVal:
			return FloatVal(x), nil
		case FloatVal:
			return x, nil
		case StrVal:
			f, err := strconv.ParseFloat(strings.TrimSpace(string(x)), 64)
			if err != nil {
				return nil, fmt.Errorf("无法把 %q 转为 float", string(x))
			}

			return FloatVal(f), nil
		}

		return nil, fmt.Errorf("float() 不支持 %s", typeName(args[0]))
	})
	reg("keys", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("keys", args, 1); err != nil {
			return nil, err
		}

		m, ok := args[0].(*MapVal)
		if !ok {
			return nil, fmt.Errorf("keys 需要映射，得到 %s", typeName(args[0]))
		}

		return &ArrVal{Items: m.Keys()}, nil
	})
	reg("values", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("values", args, 1); err != nil {
			return nil, err
		}

		m, ok := args[0].(*MapVal)
		if !ok {
			return nil, fmt.Errorf("values 需要映射，得到 %s", typeName(args[0]))
		}

		vs := make([]Value, 0, m.Len())
		for _, k := range m.ks {
			v, _ := m.Get(k)
			vs = append(vs, v)
		}

		return &ArrVal{Items: vs}, nil
	})
	reg("has", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("has", args, 2); err != nil {
			return nil, err
		}

		m, ok := args[0].(*MapVal)
		if !ok {
			return nil, fmt.Errorf("has 需要映射，得到 %s", typeName(args[0]))
		}

		_, found := m.Get(args[1])

		return BoolVal(found), nil
	})
	reg("push", func(it *Interp, args []Value) (Value, error) {
		if len(args) < 2 {
			return nil, errors.New("push 需要至少 2 个参数")
		}

		arr, ok := args[0].(*ArrVal)
		if !ok {
			return nil, fmt.Errorf("push 需要数组，得到 %s", typeName(args[0]))
		}

		arr.Items = append(arr.Items, args[1:]...)

		return arr, nil
	})
	reg("assert", func(it *Interp, args []Value) (Value, error) {
		if len(args) < 1 || len(args) > 2 {
			return nil, errors.New("assert 需要 1~2 个参数")
		}

		t, err := truthy(args[0])
		if err != nil {
			return nil, err
		}

		if !t {
			if len(args) == 2 {
				return nil, fmt.Errorf("assert 失败: %s", fmtVal(args[1], true))
			}

			return nil, errors.New("assert 失败")
		}

		return UnitVal{}, nil
	})
	reg("err", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("err", args, 1); err != nil {
			return nil, err
		}

		msg, ok := args[0].(StrVal)
		if !ok {
			return nil, fmt.Errorf("err 需要字符串，得到 %s", typeName(args[0]))
		}

		def := it.structs["error"]

		return &StructVal{Def: def, Fields: []Value{msg}}, nil
	})
	reg("bytes", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("bytes", args, 1); err != nil {
			return nil, err
		}

		s, ok := args[0].(StrVal)
		if !ok {
			return nil, fmt.Errorf("bytes 需要字符串，得到 %s", typeName(args[0]))
		}

		bs := []byte(string(s))

		items := make([]Value, len(bs))
		for i, b := range bs {
			items[i] = IntVal(b)
		}

		return &ArrVal{Items: items}, nil
	})
	reg("char", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("char", args, 1); err != nil {
			return nil, err
		}

		n, ok := args[0].(IntVal)
		if !ok {
			return nil, fmt.Errorf("char 需要 int，得到 %s", typeName(args[0]))
		}

		return StrVal(string(rune(n))), nil
	})
}

func (it *Interp) fmtArgs(args []Value) string {
	parts := make([]string, len(args))
	for i, a := range args {
		parts[i] = fmtVal(a, true)
	}

	return strings.Join(parts, " ")
}

func (it *Interp) write(s string) error {
	_, err := io.WriteString(it.Out, s)
	return err
}

func (it *Interp) arity(name string, args []Value, n int) error {
	if len(args) != n {
		return fmt.Errorf("%s 需要 %d 个参数，得到 %d 个", name, n, len(args))
	}

	return nil
}

// floatToInt64 实现 int(float) 的裁定边界（TYPE-CONV-005 v1.0.1，ADR-0006/RS-006
// 跨实现硬契约）：NaN → 0；正/负溢出饱和到 int64 边界；其余向零截断。
// 不用裸 int64(f)：Go 该转换对越界/NaN 是平台定义行为（amd64 取 MinInt64）。
func floatToInt64(f float64) int64 {
	switch {
	case math.IsNaN(f):
		return 0
	case f >= 9223372036854775808.0: // 2^63 的 f64 精确表示
		return math.MaxInt64
	case f <= -9223372036854775808.0: // 恰为 -2^63 时 int64(f) 本就等于 MinInt64
		return math.MinInt64
	}

	return int64(f)
}
