# A6-verify 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。
> 对齐锚：lang-rs 版冻结登记（docs/lang-rs/status/A6-verify.md）1:1 对齐 + C# 形态差异（见下）。

## 完成块（2026-09-26，首批交付：Agent Contract & Verification）

> 规模：6 src 项目 + 6 测试项目入 sln；**60 测试全绿**（Contract 24 + Conformance 25 +
> Graph 5 + Edit 2 + Refactor 2 + Verify 2）。门禁实测：`dotnet build LangCs.slnx
> -c Release -warnaserror` 退出码 0；12 个自有项目逐个 `dotnet format --verify-no-changes`
> 全干净。零 NuGet 依赖（JSON = System.Text.Json，YAML 手写受限子集解析器）。

### Lang.Contract：Diagnostic → JSON DTO（specs/diagnostics.md §2 + agent-protocol schemaVersion "1"）

- `Dto.cs`：`DiagnosticDto`/`SpanDto`/`PositionDto` 与 Lang.Core 内部 record 分离，
  `DiagnosticDto.Of(Diagnostic, IFileResolver)` 唯一转换入口。
- 字段序 = schema 结构体序：`code, ruleId, severity, message, primarySpan, relatedSpans, help`；
  位置序 `offset, line, column`；区间序 `file, start, end`。每属性显式 `JsonPropertyName`——
  形状不依赖序列化方命名策略。全字段必出，primarySpan 缺席序列化 `null`。
- 行级形态（column 恒 1、offset 恒 0）由 Lang.Core `Diagnostic.AtLine` 保证，DTO 逐字映射。
- `ContractJson.Serialize/Deserialize` 唯一序列化入口：非 ASCII 码点**字面输出**（与
  Go encoding/json / serde_json 证据一致）。实现边界见 SPEC-GAP-CS-A6-02。
- `IFileResolver` + `SourceNames`（字典只按键查询、从不迭代；未登记 SourceId → file ""，对齐 TS 证据）。
- golden JSON 测试 7 条：spec §2 样例手抄（含 file ""）、agent-protocol §3 span 形态 +
  related/help 非空、null primarySpan、schema 形状校验（required 七字段 + 属性出现序 +
  severity 枚举 + code/ruleId 模式 + 行级形态）、**astral 码点 offset 专项**（🦀 码点 offset 4 =
  char index 4..5，offset/column 一律码点度量）、协议侧反序列化往返、未登记 SourceId → ""。
  期望全部手抄自 spec，禁同义反复。

### Lang.Contract：Effects fixpoint 引擎（specs/runtime.md EFFECT-001）

- `Effects.cs`：`Effect` 9 枚举（枚举数值序 = 声明序 = `EffectText.All` 契约序
  PURE,IO,FS,NET,TIME,RANDOM,SPAWN,CHANNEL,FFI）、`CallGraph.AddFn/SetDirect/SetBuiltinCall/
  AddCall/Solve`、`EffectEntry`、`Effects.BuiltinDirectEffect`（println/print→IO、http.get→NET、
  makeChan/send/recv→CHANNEL；extern→FFI 与 go 语句→SPAWN 属 IR 结构形状，由 IR 接线方
  `SetDirect` 挂）。输入 = 泛型调用图（FunctionId+名，边调用/go 目标），本波不接语义核。
- 容器全部有序（SortedDictionary/SortedSet），solve 输出按 FunctionId 升序、效果按契约序
  去重，空集输出 `[PURE]`；幂集格单调增长必收敛。构造期良构性拒绝：
  `EffectsGraphException{DuplicateFn, UnknownFn}`（受控异常类型，非用户源码错误）。
- 专项测试 13 条：对抗重点 8 条全落（直接/1 级/3 级传播、自递归收敛、互递归、spawn=
  SPAWN+被调效果并集、channel 三内建、纯环保持 PURE）+ 契约序输出 + 登记序无关确定性
  + 多 callee 并集 + 内建映射表 + 良构性拒绝。
- **与 Lang.Ir 暂驻 Effect 的对照测试**（`IrBridgeTests`，4 条）：枚举 9 变体逐位同名同序、
  内建映射对照表 23 名（含 http.get/空串/astral 未知名）逐一相等、IR 带效果指令 7 形态
  与 spec 推导规则名集一致、IR 直接效果事实桥进 CallGraph 的接线预演。
  测试类头部已注明：A3 迁移删除 Lang.Ir.Effect 时本类同步退役。

### Lang.Conformance：manifest 解析 + 套件扫描 + 四路路由骨架 + ConformanceReport

- `Manifest.cs`：**手写 YAML 受限子集解析器**（零外部依赖）。支持形态穷举：顶层标量键
  name/category/mode + 列表键 ruleIds + 恰一层 expect 嵌套；`stdout: |` 块标量；
  diagnostics 列表项（`- ` 起头 + 对齐延续字段）；`#` 注释（独立行）；Tab 缩进拒绝；
  不支持转义/锚点/多级嵌套/流式标量（引号仅接受空串 `""`），重复键拒绝，**任何超集形态
  一律报错**。语义校验落硬：run 必须 `exitCode: 0`、stderr 空串、stdout 末尾恰一换行
  无空行且不得声明 diagnostics；check ≥1 条诊断且不得声明 exitCode/stdout/stderr；
  诊断码必须已知（`Enum.TryParse`，Lang.Core 单一事实源）、severity ∈ error|warning、
  ruleId 可省（省 = `DiagnosticCode.Rule().RuleText()` 回填）、声明了必须与映射一致、
  line 0 放行（套件真值的「无位置诊断」形态）。
- `Scan.cs`：`TryScan(root, out cases, out failures)` —— category/case 两级目录扫描
  （Ordinal 排序），缺 case.yaml、manifest 失败、name/category 与目录名失配、空 category
  都收集后一次报错；输出 (category, name) 确定序。`DefaultSuiteRoot()` 从程序集位置向上
  定位仓库根。**实测：`conformance/` 恰好 151 case 全部解析零失败**（16 类：closure 4/
  collections 11/concurrency 6/diagnostics 30/enum 3/expressions 16/generics 6/io 7/match 9/
  module 5/option 3/result 6/struct 8/syntax 13/types 14/values 10）；测试断言恰 151
  （套件扩容时同步改断言）+ 两次扫描指纹逐项相等 + 排序形状 + run/check 模式双存在。
