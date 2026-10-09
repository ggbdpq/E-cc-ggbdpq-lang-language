package interp

import (
	"errors"
	"fmt"
	"io"
	"strings"

	"lang-go/internal/syntax"
)

// sigKind 控制流信号：用 error 通道承载，调用/循环边界捕获.
type sigKind int

const (
	sigReturn sigKind = iota
	sigBreak
	sigContinue
	sigErr // ? 传播的 Err 值
)

type signal struct {
	kind sigKind
	val  Value
}

func (s *signal) Error() string {
	switch s.kind {
	case sigReturn:
		return "return 出现在函数之外"
	case sigBreak:
		return "break 出现在循环之外"
	case sigContinue:
		return "continue 出现在循环之外"
	default:
		return "Err 传播出函数之外：" + fmtVal(s.val, true)
	}
}

func asSignal(err error) (*signal, bool) {
	var s *signal
	if errors.As(err, &s) {
		return s, true
	}
	return nil, false
}

type ctor struct {
	def   *EnumDef
	idx   int
	arity int
}

// Interp 树遍历解释器。REPL 场景下可反复 EvalChunk，环境持久。
type Interp struct {
	Out     io.Writer
	Globals *Env

	structs map[string]*StructDef
	enums   map[string]*EnumDef
	ctors   map[string]*ctor
}

func New(out io.Writer) *Interp {
	it := &Interp{
		Out:     out,
		Globals: newEnv(nil),
		structs: map[string]*StructDef{},
		enums:   map[string]*EnumDef{},
		ctors:   map[string]*ctor{},
	}
	it.prelude()
	it.preludeStdlib()
	return it
}

func (it *Interp) errf(line int, format string, args ...any) error {
	return fmt.Errorf("第%d行: %s", line, fmt.Sprintf(format, args...))
}

// Run 执行整个程序；定义了 main 则随后调用.
func (it *Interp) Run(prog []syntax.Stmt) error {
	if err := it.execStmts(prog, it.Globals); err != nil {
		return err
	}
	if c, ok := it.Globals.lookup("main"); ok {
		if cl, ok := c.val.(*Closure); ok {
			if _, err := it.callClosure(cl, nil); err != nil {
				return err
			}
		}
	}
	return nil
}

// EvalChunk 执行 REPL 片段，返回最后一个表达式语句的值.
func (it *Interp) EvalChunk(stmts []syntax.Stmt) (Value, bool, error) {
	var last Value
	has := false
	for _, s := range stmts {
		if es, ok := s.(*syntax.ExprStmt); ok {
			v, err := it.eval(es.X, it.Globals)
			if err != nil {
				return nil, false, err
			}
			last, has = v, true
			continue
		}
		if err := it.execStmt(s, it.Globals); err != nil {
			return nil, false, err
		}
	}
	return last, has, nil
}

// CallByName 调用全局零参函数（langGo test 运行器用）.
func (it *Interp) CallByName(name string) error {
	c, ok := it.Globals.lookup(name)
	if !ok {
		return fmt.Errorf("未定义 %s", name)
	}
	cl, ok := c.val.(*Closure)
	if !ok {
		return fmt.Errorf("%s 不是函数", name)
	}
	_, err := it.callClosure(cl, nil)
	return err
}

// —— 语句 ——

func (it *Interp) execStmts(stmts []syntax.Stmt, env *Env) error {
	for _, s := range stmts {
		if err := it.execStmt(s, env); err != nil {
			return err
		}
	}
	return nil
}

func (it *Interp) execStmt(s syntax.Stmt, env *Env) error {
	switch st := s.(type) {
	case *syntax.LetStmt:
		v, err := it.eval(st.Init, env)
		if err != nil {
			return err
		}
		if st.Name == "_" {
			return nil
		}
		return env.define(st.Name, copyValue(v), st.Mutable)

	case *syntax.AssignStmt:
		return it.assign(st, env)

	case *syntax.ExprStmt:
		_, err := it.eval(st.X, env)
		return err

	case *syntax.ReturnStmt:
		var v Value = UnitVal{}
		if st.Val != nil {
			av, err := it.eval(st.Val, env)
			if err != nil {
				return err
			}
			v = copyValue(av)
		}
		return &signal{kind: sigReturn, val: v}

	case *syntax.IfStmt:
		t, err := it.cond(st.Cond, env)
		if err != nil {
			return err
		}
		if t {
			return it.execStmts(st.Then, newEnv(env))
		}
		if st.Else != nil {
			return it.execStmts(st.Else, newEnv(env))
		}
		return nil

	case *syntax.WhileStmt:
		for {
			t, err := it.cond(st.Cond, env)
			if err != nil {
				return err
			}
			if !t {
				return nil
			}
			stopped, err := it.execLoopBody(st.Body, newEnv(env))
			if err != nil {
				return err
			}
			if stopped {
				return nil
			}
		}

	case *syntax.ForInStmt:
		return it.execForIn(st, env)

	case *syntax.BreakStmt:
		return &signal{kind: sigBreak}

	case *syntax.ContinueStmt:
		return &signal{kind: sigContinue}

	case *syntax.FnDecl:
		return it.declFn(st, env)

	case *syntax.StructDecl:
		return it.declStruct(st)

	case *syntax.EnumDecl:
		return it.declEnum(st)

	case *syntax.ExternDecl:
		// extern 声明：解释器一律注册为"仅转译可用"桩（decisions #36）
		for _, fn := range st.Fns {
			name := fn.Name
			it.Globals.redefine(name, &Builtin{Name: name, Fn: func(it *Interp, args []Value) (Value, error) {
				return nil, fmt.Errorf("extern 函数 %s 仅转译可用（宿主 %s）", name, st.Host)
			}})
		}
		return nil

	case *syntax.ImportStmt:
		return it.errf(st.Line, "import 由装载器在合并前展开（不应执行到这里）")

	case *syntax.GoStmt:
		// 单线程解释器：go 同步立即执行（语义子集，输出确定；decisions #57）
		_, err := it.evalCall(st.Call, env)
		return err
	}
	return it.errf(s.Pos(), "未知语句")
}

