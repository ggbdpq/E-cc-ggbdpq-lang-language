package gen

// prelude 内联进每个转译产物：ling 语义的 Go 运行时镜像。
// 格式化（lingFmt 族）必须与解释器 value.go 的 fmtVal 逐字节一致——等价测试依赖它。
const prelude = `package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"
)

// ———— 值表示 ————

type lingUnit struct{}

// LingStringer：所有复合值实现它，lingFmt 据此统一格式化
type lingStringer interface {
	lingString(top bool) string
}

// LingErr 对应 ling 的 struct error { msg: string }
type LingErr struct{ Msg string }

func (e *LingErr) Error() string            { return e.Msg }
func (e *LingErr) lingString(top bool) string {
	return "error{msg: " + strconv.Quote(e.Msg) + "}"
}

// LingEnum 对应一切用户 enum：case 名 + 装箱载荷
type LingEnum struct {
	Tag  string
	Vals []any
}

func (e LingEnum) lingString(top bool) string {
	if len(e.Vals) == 0 {
		return e.Tag
	}
	parts := make([]string, len(e.Vals))
	for i, v := range e.Vals {
		parts[i] = lingFmt(v, false)
	}
	return e.Tag + "(" + strings.Join(parts, ", ") + ")"
}

// LingOpt 对应 Option[T]
type LingOpt[T any] struct {
	has bool
	v   T
}

func (o LingOpt[T]) lingString(top bool) string {
	if o.has {
		return "Some(" + lingFmt(o.v, false) + ")"
	}
	return "None"
}

func LingSome[T any](v T) LingOpt[T] { return LingOpt[T]{has: true, v: v} }
func LingNone[T any]() LingOpt[T]    { return LingOpt[T]{} }

// lingNone 供 JSON 序列化识别 None（接口分派，规避泛型进 type switch）
func (o LingOpt[T]) lingNone() bool { return !o.has }

// LingRes 对应 Result[T, error]
type LingRes[T any] struct {
	ok bool
	v  T
	e  error
}

func (r LingRes[T]) lingString(top bool) string {
	if r.ok {
		return "Ok(" + lingFmt(r.v, false) + ")"
	}
	return "Err(" + lingFmt(r.e, false) + ")"
}

func LingOk[T any](v T) LingRes[T]        { return LingRes[T]{ok: true, v: v} }
func LingResErr[T any](e error) LingRes[T] { return LingRes[T]{e: e} }

// LingArr 对应 ling 数组：引用语义，push 原位可见
type LingArr[T any] struct{ Items []T }

func (a *LingArr[T]) Push(vs ...T) { a.Items = append(a.Items, vs...) }

// LingLen/LingGet 让 lingLen/lingIndex 能经接口分派到任意元素类型的实例
func (a *LingArr[T]) LingLen() int64 { return int64(len(a.Items)) }

func (a *LingArr[T]) LingGet(i int64) any { return a.Items[i] }

func (a *LingArr[T]) lingString(top bool) string {
	parts := make([]string, len(a.Items))
	for i, v := range a.Items {
		parts[i] = lingFmt(v, false)
	}
	return "[" + strings.Join(parts, ", ") + "]"
}

// LingMap 对应 ling 映射：插入序保持
type lingKV struct{ K, V any }

type LingMap struct {
	keys []any
	m    map[string]any
}

func lingNewMap(kvs ...lingKV) *LingMap {
	m := &LingMap{m: map[string]any{}}
	for _, kv := range kvs {
		m.Set(kv.K, kv.V)
	}
	return m
}

func lingKeyString(k any) (string, bool) {
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

func (m *LingMap) Set(k, v any) {
	ks, ok := lingKeyString(k)
	if !ok {
		panic("映射键须为 int/string/bool")
	}
	if _, exists := m.m[ks]; !exists {
		m.keys = append(m.keys, k)
	}
	m.m[ks] = v
}

func (m *LingMap) Keys() []any { return append([]any(nil), m.keys...) }
func (m *LingMap) Len() int    { return len(m.keys) }

func (m *LingMap) lingString(top bool) string {
	parts := make([]string, 0, len(m.keys))
	for _, k := range m.keys {
		ks, _ := lingKeyString(k)
		parts = append(parts, lingFmt(k, false)+": "+lingFmt(m.m[ks], false))
	}
	return "{" + strings.Join(parts, ", ") + "}"
}

// lingRange 对应区间 lo..hi（半开）
type lingRange struct{ Lo, Hi int64 }

func (r lingRange) lingString(top bool) string {
	return strconv.FormatInt(r.Lo, 10) + ".." + strconv.FormatInt(r.Hi, 10)
}

// ———— 格式化（镜像解释器 fmtVal）———

func lingFmt(v any, top bool) string {
	switch x := v.(type) {
	case nil:
		return "()"
	case lingUnit:
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
	if s, ok := v.(lingStringer); ok {
		return s.lingString(top)
	}
	return "?"
}

func lingFmtArgs(args []any) string {
	parts := make([]string, len(args))
	for i, a := range args {
		parts[i] = lingFmt(a, true)
	}
	return strings.Join(parts, " ")
}

func lingPrintln(args ...any) { fmt.Println(lingFmtArgs(args)) }
func lingPrint(args ...any)   { fmt.Print(lingFmtArgs(args)) }

// ———— 内建函数 ————

func lingLen(v any) int64 {
	switch x := v.(type) {
	case string:
		return int64(len([]rune(x)))
	case *LingMap:
		return int64(x.Len())
	case lingRange:
		if x.Hi > x.Lo {
			return x.Hi - x.Lo
		}
		return 0
	}
	if arr, ok := v.(interface {
		LingLen() int64
	}); ok {
		return arr.LingLen()
	}
	panic("len 不支持该类型")
}

func lingStr(v any) string { return lingFmt(v, true) }

func lingInt(v any) int64 {
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

func lingFloat(v any) float64 {
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

func lingKeys(v any) *LingArr[any] {
	if m, ok := v.(*LingMap); ok {
		return &LingArr[any]{Items: m.Keys()}
	}
	panic("keys 需要映射")
}

func lingValues(v any) *LingArr[any] {
	if m, ok := v.(*LingMap); ok {
		vs := make([]any, 0, len(m.keys))
		for _, k := range m.keys {
			ks, _ := lingKeyString(k)
			vs = append(vs, m.m[ks])
		}
		return &LingArr[any]{Items: vs}
	}
	panic("values 需要映射")
}

func lingHas(v, k any) bool {
	if m, ok := v.(*LingMap); ok {
		ks, ok := lingKeyString(k)
		if !ok {
			return false
		}
		_, found := m.m[ks]
		return found
	}
	panic("has 需要映射")
}

func lingPush(v any, vs ...any) {
	a, ok := v.(*LingArr[any])
	if !ok {
		panic("push 需要数组")
	}
	a.Items = append(a.Items, vs...)
}

func lingAssert(cond bool, msg ...any) {
	if !cond {
		if len(msg) > 0 {
			panic("assert 失败: " + lingFmt(msg[0], true))
		}
		panic("assert 失败")
	}
}

func lingErr(msg string) error { return &LingErr{Msg: msg} }

func lingErrMsg(e error) string {
	if le, ok := e.(*LingErr); ok {
		return le.Msg
	}
	return e.Error()
}

// lingBytes 字符串的 UTF-8 字节序列（MD5/URL 编码用）
func lingBytes(v any) *LingArr[any] {
	s, ok := v.(string)
	if !ok {
		panic("bytes 需要字符串")
	}
	bs := []byte(s)
	items := make([]any, len(bs))
	for i, b := range bs {
		items[i] = int64(b)
	}
	return &LingArr[any]{Items: items}
}

// lingChar 码点转单字符字符串
func lingChar(v any) string {
	n, ok := v.(int64)
	if !ok {
		panic("char 需要 int")
	}
	return string(rune(n))
}

// lingStrVal + 号任一侧为 any 时收窄为字符串（非字符串即运行时错误，镜像解释器）
func lingStrVal(v any) string {
	s, ok := v.(string)
	if !ok {
		panic("+ 需要字符串，得到 " + lingTypeName(v))
	}
	return s
}

// lingCompare 大小比较（任一侧为 any 时仍可运行时分派；镜像解释器 compare）
func lingCompare(a, b any) int64 {
	switch x := a.(type) {
	case int64:
		y, ok := b.(int64)
		if !ok {
			panic(lingTypeName(a) + " 与 " + lingTypeName(b) + " 不可比较大小")
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
			panic(lingTypeName(a) + " 与 " + lingTypeName(b) + " 不可比较大小")
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
			panic(lingTypeName(a) + " 与 " + lingTypeName(b) + " 不可比较大小")
		}
		return int64(strings.Compare(x, y))
	}
	panic(lingTypeName(a) + " 与 " + lingTypeName(b) + " 不可比较大小")
}

// ———— 运算与索引 ————

// lingEq 镜像解释器 equals：标量同型、enum 同 case 递归；容器不可比
func lingEq(a, b any) bool {
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
	case LingEnum:
		y, ok := b.(LingEnum)
		if !ok || y.Tag != x.Tag || len(y.Vals) != len(x.Vals) {
			return false
		}
		for i := range x.Vals {
			if !lingEq(x.Vals[i], y.Vals[i]) {
				return false
			}
		}
		return true
	}
	if eq, ok := a.(interface{ lingEqTo(any) bool }); ok {
		return eq.lingEqTo(b)
	}
	panic(lingTypeName(a) + " 不可用 == 比较")
}

func lingTypeName(v any) string {
	switch v.(type) {
	case int64:
		return "int"
	case float64:
		return "float"
	case bool:
		return "bool"
	case string:
		return "string"
	case *LingArr[any]:
		return "数组"
	case *LingMap:
		return "映射"
	case lingRange:
		return "区间"
	case LingEnum:
		return "enum"
	case *LingErr:
		return "error"
	}
	return "未知值"
}

// LingOpt/LingRes 的相等：泛型类型无法进 type switch，走方法
func (o LingOpt[T]) lingEqTo(other any) bool {
	y, ok := other.(LingOpt[T])
	if !ok || o.has != y.has {
		return false
	}
	if !o.has {
		return true
	}
	return lingEq(o.v, y.v)
}

func (r LingRes[T]) lingEqTo(other any) bool {
	y, ok := other.(LingRes[T])
	if !ok || r.ok != y.ok {
		return false
	}
	if !r.ok {
		return lingEq(r.e, y.e)
	}
	return lingEq(r.v, y.v)
}

func lingTruthy(v any) bool {
	b, ok := v.(bool)
	if !ok {
		panic("条件须为 bool")
	}
	return b
}

func lingIndex(obj, idx any) any {
	switch c := obj.(type) {
	case *LingMap:
		ks, ok := lingKeyString(idx)
		if !ok {
			panic("映射键须为 int/string/bool")
		}
		v, found := c.m[ks]
		if !found {
			panic("映射无键 " + lingFmt(idx, true) + "（用 has() 探测）")
		}
		return v
	case string:
		runes := []rune(c)
		i := lingInt(idx)
		if i < 0 || i >= int64(len(runes)) {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, len(runes)))
		}
		return string(runes[i])
	}
	if arr, ok := obj.(interface {
		LingGet(int64) any
		LingLen() int64
	}); ok {
		i := lingInt(idx)
		if i < 0 || i >= arr.LingLen() {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, arr.LingLen()))
		}
		return arr.LingGet(i)
	}
	panic("该类型不可索引")
}

func lingSetIndex(obj, idx, v any) {
	switch c := obj.(type) {
	case *LingArr[any]:
		i := lingInt(idx)
		if i < 0 || i >= int64(len(c.Items)) {
			panic(fmt.Sprintf("索引 %d 越界（长度 %d）", i, len(c.Items)))
		}
		c.Items[i] = v
		return
	case *LingMap:
		c.Set(idx, v)
		return
	case string:
		panic("字符串不可变")
	}
	panic("该类型不可索引赋值")
}

func lingSlice(obj, lo, hi any) any {
	i, j := lingInt(lo), lingInt(hi)
	switch c := obj.(type) {
	case *LingArr[any]:
		if i < 0 || j < i || j > int64(len(c.Items)) {
			panic(fmt.Sprintf("切片 [%d:%d] 越界（长度 %d）", i, j, len(c.Items)))
		}
		items := make([]any, j-i)
		copy(items, c.Items[i:j])
		return &LingArr[any]{Items: items}
	case string:
		runes := []rune(c)
		if i < 0 || j < i || j > int64(len(runes)) {
			panic(fmt.Sprintf("切片 [%d:%d] 越界（长度 %d）", i, j, len(runes)))
		}
		return string(runes[i:j])
	}
	panic("该类型不可切片")
}

func lingTo[T any](v any) T { return v.(T) }

// ———— 标准库委托：json / http（镜像解释器 stdlib.go，等价测试锁定） ————

type lingFielder interface{ LingField(name string) any }

func lingField(obj any, name string) any {
	if f, ok := obj.(lingFielder); ok {
		return f.LingField(name)
	}
	panic(lingTypeName(obj) + " 没有字段 " + name)
}

// LingHttpResp 对应 ling 预载 struct HttpResp{status, body}
type LingHttpResp struct {
	status int64
	body   string
}

func (v LingHttpResp) lingString(top bool) string {
	return "HttpResp{status: " + strconv.FormatInt(v.status, 10) +
		", body: " + strconv.Quote(v.body) + "}"
}

func (v LingHttpResp) LingField(name string) any {
	switch name {
	case "status":
		return v.status
	case "body":
		return v.body
	}
	panic("HttpResp 没有字段 " + name)
}

type lingHttpSvc struct{}

func (lingHttpSvc) get(url, headers any) any {
	s, ok := url.(string)
	if !ok {
		panic("http.get 的 url 须为 string")
	}
	req, err := http.NewRequest("GET", s, nil)
	if err != nil {
		return LingRes[any]{e: lingErr("无效 URL: " + err.Error())}
	}
	if m, ok := headers.(*LingMap); ok {
		for _, k := range m.keys {
			ks, _ := lingKeyString(k)
			if v, ok := m.m[ks].(string); ok {
				req.Header.Set(k.(string), v)
			}
		}
	}
	resp, err := (&http.Client{Timeout: 30 * time.Second}).Do(req)
	if err != nil {
		return LingRes[any]{e: lingErr(err.Error())}
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return LingRes[any]{e: lingErr("读响应失败: " + err.Error())}
	}
	return LingRes[any]{ok: true, v: LingHttpResp{status: int64(resp.StatusCode), body: string(body)}}
}

var lingHTTP = lingHttpSvc{}

func lingJSONParse(v any) any {
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
	return lingJSONFrom(raw)
}

func lingJSONFrom(raw any) any {
	switch x := raw.(type) {
	case nil:
		return LingNone[any]()
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
			items = append(items, lingJSONFrom(el))
		}
		return &LingArr[any]{Items: items}
	case map[string]any:
		keys := make([]string, 0, len(x))
		for k := range x {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		m := lingNewMap()
		for _, k := range keys {
			m.Set(k, lingJSONFrom(x[k]))
		}
		return m
	}
	return lingUnit{}
}

func lingJSONTo(v any) any {
	switch x := v.(type) {
	case nil:
		return nil
	case lingUnit:
		return nil
	case bool, string:
		return x
	case int64, float64:
		return x
	case *LingArr[any]:
		out := make([]any, 0, len(x.Items))
		for _, el := range x.Items {
			out = append(out, lingJSONTo(el))
		}
		return out
	case *LingMap:
		out := map[string]any{}
		for _, k := range x.keys {
			out[k.(string)] = lingJSONTo(x.m["s:"+k.(string)])
		}
		return out
	}
	if o, ok := v.(interface{ lingNone() bool }); ok && o.lingNone() {
		return nil
	}
	panic(lingTypeName(v) + " 不可 JSON 序列化")
}

func lingJSONStringify(v any) string {
	bs, err := json.Marshal(lingJSONTo(v))
	if err != nil {
		panic("JSON 序列化失败: " + err.Error())
	}
	return string(bs)
}

// lingIsNone 判 None（JSON null 的 ling 表示）；经接口分派支持任意元素类型
func lingIsNone(v any) bool {
	if o, ok := v.(interface{ lingNone() bool }); ok {
		return o.lingNone()
	}
	return false
}

// lingIter 动态值（如 JSON 解析结果）的迭代快照：数组→元素，映射→键，字符串→字符
func lingIter(v any) *LingArr[any] {
	switch x := v.(type) {
	case *LingArr[any]:
		return x
	case *LingMap:
		return &LingArr[any]{Items: x.Keys()}
	case string:
		items := make([]any, 0, len(x))
		for _, r := range []rune(x) {
			items = append(items, string(r))
		}
		return &LingArr[any]{Items: items}
	case lingRange:
		items := make([]any, 0, x.Hi-x.Lo)
		for i := x.Lo; i < x.Hi; i++ {
			items = append(items, i)
		}
		return &LingArr[any]{Items: items}
	}
	if arr, ok := v.(interface {
		LingLen() int64
		LingGet(int64) any
	}); ok {
		items := make([]any, 0, arr.LingLen())
		for i := int64(0); i < arr.LingLen(); i++ {
			items = append(items, arr.LingGet(i))
		}
		return &LingArr[any]{Items: items}
	}
	panic("该类型不可迭代")
}
`
