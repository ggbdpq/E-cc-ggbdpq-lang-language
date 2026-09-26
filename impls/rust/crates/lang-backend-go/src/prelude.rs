//! →Go 产物内联运行时（随产物发射的 Go 源文本；行为面 = lang-go 语义的 Go 镜像）。
//!
//! 文本形状参照 `impls/typescript/src/backends/goprelude.ts`，但按本后端的
//! **全动态 any 值域**重写：IR 不携带类型表，一切运算/索引/字段访问经运行时
//! 分派（类型断言），错误消息逐字对齐解释器（`interp/interp.ts`、`interp/value.ts`、
//! `interp/builtin.ts`），使差分执行时可观察输出一致。
//!
//! 与参考 prelude 的形状差异（均有依据）：
//! - 无泛型（`LangGoOpt[T]`/`LangGoRes[T]`）：Option/Result 以普通 enum 值表示
//!   （Tag = Some/None/Ok/Err），与 IR 的 `EnumNew`/`SwitchEnum`/`Try` 指令对齐；
//! - 无 net/http（本波内建名单无 http.get）；
//! - 用户 struct 为 `*S{n}_{名}` 指针 + 显式拷贝点（[`langGoCopy`]）承载
//!   VALUE-COPY-001 值语义（Go 指针别名 + 拷贝点 = 绑定/传参/返回拷贝、容器共享）。
pub const GO_PRELUDE: &str = r##"package main

import (
	"encoding/json"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"
	"sync"
)

// ———— 值表示（全动态 any 域） ————

// math 供生成侧 float 特殊常量的发射形引用（NaN/±Inf/-0）
var _ = math.NaN

// langGoStringer：一切复合值实现它，langGoFmt 据此统一格式化
type langGoStringer interface{ langGoString(top bool) string }

// LangGoErr 对应 lang-go 预载 struct error { msg: string }（err() 构造）
type LangGoErr struct{ Msg string }

func (e *LangGoErr) Error() string { return e.Msg }

func (e *LangGoErr) langGoString(top bool) string {
	return "error{msg: " + strconv.Quote(e.Msg) + "}"
}

func (e *LangGoErr) LangGoField(name string) any {
	if name == "msg" {
		return e.Msg
	}
	panic("error 没有字段 " + name)
}

// LangGoEnum 对应一切用户 enum 与预载 Option/Result；Name = enum 名（typeName 用），
// Tag = case 名（全局唯一），Vals = 装箱载荷
type LangGoEnum struct {
	Name string
	Tag  string
	Vals []any
}

func (e LangGoEnum) langGoString(top bool) string {
	if len(e.Vals) == 0 {
		return e.Tag
	}
	parts := make([]string, len(e.Vals))
	for i, v := range e.Vals {
		parts[i] = langGoFmt(v, false)
	}
	return e.Tag + "(" + strings.Join(parts, ", ") + ")"
}

// langGoEnumVal 枚举构造（EnumNew 发射形）：载荷按值语义拷贝
func langGoEnumVal(enumName, tag string, vals ...any) LangGoEnum {
	for i := range vals {
		vals[i] = langGoCopy(vals[i])
	}
	return LangGoEnum{Name: enumName, Tag: tag, Vals: vals}
}

// LangGoArr 对应 lang-go 数组：引用语义（引用共享、push 原位可见）
type LangGoArr struct{ Items []any }

func (a *LangGoArr) langGoString(top bool) string {
	parts := make([]string, len(a.Items))
	for i, v := range a.Items {
		parts[i] = langGoFmt(v, false)
	}
	return "[" + strings.Join(parts, ", ") + "]"
}

// LangGoMap 对应 lang-go 映射：插入序保持（keys 承载原始键的首插序）
type langGoKV struct{ K, V any }

type LangGoMap struct {
	keys []any
	m    map[string]any
}

func langGoNewMap(kvs ...langGoKV) *LangGoMap {
	m := &LangGoMap{m: map[string]any{}}
	for _, kv := range kvs {
		m.Set(kv.K, kv.V)
	}
	return m
}

func langGoKeyString(k any) string {
	switch x := k.(type) {
	case int64:
		return fmt.Sprintf("i:%d", x)
	case string:
		return "s:" + x
	case bool:
		return fmt.Sprintf("b:%v", x)
	}
	panic("映射键须为 int/string/bool，得到 " + langGoTypeName(k))
}

