# ADR-0001 v0.3 Semantic Core——独立于后端的语义层

- 状态：已接受（2026-09-20，v0.3.0）
- 关联：docs/plans/v0.3-semantic-core-plan.md、decisions.md #65–#69

## Context

v0.2 的架构是「AST 三消费方」：解释器在运行时查名与定类型；→Go 后端自带 scopes
表查名 + infer() 推断 + match 分类/穷尽 + `?` 约束；→JS 后端再实现一份同名查名
表 + 同样的约束检查。三类语义规则存在三份实现，靠人工同步。

侦察（2026-09-20）实测的分歧与漂移：

1. 未定义名：解释器运行时报错、→Go 转译期报错、**→JS 完全不查**（生成坏 JS）；
   而 spec §5 声称 `check` 覆盖「未定义名」——实现与规范漂移。
2. 顶层非声明语句（if/while 等）：解释器执行、→Go/→JS 转译器**静默丢弃**。
3. Result 单臂 match：解释器运行时报错、→Go 表达式位置静默 no-op、→JS 同。
4. enum match 混字面量臂：解释器允许、双后端拒绝。
5. 嵌套类型声明（selftest 在用）：解释器容忍、→Go 拒绝、→JS 静默丢弃。

同时「check = 跑一遍 →JS 转译前置检查」（decisions #49）使静态检查能力寄生在
后端里，无法独立演进，也无法迁移到其它宿主语言实现。

## Decision

1. **新增独立语义层**（全部宿主无关，不依赖 Go 特性）：

   ```text
   semantic（resolver）→ types（checker）→ hir（Typed HIR）→ ir（Core IR）
   ```

   - Resolver：词法作用域、稳定 SymbolID、值/类型双命名空间（struct 名不可作值）。
   - Type Checker：统一 Type 模型（名义 struct/enum + 擦除泛型 + Any/Unknown 保守放行）；
     收编全部转译期约束；诊断带稳定码（E2xxx/E3xxx/E4xxx）。
   - HIR：每节点 NodeID/Span/Symbol/Type；脱糖唯一发生地（方法调用、裸构造器、
     go 语句、短路运算）。
   - IR：后端契约——局部槽位化、迭代协议显式化、match 分类结论固化；
     后端只做目标平台映射。

2. **check 走独立前端**，不再复用 →JS 转译器（取代 decisions #49）。

3. **分歧收口**（一律收紧为编译期拒绝，附带回归测试）：
   - 未定义名/嵌套声明/Result 单臂 match：统一为编译期错误（E2002/E2010/E4001）。
   - enum match 混字面量臂：采用双后端行为（E4002），解释器成为「较宽松的
     调试子集」这一事实被文档化，check 面向三路一致。

4. **保守性铁律**：类型检查器对 Any/Unknown/TypeArg 一律放行；examples + m4
   全 corpus 零诊断由 `TestCorpus零诊断` 机械锁定——现有合法程序零误杀。

5. **明确不做（v0.3）**：解释器迁 IR 执行（保留 AST 执行作为参考语义，
   迁移通道 = 共享前端诊断）、列级 Span、JSON 诊断协议（均属 v0.4）。

## Consequences

- 语义规则单点定义；新增第 2/3/4 宿主语言实现时只需实现同一 IR 契约。
- interp-only 构造（嵌套声明、`Ok()` 作值比较）在 check/build 面被拒绝；
  `lang-go test`（解释器路径）行为不变。examples/selftest 是 interp 专用
  语料，不入 check corpus（后续版本决定是否转正这些构造或改写 selftest）。
- 顶层非声明语句仍是脚本模式（解释器可执行）；IR 降级只接受声明序列，
  转译目标不再静默丢语义（会显式报错）。

## Alternatives

- 「后端直接消费 HIR」：少一层节点定义，但后端契约暴露 Symbol/NodeID 等代理
  元数据，且 v0.4 inspect/dump 需要与执行关注点耦合——否决。
- 「维持 #49（check = →JS 前置检查）」：零新代码，但语义规则无法脱离 →JS
  存在，且五处分歧无解——否决。
