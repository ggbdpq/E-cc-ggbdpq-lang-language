// Package irinterp 是 Core IR 的树遍历解释器（v0.9 Semantic Integrity）。
//
// 定位（specs/README 真值优先级）：AST 解释器（internal/interp）是参考语义
// Oracle；irinterp 消费与 →Go/→JS 后端相同的 Core IR，用于四路差分验证——
// 它证明「IR 语义 = AST 语义」。职责边界：irinterp 不做名字解析、类型推断
// 或语义校验（结论已固化在 IR 节点上），语义规范一律以 specs/ 为准。
//
// 值模型复用 interp 的导出值类型与 interp.Fmt（输出格式是跨实现硬契约
// RUNTIME-OUTPUT-003，禁止第二份实现）；内建经 interp.Interp.CallBuiltin
// 复用同一套实现（decisions #37 两端一致）。预载 Option/Result/error 在本包
// 重建定义（字段与 interp.prelude 同名同序）——判定一律走 Def.Name/Case 序，
// 与 host 内建的 Def 指针解耦但输出与语义逐字节一致。
package irinterp

import (
	"fmt"
	"io"
	"strings"

	"lang-go/internal/interp"
	"lang-go/internal/ir"
	"lang-go/internal/prelude"
	"lang-go/internal/semantic"
)

// Machine IR 解释器
type Machine struct {
	Out io.Writer

	host    *interp.Interp // 内建值构造上下文（CallBuiltin 复用全部内建实现）
	fns     map[string]*ir.Fn
	methods map[string]*ir.Fn // structName.method → fn
	ctors   map[string]*ctorInfo
	structs map[string]*interp.StructDef
	enums   map[string]*interp.EnumDef
	globals *frame
	// globalSlots 按符号 ID 的全局槽位：IR 引用侧的 Local 可能是独立实例
	// （lower 对全局引用 fallback 新建 Local），以 Sym.ID 归位同一存储
	globalSlots map[int64]*interp.Value
}

type ctorInfo struct {
	def *interp.EnumDef
	idx int
}

// frame 局部槽位帧。槽位为 *Value：赋值写槽、读位解槽。
type frame struct {
	vars   map[*ir.Local]*interp.Value
	fns    map[string]*ir.Fn // 嵌套 FnDef
	parent *frame
}

func newFrame(parent *frame) *frame {
	return &frame{vars: map[*ir.Local]*interp.Value{}, fns: map[string]*ir.Fn{}, parent: parent}
}

func (f *frame) slot(l *ir.Local) **interp.Value {
	for cur := f; cur != nil; cur = cur.parent {
		if p, ok := cur.vars[l]; ok {
			return &p
		}
	}
	return nil
}

// New 构造 Machine（不执行）
func New(out io.Writer) *Machine {
	m := &Machine{
		Out:         out,
		host:        interp.New(io.Discard),
		fns:         map[string]*ir.Fn{},
		methods:     map[string]*ir.Fn{},
		ctors:       map[string]*ctorInfo{},
		structs:     map[string]*interp.StructDef{},
		enums:       map[string]*interp.EnumDef{},
		globalSlots: map[int64]*interp.Value{},
	}
	// 预载代数类型与 error（RESULT-PRELUDE-001）：定义来自 prelude 纯数据表（Gate D1）
	for _, pd := range prelude.Enums() {
		m.enums[pd.Name] = &interp.EnumDef{Name: pd.Name, Cases: append([]string(nil), pd.Cases...)}
	}
	for name, ci := range prelude.Ctors() {
		m.ctors[name] = &ctorInfo{def: m.enums[ci.Enum], idx: ci.Index}
	}
	if es := prelude.ErrorDef(); true {
		m.structs[es.Name] = &interp.StructDef{Name: es.Name, Fields: append([]string(nil), es.Fields...), Methods: map[string]*interp.Closure{}}
	}
	return m
}

// Run 执行程序：声明注册 → 全局初始化（MODULE-INIT-005 合并序）→ 调 main
func (m *Machine) Run(p *ir.Prog) error {
	m.globals = newFrame(nil)
	for _, sd := range p.Structs {
		fs := make([]string, len(sd.Fields))
		for i, f := range sd.Fields {
			fs[i] = f.Name
		}
		m.structs[sd.Name] = &interp.StructDef{Name: sd.Name, Fields: fs, Methods: map[string]*interp.Closure{}}
	}
	for _, ed := range p.Enums {
		cs := make([]string, len(ed.Cases))
		for i, c := range ed.Cases {
			cs[i] = c.Name
		}
		def := &interp.EnumDef{Name: ed.Name, Cases: cs}
		m.enums[ed.Name] = def
		for i, c := range ed.Cases {
			m.ctors[c.Name] = &ctorInfo{def: def, idx: i}
		}
	}
	for _, fn := range p.Fns {
		if fn.Recv != nil {
			m.methods[fn.Recv.Typ.Sym.Name+"."+fn.Name] = fn
			continue
		}
		m.fns[fn.Name] = fn
	}
	for _, g := range p.Globals {
		v, err := m.eval(g.Init, m.globals)
		if err != nil {
			return err
		}
		m.globals.vars[g.Local] = valPtr(copyVal(v))
		m.indexGlobal(g.Local)
	}
	if p.Main != nil {
		_, err := m.callFn(p.Main, nil, nil)
		return err
	}
	return nil
}

