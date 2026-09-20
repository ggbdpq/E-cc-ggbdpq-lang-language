package gen

// prelude 内联进每个转译产物：langGo 语义的 Go 运行时镜像。
// 格式化（langGoFmt 族）必须与解释器 value.go 的 fmtVal 逐字节一致——等价测试依赖它。
const prelude = `package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"sort"
	"strconv"
	"sync"
	"strings"
	"time"
)

// ———— 值表示 ————

type langGoUnit struct{}

// LangGoStringer：所有复合值实现它，langGoFmt 据此统一格式化
type langGoStringer interface {
	langGoString(top bool) string
}

// LangGoErr 对应 langGo 的 struct error { msg: string }
type LangGoErr struct{ Msg string }

func (e *LangGoErr) Error() string            { return e.Msg }
func (e *LangGoErr) langGoString(top bool) string {
	return "error{msg: " + strconv.Quote(e.Msg) + "}"
}

// LangGoEnum 对应一切用户 enum：case 名 + 装箱载荷
type LangGoEnum struct {
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

// LangGoOpt 对应 Option[T]
type LangGoOpt[T any] struct {
	has bool
	v   T
}

func (o LangGoOpt[T]) langGoString(top bool) string {
	if o.has {
		return "Some(" + langGoFmt(o.v, false) + ")"
	}
	return "None"
}

func LangGoSome[T any](v T) LangGoOpt[T] { return LangGoOpt[T]{has: true, v: v} }
func LangGoNone[T any]() LangGoOpt[T]    { return LangGoOpt[T]{} }

// langGoNone 供 JSON 序列化识别 None（接口分派，规避泛型进 type switch）
func (o LangGoOpt[T]) langGoNone() bool { return !o.has }

// LangGoRes 对应 Result[T, error]
type LangGoRes[T any] struct {
	ok bool
	v  T
	e  error
}

func (r LangGoRes[T]) langGoString(top bool) string {
	if r.ok {
		return "Ok(" + langGoFmt(r.v, false) + ")"
	}
	return "Err(" + langGoFmt(r.e, false) + ")"
}

func LangGoOk[T any](v T) LangGoRes[T]        { return LangGoRes[T]{ok: true, v: v} }
func LangGoResErr[T any](e error) LangGoRes[T] { return LangGoRes[T]{e: e} }

// LangGoArr 对应 langGo 数组：引用语义，push 原位可见
type LangGoArr[T any] struct{ Items []T }

func (a *LangGoArr[T]) Push(vs ...T) { a.Items = append(a.Items, vs...) }

// LangGoLen/LangGoGet 让 langGoLen/langGoIndex 能经接口分派到任意元素类型的实例
func (a *LangGoArr[T]) LangGoLen() int64 { return int64(len(a.Items)) }

func (a *LangGoArr[T]) LangGoGet(i int64) any { return a.Items[i] }

// LangGoSet 让 langGoSetIndex/langGoAddIndex 能经接口分派写任意元素类型的实例
func (a *LangGoArr[T]) LangGoSet(i int64, v any) { a.Items[i] = v.(T) }

func (a *LangGoArr[T]) langGoItems() []any {
	out := make([]any, len(a.Items))
	for i, v := range a.Items {
		out[i] = v
	}
	return out
}

func (a *LangGoArr[T]) langGoAppendItems(items []any) {
	for _, it := range items {
		a.Items = append(a.Items, it.(T))
	}
}

func (a *LangGoArr[T]) langGoString(top bool) string {
	parts := make([]string, len(a.Items))
	for i, v := range a.Items {
		parts[i] = langGoFmt(v, false)
	}
	return "[" + strings.Join(parts, ", ") + "]"
}

// LangGoMap 对应 langGo 映射：插入序保持
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

func langGoKeyString(k any) (string, bool) {
	switch x := k.(type) {
	case int64:
		return fmt.Sprintf("i:%d", x), true
	case string:
		return "s:" + x, true
	case bool:
		return fmt.Sprintf("b:%v", x), true
	}
	return "", false
}

func (m *LangGoMap) Set(k, v any) {
	ks, ok := langGoKeyString(k)
	if !ok {
		panic("映射键须为 int/string/bool")
	}
	if _, exists := m.m[ks]; !exists {
		m.keys = append(m.keys, k)
	}
	m.m[ks] = v
}

func (m *LangGoMap) Keys() []any { return append([]any(nil), m.keys...) }
func (m *LangGoMap) Len() int    { return len(m.keys) }

func (m *LangGoMap) langGoString(top bool) string {
	parts := make([]string, 0, len(m.keys))
	for _, k := range m.keys {
		ks, _ := langGoKeyString(k)
		parts = append(parts, langGoFmt(k, false)+": "+langGoFmt(m.m[ks], false))
	}
	return "{" + strings.Join(parts, ", ") + "}"
}

// langGoRange 对应区间 lo..hi（半开）
type langGoRange struct{ Lo, Hi int64 }

func (r langGoRange) langGoString(top bool) string {
	return strconv.FormatInt(r.Lo, 10) + ".." + strconv.FormatInt(r.Hi, 10)
}

// ———— 格式化（镜像解释器 fmtVal）———

func langGoFmt(v any, top bool) string {
	switch x := v.(type) {
	case nil:
		return "()"
	case langGoUnit:
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

// ———— 内建函数 ————

func langGoLen(v any) int64 {
	switch x := v.(type) {
	case string:
		return int64(len([]rune(x)))
	case *LangGoMap:
		return int64(x.Len())
	case langGoRange:
		if x.Hi > x.Lo {
			return x.Hi - x.Lo
		}
		return 0
	}
	if arr, ok := v.(interface {
		LangGoLen() int64
	}); ok {
		return arr.LangGoLen()
	}
	panic("len 不支持该类型")
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
	panic("int() 不支持该类型")
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
	panic("float() 不支持该类型")
}

func langGoKeys(v any) *LangGoArr[any] {
	if m, ok := v.(*LangGoMap); ok {
		return &LangGoArr[any]{Items: m.Keys()}
	}
	panic("keys 需要映射")
}

func langGoValues(v any) *LangGoArr[any] {
	if m, ok := v.(*LangGoMap); ok {
		vs := make([]any, 0, len(m.keys))
		for _, k := range m.keys {
			ks, _ := langGoKeyString(k)
			vs = append(vs, m.m[ks])
		}
		return &LangGoArr[any]{Items: vs}
	}
	panic("values 需要映射")
}

func langGoHas(v, k any) bool {
	if m, ok := v.(*LangGoMap); ok {
		ks, ok := langGoKeyString(k)
		if !ok {
			return false
		}
		_, found := m.m[ks]
		return found
	}
	panic("has 需要映射")
}

func langGoPush(v any, vs ...any) {
	a, ok := v.(*LangGoArr[any])
	if !ok {
		panic("push 需要数组")
	}
	a.Items = append(a.Items, vs...)
}

func langGoAssert(cond bool, msg ...any) {
	if !cond {
		if len(msg) > 0 {
			panic("assert 失败: " + langGoFmt(msg[0], true))
		}
		panic("assert 失败")
	}
}

func langGoErr(msg string) error { return &LangGoErr{Msg: msg} }

func langGoErrMsg(e error) string {
	if le, ok := e.(*LangGoErr); ok {
		return le.Msg
	}
	return e.Error()
}

// langGoBytes 字符串的 UTF-8 字节序列（MD5/URL 编码用）
func langGoBytes(v any) *LangGoArr[any] {
	s, ok := v.(string)
	if !ok {
		panic("bytes 需要字符串")
	}
	bs := []byte(s)
	items := make([]any, len(bs))
	for i, b := range bs {
		items[i] = int64(b)
	}
	return &LangGoArr[any]{Items: items}
}

// langGoPushFrom 数组 += ：把 another 的元素原位追加（引用语义，别名可见）
func langGoPushFrom[T, U any](arr *LangGoArr[T], another *LangGoArr[U]) *LangGoArr[T] {
	for _, it := range another.Items {
		var box any = it
		a, ok := box.(T)
		if !ok {
			panic("数组 += 元素类型不符")
		}
		arr.Items = append(arr.Items, a)
	}
	return arr
}

func langGoPushFromAny(arr, another any) any {
	if src, ok := another.(interface{ langGoItems() []any }); ok {
		if dst, ok := arr.(interface{ langGoAppendItems([]any) }); ok {
			dst.langGoAppendItems(src.langGoItems())
			return arr
		}
	}
	panic("数组 += 需要数组")
}

// langGoAddIndex 映射复合赋值：读旧值按运算符改写
func langGoAddIndex(obj, idx, v any, op string) {
	if a, ok := obj.(interface {
		LangGoGet(int64) any
		LangGoLen() int64
		LangGoSet(int64, any)
	}); ok {
		i := langGoInt(idx)
		if i < 0 || i >= a.LangGoLen() {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, a.LangGoLen()))
		}
		old := a.LangGoGet(i)
		a.LangGoSet(i, langGoArith(op, old, v))
		return
	}
	if m, ok := obj.(*LangGoMap); ok {
		ks, _ := langGoKeyString(idx)
		old := m.m[ks]
		m.Set(idx, langGoArith(op, old, v))
		return
	}
	panic("该容器不支持复合赋值")
}

// langGoArith 复合赋值读改写运算（+= -= *= /=；语义与 evalBinary 对应分支一致）
func langGoArith(op string, a, b any) any {
	switch op {
	case "+=":
		return langGoAdd(a, b)
	case "-=":
		switch x := a.(type) {
		case int64:
			if y, ok := b.(int64); ok {
				return x - y
			}
		case float64:
			if y, ok := b.(float64); ok {
				return x - y
			}
		}
	case "*=":
		switch x := a.(type) {
		case int64:
			if y, ok := b.(int64); ok {
				return x * y
			}
		case float64:
			if y, ok := b.(float64); ok {
				return x * y
			}
		}
	case "/=":
		switch x := a.(type) {
		case int64:
			if y, ok := b.(int64); ok {
				if y == 0 {
					panic("整数除零")
				}
				return x / y
			}
		case float64:
			if y, ok := b.(float64); ok {
				return x / y
			}
		}
	}
	panic("复合赋值类型不符")
}

// langGoAdd 数值加法（Map 复合赋值用；int64/float64/string）
func langGoAdd(a, b any) any {
	switch x := a.(type) {
	case int64:
		if y, ok := b.(int64); ok {
			return x + y
		}
	case float64:
		if y, ok := b.(float64); ok {
			return x + y
		}
	case string:
		if y, ok := b.(string); ok {
			return x + y
		}
	}
	panic("+= 类型不符")
}

// —— 并发：chan（阻塞 recv 语义：recv 等待直到有值；send 不阻塞、缓冲上限 1024） ————

func langGoNewChan() chan any { return make(chan any, 1024) }

func langGoSend(ch chan any, v any) {
	select {
	case ch <- v:
	default:
		panic("channel 缓冲已满（1024）")
	}
}

// langGoRecv 阻塞直到有值（decisions #63）；无人 send 的死锁程序由 Go runtime panic 兜底
func langGoRecv(ch chan any) LangGoRes[any] {
	v := <-ch
	return LangGoOk[any](v)
}

// —— 并发：go 语句 ——

var langGoWG sync.WaitGroup

func langGoGo(f func()) {
	langGoWG.Add(1)
	go func() {
		defer langGoWG.Done()
		f()
	}()
}

func langGoWaitAll() { langGoWG.Wait() }

// langGoChar 码点转单字符字符串
func langGoChar(v any) string {
	n, ok := v.(int64)
	if !ok {
		panic("char 需要 int")
	}
	return string(rune(n))
}

// langGoStrVal + 号任一侧为 any 时收窄为字符串（非字符串即运行时错误，镜像解释器）
func langGoStrVal(v any) string {
	s, ok := v.(string)
	if !ok {
		panic("+ 需要字符串，得到 " + langGoTypeName(v))
	}
	return s
}

// langGoCompare 大小比较（任一侧为 any 时仍可运行时分派；镜像解释器 compare）
func langGoCompare(a, b any) int64 {
	switch x := a.(type) {
	case int64:
		y, ok := b.(int64)
		if !ok {
			panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小")
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
			panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小")
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
			panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小")
		}
		return int64(strings.Compare(x, y))
	}
	panic(langGoTypeName(a) + " 与 " + langGoTypeName(b) + " 不可比较大小")
}

// ———— 运算与索引 ————

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
	if eq, ok := a.(interface{ langGoEqTo(any) bool }); ok {
		return eq.langGoEqTo(b)
	}
	panic(langGoTypeName(a) + " 不可用 == 比较")
}

func langGoTypeName(v any) string {
	switch v.(type) {
	case int64:
		return "int"
	case float64:
		return "float"
	case bool:
		return "bool"
	case string:
		return "string"
	case *LangGoArr[any]:
		return "数组"
	case *LangGoMap:
		return "映射"
	case langGoRange:
		return "区间"
	case LangGoEnum:
		return "enum"
	case *LangGoErr:
		return "error"
	}
	return "未知值"
}

// LangGoOpt/LangGoRes 的相等：泛型类型无法进 type switch，走方法
func (o LangGoOpt[T]) langGoEqTo(other any) bool {
	y, ok := other.(LangGoOpt[T])
	if !ok || o.has != y.has {
		return false
	}
	if !o.has {
		return true
	}
	return langGoEq(o.v, y.v)
}

func (r LangGoRes[T]) langGoEqTo(other any) bool {
	y, ok := other.(LangGoRes[T])
	if !ok || r.ok != y.ok {
		return false
	}
	if !r.ok {
		return langGoEq(r.e, y.e)
	}
	return langGoEq(r.v, y.v)
}

func langGoTruthy(v any) bool {
	b, ok := v.(bool)
	if !ok {
		panic("条件须为 bool")
	}
	return b
}

// langGoIndex 泛型化：调用点带显式类型实参（emitIndex 按元素类型特化），
// 泛型函数内的类型参数（langGoIndex[T]）据此把元素以 T 交还调用侧，
// 使 LangGoSome/LangGoNone 与泛型签名 LangGoOpt[T] 全链一致
func langGoIndex[T any](obj, idx any) T {
	switch c := obj.(type) {
	case *LangGoMap:
		ks, ok := langGoKeyString(idx)
		if !ok {
			panic("映射键须为 int/string/bool")
		}
		v, found := c.m[ks]
		if !found {
			panic("映射无键 " + langGoFmt(idx, true) + "（用 has() 探测）")
		}
		return any(v).(T)
	case string:
		runes := []rune(c)
		i := langGoInt(idx)
		if i < 0 || i >= int64(len(runes)) {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, len(runes)))
		}
		return any(string(runes[i])).(T)
	}
	if arr, ok := obj.(interface {
		LangGoGet(int64) any
		LangGoLen() int64
	}); ok {
		i := langGoInt(idx)
		if i < 0 || i >= arr.LangGoLen() {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, arr.LangGoLen()))
		}
		return any(arr.LangGoGet(i)).(T)
	}
	panic("该类型不可索引")
}

func langGoSetIndex(obj, idx, v any) {
	if a, ok := obj.(interface {
		LangGoGet(int64) any
		LangGoLen() int64
		LangGoSet(int64, any)
	}); ok {
		i := langGoInt(idx)
		if i < 0 || i >= a.LangGoLen() {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, a.LangGoLen()))
		}
		a.LangGoSet(i, v)
		return
	}
	if m, ok := obj.(*LangGoMap); ok {
		m.Set(idx, v)
		return
	}
	if _, ok := obj.(string); ok {
		panic("字符串不可变")
	}
	panic("该类型不可索引赋值")
}

func langGoSlice(obj, lo, hi any) any {
	i, j := langGoInt(lo), langGoInt(hi)
	switch c := obj.(type) {
	case *LangGoArr[any]:
		if i < 0 || j < i || j > int64(len(c.Items)) {
			panic(fmt.Sprintf("切片 [%d:%d] 越界（长度 %d）", i, j, len(c.Items)))
		}
		items := make([]any, j-i)
		copy(items, c.Items[i:j])
		return &LangGoArr[any]{Items: items}
	case string:
		runes := []rune(c)
		if i < 0 || j < i || j > int64(len(runes)) {
			panic(fmt.Sprintf("切片 [%d:%d] 越界（长度 %d）", i, j, len(runes)))
		}
		return string(runes[i:j])
	}
	panic("该类型不可切片")
}

func langGoTo[T any](v any) T { return v.(T) }

// ———— 标准库委托：json / http（镜像解释器 stdlib.go，等价测试锁定） ————

type langGoFielder interface{ LangGoField(name string) any }

func langGoField(obj any, name string) any {
	if f, ok := obj.(langGoFielder); ok {
		return f.LangGoField(name)
	}
	panic(langGoTypeName(obj) + " 没有字段 " + name)
}

// LangGoHttpResp 对应 langGo 预载 struct HttpResp{status, body}
type LangGoHttpResp struct {
	status int64
	body   string
}

func (v LangGoHttpResp) langGoString(top bool) string {
	return "HttpResp{status: " + strconv.FormatInt(v.status, 10) +
		", body: " + strconv.Quote(v.body) + "}"
}

func (v LangGoHttpResp) LangGoField(name string) any {
	switch name {
	case "status":
		return v.status
	case "body":
		return v.body
	}
	panic("HttpResp 没有字段 " + name)
}

type langGoHttpSvc struct{}

func (langGoHttpSvc) get(url, headers any) any {
	s, ok := url.(string)
	if !ok {
		panic("http.get 的 url 须为 string")
	}
	req, err := http.NewRequest("GET", s, nil)
	if err != nil {
		return LangGoRes[any]{e: langGoErr("无效 URL: " + err.Error())}
	}
	if m, ok := headers.(*LangGoMap); ok {
		for _, k := range m.keys {
			ks, _ := langGoKeyString(k)
			if v, ok := m.m[ks].(string); ok {
				req.Header.Set(k.(string), v)
			}
		}
	}
	resp, err := (&http.Client{Timeout: 30 * time.Second}).Do(req)
	if err != nil {
		return LangGoRes[any]{e: langGoErr(err.Error())}
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return LangGoRes[any]{e: langGoErr("读响应失败: " + err.Error())}
	}
	return LangGoRes[any]{ok: true, v: LangGoHttpResp{status: int64(resp.StatusCode), body: string(body)}}
}

var langGoHTTP = langGoHttpSvc{}

func langGoJSONParse(v any) any {
	s, ok := v.(string)
	if !ok {
		panic("jsonParse 需要字符串")
	}
	dec := json.NewDecoder(bytes.NewReader([]byte(s)))
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
		return LangGoNone[any]()
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
		return &LangGoArr[any]{Items: items}
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
	return langGoUnit{}
}

func langGoJSONTo(v any) any {
	switch x := v.(type) {
	case nil:
		return nil
	case langGoUnit:
		return nil
	case bool, string:
		return x
	case int64, float64:
		return x
	case *LangGoArr[any]:
		out := make([]any, 0, len(x.Items))
		for _, el := range x.Items {
			out = append(out, langGoJSONTo(el))
		}
		return out
	case *LangGoMap:
		out := map[string]any{}
		for _, k := range x.keys {
			out[k.(string)] = langGoJSONTo(x.m["s:"+k.(string)])
		}
		return out
	}
	if o, ok := v.(interface{ langGoNone() bool }); ok && o.langGoNone() {
		return nil
	}
	panic(langGoTypeName(v) + " 不可 JSON 序列化")
}

func langGoJSONStringify(v any) string {
	bs, err := json.Marshal(langGoJSONTo(v))
	if err != nil {
		panic("JSON 序列化失败: " + err.Error())
	}
	return string(bs)
}

// langGoIsNone 判 None（JSON null 的 langGo 表示）；经接口分派支持任意元素类型
func langGoIsNone(v any) bool {
	if o, ok := v.(interface{ langGoNone() bool }); ok {
		return o.langGoNone()
	}
	return false
}

// langGoIter 动态值（如 JSON 解析结果）的迭代快照：数组→元素，映射→键，字符串→字符
func langGoIter(v any) *LangGoArr[any] {
	switch x := v.(type) {
	case *LangGoArr[any]:
		return x
	case *LangGoMap:
		return &LangGoArr[any]{Items: x.Keys()}
	case string:
		items := make([]any, 0, len(x))
		for _, r := range []rune(x) {
			items = append(items, string(r))
		}
		return &LangGoArr[any]{Items: items}
	case langGoRange:
		items := make([]any, 0, x.Hi-x.Lo)
		for i := x.Lo; i < x.Hi; i++ {
			items = append(items, i)
		}
		return &LangGoArr[any]{Items: items}
	}
	if arr, ok := v.(interface {
		LangGoLen() int64
		LangGoGet(int64) any
	}); ok {
		items := make([]any, 0, arr.LangGoLen())
		for i := int64(0); i < arr.LangGoLen(); i++ {
			items = append(items, arr.LangGoGet(i))
		}
		return &LangGoArr[any]{Items: items}
	}
	panic("该类型不可迭代")
}
`
