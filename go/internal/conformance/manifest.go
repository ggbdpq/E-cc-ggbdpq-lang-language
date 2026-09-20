// Package conformance 实现 v0.8 一致性套件：manifest（YAML 受限子集）解析 +
// case runner（run 三路差分 / check 诊断一致性）。套件语料在仓库顶层 conformance/，
// 独立于 Go implementation——它是四套实现（Go/TS/Python/Rust）的共同真值。
package conformance

import (
	"fmt"
	"strconv"
	"strings"
)

// DiagExpect check 模式的单条诊断断言（code/severity/line 必须精确匹配；
// message 与列号允许漂移——diagnostics spec 的既定口径）
type DiagExpect struct {
	Code     string
	RuleID   string // 可选：声明了才比对（向后兼容 v0.8 case）
	Severity string
	Line     int
}

// Expect case 的期望面
type Expect struct {
	ExitCode    int
	Stdout      string
	Stderr      string
	Diagnostics []DiagExpect
}

// Case 单个 conformance case
type Case struct {
	Name     string
	Category string
	Mode     string // run | check
	Note     string
	RuleIDs  []string // 关联的 Semantic Rule ID（specs/rules.md；元数据，随结果透出）
	Expect   Expect
}

// ParseManifest 解析 manifest（YAML 受限子集，conformance/README.md §manifest）。
// 支持且仅支持：顶层 `key: value`、恰一层嵌套 `expect:`、块标量 `stdout: |`、
// `diagnostics:` 下的 `- code/severity/line` 列表项；# 注释与空行。
// 任何超集形态（多级嵌套、流式列表、锚点）一律报错——套件拒绝模糊输入。
func ParseManifest(src string) (*Case, error) {
	c := &Case{Mode: "run"}
	type ctxT int
	const (
		ctxTop ctxT = iota
		ctxExpect
		ctxStdoutBlock
		ctxDiag
		ctxRules
	)
	ctx := ctxTop
	var diag *DiagExpect
	blockIndent := -1
	lines := strings.Split(src, "\n")
	for i := 0; i < len(lines); i++ {
		raw := strings.TrimRight(lines[i], "\r")
		if strings.TrimSpace(raw) == "" || strings.HasPrefix(strings.TrimSpace(raw), "#") {
			continue
		}
		indent := len(raw) - len(strings.TrimLeft(raw, " "))
		line := strings.TrimSpace(raw)

		// 块标量收集：缩进不小于块首行的行都归 stdout
		if ctx == ctxStdoutBlock {
			if indent >= blockIndent {
				c.Expect.Stdout += raw[blockIndent:] + "\n"
				continue
			}
			ctx = ctxExpect
		}

		if ctx == ctxRules {
			// 列表项是无值裸标量（"- RULE-ID"），不走 key: value 切分
			if strings.HasPrefix(line, "- ") {
				c.RuleIDs = append(c.RuleIDs, strings.TrimPrefix(line, "- "))
				continue
			}
			ctx = ctxTop
		}
		key, val, hasVal := cutKey(line)
		if !hasVal {
			return nil, fmt.Errorf("manifest 第%d行: 形态不支持 %q", i+1, line)
		}
		switch ctx {
		case ctxTop:
			switch key {
			case "name":
				c.Name = val
			case "category":
				c.Category = val
			case "mode":
				c.Mode = val
			case "note":
				c.Note = val
			case "ruleIds":
				if val != "" {
					return nil, fmt.Errorf("manifest 第%d行: ruleIds 不接受内联值", i+1)
				}
				ctx = ctxRules
			case "expect":
				if val != "" {
					return nil, fmt.Errorf("manifest 第%d行: expect 不接受内联值", i+1)
				}
				ctx = ctxExpect
			default:
				return nil, fmt.Errorf("manifest 第%d行: 未知键 %q", i+1, key)
			}
		case ctxExpect:
			switch key {
			case "exitCode":
				n, err := strconv.Atoi(val)
				if err != nil {
					return nil, fmt.Errorf("manifest 第%d行: exitCode 须为整数", i+1)
				}
				c.Expect.ExitCode = n
			case "stdout":
				if val != "|" {
					// 单行标量（含 "" 空串；val 已由 cutKey 去引号）
					c.Expect.Stdout = val
					continue
				}
				ctx = ctxStdoutBlock
				blockIndent = -1
				// 下一非空行的缩进即块缩进
				for j := i + 1; j < len(lines); j++ {
					r2 := strings.TrimRight(lines[j], "\r")
					if strings.TrimSpace(r2) == "" {
						continue
					}
					blockIndent = len(r2) - len(strings.TrimLeft(r2, " "))
					break
				}
				if blockIndent < 0 {
					return nil, fmt.Errorf("manifest 第%d行: stdout 块标量为空", i+1)
				}
			case "stderr":
				c.Expect.Stderr = val
			case "diagnostics":
				if val != "" {
					return nil, fmt.Errorf("manifest 第%d行: diagnostics 不接受内联值", i+1)
				}
				ctx = ctxDiag
			default:
				return nil, fmt.Errorf("manifest 第%d行: expect 下未知键 %q", i+1, key)
			}
		case ctxDiag:
			if !strings.HasPrefix(key, "- ") {
				if diag != nil {
					// 同一 "- " 项的延续字段（severity/line）
					if err := diagField(diag, key, val, i+1); err != nil {
						return nil, err
					}
					continue
				}
				return nil, fmt.Errorf("manifest 第%d行: diagnostics 项须以 \"- \" 开头", i+1)
			}
			if diag != nil {
				c.Expect.Diagnostics = append(c.Expect.Diagnostics, *diag)
			}
			diag = &DiagExpect{}
			// key/val 已由外层 cutKey 切好（key 形如 "- code"），直接取字段
			if err := diagField(diag, strings.TrimPrefix(key, "- "), val, i+1); err != nil {
				return nil, err
			}
		default:
			return nil, fmt.Errorf("manifest 第%d行: 形态不支持", i+1)
		}
	}
	if ctx == ctxDiag && diag != nil {
		c.Expect.Diagnostics = append(c.Expect.Diagnostics, *diag)
	}

	// 收尾校验
	if c.Name == "" || c.Category == "" {
		return nil, fmt.Errorf("manifest 缺 name/category")
	}
	switch c.Mode {
	case "run":
		if c.Expect.ExitCode != 0 {
			return nil, fmt.Errorf("run 模式 exitCode 必须为 0（v0.8 不比对运行时错误文本）")
		}
		if c.Expect.Stderr != "" {
			return nil, fmt.Errorf("run 模式 stderr 必须为空串")
		}
		if !strings.HasSuffix(c.Expect.Stdout, "\n") || strings.Contains(c.Expect.Stdout, "\n\n") {
			return nil, fmt.Errorf("run 模式 stdout 须为块标量且无空行")
		}
	case "check":
		if len(c.Expect.Diagnostics) == 0 {
			return nil, fmt.Errorf("check 模式至少声明一条诊断")
		}
	default:
		return nil, fmt.Errorf("mode 须为 run|check（得到 %q）", c.Mode)
	}
	return c, nil
}

