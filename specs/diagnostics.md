# Diagnostics —— 诊断契约（v0.4）

> 状态：随 v0.4.0 冻结既有码。实现：`go/internal/semantic`、`go/internal/types`、`go/internal/pipeline`。

## 1. 稳定码分段

| 段 | 归属 |
|----|------|
| E1xxx | 词法/解析（当前统一 E1001） |
| E2xxx | 名字解析（resolver） |
| E3xxx | 类型系统（checker） |
| E4xxx | 模式匹配（checker） |

码只能追加，不能改义、不能复用。message 文本允许微调；**code + severity + 定位结构是契约**。

## 2. 结构（check --json / 未来协议复用）

```json
{
  "code": "E2002",
  "ruleId": "NAME-UNDEF-004",
  "severity": "error",
  "message": "未定义 nope",
  "primarySpan": {"file":"","start":{"offset":0,"line":2,"column":1},"end":{...}},
  "relatedSpans": [],
  "help": ""
}
```

- v0.4 全部为 `error` 级；warning 级别位保留。
- **v0.9 起 `ruleId` 携带 Semantic Rule ID**（specs/rules.md 的映射表，代码侧唯一实现在
  `go/internal/contract/rules.go`）；Rule ID 与码同为契约：一经发布不可改义。
- 定位精度：**行级**（primarySpan 的 line 正确，column 恒 1，offset 未填）——列级 primarySpan 是 v0.5 目标。
- `relatedSpans`/`help` 字段存在但可为空（结构即契约）。

## 3. 码清单（全集；逐码 Rule 映射见 specs/rules.md）

- E1001 解析/词法错误
- E2001 同作用域重复定义 · E2002 未定义名 · E2003 构造器重名 · E2004 方法接收者未知 struct ·
  E2005 对不可变绑定赋值 · E2006 case 与内建名冲突 · E2007 break/continue 在循环外 ·
  E2008 return 在函数外 · E2010 嵌套类型声明
- E3001 类型不匹配 · E3002 int/float 无隐式转换 · E3003 调用参数个数/构造器须调用 ·
  E3004 字段/方法不存在 · E3005 `?` 位置或所在函数未注解 Result · E3006 Result 函数 return 须 Ok/Err ·
  E3007 参数缺注解 · E3008 `?` 操作数须 Result · E3009 Result 第二参数须 error ·
  E3010 不可迭代 · E3011 一元/逻辑运算不适用 · E3012 `&` 仅限变量 · E3013 索引须 int ·
  E3014 映射键类型
- E4001 match 不穷尽 · E4002 enum/代数类型混字面量臂 · E4003 模式混用
