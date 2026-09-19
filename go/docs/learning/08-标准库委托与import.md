# 08 - 标准库委托与 import：语言的长出手臂

## 一句话定义

标准库委托 = lang-go 只定义"函数叫什么、语义是什么"，实现交给目标平台（解释器用 Go stdlib，→Go 生成同名调用）；import = 让一个程序由多个文件拼起来，装载器在运行前把图摊平。

## 三个核心要素

**1. 委托的代价是"两端一致"。**
`jsonParse` 在解释器侧是 Go 写的内建（`internal/interp/stdlib.go`），在 →Go 侧是 prelude 里的另一份实现（`internal/gen/prelude.go`）。两份代码必须逐字节对齐——JSON 对象键要排序（Go map 序随机）、stringify 的映射键按字母序、null 映射为 `None`。锁定手段还是老一套：E2E 等价测试 + `examples/json` 程序。没有这份测试，"两端各写一份"就是漂移的温床。

**2. 动态值需要自己的出口。**
JSON 解析结果在 lang-go 里是 any（动态值），它带来了三个 any 边界问题，解法各一个：取字段走 `langGoField` 接口分派（struct 生成 `LangGoField` 方法）；迭代走 `langGoIter` 快照；判 null 走 `isNone`（`None` 是枚举不是字面量，不能直接 `==` 比较——那会撞类型检查）。这三个出口是"静态语言的动态逃生舱"，每个都只有十几行，但少任何一个，动态值就是二等公民。

**3. import 的本质是"装载期图展开"。**
`import "./x"` 不是运行时语句——装载器（`internal/load`）在程序跑起来之前把整个 import 图摊平成一个语句序列。所以"循环导入"是装载错误而非运行错误；菱形导入（两处导同一个库）静默去重而不是重复定义。类比：C 的 #include 但有环检测，Go 的 package 但按文件合并。

## 最容易混的误解

误解：有了 http.get 就能写网络程序了，和 Go 一样。
`http.get` 是最小面：GET + headers，返回 `Result[HttpResp, error]`，其中 4xx/5xx 也是 Ok（状态码在 HttpResp.status 里自己判断）——网络错误才走 Err。没有 post、没有 cookie、没有超时配置。够 linkpull 的 API GET 用，不够写爬虫。这是刻意的（decisions #36）：第二个消费方出现之前不扩张。

第二个误解：extern 块是 FFI。
v0.2 的 `extern "go" { fn 签名 }` 只声明名字和签名：解释器里调用即报"仅转译可用"，→Go 里生成 panic 桩。真正的实现在 prelude 里由维护者手写。它是"转译器认识宿主函数"的登记簿，不是自动绑定。

## 在本项目里看什么

- `internal/interp/stdlib.go` 与 `internal/gen/prelude.go` 对照读：同一份语义的两份实现，注释里互相指认。
- `internal/load/load.go`：百行出头的装载器——`expand` 递归、`resolve` 的 containment + 环/去重双状态（`inProgress`/`done`）。测试文件里菱形、循环、越界三例俱全。
- `m4/list.lang-go` 的 `jsonStr`：动态 JSON 值取字段的惯用法——`has` 探测 + `isNone` 判空 + 缺省 ""，对齐 Go 的零值语义。
- `examples/json/`：两端 JSON 行为的最小可执行对照。

## 行业对照

Haxe 的 stdlib 按 target 分实现（`haxe.Http` 在各平台各有后端），是"委托 + 两端一致测试"的工业级样本。TypeScript 的 `lib.dom.d.ts` 走的是另一条路：类型声明而非实现委托。lang-go 的 `extern` + prelude 组合更接近 Haxe 前身 neko 的宿主函数登记法。

## 下一步

M4 已收口。剩余工程见 roadmap：→JS 后端（第二张映射表）、格式化器（工具链最后一项承诺）、最小静态检查器。学习路径上，下一块值得啃的是"同一语义三种实现"（解释器、→Go、将来的 →JS）如何靠等价测试钉住——你已经在 json 上见过一次了。