// indexGlobal 注册全局符号槽位（Sym.ID → 槽）
func (m *Machine) indexGlobal(l *ir.Local) {
	if l.Sym != nil {
		if p, ok := m.globals.vars[l]; ok {
			m.globalSlots[int64(l.Sym.ID)] = p
		}
	}
}

// globalSlot 引用侧兜底：Local 实例不同但符号相同的全局槽
func (m *Machine) globalSlot(l *ir.Local) **interp.Value {
	if l.Sym != nil {
		if p, ok := m.globalSlots[int64(l.Sym.ID)]; ok {
			return &p
		}
	}
	return nil
}

// —— 语句执行：返回控制流信号 ——

type flow uint8

const (
	flowNormal flow = iota
	flowBreak
	flowContinue
	flowReturn
)

type step struct {
	flow flow
	val  interp.Value
}

func (m *Machine) execStmts(stmts []ir.Stmt, f *frame) (step, error) {
	for _, st := range stmts {
		s, err := m.exec(st, f)
		if err != nil || s.flow != flowNormal {
			return s, err
		}
	}
	return step{}, nil
}

func (m *Machine) exec(st ir.Stmt, f *frame) (step, error) {
	switch x := st.(type) {
	case *ir.Let:
		v, err := m.eval(x.Init, f)
		if err != nil {
			return step{}, err
		}
		f.vars[x.Local] = valPtr(copyVal(v))
		return step{}, nil
	case *ir.Assign:
		return m.execAssign(x, f)
	case *ir.Eval:
		if _, err := m.eval(x.X, f); err != nil {
			return step{}, err
		}
		return step{}, nil
	case *ir.Return:
		var v interp.Value = interp.UnitVal{}
		if x.Val != nil {
			rv, err := m.eval(x.Val, f)
			if err != nil {
				return step{}, err
			}
			v = copyVal(rv)
		}
		return step{flow: flowReturn, val: v}, nil
	case *ir.If:
		c, err := m.eval(x.Cond, f)
		if err != nil {
			return step{}, err
		}
		if truthy(c) {
			return m.execStmts(x.Then, newFrame(f))
		}
		return m.execStmts(x.Else, newFrame(f))
	case *ir.While:
		for {
			c, err := m.eval(x.Cond, f)
			if err != nil {
				return step{}, err
			}
			if !truthy(c) {
				return step{}, nil
			}
			s, err := m.execStmts(x.Body, newFrame(f))
			if err != nil || s.flow == flowBreak || s.flow == flowReturn {
				return s, err
			}
		}
	case *ir.ForIn:
		return m.execForIn(x, f)
	case *ir.Break:
		return step{flow: flowBreak}, nil
	case *ir.Continue:
		return step{flow: flowContinue}, nil
	case *ir.Spawn:
		// TASK-SPAWN-002 同步确定子集：实参就地求值后，Task 在当前语句点同步执行完毕
		args, err := m.evalArgs(x.Args, f)
		if err != nil {
			return step{}, err
		}
		fn := m.fns[x.Fn.Name]
		if fn == nil {
			return step{}, fmt.Errorf("go 的目标未注册: %s", x.Fn.Name)
		}
		if _, err := m.callFn(fn, nil, args); err != nil {
			return step{}, err
		}
		return step{}, nil
	case *ir.FnDef:
		f.fns[x.Fn.Name] = x.Fn
		return step{}, nil
	}
	return step{}, fmt.Errorf("irinterp: 未支持的语句 %T", st)
}

