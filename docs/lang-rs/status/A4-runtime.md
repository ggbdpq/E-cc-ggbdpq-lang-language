# A4-runtime 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

### 2026-09-25 集成轮收官（B1 修复后差分守卫全量解禁，协调人授权集成工程师执行）

- B1（lang-ir 赋值降形两缺陷）已由集成轮修复（见 A3-hir-ir.md 集成轮小节），
  本波按 A4 侧预留的解禁形态收尾：`tests/conformance.rs` 清空 `LOWERING_BLOCKED`
  名单（全等断言双向强制不变）+ 删除 `diff_matrix_ast_vs_ir` 的 `#[ignore]`。
- **IR 真值矩阵：109p/11f（11 例登记阻断）→ 120p/0f/0s**（120 例 run 全量）；
  **双路差分 `diff_matrix_ast_vs_ir`：120/120 全部 stdout 逐字节一致 + 退出码
  一致、0 双路 skip 分歧**（自第四波 109 例一致后再无新分歧，证明修复后
  lowering 与 AST 参考解释器在全部 120 例上语义一致）。
- `cargo test -p lang-interp` = 21 绿（3 lib + conformance 4（AST 矩阵 + IR 矩阵
  + 双路差分 + 压力抽测）+ 14 手构 IR），0 ignored、0 失败。
- IrMachine 侧零改动（冻结合同自第四波起即冻结并经手构测试锁定，本波继续零漂移）。

### 2026-09-25 第四波交付：AST vs IR 全量差分矩阵 + 确定性压力抽测 + 预载身份修复

- **IR 路由接入 conformance**（`tests/conformance.rs` 新增 `IrRoute`）：MiniLoader 装载 → parse → analyze → `lang_ir::compile`（AST→HIR→IR）→ `IrMachine`；skip 门（extern/http + 静态诊断）与 AST 路由逐字同判，保证双路 skip 分类可比。
- **差分矩阵结果（run 类 120 例）**：**109 例双路 stdout 逐字节一致 + 退出码一致**；0 例 skip 判定分歧；6 例归属 IrMachine（已修，见下）；11 例归属 lang-ir lowering（A3 领地，登记阻塞区，差分测试 `#[ignore]` + IR 真值矩阵显式 `LOWERING_BLOCKED` 名单登记）。
- **修复的真 bug（本波唯一实现改动，`src/ir.rs`）**：`IrMachine::new` 把程序表尾预载 `error`/`Option`/`Result` 重复注册为新 def——运行时 `result_def()`（=EnumDefId(1)）等预载 id 与程序表 id 错位，`Try` 的 Result 判定、`isNone`、JSON None 序列化、`ok_value`/`err_value` 构造全部落空（表现为 `? 的操作数须为 Result`、`isNone(None)` 假、`None 不可 JSON 序列化`）。修复：按名映射到运行时预载定义（error=StructDefId(0)/Option=EnumDefId(0)/Result=EnumDefId(1)；名字口径与 lowering `struct_id_by_name`/`enum_id_by_name` 一致，合法程序中三名为预载保留名）。解封 6 例：option/option-basics、result/{result-err-through-two-levels, result-propagation-basic, result-try-chain}、io/{json-roundtrip, json-stringify-nested}。
- **确定性压力抽测**：10 case（4 chan/go + 4 closure + 2 map；标记轮转 go→chan→closure→map + 类目配额选样，确定可复现）× 3 遍 × 双路 = 60 份输出**零漂移**（go 同步确定子集 TASK-SPAWN-002、chan FIFO、map 插入序跨解释器可复现）。压力池门控双路可执行，lowering 阻断面不入选。
- **skip 检测补全**：`needs_host_or_ffi` 补 `Expr::FnLit` 体遍历（闭包体内的 extern/http 此前漏检；当前语料 0 例，行为面不变，纯保守性修正）。
- **测试面**：`cargo test -p lang-interp` = 3 lib + conformance 3 绿（AST 矩阵 120/120、IR 真值矩阵 109 绿 + 11 已登记阻断、压力抽测 10×3×双路）+ 1 ignored（`diff_matrix_ast_vs_ir`，登记见阻塞区）+ 手构 IR 14 例全绿无回退；`cargo fmt -p lang-interp` 干净；`cargo clippy -p lang-interp --all-targets -- -D warnings` 零警告（波次中途曾被 lang-conformance 在途编辑的 `map_flatten` 短暂阻塞，A6 修复后复跑通过）；`cargo test --workspace` 全绿零失败（`lang-conformance`/`lang-loader` 等下游不受本波改动影响）；生产代码零 unwrap/expect/panic、`#![forbid(unsafe_code)]` 不变。
- **A6 消费提示**：`ROUTE_INTERP_IR` 路由执行器形态已验证可直接复用——`RunRoute` 实现 = `run_ir_case(case_dir)`，与 `run_ast_case` 同签名同 skip 语义；runner 接入时两路执行器对称。

