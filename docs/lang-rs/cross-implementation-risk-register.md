# lang-rs 跨实现风险台账

来源：`2026-09-25-lang-rs-对抗验证重点.md`。终态：11 项高风险语义全部通过四实现交叉验证
（conformance 151×4 全绿 + 专项单测），逐行测试锚如下。任何一行后续改动必须先红后绿。

| # | 域 | 风险场景 | Rust 处理 | 测试 | 状态 |
|---|---|---|---|---|---|
| 1 | Scope | for/match/嵌套 let 遮蔽 | 值/类型双作用域栈 + 构造器全局命名空间，四遍扫描 | lang-semantic NAME-* 十规则单测 + conformance 全分类 | VERIFIED |
| 2 | Scope | 跨文件合并命名空间 | loader 合并后单次 analyze；loop 变量每迭代子环境 | conformance module 5/5 × 四路 | VERIFIED |
| 3 | Effects | 直接/1 级/3 级传播、递归、互递归 | 幂集格单调不动点（CallGraph 泛型输入） | lang-contract effects 8 专项 | VERIFIED |
| 4 | Effects | spawn / channel / 纯环 | IR 指令直接效果提取（inst_direct_effects）+ extern→FFI | 同上 + inspect effects 实测（langalgo 186 函数） | VERIFIED |
| 5 | Closure | loop/mutable/nested/escaping/shadowed capture | EnvArena 按引用捕获（属主 cell 共享）+ 每迭代子环境；IR 侧循环变量逐轮 BoxNew | lang-runtime capture 单测 + conformance closure 4/4 × 四路 | VERIFIED |
| 6 | Generic | 擦除同一性、Option 链、Result 第二参、None 特化 | type_param 擦除互同 + GEN-SUBST-003 检查 | lang-types/semantic 单测 + conformance generics 6/6 × 四路 | VERIFIED |
| 7 | Reference | &x 读写、别名可见性、集合元素/字段写回 | Place 递归 + place_store 写穿；IR 原槽直压保溯源（集成轮 B1 修复） | lang-runtime place 单测 + conformance values 10/10 × 四路 | VERIFIED |
| 8 | Eval Order | callee→args L2R、receiver 先行、赋值 target 先于 RHS、go 实参快照 | AST interp 逐条落实；降形地址组件先于右值落槽 + 压栈序冻结合同 | 降形专项单测（kmark/vmark）+ conformance expressions 16/16 | VERIFIED |
| 9 | Unicode Span | ASCII/中文/emoji/combining/CRLF/混合 | SourceText 码点映射层；标识符 Unicode L*/Nd 精确闭区间表（对齐 Go） | lang-core 14 测试 + lexer span 单测 | VERIFIED |
| 10 | Runtime Output | int64 边界、float 格式、嵌套引号、map 插入序、JSON 键序/HTML 转义 | A5 冻结规则层（strconv 镜像 + IndexMap + 码点序键）三方共用 | lang-backend fmt/json 46 测试 + conformance 全分类输出逐字节 | VERIFIED |
| 11 | Concurrency | 单生产者 FIFO、spawn 实参立即求值、可移植确定子集 | ChannelTable 缓冲 1024 + 同步确定调度；压力 10 case × 3 遍 × 双路 | lang-runtime chan/sched 单测 + conformance concurrency 6/6 | VERIFIED |
