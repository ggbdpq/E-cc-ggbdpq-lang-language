# ADR-0003 Safe Editing——轻量 Lossless 与机械安全修复

- 状态：已接受（2026-09-20，v0.5.0）
- 关联：docs/plans/v0.5-safe-editing-plan.md、decisions.md #77–#78

## Context

Formatter 从 AST 全量重排，注释按行号挂回——行尾注释、表达式内注释、块尾注释丢失
（v0.3 spec §9 自认的限制）。Coding Agent 高频自动修改要求：注释零丢失、编辑
结构化可验证、修复机械安全。

## Decision

1. **轻量 Lossless 模型**（采纳 Prompt 02 许可的 token-preserving 轻量方案）：
   `syntax.ScanComments`（有序注释 trivia 流，词法态扫描跳过字符串）+ 节点 Span
   （v0.4）+ 源码文本 = 可复原信息全集。不引入 red-green tree——当前语言规模下
   其工程成本（不可变树/编辑增量/标识 intern）无对应收益。
2. **Formatter 注释插回**：行缓冲渲染 + 语句边界冲刷。行首注释独立行（保留空行
   分组）、行尾注释追加语句末行（两空格规范分隔）、表达式内注释边界冲刷（位置后移、
   文本零丢失）、块尾注释原位保留（`}` 之前）。
3. **结构化编辑**：TextEdit/WorkspaceEdit + sha256 前置校验（哈希不匹配拒绝写）+
   重叠区间拒绝 + rune 偏移。偏移度量与 v0.4 Span 一致（rune，非字节）。
4. **fix --safe 三修复**：同文件重复 import 去重、未使用 import 删除（保守判定：
   import 行自身不计入引用）、行尾空白/EOF 换行规范化。事务闭环
   analyze→plan→precondition→apply→parse→resolve→typecheck，任一步失败即 STOP。
5. **装载器 done 键归一化**（顺手修复的潜在 bug）：根目录文件以带/不带扩展名
   双键登记 done，消除「根文件 + 同目录 import」双载导致的 E2001 误报；
   根文件间冗余互引转为静默去重，装载链内真循环仍报错（测试更新）。

## Consequences

- 注释 100% 保留由 `Test注释100保留`（全 corpus + 对抗样本）机械锁定；
  幂等与语义等价同测。
- 表达式内注释位置后移是允许的规范移动（文本不丢）；精确原位保留留给后续需要时。
- unused-import 判定保守（import 行不计引用 + 名字含于字段链即保留）——宁可漏删。

## Alternatives

- 完整 red-green/lossless tree（Rust-analyzer 式）：工程量大数倍，当前无 IDE 消费方——否决。
- fix 允许非机械修复（API 改名等）：属 v0.7 语义重构，需 Impact Analysis——否决。
