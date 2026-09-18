package load

// 目录即包 + import 装载器（spec §5，decisions #19/#40）：
// Program() 把目录内全部非测试 .ling 按文件名序合并为一个程序，
// 递归展开 import "./x"（文件或目录），限制在根目录树内；
// 循环导入报错，菱形导入（多处导同一库）静默去重。

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"ling/internal/syntax"
)

// Program 装载 dir 为一个合并程序。includeTests 时 *_test.ling 追加在后，
// 并返回其中全部 test 前缀无参函数名（按文件名序）。
func Program(dir string, includeTests bool) ([]syntax.Stmt, []string, error) {
	root, err := filepath.Abs(dir)
	if err != nil {
		return nil, nil, err
	}
	l := &loader{root: root, inProgress: map[string]bool{}, done: map[string]bool{}}
	prog, err := l.loadDir(root)
	if err != nil {
		return nil, nil, err
	}
	var tests []string
	if includeTests {
		testFiles, err := listLing(root, true)
		if err != nil {
			return nil, nil, err
		}
		for _, f := range testFiles {
			stmts, err := l.loadFile(filepath.Join(root, f))
			if err != nil {
				return nil, nil, err
			}
			prog = append(prog, stmts...)
			for _, s := range stmts {
				if d, ok := s.(*syntax.FnDecl); ok {
					if d.Recv == nil && len(d.Params) == 0 && strings.HasPrefix(d.Name, "test") {
						tests = append(tests, d.Name)
					}
				}
			}
		}
	}
	return prog, tests, nil
}

type loader struct {
	root       string
	inProgress map[string]bool // 正在展开的路径（真环检测）
	done       map[string]bool // 已完整装载的路径（菱形去重）
}

func listLing(dir string, tests bool) ([]string, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}
	var out []string
	for _, e := range entries {
		n := e.Name()
		if e.IsDir() || !strings.HasSuffix(n, ".ling") {
			continue
		}
		isTest := strings.HasSuffix(n, "_test.ling")
		if isTest == tests {
			out = append(out, n)
		}
	}
	sort.Strings(out)
	return out, nil
}

// loadDir 合并目录内非测试 .ling 并展开 import；根文件标记为 done 供去重
func (l *loader) loadDir(dir string) ([]syntax.Stmt, error) {
	files, err := listLing(dir, false)
	if err != nil {
		return nil, err
	}
	var raw []syntax.Stmt
	for _, f := range files {
		ap, err := filepath.Abs(filepath.Join(dir, f))
		if err != nil {
			return nil, err
		}
		stmts, err := l.loadFile(ap)
		if err != nil {
			return nil, err
		}
		l.done[ap] = true
		raw = append(raw, stmts...)
	}
	return l.expand(raw, dir)
}

// loadFile 解析单个文件（import 语句保留给上层 expand）
func (l *loader) loadFile(ap string) ([]syntax.Stmt, error) {
	src, err := os.ReadFile(ap)
	if err != nil {
		return nil, err
	}
	stmts, err := syntax.Parse(string(src))
	if err != nil {
		return nil, fmt.Errorf("%s: %w", filepath.Base(ap), err)
	}
	return stmts, nil
}

// expand 展开语句序列里的 ImportStmt：相对 fromDir 解析 "./x" → x.ling 或 x/ 目录
func (l *loader) expand(stmts []syntax.Stmt, fromDir string) ([]syntax.Stmt, error) {
	var out []syntax.Stmt
	for _, s := range stmts {
		imp, ok := s.(*syntax.ImportStmt)
		if !ok {
			out = append(out, s)
			continue
		}
		if !strings.HasPrefix(imp.Path, "./") {
			return nil, fmt.Errorf("第%d行: import 仅支持 \"./相对路径\"（得到 %q）", imp.Line, imp.Path)
		}
		target, skip, err := l.resolve(imp.Path, fromDir, imp.Line)
		if err != nil {
			return nil, err
		}
		if skip {
			continue // 菱形导入：已装载过
		}
		var sub []syntax.Stmt
		if info, statErr := os.Stat(target); statErr == nil && info.IsDir() {
			sub, err = l.loadDir(target)
		} else {
			var fileStmts []syntax.Stmt
			fileStmts, err = l.loadFile(target + ".ling")
			if err == nil {
				sub, err = l.expand(fileStmts, filepath.Dir(target))
			}
		}
		if err != nil {
			return nil, err
		}
		l.done[target] = true
		delete(l.inProgress, target)
		out = append(out, sub...)
	}
	return out, nil
}

// resolve 规范化 + containment + 环/去重判定
func (l *loader) resolve(rel string, fromDir string, line int) (string, bool, error) {
	target := filepath.Clean(filepath.Join(fromDir, rel))
	ap, err := filepath.Abs(target)
	if err != nil {
		return "", false, err
	}
	relToRoot, err := filepath.Rel(l.root, ap)
	if err != nil || relToRoot == ".." || strings.HasPrefix(filepath.ToSlash(relToRoot), "../") {
		return "", false, fmt.Errorf("第%d行: import %s 逃出模块根目录", line, rel)
	}
	if l.done[ap] {
		return ap, true, nil
	}
	if l.inProgress[ap] {
		return "", false, fmt.Errorf("第%d行: 循环 import %s", line, rel)
	}
	l.inProgress[ap] = true
	return ap, false, nil
}
