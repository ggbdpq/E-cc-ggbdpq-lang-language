# lang-rs Implementation Map

Go/TS/Python 证据 → Rust 归属映射。各 Agent 在登记冻结接口时同步补行。

| 能力 | Go（参考） | TypeScript | Python | Rust 归属 |
|---|---|---|---|---|
| Source/Span | internal/syntax | src/syntax/span.ts | lang_py/syntax | lang-core |
| 诊断契约 | internal/contract | src/contract | lang_py/contract | lang-core + lang-contract |
| Lexer | internal/syntax | src/syntax/lexer.ts | lang_py/syntax/lexer | lang-syntax |
| Parser/AST | internal/syntax | src/syntax/{parser,ast}.ts | lang_py/syntax | lang-syntax |
| Resolver | internal/semantic | src/semantic | lang_py/semantic | lang-semantic |
| Checker | internal/types | src/types | lang_py/types | lang-types + lang-semantic |
| HIR | internal/hir | src/hir | lang_py/hir | lang-hir |
| IR | internal/ir | src/ir | lang_py/ir | lang-ir |
| 解释器 | internal/interp | src/interp | lang_py/interp | lang-interp |
| IR 解释器 | internal/irinterp | src/irinterp | lang_py/irinterp | lang-interp |
| →Go | internal/gen | src/backends/gen | lang_py/backends/go_backend | lang-backend-go |
| →JS | internal/genjs | src/backends/genjs | lang_py/backends/js_backend | lang-backend-js |
| Loader | internal/load | src/loader | lang_py/loader | lang-loader |
| Formatter | internal/format | src/format | lang_py/format | lang-formatter |
| Agent Contract/Graph/Edit/Refactor | internal/contract | src/{contract,graph,edit} | lang_py/* | lang-contract/graph/edit/refactor |
| Verify/Conformance | internal/conformance | src/cli + tests | lang_py/conformance | lang-verify + lang-conformance |
| CLI | cmd/lang-go | src/cli | lang_py/cli | lang-cli |
