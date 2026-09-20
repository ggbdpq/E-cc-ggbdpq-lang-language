# lang-go v0.4 → v1.0 分阶段开发提示词

> 适用前提  
> 当前项目已经完成 `v0.3.0 Semantic Core`。Go 继续作为第一参考实现。现有能力至少包括 Lexer、Parser、AST、Resolver、Type Checker、Typed HIR、Core IR、Interpreter、Go Backend、JS Backend、Formatter、CLI、测试与示例。  
> 当前已完成全量构建与测试，现有测试约 128 项。  
> `v0.4+` 不再继续堆语法，重点进入 Agent Contract、Safe Editing、Semantic Graph、Refactoring、Verification、Hardening 与 v1.0 收口。

---

## 总体开发原则

你是一名拥有 30 年软件工程经验的资深编程语言与编译器架构师，同时精通 Go、TypeScript、Python、Rust、Compiler Engineering、Programming Language Design、AI Agent、Coding Agent、Context Engineering 与 Developer Tooling。

请继续接管当前 `lang-go` 项目。

长期目标是设计一门同时适合 Human 与 Coding Agent 编写、理解、分析、验证、重构和自动修改的现代编程语言，同时保持传统编程语言的重要优点：

- 清晰、稳定、可预测的语法与语义
- 静态强类型
- 明确控制流
- 明确值语义与引用语义
- Result / Option / match 等结构化错误处理
- 泛型
- 模块系统
- 可测试、可调试、可编译
- 尽量减少隐式行为
- 跨后端 observable semantics 一致
- 对 Human 可读
- 对 Agent 可分析
- 对自动修改与自动验证友好

Go 是当前 Reference Implementation。未来会分别使用 Go、TypeScript、Python、Rust 实现同一门语言及其工具链。

因此任何 `v0.4+` 设计必须满足：

```text
Language Spec
    ↓
Semantic Contract
    ↓
Agent Protocol
    ↓
Conformance Suite
    ↓
Go / TypeScript / Python / Rust
```

禁止把 Go 特有行为写成语言语义。每一个阶段只实现当前版本目标，禁止提前把后续所有能力一次性做完。

---

# Prompt 01：v0.4 Agent Contract

你现在只执行：

```text
v0.4 Agent Contract
```

当前 `v0.3.0` 已经建立 Semantic Core：

```text
Source
 ↓
Lexer
 ↓
Parser
 ↓
AST
 ↓
Resolver
 ↓
Type Checker
 ↓
Typed HIR
 ↓
Core IR
 ├─ Interpreter
 ├─ Go Backend
 └─ JavaScript Backend
```

本轮目标是让 Coding Agent 可以通过稳定、机器可读、版本化的接口理解 lang-go 程序，而无需直接解析编译器内部 Go struct。

先完整检查 CLI、AST/HIR/IR、Resolver、NodeId/SymbolId、diagnostics、source span、module/import、interpreter、Go backend、JS backend、formatter、tests、specs、ADR 与 roadmap，并先生成：

```text
docs/plans/v0.4-agent-contract-plan.md
```

该计划必须包含 Current State、Existing Semantic Data、Public Contract Boundary、JSON Protocol Design、ID Stability Strategy、SourceSpan Strategy、Diagnostic Schema、Effect Metadata Design、CLI Design、Compatibility Strategy、Test Strategy、File Change Map、Commit Plan、Risks、Definition of Done。完成勘察后再实施。

建立独立 Agent Contract。Agent 只能依赖公开 Contract，不允许依赖 Go 内部 struct 字段、临时 debug dump、不稳定 formatter 输出、backend 私有类型和 interpreter 内部对象。

建议架构：

```text
Compiler Internal Model
        ↓
Stable Agent Contract
        ↓
JSON / CLI
```

至少实现：

```bash
lang-go inspect symbols --json
lang-go inspect refs --symbol <symbol-id> --json
lang-go inspect type --file <file> --line <n> --column <n> --json
lang-go inspect dependencies --json
lang-go inspect effects --json
lang-go dump ast --json
lang-go dump hir --json
lang-go dump ir --json
lang-go check --json
```

所有机器接口必须 deterministic、versioned、documented、testable，并在 v0.4.x 内保持兼容。

统一 JSON Schema。顶层至少包含：

```text
schemaVersion
toolVersion
kind
```

SourceSpan 统一格式：

```json
{
  "file": "src/main.lang-go",
  "start": { "offset": 0, "line": 1, "column": 1 },
  "end": { "offset": 12, "line": 1, "column": 13 }
}
```

