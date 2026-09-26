# A3-hir-ir 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

### 2026-09-25 集成轮修复（B1：普通赋值降形两缺陷，协调人授权集成工程师执行）

- **根因一（压栈序违契）**：`assign` 对 Field/Index 目标的普通赋值早退路径把右值
  先压栈（[val, obj(, idx)]），违反冻结合同 FieldSet 2→0 (obj,val) / IndexSet
  3→0 (obj,idx,val)；同函数复合赋值路径却是正确序，内部不一致。
- **根因二（地址组件落临时槽深拷贝）**：地址组件经 `local.get+local.set` 落临时槽
  （铁律①误用于单块地址组件），struct 值按 VALUE-COPY-001 复制 → FieldSet/IndexSet
  写在副本上，原绑定不变（`c.n=41` 后读 0）。
- **修法**（`lower.rs` 重写赋值降形）：目标分解为 Ident 根 + Field/Index 链
  （`split_assign_path`，checker 保证）。整体目标（无链）路径原样保留。链目标走
  `assign_path`：①路径 Index 表达式从左到右求值落临时槽（EVAL-ASSIGN-003 地址
  组件先于右值）；②右值统一落临时槽（多块右值安全）；③压根槽 `LocalGet`/
  `GlobalGet` 直压（操作数带 `Place::Var` 溯源；装箱槽原样压 Ref，`place_store`
  写穿透原槽/全局），中间组件 `FieldGet`/`LocalGet(idx)+IndexGet` 带溯源不落槽，
  终组件压 (idx,)val 后 `FieldSet`/`IndexSet`——压栈序 = 冻结合同。复合赋值同构
  （读-算-写经原槽，两读间无副作用）；数组 `+=`（is_concat）读终组件 + 右值 →
  `ArrayPush` 原位拼接，无需写回；`GlobalGet` 溯源即根 env 槽，全局目标同链处理。
- **证据**：`cargo test -p lang-ir` 63 全绿（原测试只锁指令种类，无需改）；
  lang-interp 双路差分 `diff_matrix_ast_vs_ir`（原 #[ignore] 11 例阻断）解禁后
  120 例全绿；conformance ir 路 109p/11f → **120p/0f**；go/js 路随之暴露 4 个
  后端链写缺口（详见 A5 集成轮小节），修后 go/js 亦 120p/0f。
- 回归验证（修法档案要求项）：`a[i()]=v()`/`m[kmark()]=vmark()` 求值序与压栈序、
  `c.n=41` 写原槽、`a[i].j=v` 嵌套链、`c.n+=1` 复合、map/slice 基底写穿——全部由
  conformance 对应 case（eval-assign-index-from-call / struct-field-write /
  containers-shared-aliasing / reference-read-write 等 11 例）逐字节锁定。

**2026-09-25 第三波交付（lowering：AST+语义 → HIR → IR，「源码 → IR」全链打通）**

- `lang-hir`（build.rs 两处事实恢复，均因 A2 记录缺口、非新推断）：
  - extern 内层 FnDecl 不入 resolver `decl_syms` → 按名回查 arena Fn 符号回填
    `meta.sym`（extern fn 名顶层唯一，首中确定）；
  - checker 对 let/return/赋值顶层的 `?` 走 `infer_try` 直呼、绕过 `expr_ty`
    记录，Try 节点无类型结论 → 按 `infer_try_operand` 同一规则从操作数已记录
    类型恢复 Result 的 ok 侧回填 `meta.type_id`。**两条都是 lang-semantic 侧
    记录缺口，建议 A2 修复后本处恢复逻辑可退化为断言**（SPEC-GAP 候选 SG-1/SG-2）。
  - 新增 `HirBuilder::lower_slice(&[Stmt])`：与 `LowerHir::lower` 同管线，
    compile 入口免克隆 AST。
