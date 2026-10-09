# lang-go

一门覆盖前端、后端与日常工具开发的个人语言：一套源码，转译到 Go 跑后端与系统侧，转译到 JS 进浏览器；GC 与标准库能力委托给目标平台。定位是个人统一工具链，不求社区采纳。设计决策与理由见 [docs/decisions.md](docs/decisions.md)，语言规范见 [docs/spec.md](docs/spec.md)。

## 现状

| 里程碑 | 内容 | 状态 |
|--------|------|------|
| M1 | 规范 v0 + 决策台账 | 完成 |
| M2 | Go 实现词法器/解析器/树遍历解释器 + REPL + `lang-go run`/`lang-go test` | 完成 |
| M3 | →Go 转译后端 + 编译期穷尽检查 + extern/http/json/import | 完成（→JS 未做） |
| M4 | 移植 linkpull bilibili 提取器核心 | **完成**：md5/wbi/video/list 纯函数层，`lang-go test` 11/11，E2E（解释器 = →Go = gofixture 原版输出）全绿 |
| M5 | →JS 后端 + 格式化器 + 最小检查器 + CLI 子目录 | 未开始（见 docs/roadmap.md） |

v0 解释器的边界（诚实声明）：不做静态类型检查（注解为文档性）、match 穷尽性在解释器运行时检查、单模块运行（import 仅限模块根内相对路径）、单线程（go 同步执行，chan 阻塞语义见 decisions #63）。→Go 转译的约束与映射表见 spec §6。

## 上手

```bash
cd vibe/labs/language/go
go build -o lang-go.exe ./cmd/lang-go

(cd examples/hello && ../../lang-go.exe run)     # 每个示例目录一个 main.lang-go
(cd examples/tour && ../../lang-go.exe run)      # 十分钟可执行入门教程
(cd examples/selftest && ../../lang-go.exe test) # lang-go 自测
(cd examples/compound && ../../lang-go.exe build) # 产物写入 out/（build js 为 →JS）
./lang-go.exe                                    # REPL（:quit 退出）
mkdir bind.d && put go pkg .go files in bind.d/   # bind 准备
./lang-go.exe bind                               # 生成 extern 声明草案（stdout）
go test -p 1 ./...                            # 本机编译器并发易崩，固定 -p 1
```

v0.1 CLI 不接收路径参数：`lang-go run` 固定运行当前目录的 `main.lang-go`，`lang-go test` 固定扫描当前目录（安全边界：文件输入无 `..` 穿越面）。

## 开发检查

`conformance` 的 run 用例逐一比较 AST 解释器、IR 解释器、→Go、→JS 与 manifest 的期望 stdout；`verify` 比较这四路输出是否一致。IR 循环在最近一层消费 `break`，并将 `return` 传播到函数调用边界。

本地运行 `go build ./...`、`go vet ./...`、`go test ./...` 与 `~/go/bin/golangci-lint run ./...`（v2.14.0，含存量问题）。根仓库 CI 的 Go job 运行 vet 与增量 lint；lint 只阻挡 PR/push diff 新增的问题，存量按类别逐步清理。

CI 仅在临时配置中加入固定基线 `630905f`：事件 diff 可用时由 action 优先过滤；新分支首推或 API 取 diff 失败时，比较采纳前基线之后的变更，避免退回全量存量门禁。原 `.golangci.yml` 保持本地完整检查口径。

## 目录

```
docs/decisions.md    设计决策台账（grilling 共识 + M3/M4 追加决策）
docs/spec.md         语言规范 v0（与实现同步修订，变更留痕）
docs/roadmap.md      剩余工程路线图（extern/import/→JS/格式化器等）
docs/learning/       逐步施工学习指南（设计/词法/解析/解释器/REPL/转译/移植）
internal/syntax/     词法器 + AST + 解析器
internal/interp/     树遍历解释器 + 内建
internal/gen/        →Go 转译器 + 运行时 prelude
cmd/lang-go/            CLI 与 REPL（main.go 薄入口 + cli.go）
examples/<名>/main.lang-go  示例：hello / shapes / result / closures / json / compound / tour / goroutine / channel
examples/selftest/   lang-go 语言自测（lang-go test）
m4/                  M4 移植：md5.lang-go / wbi.lang-go / main.lang-go + 测试
m4/gofixture/        linkpull 原版逐字拷贝的对照 fixture 生成器
```

## 执行与转译失败

IR 解释器的 `print` / `println` 会把输出设备写入错误返回给调用方；JS 转译器在 `if`、`else`、`while` 块内遇到转译错误时返回错误与空产物，和顶层语句保持一致。

诊断与 JSON 内建的错误链保留：`pipeline.Diagnostics` 可读取包装后的诊断，JSON 解析和序列化失败可用 `errors.As` 识别宿主错误类型；现有可见错误文案保持不变。
