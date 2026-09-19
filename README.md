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
cd vibe/languages/lang-go
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
