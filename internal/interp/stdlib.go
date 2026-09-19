package interp

// 标准库委托最小面（spec §4，decisions #35–37）：
// json/http 在解释器侧直接用 Go stdlib 实现，→Go 侧由 gen/prelude.go
// 提供行为一致的同名实现——两端输出等价由 gen 的 E2E 测试锁定。

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"sort"
	"strconv"
	"time"
)

// preludeStdlib 预载 HttpResp / http 服务值与 json 内建
func (it *Interp) preludeStdlib() {
	it.structs["HttpResp"] = &StructDef{
		Name:   "HttpResp",
		Fields: []string{"status", "body"},
	}
	httpDef := &StructDef{Name: "http", Fields: []string{"get"}, Methods: map[string]*Closure{}}
	it.structs["http"] = httpDef
	it.Globals.redefine("http", &StructVal{
		Def:    httpDef,
		Fields: []Value{&Builtin{Name: "http.get", Fn: httpGet}},
	})

	reg := func(name string, fn func(it *Interp, args []Value) (Value, error)) {
		it.Globals.redefine(name, &Builtin{Name: name, Fn: fn})
	}
	reg("jsonParse", jsonParseBuiltin)
	reg("jsonStringify", jsonStringifyBuiltin)
	reg("makeChan", func(it *Interp, args []Value) (Value, error) {
		if len(args) != 0 {
			return nil, fmt.Errorf("makeChan 不需要参数")
		}
		return &ChanVal{}, nil
	})
	reg("send", func(it *Interp, args []Value) (Value, error) {
		if len(args) != 2 {
			return nil, fmt.Errorf("send 需要 2 个参数（ch, v），得到 %d 个", len(args))
		}
		ch, ok := args[0].(*ChanVal)
		if !ok {
			return nil, fmt.Errorf("send 需要通道，得到 %s", typeName(args[0]))
		}
		ch.Items = append(ch.Items, args[1])
		return UnitVal{}, nil
	})
	reg("recv", func(it *Interp, args []Value) (Value, error) {
		if len(args) != 1 {
			return nil, fmt.Errorf("recv 需要 1 个参数（ch），得到 %d 个", len(args))
		}
		ch, ok := args[0].(*ChanVal)
		if !ok {
			return nil, fmt.Errorf("recv 需要通道，得到 %s", typeName(args[0]))
		}
		if len(ch.Items) == 0 {
			return errResult(it, "channel 空"), nil
		}
		v := ch.Items[0]
		ch.Items = ch.Items[1:]
		return &EnumVal{Def: it.enums["Result"], Case: 0, Payloads: []Value{v}}, nil
	})
	reg("isNone", func(it *Interp, args []Value) (Value, error) {
		if err := it.arity("isNone", args, 1); err != nil {
			return nil, err
		}
		if ev, ok := args[0].(*EnumVal); ok {
			return BoolVal(ev.Def.Name == "Option" && ev.Case == 1), nil
		}
		return BoolVal(false), nil
	})
}

// —— JSON ——

func jsonParseBuiltin(it *Interp, args []Value) (Value, error) {
	if err := it.arity("jsonParse", args, 1); err != nil {
		return nil, err
	}
	s, ok := args[0].(StrVal)
	if !ok {
		return nil, fmt.Errorf("jsonParse 需要字符串，得到 %s", typeName(args[0]))
	}
	dec := json.NewDecoder(bytes.NewReader([]byte(s)))
	dec.UseNumber()
	var raw any
	if err := dec.Decode(&raw); err != nil {
		return nil, fmt.Errorf("JSON 解析失败: %v", err)
	}
	return jsonToValue(raw, it), nil
}

// jsonToValue Go 的 JSON 表示 → langGo 值；null → None（langGo 无 null，decisions #12）
func jsonToValue(raw any, it *Interp) Value {
	switch x := raw.(type) {
	case nil:
		return &EnumVal{Def: it.enums["Option"], Case: 1}
	case bool:
		return BoolVal(x)
	case string:
		return StrVal(x)
	case json.Number:
		if n, err := strconv.ParseInt(x.String(), 10, 64); err == nil {
			return IntVal(n)
		}
		f, _ := strconv.ParseFloat(x.String(), 64)
		return FloatVal(f)
	case []any:
		items := make([]Value, 0, len(x))
		for _, el := range x {
			items = append(items, jsonToValue(el, it))
		}
		return &ArrVal{Items: items}
	case map[string]any:
		m := &MapVal{}
		keys := make([]string, 0, len(x))
		for k := range x {
			keys = append(keys, k)
		}
		sort.Strings(keys) // Go map 序随机，排序保证两端一致
		for _, k := range keys {
			_ = m.Set(StrVal(k), jsonToValue(x[k], it))
		}
		return m
	}
	return UnitVal{}
}

