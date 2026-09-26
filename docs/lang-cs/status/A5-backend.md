# A5-backend 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

### 1. 双后端输出格式化规则层 + JSON 面（首批交付）

`Lang.BackendGo` / `Lang.BackendJs` 各自的 `Fmt` + `Json` 纯函数模块（零依赖，不消费
IR——IR 冻结前的 fmt 自有形状 `FmtNode`，两程序集同构不共享，沿 lang-rs 决策）。
期望值全部手算，并经本机 go 1.27.0 标准库对拍独立确认（`strconv.FormatFloat('g',-1,64)`
/ `strconv.Quote` / `encoding/json` 三面逐值对拍，非回读自身输出）。

**打印面（RUNTIME-OUTPUT-003/004）规则清单（已冻结，与 lang-rs 逐条对齐同规范形）**：

| 规则 | 规范形 | 证据来源 |
|---|---|---|
| float 文本 | Go `strconv.FormatFloat('g',-1,64)` 镜像：最短舍入数字；指数 < -4 或 >= 6 走 `d.ddde±XX`（指数至少两位恒带符号），否则定点无尾零；`NaN`/`+Inf`/`-Inf`/`-0` | specs/runtime.md RUNTIME-OUTPUT-004 + TS `interp/value.ts` goFormatFloat + jsprelude langGoFloatStr（FIX 注释）；手算+Go 对拍：1.0→`1`、0.1→`0.1`、1e6→`1e+06`、1e-4→`0.0001`、-0→`-0`、1/3→`0.3333333333333333`、5e-324→`5e-324` |
| int64 | 全域十进制（long 原生，i64::MIN = -9223372036854775808 用例锁定）；JSON 面同样直出不经浮点（2^53+1 = 9007199254740993 精度用例） | TS interp `fmtVal`/`marshalJSON` jsonInt 分支 |
| 字符串顶层/嵌套 | 顶层原样；嵌套带引号。→Go 用 strconv.Quote 语义（`\a\b\f\n\r\t\v`、`\xNN` 小写、非打印→`\uNNNN`/`\UNNNNNNNN`、U+2028/9 转义、打印 Unicode 直出）；→JS 用 JSON.stringify 语义（仅 `"\` + <0x20，U+2028/DEL/NBSP/私用区直出）。跨后端分歧按 lang-rs SPEC-GAP-RS-001 口径如实分别实现 | TS `value.ts` goQuote vs `jsprelude.ts` langGoFmt 字符串分支；Go 对拍锁定 `\x7f`/`\u00a0`/`\u200d`/`\u00ad`/`\ue000`/`\U0010ffff`/`\u0378` 各点位 |
| IsPrint 实现 | C# 用 `Rune.GetUnicodeCategory` 直读 Unicode 类别表：L/M/N/P/S + ASCII 空格为打印——比 lang-rs 在 std 无类别表下的硬编码近似更精确（未分配 Cn/私用区 Co/分隔符 Z*/格式符 Cf 全部非打印，与 Go 一致，`quote_go("\u0378")`→`\u0378` 对拍证实） | Go strconv.Quote×unicode.IsPrint 语义 |
| map 打印 | `{k: v, ...}` 按插入序（列表承载，禁 Dictionary 迭代序），键嵌套形 | goprelude/jsprelude + interp fmtVal |
| struct/enum/array | `Name{f: v, ...}` 声明序 / `Tag(p1, p2)`·`Tag` / `[a, b]` | RUNTIME-OUTPUT-003 + interp fmtVal |
| Some/None/Ok/Err/error | `Some(v)`/`None`/`Ok(v)`/`Err(e)`/`error{msg: "<引号形>"}`（→Go 用 strconv.Quote 形、→JS 用 JSON 形） | 同上 |
| println | `FmtArgs` 实参顶层形空格分隔，换行由调用点 | RUNTIME-OUTPUT-003 |

**JSON 面（RUNTIME-JSON-002 硬契约，两后端同文）**：映射键**码点序**输出（Go
sort.Strings 的 UTF-8 字节序 = 码点序）——C# 陷阱：`string.CompareOrdinal` 是
UTF-16 序，astral 键会错位（`\uFFFF` vs `\U{10000}`：码点序 65535 < 65536，
UTF-16 序颠倒），故实现逐码点 `CodePointCompare` 并用该对键的排序用例锁定；
None/unit → `null`；HTML 转义开启（`< > &` → `\u003c/\u003e/\u0026`）；
float 按 Go encoding/json：abs∈[1e-6,1e21) 定点否则 `e` 形负指数去前导零
（1e8→`100000000`、9.9e-7→`9.9e-7`、-0.0→`0` 解释器语义）；NaN/Inf 受控错误
`JSON 序列化失败: json: unsupported value: NaN/+Inf/-Inf`；非字符串键受控错误
`JSON 对象键须为 string，得到 {类型}`；不可序列化变体 `{值名} 不可 JSON 序列化`。
以 TS interp（builtin.ts marshalJSON + value.ts goJSONFloat/goJSONString）为规范源。
受控错误用 Try 模式（`TryStringify/TryJsonFloat` out 参数），不抛异常。

**TDD 揪出的真 bug（红→绿证据）**：最短数字提取首版照搬带 dot 位公式
`dp = exp + dot + 1`——C# E 形尾数恒为「点前恰一位」，正确式 `dp = exp + 1`；
`double.MaxValue` 出 `1.7976931348623157e+309`（指数 +1）、`1.5e-7` 出 `1.5e-06`
被手算期望当场抓出，双后端同修。另：`quote_go("\uE000")` 期望串误写大写十六进制，
实现按 Go 输出小写 `\ue000`（Go 对拍裁定），修测试不改实现。

### 2. Lang.Loader 模块图（首批交付）

`ModuleLoader.TryLoadProgram(dir, includeTests, importsOf, out program, out error)`
覆盖 MODULE-ROOT-001..TEST-007：目录即包（两阶段：目录内非测试 .lang-go 文件名序
全读并标 done + 去扩展名键，再按序展开 import 后落文件自身——与参考实现的预读去重
语义逐点一致）；`./x` → `x/` 目录或 `x.lang-go`（扩展名由装载补，import 原文不带）；
越根 → `第N行: import <p> 逃出模块根目录`；非 `./` →
`第N行: import 仅支持 "./相对路径"（得到 "<p>"）`；菱形去重（done 集，含同目录
去扩展名键）；真环 → `第N行: 循环 import <p>`（in_progress 链）；
`*_test.lang-go` 仅 include_tests=true 追加且 import 同样展开。词法规范化用
`Path.GetFullPath`（绝对入参纯词法折叠，等价 Go filepath.Clean，不做符号链接解析）。
import 提取走注入委托 `ImportsOf(relPath, source)`，本层不做语法分析。
rel_path 一律 `/` 分隔（`RelToRoot` 自拼）。测试 17 条（临时目录夹具 + 注入桩）。
证据：`impls/go/internal/load/load.go`、`impls/typescript/src/loader/loader.ts`。

**如实锁定的参考实现语义**（同 lang-rs 登记，非本实现发明）：同目录互 import 是
去重跳过、合并序保持文件名序；根预读 `x.lang-go` 的去扩展名键遮蔽同名目录包 `x/`；
import 原文带扩展名会产出 `x.lang-go.lang-go` 读文件错误。

### 3. Lang.Cli L0 骨架（首批交付）

`CliApp`（手写分派，零 NuGet 新依赖）：`version`/`--version`/`-V` →
`lang-cs 0.1.0`（stdout，退出码 0）；`help`/`--help`/`-h` → usage（stdout，0）；
未知命令/无参 → usage 到 stderr + 退出码 2。usage 只列已实现命令（未接线的
run/check 等不谎报开放，注明随解释器/后端接线分批开放）。Program.cs 一行转发
`CliApp.CliMain`。测试 6 条（版本行钉死、退出码、astral 命令名干净走 2）。

### 4. 验证矩阵（最终）

- `dotnet test`（Debug，真工程）：**72/72 全绿** —— BackendGo.Tests 26 +
  BackendJs.Tests 23 + Loader.Tests 17 + Cli.Tests 6。
- `dotnet build LangCs.slnx -c Release -warnaserror`：0 警告 0 错误（csproj 层
  TreatWarningsAsErrors=true 全程生效）。
- `dotnet format --verify-no-changes`（本人 8 个项目逐项）：全过。BackendGo/Js
  首轮揪出 case 块花括号缩进的空格违规（真实违规非误报），format 就地修复后
  复验过，行为面测试复跑仍全绿。
- 真二进制冒烟：`Lang.Cli.exe --version` → `lang-cs 0.1.0`（exit 0）；
  未知命令 → usage 到 stderr（exit 2）。
- 过程性验证（链路阻断期的旁路）：A3 的 Lang.Hir 在途重写（68 错）一度拖断
  BackendGo→Lang.Ir→Lang.Hir 与 Cli→Lang.Runtime 依赖链，当时以仓库外临时
  xunit 工程 `<Compile Include>` 链接同批源文件先完成行为验证（55/55 绿）并
  揪出 E 形 dp 公式 off-by-one；链路恢复后以上述真工程矩阵为准。临时工程
  已清理，不进仓库。

### 2026-09-26 第二波交付：Core IR → Go/JS 源码生成 + CLI check/fmt/build/verify 接线 + dogfood 实测

#### 1. Lang.BackendGo / Lang.BackendJs：IR → 单文件可执行产物（codegen + prelude）

`CodeGen.TryGenerate(IrProgram, out source, out error)` → 单文件自包含产物
（Go：`package main`，`go build <file>` 直接可用；JS：ESM `.mjs`，`node <file>.mjs`
直接可用）。prelude 内联常量 `Prelude.GoPrelude` / `Prelude.JsPrelude`，行为面按
lang-rs `lang_backend_{go,js}`（集成轮修复后最新结论）逐行移植，三个已修 bug
直接规避：

| 决策点 | 形状 | 依据/教训 |
|---|---|---|
| 值域 | 全动态 `any`（Go）/ JS 动态 + int=BigInt；Option/Result=普通 enum 值 Tag Some/None/Ok/Err | IR 无类型表，TypeId 不可解 |
| 调用约定 | 每函数 `lf{n}_{名}(as ...any) any`（JS：`function lf{n}_{名}(...as)`）统一变参；捕获/参数槽=`as[i]`，局部=`l{i}` 预声明；闭包调用 `langGoCallClosure` 把 Env 前置落地 | VALUE-CAPTURE-004 |
| 控制流 | Go：label `L{k}`+花括号块+goto（只给被引用块发 label——Go 视未用 label 为编译错误）；JS：`let __pc`+`while(true) switch` 状态机，`break` 即跳转 | 块入口栈深 0 不变量 |
| 操作数栈 | 符号串栈内联（IR 无 Dup，每值至多消费一次）；Try/IterNext 物化临时 `_t{n}`；**has_ret=false 的调用按语句发射**（lang-rs E2E 真 bug 教训：当表达式压栈则语句整体丢失） | 指令栈效应表 |
| 值语义 | 用户 struct 以 `*S{n}_{名}` 指针（JS：`__langGoStruct` 标签对象）承载，绑定/传参/返回/容器写入点显式 `langGoCopy`（JS 同点拷贝）；「读出皆副本」VALUE-ELEM-009 | VALUE-COPY-001/002/009 |
| **B2 规避** | `ArrayPush` 发射 `langGoPushArr(arr, rhs)`（rhs 须数组、原位逐元素拼接、消息逐字对齐机器）；`push` 内建（`langGoPush`）返回数组本身、两者不混用 | lang-rs B2 拼接语义教训 |
| **B3 规避** | Go `RangeNew` 发射 `langGoRange{Lo: langGoInt(lo), Hi: langGoInt(hi)}`（any→int64 断言，对齐 SwitchInt 整型操作数处理）；JS 动态域 `__langGoRange` 值对象不受影响 | lang-rs B3 int64 cast 教训 |
| **B1 规避** | `mark_ref_reads`：按 lowering 链形（`[LocalGet|GlobalGet, (FieldGet | LocalGet+IndexGet)*] → (LocalGet(val),) FieldSet|IndexSet|ArrayPush`）回溯标记写链地址组件的 FieldGet/IndexGet，发射 Ref 读形 `langGoFieldRef/langGoIndexRef`（不落副本、最终 set 写穿）；prelude 入口补 `langGoUnref`；回溯不匹配保守回退普通读形（hand-built IR 安全） | lang-rs B1 修复牵出的写链缺口教训 |
| 并发 | Go：原生 `chan any`(1024)+真 goroutine，spawn 实参快照进 `[]any` 字面量，含 Spawn 函数每个 Return 前 + main 尾 `langGoWaitAll`；JS：同步确定子集（spawn=直接调用+实参逐个 `langGoCopy` 快照，chan=数组队列，recv 空=Err"channel 空"） | TASK-SPAWN-002/CHAN-MAP-005 |
| 映射键 | JS `i:/s:/b:` 类型前缀 + `#o:` 原始键条目保首插序（int 1 与 string "1" 不撞）；Go `langGoKeyString` 同前缀 | 参考实现撞键 bug 直接规避 |
| `?` | Try 发射为临时+`langGoIsErr` 检查：Err 存 err 槽跳 fail 块（fail 块合成 Err 返回） | RESULT-TRY-003 |
| for-in | `IterNew` 快照迭代器（数组索引序/映射插入序/字符串码点序/区间半开），元素读出拷贝 | CTRL-FORIN-003 |
| 常量 | float→最短科学计数（Rust `{:e}` 同形；Go `float64(...)` 包装、NaN/±Inf/-0 走 math/字面量）；字符串 Go 用 strconv.Quote 语义（复用第一波 `Fmt.QuoteGo`）、JS 用 JSON 转义面（`JsStrLit`）；int 常量 JS 为 BigInt 字面量（负数带括号） | RUNTIME-OUTPUT-004 |
| 确定性 | 发射顺序 = 声明序/块序/指令序，无哈希序遍历；**同 IR 两次生成逐字节相等（测试锁定）** | specs/versioning.md |