func diagField(d *DiagExpect, key, val string, lineNo int) error {
	switch key {
	case "code":
		d.Code = val
	case "ruleId":
		d.RuleID = val
	case "severity":
		d.Severity = val
	case "line":
		n, err := strconv.Atoi(val)
		if err != nil {
			return fmt.Errorf("manifest 第%d行: line 须为整数", lineNo)
		}
		d.Line = n
	default:
		return fmt.Errorf("manifest 第%d行: 诊断项未知键 %q", lineNo, key)
	}
	return nil
}

// cutKey "key: value" → key, value（value 已去引号与注释）；无冒号 → 未支持形态
func cutKey(s string) (string, string, bool) {
	i := strings.Index(s, ":")
	if i <= 0 {
		return "", "", false
	}
	key := strings.TrimSpace(s[:i])
	val := strings.TrimSpace(s[i+1:])
	// 行尾注释（值不在引号内时）
	if j := strings.Index(val, " #"); j >= 0 && !strings.HasPrefix(val, "\"") {
		val = strings.TrimSpace(val[:j])
	}
	if strings.HasPrefix(val, "\"") && strings.HasSuffix(val, "\"") && len(val) >= 2 {
		var err error
		val, err = unquote(val)
		if err != nil {
			return key, val, true
		}
	}
	return key, val, true
}

// unquote 仅支持普通双引号串（不允许转义）——子集刻意收窄
func unquote(s string) (string, error) {
	if s == `""` {
		return "", nil
	}
	if !strings.HasPrefix(s, "\"") || !strings.HasSuffix(s, "\"") || len(s) < 2 {
		return "", fmt.Errorf("仅支持双引号标量（得到 %q）", s)
	}
	inner := s[1 : len(s)-1]
	if strings.Contains(inner, "\\") {
		return "", fmt.Errorf("标量不支持转义（得到 %q）", s)
	}
	return inner, nil
}