- `lang-ir` 新模块 `lower.rs`（重写，`LowerIr`/`EntryPoints` 冻结签名零改动）：
  - **`IrLowerer::new(&TypeTables, &SymbolArena)`** 实现 `LowerIr`
    （`Error = LowerError`）；三张 HIR 未展开映射：ctor 符号→(enum, case)
    （NAME-CTOR-005 符号自带 owner+case 序）、fn/方法符号→fid、
    TypeId→enum/struct 名。
  - **`compile(stmts: &[Stmt], &mut Analysis) -> Result<IrProgram, CompileError>`**
    （新模块 `compile.rs`）：build_hir→lower→validate 全链；`CompileError::
    Build|Lower` 全为内部错误（用户诊断已在 semantic 发射）；lower 内部先
    validate 再返回，产物保证过 validate。
  - 依赖变更（lang-ir/Cargo.toml）：+lang-types、+lang-semantic、+lang-syntax
    （无环；语义表与 Stmt 为 lowering 事实输入）。
- **降形决策表**（证据=specs/ + TS interp/irinterp 语义锚点）：
  - 块模型两铁律（validator「块入口栈深 0」推论）：①多操作数构造（Binary/
    Range/Index/Slice、调用实参、Array/Map/Struct/Enum 构造、赋值三元组）
    一律先落临时槽再统一压栈——任一操作数可能是多块形态（match/短路/`?`）；
    ②跨块存活的值一律驻局部槽（match/短路结果槽、scrut、迭代器、err 槽）。
  - 函数表：HIR fns 全体先分配 fid 并登记（顶层 fn 互为前向可见；签名同时
    登记进 `fn_sig_by_sym`——占位表在体下降完成前不可读，`expr_has_value`
    的栈效应判定读此表）；嵌套 fn 预扫描分配（兄弟前向引用可达），体在语句
    位下降；闭包按遭遇序追加。方法 recv=第 0 参（`sig.param_count` 含 recv）。
  - 表合成：structs/enums 声明序，**末尾恒追加预载 `error`/`Option`/`Result`**
    （名字查表需要总表；预载载荷类型=泛型擦除 `None`）；globals 声明序 +
    `init` 合成（声明序 GlobalSet；无全局缺省；main 按 HIR main 节点 id 对认）。
  - 装箱纪律：装箱名集 = 体内所有 `FnLit.captures` 名 ∪ `&x` 操作数名（名字
    集过近似，同名兄弟绑定多装箱一次堆分配、语义透明）；装箱绑定声明处
    `BoxNew`、读 `Deref`、写 `RefSet`；装箱参数入口序言装箱；装箱循环变量
    逐轮 `BoxNew` 重绑（CTRL-FORIN-003）；**ClosureNew 约定（A4 对接）：Ref
    按 captures 升序压栈，被调捕获槽 i 接第 i 次压入的 Ref**。
  - 短路：`&&`/`||` → Branch（左值弹 1）+ 短路常量块 + 结果临时槽 + Goto 汇合
    （EVAL-SHORTCUT-006；无 Binary 形态）。
  - match：int → `SwitchInt`（源码臂序首中即跳，通配/绑定臂=default）；str/
    bool/float → `Eq` 链 + Branch（fail 链逐臂顺延）；enum/Option/Result →
    `SwitchEnum`（arms=case 数，未覆盖 case 落通配块或 Unreachable；同 case
    多臂按源码序候选链）；子模式：Wild/Bind 直落、Lit→Eq 检查链、嵌套
    Ctor→递归 SwitchEnum；**match 值性取节点类型**（checker 已统一臂型），
    void 臂（`Ok(v) => println(v)`）无结果槽；无兜底字面量 match 的 fail 落
    `Unreachable`（运行时 RUNTIME-MATCH-001 由消费方解释）。
  - `?` → `Inst::Try`（err 槽每函数一个 `<err>`；每 Try 一个 fail 块，函数
    收尾统一回填 `LocalGet(err)→EnumNew(Result.Err)→Return{true}`）。
  - break/continue → `Goto`（循环栈：for-in continue→cond 块、break→done 块）；
    if/while/for-in 标准块图；控制流终止语句后的同层语句降入死块（finish 时
    死块补 `Unreachable`、活块未终接=内部错误）；体走完未返回=隐式
    `return ()`。
  - 赋值（EVAL-ASSIGN-003）：地址组件→右值→读旧值→运算→写回；装箱目标
    经地址临时槽 + `Deref`/`RefSet` 穿透；数组 `+=`（rhs 为 Slice）→
    `ArrayPush` 原位拼接（装箱目标无需写回）；Field/Index 复合赋值运算结果
    落临时槽再按弹序压栈写回。
  - 调用：用户 fn/方法 → `Call`（recv 先于实参，EVAL-CALL-002）；makeChan/
    send/recv → 专指令（冻结约定）；其余内建 → `CallBuiltin`（has_ret 见
    下）；函数值/字段函数值 → `CallIndirect`（闭包值先压）；顶层 fn 一等
    引用 → 零捕获 `ClosureNew`。
  - 栈效应判定 `expr_has_value`：Call{func}/MethodCall{method} 查
    `fn_sig_by_sym` 的 has_ret；Call{builtin} 查单元面
    （println/print/assert/send 恒 `()`，与 `builtin_return` 一致）；其余查
    节点类型（Unit/Unknown/Invalid/Never = 无值，保守侧与栈平衡一致）。
