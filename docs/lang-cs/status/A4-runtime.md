# A4-runtime 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

### 2026-09-26 第二波交付：Lang.Interp 双解释器 + conformance 120 run case 实测 + AST/IR 差分全绿

- **Lang.Interp 四文件**（`Ops.cs` / `InterpFmt.cs` / `Interp.cs` / `IrMachine.cs`，全解决方案
  Release -warnaserror 0 警告 0 错误）：
  - **AST 参考解释器**（`Interp`，与 IR 解释器只共享 Machine/Value/Ops/输出面桥，遍历彼此独立）：
    `new Interp(Machine).Run(stmts, Analysis, OutputSink) → RtResult`——诊断非空拒绝执行
    （`程序含 N 条静态诊断，拒绝执行`，Scope 类）；EVAL-* 全落位：callee（含方法接收者）
    先于实参、实参左到右、赋值 target 地址组件先于右值（Ident 提前 lookup → 右值 → Set/
    复合读算写；Field/Index 链走 `AssignPlace` → `PlaceStore`）、短路 `&&`/`||`、obj 先 idx、
    字面量元素/键值对/struct 字段左到右、`go` 实参立即求值快照经 **Machine.Spawn 冻结调度面**
    （Interp 实现 TaskExec，go 目标恒顶层 fn → 闭包环境恒 Root）；方法/泛型擦除（解释器天然
    无类型面）/闭包按引用捕获（FuncDef + 定义处 EnvId）/match 四分类/`?` Propagate 在函数边界
    合成 Err（RESULT-TRY-003）/extern 调用报「仅转译可用」（FFI-001）/for-in 每迭代子环境 +
    元素快照（CTRL-FORIN-003）/宿主内建 jsonParse/jsonStringify 路由前拦截。执行序：顶层语句
    依序 → main（有则调）。20 内建预载进根环境（值面一等）。
  - **IR 解释器**（`IrMachine : TaskExec`）：`new IrMachine(IrProgram).Run(OutputSink) → RtResult`——
    栈式基本块 + LocalId 帧环境（帧 = Child(Root)，locals 全可写、%{i} 命名）+ 操作数栈
    `StackVal{Value, Place?}` 溯源写穿（LocalGet/GlobalGet 产 `Place.Var`；FieldGet/IndexGet
    链上扩展；FieldSet/IndexSet 经 `PlaceOf` → `Machine.PlaceStore`；**`PopN` 按栈序（=压入序）
    返回不反转**——构造类指令冻结合同）；ClosureNew/CallIndirect（捕获槽逐槽落地按引用共享）/
    SwitchEnum/SwitchInt（臂序首中即跳、default 可缺省）/Try fail 块/IterNew+IterNext（box 槽
    引用为句柄）/Chan 三专指令（recv 走 `Machine.Recv` 协作面）/BoxNew/Deref/RefSet；init→main
    双入口。**程序表尾预载三定义按名映射**（error→ErrorDef、Option→OptionDef、Result→ResultDef，
    勿重复注册——lang-rs 第四波 bug 教训落地）。
  - **输出面**（`InterpFmt : ValueFormatter`）：`Value → FmtNode` 桥 + 宿主内建，打印/JSON 规则
    **零漂移复用 Lang.BackendGo 冻结的 `Fmt`/`Json` 纯函数**（float 'g' 形、strconv.Quote 嵌套
    引号、map 插入序、JSON 码点序键 + HTML 转义 + None/unit→null）；chan→`?`、内建→`<内建 名>`
    截获（TS 证据口径）；jsonParse 按 json.Number 语义（整型文本优先 int64、键码点序载入、
    null→None）、非法 JSON → JsonParse 分类（RUNTIME-JSON-001）、jsonStringify 失败 → TypeCast
    带冻结消息。
  - **共享运算**（`Ops`）：AST BinaryOp / IR Binop 公共键单份实现（i64 回绕、float IEEE、
    除零/模零/移位域 Arith 类错误、字符串码点 `<`）——两路运算规则不漂移；`Compound` 复合赋值
    运算段（数组 `+=` 原位扩展由两路各自调用方先行处理）。
