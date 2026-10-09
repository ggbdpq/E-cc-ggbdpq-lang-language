package types

// 注解解析的独立入口：HIR 构建阶段把声明上的注解解析为语义类型。
// 前置条件：checker 已通过（注解合法，无需再报错）。

import (
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
)

// ResolveAnno 解析注解。tparams 为泛型参数名集（可空）。
func ResolveAnno(res *semantic.Result, t syntax.TypeEx, tparams []string) *Type {
	if t == nil {
		return Unknown
	}

	return resolveAnno(res, t, tparamsOf(tparams))
}

func tparamsOf(names []string) map[string]bool {
	m := map[string]bool{}
	for _, n := range names {
		m[n] = true
	}

	return m
}

func lookupTypeSym(res *semantic.Result, name string) *semantic.Symbol {
	if s := res.Builtin[name]; s != nil && s.Kind == semantic.KStruct {
		return s
	}

	for _, s := range res.Symbols {
		if (s.Kind == semantic.KStruct || s.Kind == semantic.KEnum) && s.Name == name {
			return s
		}
	}

	return nil
}

func resolveAnno(res *semantic.Result, t syntax.TypeEx, tps map[string]bool) *Type {
	switch tt := t.(type) {
	case *syntax.NamedType:
		switch tt.Name {
		case "int":
			return Int
		case "float":
			return Float
		case "string":
			return String
		case "bool":
			return Bool
		case "error":
			return ErrorT
		case "any":
			return Any
		case "chan":
			elem := Unknown
			if len(tt.Args) == 1 {
				elem = resolveAnno(res, tt.Args[0], tps)
			}

			return ChanOf(elem)
		case "Option":
			if len(tt.Args) == 1 {
				return OptionOf(resolveAnno(res, tt.Args[0], tps))
			}

			return OptionOf(Unknown)
		case "Result":
			ok := Unknown
			if len(tt.Args) >= 1 {
				ok = resolveAnno(res, tt.Args[0], tps)
			}

			return ResultOf(ok, ErrorT)
		}

		if tps[tt.Name] {
			return &Type{Kind: KTypeArg, Name: tt.Name}
		}

		if sym := lookupTypeSym(res, tt.Name); sym != nil {
			if sym.Kind == semantic.KEnum {
				return EnumOf(sym)
			}

			return StructOf(sym)
		}

		return Unknown
	case *syntax.ArrType:
		return SliceOf(resolveAnno(res, tt.Elem, tps))
	case *syntax.MapType:
		return MapOf(resolveAnno(res, tt.Key, tps), resolveAnno(res, tt.Val, tps))
	case *syntax.PtrType:
		return RefOf(resolveAnno(res, tt.Elem, tps))
	case *syntax.FnType:
		var ps []*Type
		for _, p := range tt.Params {
			ps = append(ps, resolveAnno(res, p, tps))
		}

		ret := Unknown
		if tt.Ret != nil {
			ret = resolveAnno(res, tt.Ret, tps)
		}

		return FnOf(ps, ret)
	}

	return Unknown
}