- TDD（红→绿，共 34 新测试）：降形专项 21（短路 Branch 形态手对指令序列、
  装箱 BoxNew/Deref/RefSet、循环变量逐轮 BoxNew、Try+fail 块合成 Err 返回、
  match 三形态、init 合成序、extern 形态、chan/spawn 专指令、方法 recv 第
  0 参、CallIndirect、全指令覆盖源程序（33 Inst+5 终接逐项断言）、确定性
  两次逐字段相等、死块、`&` 直压 Ref）；conformance 13 分类 **100 个 run
  case 全量** parse→analyze→compile→validate 全过（expressions 16 / syntax 13
  / collections 11 / values 10 / match 9 / struct 8 / generics 6 / result 6 /
  concurrency 6 / module 5 / closure 4 / enum 3 / option 3；io 需宿主 http、
  diagnostics 不产 IR，不做）。多文件 case 按 import 指令依赖先序展开合并
  （NAME-MERGE-010 装载序近似，module 5 case 含嵌套子目录全过）。
- 完成判据：`cargo test -p lang-hir -p lang-ir` 68 全绿（hir 5 + ir 63）+
  fmt + `clippy -p lang-ir -p lang-hir --all-targets -- -D warnings` 干净 +
  `cargo check --workspace` 不破坏兄弟 crate。
- **A4/A5 对接要点**：Call/Spawn/StructNew/EnumNew 实参/载荷「按声明/调用序
  压栈」→ 运行时按弹序逆序装入；ClosureNew 捕获槽序见上；`expr_has_value`
  语义已编码进 `CallBuiltin.has_ret`/`CallIndirect.has_ret`，解释器无需重推；
  无值函数体走完 = Return{false}（unit）。

**2026-09-25 第二波交付（IR 胶水层：内建对照表 / 直接效果提取 / 文本 dump）**