- **Conformance 实测（本波最大价值）**：`tests/Lang.Interp.Tests`（xunit，已 `dotnet sln` 登记）
  ——迷你装载器（目录内 .lang-go 名序收集 → import post-order DFS 依赖先序展开 + 菱形去重
  done 集 + in-progress **环检测** + `./x` 目录包展开（MODULE-IMPORT-002）→ 合并语句流剥除
  import）→ `LangSemantic.Analyze` → **AST 解释器 stdout 逐字节比对 case.yaml**。
  **run 类 120/120 全绿**（io 7 类 jsonParse/jsonStringify 宿主内建在内；check 类 31 个显式
  跳过——diagnostics 30 + types 1，真值不属执行面）。矩阵：expressions 16 / syntax 13 /
  collections 11 / values 10 / match 9 / struct 8 / generics 6 / result 6 / concurrency 6 /
  module 5 / types 13 / io 7 / closure 4 / enum 3 / option 3。总量锁定测试 `RunCaseCountIs120`
  （漏扫/静默跳过先红）。
- **AST vs IR 差分**：同一 case 双路（Interp vs IrMachine，同 stmts 同 Analysis 独立跑）
  **stdout 逐字节一致 + 两路均成功 + 与 case.yaml 真值一致，120/120**。
- **确定性压力**：10 个含 go/chan/closure/map 的 case（concurrency 全 6 + closure 2 +
  nested-struct-copy + map-iteration-insertion-order）× 3 遍 × 双路，**零漂移**。
- **消融验证**：向 `IrMachine.PopN` 注入 `slice.Reverse()`（构造类栈序反转）→ 差分立即
  **74/120 红**（`双路 stdout 不一致`）→ 恢复后 55 全绿，测试非空洞。
- **发现的 bug（归属 Lang.Ir = A3 领地，本波只最小化定位未修，请协调人派单）**：
  - **IR-Lowering-临时槽缺口**：无注解空数组 `let fns = []` → 元素类型 Unknown →
    `fns[0]()` 调用节点 `meta.type_id` = Unknown → lowering `TypeHasValue=false` →
    `CallIndirect` 发射 `ret=0`，而实参落临时槽的 `ExprsToTemps` 仍对其 `LocalSet` →
    栈下溢 → validate 拒绝（`IR 内部错误 fn#0 block#2 inst#10: LocalSet 栈下溢`）。
    最小复现（10 行）：`let fns = []` + for-in 内 `let f = fn(): int { return i * 10 }` +
    `push(fns, f)` + `println(fns[0](), fns[1]())`。**注解 `let fns: [fn(): int] = []` 即
    双路全绿**——真值套件无此形态（120/120 不受影响）；修法候选：lowering 对 has_value=false
    表达式按丢弃语句发射（Pop 而非 LocalSet；lang-rs E2E「has_ret=false 按语句发射」同款
    教训，疑同源于 lang-rs lower.rs，需 A3/上游双查）。A2 checker 对该程序静默容忍（Unknown
    无诊断）为另一观察点。
  - **观察（非缺陷，参考实现本身分歧）**：匿名 fn 值打印 AST 路 `<fn 匿名>`（TS value.ts：
    name 空 → 匿名）vs IR 路 `<fn <fnlit>>`（lang-rs lower.rs 亦将 fnlit 命名 `<fnlit>`）——
    真值套件不打印 fn 值，不进差分；C# 两路 1:1 继承各自参考行为。单元测试只锁 AST 路。
- **TDD**：55 测试全绿（ConformanceRun 17 + Differential 3 + InterpTests 35），期望值全部
  手算自 specs/conformance；对抗轴逐条落地：求值序（callee/assign-target/index/短路/map 对/
  方法接收者）、闭包（mutable/loop/nested/escaping）、引用读写穿透、值语义（嵌套 struct 拷贝
  `1 100`、元素读出即副本、容器别名共享）、`?` 传播合成 Err、spawn 实参快照（`2`/`Ok(1)`）、
  空 chan `Err("channel 空")` 逐字、运行时错误分类（越界/除零/match 无臂/extern 仅转译）、
  astral（len 码点、索引码点、`\uE000 < \U00020000` 码点序——UTF-16 序会反向）、输出面
  （`1e+06`/`-0`/`0.3333333333333333`、map 插入序、嵌套引号、JSON 键码点序、jsonParse null→None）。
  预载 20 内建名与 `BuiltinRegistry.Core()` 锁名测试（首批遗留项补上）。
