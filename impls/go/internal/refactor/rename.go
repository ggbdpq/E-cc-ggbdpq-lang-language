// rename.go：基于 SymbolId + 语义引用的 rename（v0.7 P0）。禁止 Regex Rename。
//
// 编辑位两类：
//   1. 定义位：声明节点内「名字 token」的精确区间（token 级扫描定位，见 locate.go）；
//   2. 引用位：resolver 引用表中指向目标符号的 Ident 节点 Span（精确）；
//      方法调用点为 HIR MethodCall 携带的 NameSp。
//
// 冲突检测（保守）：合并程序内存在其他同名用户符号 → 拒绝。

package refactor

import (
	"errors"
	"fmt"
	"lang-go/internal/edit"
	"lang-go/internal/hir"
	"lang-go/internal/semantic"
	"lang-go/internal/syntax"
)

// ResolveTarget 解析 --symbol 参数（ID 或名字）为唯一符号.
func ResolveTarget(res *semantic.Result, arg string) (*semantic.Symbol, error) {
	if arg == "" {
		return nil, errors.New("缺少 --symbol 参数")
	}

	if n, ok := allDigits(arg); ok {
		for _, s := range res.Symbols {
			if !s.BuiltinMark && int(s.ID) == n {
				return s, nil
			}
		}

		return nil, fmt.Errorf("未知符号 ID %s", arg)
	}

	var hits []*semantic.Symbol

	for _, s := range res.Symbols {
		if !s.BuiltinMark && s.Name == arg {
			hits = append(hits, s)
		}
	}

	switch len(hits) {
	case 1:
		return hits[0], nil
	case 0:
		return nil, fmt.Errorf("未知符号 %s", arg)
	default:
		return nil, fmt.Errorf("符号名 %s 对应多个符号，请用 --symbol <id>", arg)
	}
}

func allDigits(s string) (int, bool) {
	if s == "" {
		return 0, false
	}

	n := 0

	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, false
		}

		n = n*10 + int(r-'0')
	}

	return n, true
}

// ValidateNewName 新名字合法性：标识符、非关键字/保留字、非内建名.
func ValidateNewName(name string) error {
	if name == "" {
		return errors.New("新名字为空")
	}

	for i, r := range name {
		first := i == 0

		ok := r == '_' || (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') ||
			(!first && r >= '0' && r <= '9')
		if !ok {
			return fmt.Errorf("新名字含非法字符 %q", string(r))
		}
	}

	for _, kw := range reservedNames {
		if name == kw {
			return fmt.Errorf("新名字 %s 与关键字/保留字/内建名冲突", name)
		}
	}

	return nil
}

var reservedNames = []string{
	// 关键字 + 保留字（spec §2）
	"let", "var", "fn", "return", "if", "else", "while", "for", "in",
	"match", "struct", "enum", "true", "false", "break", "continue",
	"go", "chan", "import", "extern", "interface",
	// 内建函数
	"println", "print", "len", "str", "int", "float", "keys", "values",
	"has", "push", "assert", "err", "bytes", "char", "jsonParse",
	"jsonStringify", "isNone", "makeChan", "send", "recv",
	// 预载代数类型构造器与服务名
	"Some", "None", "Ok", "Err", "http",
}

// Rename 语义重命名：返回文本编辑集（调用方经 WorkspaceEdit 应用）。
// 支持 fn / method / variable（全局+局部）/ parameter；
// struct/enum/constructor/字段 rename 需要类型引用定位——显式拒绝（列 P1）。
func Rename(prog []syntax.Stmt, srcs Sources, res *semantic.Result, h *hir.Prog, target *semantic.Symbol, newName string) ([]edit.TextEdit, error) {
	if err := ValidateNewName(newName); err != nil {
		return nil, err
	}

	switch target.Kind {
	case semantic.KFn, semantic.KMethod, semantic.KVar, semantic.KParam:
	case semantic.KStruct, semantic.KEnum, semantic.KCtor:
		return nil, fmt.Errorf("暂不支持对 %s 的 rename（列 P1）", kindName(target.Kind))
	default:
		return nil, errors.New("该符号类型不支持 rename")
	}

	if target.Name == "_" {
		return nil, errors.New("占位符 _ 不可 rename")
	}
	// 冲突检测（保守）：其他同名用户符号存在即拒绝
	for _, s := range res.Symbols {
		if s.BuiltinMark || s.ID == target.ID {
			continue
		}

		if s.Name == newName {
			return nil, fmt.Errorf("新名字 %s 与已有符号冲突（%s #%d）", newName, s.Name, int(s.ID))
		}
	}

	// 定义位：名字 token 精确区间
	defSp, defErr := declNameSpan(prog, srcs, res, target)
	if defErr != nil {
		return nil, defErr
	}

	var edits []edit.TextEdit

	edits = append(edits, edit.TextEdit{
		File:        defSp.File,
		StartOffset: defSp.Start.Offset,
		EndOffset:   defSp.End.Offset,
		Replacement: newName,
	})

	// 引用位：Ident（read/write/call 位置的直接标识符引用）
	syntax.WalkStmts(prog, func(s syntax.Stmt) {
		if a, ok := s.(*syntax.AssignStmt); ok {
			if id, ok := a.Target.(*syntax.Ident); ok {
				if sym := res.Refs[id]; sym != nil && sym.ID == target.ID {
					edits = append(edits, edit.TextEdit{
						File:        id.Sp.File,
						StartOffset: id.Sp.Start.Offset,
						EndOffset:   id.Sp.End.Offset,
						Replacement: newName,
					})
				}
			}
		}
	}, func(e syntax.Expr) {
		if id, ok := e.(*syntax.Ident); ok {
			if sym := res.Refs[id]; sym != nil && sym.ID == target.ID {
				edits = append(edits, edit.TextEdit{
					File:        id.Sp.File,
					StartOffset: id.Sp.Start.Offset,
					EndOffset:   id.Sp.End.Offset,
					Replacement: newName,
				})
			}
		}
	}, nil)

	// 方法调用点：HIR MethodCall（Method == target）
	if target.Kind == semantic.KMethod && h != nil {
		for _, st := range h.Globals {
			collectHIRMethodRefs(st, res, target, newName, &edits)
		}

		for _, f := range h.Fns {
			for _, st := range f.Body {
				collectHIRMethodRefs(st, res, target, newName, &edits)
			}
		}
	}

	return dedupeEdits(edits), nil
}

func dedupeEdits(edits []edit.TextEdit) []edit.TextEdit {
	var out []edit.TextEdit

	for _, e := range edits {
		dup := false

		for _, x := range out {
			if x.StartOffset == e.StartOffset && x.EndOffset == e.EndOffset && x.File == e.File {
				dup = true
			}
		}

		if !dup {
			out = append(out, e)
		}
	}

	sortByStartDesc(out)

	return out
}

func sortByStartDesc(edits []edit.TextEdit) {
	for i := 1; i < len(edits); i++ {
		for j := i; j > 0 && edits[j].StartOffset > edits[j-1].StartOffset; j-- {
			edits[j], edits[j-1] = edits[j-1], edits[j]
		}
	}
}

func kindName(k semantic.Kind) string {
	switch k {
	case semantic.KFn:
		return "function"
	case semantic.KMethod:
		return "method"
	case semantic.KVar:
		return "variable"
	case semantic.KParam:
		return "parameter"
	case semantic.KStruct:
		return "struct"
	case semantic.KEnum:
		return "enum"
	case semantic.KCtor:
		return "constructor"
	}

	return "unknown"
}