受控错误：未知内建名 → `GenError`（Try 模式，失败时 source=null，不抛异常）。
两程序集同构不共享（沿第一波 lang-rs 决策；消毒/浮点提取/mark_ref_reads 各有同形副本）。

#### 2. E2E 矩阵（红→绿，`go build` 执行 / `node` 执行，stdout 逐字节断言）

夹具 = lang-rs 同一形状（手构 IR，过 `Validator.Validate`，每块栈效应手算平衡）+
**新增 B1 写链写穿回归段**（`arr[0].x = 9`：IndexGet 作为写链地址组件须发 Ref 读形，
否则 FieldSet 写在 `langGoCopy` 副本上、原数组不变——夹具直接锁定该行为）。
期望 stdout 24 行手算锁定：算术+float+字符串拼接 / str / 直接调用 / 闭包捕获计数器
（3 次调用写穿=3）/ 引用别名写穿（42）/ 数组-映射引用共享（ArrayPush 拼接、has）/ 
struct 拷贝（q 改 p 不动）/ map 插入序打印（b,a,c）/ chan FIFO / Option match /
Result+`?` 传播（Ok 50、Err.msg neg）/ spawn 实参快照（go 后改源=1）/ for-in 区间求和 /
SwitchInt / 全局 init+读改写 / 一元负号非 / 字符串切片与索引 / 数组切片 / **写链写穿
`P{x: 9, y: 8}`**。两后端同夹具同期望（跨后端差分信号）；外部工具存在性守卫
（`LANG_CS_GO`/`LANG_CS_NODE`/PATH/已知路径探测，缺失 skip 打印原因）；
临时目录清理（`LANG_CS_KEEP` 可保留排查）。