- **验证矩阵（最终）**：`dotnet test LangCs.slnx -c Release` 全解决方案 18 项目全绿
  （本域 55 + Runtime 81 + Ir 87 + Semantic 81 + Syntax 95 等无回退）；
  `dotnet build LangCs.slnx -c Release -warnaserror` 0 警告 0 错误；
  `dotnet format --verify-no-changes`（Lang.Interp + Lang.Interp.Tests 两项目）通过
  （ConformanceHarness/InterpTests 首轮抓出 5 处空格违规，format 就地修复后复验过）。

### 2026-09-26 首批交付：Lang.Runtime 值模型与执行语义地基（与 lang-rs A4 语义 1:1）

- **Value 类层次**（`Value.cs`）：C# 无 sum 类型，用 `abstract record Value` + 嵌套 sealed record
  承载 14 变体（`private` 构造封闭层次，新增变体在所有穷尽 switch 处显式报错而非静默兜底）。
  `CloneValue()` 即 VALUE-COPY-001：struct/enum 递归拷贝（顶层逐字段 + 嵌套递归），容器字段
  （Slice/Map 的 ValueId）共享，标量与地址类值（Str/Chan/Closure/Builtin/Ref）不可变直接共享——
  ELEM-009「容器内 struct 共享指针」偏差在结构上不可表示。**值对象不可变纪律**：所有存储边界
  （cell define/set/写回、slice set/push、map insert、chan send、spawn 实参快照）一律 CloneValue 入库。
- **堆**（`Heap.cs`）：HeapArena（List 槽位只增不减，GC 委托宿主）+ `ValueId` newtype；
  `MapObj` = `List<MapSlot>` + `Dictionary<MapKey,int>` 仅做索引，**迭代序只来自插入序列表**，
  覆盖保位、原键形态不变（VALUE-MAP-006）；slice_get CloneValue 返回（VALUE-ELEM-009 读出即副本）。
- **环境**（`Env.cs`）：EnvArena cell{Value,Mutable}；`Lookup` 返回**属主** (EnvId, Slot)——
  共享属主 cell 即按引用捕获（VALUE-CAPTURE-004）；let cell 拒写（ImmutableAssign），
  `SetFieldWriteback` 专供字段写回路径（绑定不可重绑 ≠ 值的字段不可变）；每迭代子环境支撑 loop capture。
- **Place/Ref**（`Place.cs` + `Machine.cs`）：`Place::{Var,Element,MapEntry,Field}`；
  `PlaceStore` 先 `ResolvePlace` 穿透 Ref 链（探测装载失败视为无引用可穿透，原样返回）再落地；
  Field 走「读 base 副本 → 改字段 → 写回」（Var base 走 SetFieldWriteback，let cell 允许字段更新）；
  `&x` = `Value.Ref(Place.Var)`，**全程未用 C# ref/out/ref struct 表达语言引用**（VALUE-REF-003）；
  穿透深度上限 64（引用环防护，超限 Internal）。
- **Chan**（`Chan.cs`）：缓冲 1024 常量（`ChannelTable.ChanCap`）、send 不阻塞、满 → ChanFull
  （RUNTIME-CHAN-001）、FIFO、空渠道 `RecvOutcome.Empty` + **逐字冻结文本
  `ChannelTable.ChanEmptyMsg = "channel 空"`**（CHAN-RECV-003，程序可打印的值）；无 close（CHAN-* 冻结面）；
  载荷入队即 CloneValue 快照（VALUE-TASKSHARE-007）。
