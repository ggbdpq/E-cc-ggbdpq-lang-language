# A3-hir-ir 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

**2026-09-26 第二波交付（lowering：AST+语义 → HIR → IR，「源码 → IR」全链打通）**

- `Lang.Hir` 新模块 `Build.cs`（lang-rs build.rs 1:1 移植）：
  - 真实入口：`HirBuild.BuildHir(IReadOnlyList<Stmt>, Analysis) → LowerResult<HirProg>`
    （任务书入口名）；实现类 `HirBuilder : ILowerHir<IReadOnlyList<Stmt>>`（冻结签名零改动，
    `TAst = 装载器合并后的语句序列`）。构建失败 = 内部不变量破坏，以受控错误返回（不 throw 表达）。
  - NodeId 从 1 起先序递增；顶层收集序与 TS 证据一致：struct/enum（声明序交错）→ 顶层 let →
    非 method fn（首个 main 记为 main）+ extern（`with {IsExtern, ExternHost, Body=[]}`）→ method。
  - 类型事实三通道：表达式/模式用 `Analysis.TypeOf/PatType/MatchKindOf` 查询视图；let/for-in 用
    符号类型；参数/返回/字段/载荷注解经 `LangSemantic.ResolveAnnotation` 静默通道（A2 冻结入口）。
  - 方法调用解糖：recv 类型 Deref 后按 nominal def 反查 struct 符号 → `Methods[name]`；
    `NameSpan` 从 Field 节点 span 终点回退名字码点数（astral 安全，按码点计数）。
  - 闭包捕获集（VALUE-CAPTURE-004）：fnlit 体自由名（解析到外层 Var/Param、非全局、不在本层
    作用域）按首次出现序；嵌套 fnlit 传递捕获向外传播；具名 fn/fnlit 都是捕获边界（free 表
    save/clear/restore——漏 restore 会让捕获跨函数边界泄漏，单测 `FnLit_captures_free_locals_not_globals`
    锁定）。全局 let 名集合排除出捕获集。
  - **A2 记录缺口的两处事实恢复（同 lang-rs SG-1/SG-2，搬运面补全非新推断）**：①let/return/赋值
    顶层的 `?` 走 checker `InferTry` 直呼绕过 expr_ty 记录 → build 按 `InferTryOperand` 同一规则从
    操作数类型恢复 Result 的 ok 侧回填 `meta.type_id`；②extern 内层 FnDecl 不入 decl_syms → 按名
    回查 arena 首个同名 Fn 符号回填 `meta.sym`。A2 修复后两处可退化为断言。