**TDD 红→绿证据**：首版夹具 b25 误把 `ArrayNew` 前的元素数当栈值压（栈深 2≠终接消费 1）
被 Validator 当场抓出；`𝕏`（U+1D54F 数学字母类）在消毒规则中属字母保留，测试期望
误写 `__` 被实现裁定为 `_𝕏`——两处修正均记录在案。

#### 3. Lang.Cli：check / fmt / build / verify 全接线（run/test 阻断见下）

| 命令 | 行为 | 退出码 |
|---|---|---|
| `check [目录] [--json]` | 装载（Lang.Loader）→ 逐文件 parse（SourceId 按装载序）→ 合并语句流剥除 import → `LangSemantic.Analyze` → 诊断聚合；text：`{file}:第{line}行: [{code}] {message}`（多文件 `file:` 前缀消歧）到 stderr + `检查未通过:`，干净打印 `检查通过`；--json：`{schemaVersion:"1", toolVersion, kind:"check", success, diagnostics[]}`（DiagnosticDto 直出，字段序即 schema 序） | 有诊断 1 |
| `fmt [目录]` | 目录内（不递归）`.lang-go` 就地 `Formatter.FormatSource`，打印改动文件名 + `N 个文件已格式化` | 格式化失败 1 / 读目录 2 |
| `build [go\|js] [目录]` | 检查 → `Lang.Ir.Compiler.Compile`（A3 真降形）→ 本波 codegen → 写 `{目录}/out/main.go|.mjs`（Go 证据同文案提示）；未知目标 → `未知目标: …（可选 go / js）` | 检查/降形/生成失败 1 / 未知目标 2 |
| `verify [目录] [--json]` | `Lang.Verify.VerifyEngine.Verify`（九段管线，DefaultVerifyDriver 已接 A3 降形 + 本波双后端生成 + go/node 工具链执行）；text：逐段 `parse 通过`/`X 失败: …`/`X 跳过: …` + `验证通过/失败: X`；--json：`kind:"verify"` + passed/failedStage/stages[9]（Skipped 显式在册） | passed()=false → 1 |
| `version` / `help` | 同第一波（`lang-cs 0.1.0`） | 未知命令 2 |