- `lang-ir` 新模块 `builtins.rs`：
  - **内建三方对照表** `BUILTINS: &[BuiltinEntry]`（常量表，20 行 = runtime core 注册表全量，
    顺序 = `BuiltinId` 序）：一行锁定「内建名 ↔ runtime BuiltinId ↔ 直接效果 ↔ IR 形态」。
    形态分布：17 名 `IrBuiltinForm::Call`（`CallBuiltin{name}`）+ 3 名 `IrBuiltinForm::Inst`
    （makeChan→ChanNew、send→ChanSend、recv→ChanRecv）。
  - 测试经 runtime 公共 API 锁名（`BuiltinRegistry::core()` 的 `lookup`/`name` 双向断言 +
    id 0..=19 可查、20 越界断言注册表恰 20 项），效果列与 `lang_contract::builtin_direct_effect`
    逐项断言——三方各自演进时本表测试先红。
  - **直接效果提取**：`inst_direct_effects(&Inst) -> Vec<Effect>`——Spawn→SPAWN、
    ChanNew/ChanSend/ChanRecv→CHANNEL、`CallBuiltin{name}`→`lang_contract::builtin_direct_effect(name)`
    （http.get→NET 在此路径生效；未知名/纯名 = 空）、其余指令空（保守缺省：新增效果面指令漏登记
    不会虚增效果）。`fn_direct_effects(&IrFunction) -> Vec<Effect>`——extern 声明→FFI（A6 登记
    的「extern→FFI 由 IR 接线方挂」承接点）；普通函数空，体内指令效果由消费方遍历
    `blocks[*].insts` 逐条取。测试覆盖全部 5 条带效果指令 + 32 条无效果指令逐条枚举 + extern。
- `lang-ir` 新模块 `dump.rs`：IR 确定性文本 dump。`dump_program(&IrProgram) -> String` +
  `dump_function(&IrFunction, FunctionId) -> String`。**调试视图，非跨版本契约**（模块头注释声明）；
  同一 IrProgram 两次 dump 逐字节相等（表序遍历、浮点 `{:?}` 最短往返、字符串 `escape_debug`）；
  **不含 span**（specs/source-span.md §2 既有注明，代码注释呼应），亦不打印 TypeId。
  格式：`entry init/main=` 头行 → `struct <i> "<名>" <T,...> fields=n`（field 子行）→
  `enum`（case 子行 payloads=n）→ `global <i> "<名>" mut|imm` → `func <i> "<名>" captures/params/ret`
  或 `func <i> "<名>" extern="<宿主名>"` → 块 `block <b>:` 缩进 2 + 指令缩进 4
  （助记符点分层：`const.i64/local.get/binary.add/call.builtin "println" argc=1 ret=0/spawn 0 argc=1/
  switch.int 0->1 default=3/switch.enum 0 0->1 1->2 default=-` 等）。
  golden 测试 2 例（期望文本手写，且都先过 `validate` 再比对逐字节）+ 确定性断言 + 转义断言。
- 依赖变更（lang-ir/Cargo.toml）：新增 `lang-contract`（效果枚举与内建映射的单一事实源，无环）；
  dev-dependency `lang-runtime`（仅测试锁名用）。
- **lowering 入口保持冻结不动**（`LowerHir`/`LowerIr`/`EntryPoints` 签名零改动）。匹配度评估：
  - `LowerHir::Ast` 关联类型留白正确——A2 语义结论可经实现方 self 状态或组合 Ast 类型
    （AST + 符号/类型表）传入，`&mut self` 已容纳；无签名缺口。
  - `HirExpr::Call` 已是三选一静态结论（func/builtin/callee），lowering 可直接分派
    `Call/CallBuiltin/CallIndirect`，无需 IR 侧改形态。
  - `LowerIr`：HIR 是语义事实层（NodeInfo 带 sym/type_id），IrProgram 纯结构，搬运即可；
    `HirProg` 无 init——由 lowering 从 globals 声明合成 init 函数并回填
    `IrProgram.init` + `EntryPoints`，签名无需改。

## 2026-09-25 首批交付（HIR/IR 模型 + Validator + lowering 入口冻结）

