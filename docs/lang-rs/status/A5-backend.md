# A5-backend 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

### 2026-09-25 集成轮修复（B2/B3 + B1 修复牵出的写链缺口，协调人授权集成工程师执行）

- **B2（ArrayPush 拼接语义）**：真值 = case.yaml（`xs += [2]` → `[1, 2]`）+
  specs/values.md（数组引用语义）+ IrMachine（rhs 须 Slice、逐元素拼接）。修复：
  两后端 `Inst::ArrayPush` 发射改调新增 prelude 助手 `langGoPushArr(arr, rhs)`
  （原位逐元素追加 rhs 全部元素、逐元素 `langGoCopy`，错误消息逐字对齐解释器
  「数组 += 需要数组，得到 {type}」）；`push` 内建（单元素追加，langGoPush）不动。
  两处不同时改语义，单点对齐 spec 真值。
- **B3（RangeNew int64 cast）**：Go `Inst::RangeNew` 发射补 `langGoInt(...)` 断言
  （对齐 SwitchInt 等整型操作数的处理），`langGoRange{Lo: langGoInt(lo),
  Hi: langGoInt(hi)}`；`range-literal` go build 失败消除，js 动态域不受影响。
- **B1 修复牵出的写链缺口（4 case go+js 8 格，B1 修复前被同一批 case 的
  运行时崩溃/错值掩盖）**：修正后的降形把带 Place 溯源的操作数直压给链写——
  双后端 prelude 缺两块能力：①装箱 Ref 解引用（`r.v = 2` 报「引用 没有字段 v」）；
  ②写链中间组件的 Ref 读形（`langGoField`/`langGoIndex` 读出皆副本 VALUE-ELEM-009，
  `xs[1].v=20`/`b.inner.v=100` 写在副本上）。修复：prelude 新增 `langGoUnref`
  （有界解引用，镜像 IrMachine deref_value 位点）与 `langGoFieldRef`/
  `langGoIndexRef`（不落副本的读形）；`langGoField/langGoSetField/langGoIndex/
  langGoSetIndex/langGoPushArr` 入口补 unref（机器在 FieldGet/IndexGet/ArrayPush
  位点 deref obj 的镜像）。codegen 新增 `mark_ref_reads`：按 lowering 链形
  （`[LocalGet|GlobalGet, (FieldGet | LocalGet+IndexGet)*] → (LocalGet(val),)
  FieldSet|IndexSet|ArrayPush`）回溯标记块内作为写链地址组件的 FieldGet/IndexGet
  下标，标记者发射 Ref 读形，其余保持读出皆副本——与机器 Place 溯源写穿同语义，
  回溯不匹配即保守回退（hand-built IR 安全）。
- **测试面同步**：两后端 E2E 手构夹具 b2 的 `ArrayPush` 原压单元素 int（锁定
  违反机器冻结语义的旧行为），按冻结拼接语义改为压 `[2]` 数组再 ArrayPush
  （期望 stdout 不变）。
- **验证**：`cargo test -p lang-backend-go -p lang-backend-js` = 52 绿（go 24+3
  E2E、js 22+3 E2E）；conformance go/js 路 116p/4f → **120p/0f**（4 case =
  struct-in-collection、nested-struct-copy、reference-read-write、
  struct-nested-copy-deep）；dogfood/langalgo `verify` 九段全过（修复前
  execute 段因 B1 失败）；`cargo clippy --workspace --all-targets -- -D warnings`
  干净、`cargo fmt --all` 干净。

### 1. 输出格式化规则层（2026-09-25，首轮交付）

`lang-backend-go` / `lang-backend-js` 各自的 `fmt` + `json` 纯函数模块。抽象值树
`Node`（IR 冻结前的 fmt 自有形状，两 crate 同构、转义规则各异）+ 打印/序列化双面。
测试 46 条（go 24 + js 22），期望值全部手算，证据只读对照不照抄。

**打印面（RUNTIME-OUTPUT-003/004）规则清单（已冻结）**：