装载面（CLI 内部 `Loader.LoadModule`）：`ImportsOf` 委托由 CLI 内部 `ParseImports`
真实现收拢（Lang.Syntax parse 抽顶层 Import，解析失败返回空列表——Lang.Loader 本波
冻结不动，故未按第一波计划上提为 Loader 公共 API）。信封 JSON 输出 = 两空格缩进
（Go `json.MarshalIndent`/serde pretty 同构）+ 非 ASCII 码点字面（与 Lang.Contract 口径一致）。

#### 4. dogfood 实测矩阵（dogfood/langalgo，66 文件 / 1953 行 / 6 个 test 函数）

| 命令 | 结果 | 与 Go 参考差分 |
|---|---|---|
| `check` | `检查通过`，exit 0 | Go `lang-go check` 同绿（exit 0） |
| `check --json` | success:true 信封，exit 0 | — |
| `build go` / `build js` | 产物 `out/main.go` / `out/main.mjs` 落盘，exit 0 | — |
| **产物执行**（build go → `go run out/main.go`） | stdout `ok 1` | **与 Go 参考 `lang-go run` 输出逐字节一致**（diff 空） |
| `verify` | parse/resolve/typecheck/hir/ir/effect/compile/execute 八段通过；`differential 跳过: 已比对 2 路逐字节一致；缺失路: ast(AST 参考路未接线), ir(IR 解释器未接线)`；`验证通过`，exit 0 | 差分 2 路 = 本波 →Go/→JS 产物经 go/node 实跑 |
| `fmt` | `0 个文件已格式化`（语料已规范化，幂等；副本上先验证再动真目录） | — |
| `test` | 未接线（A4 Lang.Interp 零源文件）；Go 参考实测 `6 通过, 0 失败`（记录参考值） | 阻断项 |
| `run` | 未接线（同上）；**等价路径已验证**：build 产物执行 stdout 与参考一致 | 见上 |