func (m *LangGoMap) Set(k, v any) {
	ks := langGoKeyString(k)
	if _, exists := m.m[ks]; !exists {
		m.keys = append(m.keys, k)
	}
	m.m[ks] = v
}

func (m *LangGoMap) langGoString(top bool) string {
	parts := make([]string, 0, len(m.keys))
	for _, k := range m.keys {
		parts = append(parts, langGoFmt(k, false)+": "+langGoFmt(m.m[langGoKeyString(k)], false))
	}
	return "{" + strings.Join(parts, ", ") + "}"
}

// langGoRange 对应区间 lo..hi（半开）
type langGoRange struct{ Lo, Hi int64 }

func (r langGoRange) langGoString(top bool) string {
	return strconv.FormatInt(r.Lo, 10) + ".." + strconv.FormatInt(r.Hi, 10)
}

// langGoRef 对应 &x 的装箱引用（装箱纪律：槽位持 Ref，读 Deref、写 RefSet）
type langGoRef struct{ V any }

func (r *langGoRef) langGoString(top bool) string {
	return "&" + langGoFmt(r.V, false)
}

// langGoClosure 闭包值：Env = 捕获槽（Ref 值），调用时落在被调局部表前 capture_count 槽
type langGoClosure struct {
	Fn   func(...any) any
	Env  []any
	Name string
}

func (c *langGoClosure) langGoString(top bool) string {
	if c.Name != "" {
		return "<fn " + c.Name + ">"
	}
	return "<fn 匿名>"
}

// langGoIter for-in 迭代器（IterNew 时快照，IterNext 逐个消费）
type langGoIter struct {
	Items []any
	I     int64
}

// ———— 值语义拷贝点（VALUE-COPY-001/002/009） ————

// langGoCopy 绑定/传参/返回/容器写入处的值语义拷贝：
// 用户 struct 深拷贝（嵌套 struct 递归、容器/引用字段共享），其余原样
// （标量本就复制；容器/引用/枚举/闭包按引用共享；枚举载荷不可变）。
func langGoCopy(v any) any {
	if c, ok := v.(interface{ langGoClone() any }); ok {
		return c.langGoClone()
	}
	return v
}

// ———— 格式化（镜像解释器 fmtVal / RUNTIME-OUTPUT-003/004） ————

func langGoFmt(v any, top bool) string {
	switch x := v.(type) {
	case nil:
		return "()"
	case int64:
		return strconv.FormatInt(x, 10)
	case float64:
		return strconv.FormatFloat(x, 'g', -1, 64)
	case bool:
		return strconv.FormatBool(x)
	case string:
		if top {
			return x
		}
		return strconv.Quote(x)
	}
	if s, ok := v.(langGoStringer); ok {
		return s.langGoString(top)
	}
	return "?"
}

func langGoFmtArgs(args []any) string {
	parts := make([]string, len(args))
	for i, a := range args {
		parts[i] = langGoFmt(a, true)
	}
	return strings.Join(parts, " ")
}

func langGoPrintln(args ...any) { fmt.Println(langGoFmtArgs(args)) }
func langGoPrint(args ...any)   { fmt.Print(langGoFmtArgs(args)) }

func langGoTypeName(v any) string {
	switch v.(type) {
	case nil:
		return "()"
	case int64:
		return "int"
	case float64:
		return "float"
	case bool:
		return "bool"
	case string:
		return "string"
	case *LangGoArr:
		return "数组"
	case *LangGoMap:
		return "映射"
	case langGoRange:
		return "区间"
	case LangGoEnum:
		if e, ok := v.(LangGoEnum); ok {
			return e.Name
		}
		return "enum"
	case *LangGoErr:
		return "error"
	case *langGoRef:
		return "引用"
	case *langGoClosure:
		return "函数"
	case chan any:
		return "通道"
	}
	if s, ok := v.(interface{ LangGoTypeName() string }); ok {
		return s.LangGoTypeName()
	}
	return "未知值"
}

// ———— 内建函数（镜像解释器 builtin.ts 消息） ————

