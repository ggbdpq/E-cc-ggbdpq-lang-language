# lang-go 能力矩阵 → lang-ts 对应（Phase A 侦察产物）

> 生成于 lang-ts 完整复刻收尾时；对齐基准 = go/ 参考实现 v1.0 语义收口第一批
>（commit 4aea287 时的 specs/ + conformance/ + go/internal/）。
> 状态：`OK` 已实现且验证 · `OK*` 已实现、与参考同款已知缺口 · `—` 参考实现亦未定。

## 1. 语义前端

| 能力 | Spec Rule | Go 位置 | TS 位置 | 状态 | 备注 |
|------|-----------|---------|---------|------|------|
| 码点流/记号 | LEX-001..008 | syntax/lex.go | syntax/lexer.ts | OK | 码点数组扫描；Span offset/line/col 与 rune 度量逐位对齐 |
| 注释 trivia | —（v0.5 Lossless） | syntax/comments.go | syntax/comments.ts | OK | format 100% 保留 |
| Pratt 解析 | PARSE-001..006 | syntax/parse.go | syntax/parser.ts | OK | 优先级表、续行、noStruct 禁裸 struct 字面量、错误文案逐字 |
| AST/Walk | PARSE | syntax/ast.go walk.go | syntax/ast.ts walk.ts | OK | 判别联合 + 穷尽 switch |
| 名字解析 | NAME-* | semantic/resolver.go | semantic/resolver.ts | OK | 四遍结构；E2001..E2010 文案逐字 |
| 符号模型 | — | semantic/symbol.go | semantic/symbols.ts | OK | SymbolID 编译内稳定（含预载 27 符号序） |
| 类型模型 | TYPE-KINDS/ASSIGN/ANY | types/type.go | types/model.ts | OK | assignable 逆协变 fn；Type.String() 逐字 |
| 检查器 | E3001..E3014, E4001..4003 | types/checker.go infer.go anno.go | types/checker.ts anno.ts | OK* | 诊断码+文案对齐；`if 条件非 bool` 现报 E3011（spec CTRL-IF-001 写 E3001）——参考实现同款缺口，v1.0 工作流修复中 |
| HIR | —（v0.3） | hir/hir.go build.go | hir/model.ts build.ts | OK | NodeID 先序稳定（构造点求值序逐一复刻）；字面量/区间/逻辑节点类型已对齐 |
| Core IR | —（v0.3） | ir/ir.go lower.go | ir/model.ts lower.ts | OK | Local 槽位同一性共享；闭包按引用捕获（v0.9 收口） |
| 泛型 | GEN-* | types/checker.go | types/checker.ts | OK | 擦除语义；Result 第二参须 error（E3009） |

## 2. 执行与后端

| 能力 | Rule | Go | TS | 状态 | 备注 |
|------|------|----|----|------|------|
| AST 解释器 | EVAL-*/CTRL-*/MATCH-*/RUNTIME-* | interp/ | interp/ | OK | 求值顺序（含 EVAL-ASSIGN-003 地址先于右值）、输出格式逐字节 |
| int64 语义 | LEX-005 位运算 | interp | interp/value.ts | OK | bigint 二补码回绕 + 移位 0..63 检查 |
| float 输出 | RUNTIME-OUTPUT-003 | strconv 'g' | interp/value.ts goFormatFloat | OK | 最短舍入 + exp<-4‖>=6 科学计数（对 Go 实测标定） |
| 内建 20 个 | RUNTIME | interp/builtin.go stdlib.go | interp/builtin.ts | OK | 错误文案逐字；jsonParse 数字字面保留、jsonStringify 键码点序+HTML 转义+BigInt 精度 |
| 值语义 | VALUE-COPY/SHARE/REF/CAPTURE/ELEM-009 | interp | interp ir/irinterp | OK | xs[i].f=v 读改写回写；容器读出副本 |
| 并发 | TASK-*/CHAN-* | interp + gen prelude | interp + backends/goprelude.ts | OK | 同步确定子集；chan 缓冲/阻塞语义对齐 |
| IR 解释器 | —（v0.9 四路差分） | irinterp/ | irinterp/irinterp.ts | OK | 复用值模型与内建；TryErr 边界合成；&x deref 全链 |
| →Go 后端 | — | gen/ | backends/gen.ts | OK* | 发射文本与参考等价可编译；None 实参收窄比参考多一处特化（见 spec-gaps #6） |
| →JS 后端 | decisions #47/#48/#50 | genjs/ | backends/genjs.ts | OK* | int=BigInt、全 async、langGoCopy 三时机；prelude 修正 float %v 与 JSON 编码（见 spec-gaps #1/#2） |
| gofmt 归一 | — | gen format.Source | — | — | 工具链步骤非语义；TS 产物直接保证 gofmt 兼容形态 |