| 规则 | 规范形 | 证据来源 |
|---|---|---|
| float 文本 | Go `strconv.FormatFloat('g',-1,64)` 镜像：最短舍入数字；指数 < -4 或 >= 6 走 `d.ddde±XX`（指数至少两位恒带符号），否则定点无尾零；`NaN`/`+Inf`/`-Inf`/`-0` | specs/runtime.md RUNTIME-OUTPUT-004 安全区 [1e-4,1e6) 端点行为 + TS `interp/value.ts` goFormatFloat + jsprelude langGoFloatStr（带 FIX 注释，弃用 String(v)）；手算：1.0→`1`、0.1→`0.1`、1e21→`1e+21`、1e6→`1e+06`、1e-4→`0.0001`、-0→`-0` |
| int64 | 全域十进制（含 i64::MIN = -9223372036854775808）；JSON 面同样直出不经浮点（2^53+1 精度用例锁定） | TS interp `fmtVal`/`marshalJSON` jsonInt 分支 + jsprelude BigInt FIX 注释 |
| 字符串顶层/嵌套 | 顶层原样；嵌套带引号。→Go 用 strconv.Quote 语义（`\a\b\f\n\r\t\v`、`\xNN`、非打印→`\uNNNN`/`\UNNNNNNNN`、U+2028/9 转义、打印 Unicode 直出）；→JS 用 JSON.stringify 语义（仅 `"\` + <0x20，U+2028/DEL/NBSP 直出）。分歧登记 SPEC-GAP-RS-001 | TS `value.ts` goQuote（strconv.Quote 镜像）vs `jsprelude.ts` langGoFmt 字符串分支 |
| map 打印 | `{k: v, ...}` 按插入序（Vec 承载），键嵌套形 | goprelude/jsprelude langGoString + interp fmtVal |
| struct/enum/array | `Name{f: v, ...}` 声明序 / `Tag(p1, p2)`·`Tag` / `[a, b]` | RUNTIME-OUTPUT-003 + interp fmtVal |
| Some/None/Ok/Err/error | `Some(v)`/`None`/`Ok(v)`/`Err(e)`/`error{msg: "<引号形>"}` | 同上 |
| println | 实参顶层形空格分隔（`fmt_args`），换行由调用点 | RUNTIME-OUTPUT-003 |

`quote_go` 的 `unicode.IsPrint` 用 std 可行近似：Zs/Zl/Zp、Cf、Co、非字符硬编码，
未分配码点按打印处理（conformance 语料不使用，分歧不可达）——代码注释已声明。

**JSON 面（RUNTIME-JSON-002 硬契约，两后端同文）**：映射键码点序输出（Rust str 序
= UTF-8 字节序 = 码点序，非 JS UTF-16 序）；None/unit → null；HTML 转义开启
（`< > &` → `\u003c/\u003e/\u0026`）；float 按 Go encoding/json：abs∈[1e-6,1e21) 定点
否则 `e` 形负指数去前导零（1e8→`100000000`、1e-7→`1e-7`、-0.0→`0`）；NaN/Inf →
`JSON 序列化失败: json: unsupported value: NaN/+Inf/-Inf`；非字符串键 →
`JSON 对象键须为 string，得到 int`。以 TS interp（builtin.ts marshalJSON +
value.ts goJSONFloat/goJSONString）为规范源；jsprelude 浮点分歧登记 SPEC-GAP-RS-002。

### 2. lang-loader 模块图（2026-09-25，首轮交付）

`load_program(dir, include_tests, imports_of)` 覆盖 MODULE-ROOT-001..TEST-007：
目录即包（两阶段：目录内非测试 .lang-go 文件名序全读并标 done，再按序展开 import
后落文件自身——与参考实现的预读去重语义逐点一致）；`./x` → `x/` 目录或 `x.lang-go`
（扩展名由解析补，import 原文不带扩展）；越根 → `第N行: import <p> 逃出模块根目录`；
非 `./` → `第N行: import 仅支持 "./相对路径"（得到 "<p>"）`；菱形去重（done 集，
含同目录去扩展名键）；真环 → `第N行: 循环 import <p>`（in_progress 链）；`*_test.lang-go`
仅 include_tests=true 追加且 import 同样展开。词法规范化（lexically_clean）等价
Go filepath.Clean，不做符号链接解析。测试 16 条（临时目录夹具 + 注入的 import
提取桩闭包）。证据：`impls/go/internal/load/load.go`、`impls/typescript/src/loader/loader.ts`。

**测试发现并如实锁定的参考实现语义**（非本实现发明）：
- 同目录文件互相 import 是去重跳过，合并序保持文件名序（目录预读所致）；
- 根预读 `x.lang-go` 的去扩展名键会遮蔽同名目录包 `x/`（撞键跳过）；
- import 原文带扩展名会产出 `x.lang-go.lang-go` 的读文件错误（参考实现同样如此）。

### 3. lang-cli L0 骨架（2026-09-25）

`src/main.rs` + `src/lib.rs`，无 clap：`--version`/`-V` → `lang-rs 0.1.0`（退出码 0，
已 cargo run 实测）；未知命令 → usage 到 stderr + 退出码 2。测试 2 条。

### 4. 验证矩阵

`cargo test -p lang-backend-go -p lang-backend-js -p lang-loader -p lang-cli` 全绿
（64 条：24+22+16+2）；`cargo clippy -p <四 crate> -- -D warnings` 干净；
`cargo fmt --check` 干净。四 crate 均 `#![forbid(unsafe_code)]`。

