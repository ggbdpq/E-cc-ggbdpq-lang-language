# ADR-0004：语义完整性与跨实现契约（v0.9）

日期：2026-09-20　状态：已接受　关联：docs/lang-go/2026-09-20-v0.9-语义完整性实施计划.md、decisions #89–#92

## 背景

v0.8 完成后，语言正确性由 conformance 套件（33 case）与 Go 参考实现自证，但存在
四个结构性问题：(1) 语义真值分散在 go/docs/spec.md 与实现代码之间；(2) 规则没有
稳定编号，Human/Compiler/Agent 无法引用同一条规则；(3) IR 的闭包捕获等关键语义
依赖各后端按名字重建作用域——宿主闭包行为事实上成了规范；(4) 验证是三路
（AST 解释器/→Go/→JS），IR 层语义无人独立验证。

## 决定

1. **specs/ 为唯一语义真值源**：11 份主题规范 + rules.md Rule Catalog（约 60 条，
   N/D/F 三级），27 个诊断码全量映射 Rule ID；go/docs/spec.md 降为实现侧摘要。
   真值优先级：specs → conformance → 参考实现 → 教程。
2. **诊断携带 ruleId**：Diagnostic JSON 增加 `ruleId`（omitempty，schemaVersion 1
   只加不改）；映射唯一实现在 `contract/rules.go`；conformance manifest 诊断项与
   case 级 ruleIds 均可比对/透出。
3. **新增 IR 解释器（internal/irinterp）**，验证体系升级四路（AST Oracle /
   IR 解释器 / →Go / →JS，stdout 逐字节一致）。irinterp 复用 interp 值类型、
   interp.Fmt 与 CallBuiltin 内建——输出格式与内建语义禁止第二份实现。
4. **并发抽象为宿主无关 Task/Channel 模型**（specs/concurrency.md）：`go` 语法
   保留；同步确定子集是规范保证面，真并行是调度自由度；无 close 冻结。
5. **闭包捕获显式化**：lower 的 FnLit 作用域继承外层链，捕获变量解析到外层同一
   Local 槽位指针——捕获关系保留在 IR 中，不再依赖后端按名字重建词法作用域。

## 语义收口实测修复（四例，全部先复现后修）

| 例 | 分歧 | 修复 |
|----|------|------|
| 1 | 赋值求值顺序：interp 为 value 先 target 后，→Go/→JS 为 target 先 | interp 重写两阶段 assignAddr（EVAL-ASSIGN-003 成文） |
| 2 | →JS 表达式位置 match 无 await（v0.8 已修，套件回归锁） | exprMatch 片段自带 await |
| 3 | →Go 数组/映射元素写路与复合赋值只支持 *LangGoArr[any] | LangGoArr.LangGoSet + 接口分派 + langGoArith |
| 4 | IR 闭包捕获丢失（本表第 5 条） | lower FnLit 作用域继承 |

## 后果

- 正面：规则可引用（spec↔diagnostic↔conformance↔agent 四方同源）；IR 语义可独立
  验证；未来 TS/Python/Rust 实现有明确契约面（specs + conformance + schemas +
  runner contract）。
- 代价：双 Def 体系（irinterp 与 interp 各自持有 Option/Result 定义，判定走
  Def.Name/Case 序解耦）；GenGo 性能热点已知未修（specs/performance.md 记录）。
- 冻结面：无 close、无 pub、Any/Unknown 不得作逃生口、unit 位置——变更须经 ADR。