**负例差分（发现 Go 参考宽纵，非本侧缺陷）**：conformance
`diagnostics/undefined-variable`（case.yaml 期望 E2002@line 2）——lang-cs 正确报
`main.lang-go:第2行: [E2002] 未定义 x`、exit 1；Go 参考报 `检查通过`、exit 0。
lang-cs 以 case.yaml + lang-rs 口径为准。

#### 5. 测试数与验证矩阵（最终）

- 本波新增测试：BackendGo 32（发射面/常量形/消毒/受控错误/确定性/E2E + 首波 26 保留）；
  BackendJs 30（同构 + JS 字面量专项）；Cli 18（check 6 + fmt/build/verify 6 + 骨架 6）。
- `dotnet test`（本域 4 项目）：**97/97 全绿**（32+30+18+17，Loader 首波未动无回退）。
- `dotnet test LangCs.slnx -c Release`：**全解决方案绿**（含 Syntax 95 / Ir 87 / Runtime 81 /
  Semantic 81 / Refactor 31 / Conformance 25 / Contract 24 / Verify 23 等兄弟项目——
  本波收工时全绿，无破坏）。
- `dotnet build LangCs.slnx -c Release -warnaserror`：0 警告 0 错误。
- `dotnet format --verify-no-changes`：本人 6 个项目（3 src + 3 tests）全过。
- 真二进制冒烟：Lang.Cli.exe check/version/build/verify 对 dogfood 与临时夹具实测（见上表）。
- 过程性观察（不阻塞）：本波中段 A3 在途提交两度拖断 Lang.Hir/Lang.Ir 构建链
  （Build.cs 未含 Analysis 引用、IrLowerer 类型名与 A6 Exec.cs 引用不一致），
  均随兄弟项目收敛自愈；期间进度按「可编译优先」推进，最终矩阵以收工态为准。

### 2026-09-26 第三波交付（收官波）：CLI 全命令接线 + verify 四路差分 + dogfood 全矩阵逐字节对拍

#### 1. CLI 补齐 run / test / inspect / dump / refactor（证据：lang-rs lang-cli commands.rs / inspect.rs / dump.rs / refactor.rs + Go cli.go；命令面以 lang-rs 已验证形态为准）

| 命令 | 行为 | 退出码 |
|---|---|---|
| `run [目录]` | 装载 → analyze：分析诊断非空打 check JSON 信封并停（解析诊断走 check 文本契约）→ AST 参考解释器（Lang.Interp）stdout 直出；运行期错误 `错误: {msg}` 到 stderr | 诊断/错误 1 |
| `test [目录]` | includeTests=true 装载 → test* 函数发现（顶层 fn、无接收者、无参、`test` 前缀，声明序）→ 先执行「滤除顶层 fn main」的合并序列（evalChunk 等价，不跑 main）→ 逐测试以合成 `name()` 单语句调用切片走 `Interp.Run`（同一解释器实例共享状态）；`PASS/FAIL {name}` + `\nN 通过, M 失败` | 有失败 1 |
| `inspect symbols\|refs\|type\|effects\|callers\|callees\|impact [目录] [--json]` | Analysis/graph 最小实现 canonical 序（详见下） | 未知/缺子命令 2，查询失败 1 |
| `dump ast\|ir [目录]` | ast = 本波新增结构转储器（每节点一行、两空格缩进、紧凑 s 形表达式、无 span、JSON 转义字符串、浮点 "R" 最短往返）；ir = Lang.Ir 冻结 `IrDumper.DumpProgram`；两者同输入逐字节相等（调试视图非跨版本契约）；ir 需干净程序（`模块存在 N 条诊断，ir dump 需要干净程序（先 check）`） | 未知/缺子命令 2，脏程序 1 |
| `refactor rename\|organize-imports\|safe-remove [--symbol N] [--to NAME] [--apply] [--json] [目录]` | Lang.Refactor 三入口；默认 dry-run 报编辑计划（JSON：`applied:false/editCount/symbol/workspaceEdit{edits,preconditions}`；文本：`重构计划（dry-run，--apply 落盘）：N 处编辑`）；`--apply` 走 `ApplyAndVerify` 闭环（precondition sha256 → apply → format → 重新分析诊断码零回归）后落盘（未被编辑文件不重写）；safe-remove 有引用 → `removable:false + reason + refSites` exit 1 | 参数缺失/未知符号/不可删 1，未知/缺子命令 2 |

inspect 七子命令最小实现面：symbols = 非预载符号 SymbolId 升序（fn/method 类型走
`FnType`（函数签名登记表），无类型诚实 `unknown`；`{id}\t{kind}\t{name}\t{type}` 文本形）；
refs = `symbol_of_ref` 四分类 call/write/read/type（file+offset 序）；type =
`--file/--line/--column` 1 基 → 0 基码点 offset（列越界行尾兜底）→ 最内层包含表达式
（span 最短优先、同长取先序）；effects = Lang.Verify `EffectFacts.Solve`；
callers/callees/impact = `GraphFill.FromAnalysis` 图沿 calls/spawn 边闭包（`--depth` 限深、
缺省不限、不含起点、结果升序）。装载器口径：run/test 走第二波 `Loader.LoadModule`
（Lang.Loader + ParseImports 组合），**不复用** Lang.Interp.Tests 的 mini 装载器
（单一装载事实源；mini 装载器留在 Interp 测试域）。

