# lang-cs Ownership / Runtime Decisions

日期：2026-09-26 · 母本模板逐项落定 · 全解决方案 `AllowUnsafeBlocks=false` / 禁 async 语义面 / 警告即错误

## Compiler

| 决策点 | 决定 | 理由 / 后果 |
|---|---|---|
| SourceText 所有权 | Lang.Core 不可变 sealed class（path/text/lineStartsCp/cpToChar 构造即建） | 单一事实源；charIndex↔cp 映射 O(log n) |
| UTF-16 映射 | cpToChar 表 + 文末钳制（≥LengthChars → LengthCp） | 代理对中间向下取整；EOF 定位专用语义（A1 依赖，有专测） |
| AST ownership | abstract record 封闭层次 + 嵌套 sealed record 子型（Expr.IntLit 双段命名） | 穷尽 switch expression 无静默兜底；禁 default 吞新变体 |
| Symbol/Type 存储 | SymbolArena / TypeTables（List 槽位 + int 索引 newtype；结构去重 intern） | 稳定 ID；类型相等 O(1) |
| 泛型 | 语言泛型=擦除，显式 TypeObject 实现；C# 泛型只用于编译器内部容器 | reified 泛型诱惑被契约 §0 明令禁止 |
| 诊断生命周期 | 不可变 record；DTO 层序列化借 SourceNames 反查 file | — |

## Runtime

| 决策点 | 决定 | 理由 / 后果 |
|---|---|---|
| Value 表示 | abstract record Value 封闭层次 14 变体：struct/enum 内联、Slice/Map 持 ValueId | **CloneValue() 即 VALUE-COPY-001**（顶层逐字段+嵌套递归+容器共享），ELEM-009 结构上不可表示 |
| Heap/Env/Channel | 各自独立槽位表 + newtype 索引；Map 用 List+Dictionary 双结构（迭代序只来自 List） | VALUE-MAP-006 插入序；Dictionary 只做 O(1) 查找不外泄序 |
| Place/Ref | Place 四变体 + RefValue 显式建模；**禁 ref/out/ref struct 表达语言引用** | 语言别名 ≠ 宿主引用 |
| Closure | EnvArena cell（属主共享=按引用捕获）+ loop 每迭代子环境 | VALUE-CAPTURE-004 六专项锁定 |
| Task/Channel | 协作式单线程调度（spawn 快照+语句点排空）；ChannelTable 缓冲 1024 FIFO 无 close | **async/await 禁入语义面** |
| 字符串/浮点 | 比较一律码点序（自写 CompareCodePoints——C# Ordinal 的 UTF-16 序在 astral 段方向相反，有判别用例）；解析/格式化一律 InvariantCulture | UTF-16 轴的运行时面 |
| Runtime error | RtError + RtResult<T> 受控通道；零 throw 处理用户源码错误 | — |

## 明确拒绝

`unsafe`（csproj 禁）/ `async` 语义面 / Dictionary 迭代序外泄 / culture 敏感字符串操作 /
C# 泛型承载语言泛型 / ref 系表达语言引用 / throw 表达用户源码错误。