- `Lang.Ir` 新模块 `Lowerer.cs`（lang-rs lower.rs 1:1 移植，`ILowerIr` 冻结签名零改动）：
  - `new IrLowerer(TypeTables, SymbolArena)` 实现三张 HIR 未展开映射：ctor 符号 → (enum, case)
    （NAME-CTOR-005 符号自带 owner+case 序）、fn/方法/嵌套 fn 符号 → fid、TypeId → enum/struct 名。
  - `Compiler.Compile(IReadOnlyList<Stmt>, Analysis) → LowerResult<IrProgram>` 新模块 `Compile.cs`：
    BuildHir → Lower → validate 全链（内部已 validate，错误消息分「HIR 构建 / IR lowering」两段，
    均为内部错误不含用户诊断）。
  - 函数表：HIR fns 全体先分配 fid 并登记 `fnSigBySym`（顶层 fn 互为前向可见；占位表在体下降
    完成前不可读，`expr_has_value` 的栈效应判定读此表）；嵌套 fn 预扫描分配（兄弟前向引用可达）；
    闭包/嵌套 fn 按遭遇序追加；方法 recv=第 0 参；extern 按 `ExternHost` 形态（无块）。
  - 表合成：structs/enums 声明序，**末尾恒追加预载 `error`/`Option`/`Result`**（载荷类型=泛型擦除
    null）；globals 声明序 + `init` 合成（声明序 GlobalSet；无全局缺省）；main 按 HIR main 节点 id 对认。
  - **降形决策表（与 lang-rs status 同名节逐条对齐，偏离 0）**：
    - 块模型两铁律：①多操作数构造（Binary/Range/Index/Slice、调用实参、Array/Map/Struct/Enum
      构造、赋值三元组）先落临时槽再统一压栈；②跨块存活的值驻局部槽（match/短路结果槽、scrut、
      迭代器、err 槽）。
    - 装箱纪律：装箱名集 = 体内所有 `FnLit.Captures` 名 ∪ `&x` 操作数名（名字集过近似，语义透明）；
      声明处 `BoxNew`、读 `Deref`、写 `RefSet`；装箱参数入口序言装箱；装箱循环变量逐轮 `BoxNew`
      重绑（CTRL-FORIN-003）；ClosureNew 按 captures 升序压 Ref，被调槽 i 接第 i 压。
    - 短路：`&&`/`||` → Branch + 短路常量块 + 结果临时槽 + Goto 汇合（无 Binary 形态）。
    - match：int → `SwitchInt`（源码臂序首中即跳，通配/绑定臂=default）；str/bool/float → `Eq` 链 +
      Branch；enum/Option/Result → `SwitchEnum`（arms=case 数平行，同 case 多臂候选链，未覆盖落
      通配块或 Unreachable）；子模式 Wild/Bind 直落、Lit→Eq 检查、嵌套 Ctor→递归 SwitchEnum；
      match 值性取节点类型（void 臂无结果槽）；无兜底字面量 match 的 fail 落 `Unreachable`。
    - `?` → `Inst.Try`（err 槽每函数一个 `<err>`；每 Try 一个 fail 块，函数收尾统一回填
      `LocalGet(err)→EnumNew(Result.Err)→Return{true}`）。
    - break/continue → `Goto`（循环栈：for-in continue→cond、break→done）；if/while/for-in 标准块图；
      终止语句后的同层语句降入死块（finish 时死块补 `Unreachable`、活块未终接=内部错误）；体走完
      未返回=隐式 `return ()`。
    - 赋值（EVAL-ASSIGN-003，**直接按 B1 修后冻结合同实现**）：整体目标直写（装箱经
      `LocalGet(addr)+RefSet` 穿透）；Field/Index 链目标走 `assign_path`：①路径 Index 表达式左到右
      落临时槽（先于右值）；②右值落临时槽；③压根槽 `LocalGet`/`GlobalGet` 直压（保 Place 溯源、
      **禁落临时槽**——B1 深拷贝教训），中间组件 `FieldGet`/`LocalGet(idx)+IndexGet` 带溯源不落槽；
      压栈序 = 冻结合同（FieldSet (obj,val)、IndexSet (obj,idx,val)）；复合赋值读-算-写经原槽
      （写回重压根链，两读间无副作用）；数组 `+=`（rhs 为 Slice）读终组件+右值 → `ArrayPush`
      原位拼接无需写回；全局目标同链处理。
    - 调用：用户 fn/方法 → `Call`（方法 recv 先于实参）；makeChan/send/recv → 专指令；其余内建 →
      `CallBuiltin`（has_ret 取调用点类型）；函数值/字段函数值 → `CallIndirect`（闭包值先压）；
      顶层 fn 一等引用 → 零捕获 `ClosureNew`。
    - 栈效应判定 `expr_has_value`：Call/MethodCall 查 `fnSigBySym` 的 has_ret；内建查单元面
      （println/print/assert/send 恒无值）；其余查节点类型（Unit/Unknown/Invalid/Never = 无值）。
    - 内建一等引用（`let f = println;`）与全局取引用（`&g`）→ 显式内部错误（IR 无形态，
      承接 lang-rs SG-3）。
