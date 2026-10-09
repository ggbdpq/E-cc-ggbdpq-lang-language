package semantic

import "fmt"

// Diagnostic 稳定诊断（v0.3：行级定位 + 固定码段）。
// E2xxx = resolver/module 段。
const (
	E2001 = "E2001" // 同作用域重复定义
	E2002 = "E2002" // 未定义名（含未知 struct / 未知构造器）
	E2003 = "E2003" // 构造器重名
	E2004 = "E2004" // 方法接收者不是已知 struct
	E2005 = "E2005" // 对不可变绑定赋值
	E2006 = "E2006" // case 与内建名冲突
	E2007 = "E2007" // break/continue 在循环之外
	E2008 = "E2008" // return 在函数之外
	E2010 = "E2010" // 嵌套类型声明（→Go 无法表达；interp 容忍属实现越界）
)

// Diagnostic 一条编译诊断.
type Diagnostic struct {
	Code string
	Msg  string
	Line int
}

func (d Diagnostic) Error() string {
	return fmt.Sprintf("第%d行: [%s] %s", d.Line, d.Code, d.Msg)
}

func (d Diagnostic) String() string { return d.Error() }

// ErrorList 多诊断聚合（实现 error）.
type ErrorList []Diagnostic

func (l ErrorList) Error() string {
	if len(l) == 0 {
		return "无诊断"
	}
	return l[0].Error()
}