#### 2. 查缺补漏（第二波遗留）

- **verify 四路差分补线**：CLI verify 的 `DefaultVerifyDriver` 注入 runAst/runIr
  （A4 双解释器）——differential 从「2 路 + ast/ir 跳过」升级为 **4 路全比**；
  dogfood 上九段全过、differential 通过（`--json` 9 段全 passed、零 skip）。
- **换行口径**：C# `Console.WriteLine` 在 Windows 产 `\r\n`，与 Go/Unix 证据的 `\n`
  逐字节对拍口径不符——run/test 首次对拍即红。修法：Program.cs 进程入口把
  stdout/stderr 包成 `NewLine="\n"` 的 StreamWriter（UTF-8 无 BOM；仅进程入口设置，
  测试直调 CliMain 的 Console 重定向不受影响）。
- **fmt 补「写文件失败」分支**（Go/rust 证据同文案 exit 1；第二波漏）。

#### 3. dogfood 全矩阵（dogfood/langalgo，66 文件 / langalgo_test 6 个 test 函数）

| 命令 | 结果 | 与 Go 参考（`impls/go` 本机构建 lang-go）差分 |
|---|---|---|
| `run .` | stdout `ok 1`，exit 0 | **逐字节一致（diff 空）** |
| `test .` | 6 PASS + `\n6 通过, 0 失败`，exit 0 | **逐字节一致（diff 空）** |
| `check .` / `check . --json` | `检查通过` / success:true 信封，exit 0 | 同绿（Go 无 --json 差异面） |
| `fmt`（语料副本） | `0 个文件已格式化`，幂等；副本与原目录 `diff -r` 空 | 同文案同数 |
| `build go` / `build js` | `out/main.go` / `out/main.mjs` 落盘 | — |
| 产物执行 | `go run out/main.go` 与 `node out/main.mjs` 均出 `ok 1` | 与解释器 run 输出一致 |
| `verify .` | 九段全过、`differential 通过`（ast/ir/go/js 四路），exit 0 | Go 为三路差分（无 AST oracle 路） |
| `inspect symbols` | 687 个符号（文本 + JSON 双形，canonical 序） | 形态对照（Go contract 信封不同构、id 分配不同，不逐字节比） |
| `inspect effects` | 186 个函数（PURE/IO/SPAWN/FFI） | 同上 |
| `inspect callees/callers/impact` | `callees sortRunSelfCheck` = 3 被调方；`callers main` = 0；`impact bubble` direct=[sortReport, sortRunSelfCheck] | 同上 |
| `inspect refs` | `refs bubble` = 4 处引用（call 分类） | — |
| `inspect type` | `sort/sort.lang-go:4:13` → `ident len unknown`（内层 len 标识符） | — |
| `dump ast` / `dump ir` | 3025 / 12949 行；各两次执行 `cmp` 逐字节相等 | 调试视图非契约，不比对 |

Go 参考已知差异沿用第二波登记：`lang-go run <路径>` 位置参数被忽略（参考侧须
`cd <dir> && lang-go run` 才真执行——对拍按此调用）；Go check 对未定义名宽纵。

#### 4. TDD 差分点（本波红→绿记录）

- 换行口径（见上）：run/test 首拍红（`\r\n` vs `\n`）→ 进程入口统一后逐字节绿。
- refs write 分类：lang-rs 最小实现只扫顶层 Assign（体内写点会降级 classified 为
  read）——C# 按 Analysis 事实全树收集补全，登记为**有意分歧**（引用分类正确性优先）。
- inspect symbols 的 fn 类型须走 `FnType`（`SymbolType` 不含函数签名登记），否则
  `fn(int): int` 全部塌成 `unknown`——测试抓出后修。
- struct/enum 符号无 TypeId 登记面 → 类型列诚实 `unknown`（与 lang-rs `symbol_type`
  None 同口径）。
- verify 接线后 wave-2 两条 `differential 跳过` 断言过时 → 按宿主工具链可用性分支
  断言（有 go+node → `differential 通过`；缺 → 如实跳过），两种环境态都在册。

#### 5. 测试数与验证矩阵（最终）

- Lang.Cli.Tests：**43/43 绿**（骨架 6 + check 6 + tooling 6 + run/test E2E 10 +
  inspect/dump/refactor E2E 15）。本波 Cli 域新增 25 条、更新 3 条。
- `dotnet test LangCs.slnx -c Release`：**18 项目 694 全绿**（exit 0）。
  Lang.Formatter.Tests **不在 sln**（既有缺口，交协调人），单独跑 **254/254 绿**
  → 全仓 19 测试项目 948 条全绿。
