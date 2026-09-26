# A6-verify 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块（2026-09-25，集成轮收官：B1–B3 清零，strict 门禁解禁，协调人授权集成工程师执行）

- B1（lang-ir 赋值降形，见 A3-hir-ir.md 集成轮小节）/ B2（双后端 ArrayPush 拼接，
  见 A5-backend.md 集成轮小节）/ B3（Go RangeNew int64 cast，见 A5 集成轮小节）
  全部修复；B1 修复后四路差分另牵出并修复双后端写链缺口 8 格（装箱 Ref 解引用 +
  链中间组件 Ref 读形，见 A5 集成轮小节）。
- `tests/conformance_matrix.rs`：`REGISTERED_BLOCKERS` 清空（历史条目留存注释）、
  台账格数守卫 36 → 0、`strict_all_routes_green` 解禁（#[ignore] 移除）。
- **strict_all_routes_green（L6 收官判据）实测全绿：151×4 = run 120×4 路 480 格
  + check 31 格 = 511/511 全过，0 失败 0 skip**。最终矩阵：interp-ast 120p /
  interp-ir 120p / go 120p / js 120p（各 0f/0s）、check 31p/0f——四路逐字节一致
  （修复前 109p/11f + 107p/13f + 108p/12f）。
- dogfood/langalgo `verify`（lang-cli 真二进制，go 路走本机 Golang/bin）：**九段
  全过，exit 0**（修复前 execute 段因 B1 失败「ir: IR 解释器: int 不可索引赋值」、
  differential 短路 Skipped）。
- 全量门禁（全部实测退出码 0）：`cargo test --workspace` = 505 测试全绿 0 失败
  0 ignored（此前 strict 的 1 ignored 已解禁转绿）；`cargo fmt --all -- --check`
  干净；`cargo clippy --workspace --all-targets -- -D warnings` 干净。

## 完成块（2026-09-25，首批交付）

### lang-contract：Diagnostic → JSON DTO（serde）
- `dto.rs`：`DiagnosticDto`/`SpanDto`/`PositionDto`，DTO 与 lang-core 内部 struct 分离，
  `DiagnosticDto::of(&Diagnostic, &dyn FileResolver)` 唯一转换入口。
- 字段序 = schema 结构体序：`code, ruleId, severity, message, primarySpan, relatedSpans, help`；
  位置序 `offset, line, column`；区间序 `file, start, end`。全字段必出，
  primarySpan 缺席序列化为 `null`（schema required 集合照办）。
- 行级形态（column 恒 1、offset 恒 0）由 lang-core `Diagnostic::at_line` 保证，DTO 逐字映射。
- `FileResolver` trait + `SourceNames`（BTreeMap 反查表；未登记 SourceId → file ""，对齐 TS 证据）。
- golden JSON 测试 5 条：specs/diagnostics.md §2 样例手抄（含 file ""）、agent-protocol §3
  span 形态 + related/help 非空、null primarySpan 单行形、schema 形状校验（required 七字段/
  severity 枚举/code ruleId 模式/行级形态）、协议侧反序列化往返。期望全部手抄自 spec，禁同义反复。

### lang-contract：Effects fixpoint 引擎（EFFECT-001）
- `effects.rs`：`Effect`（9 枚举，`ALL` 契约序 PURE,IO,FS,NET,TIME,RANDOM,SPAWN,CHANNEL,FFI）、
  `CallGraph::{add_fn, set_direct, set_builtin_call, add_call, solve}`、`EffectEntry`、
  `builtin_direct_effect`（println/print→IO、http.get→NET、makeChan/send/recv→CHANNEL；
  extern→FFI 与 go 语句→SPAWN 属 IR 结构形状，由 IR 接线方 `set_direct` 挂）。
- 输入 = 泛型调用图（节点 FunctionId+名，边调用/go 目标），本波不接语义核。
- 传播语义保守：caller ⊇ 全部 callee 效果；go 目标照常沿边传播（SPAWN 另挂直接效果）。
  幂集格单调增长必收敛；BTree 容器 + solve 按 FunctionId 升序 + 效果按 ALL 序去重，
  空集输出 `[PURE]`。
- 专项测试 13 条：对抗重点 8 条全落（direct/1 级/3 级传播、递归、互递归、spawn、channel、
  纯环）+ 契约序输出 + 登记序无关确定性 + 多 callee 并集 + 内建映射表 + 良构性拒绝。

### lang-conformance：manifest 解析 + 套件扫描 + 比较骨架
- `manifest.rs`：serde_yaml + `deny_unknown_fields`；公开模型 `Case{Mode, rule_ids, Expect}`。
  语义校验（README manifest 节落硬）：run 必须 `exitCode: 0`、stderr 空串、stdout 为
  `|` 块标量（末尾恰一换行、无空行）且不得声明 diagnostics；check 至少一条诊断且不得
  声明 exitCode/stdout/stderr。诊断项 code 必须是已知 E 码、severity ∈ error|warning、
  ruleId 可省（省 = 由 `DiagnosticCode::rule()` 回填），声明了必须与映射一致。
- E 码字符串桥接表在本 crate（lang-core 无 from_str；映射仍走 lang-core 单一事实源，
  lang-core 追加码时同步）。