### 2026-09-25 第三波交付：双解释器（AST 参考解释器 + IR 解释器）+ conformance 全量实测

- **AST 参考解释器**（`lib.rs` + `eval.rs`）：`Interp::new(Machine)` → `run(&mut self, stmts: &[Stmt], analysis: &Analysis, out: &mut dyn OutputSink) -> RtResult<()>`；analysis 含诊断即拒执行（Scope 分类）。执行序 = 顶层语句依次 → `main`（若定义）。EVAL-* 全部落位：callee（含方法接收者）先于实参、实参左到右（EVAL-CALL-002）、赋值 target 地址组件先于右值（EVAL-ASSIGN-003；Ident 纯赋值写 cell 本身不穿透，Field/Index 经 `place_store` 写穿）、`&&`/`||` 短路（EVAL-SHORTCUT-006）、索引 obj 先 idx（EVAL-INDEX-005）、字面量左到右（EVAL-LITERAL-004）。方法按 (StructDefId, 方法名) 于声明点登记；泛型擦除语义（`type_params` 运行时忽略）；闭包 = 定义处环境帧（VALUE-CAPTURE-004）；`?` 的 Err 载荷 Propagate 到函数边界合成 `Err`（RESULT-TRY-003）；`go` = 实参立即求值后同步执行至返回（TASK-SPAWN-002 同步确定子集，与 TS 证据 interp.ts 逐字同构）；extern 调用报 UnsupportedHost（FFI-001）；for-in 迭代对象开始时快照、每迭代子环境（CTRL-FORIN-003）。
- **IR 解释器**（`ir.rs`）：`IrMachine::new(&IrProgram)` → `run(out)`；帧 = EnvArena 子环境（`LocalId` = 槽位号，捕获槽在前），语言级可变性是静态面、槽位一律可写；操作数栈条目带 Place 溯源（LocalGet/GlobalGet/IndexGet/FieldGet 产生，FieldSet/IndexSet 经 `place_store` 写回）；装箱纪律 BoxNew/Deref/RefSet；`ClosureNew` 捕获 Ref 值落闭包环境、`CallIndirect` 逐槽落地被调帧按引用共享；`IterNew`/`IterNext{done}`（迭代器以 box 槽引用驻留局部槽——块入口栈深恒 0 的 validator 不变量决定循环形状）；ChanNew/ChanSend/ChanRecv（Recv 空渠道 = `Err("channel 空")` 与内建 recv 同口径，CHAN-RECV-003）；`Try{err,fail}`：Ok 压载荷继续本块、Err 存 err 槽跳 fail 块（RESULT-TRY-003）；init→main 双入口；extern 报 UnsupportedHost。
- **输出面**（`format.rs`）：`InterpFmt` 实现 `ValueFormatter`——零漂移复用 `lang_backend_go::fmt`/`json` 冻结纯函数（`Value → Node` 桥；map 打印真值 `{"b": 2, "a": 1}` 与 conformance map-keys-values 逐字节核对）；chan 打 `?`、内建打 `<内建 名>`（语料无此类嵌套打印，差异不可达）。宿主内建 jsonParse（serde_json 合法性校验 + json.Number 整数优先 int64）/jsonStringify（A5 字节契约）在 Host 类别路由前拦截。
- **conformance run 类实测矩阵（路由 interp-ast）：run 120 / 通过 120 / 跳过 0 / 失败 0**（check 类 31 例不属本路由）。语料零 http/extern 用法（skip 检测保留并计数：extern 语句或 `http.*` 调用即 Skipped）；诊断非空的 run case 按 check 语义 Skipped 计数。模块类 5 例经测试内置迷你装载器跑通（文件名序合并 + import 语句位置原位展开 + 菱形去重 + 循环报错 + 越根检查；MODULE-IMPORT-002/MERGE-003/CYCLE-004；真实装载器在 lang-loader）。判定经 A6 冻结 `judge_run` 逐字节比对；测试断言「通过 + 跳过 = run 总数」防静默漏跑。
- **IR 手构测试 14 例**（`tests/ir_interp.rs`，先过 `lang_ir::validate` 再执行）：Call+Binary / Branch+Goto / SwitchInt 臂与 default / SwitchEnum+EnumPayloadGet / BoxNew-Deref-RefSet 别名 / 闭包按引用捕获（CallIndirect）/ 数组+映射+切片 / struct 字段 place 溯源写回 / 区间迭代协议 / chan FIFO+Try fail 块 / 全局 init→main / 一元算符 / extern 报错 + **双路径一致性**（同程序 AST interp vs 手构等价 IR stdout 逐字节相等，`ast_interp_and_ir_interp_agree`）。
- **修复的真 bug**：IR 构造类指令（ArrayNew/MapNew/StructNew/EnumNew/ClosureNew）对 `pop_n` 结果误 `reverse()`——`pop_n` 按栈序（=压入序）返回切片，反转会颠倒元素序/字段序/载荷序/捕获序（struct 首字段读成次字段、map 插入序颠倒、映射键值互换），由手构 IR 测试抓出后修复；Try 的类型错误消息硬编码「得到 Option」改为实际 `type_hint()`。
- **验证**：`cargo test -p lang-interp` 18 绿（3 lib + 1 conformance 矩阵 + 14 IR）+ `lang-runtime` 55 绿；`cargo fmt` 干净；clippy 对 lang-interp/lang-runtime 零警告（lang-hir `build.rs:365` 有 1 个 pre-existing `manual_map`——A3 领地未动）；`cargo check --workspace` 通过；生产代码零 unwrap/expect/panic；`#![forbid(unsafe_code)]`。
- **AST vs IR 差分状态**：HIR→IR lowering（`LowerIr` 实现）未落地（`lang_hir::build_hir` AST→HIR 本波已就绪可消费）；逐 case 全量差分待 A3 交付后接入——届时经 `build_hir` → `LowerIr` 量产等价 IR，`ROUTE_INTERP_IR` 路由直接可插（本波差分以手构等价 IR 一致性测试先行落地）。

