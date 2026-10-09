package edit

// 结构化编辑测试缝：哈希前置校验、重叠拒绝、多编辑、边界。

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func write(t *testing.T, dir, name, content string) string {
	t.Helper()

	p := filepath.Join(dir, name)
	if err := os.WriteFile(p, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}

	return name
}

func TestApply多编辑(t *testing.T) {
	dir := t.TempDir()
	f := write(t, dir, "a.txt", "hello world")
	h, _ := HashFile(filepath.Join(dir, f))

	ws := WorkspaceEdit{
		Edits: []TextEdit{
			{File: f, StartOffset: 6, EndOffset: 11, Replacement: "lang-go"},
			{File: f, StartOffset: 0, EndOffset: 5, Replacement: "你好"},
		},
		Preconditions: []Precondition{{File: f, FileHash: h}},
	}
	if err := Apply(ws, dir); err != nil {
		t.Fatal(err)
	}

	bs, _ := os.ReadFile(filepath.Join(dir, f))
	if string(bs) != "你好 lang-go" {
		t.Fatalf("结果 = %q", string(bs))
	}
}

func TestHash不匹配拒绝(t *testing.T) {
	dir := t.TempDir()
	f := write(t, dir, "a.txt", "v1")
	ws := WorkspaceEdit{
		Edits:         []TextEdit{{File: f, StartOffset: 0, EndOffset: 2, Replacement: "v2"}},
		Preconditions: []Precondition{{File: f, FileHash: HashBytes([]byte("stale"))}},
	}

	err := Apply(ws, dir)
	if err == nil || !strings.Contains(err.Error(), "哈希") {
		t.Fatalf("哈希不匹配应拒绝: %v", err)
	}

	bs, _ := os.ReadFile(filepath.Join(dir, f))
	if string(bs) != "v1" {
		t.Fatal("拒绝后文件不得被修改")
	}
}

func Test重叠区间拒绝(t *testing.T) {
	dir := t.TempDir()
	f := write(t, dir, "a.txt", "abcdef")
	h, _ := HashFile(filepath.Join(dir, f))

	ws := WorkspaceEdit{
		Edits: []TextEdit{
			{File: f, StartOffset: 0, EndOffset: 4, Replacement: "X"},
			{File: f, StartOffset: 2, EndOffset: 6, Replacement: "Y"},
		},
		Preconditions: []Precondition{{File: f, FileHash: h}},
	}
	if err := Apply(ws, dir); err == nil || !strings.Contains(err.Error(), "重叠") {
		t.Fatalf("重叠应拒绝: %v", err)
	}
}

func Test缺前置校验拒绝(t *testing.T) {
	dir := t.TempDir()
	f := write(t, dir, "a.txt", "data")

	ws := WorkspaceEdit{Edits: []TextEdit{{File: f, StartOffset: 0, EndOffset: 1, Replacement: "x"}}}
	if err := Apply(ws, dir); err == nil || !strings.Contains(err.Error(), "前置") {
		t.Fatalf("缺前置校验应拒绝: %v", err)
	}
}

func Test越界拒绝(t *testing.T) {
	dir := t.TempDir()
	f := write(t, dir, "a.txt", "ab")
	h, _ := HashFile(filepath.Join(dir, f))

	ws := WorkspaceEdit{
		Edits:         []TextEdit{{File: f, StartOffset: 0, EndOffset: 99, Replacement: "x"}},
		Preconditions: []Precondition{{File: f, FileHash: h}},
	}
	if err := Apply(ws, dir); err == nil || !strings.Contains(err.Error(), "越界") {
		t.Fatalf("越界应拒绝: %v", err)
	}
}

func TestUnicode偏移按rune(t *testing.T) {
	dir := t.TempDir()
	// "中文abc"：中文 2 rune + abc 3 rune
	f := write(t, dir, "a.txt", "中文abc")
	h, _ := HashFile(filepath.Join(dir, f))

	ws := WorkspaceEdit{
		Edits:         []TextEdit{{File: f, StartOffset: 2, EndOffset: 5, Replacement: "X"}},
		Preconditions: []Precondition{{File: f, FileHash: h}},
	}
	if err := Apply(ws, dir); err != nil {
		t.Fatal(err)
	}

	bs, _ := os.ReadFile(filepath.Join(dir, f))
	if string(bs) != "中文X" {
		t.Fatalf("结果 = %q（rune 偏移应为 2..5）", string(bs))
	}
}