func langGoLen(v any) int64 {
	switch x := v.(type) {
	case string:
		return int64(len([]rune(x)))
	case *LangGoArr:
		return int64(len(x.Items))
	case *LangGoMap:
		return int64(len(x.keys))
	case langGoRange:
		if x.Hi > x.Lo {
			return x.Hi - x.Lo
		}
		return 0
	}
	panic("len 不支持 " + langGoTypeName(v))
}

func langGoStr(v any) string { return langGoFmt(v, true) }

func langGoInt(v any) int64 {
	switch x := v.(type) {
	case int64:
		return x
	case float64:
		return int64(x)
	case string:
		n, err := strconv.ParseInt(strings.TrimSpace(x), 10, 64)
		if err != nil {
			panic("无法把 " + strconv.Quote(x) + " 转为 int")
		}
		return n
	}
	panic("int() 不支持 " + langGoTypeName(v))
}

func langGoFloat(v any) float64 {
	switch x := v.(type) {
	case int64:
		return float64(x)
	case float64:
		return x
	case string:
		f, err := strconv.ParseFloat(strings.TrimSpace(x), 64)
		if err != nil {
			panic("无法把 " + strconv.Quote(x) + " 转为 float")
		}
		return f
	}
	panic("float() 不支持 " + langGoTypeName(v))
}

func langGoKeys(v any) *LangGoArr {
	m, ok := v.(*LangGoMap)
	if !ok {
		panic("keys 需要映射，得到 " + langGoTypeName(v))
	}
	return &LangGoArr{Items: append([]any(nil), m.keys...)}
}

func langGoValues(v any) *LangGoArr {
	m, ok := v.(*LangGoMap)
	if !ok {
		panic("values 需要映射，得到 " + langGoTypeName(v))
	}
	vs := make([]any, 0, len(m.keys))
	for _, k := range m.keys {
		vs = append(vs, m.m[langGoKeyString(k)])
	}
	return &LangGoArr{Items: vs}
}

func langGoHas(v, k any) bool {
	m, ok := v.(*LangGoMap)
	if !ok {
		panic("has 需要映射，得到 " + langGoTypeName(v))
	}
	_, found := m.m[langGoKeyString(k)]
	return found
}

// push 返回数组本身（TS/Go 证据 builtin.ts/builtin.go 同口径；IR has_ret=1
// 会把调用当值消费——void 形让 →Go 产物编译失败「(no value) used as value」）
func langGoPush(v any, vs ...any) any {
	a, ok := v.(*LangGoArr)
	if !ok {
		panic("push 需要数组，得到 " + langGoTypeName(v))
	}
	for _, it := range vs {
		a.Items = append(a.Items, langGoCopy(it))
	}
	return v
}

// ArrayPush（数组 `+=`）：rhs 为数组 → 原位拼接（逐元素追加 rhs 全部元素，
// 与 IrMachine ArrayPush 同语义；错误消息逐字对齐解释器）。单元素追加是
// push 内建（langGoPush），两者不得混用。
func langGoPushArr(v any, rhs any) any {
	v = langGoUnref(v)
	a, ok := v.(*LangGoArr)
	if !ok {
		panic("数组 += 需要数组，得到 " + langGoTypeName(v))
	}
	b, ok := rhs.(*LangGoArr)
	if !ok {
		panic("数组 += 需要数组，得到 " + langGoTypeName(rhs))
	}
	for _, it := range b.Items {
		a.Items = append(a.Items, langGoCopy(it))
	}
	return v
}

func langGoAssert(cond any, msg ...any) {
	b, ok := cond.(bool)
	if !ok {
		panic("条件须为 bool，得到 " + langGoTypeName(cond))
	}
	if !b {
		if len(msg) > 0 {
			panic("assert 失败: " + langGoFmt(msg[0], true))
		}
		panic("assert 失败")
	}
}

func langGoErr(msg any) error {
	s, ok := msg.(string)
	if !ok {
		panic("err 需要字符串，得到 " + langGoTypeName(msg))
	}
	return &LangGoErr{Msg: s}
}

func langGoErrMsg(e error) string {
	if le, ok := e.(*LangGoErr); ok {
		return le.Msg
	}
	return e.Error()
}

func langGoBytes(v any) *LangGoArr {
	s, ok := v.(string)
	if !ok {
		panic("bytes 需要字符串，得到 " + langGoTypeName(v))
	}
	bs := []byte(s)
	items := make([]any, len(bs))
	for i, b := range bs {
		items[i] = int64(b)
	}
	return &LangGoArr{Items: items}
}