- `lang-hir`：Typed HIR 节点模型全量落地（`src/hir.rs`）。程序/函数/语句/表达式/模式五级节点，每个重要节点内嵌 `NodeInfo { id: NodeId, span: Span, sym: Option<SymbolId>, type_id: Option<TypeId> }`——HIR 是语义事实层，后端禁止重新推断。自带归一算符枚举（`UnaryOp` 3 / `BinaryOp` 16 / `AssignOp` 5 / `MatchKind` 4 / `LitKind` 4），不依赖 lang-syntax 的 token 形态（A1 未就绪也不阻塞）。`type_of(&HirExpr) -> Option<TypeId>` 读取节点类型结论。
- `lang-ir`：Core IR 指令集全量落地（`src/model.rs`），形状 = **arena/index 图 + 栈式基本块 + 显式终接**，不含 span（specs/source-span.md §2）。33 条指令 + 6 条终接（清单见下方冻结登记）。
- `lang-ir`：IR Validator（`src/validate.rs`）——线性扫描校验 LocalId/GlobalId/FunctionId/BlockId/StructId/EnumId 引用越界、Call/Spawn 实参个数 vs 目标签名、ClosureNew 捕获数 vs 目标 capture_count、块终接与栈效应（块入口栈深 0、逐指令弹压平衡、终接处栈深恰等于终接消费量、终接后归零）、extern 函数无函数体、局部表 ≥ 捕获+参数槽。失败形态 `IrError`（内部错误，非用户诊断）。
- TDD：手构 IR 程序红→绿（悬垂 LocalId/FunctionId/BlockId/GlobalId/StructId/enum case、空块、栈下溢、Return 残栈、Spawn/Closure 个数不符、SwitchEnum 臂数不符、extern 带体、Try 引用非法全部被拒；合法程序与全指令覆盖程序通过）。HIR/IR 各有「同构造两次逐字段相等」确定性测试 + `full_feature_program` 全指令覆盖活文档。
- lowering 入口冻结（不实现）：`lang_hir::LowerHir`（AST→HIR，`Ast` 关联类型留给 A1 绑定）、`lang_ir::LowerIr`（HIR→IR）、`lang_ir::EntryPoints`（init/main）。

## 进行中

（无）

## 下一步

- A4 IR 解释器：消费 `IrProgram`（单文件程序：函数表+全局+init；入口 `EntryPoints{init, main}`，有 init 先执行再执行 main）。对接要点见第三波交付「A4/A5 对接要点」行。
- 效果引擎接线（A6 消费方）：遍历 IrProgram——每函数 `add_fn` 后，`fn_direct_effects`（extern→FFI）+ 体内逐指令 `inst_direct_effects` 结果 `set_direct`，调用边（Call/Spawn/CallIndirect 目标）`add_call`；Spawn 的被调效果沿边保守传播与 lang_contract 现有测试一致。
- A2 修复 checker 记录缺口（SG-1/SG-2，见 SPEC-GAP）后，lang-hir 的两处事实恢复可退化为断言。

## 阻塞

（无。观察两条，不阻塞：① 第一波登记写「33 条指令」，逐变体清点 Inst 实为 37 条（带效果 5：Spawn/ChanNew/ChanSend/ChanRecv/CallBuiltin；无效果 32，测试已逐条枚举锁定），待协调人核对口径；② 本波降形发现的三条语义面缺口已按「lowering 是搬运工、不做补救」原则处理并登记 SPEC-GAP（SG-1/SG-2/SG-3），其中 SG-1/SG-2 为 A2 记录缺口、已在 lang-hir 侧做事实恢复兜底。）

## SPEC-GAP

- **SG-1（lang-semantic，建议 A2 修）**：let/return/赋值顶层的 `?` 走
  `checker::infer_try` 直呼，绕过 `infer()` 的 `expr_ty.insert`——Try 节点
  无类型结论，HIR `meta.type_id=None`。本波在 lang-hir build 以操作数已
  记录类型恢复 ok 侧（`infer_try_operand` 同规则）补全事实；A2 修复后可
  退化为断言。
- **SG-2（lang-semantic，建议 A2 修）**：`Extern` 内层 FnDecl 不入
  `decl_syms`（仅顶层作用域持名）——HIR `fn_decl` 的 `symbol_of_decl`
  拿不到 extern 符号。本波在 lang-hir build 按名回查 arena 回填
  `meta.sym`（extern fn 名顶层唯一）。
- **SG-3（语义面冻结缺口，本波不做形态、lowering 显式内部错误）**：
  ①内建一等引用（`let f = println;`——checker 对裸内建引用类型 UNKNOWN
  未拒绝，语法上可达）——IR 无 builtin-get 值形态；②对全局取引用 `&g`——
  无 GlobalRef 形态。两者需先在语言/IR 层冻结决策再补指令。