- `scan.rs`：`scan_suite(root)` —— category/case 两级目录扫描，缺 case.yaml、manifest
  失败、name/category 与目录名失配都收集后一次报错；输出按 (category, name) 确定序。
  实测：`conformance/` 恰好 **151 case 全部解析零失败**（16 类：closure4/collections11/
  concurrency6/diagnostics30/enum3/expressions16/generics6/io7/match9/module5/option3/
  result6/struct8/syntax13/types14/values10）；测试断言恰 151（套件扩容时同步改断言）。
- `compare.rs`：run 四路比较骨架 —— 路由名冻结 `interp-ast/interp-ir/go/js`
  （`RunRoute` trait 注入执行器，本波不接解释器）；stdout 逐字节、exitCode、stderr 全比对，
  `RouteError::{Skipped,Failed}` 区分环境跳过（不谎报通过）与真实失败；
  check = `code|ruleId|severity|line` 多重集比对（message/列号是既定漂移面，不入键），
  缺/多按 `key×n` 报告。判定结果 `CaseResult{ok, skipped, note}`。
- 测试 16 条（manifest 8 + scan 2 + compare 6），含 151 case 真套件扫描。

### lang-graph：语义图模型
- `NodeKind` 11 变体（Module/File/Function/Variable/Parameter/Struct/Field/Method/Enum/
  Variant/Type）、`EdgeKind` 10 变体（defines/references/calls/imports/returns/reads/
  writes/contains/depends_on/spawn），as_str 冻结；Ord 推导序 = 声明序 = 边排序 kind 次序。
- `Graph`：节点身份 = `SymbolId`（模块级节点同编号空间）；节点 BTreeMap、边 BTreeSet
  （(from, kind, to) 升序、天然去重）；`edges_from`（callees 直接集）/`edges_to`（callers
  直接集）；重复节点/悬空边拒绝，重复边幂等。
- 确定性测试：同图不同登记序 → 公开迭代逐项相等；节点 id 升序、边 (from,kind,to) 序锁定。

### 骨架 crate（本波只登记形状与签名，不实现）
- `lang-edit`：`TextEdit`/`Precondition`/`WorkspaceEdit` 数据形状；**TextEdit offset 一律
  0 基 Unicode 码点**（与 SourceSpan 同度量）已写进模块头注释；apply 事务入口签名 +
  ApplyError 拒绝语义（precondition/重叠/缺前置）登记。
- `lang-refactor`：rename/organize_imports/safe_remove 三入口签名 + 拒绝语义登记
  （基于 SymbolId+lang_graph 引用面，禁 Regex Rename；默认 dry-run 产 WorkspaceEdit）。
- `lang-verify`：`Stage` 9 阶段（parse→…→differential，执行序冻结）+ `StageOutcome`
  （Passed/Failed/Skipped）+ `VerifyReport{passed, failed_stage}`；入口 `verify(dir) -> VerifyReport` 登记。带 2 条语义测试。

## 完成块（2026-09-25，第三波：Safe Editing / Refactoring / Verify 管线）

> 本波盘定：lang-edit 事务、lang-graph 填充器、lang-refactor 三入口在盘上已实现
> （前波交付、状态文件未及登记），本波逐 crate 验证全绿后如实登记；lang-verify
> 九段管线与全部 E2E 为本波新实现。所有数字为实测。

### lang-edit：安全编辑事务（specs/agent-protocol.md §6）
- `hash.rs`：SHA-256（FIPS 180-4）就地实现（零外部依赖），NIST 标准向量
  （空串/"abc"/双块/百万 a）+ 块边界锁定。
- `apply.rs`：`apply_in_memory`（map 进 map 出）+ `apply`（文件系统事务：
  临时文件 + 原子改名）。码点度量切片拼接；拒绝语义四类全测：
  `PreconditionFailed`（哈希不匹配）/`MissingPrecondition`/`Overlap`/`OutOfRange`
  （越界与倒序）；同 start 稳定降序落位（同点插入确定性）；多文件事务
  全成或全不成（校验阶段零触碰文件系统）。
- CJK 码点偏移专项（`let 甲 = 乙 + 1` 改名用例）+ 文件系统原子性用例。
- 测试 **16 通过**。

### lang-graph：Analysis → Graph 填充器（fill.rs）
- 边语义全落：defines/contains（struct→方法、enum→ctor、fn→参数/内部绑定）、
  calls/spawn（go 目标）、reads/writes（赋值目标）、references（StructLit 类型 +
  match 构造模式）、imports（模块 → File 节点）、returns（返回注解 → 用户
  struct/enum）。预载符号（println/Some/…）不建节点不落边。
- 节点身份 = SymbolId；模块/File 为合成 id（自 `symbols.len()` 起，与 arena
  编号空间不撞）。同 AST 两次填充逐项相等（确定性测试）。
- 已知缺口（登记）：方法调用边（`recv.method()`）依赖接收者静态类型，等
  检查器类型面在图上开放后补。
- 测试 **6 通过**（含 9 类边清点 + 确定性）。