func (m *Machine) execForIn(x *ir.ForIn, f *frame) (step, error) {
	it, err := m.eval(x.Iter, f)
	if err != nil {
		return step{}, err
	}
	var items []interp.Value
	switch x.Kind {
	case ir.IterSlice:
		items = append(items, it.(*interp.ArrVal).Items...)
	case ir.IterMap:
		items = append(items, it.(*interp.MapVal).Keys()...)
	case ir.IterString:
		for _, r := range string(it.(interp.StrVal)) {
			items = append(items, interp.StrVal(string(r)))
		}
	case ir.IterRange:
		rv := *it.(*interp.RangeVal)
		for i := rv.Lo; i < rv.Hi; i++ {
			items = append(items, interp.IntVal(i))
		}
	default:
		return step{}, fmt.Errorf("该类型不可迭代")
	}
	for _, item := range items {
		body := newFrame(f)
		body.vars[x.Var] = valPtr(item)
		s, err := m.execStmts(x.Body, body)
		if err != nil {
			return s, err
		}
		if s.flow == flowBreak {
			break
		}
	}
	return step{}, nil
}

// —— 赋值：槽位接口（EVAL-ASSIGN-003 地址组件先于右值） ——

type assignSlot interface {
	read() interp.Value
	write(v interp.Value)
}

type localSlot struct{ p *interp.Value }

func (s localSlot) read() interp.Value   { return *s.p }
func (s localSlot) write(v interp.Value) { *s.p = v }

type fieldSlot struct {
	sv  *interp.StructVal
	idx int
}

func (s fieldSlot) read() interp.Value   { return s.sv.Fields[s.idx] }
func (s fieldSlot) write(v interp.Value) { s.sv.Fields[s.idx] = v }

type arrSlot struct {
	arr *interp.ArrVal
	idx int
}

func (s arrSlot) read() interp.Value   { return s.arr.Items[s.idx] }
func (s arrSlot) write(v interp.Value) { s.arr.Items[s.idx] = v }

type mapSlot struct {
	mm *interp.MapVal
	k  interp.Value
}

func (s mapSlot) read() interp.Value {
	v, ok := s.mm.Get(s.k)
	if !ok {
		return interp.UnitVal{}
	}
	return v
}
func (s mapSlot) write(v interp.Value) { _ = s.mm.Set(s.k, v) }

func (m *Machine) evalSlot(tgt ir.Expr, f *frame) (assignSlot, error) {
	switch t := tgt.(type) {
	case *ir.LocalGet:
		p := f.slot(t.Local)
		if p == nil {
			if gp := m.globalSlot(t.Local); gp != nil {
				return localSlot{p: *gp}, nil
			}
			return nil, fmt.Errorf("未定义变量 %s", t.Local.Name)
		}
		return localSlot{p: *p}, nil
	case *ir.Field:
		ov, err := m.eval(t.Obj, f)
		if err != nil {
			return nil, err
		}
		sv, err := asStruct(derefVal(ov))
		if err != nil {
			return nil, err
		}
		for i, fname := range sv.Def.Fields {
			if fname == t.Name {
				return fieldSlot{sv: sv, idx: i}, nil
			}
		}
		return nil, fmt.Errorf("%s 没有字段 %s", sv.Def.Name, t.Name)
	case *ir.Index:
		ov, err := m.eval(t.Obj, f)
		if err != nil {
			return nil, err
		}
		iv, err := m.eval(t.Idx, f)
		if err != nil {
			return nil, err
		}
		switch c := ov.(type) {
		case *interp.ArrVal:
			i, err := intIndex(iv, len(c.Items))
			if err != nil {
				return nil, err
			}
			return arrSlot{arr: c, idx: i}, nil
		case *interp.MapVal:
			if _, ok := keyOK(iv); !ok {
				return nil, fmt.Errorf("映射键须为 int/string/bool")
			}
			return mapSlot{mm: c, k: iv}, nil
		}
		return nil, fmt.Errorf("该类型不可索引赋值")
	}
	return nil, fmt.Errorf("赋值目标不合法")
}

func (m *Machine) execAssign(x *ir.Assign, f *frame) (step, error) {
	slot, err := m.evalSlot(x.Target, f)
	if err != nil {
		return step{}, err
	}
	v, err := m.eval(x.Val, f)
	if err != nil {
		return step{}, err
	}
	if x.Op == ir.OpAssign {
		slot.write(copyVal(v))
		return step{}, nil
	}
	// 复合赋值：读旧值 → 运算 → 写回。目标读出数组本身且为 += 时是原位拼接
	// （push 语义，别名可见）；数组元素/其他类型走读改写
	if x.Op == ir.OpPlusA {
		if arr, ok := slot.read().(*interp.ArrVal); ok {
			add, ok := v.(*interp.ArrVal)
			if !ok {
				return step{}, fmt.Errorf("数组 += 需要数组，得到 %s", typeName(v))
			}
			arr.Items = append(arr.Items, add.Items...)
			return step{}, nil
		}
	}
	combined, err := m.arith(binOpOf(x.Op), slot.read(), v)
	if err != nil {
		return step{}, err
	}
	slot.write(combined)
	return step{}, nil
}

