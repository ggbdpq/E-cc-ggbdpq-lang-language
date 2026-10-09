# conformance/ — 跨实现一致性套件

本目录是 lang-go 的**共同真值**：任何实现（Go / TypeScript / Python / Rust / C#）跑完
同一套件全绿，即视为与语言规范行为一致。套件独立于任何实现目录。

## 布局

```text
conformance/<category>/<case-name>/case.yaml     manifest（YAML 受限子集）
conformance/<category>/<case-name>/main.lang-go  入口（目录即包）
conformance/<category>/<case-name>/*.lang-go     可选附加文件（module 类）
```

16 类：syntax / types / generics / struct / enum / match / option / result /
closure / module / collections / io / concurrency / diagnostics / values / expressions。

## manifest（YAML 受限子集）

```yaml
name: result-propagation-basic
category: result
mode: run              # run | check
expect:
  exitCode: 0          # run 模式必须 0
  stdout: |            # 块标量：末尾恰一个换行、无空行
    ok
  stderr: ""           # run 模式必须空串
  diagnostics:         # 仅 check 模式
    - code: E4001      # 码精确匹配
      severity: error
      line: 6          # primarySpan.start.line 精确匹配
```

解析器只支持以上形态：顶层标量键、恰一层 `expect:` 嵌套、`|` 块标量、
`diagnostics:` 列表项（`- ` 起头 + 延续字段）、`#` 注释。**任何超集形态一律报错**——
套件拒绝模糊输入。不支持转义、锚点、多级嵌套、流式标量。

## mode 语义

- **run**：AST 解释器 / IR 解释器 / →Go 产物（go build 后执行）/ →JS 产物（node ESM）四方执行；
  stdout 与期望逐字节一致、exitCode 0、stderr 空。四方输出本就要求逐字节一致，
  故只声明一份期望。
- **check**：程序不执行；静态诊断的 code + severity + primarySpan.line 多重集
  与期望相等。message 文本与列号允许漂移（diagnostics spec 的既定口径）。

## 确定性例外（文档化）

- `go` 语句 + chan：解释器/→JS 为顺序确定同步子集，→Go 为真 goroutine
  （spec §6.4）。套件只用四路结果确定的形态（`go send(ch, v)` 后主线程 `recv`）。
- 运行时错误文本（越界、channel 空等）跨后端措辞不同，归 diagnostics spec 的
  message 漂移面，**v0.8 不进 run 模式比对**——run 模式 case 一律为正确程序。
- `jsonStringify` 映射键按字母序输出（spec §内建两端确定）；映射字面量本身
  保插入序。两者都是规范行为，不是例外。

## 与 runner contract 的关系

本套件是 specs/runner-contract.md 五动词（compile/check/run/inspect/verify）的
共同消费方。Go 实现：`go test ./internal/conformance/` 全量执行；
`lang-go verify` 提供单程序全管线验证。
