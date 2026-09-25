package types

import "lang-go/internal/syntax"

// opName 复合赋值运算符的诊断名
func opName(k syntax.Kind) string {
	switch k {
	case syntax.PlusAssign:
		return "+="
	case syntax.MinusAssign:
		return "-="
	case syntax.StarAssign:
		return "*="
	case syntax.SlashAssign:
		return "/="
	case syntax.Assign:
		return "="
	}
	return "?"
}

// joinNames 逗号连接 case 名
func joinNames(names []string) string {
	out := ""
	for i, n := range names {
		if i > 0 {
			out += ", "
		}
		out += n
	}
	return out
}