- **调度**（`Sched.cs` + `Machine.cs`）：`Task{Func, Args}` 实参快照（Spawn 内 CloneValue 入队）；
  `Machine.Spawn` 入队后 `RunPending` 语句点同步排空（TASK-SPAWN-002），嵌套 spawn 由排空循环消化，
  Task 错误上抛、返回值丢弃；`Machine.Recv(exec, ch)` 先协作让位再 FIFO 出队。
- **内建**（`Builtin.cs`）：注册表 **20 名字面锁定**（println/print/len/str/int/float/keys/values/has/
  push/assert/err/bytes/char/makeChan/send/recv/isNone/jsonParse/jsonStringify）+ `RegisterHost` 扩展口；
  `BuiltinKind` 19 变体（含 Host，Host 调用返回 UnsupportedHost 由宿主拦截）；
  预载构造器 some/none/ok/err/error_value/is_none 挂在 Machine。
- **预载定义**（`Defs.cs`）：`error`→StructDefId(0)、`Option`→EnumDefId(0)、`Result`→EnumDefId(1)
  （与 A2 对齐；Option cases=[Some,None]、Result cases=[Ok,Err]、error fields=[msg] 已测锁定）。
- **错误模型**（`Error.cs`）：`RtError(Kind, Message)` + `RtErrorKind` 13 变体（RUNTIME-FAIL-001
  分类即契约）+ `RtResult`/`RtResult<T>` 值结构受控错误通道——**用户源码错误零 throw**，
  仅穷尽 switch 的「未知变体」内部不变量与注册表越界用 InvalidOperationException。
- **UTF-16 对抗轴落地**：语言字符串大小比较走 `Value.CompareCodePoints`（Rune/码点序）——
  **C# `string.Ordinal` 是 UTF-16 unit 序，astral 与 U+E000..U+FFFF 的相对次序与码点序相反**，
  已有专门测试锁定 `\uE000 < \U00020000`（UTF-16 ordinal 会给出相反方向）；`==` 一律
  StringComparison.Ordinal；`len`=码点数（EnumerateRunes）；`bytes`=UTF-8；`char` 非法码点/代理区→U+FFFD；
  int/float 字符串解析一律 InvariantCulture。**每个测试类至少 1 条 astral 用例（🦀/𝕏）**。
- **输出面**：`println`/`print` 空格分隔（+换行）经 `ValueFormatter.Fmt(machine, v, top)` 钩子与
  `OutputSink` 落点——float 安全区/%q/JSON 字节契约是 A5 领地，运行时不自带格式化器。
- **TDD 与消融**：81 测试全绿（ValueTests 16 / HeapTests 10 / EnvTests 7 / MachineRefTests 13 /
  ChanTests 5 / SchedTests 5 / BuiltinTests 21 / DefsTests 4），期望值全部手算自 specs/conformance
  （如 chan FIFO 序、1024 容量、"channel 空" 逐字、int 截断 -2.7→-2、bytes("🦀")=F0 9F A6 80）。
  消融验证：向 `MapObj.Insert` 注入「覆盖也追加」bug → 3 条插入序测试（含 astral）立即变红 →
  恢复后 81 全绿，测试非空洞。
- **验证**：`dotnet test`（Lang.Runtime.Tests 81 全绿；Lang.Core.Tests 12 绿无回退）+
  `dotnet build -c Release -warnaserror`（Lang.Runtime / Lang.Runtime.Tests / Lang.Interp 及整个
  LangCs.slnx 0 警告 0 错误）+ `dotnet format --verify-no-changes`（三个项目 exit 0）。
- **Lang.Interp**：本波零源文件，留骨架——`run` 入口签名待 A1（Syntax）/A2（Semantic）/
  A3（Hir/IR）公共接口冻结后在下一波落地（AST 参考解释器 + IR 解释器双路，驱动已冻结的 Machine）。

### Runtime Model 裁定（与 lang-rs A4 相同的裁定，协调人知悉）

- **struct/enum 内联于 Value**：`CloneValue()` 即 VALUE-COPY-001；arena 索引只保留给**引用语义**
  对象（slice/map 堆、EnvArena、ChannelTable），各自独立 newtype，无 `dynamic`/object 装箱混用、
  禁 unsafe（csproj 已禁）、无 async 进入语义面。