- TDD（新增 43 测试，两项目 96 全绿）：
  - 降形专项 24（`LowerTests`）：短路 Branch 形态手对指令序列、装箱 BoxNew/Deref/RefSet、循环
    变量逐轮 BoxNew、Try+fail 块合成 Err 返回、match 三形态（SwitchInt/Eq 链/SwitchEnum 臂平行）、
    init 合成序、extern 形态、chan/spawn 专指令、方法 recv 第 0 参、CallIndirect、全指令覆盖源程序
    （37 Inst + 5 终接逐项断言）、确定性（两次 compile 逐字段相等）、死块、类型事实进表
    （`P.v: int` → `TypeId(1)`）；**B1 三教训直接锁定**：`c.n=41` 写原槽（手写全序列：
    根槽 LocalGet 直压 + 无 addr 临时槽往返）、`a[i()]=v()` 求值序（Call(idx) 先于 Call(v)）+ 压栈序
    （obj→idx→val，地址链无 LocalSet）、`c.n+=1` 复合赋值读-算-写经原槽（手写全序列）。
  - HIR 构建 5（`HirBuildTests`）：两次 build 逐字段相等、事实落节点（方法 recv 解糖/符号/类型）、
    Try 类型事实恢复（SG-1）、extern 符号按名回查（SG-2）、捕获集（fnlit 捕局部不捕全局）。
  - conformance 13 分类 **100 个 run case 全量** parse→analyze→compile→validate 全过（expressions 16 /
    syntax 13 / collections 11 / values 10 / match 9 / struct 8 / generics 6 / result 6 / concurrency 6 /
    module 5 / closure 4 / enum 3 / option 3）；另有 100 总量锁定测试（漏扫/静默跳过先红）；io（宿主
    服务面）与 diagnostics（不产 IR）不做，与任务范围一致。多文件 case 按 import 指令依赖先序展开
    合并（post-order DFS 防环，NAME-MERGE-010 装载序近似，module 5 case 含嵌套子目录全过）。
- 完成判据：4 个本域项目 `dotnet build -c Release -warnaserror` 0 警告 0 错误 +
  `dotnet format --verify-no-changes` 四项目通过；依赖面（Lang.Core/Syntax/Types/Semantic/Contract/
  Runtime/Loader/Interp/Edit/Formatter/Graph/Refactor/Verify/Conformance 及其测试）全量
  Release -warnaserror 复验不破坏。`LangCs.slnx` 全解决方案目前红于 `Lang.Cli`↔`Lang.BackendGo/Js`
  的 `GenError` API 不匹配（A5 第二波在途工作，目录未跟踪，与本波改动无关，见阻塞）。
- 依赖变更：`Lang.Hir.csproj` +Lang.Semantic（HIR 构建消费 Analysis，无环）。

**2026-09-26 首批交付（HIR 模型 + Core IR 指令集 + Validator + 胶水层 + lowering 入口冻结）**

- `Lang.Hir`（src/Lang.Hir/）：Typed HIR 节点模型全量落地（hir.rs 1:1）。程序/函数/语句/表达式/模式五级节点，
  每个重要节点内嵌 `NodeInfo { Id, Span, Sym?, TypeId? }`——HIR 是语义事实层，后端禁止重新推断。自带归一
  算符枚举（`UnaryOp` 3 / `BinaryOp` 16 / `AssignOp` 5 / `MatchKind` 4 / `LitKind` 4），不依赖 Lang.Syntax
  的 token 形态。`HirFacts.TypeOf(expr)` 读取节点类型结论。C# 形态 = abstract record + 嵌套 sealed record
  （契约 §3 双轨选项之一，switch 分派无 default 兜底，漏变体在运行期抛「未穷尽」异常由测试兜住）。
  含集合成员的 record 全部手写值相等/哈希（插入序逐元素），供确定性测试与下游 diff 用。
- `Lang.Ir`（src/Lang.Ir/）：Core IR 指令集全量落地（model.rs 1:1，**37 Inst + 6 Terminator**，逐变体清点；
  rust 侧 status 标题的「33 条」经其自身核对实为 37，本表按 37 登记）。形状 = arena/index 图 + 栈式基本块 +
  显式终接，不含 span（specs/source-span.md §2）。**带 Place 溯源的操作数**：IR 指令面即溯源载体——
  `LocalGet`/`GlobalGet` 产 `Place.Var` 溯源、`FieldGet`/`IndexGet` 链上扩展、`FieldSet`/`IndexSet`/
  `ArrayPush`/`RefSet` 写穿透；IR 侧冻结义务 = lowering 的地址组件禁落临时槽（lang-rs B1 教训），
  A4 解释器同构实现 `StackVal { Value, Place? }`（契约已写入 Model.cs 头注释并登记下方）。
