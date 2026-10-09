package fix

// fix.go：机械可证明安全的自动修复（v0.5 Safe Editing）。
// 只允许语义零变化的修复，每条都有安全性论证：
//   1. duplicate-import  同文件重复 import 去重（装载器对已装载文件跳过，语义零变化）
//   2. unused-import     未使用 import 删除（声明名字集合与文件内引用无交集 + 装载幂等）
//   3. trailing-ws       行尾空白去除（词法器跳过空白）
//   4. eof-newline       文件尾确保恰好一个换行（词法器以 EOF 为语句终止）
//
// 偏移一律为 0 基 rune 偏移（与 edit.Apply 一致，中文安全）。

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"lang-go/internal/edit"
)

// ListDir 列出目录内全部 .lang-go 文件（模块根相对路径，/ 分隔，依文件名序）.
func ListDir(root string) ([]string, error) {
	entries, err := os.ReadDir(root)
	if err != nil {
		return nil, err
	}

	var out []string

	for _, e := range entries {
		if !e.IsDir() && strings.HasSuffix(e.Name(), ".lang-go") {
			out = append(out, e.Name())
		}
	}

	sort.Strings(out)

	return out, nil
}

// PlanFile 对单文件计算 safe fix 的 TextEdit 列表（rune 偏移，依偏移降序——Apply 约定）.
func PlanFile(src string) []edit.TextEdit {
	var edits []edit.TextEdit

	// 行切分（rune 基；text 不含换行）
	type lineR struct {
		text  []rune // 不含换行
		start int    // 行首 rune 偏移
	}

	rs := []rune(src)

	var lines []lineR

	cur := lineR{start: 0}

	for _, r := range rs {
		if r == '\n' {
			lines = append(lines, cur)
			cur = lineR{start: cur.start + len(cur.text) + 1}
		} else {
			cur.text = append(cur.text, r)
		}
	}

	if len(cur.text) > 0 || len(lines) == 0 {
		lines = append(lines, cur)
	}

	srcRunes := len(rs)

	seenImports := map[string]bool{}

	var removals [][2]int // [start, end) rune 区间

	for _, ln := range lines {
		text := string(ln.text)
		trimmedRight := strings.TrimRight(text, " \t\r")
		trimmedAll := strings.TrimSpace(text)

		// 行尾空白
		if trimmedRight != text {
			removals = append(removals, [2]int{ln.start + len([]rune(trimmedRight)), ln.start + len([]rune(text))})
		}

		// import：重复 → 删；未使用 → 删（同一行只命中一条，避免区间重叠）
		if strings.HasPrefix(trimmedAll, "import ") {
			dup := seenImports[trimmedAll]
			seenImports[trimmedAll] = true

			unused := false
			if name := importName(trimmedAll); name != "" && !referencedIn(src, name) {
				unused = true
			}

			if dup || unused {
				removals = append(removals, [2]int{ln.start, ln.start + len([]rune(text)) + 1}) // 含换行
			}
		}
	}

	for _, r := range removals {
		if r[1] > srcRunes {
			r[1] = srcRunes // 末行无换行时收缩到 EOF
		}

		if r[0] < r[1] {
			edits = append(edits, edit.TextEdit{StartOffset: r[0], EndOffset: r[1]})
		}
	}

	// EOF 换行：缺失且无触及 EOF 的删除时插入
	if !strings.HasSuffix(src, "\n") && src != "" {
		touchEOF := false

		for _, e := range edits {
			if e.EndOffset == srcRunes {
				touchEOF = true
			}
		}

		if !touchEOF {
			edits = append(edits, edit.TextEdit{StartOffset: srcRunes, EndOffset: srcRunes, Replacement: "\n"})
		}
	}

	// Apply 约定：按 StartOffset 降序
	sort.Slice(edits, func(i, j int) bool { return edits[i].StartOffset > edits[j].StartOffset })

	return edits
}

// importName 从 import 行提取 "./x" 的 x.
func importName(line string) string {
	i := strings.Index(line, `"`)

	j := strings.LastIndex(line, `"`)
	if i < 0 || j <= i {
		return ""
	}

	target := line[i+1 : j]
	if !strings.HasPrefix(target, "./") {
		return ""
	}

	p := strings.TrimPrefix(target, "./")
	if k := strings.LastIndex(p, "/"); k >= 0 {
		p = p[k+1:]
	}

	return p
}

// referencedIn 保守判定：源码（除 import 行自身——那会命中目标名）任何位置
// 出现该名字即视为被引用。宁可漏删不误删。
func referencedIn(src string, name string) bool {
	for _, line := range strings.Split(src, "\n") {
		if strings.HasPrefix(strings.TrimSpace(line), "import ") {
			continue
		}

		if strings.Contains(line, name) {
			return true
		}
	}

	return false
}

// PlanDir 对目录内全部 .lang-go 计算 safe fix WorkspaceEdit.
func PlanDir(root string) (edit.WorkspaceEdit, error) {
	files, err := ListDir(root)
	if err != nil {
		return edit.WorkspaceEdit{}, fmt.Errorf("读目录失败: %w", err)
	}

	ws := edit.WorkspaceEdit{Edits: []edit.TextEdit{}, Preconditions: []edit.Precondition{}}

	for _, rel := range files {
		bs, err := os.ReadFile(filepath.Join(root, rel))
		if err != nil {
			return ws, fmt.Errorf("读 %s: %w", rel, err)
		}

		edits := PlanFile(string(bs))
		if len(edits) == 0 {
			continue
		}

		for i := range edits {
			edits[i].File = rel
		}

		ws.Edits = append(ws.Edits, edits...)
		ws.Preconditions = append(ws.Preconditions, edit.Precondition{
			File:     rel,
			FileHash: edit.HashBytes(bs),
		})
	}

	return ws, nil
}
