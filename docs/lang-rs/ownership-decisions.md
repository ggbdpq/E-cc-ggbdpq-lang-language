# lang-rs Ownership / Runtime Decisions

日期：2026-09-25 · 按计划 §45 模板逐项落定 · 全 workspace `#![forbid(unsafe_code)]`

## Compiler

| 决策点 | 决定 | 理由 / 替代方案 | 后果 |
|---|---|---|---|
| SourceText 所有权 | lang-core 持有不可变 `SourceText`（path/text/双索引构造即建）；各层传 `&SourceText` 或 SourceId | 替代：每层带 String——浪费且 span 反查困难 | 单一事实源；诊断/file 反查 O(1) |
| byte↔code-point 映射 | 构造时建 `cp_index`（Vec<u32>）+ `line_starts_cp`；`byte_to_cp`/`cp_to_byte`/`line_col` 三 API | 替代：懒建索引——解析期一次性成本更可预测 | 内存 O(码点)；映射 O(log n) |
| AST ownership | 纯数据 enum（`Vec<Stmt>` 树），节点带 span，无 arena | 替代：AST arena——语法树生命周期与编译期相同，裸树 + 穷尽 match 更直白 | 克隆成本仅在 formatter 夹具测试可接受 |
| Symbol 存储 | SymbolArena（Vec 槽 + SymbolId newtype） | 替代：Rc<RefCell>——禁用 | 稳定 ID，refactor/graph 直接消费 |
| SymbolId 稳定范围 | 单次 `analyze` 调用内稳定（与 Agent Contract 同口径） | — | re-analyze 重算，不做差异保持（spec 同口径） |
| Type interning | TypeTables：TypeData 结构去重 intern，TypeId(u32) | 替代：每次分配——类型相等退化为结构比较 | 相等比较 O(1)；deep clone 泛滥被结构性消除 |
| HIR/IR 存储 | HIR 节点树 + NodeInfo；IR 全 arena（函数表/局部表/块表，u32 索引 newtype） | 替代：自引用 struct——Rust 禁；Rc——禁 | validator 纯线性扫描可完成 |
| String interning | 未引入（Rc<str> 承载运行时字符串） | 少一个系统 | — |
| Diagnostic 生命周期 | 不可变值，分析结果 Vec 聚合；DTO 层序列化时借 file 反查 | — | 无可变诊断状态 |

## Runtime

| 决策点 | 决定 | 理由 / 替代方案 | 后果 |
|---|---|---|---|
| Value 表示 | 内联 enum：Int/Float/Bool/Str(Rc<str>)/Unit/Struct(内联)/Enum(内联)；堆索引：Slice/Map(ValueId)；Chan/Ref/Closure 各自 newtype | 替代：全堆装箱——访问成本与 clone 语义都差 | **`Value::clone()` 结构上不可表示 ELEM-009 偏差**（容器内 struct 共享指针）——值语义由类型系统强制 |
| Heap Arena | HeapArena 槽位表（只增不减，GC 委托宿主/进程退出） | 替代：Rc——禁；真实 GC——超范围 | slice/map 共享 O(1)；泄漏面 = 程序运行期，可接受 |
| Place 表示 | `Place::{Var,Element,MapEntry,Field}` 显式递归 | 替代：映射 Rust `&T/&mut T`——借用检查器无法表达语言别名语义（spec 明令禁止） | place_store 解析递归写穿；操作数栈带溯源 |
| Reference 表示 | `Value::Ref(Place)`——语言级引用与 Rust 引用零关系 | 同上 | &x/写穿/别名全部显式可测 |
| Struct copy | 内联 Value 的 clone = 顶层逐字段+嵌套递归+容器字段共享（VALUE-COPY-001） | — | 拷贝语义单点（clone），不可漂移 |
| Slice/Map sharing | ValueId 共享 + HeapArena；Map 用 IndexMap 保插入序（VALUE-MAP-006） | 替代：HashMap——迭代序不确定，契约禁止 | 插入序/覆盖保位免费获得 |
| 容器元素写回 | place 解析「读副本→改→写回属主槽/堆」 | — | conformance containers-aliasing 全绿 |
| Closure CaptureEnv | EnvArena cell{value,mutable}；lookup 返回属主 (EnvId,slot)——共享属主 cell 即按引用捕获；loop capture 每迭代子环境 | 替代：Rust closure——无法表达按引用捕获计数器 | VALUE-CAPTURE-004 六专项全锁定 |
| Captured mutation | cell 一律可写（语言级 mutable 是静态检查面，运行时槽位不设锁） | — | 与 AST interp/双后端语义一致 |
| Task state | Scheduler 任务表 + spawn 实参 owned 快照；同步确定子集（语句点排空） | 替代：Tokio——spec 明令禁止定义语义 | TASK-SPAWN-002/DETERMINISM-003 全绿 |
| Channel state | ChannelTable：VecDeque 缓冲 1024 常量、FIFO、send 满 ChanFull、空 recv 逐字冻结消息、无 close | — | CHAN-* 冻结面全锁定 |
| Builtin registry | 20 名面锁定 Vec + BuiltinId；Host 类别留宿主拦截；输出经 ValueFormatter 钩子 | — | IR/interp/后端三方对照表测试锁定 |
| Runtime error | Result 型受控错误（RtError），RUNTIME-FAIL-001 分类；禁 panic 传播用户错误 | — | 生产代码零 unwrap/expect/panic |

## 明确拒绝的方案（计划 §45 要求的负面清单）

- `Rc<RefCell<T>>`/`Arc<Mutex<T>>`：零使用——arena+newtype 索引全覆盖。
- `unsafe`：零使用（forbid）。`dyn Any`：零使用。
- 大量 clone：值语义单点化于 `Value::clone`，类型层 intern 消除类型克隆。
- Tokio/std::thread：未进入语义面（未来可替换 runtime 实现留接口）。
- 宿主 panic 作为语言错误：禁止（Result 全链）。

## 每结构测试锚（抽查入口）

lang-runtime 55 测试（值语义/引用/捕获/chan/调度）、lang-core 14（映射层）、
lang-ir 63（validator/降形/对照表/dump）、lang-interp 21（双解释器+120 例差分）、
conformance 151×4（`strict_all_routes_green`）。
