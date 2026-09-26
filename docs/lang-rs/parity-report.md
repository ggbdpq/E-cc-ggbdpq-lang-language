# lang-rs Parity Report（L6 Full Independent Parity）

日期：2026-09-25 · 协调人：Main Coordinator · 执行：六智能体四波并发 + 一轮集成修复
基线：lang-go v1.0.0 · HEAD 73187d3 · 开发期 git 提交数 = 0

## 1. 结论

lang-rs 达成计划 §50 的 L6 Full Independent Parity：第四套完整独立实现，151 case 四路全绿。
最终验收清单（计划 §53）22/22 项满足，逐项证据见 §3。

## 2. 工程总量

- 19 crates（`impls/rust/crates/`），`#![forbid(unsafe_code)]` 全 workspace，生产代码零 unwrap/expect/panic。
- workspace 测试 **505 通过 / 0 失败 / 0 ignored**；`cargo fmt --check` 干净；`cargo clippy --workspace --all-targets -- -D warnings` 零告警。
- MSRV：rust-version 1.85（edition 2024）；实测工具链 Rust 1.98.1 stable-msvc。

## 3. 验收清单（计划 §53 逐项）

| # | 项 | 证据 | 结果 |
|---|---|---|---|
| 1 | 位于 `impls/rust/` | 19 crates | ✓ |
| 2-4 | fmt / clippy -D warnings / test --workspace | 协调人本机复跑 | ✓ |
| 5 | 151/151 canonical Conformance | `strict_all_routes_green`（conformance_matrix.rs） | ✓ |
| 6 | AST/IR/Go/JS 四路 | interp-ast 120p / interp-ir 120p / go 120p / js 120p + check 31p = **511/511** | ✓ |
| 7 | Rule ID parity | lang-core 27 码 ↔ Rule 映射测试（specs/rules.md 逐行） | ✓ |
| 8 | Diagnostic parity | check 类 31/31（code/ruleId/severity/line 多重集） | ✓ |
| 9 | SourceSpan code-point parity | lang-core 映射层 14 测试（ASCII/中文/emoji/CRLF/越界）+ 全链 span 码点化 | ✓ |
| 10-14 | shadowing / generics / closure capture / reference alias / eval order parity | 对应 conformance 分类全绿 + 对抗专项单测（scope 十规则、loop capture、EVAL-* 降形单测） | ✓ |
| 15 | Task/Channel portable subset | concurrency 6/6 × 四路；确定性压力 10 case × 3 遍 × 双路零漂移 | ✓ |
| 16 | effect fixpoint | 幂集格不动点引擎 + 8 条对抗专项（递归/互递归/spawn/channel/纯环） | ✓ |
| 17 | formatter idempotence | conformance 151/151 + dogfood 66/66 五重断言（幂等/可重解析/AST 形态相等/确定性/注释保留） | ✓ |
| 18 | Agent Contract deterministic | DTO golden JSON（specs/diagnostics.md §2 手抄）+ inspect canonical 序 | ✓ |
| 19-20 | Safe Edit / Refactor E2E | 跨文件 rename 事务闭环、哈希拒绝零半写、organize-imports/safe-remove E2E 16 条 | ✓ |
| 21 | dogfood/langalgo | run/check/test/fmt 与 Go 参考逐字节一致（6/6 测试）；verify 九段全过 | ✓ |
| 22 | 开发期 git 提交 = 0 | 全程工作区 | ✓ |

## 4. Rust 特有的对抗式审查收益

第四实现不是复刻，过程中抓到并修正了一批「三实现共同或参考实现自身」的问题：

- **IR 构造指令误 reverse**（pop_n 栈序语义），手构差分测试抓出——Rust 侧修复。
- **IrMachine 预载定义错位**（程序表尾重复注册 error/Option/Result），差分抓出——修复后 option/result 6 例解封。
- **降形赋值压栈序违约 + 地址临时槽深拷贝**（33 格红），IR dump 铁证定位，修法=原槽直压保 Place 溯源；修复牵出后端写链 Ref 缺口一并补齐。
- **参考后端撞键 bug**（JS `String(k)` 映射键 int/"1" 撞键）——Rust 侧类型前缀键修复。
- **fnlit type_params 丢失**（TS/Go formatter 证据丢弃导致重解析漂移）——Rust formatter 保留。
- **push void 形**（goprelude langGoPush 无返回值致 →Go 产物编译失败）——修正为返回首参。

## 5. SPEC-GAP 状态

影响 canonical 契约（151 case 可观察面）的语义阻塞 gap = **0**。
13 条 spec 歧义/文档漂移登记于 `spec-gaps.md`（RS-001..013），全部不在语料可达面内或属证据层分歧，
需人工 ADR 裁决后统一四实现——按计划 §52 属 Final Contract Sync 的移交项，不构成 L6 阻塞。

## 6. 四波执行记录

| 波 | 交付 | 测试增量 |
|---|---|---|
| W0 | 接口契约冻结 + 19 crate 脚手架 + lang-core（身份/Span/诊断） | 14 |
| W1 | Syntax 前端 / IR 指令集+validator / Runtime 值模型 / 输出规则层 / DTO+effects+manifest（A2 首波限流失败） | →258 |
| W2 | 语义核全量（diagnostics 30/30）/ Formatter 151 幂等 / IR 胶水 / Go+JS codegen E2E | →341 |
| W3 | Lowering 全链 / 双解释器（AST 路由 120/120）/ Edit+Graph+Refactor+Verify 管线 | →460 |
| W4 | CLI 全命令 + dogfood 实测 / 双路差分 / conformance 151×4 总账 | →505 |
| 集成轮 | B1/B2/B3 修复 + 全部守卫解禁 | 511/511 ✓ |