Symbol 至少包含 symbolId、name、kind、definition、type、visibility、module。Node 至少包含 nodeId、kind、span、type。Reference 至少包含 symbolId、span、referenceKind。Diagnostic 至少包含 code、severity、message、primarySpan、relatedSpans、help。diagnostic code 必须稳定。

明确 NodeId / SymbolId 稳定性。禁止把内存地址或简单 traversal index 暴露给 Agent。至少保证同一份源码内容、同一 compiler version 下多次运行结果稳定。如果跨编辑版本稳定成本过高，v0.4 不强求永久稳定，但必须写进 spec。

实现轻量 Effect Metadata：

```text
PURE
IO
FS
NET
TIME
RANDOM
SPAWN
CHANNEL
FFI
```

不要实现复杂 Effect Type System。需要能够回答 function 是否纯、是否产生 IO、是否访问 filesystem/network、是否依赖 time/random、是否启动 task、是否操作 channel、是否进入 FFI。Effects 必须从 Semantic Core / HIR / IR 推导，禁止通过源码字符串搜索判断。

新增规范，例如：

```text
specs/agent-protocol.md
specs/diagnostics.md
specs/source-span.md
schemas/inspect.schema.json
schemas/diagnostics.schema.json
```

Schema 不要过度碎片化。

测试必须覆盖 JSON golden、schema、ID deterministic、source span、diagnostic compatibility、effect inference、CLI E2E、invalid input。现有约 128 项测试不能被破坏。

本轮禁止 LSP、IDE 插件、Semantic Graph 全量实现、自动重构、safe edit、macro、debugger、package manager、新语言语法、新 backend。

Definition of Done：Agent 不读取 Go 内部 struct，也能理解 symbols、refs、types、diagnostics、AST/HIR/IR、dependencies、effects。JSON schema 已版本化，输出 deterministic，diagnostics 稳定，tests 全绿，spec/roadmap/ADR 已更新，`lang-go check --json` 不依赖 JS backend，且没有 backend-specific semantic logic 泄漏进 Agent Contract。

完成后打：

```text
v0.4.0
```

最终报告：v0.4 完成情况、Agent Contract 架构图、JSON Protocol 示例、CLI 能力矩阵、Effect Metadata 能力矩阵、测试结果、新增/修改文件、遗留问题、是否具备进入 v0.5 条件。

---

# Prompt 02：v0.5 Safe Editing

你现在只执行：

```text
v0.5 Safe Editing
```

前提是 `v0.4 Agent Contract` 已完成，symbols / refs / types / diagnostics / effects 可通过稳定 Contract 获取，AST / HIR / IR 可机器读取，NodeId / SymbolId 已存在稳定策略。

本轮目标是让 Coding Agent 可以在尽可能低 diff、可验证、可回滚的条件下安全修改 lang-go 源码。

先审计 Parser / Formatter，重点调查 token、comments、whitespace、source span、CRLF/LF、Unicode、trailing/block/import comments 等保留情况，并写：

```text
docs/plans/v0.5-safe-editing-plan.md
```

建立 Lossless Syntax Layer，至少保留 tokens、comments、whitespace、source ranges、trivia。不要把所有 trivia 强行塞入 Semantic AST。推荐：

```text
Source Text
    ↓
Lossless Syntax Tree / Token Tree
    ↓
Semantic AST
    ↓
HIR
```

Lossless Syntax 面向 formatter、patch、safe edit、refactor。Semantic AST 面向 type checking 与 semantic analysis。若当前规模更适合 token-preserving model，可采用更轻量方案，但必须写清权衡。

Formatter 必须满足：

```text
format(format(source)) == format(source)
```

并保证 comment retention、deterministic output、semantic equivalence、stable newline handling、stable import formatting、stable indentation、minimal noise。

建立结构化 Edit：

```text
TextEdit
  file
  startOffset
  endOffset
  replacement
```

进一步支持：

```text
WorkspaceEdit
  edits[]
  preconditions[]
```

precondition 可包含 fileHash、expectedText、symbolId、compilerVersion，防止 Agent 在源码已变化时误写。

实现：

```bash
lang-go fix --safe
lang-go fix --safe --json
lang-go fix --safe --dry-run
```

v0.5 只允许机械证明安全的 fix，例如 unused import、确定性 trivial syntax normalization 等。不要自动做 API 改名、change signature、move symbol、复杂控制流重构。

定义 Agent Edit Transaction：