- `Compare.cs`：路由名冻结 `Routes.RunRoutes = [interp-ast, interp-ir, go, js]`；
  `IRunRoute{Name, Run(caseDir)}` 注入接口（本波不接解释器/后端，测试用假执行器驱动）；
  `RouteResult{Output, Error}` + `RouteError{Skipped, Failed}` 区分环境跳过（不谎报通过）
  与真实失败；run 三要素逐字节比对（exitCode/stdout/stderr）；check =
  `code|ruleId|severity|line` 多重集比对（message/列号是既定漂移面，不入键），缺/多按
  `key×n` 报告（SortedDictionary 键序，禁哈希序外泄）；`JudgeRun/JudgeCheck` 判定语义。
- `Report.cs`：`ConformanceReport{cases}`（(category,name) 确定序）+ `CaseVerdict{category,
  name, mode, routes, check}`（run 模式 routes 与 RunRoutes 顺序平行、check 恒空，反之
  routes 恒空）；`RouteOutcomeKind{Passed, Failed, Skipped}` 三格独立（skipped 不并入任何
  一侧）；聚合面 `CellFor(category,route)`/`RouteTotal`/`CheckCell`/`CheckTotal`/`Categories`/
  `RunCaseTotal`/`CheckCaseTotal`/`IsFullyCovered`（每路由格数 = run case 数且 check 恒有
  判定——防路由漏挂静默漏跑，测试验证检出漏挂）。JSON 形状测试锁定：camelCase 键 +
  声明序 + `mode="run"/"check"`、`outcome="passed"/"failed"/"skipped"` 字符串枚举
  （`JsonStringEnumMemberName` + `JsonStringEnumConverter`）。
- `Runner.cs`：`RunWithRoutes(cases, routes, checkDiagnostics?)` —— 路由表须与 RunRoutes
  名字逐位平行（否则 ArgumentException）；run case → 四路三态映射；check case → 诊断源
  委托注入（缺注入判败并注明，不静默）。测试 5 条（全绿聚合 / 失败·跳过分格 /
  未注入诚实判败 / 路由表不平行拒绝 / 假执行器判定透传）。

### Lang.Graph：语义图模型

- `NodeKind` 11 变体（Module/File/Function/Variable/Parameter/Struct/Field/Method/Enum/
  Variant/Type）、`EdgeKind` 10 变体（defines/references/calls/imports/returns/reads/
  writes/contains/depends_on/spawn），`AsStr` 冻结（测试全枚举锁定 11/10）；枚举数值序 =
  声明序 = 边排序 kind 次序。
- `Graph`：节点身份 = `SymbolId`；节点 SortedDictionary、边 SortedSet<GraphEdge>
  （`GraphEdge : IComparable` 按 (from, kind 声明序, to) 升序、天然去重）；`EdgesFrom`
  （GetViewBetween 区间，callees 直接集）/`EdgesTo`（callers 直接集）；重复节点/悬空边
  拒绝（`GraphException{DuplicateNode, UnknownNode}`），重复边幂等。
- 确定性测试 5 条：冻结字符串形全枚举、同图不同登记序 → 公开迭代逐项相等 +
  canonical 序形状（对齐 lang-rs 同数据）、集合语义与良构性、直接入/出边序、
  astral 节点名（函数🦀）原样保留。

### 骨架项目（本波只登记形状与签名，不实现）

- `Lang.Edit/Edit.cs`：`TextEdit{File, StartOffset, EndOffset, Replacement}`/
  `Precondition{File, FileHash}`/`WorkspaceEdit{Edits, Preconditions}`；**TextEdit offset
  一律 0 基 Unicode 码点**（与 SourceSpan 同度量；C# string 是 UTF-16，astral 占 2 char，
  禁 char index 当 offset，实现须经 `SourceText.CpToCharIndex` 折算）已写进模块头注释；
  `ApplyErrorKind{MissingPrecondition, PreconditionFailed, Overlap, OutOfRange}` 拒绝语义
  + `IEditApplier.ApplyInMemory`（map 进 map 出、校验期零写入、全成或全不成）签名登记。
  测试 2 条：astral 码点 offset 契约锁定 + 跨文件聚合形状与拒绝语义四类清单。
- `Lang.Refactor/Refactor.cs`：`IRefactoring` 三入口签名（Rename/OrganizeImports/
  SafeRemove）+ 拒绝语义登记：`RenameRefusalKind` 七类（Unknown/UnsupportedKind/
  InvalidName/Conflict/Dirty/NoDeclSite/UntypedReceiver）、`OrganizeRefusalKind.Scattered`、
  `NotRemovable{Reason, RefSites}`（path:line:col 证据面）。基于 SymbolId + Lang.Graph
  引用面，**禁 Regex Rename**；默认 dry-run 产 Lang.Edit.WorkspaceEdit。测试 2 条
  （七类拒绝清单冻结 + NotRemovable 证据形状含 astral 路径）。
- `Lang.Verify/Verify.cs`：`Stage` 9 阶段（parse→resolve→typecheck→hir→ir→effect→compile→
  execute→differential，枚举数值序 = 执行序冻结）+ `StageOutcomeKind{Passed, Failed,
  Skipped}`（Skipped ≠ 通过 ≠ 失败）+ `VerifyReport{Stages}`（报告恒含全 9 段、
  `FailedStage()` 取执行序最前 Failed、整体通过 = 无 Failed）+ `IVerifyPipeline.Verify(dir)`
  入口登记。测试 2 条（九段执行序冻结 + 失败短路语义：1 Failed + 8 Skipped 显式在册）。

### 本波结构决策（登记备案）

- **依赖收窄**：6 个 src csproj 的预接线引用收窄为本波实际消费面（Contract/Conformance/
  Graph/Edit → Lang.Core；Refactor → Core+Edit+Graph；Verify → 无）。动机：本波只消费
  Lang.Core；窗口内 A2 的 Lang.Types 在途编译错（CS0549，已由 A2 修复）会沿传递引用
  阻断 A6 全部项目。后续波次消费语义面/路由真执行时按需加回（一行/条）。
  全 sln 构建在收工时已实测恢复通过。