// —— 表达式求值 ——

func (m *Machine) eval(e ir.Expr, f *frame) (interp.Value, error) {
	switch x := e.(type) {
	case *ir.Const:
		switch x.Kind {
		case ir.KInt:
			return interp.IntVal(x.I64), nil
		case ir.KFloat:
			return interp.FloatVal(x.F64), nil
		case ir.KStr:
			return interp.StrVal(x.S), nil
		case ir.KBool:
			return interp.BoolVal(x.B), nil
		}
	case *ir.LocalGet:
		p := f.slot(x.Local)
		if p == nil {
			if gp := m.globalSlot(x.Local); gp != nil {
				return **gp, nil
			}
			return nil, fmt.Errorf("未定义变量 %s", x.Local.Name)
		}
		return **p, nil
	case *ir.FnGet:
		if fn, ok := m.fns[x.Name]; ok {
			return &irFnVal{fn: fn, name: x.Name}, nil
		}
		return nil, fmt.Errorf("未注册函数 %s", x.Name)
	case *ir.BuiltinGet:
		return &irBuiltinVal{name: x.Name}, nil
	case *ir.Ctor:
		args, err := m.evalArgs(x.Args, f)
		if err != nil {
			return nil, err
		}
		ci, ok := m.ctors[x.Sym.Name]
		if !ok {
			return nil, fmt.Errorf("未知构造器 %s", x.Sym.Name)
		}
		return &interp.EnumVal{Def: ci.def, Case: ci.idx, Payloads: args}, nil
	case *ir.Call:
		return m.evalCall(x, f)
	case *ir.Unary:
		v, err := m.eval(x.X, f)
		if err != nil {
			return nil, err
		}
		switch x.Op {
		case ir.OpNeg:
			switch n := v.(type) {
			case interp.IntVal:
				return -n, nil
			case interp.FloatVal:
				return -n, nil
			}
			return nil, fmt.Errorf("一元 - 不适用于该类型")
		case ir.OpNot:
			return interp.BoolVal(!truthy(v)), nil
		case ir.OpRef:
			// &x（VALUE-REF-003）：仅 LocalGet 有稳定槽位
			if lg, ok := x.X.(*ir.LocalGet); ok {
				if p := f.slot(lg.Local); p != nil {
					return &interp.PtrVal{Cell: *p}, nil
				}
			}
			return nil, fmt.Errorf("& 仅可作用于变量")
		}
		return nil, fmt.Errorf("irinterp: 未支持的一元运算 %s", string(x.Op))
	case *ir.Binary:
		lv, err := m.eval(x.L, f)
		if err != nil {
			return nil, err
		}
		// EVAL-SHORTCUT-006
		if x.Op == ir.OpAndAnd {
			if !truthy(lv) {
				return interp.BoolVal(false), nil
			}
			rv, err := m.eval(x.R, f)
			if err != nil {
				return nil, err
			}
			return interp.BoolVal(truthy(rv)), nil
		}
		if x.Op == ir.OpOrOr {
			if truthy(lv) {
				return interp.BoolVal(true), nil
			}
			rv, err := m.eval(x.R, f)
			if err != nil {
				return nil, err
			}
			return interp.BoolVal(truthy(rv)), nil
		}
		rv, err := m.eval(x.R, f)
		if err != nil {
			return nil, err
		}
		return m.evalBinary(x.Op, lv, rv)
	case *ir.Range:
		lo, err := m.eval(x.Lo, f)
		if err != nil {
			return nil, err
		}
		hi, err := m.eval(x.Hi, f)
		if err != nil {
			return nil, err
		}
		return &interp.RangeVal{Lo: int64(lo.(interp.IntVal)), Hi: int64(hi.(interp.IntVal))}, nil
	case *ir.Index:
		ov, err := m.eval(x.Obj, f)
		if err != nil {
			return nil, err
		}
		iv, err := m.eval(x.Idx, f)
		if err != nil {
			return nil, err
		}
		switch c := ov.(type) {
		case *interp.ArrVal:
			i, err := intIndex(iv, len(c.Items))
			if err != nil {
				return nil, err
			}
			return c.Items[i], nil
		case *interp.MapVal:
			v, ok := c.Get(iv)
			if !ok {
				return nil, fmt.Errorf("映射无键 %s（用 has() 探测）", interp.Fmt(iv))
			}
			return v, nil
		case interp.StrVal:
			i, err := intIndex(iv, len([]rune(string(c))))
			if err != nil {
				return nil, err
			}
			return interp.StrVal(string([]rune(string(c))[i])), nil
		}
		return nil, fmt.Errorf("该类型不可索引")
	case *ir.Slice:
		ov, err := m.eval(x.Obj, f)
		if err != nil {
			return nil, err
		}
		loV, err := m.eval(x.Lo, f)
		if err != nil {
			return nil, err
		}
		hiV, err := m.eval(x.Hi, f)
		if err != nil {
			return nil, err
		}
		lo, hi := int64(loV.(interp.IntVal)), int64(hiV.(interp.IntVal))
		switch c := ov.(type) {
		case *interp.ArrVal:
			if lo < 0 || hi > int64(len(c.Items)) || lo > hi {
				return nil, fmt.Errorf("切片区间 [%d,%d) 越界（长度 %d）", lo, hi, len(c.Items))
			}
			return &interp.ArrVal{Items: append([]interp.Value(nil), c.Items[lo:hi]...)}, nil
		case interp.StrVal:
			rs := []rune(string(c))
			if lo < 0 || hi > int64(len(rs)) || lo > hi {
				return nil, fmt.Errorf("切片区间 [%d,%d) 越界（长度 %d）", lo, hi, len(rs))
			}
			return interp.StrVal(string(rs[lo:hi])), nil
		}
		return nil, fmt.Errorf("该类型不可切片")
	case *ir.Field:
		ov, err := m.eval(x.Obj, f)
		if err != nil {
			return nil, err
		}
		sv, err := asStruct(derefVal(ov))
		if err != nil {
			return nil, err
		}
		for i, fname := range sv.Def.Fields {
			if fname == x.Name {
				return sv.Fields[i], nil
			}
		}
		return nil, fmt.Errorf("%s 没有字段 %s", sv.Def.Name, x.Name)
	case *ir.Try:
		v, err := m.eval(x.X, f)
		if err != nil {
			return nil, err
		}
		ev := v.(*interp.EnumVal)
		if ev.Def.Name == "Result" && ev.Case == 0 {
			return ev.Payloads[0], nil
		}
		// Err(e)：传播出当前函数（RESULT-TRY-003 函数边界合成 Err 返回）
		return nil, &tryErr{val: ev}
	case *ir.StructLit:
		def, ok := m.structs[structName(x.Struct)]
		if !ok {
			return nil, fmt.Errorf("未知 struct %s", structName(x.Struct))
		}
		vals := map[string]interp.Value{}
		for _, fv := range x.Fields {
			v, err := m.eval(fv.Val, f)
			if err != nil {
				return nil, err
			}
			vals[fv.Name] = v
		}
		fields := make([]interp.Value, len(def.Fields))
		for i, fname := range def.Fields {
			fields[i] = vals[fname]
		}
		return &interp.StructVal{Def: def, Fields: fields}, nil
	case *ir.MapLit:
		mm := &interp.MapVal{}
		for i := range x.Keys {
			k, err := m.eval(x.Keys[i], f)
			if err != nil {
				return nil, err
			}
			v, err := m.eval(x.Vals[i], f)
			if err != nil {
				return nil, err
			}
			if err := mm.Set(k, v); err != nil {
				return nil, err
			}
		}
		return mm, nil
	case *ir.SliceLit:
		items := make([]interp.Value, len(x.Elems))
		for i, el := range x.Elems {
			v, err := m.eval(el, f)
			if err != nil {
				return nil, err
			}
			items[i] = v
		}
		return &interp.ArrVal{Items: items}, nil
	case *ir.Match:
		return m.evalMatch(x, f)
	case *ir.FnLit:
		return &irFnVal{lit: x, env: f}, nil
	}
	return nil, fmt.Errorf("irinterp: 未支持的表达式 %T", e)
}

