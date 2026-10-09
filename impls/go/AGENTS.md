# Notes for coding agents

lang-go 编译器的 Go 实现（module `lang-go`，go 1.26）：一门个人语言，一套源码转译到 Go（后端/系统侧）与 JS（浏览器），GC 与标准库委托目标平台；自带 AST 树遍历解释器与 REPL。前端管线为 parse → resolve → typecheck → HIR → IR，四个执行后端（interp / irinterp / →Go / →JS）以"同一程序输出逐字节一致"为机械判据。

主要包（均在 `internal/` 下）：

- `syntax/` — 词法器 + AST + 解析器，附注释 trivia 扫描（Lossless 格式化用）
- `semantic/` — resolver / 模块级语义，产出稳定诊断码（E1xxx 解析、E2xxx 解析名）
- `types/` — 类型检查器，收编 →Go/→JS 全部前置约束（注解、? 位置、穷尽性）
- `hir/`、`ir/` — Typed HIR 与 Core IR（dump 有 snapshot 测试锁定）
- `interp/`、`irinterp/` — AST 参考解释器与 IR 解释器（语义必须逐字节对齐）
- `gen/`、`genjs/` — →Go 与 →JS 转译器（gen 含运行时 prelude）
- `pipeline/` — 串起 parse → resolve → typecheck → HIR → IR，CLI 与测试共用
- `load/` — 程序装载：import 展开、菱形去重、环检测
- `prelude/` — 预载代数类型（Option/Result/error）定义的单一来源
- `format/` — 格式化器（幂等 + 语义等价 + 注释 100% 保留）
- `bind/` — Go 导出函数 → langGo extern 声明草案生成
- `contract/` — check --json 契约负载（稳定码 + 行级 Span）
- `graph/`、`refactor/`、`fix/`、`edit/` — 代码图谱建边、rename/organize imports/safe remove、safe fix 事务、结构化编辑
- `conformance/` — 确定性契约测试：HIR/IR dump、诊断序、format、双转译产物、四路执行输出逐字节稳定
- `version/` — 唯一版本源（CLI version / Agent Contract / 发布 tag 同源）
- `cmd/lang-go/` — CLI 与 REPL（main.go 薄入口）
- `m4/`、`m4/gofixture/` — linkpull bilibili 提取器纯函数移植及原版对照 fixture

设计决策见 `docs/decisions.md`，语言规范见 `docs/spec.md`，施工指南见 `docs/learning/`。

## 常用命令

- 构建：`go build ./...`
- 测试：`go test ./...`（gen/genjs/conformance 会执行 go build 与 node 外部进程，偏慢属正常；本机高并发编译易崩时按 README 用 `go test -p 1 ./...`）
- 静态检查：`golangci-lint run ./...`（v2.14.0，配置见 .golangci.yml；本机二进制在 ~/go/bin/golangci-lint）
- 格式：改动文件 `gofmt -l` 输出必须为空

## Go 规范

根级约定见 /Users/ggbdpq/cc/guahub/AGENTS.md 与 .agents/skills/golang-*（code-style、error-handling、testing、concurrency、context、lint），按需读取。测试只用标准库，不引入 testify。

## 工作纪律

- 修 bug 先写一个"把修复还原后必失败"的测试，提交说明里写它在旧代码上以什么失败。
- 交付说明固定两行：已跑 X（结果）；未跑 Y（原因）。未跑不写成通过。
- 改动行为同步更新相关文档；教训记 LESSONS.md（3 次重现升格为必查项）。
- 提交信息写可观察行为（如 `parser: X now Y instead of Z`），一次提交一个目标。