### 5. Core IR → Go/JS 源码生成（2026-09-25，第二波交付）

`lang-backend-go::codegen` / `lang-backend-js::codegen` + 各自 `prelude` 模块。
输入 = 冻结的 `lang_ir::IrProgram`（33 Inst + 6 Terminator），生成物单文件自包含
（Go：`package main`，`go build <file>` 直接可用；JS：ESM，`node <file>.mjs` 直接可用）。

**发射架构（两后端同构，控制流承接不同）**：

| 决策点 | 形状 | 依据 |
|---|---|---|
| 值域 | 全动态 `any`/动态分派（IR 无类型表，`type_id` 不可解；Option/Result=普通 enum 值 Tag Some/None/Ok/Err，error=prelude 类型经 `err()` 内建） | IR 形状 + RESULT-PRELUDE-001 |
| 调用约定 | 每函数 `lf{n}_{名}(as ...any) any` 统一变参；捕获/参数槽=`as[i]`，局部=`l{i}` 预声明；闭包调用 `langGoCallClosure` 把 Env 前置落地 | VALUE-CAPTURE-004 捕获槽语义 |
| 控制流 | Go：label `L{k}`+花括号块+goto（只给被引用块发 label——Go 视未用 label 为错误）；JS：`let __pc`+`while(true) switch` 状态机，`break` 即跳转 | 块入口栈深 0 不变量 |
| 操作数栈 | 符号串栈内联（IR 无 Dup，每值至多消费一次）；Try/IterNext 物化临时 `_t{n}` | 指令栈效应表 |
| 值语义 | 用户 struct 以指针承载 + 绑定/传参/返回/容器写入点显式 `langGoCopy`（深拷贝 struct、共享容器/引用/枚举/chan）；JS 同点拷贝 | VALUE-COPY-001/002/009 |
| 并发 | Go：原生 `chan any`(1024)+真 goroutine，spawn 实参快照进 `[]any` 字面量（EVAL-GO-007），含 Spawn 函数每个 Return 前 + main 尾 `langGoWaitAll`；JS：同步确定子集（spawn=直接调用+实参拷贝，chan=数组队列，recv 空=Err"channel 空"） | TASK-SPAWN-002/CHAN-MAP-005 |
| for-in | `IterNew` 快照迭代器（数组索引序/映射插入序/字符串码点序/区间半开），元素读出拷贝 | CTRL-FORIN-003 |
| `?` | `Try` 发射为临时+Tag 检查：Err 存 err 槽跳 fail 块（fail 块合成 Err 返回） | RESULT-TRY-003 |
| 常量 | int→`int64(v)`/`vn`；float→最短 `{:e}` 科学计数（Go `float64(1.5e0)` 形/JS 直出），NaN/±Inf/−0 走 math/`NaN`/`Infinity`/`-0`；字符串 Go 用 strconv.Quote 语义（复用 fmt::quote_go，输出即合法 Go 源）、JS 用 JSON 转义面 | RUNTIME-OUTPUT-004 |