// —— 调用 ——

type irFnVal struct {
	fn   *ir.Fn    // 顶层 fn / 方法
	lit  *ir.FnLit // 匿名函数
	env  *frame
	name string
}

type irBuiltinVal struct{ name string }

func (m *Machine) evalCall(x *ir.Call, f *frame) (interp.Value, error) {
	switch {
	case x.Builtin != "":
		args, err := m.evalArgs(x.Args, f)
		if err != nil {
			return nil, err
		}
		return m.callBuiltin(x.Builtin, args)
	case x.Method != nil:
		recv, err := m.eval(x.Recv, f)
		if err != nil {
			return nil, err
		}
		sv, err := asStruct(recv)
		if err != nil {
			return nil, err
		}
		fn := m.methods[sv.Def.Name+"."+x.Method.Name]
		if fn == nil {
			return nil, fmt.Errorf("%s 没有方法 %s", sv.Def.Name, x.Method.Name)
		}
		args, err := m.evalArgs(x.Args, f)
		if err != nil {
			return nil, err
		}
		return m.callFn(fn, recv, args)
	case x.Fn != nil:
		args, err := m.evalArgs(x.Args, f)
		if err != nil {
			return nil, err
		}
		fn := m.fns[x.Fn.Name]
		if fn == nil {
			if nested := f.lookupFn(x.Fn.Name); nested != nil {
				return m.callFn(nested, nil, args)
			}
			return nil, fmt.Errorf("未注册函数 %s", x.Fn.Name)
		}
		return m.callFn(fn, nil, args)
	default:
		cv, err := m.eval(x.Callee, f)
		if err != nil {
			return nil, err
		}
		args, err := m.evalArgs(x.Args, f)
		if err != nil {
			return nil, err
		}
		switch fv := cv.(type) {
		case *irFnVal:
			return m.callFnVal(fv, args)
		case *irBuiltinVal:
			return m.callBuiltin(fv.name, args)
		}
		return nil, fmt.Errorf("该值不可调用")
	}
}