### 2026-09-25 首批交付：lang-runtime 值模型与执行语义地基

- **Value enum**（`value.rs`）：Int(i64)/Float(f64)/Bool/Str(Rc<str>)/Unit/Range{lo,hi}/Slice(ValueId)/Map(ValueId)/Struct(StructVal)/Enum{def,case,payloads}/Chan(ChannelId)/Closure{func,env}/Builtin(BuiltinId)/Ref(Place)。语言级 `==`（RUNTIME-EQ-003）、compare（int/float/string 码点序）、truthy（仅 bool）同文件。
- **堆与值语义**（`heap.rs`）：HeapArena 槽位表（只增不减，GC 委托宿主）；MapObj 用 IndexMap 保插入序（VALUE-MAP-006），覆盖保位、原键形态不变；slice_get clone 返回（VALUE-ELEM-009 读出即副本）。
- **环境**（`env.rs`）：EnvArena cell{value,mutable}；`lookup` 返回属主 (EnvId, slot)——共享属主 cell 即按引用捕获（VALUE-CAPTURE-004）；let cell 写入拒绝（ImmutableAssign）；每迭代子环境支撑 loop capture。
- **Place/Ref**（`place.rs` + `machine.rs`）：Place::{Var,Element,MapEntry,Field}；`place_store` 先经 `resolve_place` 穿透 `Value::Ref` 再落地；Field 走「读副本→改字段→写回槽位」；`&x` = `Value::Ref(Place::Var)`，显式建模未映射 Rust 引用（VALUE-REF-003）。conformance 四例（reference-alias-write/reference-read-write/closure-capture-by-reference/containers-shared-aliasing）语义全部有测试锁定。
- **Channel**（`chan.rs`）：缓冲 1024 常量、send 满抛 ChanFull（RUNTIME-CHAN-001）、FIFO、空渠道 `RecvOutcome::Empty` + 逐字冻结文本 `CHAN_EMPTY_MSG = "channel 空"`；无 close（CHAN-* 冻结面）。载荷入队按值语义（struct 拷贝、容器共享，VALUE-TASKSHARE-007）。
- **调度**（`sched.rs`+`machine.rs`）：Task{func,args}（实参快照 owned Vec）；`Machine::spawn` 入队后在语句点同步排空（TASK-SPAWN-002），嵌套 spawn 由排空循环消化，Task 错误上抛、返回值丢弃；`Machine::recv` 先协作让位再出队。
- **内建注册表**（`builtin.rs`）：20 个名字面锁定（println/print/len/str/int/float/keys/values/has/push/assert/err/bytes/char/makeChan/send/recv/isNone/jsonParse/jsonStringify）+ `register_host` 扩展口；`Machine::call_builtin` 承接运行时自有内建，Host 类别（json/http）返回 UnsupportedHost 由宿主拦截。输出/JSON 格式化细节经 `ValueFormatter` 钩子留给 A5。
- **预载定义**（`defs.rs`）：`error`→StructDefId(0)、`Option`→EnumDefId(0)、`Result`→EnumDefId(1)，Machine 提供 some/none/ok/err/error_value 构造器与 is_none。
- **lang-interp 骨架**：`Interp::new`/`run(&mut self, &mut dyn OutputSink, &dyn ValueFormatter)` 入口签名 + eval/frame/step 模块占位（实现待 A3 IR 冻结）。
- **验证**：`cargo test -p lang-runtime -p lang-interp` 54 测试全绿（53+1）；`cargo clippy --all-targets -- -D warnings` 干净；`cargo fmt` 已跑；生产代码零 unwrap/expect/panic；`#![forbid(unsafe_code)]`。