### lang-refactor：三入口（specs §7，禁 Regex——只走 SymbolId 事实）
- `input.rs`：`RefactorInput::from_files/load`（目录装载走 lang-loader）；
  SourceId 按路径字典序；`resolve_target`（协议 `<sym>`：id 或名字，名字多义
  拒绝要求用 id）；`baseline_codes`（零回归断言基线）。
- `rename.rs`：fn/method/Var（let/for 变量/match 绑定）/Param；定义位经词元流
  定位名字 token；引用位 = `refs` + StructLit 类型引用；方法调用点经检查器
  接收者静态类型 → `struct.methods` 表，类型未知即 `UntypedReceiver` 拒绝
  （不半改）；struct/enum/ctor 显式 `UnsupportedKind`（P1）；同名新名
  `Conflict`；解析有诊断 `Dirty`；保留字名单逐字对齐 TS 证据。
- `imports.rs`：organize-imports 单文件排序 + 去重（保首现、码点序），行尾
  注释随行走、独立注释归拢块首零丢失；已序零编辑；区间夹代码
  `Scattered` 保守拒绝。
- `remove.rs`：safe-remove 仅顶层 fn/全局变量、引用计数 0 才给删除编辑
  （吞声明后换行）；有引用返回 `NotRemovable{reason, ref_sites}`
  （`path:line:col` 证据面）。
- `apply.rs`：`apply_and_verify` 闭环 = precondition → apply → format →
  reparse → re-analyze → 诊断码多重集零回归断言；失败报
  `RefactorError::Apply|Verify`（specs `stage: apply|verify`）。
- 测试 **15 通过**（lib）+ **8 通过**（tests/e2e.rs，见 E2E 矩阵）。

### lang-verify：九段管线（本波新实现）
- 冻结形状不动：`Stage` 9 阶段（specs runner-contract 执行序）、
  `StageOutcome{Passed,Failed,Skipped}`、`VerifyReport{stages}`；报告语义
  从「未执行段缺席」改为「短路段 Skipped 显式在册」（任务书「后续 skip 且
  报告可见」；Skipped ≠ 失败的判定不变）。
- `pipeline.rs`：`run_pipeline`/`verify(dir)`/`verify_files`。分段判定：
  Parse（逐文件 parse，路径字典序 SourceId）→ Resolve（resolver 诊断 =
  `!checked && has_errors`）→ Typecheck（checker 诊断）→ Hir（`HirBuilder::
  lower`，取 `Result` 形避免 `build_hir` 的 panic 面）→ Ir（注入 lower +
  `lang_ir::validate`）→ Effect → Compile（→Go/→JS 生成可生成性）→
  Execute（AST 参考路必须成功；ir/go/js 路按可用性参与，非零退出即败）→
  Differential（stdout 逐字节 + exitCode + stderr 全比对；不足 2 路或有路
  缺席 → Skipped 带明细，绝不谎报全路一致）。任一段 Failed → 后续段全部
  `Skipped("上游 <段> 未通过，短路")`。
- `exec.rs`：`ExecDriver` 注入 trait——`run_ast/lower_ir(hir, &analysis)/
  run_ir/run_go/run_js`。**`DefaultExec` 五段全接**（A3 的 IrLowerer 在本波
  收尾窗口落地，随即接线：构造期注入 `&analysis.types/&analysis.symbols`
  语义查询面、产物自过 validate）+ A4（`Interp` + `IrMachine`）+ A5
  （双后端 codegen + go/node 宿主执行，工具链探测缓存，node 经解释器启动
  ——Windows 直接执行 .mjs 报 os error 193）。`StubExec` 全段 Skipped 桩。
  临时目录 Drop 清理。
- `effects.rs`：Effect 段事实提取（AST + SymbolId，禁文本搜索）→
  lang_contract `CallGraph` → 不动点。直接效果：内建名面（println/print→IO、
  makeChan/send/recv→CHANNEL，单一事实源 = `builtin_direct_effect`）、
  go→SPAWN、extern→FFI；调用边 = 直接调用 + go 目标 + 方法调用（接收者
  类型 → struct.methods，未知保守缺边）；FnLit 效果归属所在函数；顶层副作用
  归属合成 `init` 节点（懒登记，FunctionId::MAX）；模式（绑定/字面量/构造）
  不含调用、零效果事实，不走查。确定性测试锁定。
- 测试 **19 通过**（lib）+ **8 通过**（tests/e2e.rs，见 E2E 矩阵）。

### E2E 矩阵（真实夹具目录，系统临时目录 + Drop 清理）