- `dotnet build LangCs.slnx -c Release -warnaserror`：0 警告 0 错误。
- `dotnet format --verify-no-changes`：Lang.Cli + Lang.Cli.Tests 过（Inspect.cs 两处
  对象初始化器单行违规，format 就地修复后复验，行为面测试复跑仍绿）。
- 真二进制 dogfood 冒烟：上表全矩阵 + 负例（`run <不存在目录>` → 装载失败 exit 1）。

## 进行中

（无）

## 下一步

1. ~~A4 Lang.Interp 落地后：CLI `run`/`test` 接线；verify ast/ir 两路补齐后
   differential 四路全比~~（第三波已交付）。
2. lowering 产物替换手构夹具跑同一 E2E（A3 Compiler 已就位，夹具仍保留作后端
   独立性锚）。
3. ~~CLI `inspect/dump/refactor` 接线~~（第三波已交付）。
4. jsonParse/jsonStringify 解析面负例差分（非法 JSON 报错文案）：宿主内建已接线、
   dogfood 四路一致；负例对拍另开小单。
5. `bind`/`fix`（Go 参考特有动词）未纳入 lang-cs 命令面（lang-rs 参考亦无）——
   是否对齐由协调人裁决。

## 阻塞

（无硬阻塞。观察三项，交协调人）

1. **Go 参考实现 `run <路径>` 位置参数被忽略**（`impls/go` 收工态实测：`lang-go run <dir>`
   静默输出空、exit 0；`cd <dir> && lang-go run`（默认 "."）才输出 `ok 1`）。
   影响跨实现差分的调用方式约定，不影响 lang-cs 功能面。
2. **Go 参考实现 `check` 对未定义名宽纵**（见负例差分）——conformance case 真值在
   case.yaml，建议协调人裁决 Go 参考是否属已知缺口。
3. **Lang.Formatter.Tests 不在 LangCs.slnx**（既有缺口，非本波引入）——全解
   `dotnet test` 少统计该项目（254 条），建议补 `dotnet sln add`。

## 已冻结接口登记

- 冻结：Lang.BackendGo.CodeGen::{TryGenerate(IrProgram, out string? source, out GenError? error)→bool, GenError(Message)}：Core IR → 单文件 `package main` Go 源码（prelude 内联），`go build <file>` 直接可用；同 IR 两次生成逐字节相等；失败时 source=null、error 带因（未知内建等内部错误）
- 冻结：Lang.BackendGo.Prelude.GoPrelude：→Go 产物运行时（全动态 any 域；错误消息逐字对齐解释器；langGoUnref/langGoFieldRef/langGoIndexRef 写链面、langGoPushArr 拼接、push 返回数组本身）
- 冻结：Lang.BackendJs.CodeGen::{TryGenerate…}：Core IR → 单文件 ESM（`node <file>.mjs` 直接可用）；确定性同上
- 冻结：Lang.BackendJs.Prelude.JsPrelude：→JS 产物运行时（int=BigInt、同步确定子集、映射键类型前缀 i:/s:/b: + #o: 原始键）
- 冻结（生成器命名约定，两后端同构）：用户函数 `lf{n}_{消毒名}`、struct 类型 `S{n}_{消毒名}`/JS `__langGoStruct` 标签、全局 `g{n}_{消毒名}`、enum 构造经 `langGoEnumVal(enumName, tag, ...)`；消毒 = 非字母数字/下划线码点 → `_`（按码点迭代，astral 不拆；字母/数字类保留），Go 字段名遇关键字/数字开头加 `X` 前缀
- 冻结：Lang.Cli.CliApp.Usage() 扩展（usage 只列已实现命令：version/help/check/fmt/build/verify）
- 冻结：Lang.Cli 命令面第二波 = `check [目录] [--json]`（诊断文本形 `{file}:第{line}行: [{code}] {message}`；有诊断 exit 1）+ `fmt [目录]`（就地规范化，`N 个文件已格式化`）+ `build [go|js] [目录]`（写 `{目录}/out/main.go|.mjs`；未知目标 exit 2）+ `verify [目录] [--json]`（九段文本 `X 通过/失败: …/跳过: …` + `验证通过/失败: X`；passed()=false → 1）
- 冻结：Lang.Cli 信封口径 = `{schemaVersion: "1"（字符串）, toolVersion, kind, …}`；check 负载 success+diagnostics[]（DiagnosticDto 直出）；verify 负载 passed+failedStage+stages[9]（stage/outcome/note，Skipped 显式在册）；error 负载配非零退出码；JSON 输出两空格缩进 + 非 ASCII 码点字面