```text
inspect
  ↓
plan
  ↓
precondition check
  ↓
edit
  ↓
format
  ↓
parse
  ↓
resolve
  ↓
typecheck
  ↓
test
  ↓
verify
```

任一步失败立即 STOP，并输出结构化原因。禁止 silent fallback。

明确 edit 后 NodeId、SymbolId、SourceSpan、diagnostics 如何刷新。不要强求所有 NodeId 永久不变，重点保证修改前 Contract 清晰、修改后可重新 inspect、edit transaction 可验证。

测试至少覆盖 comments 100% retention、formatter idempotence、semantic equivalence、empty/invalid file、Unicode、CRLF、line/trailing/import comments、edit conflict、file hash mismatch、multi-edit、dry-run、safe fix E2E。

本轮禁止 semantic rename、extract function、move symbol、full Semantic Graph、LSP、IDE、package registry、新语法。

完成条件：Coding Agent 可以读取源码 → 获取 semantic context → 产生结构化 edit → 校验 precondition → 修改 → format → check → test → verify，且 comments 不丢失、formatter 幂等、edit deterministic、safe fix 可 dry-run、全量测试通过。

完成后打：

```text
v0.5.0
```

---

# Prompt 03：v0.6 Semantic Graph

你现在只执行：

```text
v0.6 Semantic Graph
```

目标是在 Semantic Core 与 Agent Contract 上建立真实语义关系图，让 Human 与 Agent 能可靠理解代码之间的定义、引用、调用、读写和依赖关系。

先生成：

```text
docs/plans/v0.6-semantic-graph-plan.md
```

Graph Node 至少支持 Module、File、Function、Variable、Parameter、Struct、Field、Method、Enum、Variant、Type。每个节点关联 symbolId、kind、name、span、module，必要时关联 type、effects、visibility。

Graph Edge 至少支持：

```text
defines
references
calls
imports
returns
reads
writes
contains
depends_on
```

只增加有明确查询价值的 edge。

Graph 必须来源于 Resolver、Type Checker、HIR、IR、Effect Metadata。grep/regex 只能辅助候选发现，禁止作为最终语义真值。

至少实现：

```bash
lang-go inspect callers <symbol>
lang-go inspect callees <symbol>
lang-go inspect references <symbol>
lang-go inspect dependency <symbol-or-module>
lang-go inspect impact <symbol>
```

全部支持 `--json`，并至少支持 `--depth`。impact 区分 direct 与 transitive。

第一版 Impact Analysis 重点回答：如果修改这个 Symbol，哪些地方可能受到影响？区分 definition impact、reference impact、call impact、type impact、module impact、effect impact，并输出 evidence。禁止宣称能够自动证明所有影响。

v0.6 优先使用 in-memory graph，不要提前引入 graph database、external database、vector database、persistent daemon，除非真实规模证明有必要。

测试覆盖 graph node/edge、callers/callees、cross-module refs、read/write、recursion、mutual recursion、module cycle、shadowing、generic call、method call、effect propagation、impact golden tests。

Definition of Done：Agent 能回答一个 symbol 在哪里定义、谁引用、谁调用、它调用谁、它读写什么、它依赖什么、修改它可能影响什么，并通过稳定 JSON 返回 evidence。

完成后打：

```text
v0.6.0
```

---

# Prompt 04：v0.7 Refactoring

你现在只执行：

```text
v0.7 Refactoring
```

目标是基于 SymbolId、Semantic Graph 与 Safe Editing 实现真正语义级重构。

先输出：

```text
docs/plans/v0.7-refactoring-plan.md
```

优先级严格按照：

```text
P0 rename symbol
P0 organize imports
P0 safe remove
P1 change signature
P1 move symbol
P2 extract function
P2 inline variable
```

如果时间有限，优先把 P0 做到可靠。

Rename：

```bash
lang-go refactor rename --symbol <symbol-id> --to NewName
```

支持 `--dry-run`、`--json`。必须基于 SymbolId + Semantic References，禁止 Regex Rename。必须正确处理 shadowing、same-name symbols、imports、methods、fields、local variables、parameters、cross-file refs、cross-module refs。

Organize Imports 处理 unused、duplicated、deterministic ordering、comment preservation。

Safe Remove 只有 Semantic Graph 证明 no reachable references 时才允许执行。不确定就 REFUSE，不要猜。

Change Signature 如果实现，必须同步更新合法 call sites；不确定时拒绝执行。

统一 Refactoring Pipeline：