| # | 夹具 | 断言 | 结果 |
|---|------|------|------|
| R1 | 跨文件 rename（main+util）| 3 处编辑跨 2 文件、双 precondition → 闭环 → 落盘重载：新名解析/旧名消失/零诊断/码多重集相等 | 绿 |
| R2 | rename 后外部改动文件 | 盘上事务 `lang_edit::apply` 哈希不符拒绝、两个文件零字节半写 | 绿 |
| R3 | struct/enum/重名 | `UnsupportedKind`/`Conflict` 显式拒绝 | 绿 |
| R4 | 乱序+重复 import（含被引模块文件）| 排序去重、行尾注释随行、空行与函数体不动；落盘重载零诊断 | 绿 |
| R5 | import 区间夹代码 | `Scattered` 拒绝 | 绿 |
| R6 | 被引用顶层 fn | 拒绝 + refSites（`path:line:col`）证据 | 绿 |
| R7 | 未引用顶层 fn | 删除编辑 → 闭环 → 声明消失、无残留空行、重载零诊断 | 绿 |
| R8 | 方法/局部变量目标 | 「仅支持顶层 fn 与全局变量」拒绝 | 绿 |
| V1 | 全绿夹具 + DefaultExec 全链（A3 lowering → A4 双解释器 → A5 生成/宿主执行）| 前八段全 Passed；差分对可用执行路成立；ast/ir/js 三路 stdout 独立逐字节断言 `"3\nok\n"`（不依赖差分消息）| 绿 |
| V2 | `verify(dir)` 动词形直跑 | 与 V1 同链路全过、差分不失败、整体绿 | 绿 |
| V3 | parse/resolve/typecheck 失败夹具 ×3 | 失败段 + 短路 Skipped 全在册、failed_stage 按执行序 | 绿 |
| V4 | 篡改 js 路输出 | `Differential Failed` 带证据、`failed_stage=Differential` | 绿 |
| V5 | StubExec + 全绿夹具 | 前端四段 + effect 真跑，执行段全 Skipped 在册、整体绿（Skipped ≠ 失败）| 绿 |
| V6 | 内存文件集输入 | `verify_files` 前端四段 + effect 全过 | 绿 |

（V1 实测：本机无 go、有 node v24 —— ast/ir/js 三路 stdout 逐字节 `"3\nok\n"`
一致，go 路 Skipped 显式点名；差分段相应 `Skipped("已比对 3 路…缺失路: go…")`
——有路缺席不谎报全路一致。）

## 完成块（2026-09-25，第四波：conformance runner 四路真执行 + 151×4 总账）

> 本波把 runner 从骨架升级为四路真执行：AST/IR 双解释器（A4）+ →Go/→JS 生成
> +宿主工具链执行（A5）全部接进第一波冻结的 `RunRoute`，全量 151 case 实测。
> go（1.27.0，PATH+已知路径探测命中）与 node（v24）本机均可用，四路真实执行，
> **无一路因工具缺失 Skipped**。所有数字为实测（`cargo test -p lang-conformance
> --test conformance_matrix -- --nocapture` 留档）。

### lang-conformance：四路执行器适配层（routes.rs）
- `load_case`：lang-loader 目录即包装载（文件名序预读 + import 依赖先序展开、
  菱形去重、循环报错、`*_test.lang-go` 不装载）→ 逐文件 parse → 语句合并；
  **`Stmt::Import` 在合并时滤除**（import 是装载指令非运行时语句——loader 为
  文件级合并、产物保留 import 原句，解释器对 Import 报错而 lowering 跳过，
  滤除后四路一致；本波差分实测抓出并修复的本 crate 唯一 bug，修复前 ast 路
  module 5 例全红）。
- 显式 skip 门（四路统一，诚实纪律）：extern FFI / 宿主 `http.*` 内建
  （保守全树遍历，与 lang-interp harness 同构）→ Skipped；静态诊断非空的
  run case（check 语义面）→ Skipped。本波语料两者均为 0 例，检测保留防套件
  扩容后静默误判。
- 四路实现：`InterpAstRoute`（`Interp`）、`InterpIrRoute`（`lang_ir::compile`
  全链 → `IrMachine`）、`GoRoute`/`JsRoute`（codegen → 临时目录写产物 →
  `go build`+执行 / `node main.mjs`——Windows 直接执行 .mjs 报 os error 193，
  必须经 node 启动；临时目录 Drop 清理）。
- 工具链探测：环境变量覆盖（`LANG_RS_GO`/`LANG_RS_NODE`，与 A5 E2E 同名）→
  PATH → 已知路径（go 与 A5 同清单），逐一 `<exe> version/--version` 子进程
  探测；`OnceLock` 每实例缓存，`with_exe(None)` 注入面供 skip 逻辑测试/宿主
  覆盖。缺失 → 该路 `Skipped` 注明原因，不静默、不谎报。
- 测试 3 条：注入 None → go/js 双路 Skipped 且原因点名工具；AST/IR 夹具一致；
  go/js 宿主路 E2E（工具缺失时 skip 打印原因不失败）。

### lang-conformance：ConformanceReport（report.rs，冻结登记）
- `ConformanceReport{cases}`（(category,name) 确定序）+ `CaseVerdict{category,
  name,mode,routes,check}`：run 模式 routes 与 `RUN_ROUTES` 顺序平行、check
  恒空；check 模式反之。`RouteOutcome{Passed,Failed(note),Skipped(note)}`、
  `CheckVerdict{ok,note}`、`Cell{passed,failed,skipped}`（三格独立，
  skipped 不并入任何一侧）。聚合面：`cell(category,route)`/`route_total`/
  `check_cell`/`check_total`/`categories`/`case`/`run_case_total`/
  `check_case_total`/`is_fully_covered`（每路由格数 = run case 数、check 列 =
  check case 数——防路由漏挂静默漏跑）。全型 `serde::Serialize` 供 CLI 与
  parity-report 消费。测试 2 条（聚合矩阵 + 覆盖自检检出漏挂）。