- **ConformanceReport 查询方法 `CellFor`**：C# 中与 `Cell` 类型同名的方法不可行，
  run 格查询面叫 `CellFor(category, route)`（对齐 rust 的 `cell`）。
- **清单相等语义**：record 成员含 `IReadOnlyList`（运行时 List/数组）按引用比较——
  `EffectEntry`/`DiagnosticDto` 覆写 Equals 逐元素比较；扫描确定性测试改用 JSON 指纹
  逐项比较。

## 完成块（2026-09-26，第三波：conformance 四路真执行 + 151×4 总账 + strict 收官门禁）

> 本波把 runner 从注入骨架升级为四路真执行：AST/IR 双解释器（A4）+ Compile 全链 +
> →Go/→JS 生成 + go/node 宿主工具链执行（A5）全部接进第一波冻结的 `IRunRoute`，
> 全量 151 case 实测。go（1.27.0，LANG_CS_GO/PATH/已知路径探测命中）与 node（v24）
> 本机均可用，四路真实执行，**Skipped 0**。**511 格全绿：run 120×4 = 480 格 + check 31，
> 0 失败 0 跳过**——`strict_all_routes_green` 以常跑门禁注册（对齐 lang-rs 第四波形态；
> 本波无红格，REGISTERED_BLOCKERS 台账为空集）。所有数字实测留档
> （`dotnet test -c Release` ConformanceMatrixTests，全套件单次执行 ~24s）。

### Lang.Conformance：四路执行器适配层（Routes.cs）

- `CaseLoader.TryLoad`：Lang.Loader 目录即包装载（文件名序预读 + import 依赖先序展开、
  菱形去重、循环报错；`*_test.lang-go` 不装载）→ 逐文件 parse（SourceId 按装载序）→
  合并语句流 **滤除 `Stmt.Import`**（装载指令非运行时语句——四路一致的前提；
  lang-rs 同坑同修口径）→ 空目录报错（对齐 A4 迷你装载器语义）。
- `CaseLoader.NeedsHostOrFfi` skip 门：extern 语句或 `http.*` 具名调用全树保守遍历
  （15 语句/18 表达式全覆盖）→ Skipped。当前语料 0 例，检测保留防套件扩容后静默误判。
- `RoutePipeline.Prepare` 四路统一前置：装载失败 = **Failed**（非 Skip）→ skip 门 →
  `LangSemantic.Analyze` 静态诊断非空 = **Skipped**（check 语义面，非 run 比对对象，
  note 带去重码清单）。
- 四路实现：`InterpAstRoute`（`Interp.Run` + OutputSink 捕获）、`InterpIrRoute`
  （`Compiler.Compile` 全链 → `IrMachine.Run`）、`GoRoute`（Compile → →Go 生成 →
  go build → 执行产物）、`JsRoute`（→JS 生成 → `node main.mjs`——.mjs 必须经 node
  启动）。产物落系统临时目录（`RouteTempDir` Dispose 清理；Windows 瞬时占用残留
  按 SPEC-GAP-CS-A6-06 同口径 OS 兜底）。
- 工具探测：env `LANG_CS_GO`/`LANG_CS_NODE` → PATH → 已知路径（与 A5 E2E 同清单），
  逐一子进程探测（go 用 `version` 子命令、node 用 `--version`），每实例缓存；
  `WithExe(null)` 注入面 = 强制 Skipped（skip 逻辑测试/宿主覆盖用）。
  缺失 → 该路 Skipped 注明原因，不静默、不谎报。

### Lang.Conformance：check 诊断源（CheckSources）

- 装载 → `LangSemantic.Analyze` **直产诊断** → 投影 `DiagActual{code,ruleId,severity,line}`
  （code = `CodeText()`、ruleId = `RuleText()`、severity 小写、line =
  `primarySpan.start.line`，无位置诊断 = 0——SPEC-GAP-CS-A6-04 口径）→
  第一波冻结的 `judge` 多重集比对。装载/解析失败 = 空诊断集（比对把期望项全报
  「缺」判败并带明细——check case 必须可解析）。

### 全量 conformance 实测：151×4 总账（本波核心产出，511 格全绿）

| category | interp-ast | interp-ir | go | js | check |
|---|---|---|---|---|---|
| closure | 4p | 4p | 4p | 4p | — |
| collections | 11p | 11p | 11p | 11p | — |
| concurrency | 6p | 6p | 6p | 6p | — |
| diagnostics | — | — | — | — | 30p |
| enum | 3p | 3p | 3p | 3p | — |
| expressions | 16p | 16p | 16p | 16p | — |
| generics | 6p | 6p | 6p | 6p | — |
| io | 7p | 7p | 7p | 7p | — |
| match | 9p | 9p | 9p | 9p | — |
| module | 5p | 5p | 5p | 5p | — |
| option | 3p | 3p | 3p | 3p | — |
| result | 6p | 6p | 6p | 6p | — |
| struct | 8p | 8p | 8p | 8p | — |
| syntax | 13p | 13p | 13p | 13p | — |
| types | 13p | 13p | 13p | 13p | 1p |
| values | 10p | 10p | 10p | 10p | — |
| **合计** | **120p/0f/0s** | **120p/0f/0s** | **120p/0f/0s** | **120p/0f/0s** | **31p/0f** |

- 151 case = run 120 × 4 路（480 格）+ check 31；**通过 511 格、失败 0、Skipped 0**。
- **AST 路 120/120 复现 A4 基线**；**IR 路 120/120 复现 A4 差分基线**（C# 侧 B1/B2/B3
  类缺陷在 A3/A5 波次内已规避：A5 `mark_ref_reads` 写链 Ref 读形 + A3 降形栈契、
  数组 `+=` 拼接语义——与 lang-rs 第四波 36 红格的根因清单逐条对照均不存在）。
- check 31/31 全过（LangSemantic 直产诊断与套件真值多重集零漂移，含 line 0 无位置
  诊断形态）；io 类 json 双向 7 例四路全绿（宿主内建经解释器 Host 路由与双后端
  prelude 一致）。