```text
Resolve Target
 ↓
Impact Analysis
 ↓
Build WorkspaceEdit
 ↓
Dry Run
 ↓
Precondition Check
 ↓
Apply
 ↓
Format
 ↓
Parse
 ↓
Resolve
 ↓
Type Check
 ↓
Verify
```

失败立即停止。

不要泛化成数学意义的完全 semantic equivalence。针对每种重构定义具体 invariant。例如 rename 保持 symbol identity/reference set/type behavior，organize imports 保持 semantic graph 不变。

测试覆盖 rename local/global/field/method/cross-module、shadowing、collision detection、invalid target、organize import comment retention、safe remove refusal、dry-run、conflict、完整 E2E。

完成后打：

```text
v0.7.0
```

---

# Prompt 05：v0.8 Verification

你现在只执行：

```text
v0.8 Verification
```

目标是建立统一、可复用、跨实现的 Verification 与 Conformance 基础设施，为未来 Go、TypeScript、Python、Rust 四套实现提供共同真值。

先生成：

```text
docs/plans/v0.8-verification-plan.md
```

建立 Verification Pipeline：

```text
Parse
 ↓
Resolve
 ↓
Type Check
 ↓
Effect Check
 ↓
HIR Validate
 ↓
IR Validate
 ↓
Compile
 ↓
Execute
 ↓
Differential Test
 ↓
Conformance Test
```

新增：

```bash
lang-go verify
lang-go verify --json
```

新增顶层 `conformance/`，独立于 Go implementation。建议分类：syntax、types、generics、struct、enum、match、option、result、closure、module、collections、io、concurrency、diagnostics。

每个 case 至少声明 source、expected stdout、expected stderr、expected exitCode、expected diagnostics，必要时加入 expected semantic facts。

建立简单、跨语言、无 Go 专属概念的 manifest，例如：

```yaml
name: result-propagation-basic
mode: run
expect:
  exitCode: 0
  stdout: "ok\n"
```

继续加强 Interpreter vs Go Backend vs JS Backend Differential Test。同一个合法程序的 stdout、stderr、exit code、observable behavior 必须一致，除非 spec 明确允许 platform-dependent behavior。所有例外都要文档化。

非法程序也纳入 Diagnostic Conformance，验证 diagnostic code、primary span、severity。message 文本允许轻微变化，但 code 不应漂移。

为未来四套实现定义 runner contract：compile、check、run、inspect、verify。本轮无需实现 TS/Python/Rust compiler。

完成条件：语言正确性不再由 Go implementation 自己宣布，Conformance Suite 成为跨实现契约。

完成后打：

```text
v0.8.0
```

---

# Prompt 06：v0.9 Hardening

你现在只执行：

```text
v0.9 Hardening
```

目标是停止增加主要语言能力，对 parser、semantic core、formatter、Agent Contract、refactor、verification 做稳定性、性能与安全性收口。

先生成：

```text
docs/plans/v0.9-hardening-plan.md
```

进入 v0.9 后冻结主要 feature。禁止主动增加新 syntax、新 type feature、新 backend、macro、inheritance、reflection、HKT、package manager、LSP，除非修复 v1.0 blocker。

Fuzz 至少覆盖 lexer、parser、formatter、type checker、HIR lowering、IR validation、JSON protocol，重点检测 panic、infinite loop、stack overflow、non-determinism、corrupted span、formatter instability。

Adversarial Inputs 至少覆盖 empty file、malformed syntax、huge identifier/string/comment、deep nesting、recursive types/calls、generic explosion、Unicode identifiers、invalid UTF-8 handling、cyclic imports、channel misuse、concurrency edge cases、comment-only files。

建立 performance benchmark baseline：lex、parse、resolve、typecheck、HIR、IR、interpret、Go generation、JS generation、format、inspect、semantic graph、refactor rename、verify。准备 small / medium / large 三档 corpus，记录 time、allocations、peak memory，并结合 Go 能力合理测量。

进行架构清理，重点审查 God files、duplicated helper、backend-specific semantics、duplicated type logic、duplicated diagnostics、duplicated span handling、compatibility hacks、dead code、unreachable code、stale docs、temporary migration layer。

原则：删代码优先于增加 abstraction，但禁止为了 LOC 指标牺牲可读性。

做 Determinism Audit。同一输入多次运行，formatter output、JSON ordering、symbol ordering、diagnostic ordering、graph ordering、generated Go、generated JS 必须稳定。