- **观察项（未触达）**：`let f = fn(){ f() }`（fnlit 体引用自身绑定，递归
  闭包）若语义放行，lowering 时该绑定尚未创建 → 报内部错误；conformance
  100 case 未触达，暂按语义缺口处理。
- （历史：lowering 其余语义落点均能在 specs 里找到依据：EVAL-ORDER/
  CALL-RESOLVE/CTRL-FORIN/MATCH-KIND/RESULT-TRY/CHAN-*/VALUE-REF/
  VALUE-CAPTURE/VALUE-ELEM。）

## 已冻结接口登记

- 冻结：`lang_hir::HirProg`：structs/enums/globals/fns/main 五表语义树；每重要节点带 `NodeInfo{ id, span, sym?, type_id? }`。
- 冻结：`lang_hir::NodeInfo`：`{ id: NodeId, span: Span, sym: Option<SymbolId>, type_id: Option<TypeId> }`。
- 冻结：`lang_hir` 算符归一：`UnaryOp{Neg,Not,Ref}`、`BinaryOp` 16（无 `&&`/`||`——以 `HirExpr::AndThen/OrElse` 表短路）、`AssignOp` 5、`MatchKind{Literal,Enum,Result,Option}`、`LitKind` 4。
- 冻结：`lang_hir::LowerHir`：`trait { type Ast; type Error; fn lower(&mut self, ast: &Self::Ast) -> Result<HirProg, Self::Error> }`（NodeId 先序递增；同输入逐字段同输出）。
- 冻结：`lang_ir::IrProgram`：`{ structs, enums, globals, functions, init: Option<FunctionId>, main: Option<FunctionId> }`；FunctionId = functions 下标。
- 冻结：`lang_ir::IrFunction`：`{ name, capture_count: u16, sig: FnSig{param_count, has_ret}, locals: Vec<LocalDecl>, blocks: Vec<Block>, extern_host: Option<String> }`；locals 布局 = `[捕获槽][参数槽][局部/临时]`；extern 无块。
- 冻结：`lang_ir::Block`：`{ insts: Vec<Inst>, term: Terminator }`；块入口栈深 0、终接后归零（validator 强制不变量）。
- 冻结：`lang_ir` IR 指令集（`Inst`，33 条；栈效应 in→out；弹序=压序逆序）：
  - 常量：`ConstI64(i64)`、`ConstF64(f64)`、`ConstBool(bool)`、`ConstStr(String)`（均 0→1）
  - 局部/全局：`LocalGet(LocalId)` 0→1、`LocalSet(LocalId)` 1→0、`GlobalGet(GlobalId)` 0→1、`GlobalSet(GlobalId)` 1→0
  - 运算：`Unary(Unop{Neg,Not})` 1→1、`Binary(Binop` 16 种`)` 2→1、`Pop` 1→0
  - 调用：`Call(FunctionId)` in=目标参数数 out=目标 has_ret、`CallIndirect{argc, has_ret}`（先压闭包值再压实参；has_ret 为调用点静态声明）、`CallBuiltin{name: String, argc, has_ret}`、`Spawn{func: FunctionId, argc: u16}`（实参先求值压栈，out=0）、`ClosureNew{func, captures}`（弹 captures 个 Ref 作捕获环境，captures 必须等于目标 capture_count）
  - 索引/切片/字段：`IndexGet` 2→1（obj 先 idx 后）、`IndexSet` 3→0（obj,idx,val）、`SliceGet` 3→1（obj,lo,hi）、`FieldGet{field: String}` 1→1、`FieldSet{field}` 2→0（obj,val）、`ArrayPush` 2→0（arr,val；数组 `+=` 原位 push）
  - 构造：`ArrayNew{count: u32}` in=count、`MapNew{count: u32}` in=2×count（压序 k1,v1,k2,v2…=插入序）、`StructNew{struct_id: StructId}` in=声明字段数（字段按声明序压栈）、`EnumNew{enum_id: EnumId, case_id: u16}` in=case 载荷数、`RangeNew` 2→1（lo,hi）
  - 引用/装箱：`BoxNew` 1→1、`Deref` 1→1（Ref→指称副本，非 Ref 原样回压）、`RefSet` 2→0（ref,val；写穿透）
  - match 分派取载荷：`EnumPayloadGet{index: u16}` 1→1（弹 enum 值压载荷）
  - for-in：`IterNew` 1→1、`IterNext{done: BlockId}` 1→1（有元素压元素；耗尽跳 done；迭代器驻局部槽）
  - chan：`ChanNew` 0→1（缓冲 1024 语言常量）、`ChanSend` 2→0（ch,val）、`ChanRecv` 1→1（压 `Result[T,error]`）
  - `?` 传播：`Try{err: LocalId, fail: BlockId}` 1→1（Ok 压解包值；Err 存 err 槽跳 fail 块合成 Err 返回）
