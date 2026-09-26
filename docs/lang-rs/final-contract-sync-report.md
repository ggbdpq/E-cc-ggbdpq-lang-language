# lang-rs Final Contract Sync Report

日期：2026-09-25 · 范围：specs/ + schemas/ + conformance/ + ADR ↔ lang-rs 终态复核

## 方法

收官后重读 `specs/`（diagnostics/source-span/rules/agent-protocol/runner-contract/runtime/values/expressions/control-flow/option-result/concurrency/modules/grammar/lexical-grammar）、`schemas/agent-protocol.schema.json`、`conformance/`（151 case），逐契约面对照 lang-rs 实现与测试。

## 逐契约面结论

| 契约面 | 规范源 | lang-rs 落点 | 状态 | 依据 |
|---|---|---|---|---|
| SourceSpan（0 基码点 / 1 基行列 / 半开） | specs/source-span.md | lang-core source.rs + span.rs | MATCH | 14 单测 + 全链码点化（parser 字节偏移不出边界） |
| 诊断 27 码 ↔ Rule 映射 | specs/rules.md + diagnostics.md | lang-core diagnostic.rs | MATCH | 逐行映射测试；E2009 空号保留 |
| 诊断 JSON 结构 | specs/diagnostics.md §2 | lang-contract dto.rs | MATCH | golden JSON 手抄规范测试；行级 column=1/offset=0 |
| Agent Protocol schemaVersion 1 | schemas/agent-protocol.schema.json | lang-contract + lang-cli | MATCH | schema 形状校验 + 协议往返测试；注：schema 的 schemaVersion 为字符串 "1"（按证据落定） |
| Conformance 组织与比较 | specs/runner-contract.md | lang-conformance | MATCH | manifest 硬校验 + run 三要素逐字节 + check 四元组多重集；151 case 全过 |
| 值语义（COPY/SHARE/REF/CAPTURE/MAP/ELEM） | specs/values.md | lang-runtime | MATCH | 逐条单测 + conformance values 10/10 × 四路 |
| 求值序 EVAL-* | specs/expressions.md | lang-interp + lang-ir 降形 | MATCH | 降形专项单测（kmark/vmark 序、callee 先行）+ conformance expressions 16/16 |
| Option/Result/? | specs/option-result.md | lang-semantic + lang-ir + lang-interp | MATCH | conformance option 3 + result 6 × 四路 |
| Task/Channel（缓冲 1024/send 不阻塞/recv 阻塞/无 close） | specs/concurrency.md | lang-runtime chan.rs | MATCH | conformance concurrency 6/6；close 冻结未实现 |
| 模块 MODULE-* | specs/modules.md | lang-loader + CLI | MATCH | conformance module 5/5（含嵌套 import 拓扑） |
| 词法/语法 LEX/PARSE | specs/lexical-grammar.md + grammar.md | lang-syntax | MATCH | conformance syntax 13/13 + Unicode 标识符闭区间表（比 Rust std 更严，对齐 Go IsLetter/IsDigit） |
| 版本 | specs/versioning.md | lang-cli version → lang-rs 0.1.0 | MATCH（实现版本独立打点） | v1.0 语义契约下第四实现 0.1.0 起步 |

## FIXED（开发中修正的实现侧偏离）

1. 降形赋值压栈序 → 修正为冻结合同序（集成轮 B1）。
2. IrMachine 预载定义按名映射 → 与 lowering 表合成约定一致（W4）。
3. 双后端 `+=` 数组拼接语义 → 单点对齐 spec 真值（集成轮 B2）。
4. formatter 空块+注释场景幂等性 → 批内首条注释不补空行，上下文无关（W2，对证据的故意超集）。

## SPEC_GAP（移交人工裁决，13 条见 spec-gaps.md）

全部不在 151 case 可观察面内：嵌套引号跨后端分歧（RS-001）、jsonStringify 浮点边界（RS-002）、
非字符串映射键（RS-003）、顶层 chan 保留字（RS-004）、Arm span 超集（RS-005）、int(float) 越界（RS-006）、
NaN 比较（RS-007）、文档漂移类（RS-008/010）、YAML 锚点（RS-009）、顶层 `?` 赋值（RS-011，spec 与
Go interp 一致、TS/Go checker 误报——建议上游修 checker）、块级绑定写入（RS-012，lang-rs 按
NAME-SCOPE-001 正确处理）、用户泛型 struct 静默容忍（RS-013）。

## MULTI_IMPL_SHARED_BUG

RS-011 为唯一实锤（三实现同区不一致：checker 拒 / interp 收），lang-rs 按 spec 实现，
已登记待上游 ADR；其余无「四实现共同错误」形态。
