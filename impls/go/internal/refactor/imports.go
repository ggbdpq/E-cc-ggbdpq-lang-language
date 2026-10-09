package refactor

// imports.go：organize imports——同文件 import 行去重 + 按路径排序 + 删除未使用 import。
// 注释随行保留（import 行的行尾注释随行移动，独立行注释留在原位不动）。

import (
	"sort"
	"strings"
)

// ImportLine 一条 import 声明.
type ImportLine struct {
	Path    string
	Comment string // 行尾注释（含 // 前缀；无则空）
	Line    int    // 源文件行号（1 基）
	Used    bool   // 是否被引用
}

// OrganizeImports 对单个文件源码做 organize imports，返回新源码与是否变化.
func OrganizeImports(src string) (string, bool, error) {
	changed := false
	lines := strings.Split(src, "\n")

	// 找出全部 import 行的索引
	var importIdx []int
	for i, l := range lines {
		if strings.HasPrefix(strings.TrimSpace(l), "import ") {
			importIdx = append(importIdx, i)
		}
	}
	if len(importIdx) == 0 {
		return src, false, nil
	}

	// 提取每行路径与行尾注释
	type imp struct {
		path    string
		comment string
		lineIdx int
		used    bool
	}
	var imps []imp
	usedPaths := map[string]bool{}
	for _, i := range importIdx {
		l := lines[i]
		t := strings.TrimSpace(l)
		if !strings.HasPrefix(t, "import ") {
			continue
		}
		rest := strings.TrimSpace(strings.TrimPrefix(t, "import "))
		var path, comment string
		if q := strings.Index(rest, "\""); q >= 0 {
			rest = rest[q+1:]
			e := strings.Index(rest, "\"")
			if e < 0 {
				continue
			}
			path = rest[:e]
			tail := strings.TrimSpace(rest[e+1:])
			if strings.HasPrefix(tail, "//") {
				comment = tail
			}
		}
		if path == "" {
			continue
		}
		imp := imp{path: path, comment: comment, lineIdx: i}
		// 使用判定：文件内非 import 行出现路径的最后一段名（保守）
		base := path
		if k := strings.LastIndex(base, "/"); k >= 0 {
			base = base[k+1:]
		}
		for j, other := range lines {
			if j == i || strings.HasPrefix(strings.TrimSpace(other), "import ") {
				continue
			}
			if strings.Contains(other, base) {
				imp.used = true
				usedPaths[path] = true
				break
			}
		}
		imps = append(imps, imp)
	}

	// 未使用的 import 行：删除
	var kept []imp
	var removedLines []int
	seen := map[string]bool{}
	for _, im := range imps {
		if !im.used {
			removedLines = append(removedLines, im.lineIdx)
			changed = true
			continue
		}
		if seen[im.path] {
			removedLines = append(removedLines, im.lineIdx)
			changed = true
			continue
		}
		seen[im.path] = true
		kept = append(kept, im)
	}
	if !changed && len(kept) == len(imps) {
		// 无未使用/无重复——检查是否需要排序
		paths := make([]string, len(kept))
		for i, k := range kept {
			paths[i] = k.path
		}
		sorted := append([]string(nil), paths...)
		sort.Strings(sorted)
		for i := range paths {
			if paths[i] != sorted[i] {
				changed = true
				break
			}
		}
		if !changed {
			return src, false, nil
		}
	}

	// 输出：非 import 行保持原位；import 块用排序后的行替换
	var out []string
	for i, l := range lines {
		isImport := false
		for _, ii := range importIdx {
			if ii == i {
				isImport = true
			}
		}
		removed := false
		for _, r := range removedLines {
			if r == i {
				removed = true
			}
		}
		if isImport && !removed {
			continue // import 行由排序块统一输出
		}
		if removed {
			changed = true
			continue
		}
		out = append(out, l)
	}

	// 找到第一个 import 的原位置（在其前插入排序后的 import 行）
	insertAt := -1
	for i, l := range out {
		if strings.HasPrefix(strings.TrimSpace(l), "//") {
			continue
		}
		if strings.HasPrefix(strings.TrimSpace(l), "fn ") || strings.HasPrefix(strings.TrimSpace(l), "struct ") {
			insertAt = i
			break
		}
	}
	if insertAt < 0 {
		insertAt = len(out)
	}
	sorted := append([]imp(nil), kept...)
	sort.Slice(sorted, func(a, b int) bool { return sorted[a].path < sorted[b].path })
	var importLines []string
	for _, im := range sorted {
		l := "import \"" + im.path + "\""
		if im.comment != "" {
			l += " // " + strings.TrimPrefix(im.comment, "// ")
		}
		importLines = append(importLines, l)
	}
	res := make([]string, 0, len(out)+len(importLines))
	res = append(res, out[:insertAt]...)
	res = append(res, importLines...)
	res = append(res, out[insertAt:]...)
	return strings.Join(res, "\n"), changed, nil
}