func langGoChar(v any) string {
	n, ok := v.(int64)
	if !ok {
		panic("char 需要 int，得到 " + langGoTypeName(v))
	}
	return string(rune(n))
}

func langGoIsNone(v any) bool {
	if e, ok := v.(LangGoEnum); ok {
		return e.Tag == "None"
	}
	return false
}

// ———— 运算（全动态分派；消息镜像解释器 interp.ts evalBinary） ————

func langGoArith(op string, a, b any) any {
	if op == "+" {
		if x, ok := a.(string); ok {
			y, ok2 := b.(string)
			if !ok2 {
				panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较（无隐式转换）")
			}
			return x + y
		}
	}
	if x, ok := a.(int64); ok {
		y, ok2 := b.(int64)
		if !ok2 {
			panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较（无隐式转换）")
		}
		switch op {
		case "+":
			return x + y
		case "-":
			return x - y
		case "*":
			return x * y
		case "/":
			if y == 0 {
				panic("整数除零")
			}
			return x / y
		case "%":
			if y == 0 {
				panic("整数除零")
			}
			return x % y
		}
	}
	if x, ok := a.(float64); ok {
		y, ok2 := b.(float64)
		if !ok2 {
			panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较（无隐式转换）")
		}
		switch op {
		case "+":
			return x + y
		case "-":
			return x - y
		case "*":
			return x * y
		case "/":
			if y == 0 {
				panic("浮点除零")
			}
			return x / y
		}
		panic("% 不能用于 float")
	}
	panic(langGoTypeName(a) + " 不支持运算 " + op)
}

func langGoBits(op string, a, b any) any {
	x, ok := a.(int64)
	if !ok {
		panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可位运算（须为 int）")
	}
	y, ok2 := b.(int64)
	if !ok2 {
		panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可位运算（须为 int）")
	}
	switch op {
	case "&":
		return x & y
	case "|":
		return x | y
	case "^":
		return x ^ y
	case "<<":
		if y < 0 || y >= 64 {
			panic(fmt.Sprintf("移位位数须在 0..63，得到 %d", y))
		}
		return x << uint(y)
	case ">>":
		if y < 0 || y >= 64 {
			panic(fmt.Sprintf("移位位数须在 0..63，得到 %d", y))
		}
		return x >> uint(y)
	}
	panic("不可达")
}

func langGoNeg(v any) any {
	switch x := v.(type) {
	case int64:
		return -x
	case float64:
		return -x
	}
	panic(langGoTypeName(v) + " 不支持运算 -")
}

func langGoNot(v any) bool {
	b, ok := v.(bool)
	if !ok {
		panic(langGoTypeName(v) + " 不支持运算 !")
	}
	return !b
}

// langGoEq 镜像解释器 equals：标量同型、enum 同 case 递归；容器不可比
func langGoEq(a, b any) bool {
	switch x := a.(type) {
	case int64:
		y, ok := b.(int64)
		return ok && x == y
	case float64:
		y, ok := b.(float64)
		return ok && x == y
	case bool:
		y, ok := b.(bool)
		return ok && x == y
	case string:
		y, ok := b.(string)
		return ok && x == y
	case LangGoEnum:
		y, ok := b.(LangGoEnum)
		if !ok || y.Tag != x.Tag || len(y.Vals) != len(x.Vals) {
			return false
		}
		for i := range x.Vals {
			if !langGoEq(x.Vals[i], y.Vals[i]) {
				return false
			}
		}
		return true
	}
	panic(langGoTypeName(a) + " 不可用 == 比较")
}

func langGoCompare(a, b any) int64 {
	switch x := a.(type) {
	case int64:
		y, ok := b.(int64)
		if !ok {
			panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小（无隐式转换）")
		}
		switch {
		case x < y:
			return -1
		case x > y:
			return 1
		}
		return 0
	case float64:
		y, ok := b.(float64)
		if !ok {
			panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小（无隐式转换）")
		}
		switch {
		case x < y:
			return -1
		case x > y:
			return 1
		}
		return 0
	case string:
		y, ok := b.(string)
		if !ok {
			panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小（无隐式转换）")
		}
		return int64(strings.Compare(x, y))
	}
	panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小（无隐式转换）")
}