- `Lang.Ir`：Validator（validate.rs 1:1）——纯线性扫描：引用越界（Local/Global/Function/Block/Struct/Enum/case）、
  签名一致（Call/Spawn argc、ClosureNew captures）、块终接（extern 无体/非 extern 至少一块）、栈效应
  （块入口 0、逐指令弹压平衡、终接处恰等消费量、终接后归零、SwitchEnum arms=case 数）。失败 = `IrError`
  （内部错误，定位 fn/block/inst，非用户诊断）。
- `Lang.Ir`：内建三方对照表 + 直接效果提取（builtins.rs 1:1）。`Builtins.Table` 20 行锁定
  「名 ↔ runtime BuiltinId ↔ 直接效果 ↔ IR 形态」（17 Call + 3 Inst：makeChan/send/recv 升专指令）；
  `InstDirectEffects`（Spawn→SPAWN、Chan 三指令→CHANNEL、CallBuiltin→映射、其余保守为纯）+
  `FnDirectEffects`（extern→FFI）。**过渡决策**：`Effect` 枚举与 `BuiltinDirectEffect` 映射暂驻 Lang.Ir
  （Lang.Contract 未就位，A6 落地后删除改消费）；runtime 锁名测试待 Lang.Runtime 落地后补
  （lookup/name 双向断言，lang-rs 先例），当前以常量表 + status 登记为准。
- `Lang.Ir`：确定性文本 dump（dump.rs 1:1，格式逐字节同形）。golden 2 例手写锁定 + 两次 dump 逐字节相等 +
  astral 转义（🦀𝕏 代理对不拆开）+ 浮点最短往返（整值补 .0；inf/NaN 用 rust 拼写；指数拼写为 C# "R" 形——
  调试视图非契约，已注释声明）。声明名普通引号原样，仅 const.str/call.builtin/field 名走转义。
- lowering（AST→HIR→IR）本波只冻结入口签名不实现（语义核就绪后下一波）：`Lang.Hir.ILowerHir<TAst>`、
  `Lang.Ir.ILowerIr`、`Lang.Ir.EntryPoints`、`Lang.Hir.LowerResult<T>`（值/受控错误二选一，不 throw）。
- TDD（53 测试全绿）：validator 红→绿（悬垂 Local/Function/Block/Global/Struct/Enum case、arms 长度、
  空块、栈下溢、Return 残栈、Branch 消费恰 1、Spawn/Closure 个数不符、Call 实参下溢、extern 带体、
  locals < 捕获+参数、错误定位三元组、未知名 CallBuiltin 放行、astral 全绿）；`full_feature_program`
  全指令覆盖活文档（37 Inst + 6 Terminator 逐项 + 全 16 Binop + unary.not 助记符）；确定性
  （同构造两次逐字段相等 + 变异可感知 + dump 逐字节）；效果面（5 带效果指令 + 32 无效果逐条枚举 + extern）。
- 完成判据：两 src + 两 tests 项目 `dotnet build -c Release -warnaserror` 0 警告 0 错误 +
  `dotnet format --verify-no-changes` 四项目通过 + `LangCs.slnx` 全解决方案 Release -warnaserror 0 错误
  （不破坏兄弟项目，A1/A2/A5 并行代码同批验证）。测试项目 `tests/Lang.Ir.Tests`（49）/`tests/Lang.Hir.Tests`（4）
  已登记进 slnx。

## 进行中

（无）

## 下一步

- ~~下一波 lowering：AST→HIR→IR 全链~~（第二波已交付，见完成块）。
- A4 对接：消费 `IrProgram`（入口 `EntryPoints{Init,Main}`，init 先执行）；操作数栈条目带 Place 溯源
  （见下方登记）；`CallBuiltin.HasRet` 已静态编码调用点值性，解释器无需重推；降形对接要点同
  lang-rs 第三波「A4/A5 对接要点」（Call/Spawn/StructNew/EnumNew 按声明/调用序压栈、
  ClosureNew 捕获槽序、无值函数体走完 = Return{false}）。
- A5 对接：dump 格式非跨版本契约；转译消费 `IrProgram` 表序。
- A6 对接：效果引擎接线（遍历 blocks 逐指令 `InstDirectEffects` + `FnDirectEffects` + 调用边）；
  Lang.Contract 就位后本侧删除过渡 Effect，改消费 Lang.Contract（名与契约序已 1:1）。