- **本波唯一红格（已修复，根因在 A6 crate）**：syntax/unicode-identifiers-and-strings
  go/js 双格——宿主进程 stdout 契约为 UTF-8 字节流，`HostRun` 未设
  `StandardOutputEncoding`，Windows 按系统 ANSI 代码页（本机 GBK）解码出 mojibake
  （`中文测试` → `涓枃…`）。修复 = 进程读取显式 UTF-8；`Lang.Verify.RunTool` 同款
  潜伏缺陷一并修复（其 ASCII 夹具未暴露过）。

### 测试结构（tests/Lang.Conformance.Tests，36 测试全绿 = 25 存量 + 11 本波）

- `ConformanceMatrixTests`（对齐 lang-rs conformance_matrix.rs 形态）：主测试常跑——
  套件形状（120 run + 31 check = 151）+ 覆盖自检 + **AST 参考路 120/120 硬断言**
  （参考路是差分锚，红即实现坏，不设台账）+ **check 31/31 硬断言** +
  「run 失败格 ⊆ `REGISTERED_BLOCKERS`」回归守卫 + 台账格数守卫（上游修复落地后
  守卫红提示收缩台账；本次全绿，台账 = 空集）+ 矩阵打印留档 + skip 格数守卫（断言 0）。
  **`strict_all_routes_green` 为常跑 [Fact]**（lang-rs 因 36 红格挂 `#[ignore]`，本波
  全绿故直接解禁为门禁）：四路 120p/0f/0s + check 31p/0f 的 511 格全绿判定——
  三格独立、Skipped ≠ 通过、覆盖自检防漏挂。
- `RouteTests`：`WithExe(null)` → go/js 双路 Skipped 且原因点名工具；装载失败 = Failed
  非 Skip（空目录夹具）；AST/IR 夹具输出一致；go/js 宿主路 E2E（输出逐字节一致 +
  临时目录清理观察窗，工具缺失时 skip 打印原因不失败）。
- 门禁（全部实测）：`dotnet test -c Release` Conformance 36 绿 + Verify 23 绿；
  `LangCs.slnx -c Release -warnaserror` 退出码 0；触达 3 项目
  `dotnet format --verify-no-changes` 干净；全 sln 测试套件收工复跑退出码 0
  （19 测试项目全绿，含兄弟项目）。

## 完成块（2026-09-26，第二波：Safe Editing / Refactoring / Verify 管线）

> 规模：Lang.Edit / Lang.Graph / Lang.Refactor / Lang.Verify 四实现 + 相应测试。
> **自有 6 测试项目 129 测试全绿**（Contract 24 + Conformance 25 + Graph 9 + Edit 17 +
> Refactor 31 + Verify 23；本波新增 69）。门禁实测：8 个本波触达项目逐个
> `dotnet build -c Release -warnaserror` 0 警告 + `dotnet format --verify-no-changes`
> 干净；`LangCs.slnx -c Release -warnaserror` 收工实测退出码 0（窗口内 Lang.Cli 曾有
> 并行 agent 在途编译错，收工前已由对方修复，非 A6 领地）。对齐锚 = lang-rs 第三波
> （docs/lang-rs/status/A6-verify.md 第三波小节）1:1 + C# 形态差异逐条登记。

### Lang.Edit：安全编辑事务实现（specs/agent-protocol.md §6）

- `Hash.cs`：SHA-256 走 BCL（System.Security.Cryptography，禁 unsafe 约束下零手写），
  `EditHash.Sha256Hex` 小写 hex；NIST 标准向量（空串/"abc"/双块）+ UTF-8 字节度量
  （sha256("甲")）golden 测试锁定。
- `Apply.cs`：`EditTransaction.Apply`（map 进 map 出）+ `ApplyToDirectory`（文件系统
  事务：`.{file}.lang-edit-tmp` 临时文件 + `File.Move(overwrite)` 改名）。**C# 切片按
  char，必须经映射层**：单文件先建 `SourceText` 一次折算 cp→char 区间，start 降序
  （稳定：同 start 保编辑表序，LINQ OrderBy 稳定性）逐个 Remove+Insert——高偏移编辑
  不影响低偏移区间；同点插入按表序倒序落位（"…BA…" 用例锁定）。拒绝语义四类 +
  Io（见冻结面 delta）：哈希不匹配 / 缺前置 / 重叠（降序相邻对 next.end > start）/
  越界与倒序 / 读 IO。多文件事务 = 校验全过才输出（零半写）；盘上事务哈希不符一个
  字节不动（R2 用例）+ 临时文件零残留断言。
- 冻结 `IEditApplier` out 形态由 `EditApplier` 适配实现（富错误面 → 4 类 out 值）。
- 测试 15 条新增（对齐 rust lang-edit 16 条测试全集，fs 原子性 1:1）。

### Lang.Graph：Analysis → Graph 填充器（Fill.cs）

- 边语义全落（对齐 rust fill.rs 1:1）：defines/contains（struct→方法经 methods 表、
  enum→ctor、fn→参数/内部绑定/嵌套 fn、match Bind）、calls（callee Ident）/spawn
  （go 目标，仅 Ident callee）、reads/writes（赋值目标分类递归）、references
  （StructLit 类型 + match 构造模式）、imports（模块 → File 合成节点）、returns
  （返回注解 named root → 用户 struct/enum）。预载符号不建节点不落边（保守缺边）。
- **C# 事实形态差异（登记）**：rust 参数符号 span 与 FnDecl 全 span 同值（bind_params
  按 decl 起点匹配）；C# resolver 参数符号 span 起于名字 token——对齐口径改为「AST
  Param 节点 span 锚」（Param.Span.Start 即名字 token 起点），精确且无嵌套 fn 冲突。
- 节点身份 = SymbolId；模块/File 合成 id 自 `symbols.Count` 起。同 AST 两次填充
  节点/边逐项相等（确定性）+ 9 类边清点 + extern defines + 预载保守缺边测试。
- 已知缺口（与 rust 同步登记）：方法调用边（recv.method()）等检查器类型面开放后补。
- 测试 4 条新增。

### Lang.Refactor：三入口 + apply_and_verify 闭环（specs §7，禁 Regex）

