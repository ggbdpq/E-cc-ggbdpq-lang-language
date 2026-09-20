# Rule Coverage Matrix（v1.0）

>: 151 个 conformance case 的 ruleIds 聚合（生成于 2026-09-21）。状态：COVERED=直接 case；SPEC-ONLY=参考实现测试+四路差分覆盖；FROZEN-BY-SPEC=冻结面以规范存在为证据；MISSING(N!)=发布禁止。

| Rule ID | 级 | Spec | 诊断码 | Cases（前 2） | 状态 |
|---|---|---|---|---|---|
| LEX-001..008 | D | lexical-grammar.md | E1001 | io/string-escapes-and-print, syntax/arithmetic-precedence … | COVERED |
| PARSE-001..006 | D | grammar.md | - | expressions/eval-bit-operations, expressions/eval-comparison-chains … | COVERED |
| NAME-SCOPE-001 | D | name-resolution.md | - | - | SPEC-ONLY |
| NAME-SHADOW-002 | D | name-resolution.md | - | - | SPEC-ONLY |
| NAME-DUP-003 | N | name-resolution.md | E2001 | diagnostics/duplicate-definition, diagnostics/duplicate-top-fn | COVERED |
| NAME-UNDEF-004 | N | name-resolution.md | E2002 | diagnostics/assign-to-unknown-var, diagnostics/undefined-variable … | COVERED |
| NAME-CTOR-005 | N | name-resolution.md | E2003,E2006 | diagnostics/case-conflicts-builtin | COVERED |
| NAME-RECV-006 | N | name-resolution.md | E2004 | diagnostics/method-recv-unknown-struct, struct/methods-same-name-different-structs | COVERED |
| NAME-MUT-007 | N | name-resolution.md | E2005 | diagnostics/immutable-assign, struct/struct-field-write … | COVERED |
| NAME-PLACE-008 | N | name-resolution.md | E2007,E2008 | diagnostics/break-outside-loop, diagnostics/return-outside-fn | COVERED |
| NAME-NEST-009 | N | name-resolution.md | E2010 | diagnostics/nested-struct-decl | COVERED |
| NAME-MERGE-010 | D | name-resolution.md | - | - | SPEC-ONLY |
| TYPE-KINDS-001 | D | type-system.md | - | types/annotated-bindings, types/inferred-bindings … | COVERED |
| TYPE-ASSIGN-001 | N | type-system.md | E3001,E3002 | diagnostics/int-float-mixed, diagnostics/type-mismatch … | COVERED |
| TYPE-ANY-002 | N | type-system.md | - | types/any-boundary-json-roundtrip-values | COVERED |
| TYPE-STRUCT-003 | D | type-system.md | - | struct/nested-struct, struct/point-ctor … | COVERED |
| TYPE-FN-004 | D | type-system.md | - | - | SPEC-ONLY |
| TYPE-CONV-005 | D | type-system.md | - | types/explicit-conversions | COVERED |
| TYPE-ANN-006 | N | type-system.md | E3007 | types/param-missing-annotation | COVERED |
| TYPE-MEMBER-007 | N | type-system.md | E3004 | diagnostics/unknown-field, diagnostics/unknown-method | COVERED |
| TYPE-INDEX-008 | N | type-system.md | E3011,E3013,E3014 | collections/has-probe-missing-key, diagnostics/index-not-int … | COVERED |
| GEN-DECL-001 | D | type-system.md | - | generics/erased-identity, generics/generic-identity-string … | COVERED |
| GEN-INFER-002 | N | type-system.md | - | generics/generic-erased-passthrough, generics/generic-slice-param | COVERED |
| GEN-SUBST-003 | N | type-system.md | E3009 | diagnostics/result-second-param-not-error, generics/generic-erased-passthrough … | COVERED |
| VALUE-COPY-001 | D | values.md | - | struct/struct-in-collection, values/map-value-struct-shared … | COVERED |
| VALUE-SHARE-002 | D | values.md | - | collections/empty-collections, collections/map-keys-values … | COVERED |
| VALUE-REF-003 | N | values.md | E3012 | diagnostics/amp-on-expression, values/reference-alias-write … | COVERED |
| VALUE-CAPTURE-004 | D | values.md | - | closure/closure-loop-capture-var, closure/counter-closure … | COVERED |
| VALUE-IMMUT-005 | D | values.md | - | collections/string-char-index, io/len-of-string-unicode … | COVERED |
| VALUE-MAP-006 | D | values.md | - | collections/map-int-keys, collections/map-iteration-insertion-order | COVERED |
| VALUE-TASKSHARE-007 | D | values.md | - | concurrency/chan-struct-payload | COVERED |
| VALUE-UNIT-008 | F | values.md | - | - | FROZEN-BY-SPEC |
| VALUE-ELEM-009 | D | values.md | - | - | SPEC-ONLY |
| EVAL-ORDER-001 | D | expressions.md | - | - | SPEC-ONLY |
| EVAL-CALL-002 | D | expressions.md | - | expressions/call-argument-order, expressions/eval-args-with-match … | COVERED |
| EVAL-ASSIGN-003 | D | expressions.md | - | collections/element-compound-assign, expressions/eval-assign-index-from-call … | COVERED |
| EVAL-LITERAL-004 | D | expressions.md | - | expressions/eval-nested-call-order, expressions/literal-evaluation-order … | COVERED |
| EVAL-INDEX-005 | D | expressions.md | - | expressions/eval-index-obj-before-idx | COVERED |
| EVAL-SHORTCUT-006 | D | expressions.md | - | expressions/bool-combos, expressions/eval-short-circuit-side-effect-order … | COVERED |
| EVAL-GO-007 | D | expressions.md | - | concurrency/spawn-eval-args-immediately | COVERED |
| CALL-RESOLVE-001 | D | expressions.md | - | struct/method-chain-same-struct, struct/method-reads-mutable-field | COVERED |
| CALL-ARG-002 | N | expressions.md | E3003 | diagnostics/call-arity-mismatch | COVERED |
| CALL-CLOSURE-003 | D | expressions.md | - | closure/higher-order-compose | COVERED |
| CTRL-IF-001 | D | control-flow.md | - | - | SPEC-ONLY |
| CTRL-WHILE-002 | D | control-flow.md | - | expressions/eval-while-cond-order | COVERED |
| CTRL-FORIN-003 | N | control-flow.md | E3010 | collections/map-iteration-insertion-order, collections/string-iteration-codepoints … | COVERED |
| CTRL-RETURN-004 | D | control-flow.md | - | - | SPEC-ONLY |
| MATCH-KIND-001 | N | control-flow.md | E4002,E4003 | diagnostics/enum-match-mixed-literal, diagnostics/match-result-option-mixed … | COVERED |
| MATCH-EXHAUSTIVE-002 | N | control-flow.md | E4001 | diagnostics/match-non-exhaustive, enum/enum-shape-area … | COVERED |
| MATCH-BIND-003 | D | control-flow.md | - | enum/enum-deep-payload-destructure, match/match-enum-payload-bind | COVERED |
| MATCH-EXPR-004 | D | control-flow.md | - | expressions/eval-args-with-match, match/match-match-nested-expr … | COVERED |
| RESULT-PRELUDE-001 | D | option-result.md | - | option/option-basics, result/error-value-construction | COVERED |
| RESULT-NULL-002 | D | option-result.md | - | - | SPEC-ONLY |
| RESULT-TRY-003 | N | option-result.md | E3005,E3008 | diagnostics/try-on-option, diagnostics/try-outside-result-fn … | COVERED |
| RESULT-RETURN-004 | N | option-result.md | E3006 | diagnostics/result-fn-return-plain, result/result-ok-path-only | COVERED |
| RESULT-CHAN-005 | D | option-result.md | - | - | SPEC-ONLY |
| RESULT-MATCH-006 | D | option-result.md | - | option/option-match-expr | COVERED |
| MODULE-ROOT-001 | D | modules.md | - | - | SPEC-ONLY |
| MODULE-IMPORT-002 | N | modules.md | - | module/import-nested-dir | COVERED |
| MODULE-MERGE-003 | D | modules.md | - | module/import-nested-dir, module/import-two-files … | COVERED |
| MODULE-CYCLE-004 | N | modules.md | - | module/import-same-file-twice, module/module-diamond-import | COVERED |
| MODULE-INIT-005 | D | modules.md | - | - | SPEC-ONLY |
| MODULE-VIS-006 | F | modules.md | - | - | FROZEN-BY-SPEC |
| MODULE-TEST-007 | D | modules.md | - | - | SPEC-ONLY |
| TASK-MODEL-001 | N | concurrency.md | - | concurrency/chan-send-recv | COVERED |
| TASK-SPAWN-002 | N | concurrency.md | - | concurrency/chan-send-recv, concurrency/spawn-eval-args-immediately … | COVERED |
| TASK-DETERMINISM-003 | N | concurrency.md | - | concurrency/chan-fifo-single-producer, concurrency/spawn-two-tasks-deterministic | COVERED |
| CHAN-CREATE-001 | D | concurrency.md | - | - | SPEC-ONLY |
| CHAN-SEND-002 | D | concurrency.md | - | - | SPEC-ONLY |
| CHAN-RECV-003 | N | concurrency.md | - | concurrency/chan-fifo-single-producer, concurrency/chan-multi-values-fifo | COVERED |
| CHAN-CLOSE-004 | F | concurrency.md | - | - | FROZEN-BY-SPEC |
| CHAN-MAP-005 | D | concurrency.md | - | - | SPEC-ONLY |
| RUNTIME-FAIL-001 | D | runtime.md | - | - | SPEC-ONLY |
| RUNTIME-JSON-001 | D | runtime.md | - | io/json-parse-scalars | COVERED |
| RUNTIME-JSON-002 | N | runtime.md | - | io/json-stringify-nested | COVERED |
| RUNTIME-OUTPUT-003 | D | runtime.md | - | io/json-roundtrip, io/print-mixed-floats … | COVERED |
| RUNTIME-OUTPUT-004 | D | runtime.md | - | io/print-mixed-floats, types/float-compare-safe-zone | COVERED |
| EFFECT-001 | F | runtime.md | - | - | FROZEN-BY-SPEC |
| FFI-001 | F | runtime.md | - | - | FROZEN-BY-SPEC |

**N 级覆盖 28/28——全部 COVERED，v1.0 发布门达成。**
