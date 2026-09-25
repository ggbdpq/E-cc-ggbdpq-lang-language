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
| **v0.9 Semantic Integrity** | **完成**：specs/ 唯一真值源（11 主题规范 + Rule Catalog ~60 条，27 诊断码全映射 ruleId）；IR 解释器 + 四路差分；Task/Channel 宿主无关模型；求值顺序/值语义/泛型/模块收口成文；fuzz/property/determinism/benchmark 基线；conformance 50 case（decisions #89–#92，ADR-0004） |
| **v0.8 Verification** | **完成**：顶层 conformance/（14 类 33 case）+ YAML 受限子集 manifest + runner（三路差分/诊断一致性）+ `lang-go verify` 十阶段；套件落地即修 genjs match await、prelude map 键、gen 泛型擦除链三件真 bug（decisions #87–#88） |
| **v0.7 Refactoring** | **完成**：rename symbol（SymbolId + 引用集 + 冲突拒绝）、organize imports（去重/排序/未使用删除）、safe remove（零入边证明 + 引用证据拒绝）；CLI refactor 接线（decisions #85–#86） |
| **v0.6 Semantic Graph** | **完成**：internal/graph 内存语义图（Symbol 节点 + call/references/writes/spawn/depends_on/imports 边）；inspect callers/callees/impact/references/dependency --json（--depth、direct/transitive + evidence）；effects 推导迁入 graph（decisions #81） |
| **v0.5 Safe Editing** | **完成**：注释 trivia 流 + Formatter 注释插回（100% 保留/幂等/语义等价，corpus 级锁定）；internal/edit 结构化编辑（TextEdit/WorkspaceEdit/sha256 前置校验）；fix --safe 三修复 + 编辑事务闭环（decisions #77–#80、ADR-0003） |
| **v0.4 Agent Contract** | **完成**：internal/contract 契约层（schemaVersion 1）；check/dump ast·hir·ir/inspect symbols·refs·type·dependencies·effects 全部 --json；列级 Span + 文件归属；Effect Metadata 从 IR 推导（decisions #71–#76、specs/agent-protocol.md、schemas/agent-protocol.schema.json） |

## 剩余候选

- extern 自动绑定已交付（`lang-go bind`，decisions #61）；.d.ts 摄入维持候选（#62）
- 性能（树遍历 → 字节码）永久搁置除非真实慢点（#58）
- interp 迁 IR 执行：迁移通道已建立（共享前端诊断），等 v0.4 评估（#70）
- interp-only 构造转正评估：嵌套声明、`Ok()` 作值比较（selftest 语料依赖；ADR-0001）

## 下一阶段（承接总路线）

- v0.6 Semantic Graph / v0.7 Refactoring / v0.8 Verification（conformance suite）/ v0.9 Hardening / v1.0 Freeze