- `Input.cs`：`RefactorInput.TryFromFiles/TryLoad`（目录装载走 Lang.Loader；SourceId
  按路径 Ordinal 序）+ 合并语句序列 + 同一缓冲跑 `LangSemantic.Analyze`（NodeMap 按
  节点引用取事实的构造纪律）；`Current()` 快照、`PathOf`、`BaselineCodes`（码多重集）、
  `TryResolveTarget`（id 或名字，名字多义拒绝、预载不可指名）。
- `Walk.cs`/`Locate.cs`：预序走查 15 语句/18 表达式/4 模式（含 FnLit/extern/match
  臂，出现序确定性）；refs/declStmts/patBindings/refOwners 查询面；声明名字 token
  定位（DeclNameSpan 的接收者括号平衡跳越、ParamIdentSpan 的顶层括号组分段）。
- `Refactorings.cs`：rename（fn/method/Var(let/for/match Bind)/Param；定义位词元定位
  + refs + 方法调用点经接收者静态类型→struct.methods，类型未知 `UntypedReceiver`
  保守拒绝不半改；保留字名单 45 项逐字对齐 TS 证据；`Dirty`/`Conflict`/`NoDeclSite`
  显式拒绝）；organize-imports（AST 结构事实 + 无损 trivia：去重保首现、码点序排序、
  行尾注释随行、独立注释归拢块首、已序零编辑、区间夹代码 `Scattered` 拒绝）；
  safe-remove（仅顶层 fn/全局变量；extern 显式拒绝；引用计数 0 才给删除编辑，删除
  区间吞行尾 \r 与单个 \n；有引用回 `path:line:col` 证据）。
- `ApplyAndVerify` 闭环 = precondition → apply → **format（Lang.Formatter 本波已在
  树，接上）** → reparse → re-analyze → 诊断码多重集零回归断言；失败报
  `RefactorError{Apply|Verify}`。E2E R1–R8 全绿（见矩阵）。
- 测试 29 条新增（rust 单测全集 + e2e 矩阵 1:1）。

### Lang.Verify：九段管线 + ExecDriver 注入（本波新实现）

- `Exec.cs`：`IVerifyDriver` 注入面（LowerHir/LowerIr/GenerateGo/GenerateJs/RunAst/
  RunIr/RunGo/RunJs）+ `ExecError{Skip|Fail}`（Skipped ≠ 失败）+ `RunOutput{stdout,
  stderr,exitCode}` + `StubVerifyDriver`（全段 Skipped 桩）+ `DefaultVerifyDriver`：
  LowerHir 接 `Lang.Hir.HirBuild.BuildHir`（A3 第一实现，窗口内落地即接线）、
  LowerIr 默认接 `Lang.Ir.Lowerer.IrLowerer(types, symbols)`（A3 在本波窗口内落地，
  语义查询面构造期注入）+ 管线内过 `Validator.Validate`、GenerateGo/Js 接
  `Lang.BackendGo/Js.CodeGen`（A5）、RunGo/RunJs 宿主工具链执行（`go`/`node` 探测
  每驱动缓存，go build 产物执行；Windows 下 .mjs 必须经 node 启动；临时目录 Dispose
  清理）。**A4（Interp/IrMachine）在途**：RunAst/RunIr 留构造期委托注入点，未接线
  如实 `Skipped("AST 参考路未接线"/"IR 解释器未接线")`，不谎报。
- `Pipeline.cs`：`VerifyEngine.RunPipeline/Verify(dir)/VerifyFiles`。分段判定 =
  Parse（逐文件 parse）→ Resolve/Typecheck（一次 analyze；resolver 失败 checker 不跑）
  → Hir → Ir（lower+validate）→ Effect → Compile（双后端生成可生成性）→ Execute
  （各执行路三要素；非零退出/带 stderr 即败）→ Differential（stdout 逐字节 + exit
  + stderr 全比对；<2 路或有路缺席 → Skipped 带明细，绝不谎报全路一致）。任一段
  Failed → 后续段 `Skipped("上游 X 未通过，短路")` 显式在册。**Import 语句在合并点
  滤除**（装载指令非运行时语句；消费方自滤义务，检测驱动测试锁定——lang-rs
  SPEC-GAP-RS-A6-04 的 C# 对应物同步落实）。
- `Effects.cs`：Effect 段事实提取（AST + SymbolId，禁文本搜索）→ Lang.Contract
  `CallGraph` → 不动点。节点 = 非预载 fn/method（extern 挂 FFI）；直接效果 =
  `Effects.BuiltinDirectEffect` 单一事实源 + go→SPAWN；边 = 直接调用 + go 目标 +
  方法调用（接收者类型→struct.methods，未知保守缺边）；FnLit 归属所在函数；顶层
  副作用归合成 init（FunctionId.MAX 懒登记）；模式不走查。确定性测试锁定。
- 测试 21 条新增（rust effects 9 条 + pipeline 6 条 1:1 + E2E 6 条）。

### E2E 矩阵（真实夹具，系统临时目录 + Dispose 清理；全部实测绿）

| # | 夹具 | 断言 | 结果 |
|---|------|------|------|
| R1 | 跨文件 rename（main+util）| 3 处编辑跨 2 文件、双 precondition → 闭环（含 format）→ 落盘重载：新名解析/旧名消失/零诊断/码多重集相等 | 绿 |
| R2 | rename 后外部改动文件 | 盘上事务哈希不符拒绝、两文件零字节半写、tmp 零残留 | 绿 |
| R3 | struct/enum/重名 | `UnsupportedKind`/`Conflict` 显式拒绝 | 绿 |
| R4 | 乱序+重复 import（含被引模块文件）| 单点区间编辑 + 排序去重、行尾注释随行、空行与函数体不动；落盘重载零诊断 | 绿 |
| R5 | import 区间夹代码 | `Scattered` 拒绝 | 绿 |
| R6 | 被引用顶层 fn | 拒绝 + refSites（`path:line:col`）证据 | 绿 |
| R7 | 未引用顶层 fn | 删除编辑 → 闭环 → 声明消失、无残留空行、重载零诊断 | 绿 |
| R8 | 方法/局部变量目标 | 「仅支持顶层 fn 与全局变量」拒绝 | 绿 |
| V1 | 全绿夹具 + DefaultVerifyDriver 全链（目录动词形）| Parse..Compile 七段全 Passed；go/js 双路真宿主执行，差分 Passed 且 RecordingDriver 独立断言两路 stdout 逐字节 `"3\nok\n"`（本机 go 1.27.0 + node v24；ast/ir 路待 A4 Skipped 点名不谎报）| 绿 |
| V2 | `VerifyFiles` 内存输入 | 与 V1 同链路 Ir/Compile/Execute 全 Passed | 绿 |
| V3 | parse/resolve/typecheck 失败夹具 ×3 | 失败段 + 短路 Skipped 全在册（9 段恒全）、failed_stage 按执行序、E1001/E2002/E30xx 入证据 | 绿 |
| V4 | 篡改 js 路输出 | `Differential Failed` 带两路 stdout 逐字节证据、`failed_stage=Differential` | 绿 |
| V5 | StubDriver + 全绿夹具 | 前端三段真跑、Hir 起全 Skipped 在册、整体绿（Skipped ≠ 失败）| 绿 |
| V6 | 目录含 import（跨 2 文件）| Import 滤除后 Ir/Execute 全 Passed（对 conformance load_case 同坑的管线侧锁定）| 绿 |



