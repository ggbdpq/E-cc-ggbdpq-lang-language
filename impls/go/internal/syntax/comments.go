package syntax

// comments.go：注释 trivia 扫描（v0.5 Lossless 层）。
// 词法态扫描：跳过字符串字面量（含转义），收集全部 // 行注释，
// 产出按偏移排序的有序 trivia 流。Formatter 据此做 100% 注释保留。

import "strings"

// Comment 一条注释 trivia.
type Comment struct {
	Span Span
	Text string // 原文（含 // 前缀；不含换行与行尾 \r）
}

// Line 注释所在行（1 基）.
func (c Comment) Line() int { return c.Span.Start.Line }

// Col 注释起始列（1 基）.
func (c Comment) Col() int { return c.Span.Start.Col }

// ScanComments 扫描源码中的全部注释（依出现序）.
func ScanComments(src string) []Comment {
	runes := []rune(src)
	var out []Comment
	line := 1
	lineStart := 0
	i := 0
	for i < len(runes) {
		r := runes[i]
		switch r {
		case '\n':
			i++
			line++
			lineStart = i
		case '"':
			i++
			for i < len(runes) {
				if runes[i] == '\\' {
					i += 2
					continue
				}
				if runes[i] == '"' || runes[i] == '\n' {
					i++
					break
				}
				i++
			}
		case '/':
			if i+1 < len(runes) && runes[i+1] == '/' {
				start := i
				startLine := line
				startCol := i - lineStart + 1
				for i < len(runes) && runes[i] != '\n' {
					i++
				}
				text := strings.TrimRight(string(runes[start:i]), "\r")
				out = append(out, Comment{
					Span: Span{
						Start: Position{Offset: start, Line: startLine, Col: startCol},
						End:   Position{Offset: i, Line: line, Col: i - lineStart + 1},
					},
					Text: text,
				})
				continue
			}
			i++
		default:
			i++
		}
	}
	return out
}