func langGoTruthy(v any) bool {
	b, ok := v.(bool)
	if !ok {
		panic("条件须为 bool，得到 " + langGoTypeName(v))
	}
	return b
}

// ———— 索引/切片/字段/枚举载荷（读出皆副本，VALUE-ELEM-009） ————

// langGoUnref 解引用包装：装箱槽直压的地址组件携带 *langGoRef（与 IrMachine
// deref_value 同位点；Ref 不嵌套，单层足够，上限防御引用环）
func langGoUnref(v any) any {
	for i := 0; i < 64; i++ {
		r, ok := v.(*langGoRef)
		if !ok {
			return v
		}
		v = r.V
	}
	panic("引用穿透过深（疑似引用环）")
}

func langGoIndex(obj, idx any) any {
	obj = langGoUnref(obj)
	switch c := obj.(type) {
	case *LangGoArr:
		i := langGoInt(idx)
		if i < 0 || i >= int64(len(c.Items)) {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, len(c.Items)))
		}
		return langGoCopy(c.Items[i])
	case *LangGoMap:
		ks := langGoKeyString(idx)
		v, found := c.m[ks]
		if !found {
			panic("映射无键 " + langGoFmt(idx, true) + "（用 has() 探测）")
		}
		return langGoCopy(v)
	case string:
		runes := []rune(c)
		i := langGoInt(idx)
		if i < 0 || i >= int64(len(runes)) {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, len(runes)))
		}
		return string(runes[i])
	}
	panic(langGoTypeName(obj) + " 不可索引")
}

// 写链地址组件的 Ref 读形：不落副本（与 IrMachine 的 Place 溯源操作数同
// 语义）——中间组件读出底层存储本体，最终 set 在原对象上写穿。仅供 codegen
// 的赋值链发射使用；普通读走 langGoField/langGoIndex（读出皆副本）。
func langGoIndexRef(obj, idx any) any {
	obj = langGoUnref(obj)
	switch c := obj.(type) {
	case *LangGoArr:
		i := langGoInt(idx)
		if i < 0 || i >= int64(len(c.Items)) {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, len(c.Items)))
		}
		return c.Items[i]
	case *LangGoMap:
		ks := langGoKeyString(idx)
		v, found := c.m[ks]
		if !found {
			panic("映射无键 " + langGoFmt(idx, true) + "（用 has() 探测）")
		}
		return v
	}
	panic(langGoTypeName(obj) + " 不可索引")
}

func langGoSetIndex(obj, idx, v any) {
	obj = langGoUnref(obj)
	switch c := obj.(type) {
	case *LangGoArr:
		i := langGoInt(idx)
		if i < 0 || i >= int64(len(c.Items)) {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, len(c.Items)))
		}
		c.Items[i] = langGoCopy(v)
		return
	case *LangGoMap:
		c.Set(idx, langGoCopy(v))
		return
	case string:
		panic("字符串不可变")
	}
	panic(langGoTypeName(obj) + " 不可索引赋值")
}

func langGoSlice(obj, lo, hi any) any {
	i, j := langGoInt(lo), langGoInt(hi)
	switch c := obj.(type) {
	case *LangGoArr:
		if i < 0 || j < i || j > int64(len(c.Items)) {
			panic(fmt.Sprintf("切片 [%d:%d] 越界（长度 %d）", i, j, len(c.Items)))
		}
		items := make([]any, j-i)
		copy(items, c.Items[i:j])
		return &LangGoArr{Items: items}
	case string:
		runes := []rune(c)
		if i < 0 || j < i || j > int64(len(runes)) {
			panic(fmt.Sprintf("切片 [%d:%d] 越界（长度 %d）", i, j, len(runes)))
		}
		return string(runes[i:j])
	}
	panic(langGoTypeName(obj) + " 不可切片")
}

type langGoFielder interface{ LangGoField(name string) any }

func langGoField(obj any, name string) any {
	obj = langGoUnref(obj)
	if f, ok := obj.(langGoFielder); ok {
		return langGoCopy(f.LangGoField(name))
	}
	panic(langGoTypeName(obj) + " 没有字段 " + name)
}