## 进行中

（无——第三波交付完成：conformance 四路真执行，151×4 总账 511 格全绿，
`strict_all_routes_green` 常跑门禁实测绿；全 sln Release -warnaserror 构建通过 +
全量 19 测试项目测试套件退出码 0（含兄弟项目），触达 3 项目 format 干净。）

## 下一步

- Lang.Cli 接线 conformance 输出：`ConformanceReport`（System.Text.Json 可序列化已备）
  + `run_suite` 形态接 `SuiteRunner.RunWithRoutes` 四路真执行；`verify <dir>` 接
  `VerifyEngine.Verify`；`refactor rename|organize-imports|safe-remove`（dry-run 默认，
  --apply 走 `EditTransaction.ApplyToDirectory` 盘上事务）。
- Lang.Verify ast/ir 委托接线：`DefaultVerifyDriver` 的 `runAst`/`runIr` 构造期注入点
  接 `Interp`/`Compiler.Compile + IrMachine`（本波 Conformance 侧已验证全链，Verify 侧
  接线 = 两个委托一行；接线后 verify 差分从 go/js 双路升级为 ast/ir/go/js 四路）。
- Lang.Contract 效果事实源升级：直接效果从 IR 结构提取（消费 A3 的
  `InstDirectEffects`/`FnDirectEffects`）接 `CallGraph`，inspect.effects 信封输出待 CLI 波次。
- Lang.Ir 过渡 Effect 迁移协调：A3 删除 `Lang.Ir.Effect` 改消费 Lang.Contract 时，
  `IrBridgeTests` 退役（测试类头部已注明）。
- `strict_all_routes_green` 维护纪律：套件扩容（case > 151）或上游改动引入红格时，
  按台账纪律先定性归属——A6 crate 缺陷必须修，跨 crate 红格登记
  `REGISTERED_BLOCKERS`（tests/Lang.Conformance.Tests/ConformanceMatrixTests.cs）
  并同步 status 阻塞区；工具缺失导致的 Skipped 同样使门禁红（L6 要求零 Skipped）。

## 阻塞

（无。两个窗口内事件，均已闭环、不遗留：
① A2 的 Lang.Types CS0549（首批交付窗口）——收工前上游修复；
② Lang.Cli 在途编译错（第二波窗口，并行 CLI agent 写 Commands.cs 中途态）一度使
`LangCs.slnx -c Release -warnaserror` 失败——非 A6 领地不改他人文件，以「先完成自有
12 项目门禁 + 收工前重试全 sln」处理，重试实测退出码 0。）

## 已冻结接口登记

- 冻结：Lang.Contract.DiagnosticDto：`{code, ruleId, severity, message, primarySpan,
  relatedSpans, help}`，primarySpan null 化、字段序即 schema 序、`Of(Diagnostic,
  IFileResolver)` 唯一入口；PositionDto/SpanDto 对应 `offset,line,column`/`file,start,end`。
- 冻结：Lang.Contract.ContractJson：`Serialize<T>/Deserialize<T>` 契约 JSON 唯一入口，
  非 ASCII 码点字面输出（实现 = UnsafeRelaxed + 代理对转义还原扫描，见 SPEC-GAP-CS-A6-02）。
- 冻结：Lang.Contract.IFileResolver/SourceNames：SourceId → file 反查，未登记 → ""。
- 冻结：Lang.Contract.Effect：9 变体枚举，`EffectText.All` 固定契约序，输出去重按此序，
  空集 = [PURE]。
- 冻结：Lang.Contract.CallGraph：`AddFn/SetDirect/SetBuiltinCall/AddCall/Solve`，Solve 返回
  按 FunctionId 升序的 `EffectEntry{Id, Name, Effects}`；go 目标沿调用边保守传播；
  构造期拒绝 = EffectsGraphException{DuplicateFn, UnknownFn}。
- 冻结：Lang.Contract.Effects.BuiltinDirectEffect：println/print→IO、http.get→NET、
  makeChan/send/recv→CHANNEL、其余 null；extern→FFI 与 go→SPAWN 由 IR 接线方 SetDirect 挂。
- 冻结：Lang.Conformance.ManifestParser.Parse：受限子集 + run/check 语义硬校验；
  ruleId 省略 = E 码映射回填，声明 = 必须与映射一致；run stdout 必须块标量形态；
  line 0 = 无位置诊断形态放行。
- 冻结：Lang.Conformance.SuiteScanner：`TryScan` 两级目录扫描 + name/category 与目录名
  一致性校验，失败全量收集，输出 (category,name) 确定序；套件规模断言 = 151（扩容同步）。
- 冻结：Lang.Conformance.Routes + RunRoutes：四路名 `interp-ast/interp-ir/go/js`；
  IRunRoute 注入接口；run 三要素逐字节比对，check 四元组多重集比对；
  RouteError.Skipped ≠ 通过（RouteError.Failed 直接判败）。