### Runtime Model 裁定（对首批任务书「堆对象含 struct/闭包环境」的偏差，协调人知悉）

- **struct/enum 内联于 Value**：`Value::clone()` 即 VALUE-COPY-001（顶层逐字段、嵌套递归拷贝、容器字段共享 ValueId），绑定/传参/容器写入只走 clone——历史实现「容器内 struct 共享指针」的 ELEM-009 偏差在结构上不可表示。arena 索引保留给**引用语义**对象（slice/map 堆、EnvArena、ChannelTable），各自独立 newtype 索引，无类型混淆、无 dyn Any、无 Rc<RefCell>。
- spec 真值不受影响：全部 VALUE/CHAN/TASK 条文有对应用测试（期望值来自 specs/conformance 手算，测试清单见各模块 `#[cfg(test)]`）。

## 进行中

（无）

## 下一步

- A3 修复 lowering 普通赋值缺陷（见阻塞区）后：删除 `diff_matrix_ast_vs_ir` 的 `#[ignore]` + 清空 `conformance_run_matrix_ir_interp` 的 `LOWERING_BLOCKED` 名单（测试内全等断言会双向强制），即恢复 120 例全量双路差分守卫。
- 可选演进：`go` 语句改走 `Machine::spawn`/`TaskExec` 调度器化——当前为与 TS 证据同构的同步确定子集（TASK-SPAWN-002），conformance 全绿、可观察行为不受影响。
- SPEC-GAP-RS-001/002 裁决后如需改口径，落点在 `lang-runtime`（builtin_int / Value::compare）。

## 阻塞

### BLOCK-A4-01（移交协调人→A3）：lang-ir lowering 普通赋值两缺陷，挡住 A4 差分矩阵 11 例

**归属**：lang-ir（A3 领地，A4 禁改）。IrMachine 侧已按冻结合同实现并经手构 IR 测试锁定；两缺陷均可由 IR dump 直接证实。