- C# `record` 结构相等仅供内部/测试；语言级 `==` 必须走 `Value.EqualsLang`（RUNTIME-EQ-003：
  仅标量与 enum 可比，容器/struct/函数 → Eq 错）。注意 `Value.Enum` 的 Equals/GetHashCode
  为自定义逐元素实现（合成实现对 IReadOnlyList 是引用相等）——新增 enum 载荷相关变体时保持。

## 进行中

（无）

## 下一步

- ~~A1/A2 接口冻结后：Lang.Interp 落 AST 参考解释器骨架~~（第二波已交付：双解释器 +
  conformance 120/120 + 差分，见完成块）。
- ~~宿主内建 jsonParse/jsonStringify~~（第二波已随 InterpFmt.HostBuiltin 落地）。
- A5 对接：CLI `run`/`test` 接线可直接消费 `Interp.Run`（AST 路）与 `IrMachine.Run`；
  Lang.Verify 的 ast/ir 两差分路补齐后 differential 四路全比。
- A3 修复 IR-Lowering-临时槽缺口（见完成块 bug 登记与最小复现）后，补「无注解空数组 +
  索引 callee 间接调用」回归用例。
- SPEC-GAP-CS-001/002（见 SPEC-GAP 节）裁决后如需改口径，落点分别在 `Builtin.cs`（builtin_int）
  与 `Value.cs`（Compare/CompareFloat）一处。

## 阻塞

（无）

## 观察提示（非本域，交协调人）

1. **IR-Lowering-临时槽缺口**（A3 领地）：`let fns = []` + 索引 callee 间接调用 → validate
   拒绝，最小复现与修法候选见完成块「发现的 bug」节；真值套件不覆盖，120/120 不受影响。
2. 匿名 fn 值打印 AST `<fn 匿名>` vs IR `<fn <fnlit>>`——参考实现（TS interp / rust lower.rs）
   本身分歧，建议后续裁决统一口径（当前 C# 两路各随其参考，单元测试只锁 AST 路）。
3. 容器 `==` 在 C# 由 checker 静态拒绝（E3001，A2 行为）；lang-rs 为运行时 Eq 分类——同结论
   不同层，跨实现差分时注意。

## 给消费方的接口提醒

- **给 A5（CLI run/test / Lang.Verify 差分）**：`Interp.Run(stmts, analysis, sink)` 即 AST
  参考路（tests/Lang.Interp.Tests/ConformanceHarness.cs 的装载序可直接参考：合并语句流剥除
  import → Analyze → Run）；`IrMachine.Run(sink)` 即 IR 路（Compile 产物直接装载）。
  test 发现规则与 evalChunk 等价绕行按 lang-rs 第四波口径。
- **给 A3（HIR/IR）**：赋值地址组件→Place；`PlaceStore` 内部完成引用穿透与 ELEM-009 写回，
  解释器只需按 EVAL-ASSIGN-003 顺序「先地址组件成 Place → 再右值 → 再 PlaceStore」；
  `MapEntry` 的 Key 必须是规范 `MapKey`（经 `Machine.MapKeyOf`），写回经 `MapInsertKey` 保插入位。
  IR-Lowering-临时槽缺口（观察提示 1）修复后请同步补 lang-rs 侧核查。
- **给 A2（Semantic）**：预载定义 id 契约 `error=StructDefId(0)/Option=EnumDefId(0)/Result=EnumDefId(1)`，
  Option None case=1 是 `isNone` 语义依赖，程序表按名映射时不得重新注册预载名（lang-rs 第四波 bug 教训）。

## 已冻结接口登记

### 第二波冻结（2026-09-26 Lang.Interp）

- 冻结：`Lang.Interp.Interp`：构造 `new Interp(Lang.Runtime.Machine)`；实例方法
  `Run(IReadOnlyList<Lang.Syntax.Stmt>, Lang.Semantic.Analysis, OutputSink) → RtResult`
  （AST 参考解释器；前置 = analysis 由同一棵 stmts 分析而来，诊断非空拒绝执行）；
  实现 `TaskExec`（go 实参快照的同步执行；顶层 fn 定义处环境恒 Root）。静态常量
  `Interp.PreludeBuiltins`（20 名，与 `BuiltinRegistry.Core()` 锁名测试绑定）。