- 冻结：Lang.Conformance.ConformanceReport（System.Text.Json 可序列化）：CaseVerdict
  run 模式 routes 与 RunRoutes 顺序平行/check 恒空；RouteOutcomeKind 三态 + Cell 三格独立
  （skipped 不并入任何一侧）；聚合面 CellFor/RouteTotal/CheckCell/CheckTotal/Categories/
  RunCaseTotal/CheckCaseTotal/IsFullyCovered；JSON：mode/outcome = 小写字符串枚举。
- 冻结：Lang.Conformance.SuiteRunner.RunWithRoutes：路由表须与 RunRoutes 名字逐位平行；
  check 诊断源委托注入，缺注入 = 判败并注明（不静默）。
- 冻结：Lang.Graph.NodeKind/EdgeKind：11/10 变体与 AsStr；Graph 节点身份 = SymbolId，
  公开迭代序 = id 升序 / (from, kind 声明序, to) 升序；EdgesFrom/EdgesTo = 直接集。
- 冻结：Lang.Edit.TextEdit：offset 一律 0 基 Unicode 码点（模块头注释；字节偏移止步
  parser 内部）；`WorkspaceEdit{Edits, Preconditions}`，Precondition.FileHash = sha256 hex
  （调用方计算）；ApplyErrorKind 四类拒绝 + IEditApplier.ApplyInMemory 签名。
- 冻结：Lang.Refactor.IRefactoring 三入口签名（Rename/OrganizeImports/SafeRemove）+
  拒绝语义：RenameRefusalKind 七类 / OrganizeRefusalKind.Scattered / NotRemovable{Reason,
  RefSites}；基于 SymbolId+Lang.Graph，禁 Regex Rename；默认 dry-run 产 WorkspaceEdit。
- 冻结：Lang.Verify.Stage：9 阶段执行序 parse→resolve→typecheck→hir→ir→effect→compile→
  execute→differential；StageOutcomeKind 三态；VerifyReport 恒含全 9 段，FailedStage() =
  执行序最前 Failed，Skipped 显式在册（≠ 失败）；IVerifyPipeline.Verify(dir) 入口签名。

### 第二波冻结（2026-09-26，Safe Editing / Refactoring / Verify 实现）

- 冻结：Lang.Edit.EditHash.Sha256Hex（sha256 小写 hex，UTF-8 字节度量）；
  Lang.Edit.ApplyError{Kind, Message} 详情载体 + EditTransaction.Apply（map 进 map 出，
  空编辑集 = no-op 不查前置）/ ApplyToDirectory（盘上事务：校验零触碰文件系统，写入 =
  临时文件 + 改名）；同 start 稳定降序落位、同点插入表序倒序的确定性语义随实现冻结。
- 冻结（delta 登记）：ApplyErrorKind 增第五类 `Io`（文件不存在/读写 IO 失败）——对齐
  rust 冻结面 `ApplyError::{..., Io}`，首批登记的四类不变。
- 冻结：Lang.Graph.GraphFill.FromAnalysis(stmts, analysis, moduleName)：边语义 9 类
  （defines/contains/calls/spawn/reads/writes/references/imports/returns）+ 预载符号
  保守缺边 + 模块/File 合成 id 自 symbols.Count 起；参数 contains 边按「AST Param 节点
  span 锚」定位（C# 参数符号 span 形态与 rust 证据不同，见完成块登记）。
- 冻结：Lang.Refactor.RefactorInput（TryFromFiles/TryLoad/Current/PathOf/
  BaselineCodes/TryResolveTarget）+ AstWalk/Locate 走查定位面 + Refactorings.Rename/
  OrganizeImports/SafeRemove（富拒绝详情 RenameRefused/OrganizeRefused +
  SafeRemoveVerdict 三态）+ ApplyAndVerify（precondition → apply → format → reparse →
  re-analyze 诊断码多重集零回归；RefactorError{Apply|Verify}）+ RefactoringSession
  （首批冻结 IRefactoring 三入口的实现类）。
- 冻结（delta 登记）：OrganizeRefusalKind 增第二类 `Parse`（源码解析失败拒绝）——对齐
  rust `OrganizeRefusal::{Parse, Scattered}`，首批登记的 Scattered 不变。
- 冻结：Lang.Verify.IVerifyDriver（LowerHir/LowerIr/GenerateGo/GenerateJs/RunAst/
  RunIr/RunGo/RunJs 注入面）+ ExecError{Skip|Fail} + RunOutput + StubVerifyDriver +
  DefaultVerifyDriver（LowerHir=HirBuild、LowerIr=IrLowerer(types,symbols)+Validator、
  GenerateGo/Js=双后端 CodeGen、RunGo/RunJs=go/node 探测缓存 + 宿主执行 + 临时目录
  Dispose；RunAst/RunIr 委托注入点待 A4）。
- 冻结：Lang.Verify.VerifyEngine.{RunPipeline, Verify(dir), VerifyFiles} +
  VerifyInput（FromFiles 路径序 / TryFromDir 走 Lang.Loader / TryParseAll）：九段编排、
  短路 Skipped 显式在册、**Import 合并点滤除**、Effect 段 = EffectFacts（AST 事实 →
  CallGraph 不动点；合成 init = FunctionId.MAX）。Execute 段口径（对 rust 的 C# 细化）：
  ast 路真失败即整段短路（参考路纪律）；ast 未接线 = 进 skips 名单按可用性参与，
  go/js 可独立成立差分对——A4 接线后 ast 自动回归参考路。

### 第三波冻结（2026-09-26，conformance 四路真执行）

- 冻结：Lang.Conformance.{InterpAstRoute, InterpIrRoute, GoRoute, JsRoute}：`IRunRoute`
  四实现，路由名 = `Routes.RunRoutes` 冻结名；每路自 case_dir 独立走
  「CaseLoader.TryLoad → NeedsHostOrFfi skip 门 → LangSemantic.Analyze → 执行」。
  装载/解析失败与执行失败 = `RouteResult.Fail`；skip 门命中与工具链缺失 =
  `RouteResult.Skip`（不静默、不计入通过）。GoRoute/JsRoute：Compile → CodeGen →
  产物落系统临时目录（RouteTempDir Dispose 清理）→ `go build`+执行产物 /
  `node main.mjs`（.mjs 必须经 node 启动）。