**缺陷 1（压栈序违约）**：普通赋值 `target = 右值` 发射压栈序 **[右值, 地址组件…]**；冻结合同（status/A3-hir-ir.md §IR 指令集「弹序=压序逆序」）要求 `FieldSet` 2→0（**obj,val**）、`IndexSet` 3→0（**obj,idx,val**）。同一 lowering 的复合赋值 `c.n += 1` 却发射 [obj, val] 正确序——内部不一致。
- 最小复现：`let xs = [0, 0]` + `xs[1] = v()`（`fn v(): int { return 7 }`）。
- 期望（冻结合同压序 obj,idx,val）：`local.get xs → const 1 → call v → index.set`。
- 实际（dump）：`call v → local.get xs → const 1 → index.set`（栈 [val,obj,idx]）→ IrMachine 弹 obj=val(Int) → `TypeCast "int 不可索引赋值"`。
- 附证：`m[kmark()] = vmark()` 的 dump 显示 `call 0`（kmark，地址组件）先于 `call 1`（vmark，右值）——**求值序（EVAL-ASSIGN-003）正确，仅压栈序违约**。
- 影响 4 例：expressions/{eval-assign-index-from-call, eval-nested-call-order, evaluation-order-assign-target-first}、values/containers-shared-aliasing。

**缺陷 2（地址临时槽拷贝破坏 place 身份）**：地址组件无条件先落临时槽（铁律 1），struct 值按 VALUE-COPY-001 逐字段复制 → 临时槽持独立副本；**即使按冻结合同重排压栈序，写回也落在副本上，原绑定不变**。
- 最小复现：`let c = Counter{n: 0}` + `c.n = 41` + `println(c.n)`。期望 42。
- 实际（dump）：`local.get 1 → local.set 2（c 的副本）→ const 41 → local.get 2 → field.set "n"`——写槽 2 副本，println 读槽 1 原值 → 0（静默错值，非崩溃）。
- 约束供修复参考：struct 类地址组件跨右值求值存活须保 place 身份（box/Ref 形态落槽，或单块地址组件不落槽直压）；map/slice/Ref 基底共享身份不受影响（values/reference-read-write 只缺缺陷 1）。
- 影响 7 例：struct/{struct-field-write, struct-in-collection}、values/{map-value-struct-shared, nested-struct-copy, reference-read-write, struct-copy-semantics, struct-nested-copy-deep}。

**两根因合计 11 例**。A4 侧登记形态：`tests/conformance.rs` 中 `diff_matrix_ast_vs_ir` 带 `#[ignore]`（原因注明的属性 + 文档注释），`conformance_run_matrix_ir_interp` 常绿运行并以 `LOWERING_BLOCKED` 名单做全等断言（名单内必须确实失败、失败不得超出名单）——A3 修复落地后移除 ignore + 清空名单即恢复 120 例全量守卫，无需改断言逻辑。

## 已冻结接口登记