// execLoopBody 执行循环体并消化 break/continue；return/err 信号向上抛
// execLoopBody 执行循环体：返回 breaked=true 表示体内 break（循环应终止）.
func (it *Interp) execLoopBody(body []syntax.Stmt, env *Env) (bool, error) {
	err := it.execStmts(body, env)
	if sig, ok := asSignal(err); ok {
		switch sig.kind {
		case sigBreak:
			return true, nil
		case sigContinue:
			return false, nil
		}
	}
	return false, err
}

func (it *Interp) cond(e syntax.Expr, env *Env) (bool, error) {
	v, err := it.eval(e, env)
	if err != nil {
		return false, err
	}
	return truthy(v)
}

func (it *Interp) execForIn(st *syntax.ForInStmt, env *Env) error {
	iter, err := it.eval(st.Iter, env)
	if err != nil {
		return err
	}
	var items []Value
	switch x := iter.(type) {
	case *ArrVal:
		items = x.Items
	case *MapVal:
		items = x.Keys()
	case StrVal:
		for _, r := range string(x) {
			items = append(items, StrVal(string(r)))
		}
	case *RangeVal:
		for i := x.Lo; i < x.Hi; i++ {
			items = append(items, IntVal(i))
		}
	default:
		return it.errf(st.Line, "%s 不可迭代", typeName(iter))
	}
	for _, item := range items {
		benv := newEnv(env)
		if st.Var != "_" {
			if err := benv.define(st.Var, copyValue(item), false); err != nil {
				return err
			}
		}
		stopped, err := it.execLoopBody(st.Body, benv)
		if err != nil {
			return err
		}
		if stopped {
			break
		}
	}
	return nil
}

func (it *Interp) declFn(st *syntax.FnDecl, env *Env) error {
	if st.Recv != nil {
		nt, ok := st.Recv.Typ.(*syntax.NamedType)
		if !ok {
			return it.errf(st.Line, "方法接收者须为具名 struct 类型")
		}
		def, ok := it.structs[nt.Name]
		if !ok {
			return it.errf(st.Line, "为未知 struct %s 定义方法", nt.Name)
		}
		def.Methods[st.Name] = &Closure{Name: st.Name, Decl: st, Env: env}
		return nil
	}
	env.redefine(st.Name, &Closure{Name: st.Name, Decl: st, Env: env})
	return nil
}

func (it *Interp) declStruct(st *syntax.StructDecl) error {
	fields := make([]string, len(st.Fields))
	for i, f := range st.Fields {
		fields[i] = f.Name
	}
	_, err := it.registerStruct(st.Name, fields, st.Line)
	return err
}

func (it *Interp) declEnum(st *syntax.EnumDecl) error {
	cases := make([]string, len(st.Cases))
	for i, c := range st.Cases {
		cases[i] = c.Name
	}
	def, err := it.registerEnum(st.Name, cases, st.Line)
	if err != nil {
		return err
	}
	for i, c := range st.Cases {
		it.ctors[c.Name] = &ctor{def: def, idx: i, arity: len(c.Payloads)}
	}
	return nil
}

func (it *Interp) registerStruct(name string, fields []string, line int) (*StructDef, error) {
	if _, exists := it.structs[name]; exists {
		return nil, it.errf(line, "struct %s 重复定义", name)
	}
	def := &StructDef{Name: name, Fields: fields, Methods: map[string]*Closure{}}
	it.structs[name] = def
	return def, nil
}

func (it *Interp) registerEnum(name string, cases []string, line int) (*EnumDef, error) {
	if _, exists := it.enums[name]; exists {
		return nil, it.errf(line, "enum %s 重复定义", name)
	}
	for _, c := range cases {
		if _, clash := it.ctors[c]; clash {
			return nil, it.errf(line, "构造器 %s 重名", c)
		}
		if _, used := it.Globals.lookup(c); used {
			return nil, it.errf(line, "case %s 与内建名冲突", c)
		}
	}
	def := &EnumDef{Name: name, Cases: cases}
	it.enums[name] = def
	return def, nil
}

