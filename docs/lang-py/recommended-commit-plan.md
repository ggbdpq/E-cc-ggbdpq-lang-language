# lang-py 建议提交计划（recommended-commit-plan）

> 本文件只建议未来如何把 lang-py 工作区拆成干净提交；**本轮不执行任何提交**。
> 前置状态：`python/` 骨架已被 v1.0 提交 4dd235c 收编入库（当时为部分实现）；
> 下方提交序列以「当前未提交增量」为对象，按依赖顺序逐个可独立通过
> `python -m pytest tests -q` 与 `python tests/run_conformance_dev.py`。

## 提交序列（自底向上，每个提交可独立编译、可回滚）

| # | 建议 commit message | 内容 | 验证门 |
|---|---|---|---|
| 1 | `feat(python): syntax frontend——lexer/parser/AST/Span/trivia` | syntax/ 全量（tokens/span/lexer/parser/ast_nodes/walk/comments）；E1xxx 诊断 | tests/unit::TestLexer+TestParser 绿 |
| 2 | `feat(python): loader——目录即包与 import 展开（环检测/菱形去重）` | loader/；Span.file 盖章 | module 类 conformance 绿 |
| 3 | `feat(python): semantic resolver——符号表/作用域/引用绑定（E2xxx）` | semantic/（含 NODE_REGISTRY） | diagnostics 类 check case 绿（E2 部分） |
| 4 | `feat(python): type checker——类型模型/注解/推断/匹配穷尽（E3xxx/E4xxx + ruleId）` | typesys/ + contract/rules.py | diagnostics 类 check case 全绿 |
| 5 | `feat(python): typed HIR 与 Core IR——构建/降级/遍历器` | hir/ ir/ | dump hir/ir --json 可用且确定 |
| 6 | `feat(python): AST 解释器——参考语义 Oracle（值模型/内建/stdlib）` | interp/（int64 环绕、format_g/go_quote 位级差分） | run 类 case 解释器路绿 |
| 7 | `feat(python): IR 解释器——四路差分面（v0.9 irinterp 语义等价）` | irinterp/ | AST-vs-IR 差分测试绿 |
| 8 | `feat(python): →Go/→JS 转译后端——prelude 内联与 any 边界收窄` | backends/（exprArg/Spawn 快照/字段链读改写等 v1.0 收口同步） | run 类 case 三路差分绿 |
| 9 | `feat(python): pipeline 与 Agent Contract——check/inspect/dump/--json 信封` | pipeline.py contract/ | inspect/dump 契约 JSON 与 schema 对齐 |
| 10 | `feat(python): semantic graph 与效果推导——callers/callees/impact/effects` | graph/ | inspect graph 族子命令可用 |
| 11 | `feat(python): safe editing——TextEdit 事务/fix --safe/格式化器` | edit/ fix/ formatter/ | fix 事务闭环 + 幂等 property |
| 12 | `feat(python): 语义重构与 bind——rename/organize-imports/safe-remove/extern 草案` | refactor/ bind/ | refactor 三动词 dry-run+apply 闭环 |
| 13 | `feat(python): verify 全管线与 conformance runner + CLI 全动词 + REPL` | conformance/ verify 部分 cli/ | `lang-go-py verify` 九阶段全 ok |
| 14 | `test(python): 单测/conformance/差分套件` | tests/（57 单测 + conformance 全量参数化 + AST/IR 差分 + 解释器vs→JS） | `pytest tests -q` 全绿 |
| 15 | `docs(python): 复刻计划文档——capability matrix/spec-gaps/parity report` | docs/lang-py/*（本目录）+ python/README.md | — |

## 拆分原则

- 依赖单向：1→8 严格分层，9~13 依赖 1~8 的产物面，测试（14）与文档（15）最后。
- 每个提交的信息体注明对应 specs/ 规则段与 conformance 覆盖（对照 parity-report §3）。
- `.tmp-build/` 与 `__pycache__/` 不入任何提交（.gitignore 已含 `python/.tmp-build/`）。

## 收尾核对（提交前）

- [x] conformance 132/132 全绿（参考同刻 127/132，在途收口；套件随在途工作流滚动扩容）
- [x] `pytest tests -q` 全绿（345 passed, 36 skipped——skip 为参考在途案例占位与缺 node 的降级路径）
- [x] specs/rules.md 27 码映射无漂移；schemaVersion "1" 字段面无漂移
- [x] TOOL_VERSION 随 specs/versioning.md（1.0.0）
- [x] 工作区无 lang-py 侧 git 写操作