- 冻结：`lang_runtime::Value`：enum{Int(i64),Float(f64),Bool,Str(Rc<str>),Unit,Range{lo:i64,hi:i64},Slice(ValueId),Map(ValueId),Struct(StructVal),Enum{def:EnumDefId,case:u32,payloads:Vec<Value>},Chan(ChannelId),Closure{func:FunctionId,env:EnvId},Builtin(BuiltinId),Ref(Place)}；`Value::clone`=VALUE-COPY-001。
- 冻结：`lang_runtime::StructVal{def:StructDefId,fields:Vec<Value>}`；`MapKey{Int(i64),Str(Rc<str>),Bool(bool)}`（含 `from_value/plain`）。
- 冻结：`lang_runtime::ValueId(pub u32)`/`EnvId(pub u32)`/`ChannelId(pub u32)`/`StructDefId(pub u32)`/`EnumDefId(pub u32)`/`BuiltinId(pub u32)`（arena 下标 newtype）。
- 冻结：`lang_runtime::HeapArena`：alloc_slice(Vec<Value>)->Value、alloc_map(MapObj)->Value、slice/slice_mut/slice_len/slice_push/slice_get/slice_set、map/map_mut/map_len/map_get/map_has/map_insert/map_keys/map_values；`MapObj`（IndexMap 插入序）+`MapSlot{key,value}`。
- 冻结：`lang_runtime::EnvArena`：root/child/define(env,name,value,mutable)->slot/lookup(env,name)->Option<(EnvId,slot)>/get/set/cell/parent；`Cell{value,mutable}`。
- 冻结：`lang_runtime::Place`：enum{Var{env,slot},Element{slice,index},MapEntry{map,key},Field{base:Box<Place>,index}}。
- 冻结：`lang_runtime::Machine`：new/heap/envs/chans/sched/defs/builtins/root；register_function(name)->FunctionId、set_fn_name/fn_name；take_ref(env,slot)、deref_value、place_load（不穿透）、place_load_resolved（穿透）、place_store（穿透+ELEM-009 写回）、map_key_of；alloc_slice_value/alloc_map_value/make_chan_value/some_value/none_value/ok_value/err_value/error_value/is_none/enum_case_name/struct_field_names；spawn(exec,func,args)/run_pending/recv(exec,ch)；call_builtin(out,fmt,id,args)。
- 冻结：`lang_runtime::ChannelTable`：make/send（满→ChanFull）/try_recv（FIFO/Empty）/len；`CHAN_CAP=1024`、`CHAN_EMPTY_MSG="channel 空"`（逐字冻结）、`RecvOutcome{Value,Empty}`。
- 冻结：`lang_runtime::Scheduler`：enqueue/pop/pending；`Task{func:FunctionId,args:Vec<Value>}`；`TaskExec::call(&mut self,&mut Machine,FunctionId,Vec<Value>)->RtResult<Value>`（返回值被调度器丢弃）。
- 冻结：`lang_runtime::BuiltinRegistry`：core()/register_host(name)/lookup/kind/name；`BuiltinKind` 20 变体（含 Host）；`OutputSink::write`、`StdoutSink`、`ValueFormatter::fmt(machine,value,top)`（A5 实现点）、`fmt_args`。
- 冻结：`lang_runtime::DefTables`：with_prelude/register_struct/register_enum/struct_def/enum_def/error_def()/option_def()/result_def()；预载 id：error=StructDefId(0)、Option=EnumDefId(0)、Result=EnumDefId(1)。
- 冻结：`lang_runtime::RtError{kind,message}` + `RtErrorKind`{Index,Arith,Eq,Assert,ChanFull,MatchNoArm,TypeCast,JsonParse,ImmutableAssign,NotIterable,Scope,UnsupportedHost,Internal}（RUNTIME-FAIL-001 分类即契约，message 可变）。
- 冻结：`lang_interp::Interp::new(machine: Machine)` / `run(&mut self, stmts: &[Stmt], analysis: &Analysis, out: &mut dyn OutputSink) -> RtResult<()>`（AST 参考解释器：analysis 含诊断即拒执行；顶层语句→main）。
- 冻结：`lang_interp::ir::IrMachine::new(program: &IrProgram) -> IrMachine` / `run(&mut self, out: &mut dyn OutputSink) -> RtResult<()>`（IR 解释器：init→main；`pub machine` 字段暴露运行时状态机）。**装载约定（第四波）**：程序 struct/enum 表尾预载 `error`/`Option`/`Result`（lowering 恒追加）按名映射运行时 DefTables 预载 id（error=StructDefId(0)/Option=EnumDefId(0)/Result=EnumDefId(1)）；其余条目按声明序 register_struct/register_enum。
- 冻结：`lang_interp::format::{InterpFmt（ValueFormatter 实现）, value_to_node(&Machine, &Value) -> Node, host_builtin(&mut Machine, name, args)}`（输出面桥 + 宿主内建路由）。

## SPEC-GAP

- SPEC-GAP-RS-001（待协调人裁决）：`int(float)` 越界/非有限行为 spec 未冻结（RUNTIME-FAIL-001 未覆盖类型转换）。当前按三实现证据口径：向零截断、越界/NaN/Inf → i64::MIN（Go amd64）。若裁决不同只需改 `builtin.rs::builtin_int` 一处。
- SPEC-GAP-RS-002（待协调人裁决）：float 含 NaN 的 `==`/`<` 结果 spec 未写死。当前按 IEEE：NaN==NaN false、`compare` 退化 Equal；`Value::compare` 返回 Option 由解释器消费。