- 冻结：`lang_ir` 终接指令集（`Terminator`，6 条）：`Goto{target}`（break/continue 降形）、`Branch{if_true, if_false}` 1→0（`&&`/`||` 降形）、`SwitchInt{arms: Vec<(i64, BlockId)>, default}` 1→0、`SwitchEnum{enum_id, arms: Vec<BlockId>（与 case 声明序平行、长度强制相等）, default}` 1→0、`Return{has_value}` in=has_value、`Unreachable`（穷尽 match 兜底锚）。字符串/bool 字面量 match 降为 `Binary(Eq)` 链 + Branch（不设 SwitchStr/SwitchBool）。
- 冻结：`lang_ir::validate(prog: &IrProgram) -> Result<(), IrError>`：线性扫描结构校验；失败为内部错误（非用户诊断）。
- 冻结：`lang_ir::LowerIr`：`trait { type Error; fn lower(&mut self, hir: &lang_hir::HirProg) -> Result<IrProgram, Self::Error> }`；`EntryPoints{init, main}`。
- 冻结：`lang_ir` 新增 id：`BlockId(u32)`、`GlobalId(u32)`、`StructId(u32)`、`EnumId(u32)`（crate 内 newtype，形状同 lang-core）。

### 第二波冻结（2026-09-25 胶水层）

- 冻结：`lang_ir::BUILTINS: &[BuiltinEntry]`：内建三方对照表 20 行（顺序 = runtime core 注册序），
  每行 `{ name: &'static str, runtime_id: u32, direct_effect: Option<Effect>, ir_form: IrBuiltinForm }`；
  17 名 `Call` + 3 名 `Inst`（makeChan/send/recv）。测试与 `BuiltinRegistry::core()`（lookup/name 双向 +
  恰 20 项）及 `lang_contract::builtin_direct_effect` 逐项一致。
- 冻结：`lang_ir::IrBuiltinForm::{Call, Inst}`、`BuiltinEntry`（Copy 结构，见上）。
- 冻结：`lang_ir::inst_direct_effects(&Inst) -> Vec<Effect>`：Spawn→SPAWN、ChanNew/ChanSend/ChanRecv→CHANNEL、
  CallBuiltin→lang_contract 映射（http.get→NET 生效；未知/纯名空）、其余空（新增效果面指令必须登记，
  缺省保守为纯）。`lang_ir::fn_direct_effects(&IrFunction) -> Vec<Effect>`：extern→FFI，普通函数空。
- 冻结：`lang_ir::dump_program(&IrProgram) -> String` / `dump_function(&IrFunction, FunctionId) -> String`：
  确定性文本（同程序两次逐字节相等）；**调试视图非跨版本契约**；不含 span/TypeId；
  格式见 dump.rs 模块头（golden 2 例手写锁定）。
- 依赖：lang-ir → lang-contract（Effect 单一事实源）；dev-dep lang-runtime（测试锁名）。