// assignAddr 预求的赋值目标地址。
// 求值顺序（specs/expressions.md EVAL-ASSIGN-001）：目标地址组件先于右侧值求值
// （左到右），与 →Go/→JS 后端一致。
type assignAddr struct {
	cell      *cell  // Ident：绑定单元格
	vname     string // Ident：绑定名（错误消息用）
	obj       Value  // FieldEx/IndexEx：容器值（已解引用）
	name      string // FieldEx：字段名
	idx       Value  // IndexEx：索引值
	line      int
	writeBack func() // 非空：容器元素读改写后回写槽位（VALUE-ELEM-009）
}

// evalAssignAddr 求值赋值目标的地址组件（不做写入）.
func (it *Interp) evalAssignAddr(tgt syntax.Expr, env *Env) (*assignAddr, error) {
	line := 0
	if sp, ok := tgt.(interface{ Line() int }); ok {
		line = sp.Line()
	}
	switch t := tgt.(type) {
	case *syntax.Ident:
		c, ok := env.lookup(t.Name)
		if !ok {
			return nil, it.errf(line, "未定义变量 %s", t.Name)
		}
		return &assignAddr{cell: c, vname: t.Name, line: line}, nil
	case *syntax.FieldEx:
		// 容器元素字段链 xs[i].f = v：读元素副本 → 改字段 → 回写槽位（VALUE-ELEM-009）
		if idxEx, ok := t.Obj.(*syntax.IndexEx); ok {
			ov, err := it.eval(idxEx.Obj, env)
			if err != nil {
				return nil, err
			}
			if p, ok := ov.(*PtrVal); ok {
				ov = *p.Cell
			}
			arr, ok := ov.(*ArrVal)
			if !ok {
				return nil, it.errf(line, "只能对数组元素字段赋值，得到 %s", typeName(ov))
			}
			iv, err := it.eval(idxEx.Idx, env)
			if err != nil {
				return nil, err
			}
			i, err := it.intIndex(iv, len(arr.Items), line)
			if err != nil {
				return nil, err
			}
			elem := copyValue(arr.Items[i])
			if _, ok := elem.(*StructVal); !ok {
				return nil, it.errf(line, "只能给 struct 字段赋值，得到 %s", typeName(elem))
			}
			return &assignAddr{obj: elem, name: t.Name, line: line, writeBack: func() { arr.Items[i] = elem }}, nil
		}
		ov, err := it.eval(t.Obj, env)
		if err != nil {
			return nil, err
		}
		if p, ok := ov.(*PtrVal); ok {
			ov = *p.Cell
		}
		if _, ok := ov.(*StructVal); !ok {
			return nil, it.errf(line, "只能给 struct 字段赋值，得到 %s", typeName(ov))
		}
		return &assignAddr{obj: ov, name: t.Name, line: line}, nil
	case *syntax.IndexEx:
		ov, err := it.eval(t.Obj, env)
		if err != nil {
			return nil, err
		}
		if p, ok := ov.(*PtrVal); ok {
			ov = *p.Cell
		}
		iv, err := it.eval(t.Idx, env)
		if err != nil {
			return nil, err
		}
		return &assignAddr{obj: ov, idx: iv, line: line}, nil
	}
	return nil, it.errf(line, "赋值目标不合法")
}

// writeAddr 向预求地址写入（= 赋值的唯一出口）.
func (it *Interp) writeAddr(a *assignAddr, v Value) error {
	if a.cell != nil {
		if !a.cell.mutable {
			return it.errf(a.line, "%s 由 let 绑定，不可重新赋值", a.vname)
		}
		a.cell.val = v
		if a.writeBack != nil {
			a.writeBack()
		}
		return nil
	}
	switch container := a.obj.(type) {
	case *StructVal:
		idx := -1
		for i, f := range container.Def.Fields {
			if f == a.name {
				idx = i
				break
			}
		}
		if idx < 0 {
			return it.errf(a.line, "%s 没有字段 %s", container.Def.Name, a.name)
		}
		container.Fields[idx] = v
		if a.writeBack != nil {
			a.writeBack()
		}
		return nil
	case *ArrVal:
		i, err := it.intIndex(a.idx, len(container.Items), a.line)
		if err != nil {
			return err
		}
		container.Items[i] = v
		return nil
	case *MapVal:
		if err := container.Set(a.idx, v); err != nil {
			return it.errf(a.line, "%s", err)
		}
		return nil
	case StrVal:
		return it.errf(a.line, "字符串不可变")
	default:
		return it.errf(a.line, "%s 不可索引赋值", typeName(a.obj))
	}
}

// readAddr 从预求地址读取旧值（复合赋值用）.
func (it *Interp) readAddr(a *assignAddr) (Value, error) {
	if a.cell != nil {
		return a.cell.val, nil
	}
	switch container := a.obj.(type) {
	case *StructVal:
		for i, f := range container.Def.Fields {
			if f == a.name {
				return container.Fields[i], nil
			}
		}
		return nil, it.errf(a.line, "%s 没有字段 %s", container.Def.Name, a.name)
	case *ArrVal:
		i, err := it.intIndex(a.idx, len(container.Items), a.line)
		if err != nil {
			return nil, err
		}
		return container.Items[i], nil
	case *MapVal:
		if v, ok := container.Get(a.idx); ok {
			return v, nil
		}
		return nil, it.errf(a.line, "映射无键 %s（用 has() 探测）", fmtVal(a.idx, true))
	case StrVal:
		return container, nil
	default:
		return nil, it.errf(a.line, "%s 不可索引读取", typeName(a.obj))
	}
}