func (f *frame) lookupFn(name string) *ir.Fn {
	for cur := f; cur != nil; cur = cur.parent {
		if fn, ok := cur.fns[name]; ok {
			return fn
		}
	}
	return nil
}

func (m *Machine) evalArgs(args []ir.Expr, f *frame) ([]interp.Value, error) {
	out := make([]interp.Value, len(args))
	for i, a := range args {
		v, err := m.eval(a, f)
		if err != nil {
			return nil, err
		}
		out[i] = v
	}
	return out, nil
}

func (m *Machine) callBuiltin(name string, args []interp.Value) (interp.Value, error) {
	switch name {
	case "println":
		_, err := fmt.Fprintln(m.Out, m.fmtArgs(args))
		return interp.UnitVal{}, err
	case "print":
		_, err := fmt.Fprint(m.Out, m.fmtArgs(args))
		return interp.UnitVal{}, err
	}
	v, found, err := m.host.CallBuiltin(name, args)
	if err != nil {
		return nil, err
	}
	if !found {
		return nil, fmt.Errorf("irinterp: 未支持的内建 %s", name)
	}
	return v, nil
}

func (m *Machine) callFn(fn *ir.Fn, recv interp.Value, args []interp.Value) (interp.Value, error) {
	if fn.Extern {
		return nil, fmt.Errorf("extern 函数仅转译可用")
	}
	f := newFrame(m.globals)
	if fn.Recv != nil {
		f.vars[fn.Recv] = valPtr(copyVal(recv))
	}
	for i, p := range fn.Params {
		var v interp.Value = interp.UnitVal{}
		if i < len(args) {
			v = copyVal(args[i])
		}
		f.vars[p] = valPtr(v)
	}
	return m.execBody(fn.Body, f)
}

func (m *Machine) callFnVal(fv *irFnVal, args []interp.Value) (interp.Value, error) {
	switch {
	case fv.fn != nil:
		return m.callFn(fv.fn, nil, args)
	case fv.lit != nil:
		f := newFrame(fv.env)
		// VALUE-CAPTURE-004 按引用捕获：Locals 中已存在于捕获链的槽位直接复用
		for _, l := range fv.lit.Locals {
			if p := fv.env.slot(l); p != nil {
				f.vars[l] = *p
			}
		}
		for i, p := range fv.lit.Params {
			var v interp.Value = interp.UnitVal{}
			if i < len(args) {
				v = copyVal(args[i])
			}
			f.vars[p] = valPtr(v)
		}
		return m.execBody(fv.lit.Body, f)
	}
	return nil, fmt.Errorf("空函数值不可调用")
}

// execBody 执行函数体；Err 传播（tryErr）到函数边界合成返回值（RESULT-TRY-003）
func (m *Machine) execBody(body []ir.Stmt, f *frame) (interp.Value, error) {
	s, err := m.execStmts(body, f)
	if err != nil {
		if te, ok := err.(*tryErr); ok {
			return te.val, nil
		}
		return nil, err
	}
	if s.flow == flowReturn {
		return s.val, nil
	}
	return interp.UnitVal{}, nil
}

// —— match ——

func (m *Machine) evalMatch(x *ir.Match, f *frame) (interp.Value, error) {
	scrut, err := m.eval(x.Scrut, f)
	if err != nil {
		return nil, err
	}
	for _, arm := range x.Arms {
		bind := newFrame(f)
		if !m.matchPat(arm.Pat, scrut, bind) {
			continue
		}
		return m.eval(arm.Body, bind)
	}
	return nil, fmt.Errorf("match 无匹配分支")
}

