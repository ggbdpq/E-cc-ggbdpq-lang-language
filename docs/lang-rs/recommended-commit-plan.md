# lang-rs Recommended Commit Plan

> 仅计划，不执行。开发期零 git 写操作已满足；以下拆分建议供人工审阅后实施。
> 原则：每提交独立可编译、测试绿、`cargo clippy -D warnings` 干净；按依赖拓扑排序。

## 前置检查（实施前人工确认）

1. `docs/lang-rs/spec-gaps.md` 的 13 条 OPEN gap 是否接受随本批入库（建议：入库，状态 OPEN 是诚实记录）。
2. `impls/rust/target/` 已 gitignore；`impls/rust/Cargo.lock` 建议入库（可执行复现）。
3. 建议同期更新根 README 的仓库结构表（`impls/rust/` 行）——可作为最后一笔 docs 提交或并入第 1 笔。

## 提交拆分（15 笔）

| # | Message | 内容 | 验证 |
|---|---|---|---|
| 1 | `chore(rust): bootstrap workspace and source model` | workspace Cargo.toml + lang-core（身份/Span/SourceText/诊断契约）+ 计划包文档 | cargo test -p lang-core |
| 2 | `feat(rust-syntax): add lexer parser ast and lossless trivia` | lang-syntax 全量 + conformance syntax 夹具 | -p lang-syntax |
| 3 | `feat(rust-semantic): add resolver type system and checker` | lang-types + lang-semantic（E2xxx/E3xxx/E4xxx 全量） | -p lang-types -p lang-semantic |
| 4 | `feat(rust-hir): add typed hir` | lang-hir 节点模型 + build | -p lang-hir |
| 5 | `feat(rust-ir): add core ir and lowering` | lang-ir 指令集/validator/降形/对照表/dump | -p lang-ir |
| 6 | `feat(rust-runtime): add value place reference and closure model` | lang-runtime 全量 | -p lang-runtime |
| 7 | `feat(rust-interp): add ast and ir interpreters` | lang-interp 双解释器 + 差分矩阵 | -p lang-interp |
| 8 | `feat(rust-backends): add go and javascript backends` | 双后端 codegen + prelude + fmt/json + E2E | -p lang-backend-go -p lang-backend-js |
| 9 | `feat(rust-tooling): add loader formatter cli and repl` | lang-loader + lang-formatter + lang-cli | 对应 -p |
| 10 | `feat(rust-agent): add diagnostics contract effects and graph` | lang-contract（DTO/effects）+ lang-graph | 对应 -p |
| 11 | `feat(rust-edit): add safe editing and refactoring` | lang-edit + lang-refactor | 对应 -p |
| 12 | `feat(rust-verify): add verification and conformance runner` | lang-verify + lang-conformance（runner/路由/report） | 对应 -p |
| 13 | `test(rust): add cross-implementation differential and dogfood` | 差分守卫/dogfood E2E/确定性压力（若未随 7/12 入） | 全量 |
| 14 | `docs(rust): publish parity and ownership reports` | docs/lang-rs/ 八文件（parity-report 等） | — |
| 15 | `chore(release): wire rust into repo docs and ci` | 根 README 目录表 + .github/workflows ci.yml 追加 rust 作业（fmt/clippy/test/conformance/dogfood） | CI |

## 替代方案

若嫌 15 笔碎，可压缩为 5 笔：lang-core+syntax（1）/ semantic+hir+ir（2）/ runtime+interp+backends（3）/
tooling+agent+verify（4）/ docs+ci（5）。不建议单笔巨型提交——review 面与回滚粒度都差。