## 3. 工具面

| 能力 | 契约 | Go | TS | 状态 | 备注 |
|------|------|----|----|------|------|
| 目录即包装载 | MODULE-* | load/ | loader/loader.ts | OK | 文件名序合并、目录优先解析、环/越根、行号错误逐字 |
| 管线 | — | pipeline/ | pipeline/pipeline.ts | OK | DiagList 首诊断即 Error() |
| CLI run/test/check/fmt/build/bind | — | cmd/lang-go/cli.go | cli/main.ts | OK | 自称 lang-ts（对齐 go/ 的 lang-go）；bin 名 lang-go-ts（runner contract） |
| CLI inspect/dump/fix/refactor/verify/conformance/REPL | — | 同上 | 同上 | OK | 全动词；JSON 双空格缩进 + HTML 转义（encoding/json 对齐） |
| Agent Contract | agent-protocol.md | contract/ | contract/contract.ts | OK | Envelope/Diagnostic(Symbol ruleId)/Node 形态；行级 primarySpan（line<1→0） |
| Rule 映射 | rules.md | contract/rules.go | contract/rules.ts | OK | 27 码全量同表 |
| dump ast/hir/ir | schemas | contract/dumpast.go dump.go | contract/contract.ts | OK | 键序=结构体序；未盖章 span 不输出 |
| inspect symbols/refs/type | — | contract/inspect.go | contract/contract.ts | OK | refs 排序 symbolId→file→offset；TypeAt 最内层 |
| Semantic Graph | —（v0.6） | graph/ | graph/graph.ts | OK | 节点/边/BFS/impact/effectWatch；depends_on 去重 |
| Effects | EFFECT-001 | graph/effects.go | graph/effects.ts | OK* | 仅直接效果——参考实现 symToUnit 未接线致 fixpoint 空，如实镜像（见 spec-gaps #4） |
| Safe Editing | —（v0.5） | edit/ fix/ | edit/ fix/ | OK | sha256 前置、重叠拒绝、降序应用；fix 四类 |
| Refactoring | —（v0.7） | refactor/ | refactor/refactor.ts | OK | token 扫描定位（非 Regex）、冲突保守拒绝、HIR MethodCall NameSp |
| Verify | runner-contract | conformance/verify.go | conformance/verify.ts | OK | 九阶段；四方执行三方差分；工具链缺失 skipped |
| Conformance runner | runner-contract | conformance/runner.go manifest.go | conformance/manifest.ts | OK | manifest 受限子集解析；run=四方差分、check=多重集；skipped 不计通过 |
| bind | FFI-001 | bind/ | bind/bind.ts | OK | 轻量 Go 签名扫描（参考用 go/parser）；不进 Conformance |

## 4. 验证现状（证据）

- `node dist/cli/main.js conformance ../conformance`：套件扩容至 132 case 后 **132/132，0 失败**（Final Sync；3 个 yaml 期望已由 v1.0 工作流修正为双侧实际输出）。Go 参考实现同套件 130/132——其 2 个失败（void fnlit 缺 return、genjs `--5n`）正是 lang-ts 已修、以未提交形式留证的 gen.ts/genjs.ts 两处，见 spec-gaps §D
- `node tools/examples-diff.ts`：examples 9 + m4 1 = **10/10** 三路 stdout 逐字节一致（goroutine 例为 spec 文档化平台差，按 NOTE 计）
- vitest 20/20（含三路差分冒烟、float 格式、JSON 契约、irinterp）
- 代码修复合并入库：v1.0 工作流 4dd235c（合并重写）吸收了 gen/genjs/hir/conformance 各项修复
- 已知残留缺口（双侧行为一致的镜像项）见 docs/spec-gaps.md