### lang-conformance：套件 runner（runner.rs）
- `run_suite(root) -> Result<ConformanceReport, Vec<ScanFailure>>`（内建四路）
  + `run_cases`/`run_with_routes`（路由注入形态，宿主可替换执行器）。
- run 判定：`judge_run`（第一波冻结面）→ RouteOutcome 三态映射；check 判定：
  装载 → `analyze_stmts` 直产诊断 → 投影 `DiagActual{code,ruleId,severity,
  line}`（line = `primarySpan.start.line`，无位置诊断 = 0，SPEC-GAP-RS-A6-03
  口径）→ `judge_check` 多重集比对；装载/解析失败 = Failed（check case 必须
  可解析）。
- 测试 2 条（假执行器注入三态映射 + 差异透传）。

### lang-verify：管线 Import 滤除（差分实测牵出的同坑修复）
- `run_pipeline` 的 merged 构造点统一滤除 `Stmt::Import`（下游 effect/execute
  消费同一 merged）——此前 verify 对含 import 的模块目录会在 Execute 段报
  「import 由装载器在合并前展开」（第三波 E2E 全为单文件夹具未及暴露）。
  新增测试 `import_statements_filtered_for_execution`（内存双文件 + DefaultExec
  全链，ir/execute 段 Passed、报告整体绿）。

### 全量 conformance 实测：151×4 总账（本波核心产出）

| category | interp-ast | interp-ir | go | js | check |
|---|---|---|---|---|---|
| closure | 4p | 4p | 4p | 4p | — |
| collections | 11p | 11p | 11p | 11p | — |
| concurrency | 6p | 6p | 6p | 6p | — |
| diagnostics | — | — | — | — | 30p |
| enum | 3p | 3p | 3p | 3p | — |
| expressions | 16p | 13p/3f | 12p/4f | 12p/4f | — |
| generics | 6p | 6p | 6p | 6p | — |
| io | 7p | 7p | 7p | 7p | — |
| match | 9p | 9p | 9p | 9p | — |
| module | 5p | 5p | 5p | 5p | — |
| option | 3p | 3p | 3p | 3p | — |
| result | 6p | 6p | 6p | 6p | — |
| struct | 8p | 6p/2f | 6p/2f | 6p/2f | — |
| syntax | 13p | 13p | 13p | 13p | — |
| types | 13p | 13p | 12p/1f | 13p | 1p |
| values | 10p | 4p/6f | 4p/6f | 4p/6f | — |
| **合计** | **120p/0f/0s** | **109p/11f/0s** | **107p/13f/0s** | **108p/12f/0s** | **31p/0f** |

- 151 case = run 120 × 4 路（480 格）+ check 31；通过 444 格 + check 31，
  **失败 36 格（12 case）全部定性归属上游**（见「阻塞」B0–B3），Skipped 0。
- **AST 路由 120/120 复现 A4 基线**；check 31/31 全过（lang-semantic 直产
  诊断与套件真值多重集零漂移，含 line 0 无位置诊断形态）；io 类 json 双向
  7 例四路全绿（宿主内建经解释器 Host 路由与双后端 prelude 一致）。
- 测试结构（tests/conformance_matrix.rs）：主测试常跑——全绿部分硬断言
  （ast 120/120、check 31/31、覆盖自检）+ 回归守卫（run 失败 ⊆
  `REGISTERED_BLOCKERS` 台账 + 台账格数守卫防台账腐化）+ 矩阵打印留档；
  `strict_all_routes_green` `#[ignore]` = 阻塞清零后的 L6 收官门禁（四路
  120p/0f/0s + check 31p）。