func (it *Interp) assign(st *syntax.AssignStmt, env *Env) error {
	addr, err := it.evalAssignAddr(st.Target, env)
	if err != nil {
		return err
	}
	v, err := it.eval(st.Val, env)
	if err != nil {
		return err
	}
	v = copyValue(v)

	// 复合赋值：读旧值运算后写回（数组 += 是原位 push，单独处理）
	if st.Op != syntax.Assign {
		return it.assignCompound(st, addr, v, env)
	}

	return it.writeAddr(addr, v)
}

// assignCompound 复合赋值（+= -= *= /=）：读旧值、运算、写回。
// 数组 += 是原位 push（引用可见），不做读改写。
func (it *Interp) assignCompound(st *syntax.AssignStmt, addr *assignAddr, v Value, env *Env) error {
	if st.Op == syntax.PlusAssign {
		cur, err := it.readAddr(addr)
		if err != nil {
			return err
		}
		if p, ok := cur.(*PtrVal); ok {
			cur = *p.Cell
		}
		if arr, ok := cur.(*ArrVal); ok {
			add, ok := v.(*ArrVal)
			if !ok {
				return it.errf(st.Line, "数组 += 需要数组，得到 %s", typeName(v))
			}
			arr.Items = append(arr.Items, add.Items...)
			return nil
		}
	}

	old, err := it.readAddr(addr)
	if err != nil {
		return err
	}
	if p, ok := old.(*PtrVal); ok {
		old = *p.Cell
	}
	combined, err := it.applyOp(st.Op, old, v, st.Line)
	if err != nil {
		return err
	}
	return it.writeAddr(addr, combined)
}

// applyOp 复合赋值的数值/字符串运算（语义与 evalBinary 的对应分支一致）.
func (it *Interp) applyOp(op syntax.Kind, old, v Value, line int) (Value, error) {
	switch op {
	case syntax.PlusAssign:
		switch a := old.(type) {
		case IntVal:
			if b, ok := v.(IntVal); ok {
				return a + b, nil
			}
		case FloatVal:
			if b, ok := v.(FloatVal); ok {
				return a + b, nil
			}
		case StrVal:
			if b, ok := v.(StrVal); ok {
				return a + b, nil
			}
		}
	case syntax.MinusAssign:
		switch a := old.(type) {
		case IntVal:
			if b, ok := v.(IntVal); ok {
				return a - b, nil
			}
		case FloatVal:
			if b, ok := v.(FloatVal); ok {
				return a - b, nil
			}
		}
	case syntax.StarAssign:
		switch a := old.(type) {
		case IntVal:
			if b, ok := v.(IntVal); ok {
				return a * b, nil
			}
		case FloatVal:
			if b, ok := v.(FloatVal); ok {
				return a * b, nil
			}
		}
	case syntax.SlashAssign:
		switch a := old.(type) {
		case IntVal:
			if b, ok := v.(IntVal); ok {
				if b == 0 {
					return nil, it.errf(line, "整数除零")
				}
				return a / b, nil
			}
		case FloatVal:
			if b, ok := v.(FloatVal); ok {
				if b == 0 {
					return nil, it.errf(line, "浮点除零")
				}
				return a / b, nil
			}
		}
	}
	return nil, it.errf(line, "%s 与 %s 不可做该运算（无隐式转换）", typeName(old), typeName(v))
}

func (it *Interp) intIndex(v Value, length, line int) (int, error) {
	i, ok := v.(IntVal)
	if !ok {
		return 0, it.errf(line, "索引须为 int，得到 %s", typeName(v))
	}
	idx := int(i)
	if idx < 0 || idx >= length {
		return 0, it.errf(line, "索引 %d 越界（长度 %d）", idx, length)
	}
	return idx, nil
}

// —— 表达式 ——