// 写链地址组件的 Ref 读形（不落副本，语义见 langGoIndexRef）。
func langGoFieldRef(obj any, name string) any {
	obj = langGoUnref(obj)
	if f, ok := obj.(langGoFielder); ok {
		return f.LangGoField(name)
	}
	panic(langGoTypeName(obj) + " 没有字段 " + name)
}

type langGoFieldSetter interface{ LangGoSetField(name string, val any) }

func langGoSetField(obj any, name string, v any) {
	obj = langGoUnref(obj)
	if f, ok := obj.(langGoFieldSetter); ok {
		f.LangGoSetField(name, v)
		return
	}
	panic(langGoTypeName(obj) + " 没有字段 " + name)
}

func langGoEnumTag(v any) string {
	e, ok := v.(LangGoEnum)
	if !ok {
		panic("match 目标须为 enum，得到 " + langGoTypeName(v))
	}
	return e.Tag
}

func langGoEnumPayload(v any, i int) any {
	e, ok := v.(LangGoEnum)
	if !ok {
		panic("取载荷目标须为 enum，得到 " + langGoTypeName(v))
	}
	if i < 0 || i >= len(e.Vals) {
		panic(fmt.Sprintf("载荷 %d 越界（case %s）", i, e.Tag))
	}
	return langGoCopy(e.Vals[i])
}

// ———— `?` 传播（RESULT-TRY-003：Result 以 enum Tag Ok/Err 表示） ————

func langGoIsErr(v any) bool {
	e, ok := v.(LangGoEnum)
	return ok && e.Tag == "Err"
}

func langGoTryOk(v any) any {
	e, ok := v.(LangGoEnum)
	if !ok || e.Tag != "Ok" {
		panic("? 须作用于 Result，得到 " + langGoTypeName(v))
	}
	return langGoCopy(e.Vals[0])
}

func langGoTryErrOf(v any) error {
	e, ok := v.(LangGoEnum)
	if !ok || e.Tag != "Err" {
		panic("? 须作用于 Result，得到 " + langGoTypeName(v))
	}
	er, ok := e.Vals[0].(error)
	if !ok {
		panic("Err 载荷须为 error")
	}
	return er
}

// ———— 引用装箱（VALUE-REF-003） ————

func langGoBox(v any) *langGoRef { return &langGoRef{V: langGoCopy(v)} }

func langGoDeref(v any) any {
	if r, ok := v.(*langGoRef); ok {
		return langGoCopy(r.V)
	}
	return v
}

func langGoRefSet(r, v any) {
	p, ok := r.(*langGoRef)
	if !ok {
		panic("写穿目标须为引用，得到 " + langGoTypeName(r))
	}
	p.V = langGoCopy(v)
}

// ———— 闭包（VALUE-CAPTURE-004：捕获槽按引用落在被调局部表前） ————

func langGoCallClosure(c any, args ...any) any {
	cl, ok := c.(*langGoClosure)
	if !ok {
		panic("调用目标须为函数值，得到 " + langGoTypeName(c))
	}
	full := append(append([]any{}, cl.Env...), args...)
	return cl.Fn(full...)
}

// ———— 并发：go 语句与 chan（TASK-*/CHAN-*） ————

var langGoWG sync.WaitGroup

// langGoGo spawn 发射形：实参快照在 []any 字面量处立即求值（EVAL-GO-007），
// 闭包只持有切片头，循环内 spawn 不与后续迭代竞写
func langGoGo(f func(...any), args []any) {
	langGoWG.Add(1)
	go func() {
		defer langGoWG.Done()
		f(args...)
	}()
}

func langGoWaitAll() { langGoWG.Wait() }

func langGoNewChan() chan any { return make(chan any, 1024) }

// langGoSend 不阻塞；缓冲满为运行时错误（CHAN-SEND-002）；载荷按值语义拷贝
func langGoSend(ch, v any) {
	c, ok := ch.(chan any)
	if !ok {
		panic("send 需要通道，得到 " + langGoTypeName(ch))
	}
	select {
	case c <- langGoCopy(v):
	default:
		panic("channel 缓冲已满（1024）")
	}
}