- 门禁（全部实测退出码 0）：`cargo test -p lang-conformance -p lang-verify`
  = 52 绿 + 1 ignored（conformance 23 lib + 1 矩阵 + verify 20+8，strict
  按 #[ignore] 登记）；`cargo fmt --check` 干净；`cargo clippy
  -p lang-conformance -p lang-verify --all-targets --no-deps -- -D warnings`
  干净；`cargo test --workspace` 50 套件 473 测试全绿（不破坏兄弟 crate）。

## 进行中

（无——第四波交付完成，本 crate 门禁全绿；跨 crate 阻塞 B1–B4 已定性登记，
见「阻塞」区，不阻断本 crate 门禁。）

## 下一步

- **B1（lang-ir）/B2–B3（双后端）修复后收尾**：从 tests/conformance_matrix.rs
  的 `REGISTERED_BLOCKERS` 移除对应条目（格数守卫会红提示同步）、36 格转绿后
  解禁 `strict_all_routes_green`（`cargo test -- --ignored` 应全绿）——这是
  L6 的 conformance 收官判据。
- Effects 事实源升级：直接效果可改从 IR 结构形状提取（与 AST 事实互为印证；
  当前 AST 提取已是保守超集），inspect.effects 信封输出待 CLI 波次。
- lang-cli 接线：`verify <dir> --json` 接 `lang_verify::verify` + lang-contract
  信封序列化；conformance 输出接 `lang_conformance::run_suite` +
  `ConformanceReport`（serde Serialize 已备）。
- runner 五动词收口：`compile <dir> --target`（产物落盘 + 退出码语义）与
  `inspect <dir> --json`（symbols/refs/type/dependencies/effects/graph 查询面）
  待后续波次。

## 阻塞

**第四波新增（差分实测定性，根因均不在 A6 crate；A6 只登记不代修。
定稿时点 2026-09-25 晚：共 36 失败格 = B1 33 + B2 2 + B3 1；B0 已在窗口内
由 A5 修复——并行波次落改会导致矩阵数字漂移，格数守卫负责捕获）。
【集成轮更新 2026-09-25：B1–B3 已全部修复并经 strict 门禁验证，台账清空、
守卫归零、strict 解禁——以下条目转历史定性档案，实测数字见顶部集成轮小节。】**

### B0（lang-backend-go，A5）——**已在本波窗口内修复**
- 曾有症状：`push(xs, 40)`（has_ret=false 的 CallBuiltin 语句位）发射为
  `_ = langGoPush(l4, l5)`，而 prelude 的 langGoPush 无返回值 → `go build`
  失败 `(no value) used as value`（collections/slice-ops go 格）。
- A5 在本波运行窗口内改 langGoPush 返回 `any`（返回被推数组），slice-ops
  go 格转绿；测试台账格数守卫实测捕获（37→36）并同步收缩。留档备查。

### B1（lang-ir，A3 修）：普通赋值 FieldSet/IndexSet 降形——压栈序违契 + 地址临时槽副本
- 最小复现：conformance/struct/struct-field-write（`let c = Counter{n: 0}` →
  `c.n = 41` → `c.n += 1` → `println(c.n)`；期望 `42\n`；AST 解释器 = `42\n` ✓）。
- IR dump 实证（`lang_ir::dump_program`）：`c.n = 41` 降形为
  `local.get 1 / local.set 2 / const.i64 41 / local.get 2 / field.set "n"`。
- **缺陷一（压栈序与冻结契约相反）**：压栈序 = val 先 obj 后；冻结栈效应契约
  （A3 状态文件：`FieldSet{field}` 2→0（obj,val）、弹序=压序逆序）要求 obj 先
  val 后。machine pop 序 val→obj（lang-interp/src/ir.rs FieldSet 臂先 pop val）
  于是弹出 val=Struct、obj=Int(41) → `place_of` = None → Internal
  「FieldSet 目标无地址（lowering 须保留槽位溯源或装箱）」；IndexSet 同构 case
  报「int 不可索引赋值」；go/js 忠实执行同一错误栈序 → 运行时 panic
  「int 没有字段 n」。
- **缺陷二（地址组件落临时槽深拷贝）**：地址组件 `c` 经 `local.get 1 /
  local.set 2` 复制进临时槽（铁律①「赋值三元组先落临时槽」误用于单块地址
  组件），field.set 经 place Var(slot2) 写临时副本，println 读原槽 slot1——
  即使修正压栈序，stdout 也是 `0` ≠ `42`；复合赋值同理在 slot3 副本上读改写。
- 对照：A4 手构 IR 测试（original-slot LocalGet 直接压栈、obj 先 val 后）全绿
  → 契约自洽，是降形发射违契。修复落点：lang-ir/src/lower.rs 赋值降形——
  地址组件（Ident/Field/Index 链前缀）直接以原槽 LocalGet 压栈（保 place
  溯源），仅多块操作数（右值）落临时槽；压栈序改 obj 先 val 后
  （IndexSet 三元组 obj→idx→val）。
- 波及 11 case 33 格（ir 11 + go 11 + js 11；containers-shared-aliasing 的
  go 格曾表现为 B0 build 失败，B0 修复后统一为 B1 运行时失败）：
  expressions/{eval-assign-index-from-call,
  eval-nested-call-order, evaluation-order-assign-target-first}、
  struct/{struct-field-write, struct-in-collection}、values/
  {containers-shared-aliasing, map-value-struct-shared, nested-struct-copy,
  reference-read-write, struct-copy-semantics, struct-nested-copy-deep}。

### B2（lang-backend-go/js，A5 修）：ArrayPush 发射未实现「rhs 为数组 → 拼接」语义
- 最小复现：conformance/expressions/eval-compound-string-concat
  （`var xs = [1]` → `xs += [2]` → `println(xs)`；期望 `[1, 2]\n`）。
- IR dump 实证：降形为单条 `array.push`、rhs = 整数组 `[2]`（A3 注记「数组
  +=（rhs 为 Slice）→ ArrayPush 原位拼接」）；IrMachine 的 ArrayPush 要求
  rhs 为 Slice（拼接语义）→ 输出 `[1, 2]\n` ✓；双后端 `langGoPush` 按元素
  追加 → `[1, [2]]\n` ✗。
- 修复落点：双后端 prelude/codegen——push 发射遇数组 rhs 需拼接（或发射前
  展开为逐元素 push，与机器语义对齐）。

### B3（lang-backend-go，A5 修）：RangeNew 操作数缺 int64 cast
- 最小复现：conformance/types/range-literal（`let r = 1..4`）→ `go build`
  失败：`cannot use l0 (variable of interface type any) as int64 value in
  struct literal`。RangeNew 操作数经临时槽（Go `any`）直入 struct 字面量，
  缺类型断言；js 动态域无此问题。

---

（历史——第三波收尾记录，均已闭环：
① lang-ir 在途编译错（A3 写 HIR→IR lowering 保存到一半）一度阻断 lang-verify
的 test/clippy/workspace 门禁，A3 窗口合拢后 IrLowerer 落地，本波随即接线
DefaultExec 五段全通；② lang-hir 一次 `manual_map` clippy lint 连带阻断
`-D warnings`（cargo clippy 会连带 lint 依赖图），以 `--no-deps` 限定本波
crate 后干净——lang-hir 自身的 lint 归 A3/A2 处理。

最终门禁（全部实测退出码 0）：
- `cargo test -p lang-edit -p lang-refactor -p lang-verify` = 66 测试全绿
  （edit 16 + refactor 15+8 + verify 19+8）；lang-graph 6 + lang-contract 19 亦绿。
- `cargo fmt --check`（本波五 crate）干净。
- `cargo clippy -p lang-edit -p lang-refactor -p lang-verify -p lang-graph
  -p lang-contract --all-targets --no-deps -- -D warnings` 干净。
- `cargo check --workspace` 不破坏兄弟 crate。）

## 已冻结接口登记

- 冻结：lang_contract::DiagnosticDto：`{code,ruleId,severity,message,primarySpan,relatedSpans,help}`，
  primarySpan null 化、字段序即 schema 序、`of(&Diagnostic, &dyn FileResolver)` 唯一入口。
- 冻结：lang_contract::FileResolver/SourceNames：SourceId → file 字符串反查，未登记 → ""。
- 冻结：lang_contract::Effect：9 变体枚举，`Effect::ALL` 固定契约序，输出去重按此序，空集 = [PURE]。
- 冻结：lang_contract::CallGraph：`add_fn/set_direct/set_builtin_call/add_call/solve`，
  solve 返回按 FunctionId 升序的 `Vec<EffectEntry{id,name,effects}>`；go 目标沿调用边保守传播。
- 冻结：lang_contract::builtin_direct_effect：println/print→IO、http.get→NET、
  makeChan/send/recv→CHANNEL、其余 None；extern→FFI 与 go→SPAWN 由 IR 接线方挂。
- 冻结：lang_conformance::parse_manifest：serde_yaml 受限子集 + run/check 语义校验；
  ruleId 省略 = E 码映射回填，声明 = 必须与映射一致；run stdout 必须块标量形态。
- 冻结：lang_conformance::scan_suite：两级目录扫描 + name/category 与目录名一致性校验，
  输出 (category,name) 确定序；套件规模断言 = 151（扩容时同步）。
- 冻结：lang_conformance::RunRoute + RUN_ROUTES：四路名 `interp-ast/interp-ir/go/js`；
  run 三要素逐字节比对，check 四元组多重集比对；Skipped ≠ 通过。
- 冻结：lang_graph::NodeKind/EdgeKind：11/10 变体与 as_str；Graph 节点身份 = SymbolId，
  公开迭代序 = id 升序 / (from,kind 声明序,to) 升序。
- 冻结：lang_edit::TextEdit：offset 一律 0 基 Unicode 码点（字节偏移止步 parser 内部）；
  `WorkspaceEdit{edits[], preconditions[]}`，precondition.file_hash = sha256 hex（调用方计算）。
- 冻结：lang_edit::apply/apply_in_memory（第三波登记）：拒绝语义
  `ApplyError::{MissingPrecondition, PreconditionFailed, Overlap, OutOfRange, Io}`；
  同 start 稳定降序落位；多文件事务全成或全不成（校验期零写入，盘上事务 =
  临时文件 + 原子改名）。
- 冻结：lang_refactor 三入口（第三波登记）：`rename(&RefactorInput, SymbolId,
  &str) -> Result<WorkspaceEdit, RenameRefusal>`（refusal 七类：Unknown/
  UnsupportedKind/InvalidName/Conflict/Dirty/NoDeclSite/UntypedReceiver）、
  `organize_imports(file, source) -> Result<WorkspaceEdit, OrganizeRefusal>`、
  `safe_remove(&RefactorInput, SymbolId) -> Result<Result<WorkspaceEdit,
  NotRemovable>, SafeRemoveError>`；`apply_and_verify(ws, baseline) ->
  Result<file→新文本, RefactorError::{Apply, Verify}>`（诊断码多重集零回归断言）。
- 冻结：lang_verify::ExecDriver（第三波登记）：`run_ast(&ExecContext) /
  lower_ir(&HirProg, &Analysis) / run_ir / run_go / run_js(&IrProgram) ->
  Result<RunOutput, ExecError>`；`ExecError::{Skipped, Failed}`（Skipped ≠ 失败）；
  `RunOutput{stdout, stderr, exit_code}`。DefaultExec 五段全接：A3
  （`IrLowerer::new(&analysis.types, &analysis.symbols)` + 产物自过 validate）、
  A4（Interp/IrMachine）、A5（双后端 codegen + go/node 宿主执行，探测缓存）。
- 冻结：lang_verify::Stage：9 阶段执行序 parse→resolve→typecheck→hir→ir→effect→compile→
  execute→differential；VerifyReport 失败输出失败阶段 + 已完成阶段，Skipped 显式呈现
  （第三波细化：报告恒含全 9 段，短路段 = `Skipped("上游 <段> 未通过，短路")`）。

### 第四波冻结（2026-09-25，conformance 四路真执行）

- 冻结：lang_conformance::{InterpAstRoute, InterpIrRoute, GoRoute, JsRoute}：
  `RunRoute` 四实现，路由名 = `RUN_ROUTES` 冻结名；每路自 case_dir 走
  「load_case → skip 门 → analyze → 执行」。GoRoute/JsRoute 工具探测
  `detect_go`/`detect_node`（env `LANG_RS_GO`/`LANG_RS_NODE` 覆盖 → PATH →
  已知路径，OnceLock 每实例缓存），`with_exe(Option<PathBuf>)` 注入面
  （None = 强制 Skipped）。go build 产物执行 / node 经解释器启动 .mjs；
  codegen 产物落系统临时目录，Drop 清理。
- 冻结：lang_conformance::{load_case, analyze_case, needs_host_or_ffi}：
  装载与门共享面——load_case = lang-loader 目录即包 + 逐文件 parse +
  **合并时滤除 `Stmt::Import`**（装载指令非运行时语句）；analyze_case =
  `analyze_stmts(stmts, SourceId(0))`；needs_host_or_ffi = extern 语句或
  `http.*` 调用全树检测（四路统一 skip 门，语料 0 例防扩容静默误判）。
- 冻结：lang_conformance::runner：`run_suite(root) -> Result<
  ConformanceReport, Vec<ScanFailure>>`（内建四路）、`run_cases(&[ScannedCase])`、
  `run_with_routes(cases, ast, ir, go, js)`（路由注入，路由名须与 RUN_ROUTES
  平行）；check 判定 = analyze 直产诊断投影 `DiagActual{code,ruleId,severity,
  line}`（line = primarySpan.start.line，无位置 = 0）经 `judge_check` 多重集
  比对；`default_suite_root()` = 仓库根 conformance/。
- 冻结：lang_conformance::ConformanceReport（serde::Serialize）：`{cases:
  Vec<CaseVerdict>}`（(category,name) 确定序）；CaseVerdict{category, name,
  mode, routes: Vec<RouteVerdict>, check: Option<CheckVerdict>}（run 模式
  routes 与 RUN_ROUTES 顺序平行/check 恒空，check 模式反之）；RouteVerdict
  {route, outcome}；RouteOutcome{Passed, Failed(note), Skipped(note)}；
  CheckVerdict{ok, note}；Cell{passed, failed, skipped}（三格独立，skipped
  不并入任何一侧）。聚合面：cell(category, route) / route_total(route) /
  check_cell(category) / check_total() / categories() / case(category, name) /
  run_case_total() / check_case_total() / is_fully_covered()（每路由格数 =
  run case 数且 check 格数 = check case 数）。
- 冻结（测试契约）：tests/conformance_matrix.rs 主测试 = ast 120/120 +
  check 31/31 + 覆盖自检 + 「run 失败 ⊆ REGISTERED_BLOCKERS」回归守卫 +
  台账格数守卫（定稿 36；并行波次修复落地后守卫会红提示收缩台账）；
  `strict_all_routes_green` `#[ignore]` = L6 收官门禁（四路 120p/0f/0s +
  check 31p/0f），阻塞清零后解禁。