func (it *Interp) eval(e syntax.Expr, env *Env) (Value, error) {
	switch x := e.(type) {
	case *syntax.IntLit:
		return IntVal(x.Val), nil
	case *syntax.FloatLit:
		return FloatVal(x.Val), nil
	case *syntax.StrLit:
		return StrVal(x.Val), nil
	case *syntax.BoolLit:
		return BoolVal(x.Val), nil

	case *syntax.Ident:
		if c, ok := env.lookup(x.Name); ok {
			return c.val, nil
		}
		if c, ok := it.ctors[x.Name]; ok {
			if c.arity == 0 {
				// 零参构造器允许裸写（None、Unit 这类）
				return &EnumVal{Def: c.def, Case: c.idx}, nil
			}
			return nil, it.errf(x.Line, "构造器 %s 须直接调用", x.Name)
		}
		return nil, it.errf(x.Line, "未定义 %s", x.Name)

	case *syntax.Unary:
		return it.evalUnary(x, env)

	case *syntax.Binary:
		return it.evalBinary(x, env)

	case *syntax.RangeEx:
		lo, err := it.eval(x.Lo, env)
		if err != nil {
			return nil, err
		}
		hi, err := it.eval(x.Hi, env)
		if err != nil {
			return nil, err
		}
		loi, lok := lo.(IntVal)
		hii, hik := hi.(IntVal)
		if !lok || !hik {
			return nil, it.errf(x.Line, "区间两端须为 int")
		}
		return &RangeVal{Lo: int64(loi), Hi: int64(hii)}, nil

	case *syntax.CallEx:
		return it.evalCall(x, env)

	case *syntax.IndexEx:
		return it.evalIndex(x, env)

	case *syntax.SliceEx:
		return it.evalSlice(x, env)

	case *syntax.FieldEx:
		ov, err := it.eval(x.Obj, env)
		if err != nil {
			return nil, err
		}
		if p, ok := ov.(*PtrVal); ok {
			ov = *p.Cell
		}
		sv, ok := ov.(*StructVal)
		if !ok {
			return nil, it.errf(x.Line, "%s 没有字段（%s 不是 struct）", x.Name, typeName(ov))
		}
		for i, f := range sv.Def.Fields {
			if f == x.Name {
				return sv.Fields[i], nil
			}
		}
		return nil, it.errf(x.Line, "%s 没有字段 %s", sv.Def.Name, x.Name)

	case *syntax.TryEx:
		v, err := it.eval(x.X, env)
		if err != nil {
			return nil, err
		}
		ev, ok := v.(*EnumVal)
		if !ok || ev.Def.Name != "Result" {
			return nil, it.errf(x.Line, "? 的操作数须为 Result，得到 %s", typeName(v))
		}
		if ev.Case == 0 { // Ok
			return ev.Payloads[0], nil
		}
		return nil, &signal{kind: sigErr, val: ev.Payloads[0]}

	case *syntax.StructLit:
		def, ok := it.structs[x.TypeName]
		if !ok {
			return nil, it.errf(x.Line, "未知 struct %s", x.TypeName)
		}
		if len(x.Fields) != len(def.Fields) {
			return nil, it.errf(x.Line, "%s 需要 %d 个字段，得到 %d 个", def.Name, len(def.Fields), len(x.Fields))
		}
		fields := make([]Value, len(x.Fields))
		for i, f := range x.Fields {
			if f.Name != def.Fields[i] {
				return nil, it.errf(x.Line, "%s 第 %d 个字段应为 %s，得到 %s", def.Name, i+1, def.Fields[i], f.Name)
			}
			fv, err := it.eval(f.Val, env)
			if err != nil {
				return nil, err
			}
			fields[i] = copyValue(fv)
		}
		return &StructVal{Def: def, Fields: fields}, nil

	case *syntax.MapLit:
		m := &MapVal{}
		for _, item := range x.Items {
			k, err := it.eval(item.Key, env)
			if err != nil {
				return nil, err
			}
			v, err := it.eval(item.Val, env)
			if err != nil {
				return nil, err
			}
			if err := m.Set(k, copyValue(v)); err != nil {
				return nil, it.errf(x.Line, "%s", err)
			}
		}
		return m, nil

	case *syntax.ArrLit:
		items := make([]Value, 0, len(x.Items))
		for _, el := range x.Items {
			ev, err := it.eval(el, env)
			if err != nil {
				return nil, err
			}
			items = append(items, copyValue(ev))
		}
		return &ArrVal{Items: items}, nil

	case *syntax.MatchEx:
		return it.evalMatch(x, env)

	case *syntax.FnLit:
		decl := &syntax.FnDecl{
			Name:       "",
			TypeParams: x.TypeParams,
			Params:     x.Params,
			Ret:        x.Ret,
			Body:       x.Body,
			Line:       x.Line,
		}
		return &Closure{Decl: decl, Env: env}, nil
	}
	return nil, it.errf(e.Pos(), "未知表达式")
}

func (it *Interp) evalUnary(x *syntax.Unary, env *Env) (Value, error) {
	v, err := it.eval(x.X, env)
	if err != nil {
		return nil, err
	}
	switch x.Op {
	case syntax.Minus:
		switch n := v.(type) {
		case IntVal:
			return -n, nil
		case FloatVal:
			return -n, nil
		}
		return nil, it.errf(x.Line, "- 不能用于 %s", typeName(v))
	case syntax.Not:
		t, err := truthy(v)
		if err != nil {
			return nil, it.errf(x.Line, "%s", err)
		}
		return BoolVal(!t), nil
	case syntax.Amp:
		id, ok := x.X.(*syntax.Ident)
		if !ok {
			return nil, it.errf(x.Line, "v0 只能对变量取引用")
		}
		c, ok := env.lookup(id.Name)
		if !ok {
			return nil, it.errf(x.Line, "未定义 %s", id.Name)
		}
		return &PtrVal{Cell: &c.val}, nil
	}
	return nil, it.errf(x.Line, "未知一元运算")
}