func (m *Machine) matchPat(p ir.Pat, v interp.Value, bind *frame) bool {
	switch pat := p.(type) {
	case *ir.WildPat:
		return true
	case *ir.BindPat:
		if pat.Local != nil {
			bind.vars[pat.Local] = valPtr(v)
		}
		return true
	case *ir.LitPat:
		lv, err := m.eval(pat.Val, bind)
		if err != nil {
			return false
		}
		return eqValues(lv, v)
	case *ir.CtorPat:
		ev, ok := v.(*interp.EnumVal)
		if !ok || ev.Def.Cases[ev.Case] != pat.Sym.Name {
			return false
		}
		for i, sub := range pat.Subs {
			if i >= len(ev.Payloads) {
				return false
			}
			if !m.matchPat(sub, ev.Payloads[i], bind) {
				return false
			}
		}
		return true
	}
	return false
}

// —— 运算 ——

func (m *Machine) evalBinary(op ir.Op, lv, rv interp.Value) (interp.Value, error) {
	switch op {
	case ir.OpAndAnd, ir.OpOrOr:
		return interp.BoolVal(truthy(lv) || truthy(rv)), nil // 调用方已短路，此分支不达
	case ir.OpEq:
		return interp.BoolVal(eqValues(lv, rv)), nil
	case ir.OpNeq:
		return interp.BoolVal(!eqValues(lv, rv)), nil
	case ir.OpLt, ir.OpLe, ir.OpGt, ir.OpGe:
		return m.compare(op, lv, rv)
	}
	return m.arith(op, lv, rv)
}

func (m *Machine) arith(op ir.Op, lv, rv interp.Value) (interp.Value, error) {
	switch a := lv.(type) {
	case interp.IntVal:
		b, ok := rv.(interp.IntVal)
		if !ok {
			return nil, fmt.Errorf("int 与 float 不可混合运算（无隐式转换）")
		}
		switch op {
		case ir.OpPlus, ir.OpPlusA:
			return a + b, nil
		case ir.OpMinus, ir.OpMinusA:
			return a - b, nil
		case ir.OpStar, ir.OpStarA:
			return a * b, nil
		case ir.OpSlash, ir.OpSlashA:
			if b == 0 {
				return nil, fmt.Errorf("整数除零")
			}
			return a / b, nil
		case ir.OpPercent:
			if b == 0 {
				return nil, fmt.Errorf("整数模零")
			}
			return a % b, nil
		case ir.OpAnd:
			return a & b, nil
		case ir.OpBitOr:
			return a | b, nil
		case ir.OpXor:
			return a ^ b, nil
		case ir.OpShl:
			return a << b, nil
		case ir.OpShr:
			return a >> b, nil
		}
	case interp.FloatVal:
		b, ok := rv.(interp.FloatVal)
		if !ok {
			return nil, fmt.Errorf("int 与 float 不可混合运算（无隐式转换）")
		}
		switch op {
		case ir.OpPlus, ir.OpPlusA:
			return a + b, nil
		case ir.OpMinus, ir.OpMinusA:
			return a - b, nil
		case ir.OpStar, ir.OpStarA:
			return a * b, nil
		case ir.OpSlash, ir.OpSlashA:
			return a / b, nil
		}
	case interp.StrVal:
		b, ok := rv.(interp.StrVal)
		if !ok {
			return nil, fmt.Errorf("string 只能与 string 拼接")
		}
		if op == ir.OpPlus || op == ir.OpPlusA {
			return a + b, nil
		}
	}
	return nil, fmt.Errorf("运算不适用于该类型")
}

func (m *Machine) compare(op ir.Op, lv, rv interp.Value) (interp.Value, error) {
	switch a := lv.(type) {
	case interp.IntVal:
		if b, ok := rv.(interp.IntVal); ok {
			switch op {
			case ir.OpLt:
				return interp.BoolVal(a < b), nil
			case ir.OpLe:
				return interp.BoolVal(a <= b), nil
			case ir.OpGt:
				return interp.BoolVal(a > b), nil
			case ir.OpGe:
				return interp.BoolVal(a >= b), nil
			}
		}
	case interp.FloatVal:
		if b, ok := rv.(interp.FloatVal); ok {
			switch op {
			case ir.OpLt:
				return interp.BoolVal(a < b), nil
			case ir.OpLe:
				return interp.BoolVal(a <= b), nil
			case ir.OpGt:
				return interp.BoolVal(a > b), nil
			case ir.OpGe:
				return interp.BoolVal(a >= b), nil
			}
		}
	case interp.StrVal:
		if b, ok := rv.(interp.StrVal); ok {
			switch op {
			case ir.OpLt:
				return interp.BoolVal(a < b), nil
			case ir.OpLe:
				return interp.BoolVal(a <= b), nil
			case ir.OpGt:
				return interp.BoolVal(a > b), nil
			case ir.OpGe:
				return interp.BoolVal(a >= b), nil
			}
		}
	}
	return nil, fmt.Errorf("该类型不可比较")
}