**prelude 行为面**（错误消息逐字对齐解释器 interp/*.ts，供差分）：
索引越界/映射无键/整数除零/浮点除零/移位位数/条件须为 bool/==比较/match 无匹配分支/
channel 空/缓冲满 1024/len·push·keys·values·has·err·bytes·char·int·float 类型拒斥；
int 算术回绕（Go 原生 int64、JS `langGoWrap64`）、移位位数 0..63 检查；
JSON：`encoding/json.Marshal`（Go）/手写 marshal（JS）同契约——键码点序、HTML 转义、
int64 全精度、float encoding/json 形、None/unit→null、非字符串键与不可序列化值拒斥
（只有 Tag None 特判，镜像解释器 valueToJSON）。

**E2E 矩阵**（`tests/e2e.rs`，两后端同一夹具同一期望 stdout，23 行逐字节断言全绿）：
算术 / 字符串连接+str / 直接调用 / 闭包捕获计数器（3 次调用写穿=3）/ 引用别名写穿（42）/
数组-映射引用共享 / struct 拷贝（q 改 p 不动）/ map 插入序打印（b,a,c）/ chan FIFO /
Option match / Result+`?` 传播（Ok 50、Err.msg neg）/ spawn 实参快照（go 后改源=1）/
for-in 区间求和 / SwitchInt / 全局 init+读改写 / 一元负号非 / 字符串切片与索引 / 数组切片。
另含：夹具过 `lang_ir::validate`、同 IR 两次生成逐字节相等（确定性）、
发射面形状断言、外部工具存在性守卫（go 经 LANG_RS_GO/PATH/已知路径探测，node 同理，
不在则 skip 打印原因）、临时目录 Drop 守卫清理（LANG_RS_KEEP 可保留排查）。

**E2E 揪出的真 bug**（红→绿证据）：`has_ret=false` 的调用（println/void 闭包调用）
最初被当表达式压栈，语句整体丢失（stdout 为空）——修正为按调用点 has_ret 分
语句/表达式两条路后全绿。这验证了「栈式 IR 生成器必须严格按栈效应表发射」。

**测试数**：52 条（go 24+3，js 22+3；fmt/json 首波 46 + 第二波 6）。

### 6. 验证矩阵（第二波）

`cargo test -p lang-backend-go -p lang-backend-js` 全绿（52 条）；
`cargo clippy -p lang-backend-go -p lang-backend-js --all-targets -- -D warnings` 干净；
`cargo fmt --check` 干净；`cargo check --workspace` 不破坏兄弟 crate。
E2E 实测：go build 一次通过后产物执行 stdout 与手算期望逐字节一致；
node 同夹具输出与 Go 完全一致（跨后端差分信号）。

### 7. lang-cli 全命令接线（2026-09-25，第四波交付：骨架 → 全命令 CLI）

**命令矩阵（无 clap，手写 arg 分派，`lib::cli_main` 单入口，退出码 = 进程码）**：

| 命令 | 行为 | 退出码 |
|---|---|---|
| `run [dir]` | loader 装载 → parse → analyze → 诊断非空打印 check JSON 信封即停 → AST 参考解释器执行（stdout 直出）；装载器错误走 stderr（非 Diagnostic） | 有诊断/错误 1 |
| `check [dir] [--json]` | 诊断聚合（解析 E1001 + 语义）；text：`{file}:第{line}行: [{code}] {message}`（TS formatDiag 同构 + 文件前缀消歧）；--json：`{schemaVersion:"1", toolVersion, kind:"check", success, diagnostics[]}`（DTO 字段序即 schema 序） | 有诊断 1 |
| `test [dir]` | 装载 include_tests → 发现规则 = TS loader.ts（`*_test.lang-go` 顶层 fn、无接收者、无参、`test` 前缀）→ evalChunk 等价（顶层语句执行、main 过滤不跑）→ 逐测试以合成单语句调用切片走 `Interp::run`（同一 interp 状态共享、失败隔离逐测继续）→ `PASS/FAIL` + `N 通过, M 失败` | fail>0 → 1 |
| `build [go\|js] [dir]` | 检查 → `lang_ir::compile` → codegen → 写 `{dir}/out/main.go` / `out/main.mjs`（Go 证据同文案提示） | 检查/生成失败 1 |
| `fmt [dir]` | 目录内（不递归）`.lang-go` 就地 `format_source`，打印改动文件名 + 计数 | 格式化失败 1 |
| `verify [dir] [--json]` | `lang_verify::run_pipeline` + `ImportStripExec` 薄包装（见绕行登记）；text 逐段 + `验证通过/失败`；--json：kind="verify"，passed/failedStage/stages[9]（Skipped 显式在册） | passed()=false → 1 |
| `inspect symbols\|refs\|type\|effects\|callers\|callees\|impact [dir] [--json]` | 最小实现（见下） | 查询失败 1 / 用法 2 |
| `dump ast\|hir\|ir [dir]` | ast = 合并 AST `{:#?}`；hir = `HirBuilder::lower_slice` 产物 `{:#?}`；ir = 冻结 `lang_ir::dump_program`。同输入两次逐字节相等（E2E 锁定）；hir/ir 需干净程序 | 需干净 1 |
| `refactor rename\|organize-imports\|safe-remove [--symbol N] [--to NAME] [--apply] [--json] [dir]` | lang-refactor 冻结入口 + precondition 补齐（sha256，按文件去重，入口已填的只补缺）→ dry-run 打印计划/信封；--apply 走 `apply_and_verify` 闭环后落盘（未涉及文件不重写） | refusal 1 / 用法 2 |
| `version` / `--version` / `help` | `lang-rs 0.1.0` / 用法文本 | 未知命令 2 |

**inspect 最小实现面**（契约 §2 的本波子集）：symbols = 非预载符号全列
（SymbolId 升序 = canonical 序，symbolId/name/kind 英文契约串/definition SpanDto/type/arity/module）；
refs = `lang_refactor::walk::walk` + `symbol_of_ref`（read/write/call/type 四类，
pattern 类 facts 层未开放不输出；file+offset 序）；type = `--file/--line/--column` →
最内层包含该点的表达式（kind/type/span）；effects = `lang_verify::effects::solve_effects`
（AST 事实 → CallGraph 不动点）；callers/callees = `lang_graph::fill::from_analysis`
图上沿 calls/spawn 边的闭包（`--depth` 限深、缺省不限）；impact = direct 入边 + 传递闭包
+ spec 明文 note。错误一律 error 信封。

**信封口径**：`schemaVersion` 用字符串 `"1"`（specs/agent-protocol.md §1 明文 +
Go/TS 证据同文；任务书 `{"schemaVersion":1,...}` 按证据口径落为 `"1"`）。

**装载统一走 lang-loader**：本波在 loader 补公共 API `parse_imports(rel, source)
-> Vec<ImportRef>`（解析器接线真实现：lang-syntax parse 抽顶层 Import，
解析失败返回空列表——与 lang-verify/lang-refactor 内联闭包逐字一致，单一事实源收拢）。
登记理由：任务书允许「上提到 lang-loader 公共 API 或 CLI 内部模块」二选一，
选公共 API 因 lang-refactor/lang-verify 已各有一份私有同形闭包，本波不改动它们的
crate，但 CLI 侧用公共版收口。CLI 装载模块（`load.rs`）：逐文件 parse（SourceId
按装载序，与 RefactorInput::from_files 同口径）→ 合并语句流 **剥除 import 语句**
（loader 文件级合并已完成依赖先序；Go expand 在语句位替换后同样不保留 import 语句，
解释器对 Import 拒执行）→ `analyze_stmts(merged, SourceId(0))`；任一文件解析失败
→ 只报解析诊断（E1001）不进语义分析（避免缺文件连锁噪声）。

**test 命令的绕行登记**：lang-interp 冻结面无 callByName（A4 冻结）——test 运行器
先执行「过滤顶层 `fn main`」的合并序列（等价 TS evalChunk：顶层语句执行、main 不跑），
再对每个测试合成 `ExprStmt(Call(Ident(test)))` 单语句切片走 `Interp::run`（解释器
只消费 has_errors 门，gate 复用同一份干净 Analysis；同一 interp 上状态共享 = 语义
等价 callByName）。失败隔离：逐测 try 边界 = 逐次 run 调用。

**verify 命令的绕行登记**：lang-verify 管线合并语句流保留 import 语句 → AST 解释器
拒执行（langalgo 实测命中）。`ImportStripExec` 薄包装（`ExecDriver` 是 lang-verify
文档化的宿主注入点）在 run_ast 前剥除 import，其余四段直通 DefaultExec。
lang-verify 本体不动（A6 领地；管线对含 import 模块的 Execute 段缺陷建议 A6 参考
本包装修 input/merge 面）。

**dogfood 实测矩阵（dogfood/langalgo，66 文件 / 1953 行 / 6 个 test 函数）**：

| 命令 | 结果 | 与 Go 参考差分 |
|---|---|---|
| `run` | `ok 1`，exit 0 | Go `lang-go run` 输出逐字节一致 |
| `check` | 检查通过，exit 0 | — |
| `test` | 6/6 PASS + `6 通过, 0 失败`，exit 0 | Go `lang-go test` 输出逐字节一致 |
| `fmt` | `0 个文件已格式化`（语料已规范化，幂等；副本上先验证再动真目录） | — |
| `verify` | 前七段通过；**execute 失败：`ir: IR 解释器: int 不可索引赋值`**（A3 lowering 缺陷，见阻塞）；differential 短路 Skipped | — |
| `inspect effects` | 186 函数效果面（main=IO，自检函数 PURE） | — |
| `inspect symbols` | 687 个非预载符号，SymbolId 升序 | — |
| `dump ast/hir/ir` | 三种形态同输入两次逐字节相等 | — |

**E2E（`tests/e2e.rs`，29 条，真二进制 CARGO_BIN_EXE + 临时目录夹具）**：run
（stdout 直出/诊断 JSON/装载错误 stderr/多文件 import/菱形去重全局共享）、check
（text+json 信封 shape+E1001）、test（PASS/FAIL 汇总+main 不执行+无测试提示+全过退出码）、
fmt（重写+幂等）、build（go/js 产物+两次生成逐字节相等+脏模块拒绝）、verify
（九段全过+json 信封+失败段短路）、dump（ast/hir/ir 三形态确定性+ir 冻结文本头）、
inspect（symbols canonical 序/refs 计数+call 分类/effects IO·PURE/callers·callees
传递闭包/impact direct vs transitive/type 节点类型/错误信封）、refactor（rename
dry-run 不落盘→apply 落盘→check 干净；冲突/未知/struct 拒绝；organize 排序去重落盘；
safe-remove 引用拒绝+未引用删除）。红→绿证据：先写的断言抓出 run 装载错误信封出口
错面（stdout→改 stderr）、`offset_at` 全局 offset 当行内列的 bug、refactor --apply
路径漏补 precondition 三处本体缺陷，修正后全绿。

**E2E + dogfood 揪出的下层真 bug（本波修复 = 自己领地）**：
1. **lang-backend-go/js prelude：`langGoPush` 是 void 形**——参考解释器（TS builtin.ts /
   Go builtin.go）与 lang-runtime 的 `push` 都返回数组本身，IR `CallBuiltin{has_ret:1}`
   把调用当值消费：→Go 产物 `_ = langGoPush(...)` 直接编译失败（go build 报
   "(no value) used as value"，本机 go 1.27 实测抓到）；→JS 静默 undefined。修法：
   两 prelude 的 push 返回首参（对齐三方事实源）；backend 52 测试全绿不动。

## 进行中

（无）

## 下一步

1. 与 A3 对接：HIR→IR lowering 落地后，用 lowering 产物替换手构夹具跑同一 E2E。
2. `SwitchEnum`/`Try` 的 enum 名信息当前只进 typeName（错误消息）；若后续
   conformance 需要跨 enum 同名 case 的更细错误定位，再考虑在值里带 enum 名。
3. 外部宿主函数（`extern_host`）：当前发射为「执行到即 panic(未链接)」的桩；
   等 A4/内建宿主面定义后接真实实现。
4. 解析器接线第二波：loader 的 `imports_of` 闭包由 lang-syntax 提供；
   语句级 splice 与 test 前缀无参函数名提取在语句流可用后补。
5. jsonParse 解析面属 A4（lang-runtime）；本层锁 jsonStringify 发射面
   （prelude 已含 parse 形，等 A4 差分对齐后复核）。

## 阻塞

### 阻塞登记（交协调人）：A3 lowering 普通赋值操作数序错误（P0，IR 路全断）

**位置**：`impls/rust/crates/lang-ir/src/lower.rs` 的 `HirExpr::Field` 与
`HirExpr::Index` 两个 `a.op == AssignOp::Assign` 早退路径（约 757-763 行 / 799-805 行）。

**事实**：A4 冻结契约（`lang-interp/src/ir.rs` 实现与手构测试 `tests/ir_interp.rs`
一致）：`IndexSet` 压栈序 = obj, idx, val（弹序 val→idx→obj）；`FieldSet` 压栈序 =
obj, val。lowering 实际发射：

- `IndexSet`：`expr(rhs)`（值直接压栈）→ `LocalGet(obj_t)` → `LocalGet(idx_t)` →
  即压栈序 = **val, obj, idx**（解释器读到 obj=val、idx=obj，报 `int 不可索引赋值`）；
- `FieldSet`：`expr(rhs)` → `LocalGet(obj_t)` → 压栈序 = **val, obj**（obj 位是值
  无 Place 溯源，报 `FieldSet 目标无地址`）。

复合赋值路径（`new_t` 落槽后按 [obj(, idx), val] 压栈）是正确的——只有普通赋值两条
早退路径错。A3 自有测试只断言指令**种类覆盖**不断言操作数序，故未拦住。

**最小复现**（`cargo run -p lang-cli -- verify <dir>`；go 1.27 + node 在场时
execute 段 ir 路即红）：

```
fn main() {
    let a = [1, 2, 3]
    a[1] = 20            // ir: int 不可索引赋值
}
```
```
struct P { x: int }
fn main() {
    var p = P{x: 1}
    p.x = 7              // ir: FieldSet 目标无地址
}
```
`dump ir` 可见 `const.i64 20 / local.get 4 / local.get 5 / index.set`（val 在栈底）。

**影响面**（dogfood langalgo 命中）：verify 的 ir 执行/差分段（langalgo 实测
execute 失败）；**build 语义正确性**——codegen 按正确弹序消费同一 IR，生成
`langGoSetIndex(val, obj, idx)`（实参错位，产物运行时类型错/结果错）；AST 路
（run/test/check/fmt）与 inspect/refactor 不受影响。

**建议修法**（A3 领地，本波不动）：两条早退路径把 rhs 先落临时槽再按冻结序压栈
——`LocalGet(obj_t); LocalGet(idx_t); LocalGet(val_t); IndexSet` /
`LocalGet(obj_t); LocalGet(val_t); FieldSet`（顺带消除 rhs 多块形态——match/短路/`?`
——直入多操作数序列的隐患，与「多操作数一律先落临时槽」铁律一致）。A3 测试无需改
（只锁种类覆盖）。

（历史观察：①「33 条指令」逐变体实为 37 条待核对口径；② SG-1/SG-2/SG-3 见 A3 状态文件。）

### 观察项（不阻塞）

1. dump/inspect 大输出在 Windows 管道下游提前关闭（`| head`）时，Rust `println!`
   因 broken pipe panic（os error 109/232）；Go 静默。属 CLI 人体工学，不影响
   测试与契约（测试捕获完整输出）。如需修：dump/inspect 输出改 `writeln!` + 写失败即停。
2. `fmt` 与 Go 证据一致为**不递归**（只处理目录顶层 `.lang-go`）；langalgo 子目录
   不在 fmt 面内（build/verify 等不受影响）。
3. inspect refs 的 `pattern` 类引用 facts 层未开放（A2 检查器未记录），暂不输出；
   inspect type 只覆盖表达式节点（声明名/模式不在内）。

## 已冻结接口登记

- 冻结：lang_backend_go::fmt::{Node, fmt_value(&Node, bool)->String, fmt_args(&[Node])->String, fmt_float(f64)->String, quote_go(&str)->String}：float=Go 'g' -1 镜像；quote=strconv.Quote 镜像
- 冻结：lang_backend_go::json::{stringify(&Node)->Result<String, JsonError>, json_string(&str)->String, json_float(f64)->Result<String, JsonError>, JsonError(String)}：RUNTIME-JSON-002 契约面（错误消息含 "JSON 对象键须为 string，得到 {type}" / "JSON 序列化失败: json: unsupported value: {name}"）
- 冻结：lang_backend_js::fmt::{Node, fmt_value, fmt_args, fmt_float, quote_json}：形状与 go 同构；嵌套字符串走 JSON.stringify 语义
- 冻结：lang_backend_js::json::{stringify, json_string, json_float, JsonError}：与 lang_backend_go::json 同文（硬契约）
- 冻结：lang_loader::load_program(dir: &Path, include_tests: bool, imports_of: &mut dyn FnMut(&str, &str) -> Vec<ImportRef>) -> Result<LoadedProgram, LoadError>：闭包入参（根相对路径, 源码）→ import 列表；解析器接线第二波由 lang-syntax 提供实现
- 冻结：lang_loader::{ImportRef{path: String, line: u32}, LoadedFile{rel_path: String, source: String}, FileInfo{path, imports: Vec<String>, order: usize}, LoadedProgram{files: Vec<LoadedFile>, infos: Vec<FileInfo>, test_files: Vec<String>}, LoadError{message: String}}：rel_path 一律 `/` 分隔；LoadError 不是 Diagnostic（stderr + 非零退出）
- 冻结：lang_backend_go::codegen::{generate(&IrProgram) -> Result<String, GenError>, GenError(String)}：Core IR → 单文件 `package main` Go 源码（prelude 内联），`go build <file>` 直接可用；同 IR 两次生成逐字节相等
- 冻结：lang_backend_go::prelude::GO_PRELUDE: &str：→Go 产物运行时（全动态 any 域；错误消息逐字对齐解释器）
- 冻结：lang_backend_js::codegen::{generate(&IrProgram) -> Result<String, GenError>, GenError(String)}：Core IR → 单文件 ESM（prelude 内联），`node <file>.mjs` 直接可用；确定性同上
- 冻结：lang_backend_js::prelude::JS_PRELUDE: &str：→JS 产物运行时（int=BigInt、同步确定子集、映射键类型前缀 i:/s:/b: + #o: 原始键）
- 冻结（生成器命名约定）：用户函数 `lf{n}_{消毒名}`、struct 类型 `S{n}_{消毒名}`（打印/字段访问用原名）、全局 `g{n}_{消毒名}`、enum 构造经 prelude `langGoEnumVal(enumName, tag, ...)`；消毒 = 非字母数字/下划线 → `_`，字段名遇 Go 关键字/数字开头加 `X` 前缀
- 冻结：lang_cli::{VERSION: &str, version_line()->String（恰为 "lang-rs 0.1.0"）, usage()->String}；bin 目标 `lang-cli`（main.rs：--version/-V → 版本行，其余 → usage+退出码 2）

### 第四波冻结追加（2026-09-25）

- 冻结：lang_loader::parse_imports(rel: &str, source: &str) -> Vec<ImportRef>：
  imports_of 闭包的解析器接线真实现（lang-syntax parse 抽顶层 Import，
  解析失败返回空列表）；直接可作 `load_program(dir, tests, &mut parse_imports)` 传参
- 冻结：lang_cli::cli_main(args: &[String]) -> i32：全命令单入口（返回进程退出码）；
  未知命令/无参 → usage+2；命令面 = run/check/test/fmt/build/verify/inspect/dump/refactor/version/help
- 冻结：lang-cli 信封口径：`{schemaVersion: "1"（字符串，spec §1）, toolVersion, kind, ...}`；
  check 负载 = success + diagnostics[]（DiagnosticDto 序列化，字段序即 schema 序）；
  verify 负载 = passed + failedStage + stages[9]（Skipped 显式在册）；
  error 负载 = error 字符串（配非零退出码）
- 冻结：lang-cli 诊断文本形 `{file}:第{line}行: [{code}] {message}`（TS formatDiag
  同构 + 文件前缀多文件消歧）；退出码契约 = 有诊断 1、用法错误 2
- 冻结：lang-cli::CollectSink（`OutputSink` 内存收集实现，宿主/测试复用）
- 变更登记（行为修正，非契约破坏）：lang_backend_{go,js}::prelude 的 push 生成形
  返回数组本身（对齐 TS/Go 解释器证据与 lang-runtime 同位点；void 形导致 →Go 产物
  编译失败、→JS 语义分歧——见完成块第 7 节）

## SPEC-GAP

详见 `docs/lang-rs/spec-gaps.md`（SPEC-GAP-RS-001/002/003，均 OPEN 待协调人裁决）：
1. 嵌套字符串引号转义跨后端分歧（→Go strconv.Quote vs →JS JSON.stringify）；
2. jsonStringify 浮点跨实现分歧（jsprelude f 段走 'g' 形、-0.0 三方分歧）；
3. jsonStringify 非字符串键三方分歧（interp 报错 / →JS 键字符串化 / →Go panic）。