func (it *Interp) evalBinary(x *syntax.Binary, env *Env) (Value, error) {
	switch x.Op {
	case syntax.And:
		l, err := it.cond(x.L, env)
		if err != nil {
			return nil, err
		}
		if !l {
			return BoolVal(false), nil
		}
		r, err := it.cond(x.R, env)
		return BoolVal(r), err
	case syntax.Or:
		l, err := it.cond(x.L, env)
		if err != nil {
			return nil, err
		}
		if l {
			return BoolVal(true), nil
		}
		r, err := it.cond(x.R, env)
		return BoolVal(r), err
	}

	l, err := it.eval(x.L, env)
	if err != nil {
		return nil, err
	}
	r, err := it.eval(x.R, env)
	if err != nil {
		return nil, err
	}

	switch x.Op {
	case syntax.Eq, syntax.Neq:
		eq, err := equals(l, r)
		if err != nil {
			return nil, it.errf(x.Line, "%s", err)
		}
		if x.Op == syntax.Neq {
			eq = !eq
		}
		return BoolVal(eq), nil
	case syntax.Amp, syntax.BitOr, syntax.Xor, syntax.Shl, syntax.Shr:
		a, aok := l.(IntVal)
		b, bok := r.(IntVal)
		if !aok || !bok {
			return nil, it.errf(x.Line, "%s 与 %s 不可位运算（须为 int）", typeName(l), typeName(r))
		}
		switch x.Op {
		case syntax.Amp:
			return a & b, nil
		case syntax.BitOr:
			return a | b, nil
		case syntax.Xor:
			return a ^ b, nil
		case syntax.Shl:
			if b < 0 || b >= 64 {
				return nil, it.errf(x.Line, "移位位数须在 0..63，得到 %d", int64(b))
			}
			return a << uint(b), nil
		default:
			if b < 0 || b >= 64 {
				return nil, it.errf(x.Line, "移位位数须在 0..63，得到 %d", int64(b))
			}
			return a >> uint(b), nil
		}
	case syntax.Lt, syntax.Le, syntax.Gt, syntax.Ge:
		// float 对按 IEEE 运算符直落：NaN 参与任何比较一律 false
		// （TYPE-CONV-005 v1.0.1，ADR-0006/RS-007）。NaN 无全序，若走 compare
		// 会被兜成 0，把 `<=`/`>=` 错判为 true。
		if xf, ok := l.(FloatVal); ok {
			if yf, ok := r.(FloatVal); ok {
				return BoolVal(floatRel(x.Op, float64(xf), float64(yf))), nil
			}
		}
		cmp, ok := compare(l, r)
		if !ok {
			return nil, it.errf(x.Line, "%s 与 %s 不可比较大小（无隐式转换）", typeName(l), typeName(r))
		}
		switch x.Op {
		case syntax.Lt:
			return BoolVal(cmp < 0), nil
		case syntax.Le:
			return BoolVal(cmp <= 0), nil
		case syntax.Gt:
			return BoolVal(cmp > 0), nil
		default:
			return BoolVal(cmp >= 0), nil
		}
	}

	switch a := l.(type) {
	case IntVal:
		b, ok := r.(IntVal)
		if !ok {
			return nil, it.errf(x.Line, "%s", typeErr(l, r))
		}
		return it.arith(x, int64(a), int64(b))
	case FloatVal:
		b, ok := r.(FloatVal)
		if !ok {
			return nil, it.errf(x.Line, "%s", typeErr(l, r))
		}
		switch x.Op {
		case syntax.Plus:
			return a + b, nil
		case syntax.Minus:
			return a - b, nil
		case syntax.Star:
			return a * b, nil
		case syntax.Slash:
			if b == 0 {
				return nil, it.errf(x.Line, "浮点除零")
			}
			return a / b, nil
		default:
			return nil, it.errf(x.Line, "%% 不能用于 float")
		}
	case StrVal:
		b, ok := r.(StrVal)
		if !ok || x.Op != syntax.Plus {
			return nil, it.errf(x.Line, "%s", typeErr(l, r))
		}
		return a + b, nil
	}
	return nil, it.errf(x.Line, "%s 不支持运算 %s", typeName(l), x.Op)
}

func (it *Interp) arith(x *syntax.Binary, a, b int64) (Value, error) {
	switch x.Op {
	case syntax.Plus:
		return IntVal(a + b), nil
	case syntax.Minus:
		return IntVal(a - b), nil
	case syntax.Star:
		return IntVal(a * b), nil
	case syntax.Slash:
		if b == 0 {
			return nil, it.errf(x.Line, "整数除零")
		}
		return IntVal(a / b), nil
	case syntax.Percent:
		if b == 0 {
			return nil, it.errf(x.Line, "整数模零")
		}
		return IntVal(a % b), nil
	}
	return nil, it.errf(x.Line, "未知算术运算")
}

// compare 同型 int/float/str 大小比较
// floatRel float 对的关系比较（IEEE：NaN 参与任何比较一律 false，RS-007）。
func floatRel(op syntax.Kind, x, y float64) bool {
	switch op {
	case syntax.Lt:
		return x < y
	case syntax.Le:
		return x <= y
	case syntax.Gt:
		return x > y
	default:
		return x >= y
	}
}

