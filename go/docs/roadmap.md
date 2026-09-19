# lang-go 路线图（剩余工程）

本文承接 decisions.md 的里程碑表。状态截至 2026-09-20（v0.3.0）。本文件是计划快照，不是决策记录——决策仍以 decisions.md 为准。

## 当前位置

| 里程碑 | 状态 |
|--------|------|
| M1 规范 + 决策台账 | 完成 |
| M2 解释器 + REPL + lang-go run/test | 完成 |
| M3 →Go / →JS 双转译后端 | 完成 |
| M4 移植 linkpull bilibili 提取器（v0.2.0） | 完成 |
| — R1 extern / R2 http+json / R3 import / chan 阻塞语义 / bind | 完成 |
| **v0.3 Semantic Core** | **完成**：semantic（resolver）→ types（checker）→ hir（Typed HIR）→ ir（Core IR）；check 独立前端（稳定诊断码 E1xxx–E4xxx）；后端改消费 IR；三处静默分歧收口（ADR-0001、decisions #65–#70b） |
| **v0.4 Agent Contract** | **完成**：internal/contract 契约层（schemaVersion 1）；check/dump ast·hir·ir/inspect symbols·refs·type·dependencies·effects 全部 --json；列级 Span + 文件归属；Effect Metadata 从 IR 推导（decisions #71–#76、specs/agent-protocol.md、schemas/agent-protocol.schema.json） |

## 剩余候选

- extern 自动绑定已交付（`lang-go bind`，decisions #61）；.d.ts 摄入维持候选（#62）
- 性能（树遍历 → 字节码）永久搁置除非真实慢点（#58）
- interp 迁 IR 执行：迁移通道已建立（共享前端诊断），等 v0.4 评估（#70）
- interp-only 构造转正评估：嵌套声明、`Ok()` 作值比较（selftest 语料依赖；ADR-0001）

## 下一阶段（承接总路线）

- **v0.5 Safe Editing**：Lossless Syntax（行尾注释丢失待解）+ Formatter 2.0 + 语义 rename + 诊断收紧到列级 primarySpan
- v0.6 Semantic Graph / v0.7 Refactoring / v0.8 Verification（conformance suite）/ v0.9 Hardening / v1.0 Freeze
