package edit

// edit.go：结构化编辑（v0.5 Safe Editing）。
// TextEdit/WorkspaceEdit 是 Agent 结构化修改的唯一载体；
// Apply 执行前置校验（哈希防误写）→ 重叠检测 → 偏移降序拼接 → 原子写回。

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"sort"
)

// TextEdit 单个文本替换：将 [StartOffset, EndOffset) 替换为 Replacement
type TextEdit struct {
	File        string `json:"file"`
	StartOffset int    `json:"startOffset"`
	EndOffset   int    `json:"endOffset"`
	Replacement string `json:"replacement"`
}

// Precondition 前置校验：编辑落盘前文件内容哈希必须仍然匹配（防源码已变时误写）
type Precondition struct {
	File     string `json:"file"`
	FileHash string `json:"fileHash"` // sha256 hex（原文件字节）
}

// WorkspaceEdit 一次结构化编辑事务
type WorkspaceEdit struct {
	Edits         []TextEdit     `json:"edits"`
	Preconditions []Precondition `json:"preconditions"`
}

// HashBytes sha256 hex
func HashBytes(bs []byte) string {
	sum := sha256.Sum256(bs)
	return hex.EncodeToString(sum[:])
}

// HashFile 文件内容哈希
func HashFile(path string) (string, error) {
	bs, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return HashBytes(bs), nil
}

// Apply 校验前置条件并应用全部编辑（偏移为 0 基 rune 偏移）。
// 任一前置校验失败或区间重叠 → 返回错误且不写任何文件。
func Apply(ws WorkspaceEdit, root string) error {
	if len(ws.Edits) == 0 {
		return nil
	}
	pre := map[string]string{}
	for _, p := range ws.Preconditions {
		pre[p.File] = p.FileHash
	}
	// 按文件分组（保持确定性：文件路径排序处理）
	byFile := map[string][]TextEdit{}
	var files []string
	for _, e := range ws.Edits {
		if _, seen := byFile[e.File]; !seen {
			files = append(files, e.File)
		}
		byFile[e.File] = append(byFile[e.File], e)
	}
	sort.Strings(files)
	for _, file := range files {
		if _, ok := pre[file]; !ok {
			return fmt.Errorf("edit: 文件 %s 缺少前置校验（fileHash）", file)
		}
	}

	newContents := map[string][]byte{}
	for _, file := range files {
		path := filepath.Join(root, filepath.FromSlash(file))
		bs, err := os.ReadFile(path)
		if err != nil {
			return fmt.Errorf("edit: 读 %s: %w", file, err)
		}
		if h, ok := pre[file]; ok {
			if HashBytes(bs) != h {
				return fmt.Errorf("edit: %s 自分析后已变化（哈希不匹配），拒绝写入", file)
			}
		}
		runes := []rune(string(bs))
		edits := append([]TextEdit(nil), byFile[file]...)
		sort.Slice(edits, func(i, j int) bool { return edits[i].StartOffset > edits[j].StartOffset })
		for i, e := range edits {
			if e.StartOffset < 0 || e.EndOffset < e.StartOffset || e.EndOffset > len(runes) {
				return fmt.Errorf("edit: %s 第 %d 个编辑区间越界 [%d,%d)（长度 %d）", file, i, e.StartOffset, e.EndOffset, len(runes))
			}
			if i+1 < len(edits) && edits[i+1].EndOffset > e.StartOffset {
				return fmt.Errorf("edit: %s 存在重叠编辑区间", file)
			}
		}
		for _, e := range edits {
			runes = append(runes[:e.StartOffset], append([]rune(e.Replacement), runes[e.EndOffset:]...)...)
		}
		newContents[file] = []byte(string(runes))
	}
	for _, file := range files {
		path := filepath.Join(root, filepath.FromSlash(file))
		if err := os.WriteFile(path, newContents[file], 0o644); err != nil {
			return fmt.Errorf("edit: 写 %s: %w", file, err)
		}
	}
	return nil
}