// valueToJSON langGo 值 → 可 json.Marshal 的 Go 值；None → null
func valueToJSON(v Value) (any, error) {
	switch x := v.(type) {
	case UnitVal:
		return nil, nil
	case BoolVal:
		return bool(x), nil
	case IntVal:
		return int64(x), nil
	case FloatVal:
		return float64(x), nil
	case StrVal:
		return string(x), nil
	case *EnumVal:
		if x.Def.Name == "Option" && x.Case == 1 {
			return nil, nil
		}
		return nil, fmt.Errorf("%s 不可 JSON 序列化", x.Def.Cases[x.Case])
	case *ArrVal:
		out := make([]any, 0, len(x.Items))
		for _, el := range x.Items {
			j, err := valueToJSON(el)
			if err != nil {
				return nil, err
			}
			out = append(out, j)
		}
		return out, nil
	case *MapVal:
		out := map[string]any{}
		for _, k := range x.Keys() {
			ks, ok := k.(StrVal)
			if !ok {
				return nil, fmt.Errorf("JSON 对象键须为 string，得到 %s", typeName(k))
			}
			j, err := valueToJSON(mustGet(x, k))
			if err != nil {
				return nil, err
			}
			out[string(ks)] = j
		}
		return out, nil
	}
	return nil, fmt.Errorf("%s 不可 JSON 序列化", typeName(v))
}

func mustGet(m *MapVal, k Value) Value {
	v, _ := m.Get(k)
	return v
}

func jsonStringifyBuiltin(it *Interp, args []Value) (Value, error) {
	if err := it.arity("jsonStringify", args, 1); err != nil {
		return nil, err
	}
	j, err := valueToJSON(args[0])
	if err != nil {
		return nil, err
	}
	bs, err := json.Marshal(j) // map 键按字母序，两端确定
	if err != nil {
		return nil, fmt.Errorf("JSON 序列化失败: %v", err)
	}
	return StrVal(bs), nil
}

// —— HTTP ——

var httpClient = &http.Client{Timeout: 30 * time.Second}

// httpGet(url, headers) → Result[HttpResp, error]：网络错误=Err，任何响应（含 4xx/5xx）=Ok
func httpGet(it *Interp, args []Value) (Value, error) {
	if len(args) != 2 {
		return nil, fmt.Errorf("http.get 需要 2 个参数（url, headers），得到 %d 个", len(args))
	}
	url, ok := args[0].(StrVal)
	if !ok {
		return nil, fmt.Errorf("http.get 的 url 须为 string，得到 %s", typeName(args[0]))
	}
	var hdrs map[string]string
	if _, isUnit := args[1].(UnitVal); !isUnit {
		m, ok := args[1].(*MapVal)
		if !ok {
			return nil, fmt.Errorf("http.get 的 headers 须为 map[string]string，得到 %s", typeName(args[1]))
		}
		hdrs = map[string]string{}
		for _, k := range m.Keys() {
			ks, _ := k.(StrVal)
			v, _ := m.Get(k)
			if vs, ok := v.(StrVal); ok {
				hdrs[string(ks)] = string(vs)
			}
		}
	}

	req, err := http.NewRequest("GET", string(url), nil)
	if err != nil {
		return errResult(it, "无效 URL: "+err.Error()), nil
	}
	for k, v := range hdrs {
		req.Header.Set(k, v)
	}
	resp, err := httpClient.Do(req)
	if err != nil {
		return errResult(it, err.Error()), nil
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return errResult(it, "读响应失败: "+err.Error()), nil
	}
	return &EnumVal{
		Def:      it.enums["Result"],
		Case:     0,
		Payloads: []Value{&StructVal{Def: it.structs["HttpResp"], Fields: []Value{IntVal(resp.StatusCode), StrVal(body)}}},
	}, nil
}

func errResult(it *Interp, msg string) Value {
	errVal := &StructVal{Def: it.structs["error"], Fields: []Value{StrVal(msg)}}
	return &EnumVal{Def: it.enums["Result"], Case: 1, Payloads: []Value{errVal}}
}