- A2 修复 checker 记录缺口（SG-1/SG-2）后，本侧 Build.cs 的两处事实恢复可退化为断言。

## 阻塞

1. **观察（不阻塞本域）**：`LangCs.slnx` 全解决方案 Release 构建当前红于 `Lang.Cli`（Commands.cs
   177/181/186 行）↔ `Lang.BackendGo`/`Lang.BackendJs` 的 `GenError` API 不匹配——A5 第二波在途
   工作（三个目录均未跟踪、并行开发中），不在本 Agent 领地，未触碰；本域 4 项目 + 全部依赖面
   项目已逐一复验 Release -warnaserror 干净。
2. 无其他阻塞。观察两条同首批：① runtime 锁名测试待 Lang.Runtime 落地后补；② C# record 嵌套
   类型参数名冲突改名（CS8866）见偏离点。

## 已冻结接口登记

- 冻结：`Lang.Hir.HirProg`：`{ Structs, Enums, Globals, Fns, Main }` 五表语义树；每重要节点带
  `NodeInfo { Id: NodeId, Span, Sym: SymbolId?, TypeId: TypeId? }`。
- 冻结：`Lang.Hir` 算符归一：`UnaryOp{Neg,Not,Ref}`、`BinaryOp` 16（无 `&&`/`||`——以 `HirExpr.AndThen`/
  `OrElse` 表短路）、`AssignOp` 5、`MatchKind{Literal,Enum,Result,Option}`、`LitKind{Int,Float,Str,Bool}`。
- 冻结：`Lang.Hir` 节点形状：`HirFn{Meta,Name,Recv?,TypeParams,Params,Ret?,Body,IsExtern,ExternHost}`、
  `HirStmt` 10 变体（Let/Assign/Eval/Return/If/While/ForIn/Break/Continue/Spawn/FnStmt）、`HirExpr` 20 变体、
  `HirPat` 4 变体（Wild/Bind/Lit/Ctor）、`HirFacts.TypeOf`。分派 = switch expression 无 default 兜底。
- 冻结：`Lang.Hir.ILowerHir<TAst>`：`LowerResult<HirProg> Lower(TAst ast)`（`TAst` = A1 程序节点；
  NodeId 先序递增；同输入逐字段同输出）。`LowerResult<T>{Value,Error}` 值/受控错误二选一。
- 冻结：`Lang.Ir.IrProgram`：`{ Structs, Enums, Globals, Functions, Init: FunctionId?, Main: FunctionId? }`；
  FunctionId = Functions 下标。IR 侧新增 id：`BlockId/GlobalId/StructId/EnumId`（uint newtype，形状同 Lang.Core）。
- 冻结：`Lang.Ir.IrFunction`：`{ Name, CaptureCount, Sig: FnSig{ParamCount,HasRet}, Locals, Blocks, ExternHost? }`；
  locals 布局 = `[捕获槽][参数槽][局部/临时]`；extern 无块。`Block{Insts, Term}`：块入口栈深 0、终接后归零。