- 冻结：Lang.Conformance.CaseLoader：TryLoad（Lang.Loader 目录即包 + 逐文件 parse +
  **合并滤除 `Stmt.Import`** + 空目录报错）与 NeedsHostOrFfi（extern 语句或 `http.*`
  具名调用全树保守遍历——四路统一 skip 门，语料 0 例防扩容静默误判）。
- 冻结：Lang.Conformance.CheckSources.FromAnalysis：check 模式诊断源——装载 →
  LangSemantic.Analyze 直产诊断 → 投影 `DiagActual{code,ruleId,severity,line}`
  （line = primarySpan.start.line，无位置 = 0）；装载/解析失败 = 空诊断集判败带明细。
- 冻结：Lang.Conformance 工具探测契约：env `LANG_CS_GO`/`LANG_CS_NODE` 覆盖 → PATH →
  已知路径（go 与 A5 E2E 同清单），逐一子进程探测（`version`/`--version`），每实例缓存；
  `GoRoute.WithExe(null)`/`JsRoute.WithExe(null)` = 强制 Skipped 注入面。
- 冻结（测试契约）：tests/Lang.Conformance.Tests/ConformanceMatrixTests.cs 主测试 =
  形状 151（120 run + 31 check）+ 覆盖自检 + ast 120/120 + check 31/31 + skip 守卫
  （断言 0）+「run 失败 ⊆ REGISTERED_BLOCKERS」回归守卫 + 台账格数守卫（全绿 = 空集）；
  **`strict_all_routes_green` 常跑门禁**（四路 120p/0f/0s + check 31p/0f = 511 格全绿，
  三格独立、Skipped ≠ 通过）——本波实测全绿后直接解禁注册（lang-rs 同名测试因红格
  挂 ignore，本波无红格故为常跑 [Fact]）；新红格出现时按台账纪律处置（见「下一步」）。
- 冻结（delta 登记）：Lang.Conformance/Lang.Verify 宿主进程读取显式 UTF-8
  （StandardOutput/ErrorEncoding）——进程 stdout/stderr 契约为 UTF-8 字节流，
  Windows ANSI 代码页默认解码产生 mojibake 的根因修复随实现冻结。

## SPEC-GAP

- SPEC-GAP-CS-A6-01（套件解析口径，登记备案）：manifest 列表键（ruleIds/diagnostics）在
  C# 解析器中必须 ≥1 项——「`ruleIds:` 键后无列表项」「空流式映射 `expect: {}`」一律拒绝
  （README「任何超集形态一律报错」口径的从严面；Rust 侧 serde 允许 `ruleIds: []` 形态）。
  151 个真实 case 均不涉及；若未来需要空列表形态，属 schema 扩容走协调人裁决。
- SPEC-GAP-CS-A6-02（实现边界，登记备案）：契约 JSON 的字面非 ASCII 输出在 BCL 无直接
  开关——默认 Encoder 全量 \uXXXX 转义；UnsafeRelaxedJsonEscaping 对 astral 码点仍强制
  代理对转义；`UnicodeRange` 只覆盖 BMP（无法声明 astral 允许面）；自定义 JavaScriptEncoder
  的抽象方法签名含 unsafe（工程纪律禁 unsafe）。落地 = UnsafeRelaxed 序列化 + 左到右扫描
  还原「代理对转义」为字面 UTF-16（其余转义保留；`\\u` 字面形态因成对消费而无歧义）。
  golden 测试以字面 UTF-8 形锁定（对齐 Go/serde 证据）；控制字符仍按 JSON 规范转义。
- SPEC-GAP-CS-A6-03（观察项，非语言语义）：conformance/README.md 写「14 类」，实测
  conformance/ 为 16 类 151 case；README 的类清单滞后于套件内容（同 lang-rs
  SPEC-GAP-RS-A6-01）。属共享目录文档漂移，归协调人裁决是否补 README（A6 不改 conformance/）。
- SPEC-GAP-CS-A6-04（套件真值备案，同 lang-rs SPEC-GAP-RS-A6-03）：check 诊断项存在
  `line: 0`（diagnostics/result-second-param-not-error 的 E3009）——无位置诊断的发射形态，
  README 未描述该约定。C# 解析器按套件真值放行 line 0，runner 按
  primarySpan.start.line 精确比对（与 lang-rs 同口径）。
- SPEC-GAP-CS-A6-05（实现口径差异，登记备案）：verify Execute 段的 ast 参考路在
  「未接线（Skipped）」时按可用性参与（与 ir/go/js 同列缺失名单），不整段短路——
  rust 证据是 ast Skipped 即整段 Skipped（其 DefaultExec 恒有 ast，该分支不可达）。
  C# 因 A4 在途窗口而取「诚实参与」口径：工具链路可独立成立差分对、缺失路逐个点名；
  ast 真失败仍整段短路（参考路纪律不变）。A4 接线后两实现行为重新一致。
- SPEC-GAP-CS-A6-06（实现边界，登记备案）：Lang.Verify.VerifyTempDir 的临时目录删除
  在 Windows 文件占用下可能失败——Dispose 捕获 IOException/UnauthorizedAccessException
  静默吞（与 rust `let _ = remove_dir_all` 同口径）；系统临时目录由 OS 兜底清理，
  测试断言不依赖删除成功。第三波 Lang.Conformance.RouteTempDir 同口径（第四波实测
  抓到一例 node 执行后 main.mjs 被瞬时占用致删除失败），E2E 测试按本口径留 10s
  观察窗 + 断言不依赖删除必成（残留仅 note 报告）。
- SPEC-GAP-CS-A6-07（实现边界，登记备案）：宿主子进程 stdout/stderr 解码编码——
  Windows 上 .NET Process 重定向流默认按系统 ANSI 代码页（本机 GBK）解码，而语言
  契约的程序输出为 UTF-8 字节流（spec 确定性约束）；非 ASCII 输出（如
  unicode-identifiers-and-strings 的「中文测试」）经默认解码变 mojibake。落地 =
  ProcessStartInfo 显式 `StandardOutputEncoding/StandardErrorEncoding = UTF-8`；
  BCL 无进程级全局开关（Console.OutputEncoding 只管本进程控制台）。rust/Go 侧
  无对应问题（字节面读取）。