func compare(a, b Value) (int, bool) {
	switch x := a.(type) {
	case IntVal:
		if y, ok := b.(IntVal); ok {
			switch {
			case x < y:
				return -1, true
			case x > y:
				return 1, true
			}
			return 0, true
		}
	case FloatVal:
		if y, ok := b.(FloatVal); ok {
			switch {
			case x < y:
				return -1, true
			case x > y:
				return 1, true
			}
			return 0, true
		}
	case StrVal:
		if y, ok := b.(StrVal); ok {
			return strings.Compare(string(x), string(y)), true
		}
	}
	return 0, false
}

func (it *Interp) evalIndex(x *syntax.IndexEx, env *Env) (Value, error) {
	ov, err := it.eval(x.Obj, env)
	if err != nil {
		return nil, err
	}
	if p, ok := ov.(*PtrVal); ok {
		ov = *p.Cell
	}
	iv, err := it.eval(x.Idx, env)
	if err != nil {
		return nil, err
	}
	switch c := ov.(type) {
	case *ArrVal:
		i, err := it.intIndex(iv, len(c.Items), x.Line)
		if err != nil {
			return nil, err
		}
		return c.Items[i], nil
	case *MapVal:
		v, ok := c.Get(iv)
		if !ok {
			return nil, it.errf(x.Line, "映射无键 %s（用 has() 探测）", fmtVal(iv, true))
		}
		return v, nil
	case StrVal:
		runes := []rune(string(c))
		i, err := it.intIndex(iv, len(runes), x.Line)
		if err != nil {
			return nil, err
		}
		return StrVal(string(runes[i])), nil
	}
	return nil, it.errf(x.Line, "%s 不可索引", typeName(ov))
}

func (it *Interp) evalSlice(x *syntax.SliceEx, env *Env) (Value, error) {
	ov, err := it.eval(x.Obj, env)
	if err != nil {
		return nil, err
	}
	if p, ok := ov.(*PtrVal); ok {
		ov = *p.Cell
	}
	lov, err := it.eval(x.Lo, env)
	if err != nil {
		return nil, err
	}
	hiv, err := it.eval(x.Hi, env)
	if err != nil {
		return nil, err
	}
	bounds := func(n int) (int, int, error) {
		lo, lok := lov.(IntVal)
		hi, hik := hiv.(IntVal)
		if !lok || !hik {
			return 0, 0, it.errf(x.Line, "切片端点须为 int")
		}
		loi, hii := int(lo), int(hi)
		if loi < 0 || hii < loi || hii > n {
			return 0, 0, it.errf(x.Line, "切片 [%d:%d] 越界（长度 %d）", loi, hii, n)
		}
		return loi, hii, nil
	}
	switch c := ov.(type) {
	case *ArrVal:
		lo, hi, err := bounds(len(c.Items))
		if err != nil {
			return nil, err
		}
		items := make([]Value, hi-lo)
		copy(items, c.Items[lo:hi])
		return &ArrVal{Items: items}, nil
	case StrVal:
		runes := []rune(string(c))
		lo, hi, err := bounds(len(runes))
		if err != nil {
			return nil, err
		}
		return StrVal(string(runes[lo:hi])), nil
	}
	return nil, it.errf(x.Line, "%s 不可切片", typeName(ov))
}

func (it *Interp) evalMatch(x *syntax.MatchEx, env *Env) (Value, error) {
	scrut, err := it.eval(x.Scrut, env)
	if err != nil {
		return nil, err
	}
	ev, isEnum := scrut.(*EnumVal)
	covered := map[int]bool{}
	for _, arm := range x.Arms {
		penv := newEnv(env)
		ok, err := it.matchPat(scrut, arm.Pat, penv)
		if err != nil {
			return nil, err
		}
		if isEnum {
			if cp, ok := arm.Pat.(*syntax.CasePat); ok {
				if idx := ev.Def.caseIndex(cp.Name); idx >= 0 {
					covered[idx] = true
				}
			}
		}
		if ok {
			return it.eval(arm.Body, penv)
		}
	}
	if isEnum {
		var missing []string
		for i, c := range ev.Def.Cases {
			if !covered[i] {
				missing = append(missing, c)
			}
		}
		if len(missing) > 0 {
			return nil, it.errf(x.Line, "match 不穷尽，缺少: %s", strings.Join(missing, ", "))
		}
	}
	return nil, it.errf(x.Line, "match 无匹配分支")
}

func (it *Interp) matchPat(v Value, pat syntax.Pat, env *Env) (bool, error) {
	switch p := pat.(type) {
	case *syntax.WildPat:
		return true, nil
	case *syntax.BindPat:
		if err := env.define(p.Name, v, false); err != nil {
			return false, err
		}
		return true, nil
	case *syntax.LitPat:
		lv, err := it.eval(p.Val, env)
		if err != nil {
			return false, err
		}
		eq, err := equals(v, lv)
		if err != nil {
			return false, nil
		}
		return eq, nil
	case *syntax.CasePat:
		ev, ok := v.(*EnumVal)
		if !ok {
			return false, nil
		}
		idx := ev.Def.caseIndex(p.Name)
		if idx < 0 || idx != ev.Case {
			return false, nil
		}
		if len(p.Subs) != len(ev.Payloads) {
			return false, it.errf(p.Line, "case %s 需要 %d 个子模式，得到 %d 个", p.Name, len(ev.Payloads), len(p.Subs))
		}
		for i, sp := range p.Subs {
			ok, err := it.matchPat(ev.Payloads[i], sp, env)
			if err != nil {
				return false, err
			}
			if !ok {
				return false, nil
			}
		}
		return true, nil
	}
	return false, nil
}

