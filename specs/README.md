# specs —— 唯一语言真值源

本目录是 lang-go 的**最高语义真值源**。冲突裁决顺序：

```text
specs/（语言规范 + Rule ID）
  ↓
conformance/（行为契约）
  ↓
go/ 参考实现（Reference Implementation）
  ↓
README / tour / 教程
```

文档、代码、测试冲突时：定位冲突 → 判断规范是否合理 → 经 ADR 修规范或修实现 →
补回归测试 → 更新 Conformance Case。**go/ 内部代码不是规范**；后端只做平台映射，
不得重新决定核心语义。

## 主题规范（Rule ID 分区）

| 文件 | Rule 分区 | 主题 |
|------|-----------|------|
| lexical-grammar.md | LEX | 源文件、记号、字面量、换行续行 |
| grammar.md | PARSE | 程序结构、语句、优先级、match 臂 |
| name-resolution.md | NAME | 作用域、shadowing、合并命名空间 |
| type-system.md | TYPE / GEN | 类型类别、兼容性、Any/Unknown、泛型 |
| values.md | VALUE | 值/引用语义、struct 拷贝、闭包捕获 |
| expressions.md | EVAL / CALL | 求值顺序、调用、运算符 |
| control-flow.md | CTRL / MATCH | 条件、循环、迭代协议、match 与穷尽性 |
| option-result.md | RESULT | Option/Result、`?` 传播 |
| modules.md | MODULE | 模块根、import 解析、初始化 |
| concurrency.md | TASK / CHAN | Task 模型、channel、确定性边界 |
| runtime.md | RUNTIME / EFFECT / FFI | 运行时失败、效果面、extern |
| rules.md | — | Rule Catalog 总表（ID ↔ 诊断码 ↔ Conformance） |

## 工具与实现契约（非语言语义）

diagnostics.md（诊断协议与 Rule 映射）、agent-protocol.md、source-span.md、
runner-contract.md、../schemas/（JSON Schema）。

## 实现侧文档

`go/docs/spec.md` 自 v0.9 起为**实现侧摘要**（管线架构、后端映射、工具命令），
语义真值以本目录为准；`go/docs/decisions.md` 是决策台账（为什么）；`docs/adr/` 是
重大架构决策的完整上下文。
