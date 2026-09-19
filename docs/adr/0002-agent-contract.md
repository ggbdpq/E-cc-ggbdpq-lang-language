# ADR-0002 Agent Contract——稳定 JSON 协议与 Effect Metadata

- 状态：已接受（2026-09-20，v0.4.0）
- 关联：docs/plans/v0.4-agent-contract-plan.md、decisions.md #71–#76、specs/agent-protocol.md

## Context

v0.3 语义核完成后，程序的符号/类型/依赖/副作用信息只存在于编译器内部 Go struct。
Coding Agent 只能靠读内部结构或 grep 源码理解程序——前者脆弱（内部重构即破坏），
后者不可靠（正则不是语义真值）。同时诊断只有行级文本输出，无法被机器消费。

## Decision

1. **契约层单一边界**：新增 `internal/contract`，是内部模型 → 稳定 JSON 的唯一转换层。
   禁止 import gen/genjs/interp（`go list -deps` 机械验证）。协议信封统一
   `schemaVersion/toolVersion/kind`，v0.4.x 字段只加不改不删。
2. **九条机器命令**：`check --json`、`dump ast|hir|ir --json`、
   `inspect symbols|refs|type|dependencies|effects --json`。输入恒为当前目录模块
   （沿用不收路径参数的安全边界）。
3. **列级 Span**：Lexer/Parser 全链路 rune 偏移 + 行列跟踪（next/accept 双出口维护
   prevEnd/prevLine），全部 AST 节点盖 `Span{File,Start,End}`；装载器逐文件补 File
   归属（修复 v0.3 已知缺口）。行偏移与 Lexer 同一度量（rune），杜绝字节/rune 混用。
4. **Effect Metadata 从 IR 推导**：直接效果挂在内建/extern/spawn 节点上，沿调用图
   fixpoint 传播；函数字面量效果归属定义所在函数（保守近似，文档化）。禁止字符串搜索。
5. **取舍（文档化）**：dump ir 节点不含 span（IR 为精简执行视图，span 见 dump hir）；
   诊断 primarySpan v0.4 为行级（列级收紧是 v0.5 目标）；FS/TIME/RANDOM 为保留效果
   枚举（当前语言无生产者）；跨编辑的 ID 稳定性不做承诺（插入式编号，编辑会移位）。

## Consequences

- Agent 全程通过 JSON 理解程序：符号、引用、类型、依赖、效果、三棵内部树——
  内部重构不再破坏 Agent 工作流。
- 协议即公共 API：golden 测试锁定输出，字段演进走 schemaVersion。
- 单一版本源：CLI version 复用 contract.ToolVersion（根治 v0.3 漂移 #2）。

## Alternatives

- 直接把内部 struct `json:` 序列化暴露：零新代码，但每次内部重构都破坏 Agent——否决。
- LSP：能力重叠但传输/状态管理复杂度远超当前需求，且官方建议后置——否决（v0.4 禁做）。
- 源码正则推导 effects：与语义真值脱节，泛型/别名/调用链必错——否决。