## SPEC-GAP

- SPEC-GAP-RS-A6-01（观察项，非语言语义）：conformance/README.md 写「14 类」，实测
  conformance/ 为 16 类 151 case（多 expressions、values 两类）；README 的类清单滞后于
  套件内容。属共享目录文档漂移，归协调人裁决是否补 README（A6 不改 conformance/）。
- SPEC-GAP-RS-A6-02（实现边界，登记备案）：conformance/README.md 要求 manifest「任何超集
  形态一律报错（不支持锚点）」；Rust 侧按协调人指示用 serde_yaml 解析，锚点/别名会被
  YAML 层静默解析（未知键、深层嵌套、流式形态仍被 `deny_unknown_fields` + 语义校验拒绝）。
  151 个真实 case 均不使用锚点。若要 100% 落实超集拒绝需手写 parser（Python 证据即此路线）。
- SPEC-GAP-RS-A6-03（套件真值备案）：check 诊断项存在 `line: 0`（如 diagnostics/
  result-second-param-not-error 的 E3009）——无位置诊断的发射形态，README 未描述该约定。
  Rust 解析器按套件真值放行 line 0，runner 按 primarySpan.start.line 精确比对。
- SPEC-GAP-RS-A6-04（观察项，第四波实测）：lang-loader 为文件级合并，`LoadedProgram`
  产物保留源文件中的 `Stmt::Import` 原句（A5 冻结注记「语句级 splice 在解析器接线后
  于语句流上执行」）；消费方若不滤除，AST 解释器对 Import 语句报错（lowering 静默跳过，
  恰好掩盖）——本波差分在 ast 路 module 5 例抓出。消费方义务已双双落实：
  lang-conformance `load_case`（合并点滤除）与 lang-verify `run_pipeline`（merged
  构造点滤除，新增测试 `import_statements_filtered_for_execution` 锁定）。遗留归口
  问题（splice 义务收进 lang-loader 还是各消费方自滤）由协调人裁决。