- 冻结：`Lang.Interp.IrMachine`：构造 `new IrMachine(Lang.Ir.IrProgram)`（装载 = 程序表
  struct/enum **按名映射预载三定义** error=StructDefId(0)/Option=EnumDefId(0)/Result=EnumDefId(1)、
  SetFnName、全局槽 `%g{i}` = GlobalId → Root 槽位号）；实例方法
  `Run(OutputSink) → RtResult`（init 先于 main，均可缺省）；实现 `TaskExec`。
- 冻结：`Lang.Interp.InterpFmt`：`ValueFormatter` 单例实现（`InterpFmt.Instance`）——
  打印/JSON 零漂移复用 `Lang.BackendGo.Fmt`/`Json` 纯函数；`static ToNode(Machine, Value) → FmtNode`
  桥（Option/Result/用户 enum/struct 按定义表展开、引用穿透 `&嵌套形`、chan `?`/内建
  `<内建 名>` 入口截获）；`static HostBuiltin(Machine, name, args) → RtResult<Value>`
  （jsonParse/jsonStringify，其余 Host 名报 UnsupportedHost）。两解释器统一经
  `Machine.CallBuiltin(sink, InterpFmt.Instance, …)` 消费输出面。
- 冻结（internal）：`Lang.Interp.Ops`——AST BinaryOp / IR Binop 公共运算键单份实现
  （`Of(…)` 双向映射、`Binary`、`Compound`）；语义 = i64 回绕、float IEEE（除零 Arith）、
  码点序字符串比较、RUNTIME-EQ-003 容器/函数不可比。两解释器共用，禁各自复制运算规则。

### 首批冻结（2026-09-26 Lang.Runtime）

- 冻结：`Lang.Runtime.Value`：abstract record + 嵌套 sealed record{Int(long V), Float(double V),
  Bool(bool V), Str(string V), Unit(静态 Default), Range(long Lo, long Hi), Slice(ValueId),
  Map(ValueId), Struct(StructVal), Enum(EnumDefId Def, uint Case, IReadOnlyList<Value> Payloads),
  Chan(ChannelId), Closure(FunctionId, EnvId), Builtin(BuiltinId), Ref(Place)}；
  `CloneValue()`=VALUE-COPY-001；`static EqualsLang(a,b)→RtResult<bool>`；`static Compare(a,b)→int?`
  （码点序，NaN 退化 0）；`Truthy()→RtResult<bool>`；`TypeName(DefTables)`/`TypeHint()`。
- 冻结：`Lang.Runtime.StructVal{Def: StructDefId, Fields: IReadOnlyList<Value>}`（自定义结构相等）；
  `MapKey`（readonly struct：FromInt/FromStr/FromBool/FromValue/ToValue/Plain，字符串 Ordinal）；
  `MapKeyKind{Int,Str,Bool}`。
- 冻结：`Lang.Runtime.ValueId/EnvId/ChannelId/StructDefId/EnumDefId/BuiltinId`
  （readonly record struct(uint Value)）；FunctionId 复用 `Lang.Core.FunctionId`。
- 冻结：`Lang.Runtime.HeapArena`：AllocSlice(IReadOnlyList<Value>)->Value、AllocMap(MapObj)->Value、
  Slice/Map（只读面）、SliceLen/SlicePush/SliceGet/SliceSet、MapLen/MapGet/MapHas/MapInsert/MapInsertKey/
  MapKeys/MapValues；`MapObj{Len,IsEmpty,Contains,TryGet,Insert,Slots}`（List+Dictionary 索引，
  插入序只来自 List）；`MapSlot(Key, Val)`；`HeapObj.Slice(List<Value>)/HeapObj.Map(MapObj)`。
- 冻结：`Lang.Runtime.EnvArena`：Root/Child/Define(env,name,value,mutable)->RtResult<uint>/
  Redefine/Lookup->RtResult<(EnvId,uint)?>/Get/Set/SetFieldWriteback/CellAt/ParentOf；`Cell{Value,Mutable}`。
