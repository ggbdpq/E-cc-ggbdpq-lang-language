# lang-cs Final Contract Sync Report

日期：2026-09-26 · 方法：收官后对照 specs/schemas/conformance/ADR 终态复核（承 lang-rs sync 结论）

| 契约面 | lang-cs 落点 | 状态 |
|---|---|---|
| SourceSpan（0 基码点/1 基行列/半开） | Lang.Core SourceText（charIndex↔cp 映射层） | MATCH（代理对/文末边界有专测） |
| 诊断 27 码 ↔ Rule | Lang.Core Diagnostic.cs | MATCH（映射抽样测试；E2009 空号保留） |
| 诊断 JSON 结构 | Lang.Contract Dto.cs | MATCH（golden 手抄 spec；字段序对齐 schema） |
| Agent Protocol schemaVersion 1 | Lang.Contract + Lang.Cli | MATCH |
| Conformance 组织与比较 | Lang.Conformance | MATCH（151 扫描 + run 三要素逐字节 + check 多重集） |
| 值语义 VALUE-* | Lang.Runtime（struct 内联 clone=深拷贝） | MATCH |
| 求值序 EVAL-* | Lang.Interp + Lang.Ir lowering | MATCH（降形专项锁 B1 教训） |
| Option/Result/? | Lang.Types/Semantic/Ir/Interp | MATCH（含 ADR-0005 赋值顶层 `?` 放行） |
| Task/Channel 冻结面 | Lang.Runtime Chan/Sched | MATCH（无 close） |
| 模块 MODULE-* | Lang.Loader + CLI | MATCH |
| 词法/语法 LEX/PARSE | Lang.Syntax | MATCH（astral 标识符/字符串 span 锁定） |
| 版本 | lang-cs 0.1.0 独立打点 | MATCH |

## FIXED / SPEC_GAP

- 开发中修正：宿主子进程 UTF-8 解码（GBK mojibake）、CLI 入口 CRLF 统一、CharIndexToCp 文末边界。
- SPEC-GAP：CS 台账 = 继承 RS-001..013（裁决状态同 lang-rs）+ CS 新增 4 条实现边界记录，全部不在 151 可观察面内。
- MULTI_IMPL_SHARED_BUG：0 新增。
