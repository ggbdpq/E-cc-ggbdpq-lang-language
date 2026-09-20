# language

lang-go 语言的 monorepo：一门覆盖前端、后端与日常工具开发的个人语言，源码可转译到 Go 与 JS 执行。

## 结构

| 目录 | 用途 | 状态 |
|------|------|------|
| `go/` | Go 参考实现（语义前端 + 解释器 + →Go/→JS 双转译后端 + 契约层 + CLI + REPL） | **可用**：`cd go && go build -o lang-go.exe ./cmd/lang-go`；`v0.9.0`（历史经 subtree 并入本仓库） |
| `specs/` | 语言无关规范（词法/语法/AST/解释器/类型系统/错误模型/运行时），实现共守的唯一语义真值源 | 骨架，待沉淀 |
| `docs/` | 语言级文档：`plans/`（v0.3 实施计划）、`adr/`（架构决策） | 增长中 |
| `examples/` | 语言级示例索引 | 骨架，实际示例在 `go/examples/` |

## 实现状态

| 目录 | 语言 | 状态 |
|------|------|------|
| `go/` | Go | 三路执行（解释器 / →Go / →JS）全绿；v0.3 语义前端（resolver → checker → HIR → IR）+ 独立 `check`（稳定诊断码）；语义决策台账 70 条 |
| `python/` | Python | 未开始（按 YAGNI，开工时再创建） |
| `rust/` | Rust | 未开始（同上） |
| `typescript/` | TypeScript | 未开始（同上） |

## 约定

- 各实现保留宿主语言原生工具链（go test / cargo test / pytest / bun test），不为目录统一引入 workspace。
- 本仓库为唯一 git 仓库（monorepo）；`go/.git` 为 v0.3 前的历史备份（已退役，origin 改名 origin-legacy 防误推）。
- 语义变更以 `go/docs/decisions.md` 台账为准（v0.3 起 #65–#70b）；架构决策见 `docs/adr/`；跨实现规范沉淀进 `specs/`；任何新决策必须追加留痕。
- 验收判据：同一 lang-go 程序，各实现三路（解释器 / →Go / →JS）stdout 逐字节一致（examples + m4 全量差分测试机械化此判定）。
- v0.3 起 `lang-go check` 为独立语义前端（稳定码 E1xxx/E2xxx/E3xxx/E4xxx），`build` 先检查后转译；后端只做平台映射。
- v0.4 起 Agent 通过稳定 JSON 协议理解程序：`check/inspect/dump --json`（specs/agent-protocol.md、schemas/agent-protocol.schema.json；schemaVersion 1）。
- v0.5 起 `lang-go fix --safe` 提供机械安全修复（结构化 TextEdit/WorkspaceEdit + sha256 前置校验 + 编辑事务闭环），Formatter 注释 100% 保留且幂等（ADR-0003）。
- v0.6 起 `lang-go inspect callers/callees/impact/references/dependency --json` 提供语义图查询（decisions #81）。
- v0.7 起 `lang-go refactor rename/organize-imports/safe-remove --json` 提供语义级重构（基于 SymbolId + 引用集，禁止 Regex；decisions #85–#86）。
- v0.8 起 `lang-go verify` 走全管线+多路差分；顶层 `conformance/` 是四实现共同真值（specs/runner-contract.md）。
- v0.9 起 `specs/` 是唯一语义真值源（Rule Catalog 稳定 Rule ID，诊断带 ruleId）；verify 四路差分（AST Oracle / IR 解释器 / →Go / →JS）。
- v0.7 起 `lang-go refactor rename/organize-imports/safe-remove --json` 提供语义级重构（基于 SymbolId + 引用集，禁止 Regex；decisions #82–#84）。