- 冻结：`Lang.Runtime.Place`：abstract record{Var(EnvId,uint), Element(ValueId,uint),
  MapEntry(ValueId,MapKey), Field(Place,uint)}。
- 冻结：`Lang.Runtime.Machine`：属性 Heap/Envs/Chans/Sched/Defs/Builtins/Root；
  RegisterFunction/SetFnName/FnName；AllocSliceValue/AllocMapValue/MakeChanValue/SomeValue/NoneValue/
  OkValue/ErrValue/ErrorValue/IsNone/EnumCaseName/StructFieldNames；TakeRef/DerefValue/PlaceLoadResolved/
  PlaceLoad（不穿透）/PlaceStore（穿透+ELEM-009 写回）/MapKeyOf；Spawn(exec,func,args)/RunPending(exec)/
  Recv(exec,ch)；CallBuiltin(sink,fmt,id,args)。
- 冻结：`Lang.Runtime.ChannelTable`：Make/Send（满→ChanFull）/TryRecv（FIFO/Empty）/Len；
  `ChanCap=1024`、`ChanEmptyMsg="channel 空"`（逐字冻结）；`RecvOutcome(Payload,IsValue)`+Of/Empty。
- 冻结：`Lang.Runtime.Scheduler`：Pending/Enqueue/Pop；`Task{Func,Args}`（Spawn 内 CloneValue 快照）；
  `TaskExec.Call(Machine, FunctionId, IReadOnlyList<Value>)→RtResult<Value>`（返回值被调度器丢弃）。
- 冻结：`Lang.Runtime.BuiltinRegistry`：Core()/RegisterHost/Lookup/Kind/Name/CheckArity/Count；
  `BuiltinKind` 19 变体（含 Host）；`OutputSink.Write(string)`、`StdoutSink`；
  `ValueFormatter.Fmt(machine,v,top)`（A5 实现点）；`BuiltinDispatch.FmtArgs`；核心 20 名字面见完成块。
- 冻结：`Lang.Runtime.DefTables`：WithPrelude/RegisterStruct/RegisterEnum/StructDef/EnumDef/
  ErrorDef/OptionDef/ResultDef；预载 id：error=StructDefId(0)、Option=EnumDefId(0)、Result=EnumDefId(1)。
- 冻结：`Lang.Runtime.RtError(Kind,Message)` + `RtErrorKind`{Index,Arith,Eq,Assert,ChanFull,MatchNoArm,
  TypeCast,JsonParse,ImmutableAssign,NotIterable,Scope,UnsupportedHost,Internal}（RUNTIME-FAIL-001
  分类即契约，message 可变）+ `RtResult`/`RtResult<T>`（受控错误值通道；Map/Then/Ignore 组合子）。

## SPEC-GAP

- SPEC-GAP-CS-001（待协调人裁决，同 lang-rs SPEC-GAP-RS-001）：`int(float)` 越界/非有限行为 spec
  未冻结（RUNTIME-FAIL-001 未覆盖类型转换）。当前按三实现证据口径：向零截断、越界/NaN/Inf →
  long.MinValue（Go amd64）。若裁决不同只需改 `Builtin.cs::BuiltinInt` 一处。
- SPEC-GAP-CS-002（待协调人裁决，同 lang-rs SPEC-GAP-RS-002）：float 含 NaN 的 `==`/`<` 结果 spec
  未写死。当前按 IEEE：NaN==NaN false、`Compare` 退化 0（Equal）；`Value.Compare` 返回 `int?` 由解释器消费。

## 备注（需协调人集成轮处理）

- `tests/Lang.Runtime.Tests/` 与 `tests/Lang.Interp.Tests/` 均已按惯例 `dotnet sln LangCs.slnx add`
  登记进 solution 的 `/tests/` Folder，无需集成轮补登记。
- `Lang.Interp` 现引用 Lang.BackendGo（复用 Fmt/Json 冻结纯函数；csproj 引用为首批既有形态，
  本波未改 csproj）。