- 冻结：Lang.Cli 命令面第三波（全命令收官）= `run [目录]`（分析诊断 → check JSON 信封 exit 1；解释器 stdout 直出；错误 `错误: {msg}` stderr）+ `test [目录]`（test* 发现 = 顶层 fn/无接收者/无参/test 前缀、声明序；`PASS/FAIL {name}` + `\nN 通过, M 失败`；有失败 exit 1；无测试 `未发现 *_test.lang-go` exit 0）+ `inspect symbols|refs|type|effects|callers|callees|impact [目录] [--json]` + `dump ast|ir [目录]` + `refactor rename|organize-imports|safe-remove [--symbol N] [--to NAME] [--apply] [--json] [目录]`
- 冻结：Lang.Cli.CliApp.Usage() 第三波扩展 = usage 列全命令（version/help/run/test/check/fmt/build/verify/inspect/dump/refactor）
- 冻结：Lang.Cli 进程输出口径 = stdout/stderr 换行恒 `\n`（Program.cs 进程入口统一；编码 UTF-8 无 BOM）——跨实现对拍逐字节口径的硬前提
- 冻结：Lang.Cli inspect 信封 = EnvelopeBase 三头字段（schemaVersion/toolVersion/kind，JsonPropertyOrder 恒在前）+ 各子命令负载（symbols[]/references[]/nodes[]/effects[]/callers[]/callees[]/impact{symbol,direct,transitive,note}）；缺席字段省略（rust serde skip-None 同构，与 check/verify 的 null-在册口径分流）
- 冻结：Lang.Cli refactor 信封 = dry-run `{…, applied:false, editCount, symbol?, workspaceEdit}`、apply 成功 `{…, applied:true, editCount, writtenFiles}`、safe-remove 不可删 `{…, removable:false, reason, refSites, symbol}`（exit 1）
- 冻结：Lang.Cli dump.ast 文本形 = 结构转储器（每节点一行、两空格缩进、无 span、s 形表达式、JSON 转义字符串、浮点 "R"）——同输入逐字节相等；调试视图非跨版本契约
- 冻结：Lang.Cli verify 差分路 = DefaultVerifyDriver 注入 runAst/runIr（Lang.Interp 双解释器），differential 四路全比（ast/ir/go/js；工具链缺失如实 Skipped）

（以上第三波新增；以下为第二波与首批登记）

- 冻结：Lang.BackendGo.FmtNode（抽象 record 封闭层级，嵌套子型 Unit/Int(long)/
  Float(double)/Bool/Str/Fn(string?)/Range(long,long)/Ptr/Arr/Map(插入序列表)/
  Struct(声明序)/EnumValue(Tag+载荷)/Option(null=None)/Ok/Err/Error(Msg)）
- 冻结：Lang.BackendGo.Fmt::{FmtValue(FmtNode, bool top)→string, FmtArgs(IReadOnlyList<FmtNode>)→string, FmtFloat(double)→string, QuoteGo(string)→string}：float=Go 'g' -1 镜像；quote=strconv.Quote 镜像
- 冻结：Lang.BackendGo.Json::{TryStringify(FmtNode, out string, out JsonError?)→bool, JsonString(string)→string, TryJsonFloat(double, out string, out JsonError?)→bool, JsonError(Message)}：RUNTIME-JSON-002 契约面（错误消息含 "JSON 对象键须为 string，得到 {type}" / "JSON 序列化失败: json: unsupported value: {name}" / "{tag} 不可 JSON 序列化"）
- 冻结：Lang.BackendJs.FmtNode / Fmt::{FmtValue, FmtArgs, FmtFloat, QuoteJson}：形状与 Go 同构；嵌套字符串走 JSON.stringify 语义
- 冻结：Lang.BackendJs.Json::{TryStringify, JsonString, TryJsonFloat, JsonError}：与 Lang.BackendGo.Json 同文（硬契约）
- 冻结：Lang.Loader::{ImportRef(Path, Line:int), LoadedFile(RelPath, Source), LoadedFileInfo(Path, Imports, Order)（命名避开 System.IO.FileInfo）, LoadedProgram(Files, Infos, TestFiles), LoadError(Message), ImportsOf 委托(string relPath, string source)→IReadOnlyList<ImportRef>}：RelPath 一律 `/` 分隔；LoadError 不是 Diagnostic（stderr + 非零退出）
- 冻结：Lang.Loader.ModuleLoader.TryLoadProgram(string dir, bool includeTests, ImportsOf importsOf, out LoadedProgram?, out LoadError?)→bool：受控错误不抛异常；importsOf 的解析器接线真实现（第二波实际落点 = CLI 内部 ParseImports，见完成块二.3；Loader 本体保持冻结）
- 冻结：Lang.Cli.CliApp::{Version="0.1.0", VersionLine()（恰为 "lang-cs 0.1.0"）, Usage()→string, CliMain(IReadOnlyList<string>)→int（返回进程退出码；version/help 0，未知命令/无参 usage→stderr+2）}

## SPEC-GAP

（无新增。沿用 lang-rs spec-gaps.md 的 SPEC-GAP-RS-001/-002/-003 口径：
嵌套字符串转义双后端分歧按各自目标语义分别实现；jsonStringify 的 -0.0→`0`
取解释器语义（Go json 出 `-0`，本机对拍确认分歧存在，按已裁决契约落 `0`）。）