彻底解决 README、spec、roadmap、ADR、CLI help、version 漂移，建立单一版本源。

v0.9 Gate：no known P0 bug、no known semantic divergence、no known formatter comment loss、no unstable schema、no untracked diagnostic change、conformance 全绿、differential 全绿、fuzz 无已知 crash、benchmark baseline 已建立。

完成后打：

```text
v0.9.0
```

---

# Prompt 07：v1.0 Release Candidate 与正式发布

你现在执行：

```text
v1.0
```

目标是把当前语言从实验性 v0.x 收口成第一版稳定语言与工具链契约。不要新增大功能。

先生成：

```text
docs/plans/v1.0-release-plan.md
```

正式冻结：

```text
Core Syntax
Semantic Model
Type System
Error Model
Module Semantics
Concurrency Semantics
Agent Contract v1
Diagnostic Contract v1
Conformance Format v1
```

冻结意味着 v1.x 中任何破坏性变更都必须有 migration / compatibility policy。

确认完整工具链：

```text
Lexer
Parser
Lossless Syntax
AST
Resolver
Type Checker
Typed HIR
Core IR
Interpreter
Go Backend
JavaScript Backend
Formatter
Inspector
Semantic Graph
Refactoring
Verifier
```

每一层必须职责清晰、有测试、有文档、无不必要循环依赖。

Human Experience 需要验证新用户能完成 install、hello world、module、function、struct、enum、Result、Option、match、generic、collection、IO、test、build、run、format、check，并在 README 建立最小学习路径。

Agent Experience 需要验证 Coding Agent 可以完成 inspect symbol/refs/type/effects/impact、safe edit、rename、organize imports、verify，全程不需要读取 Go compiler internal struct。

写清 Compatibility Policy：language compatibility、CLI compatibility、Agent JSON compatibility、diagnostic compatibility、conformance compatibility，以及 SemVer、deprecation、breaking change policy。

建立 v1.0 conformance baseline。未来 Go、TypeScript、Python、Rust 实现首先对齐该 baseline。

先发布：

```text
v1.0.0-rc.1
```

RC 阶段只修 bug、semantic inconsistency、documentation error、compatibility issue，不继续加 feature。完成 RC 验证后发布：

```text
v1.0.0
```

最终报告必须包含 Language Feature Matrix、Compiler Architecture、Human Workflow、Agent Workflow、Semantic Guarantees、Conformance Coverage、Differential Test Result、Fuzz Result、Benchmark Baseline、Known Limitations、Compatibility Policy、Post-v1.0 Roadmap。

---

## 推荐实际执行顺序

不要把以上 7 个 Prompt 一次交给 Coding Agent。推荐逐版本执行：

```text
v0.4 → 完成 + review + tag
v0.5 → 完成 + review + tag
v0.6 → 完成 + review + tag
v0.7 → 完成 + review + tag
v0.8 → 完成 + review + tag
v0.9 → 完成 + review + tag
v1.0
```

每一轮都重新读取上一版本最新源码，不要假设上一版计划中的文件结构仍然完全成立。

每个版本结束前执行：

```text
1. gofmt / formatter
2. go test ./...
3. differential tests
4. conformance tests（v0.8 起）
5. CLI E2E
6. doc drift check
7. version check
8. git status check
```

然后输出：

```text
Version:
Goal:
Status:
Architecture Changes:
Tests:
LOC Changes:
New Files:
Deleted Files:
Breaking Changes:
Remaining Debt:
Known Limitations:
Next Version Gate:
```

只有满足当前版本 Definition of Done，才进入下一版本。

---

## 长期多语言实现原则

Go 版本完成 v1.0 后，再开始独立实现：

```text
implementations/
├── go/
├── rust/
├── typescript/
└── python/
```

推荐顺序：

```text
Go Reference Implementation
        ↓
Rust Implementation
        ↓
TypeScript Implementation
        ↓
Python Implementation
```

每个实现使用宿主语言最自然的工程方式：

```text
Go         → struct / interface
Rust       → enum / trait
TypeScript → discriminated union
Python     → dataclass / protocol
```

不要逐行翻译 Go。唯一必须一致的是：Language Semantics、Diagnostics Contract、Agent Protocol、Conformance Results、Observable Behavior。

最终形成：

```text
             Language Spec
                   │
          Conformance Suite
                   │
      ┌────────────┼────────────┐
      ↓            ↓            ↓
     Go           Rust          TS
      │                          │
      └────────── Python ────────┘
```

形成真正独立的多实现验证体系。