// —— 调用 ——

func (it *Interp) evalCall(x *syntax.CallEx, env *Env) (Value, error) {
	// 构造器调用（Some/None/Ok/Err 与用户 enum case）
	if id, ok := x.Callee.(*syntax.Ident); ok {
		if c, ok := it.ctors[id.Name]; ok {
			if len(x.Args) != c.arity {
				return nil, it.errf(x.Line, "%s 需要 %d 个参数，得到 %d 个", id.Name, c.arity, len(x.Args))
			}
			payloads := make([]Value, 0, len(x.Args))
			for _, a := range x.Args {
				av, err := it.eval(a, env)
				if err != nil {
					return nil, err
				}
				payloads = append(payloads, copyValue(av))
			}
			return &EnumVal{Def: c.def, Case: c.idx, Payloads: payloads}, nil
		}
	}

	// 方法调用：Callee 为 FieldEx 且对象是 struct
	if fe, ok := x.Callee.(*syntax.FieldEx); ok {
		ov, err := it.eval(fe.Obj, env)
		if err != nil {
			return nil, err
		}
		if p, ok := ov.(*PtrVal); ok {
			ov = *p.Cell
		}
		if sv, ok := ov.(*StructVal); ok {
			if cl, ok := sv.Def.Methods[fe.Name]; ok {
				args, err := it.evalArgs(x.Args, env)
				if err != nil {
					return nil, err
				}
				return it.callMethod(cl, ov, args)
			}
		}
		// 字段存函数值的情况落到通用路径
	}

	cv, err := it.eval(x.Callee, env)
	if err != nil {
		return nil, err
	}
	args, err := it.evalArgs(x.Args, env)
	if err != nil {
		return nil, err
	}
	switch f := cv.(type) {
	case *Closure:
		return it.callClosure(f, args)
	case *Builtin:
		return f.Fn(it, args)
	}
	return nil, it.errf(x.Line, "%s 不可调用", typeName(cv))
}

func (it *Interp) evalArgs(exprs []syntax.Expr, env *Env) ([]Value, error) {
	args := make([]Value, 0, len(exprs))
	for _, a := range exprs {
		v, err := it.eval(a, env)
		if err != nil {
			return nil, err
		}
		args = append(args, copyValue(v))
	}
	return args, nil
}

func (it *Interp) callClosure(cl *Closure, args []Value) (Value, error) {
	name := cl.Name
	if name == "" {
		name = "匿名函数"
	}
	if len(args) != len(cl.Decl.Params) {
		return nil, fmt.Errorf("%s 需要 %d 个参数，得到 %d 个", name, len(cl.Decl.Params), len(args))
	}
	fenv := newEnv(cl.Env)
	for i, p := range cl.Decl.Params {
		if err := fenv.define(p.Name, args[i], false); err != nil {
			return nil, err
		}
	}
	return it.runBody(cl, fenv, name)
}

// callMethod 方法调用：接收者绑定到 Recv 名，参数照常绑定.
func (it *Interp) callMethod(cl *Closure, recv Value, args []Value) (Value, error) {
	name := cl.Name
	if len(args) != len(cl.Decl.Params) {
		return nil, fmt.Errorf("%s 需要 %d 个参数，得到 %d 个", name, len(cl.Decl.Params), len(args))
	}
	fenv := newEnv(cl.Env)
	if err := fenv.define(cl.Decl.Recv.Name, copyValue(recv), false); err != nil {
		return nil, err
	}
	for i, p := range cl.Decl.Params {
		if err := fenv.define(p.Name, args[i], false); err != nil {
			return nil, err
		}
	}
	return it.runBody(cl, fenv, name)
}

// runBody 执行函数体并消化控制流信号（? 传播在边界合成 Err 返回）.
func (it *Interp) runBody(cl *Closure, fenv *Env, name string) (Value, error) {
	err := it.execStmts(cl.Decl.Body, fenv)
	if sig, ok := asSignal(err); ok {
		switch sig.kind {
		case sigReturn:
			return sig.val, nil
		case sigErr:
			return &EnumVal{Def: it.enums["Result"], Case: 1, Payloads: []Value{sig.val}}, nil
		default:
			return nil, sig
		}
	}
	return UnitVal{}, err
}

// CallBuiltin 调用预载内建（IR 解释器等 IR 层执行器复用同一套内建实现——decisions #37 两端一致）。
// found=false 表示未注册该名字。
func (it *Interp) CallBuiltin(name string, args []Value) (v Value, found bool, err error) {
	c, ok := it.Globals.lookup(name)
	if !ok {
		return nil, false, nil
	}
	b, ok := c.val.(*Builtin)
	if !ok {
		return nil, false, nil
	}
	v, err = b.Fn(it, args)
	return v, true, err
}