// —— 辅助 ——

func (m *Machine) fmtArgs(args []interp.Value) string {
	parts := make([]string, len(args))
	for i, a := range args {
		parts[i] = interp.Fmt(a)
	}
	return strings.Join(parts, " ")
}

func truthy(v interp.Value) bool {
	b, ok := v.(interp.BoolVal)
	return ok && bool(b)
}

// derefVal 解引用（&x 的 PtrVal 透明化——VALUE-REF-003 引用读写穿透）
func derefVal(v interp.Value) interp.Value {
	if p, ok := v.(*interp.PtrVal); ok {
		return *p.Cell
	}
	return v
}

func asStruct(v interp.Value) (*interp.StructVal, error) {
	if sv, ok := v.(*interp.StructVal); ok {
		return sv, nil
	}
	return nil, fmt.Errorf("该值不是 struct（是 %s）", typeName(v))
}

func intIndex(v interp.Value, length int) (int, error) {
	i, ok := v.(interp.IntVal)
	if !ok {
		return 0, fmt.Errorf("索引须为 int")
	}
	n := int(i)
	if n < 0 || n >= length {
		return 0, fmt.Errorf("索引 %d 越界（长度 %d）", n, length)
	}
	return n, nil
}

func keyOK(v interp.Value) (string, bool) {
	switch x := v.(type) {
	case interp.IntVal:
		return fmt.Sprintf("i:%d", int64(x)), true
	case interp.StrVal:
		return "s:" + string(x), true
	case interp.BoolVal:
		return fmt.Sprintf("b:%v", bool(x)), true
	}
	return "", false
}

func eqValues(a, b interp.Value) bool {
	switch x := a.(type) {
	case interp.IntVal:
		y, ok := b.(interp.IntVal)
		return ok && x == y
	case interp.FloatVal:
		y, ok := b.(interp.FloatVal)
		return ok && x == y
	case interp.BoolVal:
		y, ok := b.(interp.BoolVal)
		return ok && x == y
	case interp.StrVal:
		y, ok := b.(interp.StrVal)
		return ok && x == y
	case *interp.EnumVal:
		y, ok := b.(*interp.EnumVal)
		if !ok || x.Def.Name != y.Def.Name || x.Case != y.Case || len(x.Payloads) != len(y.Payloads) {
			return false
		}
		for i := range x.Payloads {
			if !eqValues(x.Payloads[i], y.Payloads[i]) {
				return false
			}
		}
		return true
	}
	return false
}

// copyVal 值语义（VALUE-COPY-001）：struct/enum 递归拷贝，容器共享
func copyVal(v interp.Value) interp.Value {
	switch x := v.(type) {
	case *interp.StructVal:
		fs := make([]interp.Value, len(x.Fields))
		for i, fv := range x.Fields {
			fs[i] = copyVal(fv)
		}
		return &interp.StructVal{Def: x.Def, Fields: fs}
	case *interp.EnumVal:
		ps := make([]interp.Value, len(x.Payloads))
		for i, p := range x.Payloads {
			ps[i] = copyVal(p)
		}
		return &interp.EnumVal{Def: x.Def, Case: x.Case, Payloads: ps}
	}
	return v
}

func typeName(v interp.Value) string {
	switch v.(type) {
	case interp.IntVal:
		return "int"
	case interp.FloatVal:
		return "float"
	case interp.BoolVal:
		return "bool"
	case interp.StrVal:
		return "string"
	case *interp.ArrVal:
		return "数组"
	case *interp.MapVal:
		return "映射"
	case *interp.StructVal:
		return "struct"
	case *interp.EnumVal:
		return "enum"
	}
	return "未知值"
}

type tryErr struct{ val interp.Value }

func (e *tryErr) Error() string { return "Err 传播" }

func structName(sym *semantic.Symbol) string {
	if sym == nil {
		return ""
	}
	return sym.Name
}

func binOpOf(op ir.Op) ir.Op {
	switch op {
	case ir.OpPlusA:
		return ir.OpPlus
	case ir.OpMinusA:
		return ir.OpMinus
	case ir.OpStarA:
		return ir.OpStar
	case ir.OpSlashA:
		return ir.OpSlash
	}
	return op
}

func valPtr(v interp.Value) *interp.Value { return &v }