// langGoRecv 阻塞直到有值（CHAN-RECV-003）；确无 send 方时由宿主死锁 panic 兜底
func langGoRecv(ch any) LangGoEnum {
	c, ok := ch.(chan any)
	if !ok {
		panic("recv 需要通道，得到 " + langGoTypeName(ch))
	}
	v := <-c
	return LangGoEnum{Name: "Result", Tag: "Ok", Vals: []any{v}}
}

// ———— for-in 迭代协议（CTRL-FORIN-003：数组索引序/映射插入序/字符串码点序/区间半开） ————

func langGoIterNew(v any) *langGoIter {
	switch x := v.(type) {
	case *LangGoArr:
		items := make([]any, len(x.Items))
		copy(items, x.Items)
		return &langGoIter{Items: items}
	case *LangGoMap:
		return &langGoIter{Items: append([]any(nil), x.keys...)}
	case string:
		items := make([]any, 0, len(x))
		for _, r := range []rune(x) {
			items = append(items, string(r))
		}
		return &langGoIter{Items: items}
	case langGoRange:
		n := x.Hi - x.Lo
		if n < 0 {
			n = 0
		}
		items := make([]any, 0, n)
		for i := x.Lo; i < x.Hi; i++ {
			items = append(items, i)
		}
		return &langGoIter{Items: items}
	}
	panic(langGoTypeName(v) + " 不可迭代")
}

// ———— 标准库委托：json（RUNTIME-JSON-001/002；镜像解释器 builtin.ts） ————

func langGoJSONParse(v any) any {
	s, ok := v.(string)
	if !ok {
		panic("jsonParse 需要字符串，得到 " + langGoTypeName(v))
	}
	dec := json.NewDecoder(strings.NewReader(s))
	dec.UseNumber()
	var raw any
	if err := dec.Decode(&raw); err != nil {
		panic("JSON 解析失败: " + err.Error())
	}
	return langGoJSONFrom(raw)
}

func langGoJSONFrom(raw any) any {
	switch x := raw.(type) {
	case nil:
		return LangGoEnum{Name: "Option", Tag: "None"}
	case bool:
		return x
	case string:
		return x
	case json.Number:
		if n, err := strconv.ParseInt(x.String(), 10, 64); err == nil {
			return n
		}
		f, _ := strconv.ParseFloat(x.String(), 64)
		return f
	case []any:
		items := make([]any, 0, len(x))
		for _, el := range x {
			items = append(items, langGoJSONFrom(el))
		}
		return &LangGoArr{Items: items}
	case map[string]any:
		keys := make([]string, 0, len(x))
		for k := range x {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		m := langGoNewMap()
		for _, k := range keys {
			m.Set(k, langGoJSONFrom(x[k]))
		}
		return m
	}
	return nil
}

// langGoJSONTo 值域 → JSON 域：None → null；其余 enum/struct/error 不可序列化
// （镜像解释器 valueToJSON 的拒斥面）
func langGoJSONTo(v any) any {
	switch x := v.(type) {
	case nil:
		return nil
	case bool, string, int64, float64:
		return x
	case *LangGoArr:
		out := make([]any, 0, len(x.Items))
		for _, el := range x.Items {
			out = append(out, langGoJSONTo(el))
		}
		return out
	case *LangGoMap:
		out := map[string]any{}
		for _, k := range x.keys {
			ks, ok := k.(string)
			if !ok {
				panic("JSON 对象键须为 string，得到 " + langGoTypeName(k))
			}
			out[ks] = langGoJSONTo(x.m["s:"+ks])
		}
		return out
	case LangGoEnum:
		if x.Tag == "None" {
			return nil
		}
		panic(x.Tag + " 不可 JSON 序列化")
	case *LangGoErr:
		panic("error 不可 JSON 序列化")
	}
	if s, ok := v.(interface{ LangGoTypeName() string }); ok {
		panic(s.LangGoTypeName() + " 不可 JSON 序列化")
	}
	panic(langGoTypeName(v) + " 不可 JSON 序列化")
}

// langGoJSONStringify 走 encoding/json：键码点序、HTML 转义、int64 全精度、
// float 按 encoding/json 形、NaN/Inf 报错（RUNTIME-JSON-002 硬契约）
func langGoJSONStringify(v any) string {
	bs, err := json.Marshal(langGoJSONTo(v))
	if err != nil {
		panic("JSON 序列化失败: " + err.Error())
	}
	return string(bs)
}
"##;