- 冻结：`Lang.Ir` IR 指令集（`Inst`，37 变体；栈效应 in→out；弹序 = 压序逆序；与 lang-rs 同名同栈效应）：
  - 常量：`ConstI64(long)`、`ConstF64(double)`、`ConstBool(bool)`、`ConstStr(string)`（均 0→1）
  - 局部/全局：`LocalGet(LocalId)` 0→1、`LocalSet(LocalId)` 1→0、`GlobalGet(GlobalId)` 0→1、`GlobalSet(GlobalId)` 1→0
  - 运算：`Unary(Unop{Neg,Not})` 1→1、`Binary(Binop` 16 种`)` 2→1、`Pop` 1→0
  - 调用：`Call(FunctionId)` in=目标参数数 out=目标 HasRet、`CallIndirect{Argc, HasRet}`（先压闭包值再压实参）、
    `CallBuiltin{Name, Argc, HasRet}`（名不做名单硬校验）、`Spawn{Func, Argc}` out=0、
    `ClosureNew{Func, Captures}`（弹 Captures 个 Ref，按 captures 升序压栈，被调槽 i 接第 i 压）
  - 索引/切片/字段：`IndexGet` 2→1（obj, idx）、`IndexSet` 3→0（obj, idx, val）、`SliceGet` 3→1（obj, lo, hi）、
    `FieldGet{Field}` 1→1、`FieldSet{Field}` 2→0（obj, val）、`ArrayPush` 2→0（arr, val）
  - 构造：`ArrayNew{Count}` in=Count、`MapNew{Count}` in=2×Count（k1,v1,k2,v2…=插入序）、
    `StructNew{StructId}` in=声明字段数（声明序压栈）、`EnumNew{EnumId, Case}` in=case 载荷数、`RangeNew` 2→1（lo, hi）
  - 引用/装箱：`BoxNew` 1→1、`Deref` 1→1（Ref→指称副本）、`RefSet` 2→0（ref, val 写穿透）
  - match 载荷：`EnumPayloadGet{Index}` 1→1
  - for-in：`IterNew` 1→1、`IterNext{Done: BlockId}` 1→1（耗尽跳 Done；迭代器驻局部槽）
  - chan：`ChanNew` 0→1（缓冲 1024 语言常量）、`ChanSend` 2→0（ch, val）、`ChanRecv` 1→1（压 Result[T,error]）
  - `?`：`Try{Err: LocalId, Fail: BlockId}` 1→1（Ok 压解包值；Err 存 err 槽跳 fail 块合成 Err 返回）
- 冻结：`Lang.Ir` 终接指令集（`Terminator`，6 变体）：`Goto{Target}` 0→0、`Branch{IfTrue, IfFalse}` 1→0、
  `SwitchInt{Arms: (long, BlockId)[], Default?}` 1→0（源码臂序首中即跳）、`SwitchEnum{EnumId, Arms 与 case
  声明序平行且长度强制相等, Default?}` 1→0、`Return{HasValue}` in=HasValue、`Unreachable`。
  字符串/bool 字面量 match 降为 Binary(Eq) 链 + Branch（不设 SwitchStr/SwitchBool）。
- 冻结：`Lang.Ir.Validator.Validate(IrProgram): IrError?`（null = 合法）：纯线性扫描结构校验；
  `IrError{Function, Block?, Inst?, Message}` 内部错误非用户诊断，IR 不含 span。
- 冻结：`Lang.Ir.Builtins.Table`：内建三方对照表 20 行
  `BuiltinEntry{Name, RuntimeId: uint, DirectEffect: Effect?, Form: IrBuiltinForm{Call, Inst}}`，
  顺序 = runtime core 注册序（BuiltinId 序）；17 Call + 3 Inst（makeChan=14/send=15/recv=16）。
  测试锁形态分布与效果列；runtime 锁名测试待 Lang.Runtime 落地后补。
- 冻结：`Lang.Ir.Builtins.InstDirectEffects(Inst): IReadOnlyList<Effect>`：Spawn→SPAWN、
  ChanNew/ChanSend/ChanRecv→CHANNEL、CallBuiltin→映射（http.get→NET 生效；未知/纯名空）、其余空
  （新增效果面指令必须登记，缺省保守为纯）。`FnDirectEffects(IrFunction)`：extern→FFI，普通函数空。
- 冻结（过渡，A6 就位后迁移）：`Lang.Ir.Effect` 9 变体（契约序 PURE,IO,FS,NET,TIME,RANDOM,SPAWN,CHANNEL,FFI）
  与 `Builtins.BuiltinDirectEffect(string)`（println/print→IO、http.get→NET、makeChan/send/recv→CHANNEL、
  其余 null）；Lang.Contract 落地后本侧删除、改消费 Lang.Contract 同名面。
- 冻结：`Lang.Ir.IrDumper.DumpProgram / DumpFunction`：确定性文本（同程序两次逐字节相等）；
  **调试视图非跨版本契约**；不含 span/TypeId；格式与 lang-rs dump 逐字节同形（golden 2 例锁定；
  浮点指数拼写为 C# "R" 形，已注释声明）。
- 冻结：`Lang.Ir.ILowerIr`：`LowerResult<IrProgram> Lower(HirProg hir)`；`EntryPoints{Init, Main}`；
  `IrLowerError{Message}`。
