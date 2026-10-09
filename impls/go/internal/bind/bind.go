package bind

// extern 自动绑定最小切片（decisions #61）：
// 解析 Go 包目录源码（go/parser，标准库），提取导出函数签名，
// 生成 extern "go" 声明块草案——签名的人工审核起点，非免审产物。
//
// 类型映射：int/uint 系→int、float 系→float、string、bool、error 直映；
// []T → [T]（[]byte → [int]）；(T, error) 双返回 → Result[T, error]；
// 指针/struct/接口/map/chan/func/多返回值 → 跳过并注明原因。

import (
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// Dir 解析目录内顶层非测试 .go 文件，返回 extern "go" 声明块草案.
func Dir(dir string) (string, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return "", err
	}
	var files []string
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".go") || strings.HasSuffix(name, "_test.go") {
			continue
		}
		files = append(files, filepath.Join(dir, name))
	}
	sort.Strings(files)
	if len(files) == 0 {
		return "", fmt.Errorf("%s: 无 .go 文件", dir)
	}

	type fn struct {
		name string
		sig  string
	}
	var fns []fn
	var skipped []string
	fset := token.NewFileSet()
	for _, f := range files {
		file, err := parser.ParseFile(fset, f, nil, 0)
		if err != nil {
			return "", fmt.Errorf("%s: %w", filepath.Base(f), err)
		}
		for _, decl := range file.Decls {
			fd, ok := decl.(*ast.FuncDecl)
			if !ok || fd.Recv != nil || !fd.Name.IsExported() {
				continue
			}
			sig, skip := goFuncToLangGo(fd)
			if skip != "" {
				skipped = append(skipped, fmt.Sprintf("%s(%s): %s", fd.Name.Name, paramNames(fd), skip))
				continue
			}
			fns = append(fns, fn{name: fd.Name.Name, sig: sig})
		}
	}
	if len(fns) == 0 {
		return "", fmt.Errorf("%s: 无可绑定的导出函数", dir)
	}

	var b strings.Builder
	b.WriteString("// extern \"go\" 声明草案 —— 由 langGo bind 生成，签名需人工审核后使用\n")
	b.WriteString("// 绑定面仅是声明；实现需在 prelude/宿主侧另行提供\n")
	if len(skipped) > 0 {
		b.WriteString("// 跳过:\n")
		for _, sk := range skipped {
			b.WriteString("//   " + sk + "\n")
		}
	}
	b.WriteString("extern \"go\" {\n")
	for _, f := range fns {
		b.WriteString("    " + f.sig + "\n")
	}
	b.WriteString("}\n")
	return b.String(), nil
}

func paramNames(fd *ast.FuncDecl) string {
	var names []string
	for _, p := range fd.Type.Params.List {
		for range p.Names {
			names = append(names, "?")
		}
	}
	if len(names) == 0 {
		return ""
	}
	return strings.Join(names, ", ")
}

// goFuncToLangGo 单个导出函数 → langGo 声明；skip 非空表示不可绑定及原因.
func goFuncToLangGo(fd *ast.FuncDecl) (sig, skip string) {
	var params []string
	list := fd.Type.Params.List
	for i, field := range list {
		typeName, ok := langGoType(field.Type)
		if !ok {
			return "", "参数类型不支持: " + goTypeString(field.Type)
		}
		// 无名参数组按组展开
		n := len(field.Names)
		if n == 0 {
			n = 1
		}
		for j := 0; j < n; j++ {
			name := "a" + strconv.Itoa(len(params)+1)
			if j < len(field.Names) && field.Names[j] != nil {
				name = field.Names[j].Name
			}
			_ = i
			params = append(params, name+": "+typeName)
		}
	}

	results := fd.Type.Results
	switch {
	case results == nil || len(results.List) == 0:
		return fnSig(fd.Name.Name, params, ""), ""
	case len(results.List) == 1:
		typeName, ok := langGoType(results.List[0].Type)
		if !ok {
			return "", "返回类型不支持: " + goTypeString(results.List[0].Type)
		}
		return fnSig(fd.Name.Name, params, typeName), ""
	case len(results.List) == 2:
		// (T, error) 惯例 → Result[T, error]
		if isError(results.List[1].Type) {
			typeName, ok := langGoType(results.List[0].Type)
			if !ok {
				return "", "返回类型不支持: " + goTypeString(results.List[0].Type)
			}
			return fnSig(fd.Name.Name, params, "Result["+typeName+", error]"), ""
		}
		return "", "双返回值仅支持 (T, error)"
	default:
		return "", "多返回值（>2）"
	}
}

func fnSig(name string, params []string, ret string) string {
	sig := "fn " + name + "(" + strings.Join(params, ", ") + ")"
	if ret != "" {
		sig += ": " + ret
	}
	return sig
}

func isError(t ast.Expr) bool {
	id, ok := t.(*ast.Ident)
	return ok && id.Name == "error"
}

// langGoType Go 类型表达式 → langGo 类型名；ok=false 表示不支持.
func langGoType(t ast.Expr) (string, bool) {
	switch x := t.(type) {
	case *ast.Ident:
		switch x.Name {
		case "int", "int8", "int16", "int32", "int64",
			"uint", "uint8", "uint16", "uint32", "uint64":
			return "int", true
		case "float32", "float64":
			return "float", true
		case "string":
			return "string", true
		case "bool":
			return "bool", true
		case "error":
			return "error", true
		case "byte":
			return "int", true
		case "rune":
			return "int", true
		case "any", "interface{}":
			return "any", true
		}
		return "", false
	case *ast.ArrayType:
		elem, ok := langGoType(x.Elt)
		if !ok {
			return "", false
		}
		// []byte → [int]（byte 已映射 int）
		return "[" + elem + "]", true
	case *ast.SelectorExpr: // 命名类型如 time.Duration → 不支持
		return "", false
	case *ast.StarExpr:
		return "", false
	case *ast.MapType, *ast.ChanType, *ast.FuncType, *ast.StructType, *ast.InterfaceType:
		return "", false
	}
	return "", false
}

func goTypeString(t ast.Expr) string {
	switch x := t.(type) {
	case *ast.Ident:
		return x.Name
	case *ast.StarExpr:
		return "*" + goTypeString(x.X)
	case *ast.SelectorExpr:
		return goTypeString(x.X) + "." + x.Sel.Name
	case *ast.ArrayType:
		return "[]" + goTypeString(x.Elt)
	case *ast.MapType:
		return "map[" + goTypeString(x.Key) + "]" + goTypeString(x.Value)
	case *ast.StructType:
		return "struct{...}"
	case *ast.InterfaceType:
		return "interface{...}"
	case *ast.FuncType:
		return "func(...)"
	case *ast.ChanType:
		return "chan"
	}
	return "?"
}
