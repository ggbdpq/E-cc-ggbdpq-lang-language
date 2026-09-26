# lang-rs Capability Matrix

基线：`2026-09-25-lang-rs-基线快照.md`；模板：`2026-09-25-lang-rs-验收矩阵模板.md`。
状态限定：TODO / IMPLEMENTED / VERIFIED / BLOCKED_SPEC_GAP / SPEC_IMPL_CONFLICT / MULTI_IMPL_SHARED_BUG / NOT_APPLICABLE。
各 Agent 实现到块即把对应行 TODO → IMPLEMENTED（VERIFIED 必须由协调人在 conformance/differential 过后改）。

| 领域 | Contract | Rust 模块 | Conformance | 状态 |
|---|---|---|---|---|
| Lexer | LEX-001..008 | lang-syntax | conformance/syntax | VERIFIED |
| Parser | PARSE-001..006 | lang-syntax | conformance/syntax | VERIFIED |
| SourceSpan | source-span.md | lang-core | 跨类 | VERIFIED |
| Scope | NAME-* | lang-semantic | conformance/syntax 等各类 | VERIFIED |
| Type System | TYPE-* | lang-types/semantic | conformance/types | VERIFIED |
| Generics | GEN-* | lang-types/semantic | conformance/generics | VERIFIED |
| Values / Refs | VALUE-* | lang-runtime | conformance/values | VERIFIED |
| Evaluation | EVAL-* | lang-interp | 各类 | VERIFIED |
| Option / Result | RESULT-* | lang-interp | conformance/option, result | VERIFIED |
| Match | MATCH-* | lang-semantic/interp | conformance/match | VERIFIED |
| Modules | MODULE-* | lang-loader | conformance/module | VERIFIED |
| Task / Channel | TASK-*/CHAN-* | lang-runtime | conformance/concurrency | VERIFIED |
| Runtime | RUNTIME-* | lang-runtime/interp | 各类 | VERIFIED |
| HIR | Semantic Facts | lang-hir | dump 对齐 | VERIFIED |
| IR | Semantic Facts | lang-ir | dump 对齐 | VERIFIED |
| AST Interpreter | Dynamic Semantics | lang-interp | 151 | VERIFIED |
| IR Interpreter | Dynamic Semantics | lang-interp | 151 | VERIFIED |
| Go Backend | Observable Behavior | lang-backend-go | conformance + go 差分 | VERIFIED |
| JS Backend | Observable Behavior | lang-backend-js | conformance + js 差分 | VERIFIED |
| Formatter | Idempotence | lang-formatter | 属性测试 | VERIFIED |
| Diagnostics | 27 码 + RuleId | lang-core + lang-semantic | conformance/diagnostics | VERIFIED（31/31）|
| Agent Contract | schemaVersion 1 | lang-contract | schemas 对齐 | VERIFIED |
| Effects | EFFECT-001 | lang-contract | conformance/expressions 等 | VERIFIED |
| Semantic Graph | Graph Queries | lang-graph | inspect 对齐 | VERIFIED |
| Safe Editing | Text/Workspace Edit | lang-edit | E2E | VERIFIED |
| Refactoring | Symbol Identity | lang-refactor | E2E | VERIFIED |
| Verify | Verification Pipeline | lang-verify | 四路差分 | VERIFIED |
| Conformance | 151 Cases | lang-conformance | 151/151 | VERIFIED |
| Dogfood | langalgo | lang-cli | dogfood/langalgo | VERIFIED |