- 冻结（IR 侧值面契约，A4 同构实现）：操作数栈条目 = `StackVal { Value, Place? }`；
  `Place = Var(env, slot) | Element(slice, index) | MapEntry(map, key) | Field{Base, Index}`。
  LocalGet/GlobalGet 压入带 Var 溯源条目；FieldGet/IndexGet 带溯源消费时扩展链；
  FieldSet/IndexSet/ArrayPush/RefSet 要求目标条目带溯源、写穿透原槽/容器；lowering 冻结义务 =
  赋值地址组件经 LocalGet/GlobalGet 直压 + FieldGet/IndexGet 链上栈，禁落临时槽（B1 教训）。

### 第二波冻结（2026-09-26 lowering）

- 冻结：`Lang.Hir.HirBuild.BuildHir(IReadOnlyList<Stmt>, Analysis) → LowerResult<HirProg>`：
  AST+语义产物 → Typed HIR 静态门面（前置：analysis 由同一棵 stmts 分析而来且无诊断）；
  实现类 `Lang.Hir.HirBuilder : ILowerHir<IReadOnlyList<Stmt>>`（`TAst = 合并后语句序列`，
  `LowerSlice` 切片入口同管线）。NodeId 从 1 起先序递增；同输入逐字段同输出（确定性契约）。
- 冻结：`Lang.Ir.IrLowerer(TypeTables, SymbolArena) : ILowerIr`：HIR → IR 实现；`types`/`symbols`
  必须来自产生该 HIR 的同一次分析；产物保证过 `Validator.Validate`（`Lower` 内部先 validate 再返回）。
- 冻结：`Lang.Ir.Compiler.Compile(IReadOnlyList<Stmt>, Analysis) → LowerResult<IrProgram>`：
  「源码 → IR」全链便利入口（AST→HIR→IR，内部已 validate）；失败全为内部错误
  （错误消息前缀「HIR 构建 / IR lowering」），不含用户诊断。
- 冻结（依赖面）：`Lang.Hir` → `Lang.Semantic`（HIR 构建消费 `Analysis` 查询视图与
  `LangSemantic.ResolveAnnotation` 静默注解通道；无环）。
- 冻结（HIR 构建的事实恢复规则，A2 修复后退化为断言）：①let/return/赋值顶层 `?` 节点的
  `meta.type_id` 按操作数类型取 `Result.Ok`（操作数非 Result 时不补，与 checker 推断一致）；
  ②extern 内层 FnDecl 的 `meta.sym` 按 arena 声明序首个同名 Fn 符号回填。

## 与 lang-rs 的偏离点（语义面应为 0）

1. `HirExpr.MethodCall` 的字段名参数命名 `FieldName`（lang-rs 为 `field`）：C# record 嵌套类型的
   位置参数名与兄弟嵌套类型 `HirExpr.Field` 同名触发 CS8866，改名无语义影响（同为 string? 可空字段名）。
2. 计数与下标（ParamCount/CaptureCount/Argc/Count/Case/Index）用 C# `int`（lang-rs u16/u32）：宽度差异，
   无栈效应/语义影响；id 一律 uint newtype（与 Lang.Core 冻结集同形）。
3. 效果枚举与内建映射暂驻 Lang.Ir（lang-rs 在 lang-contract）：Lang.Contract 未就位的过渡形态，
   名与契约序 1:1，A6 落地即迁移。
4. dump 浮点指数拼写跟随 C# "R"（lang-rs `{:?}`）：调试视图非契约，golden 已锁定本侧格式。
5. （第二波新增，无语义面偏离）HIR/IR 降形实现内部以私有异常类型做控制流短路，公开面一律返回
   `LowerResult` 受控错误（契约 §5 形态）；lang-rs 同位置为 `Result`/`panic!`。

## SPEC-GAP

（无新增。lang-rs 侧 SG-1/SG-2（A2 checker 记录缺口）已在本波 Build.cs 落地同款事实恢复
（Try ok 侧类型 / extern 符号回查，见第二波冻结登记），A2 修复后可退化为断言；SG-3
（内建一等引用 / 全局取引用无 IR 形态）本波降形在触达点显式报内部错误，语义面仍待冻结；
以上三条清单与 lang-rs status 完全一致，C# 侧未新增发现。）
