# lang-go Human × Coding Agent 编程语言开发计划

> 版本：v1.0 规划稿  
> 日期：2026-09-20  
> 适用仓库：`vibe/labs/language/go/` 及未来语言级 `specs/`、`docs/` 目录  
> 核心目标：设计一门同时适合 Human 与 Coding Agent 编写、分析、验证和安全修改，同时保留传统编程语言核心优点的小型通用语言。

---

## 0. 当前基线

lang-go 已经具备真实语言工程的基本骨架：

| 能力 | 当前状态 |
|---|---|
| Lexer / Parser / AST | 已实现 |
| 树遍历 Interpreter / REPL | 已实现 |
| Go 转译后端 | 已实现 |
| JavaScript 转译后端 | 已实现 |
| `run / test / check / fmt / build / bind` | 已具备 |
| `struct / enum / match / Result / Option / ? / closure / generic` | 已具备核心形态 |
| `go / chan / send / recv` | 已进入第一版语义 |
| Go / JS / Interpreter 差分验证 | 已具备 |
| Go 测试 | 当前约 91 个 Test |
| 真实项目移植 | 已用 bilibili/linkpull 纯函数层验证 |

当前最重要的技术债务集中在五处：

1. `check` 仍依赖转译器承担部分语义检查，独立 Type Checker 尚未形成。
2. Interpreter、Go backend、JS backend 直接理解 AST，语义逻辑容易三份复制。
3. 并发模型仍存在宿主差异，`go`、`recv` 的正式语言语义需要重新统一。
4. AST 格式化会损失部分注释，无法满足 Coding Agent 高频自动修改的长期要求。
5. `specs/` 尚未成为真正唯一的语言语义真值源，版本和状态存在文档漂移风险。

因此从 v0.3 开始，原则上冻结大部分新语法，工作重点从“增加 Feature”切换为“建立 Semantic Core”。

---

# 1. 语言北极星

lang-go 的长期定位建议调整为：

> **A deterministic, typed, agent-operable programming language for humans and coding agents.**
>
> 一门确定性优先、静态类型、Human 可读、Agent 可操作、机器可验证、可安全自动修改的编程语言。

它需要同时服务三个对象。

| 对象 | 首要诉求 |
|---|---|
| Human | 易读、易写、容易局部推理、错误清晰、生态可用 |
| Coding Agent | 结构稳定、语义显式、机器接口稳定、修改后容易验证 |
| Compiler / Tooling | 语义单一、后端解耦、确定性强、测试可以机械判定 |

语言的独特价值不应依赖奇特语法。真正的差异化应该来自四个属性：

**Readable、Inspectable、Verifiable、Editable。**

也就是“人能读、机器能查、行为能验、Agent 能安全改”。

---

# 2. 必须长期保留的传统语言优点

Agent Friendly 不能以牺牲传统语言工程质量为代价。以下能力应视为长期不变量。

## 2.1 静态类型

编译期尽量发现类型错误，运行时动态能力只允许出现在明确边界，例如 JSON、FFI、宿主对象。

`any` 应成为一种受控逃生舱，而不能逐渐侵蚀整个类型系统。

## 2.2 显式控制流

保留普通 `if / for / match / return / fn`。

避免大量 DSL、隐式生命周期、隐式控制跳转和宏生成控制流。Human 与 Agent 都需要能够通过局部源码理解程序。

## 2.3 明确错误模型

继续坚持：

```text
Result[T, E]
Option[T]
?
match
```

避免异常成为默认业务控制流，也继续维持无 `null` 方向。

## 2.4 可预测值语义

建议继续保留当前核心：

```text
struct       值语义
array/slice  共享容器语义
map          共享容器语义
string       不可变
&T           显式共享可变
```

任何隐式复制、隐式引用、隐式所有权转换都应该慎重。

## 2.5 稳定模块边界

文件、目录、模块、import 的关系必须简单且稳定。不要为了 Agent 新增隐藏模块系统。

## 2.6 宿主生态互操作

Go 和 JavaScript 生态依然是 lang-go 的现实杠杆。

继续采用 transpile-first 与 extern 思路，让语言集中解决语义和开发体验，不重复制造 HTTP、JSON、TLS、数据库驱动等成熟基础设施。

## 2.7 向后兼容意识

进入 v0.5 后，每次语义变更必须说明：

```text
breaking
compatible
implementation-only
experimental
```

v1.0 以后语言规范需要明确兼容策略。

---

# 3. Human × Agent 双重设计原则

## 3.1 一个语义只定义一次

禁止 Interpreter、Go Generator、JS Generator 各自重新解释高级语言规则。

目标流水线：

```text
Source
  ↓
Lexer
  ↓
Lossless Syntax + AST
  ↓
Resolver
  ↓
Type Checker
  ↓
Typed HIR
  ↓
Lowering
  ↓
Core IR
  ├─ Interpreter
  ├─ Go Backend
  └─ JS Backend
```

高级语言语义应在 Resolver、Type Checker、HIR 和 Lowering 阶段完成。

后端只负责目标平台映射。

## 3.2 Canonical Source

同一程序应该尽量只有一种官方格式。

```text
parse(format(parse(source)))
```

必须稳定。

Formatter 需要满足：

```text
幂等
语义不变
注释不丢
顺序稳定
跨平台稳定
```

这会显著降低 Agent 修改代码时的 diff 噪音。

## 3.3 Explicit over Clever

优先显式类型边界、显式错误、显式副作用、显式宿主互操作。

避免：

```text
隐式数字转换
隐式 null
隐式异常
隐式 import
隐式异步传播
隐式全局状态
```

## 3.4 Determinism First

确定性直接决定 Coding Agent 能否可靠验证修改。

默认要求：

```text
Formatter 输出确定
Compiler diagnostics 排序确定
Map 序列化确定
Build 输出确定
Test 顺序确定
AST/IR JSON 输出确定
相同输入得到相同语义结果
```

对于时间、随机数、网络、并发等天然非确定能力，需要明确进入 Effect Metadata。

## 3.5 Machine Interface 与 Human Syntax 解耦

Human 使用普通源码。

Agent 优先使用编译器提供的机器接口理解源码。

不要强迫 Agent 通过正则、grep 或猜测文本结构才能回答“这个变量是什么类型”“这个函数被谁调用”。

---

# 4. v0.3 核心目标：Semantic Core

建议周期：第 1 到第 4 周。

v0.3 是当前项目最重要的一次架构升级。

## 4.1 新建 Resolver

建议目录：

```text
internal/semantic/
  resolver.go
  scope.go
  symbol.go
  diagnostic.go
```

Resolver 负责：

1. 变量声明与引用绑定。
2. 函数声明与调用绑定。
3. struct / enum / constructor 名称解析。
4. import 后符号来源解析。
5. 重名、遮蔽、未定义名称检查。
6. 每个符号分配本次编译稳定的 `SymbolID`。

建议内部表示：

```text
SymbolID
SymbolKind
QualifiedName
DefinitionSpan
ModuleID
Visibility
```

退出条件：Generator 中不再自己判断“这个名字到底是什么”。

## 4.2 建立真正 Type Checker

建议目录：

```text
internal/types/
  type.go
  checker.go
  infer.go
  unify.go
  assign.go
  diagnostics.go
```

第一阶段支持：

```text
int
float
bool
string
error
any
[T]
map[K, V]
&T
fn(...): ...
Option[T]
Result[T, E]
struct
enum
chan[T]
generic parameter
```

需要正式定义：

```text
assignment compatibility
function call compatibility
return compatibility
generic inference
match exhaustiveness
field access
indexing
operator typing
? propagation
```

### 推荐的重要类型决策

建议在 v0.3 单独做一次设计决策：

```text
struct / enum       名义类型
interface / shape   结构化兼容
```

原因：

1. struct 与 enum 名义化后更容易理解身份、错误信息和 API 演进。
2. interface 保留结构化能力，可以继续获得 Go 与 TypeScript 风格的灵活性。
3. Agent 处理 rename、refactor、API diff 时会更加可靠。
4. 避免“两个长得一样的业务结构自动互换”产生隐藏语义。

如果暂时不引入 interface，也可以先保留现状，在 v0.4 再决策。

## 4.3 限制 `any`

规定 `any` 的合法主要来源：

```text
jsonParse
extern
宿主对象
显式 as any
```

普通语言内部禁止无原因退化到 `any`。

增加诊断：

```text
W3201 any escapes FFI boundary
W3202 unsafe field access on any
```

最终目标是让 `any` 的传播路径可被 `lang-go inspect` 查询。

## 4.4 引入 Typed HIR

建议：

```text
internal/hir/
  node.go
  build.go
  dump.go
```

每个表达式至少携带：

```text
NodeID
SourceSpan
TypeID
ResolvedSymbol
```

例如源码：

```text
let user = loadUser(id)?
```

HIR 应已经明确：

```text
loadUser 对应哪个 SymbolID
id 的类型
调用返回 Result[User, error]
? 的错误传播目标函数
user 的最终类型 User
```

后端无需再次推导。

## 4.5 建立 Core IR

v0.3 不需要 SSA。

先建立非常小的 Portable Core IR：

```text
Literal
LocalGet / LocalSet
Call
ConstructStruct
ConstructEnum
FieldGet
IndexGet
Branch
Loop
Match
Return
TryResult
Spawn
ChanSend
ChanRecv
BuiltinCall
```

目标只有一个：

> 三个执行后端共享同一套已经完成语义归一化的输入。

## 4.6 v0.3 验收标准

必须同时满足：

1. `lang-go check` 完全脱离 JS Generator。
2. Go / JS Generator 不再负责名字解析。
3. Go / JS Generator 不再负责高级类型推断。
4. Interpreter 开始执行 HIR/Core IR，或至少建立迁移通道。
5. 现有 91 个测试全部继续通过。
6. 新增不少于 80 个 semantic/type checker 测试。
7. 所有已有示例完成三路差分验证。
8. `specs/type-system.md` 成为类型语义真值源。

完成后打 `v0.3.0`。

---

# 5. v0.4 核心目标：Agent Contract

建议周期：第 5 到第 8 周。

这一版本不重点增加语言语法，重点建立 Coding Agent 可以稳定调用的编译器协议。

## 5.1 所有核心 CLI 支持 JSON 输出

建议：

```text
lang-go check --json
lang-go test --json
lang-go build go --json
lang-go build js --json
lang-go fmt --check --json
```

Machine API 必须带：

```json
{
  "schemaVersion": "1",
  "command": "check",
  "success": false,
  "diagnostics": []
}
```

JSON schema 一旦发布，就需要独立版本号。

## 5.2 稳定 Diagnostics

每条错误都需要永久错误码。

建议分段：

```text
E1xxx  lexer / parser
E2xxx  resolver / module
E3xxx  type system
E4xxx  pattern / exhaustiveness
E5xxx  concurrency
E6xxx  FFI / backend
E7xxx  build / tooling
Wxxxx  warning
```

结构：

```json
{
  "code": "E3104",
  "severity": "error",
  "message": "argument type mismatch",
  "file": "src/main.lang-go",
  "range": {},
  "symbol": "app.loadUser",
  "expected": "UserID",
  "actual": "string",
  "related": [],
  "fixes": []
}
```

Human 文本错误继续保留。

Agent 使用 JSON。

## 5.3 增加 Inspect 系列

第一批：

```text
lang-go inspect symbols --json
lang-go inspect symbol <qualified-name> --json
lang-go inspect type --at file:line:column --json
lang-go inspect refs <qualified-name> --json
lang-go inspect imports --json
lang-go inspect effects --json
```

这些命令比直接做 LSP 更适合当前阶段。

未来 LSP 只需要包装同一个 Semantic API。

## 5.4 AST / HIR / IR 可观察

增加：

```text
lang-go dump ast --json
lang-go dump hir --json
lang-go dump ir --json
```

用途：

1. 编译器调试。
2. Coding Agent 分析源码。
3. 未来数据集构造。
4. 自动验证语义变化。
5. 编译器学习。

输出必须有 `schemaVersion`。

## 5.5 Effect Metadata

暂时不要引入完整 Effect Type System。

先让编译器能够标记函数可能包含：

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

例如：

```text
pickCodec      PURE
http.get       NET
readFile       FS
now            TIME
random         RANDOM
extern call    FFI
```

Agent 可以通过：

```text
lang-go inspect effects app.main --json
```

知道一次修改是否把纯函数变成了网络或文件操作函数。

这会成为 Agent 安全审查的重要基础，同时避免现在就设计复杂 effect syntax。

## 5.6 v0.4 验收标准

1. 所有 Diagnostics 都有稳定错误码。
2. `check/test/fmt/build` 均能机器读取。
3. Agent 无需解析 Human CLI 文本即可判断成功和失败。
4. 能通过 CLI 查询 symbol、reference、type、import、effect。
5. AST/HIR/IR JSON 有独立 schema version。
6. 建立 `docs/agent-contract.md`。
7. 用一个真实 Coding Agent 完成十个自动诊断任务，准确率目标 100%。

完成后打 `v0.4.0`。

---

# 6. v0.5 核心目标：Lossless Source 与安全自动修改

建议周期：第 9 到第 12 周。

这是 90 天计划的最后一个核心阶段。

## 6.1 建立 Lossless Syntax Layer

现有 AST 适合执行语义，不足以支持高质量自动代码修改。

原因包括：

```text
注释可能丢失
空行信息丢失
原始 token trivia 丢失
Agent 重写节点容易制造巨大 diff
```

建议形成双层结构：

```text
Lossless Syntax Tree
        ↓
Semantic AST / HIR
```

Lossless Tree 至少保留：

```text
every token
comments
whitespace/trivia
source range
syntax errors
```

无需立刻引入复杂 green/red tree 框架，可以先采用 token stream + syntax nodes + trivia attachment。

## 6.2 Formatter 升级

硬性契约提升为：

```text
100% 保留所有注释
幂等
语义等价
错误代码也尽量可格式化
diff 稳定
```

新增测试：

```text
format(format(x)) == format(x)
parse(format(x)) semantic-equivalent parse(x)
comments(format(x)) == comments(x)
```

## 6.3 Safe Fix API

Diagnostics 的 `fixes` 开始支持真正安全的机械修复。

第一批只做高置信度修改：

```text
补 import
删除无用 import
修复明确类型名
补穷尽 match case
格式化
简单 deprecated API 替换
```

命令：

```text
lang-go fix --code E4002
lang-go fix --safe
```

默认只允许语义风险极低的修改。

## 6.4 Rename Refactor

第一项真正的 Semantic Refactor：

```text
lang-go refactor rename app.User.name displayName
```

它必须基于 `SymbolID + references`，不能基于文本替换。

验证：

```text
rename
→ check
→ test
→ format --check
→ symbol graph consistency
```

## 6.5 Agent Transaction

为 Agent 定义一次标准修改事务：

```text
1. inspect
2. plan
3. edit
4. fmt
5. check
6. test
7. diff
8. accept / rollback
```

编译器本身提供尽可能稳定的数据，不负责模型规划。

建议形成 `docs/agent-workflow.md`。

## 6.6 v0.5 验收标准

1. 仓库全部注释经过 formatter 后无损。
2. Rename 能跨文件正确修改符号。
3. 至少 5 种 Safe Fix 可自动执行。
4. Coding Agent 连续完成 20 个小型修改任务，没有出现误改同名符号。
5. 每次 Agent 修改均可通过机器接口完成验证。
6. `fmt` 导致的无关 diff 控制在接近零。

完成后打 `v0.5.0`。

---

# 7. 并发模型重构

并发建议在 v0.3 到 v0.5 期间独立推进，不继续按宿主能力直接映射。

## 7.1 语言层先定义 Logical Task

`go f()` 的语言语义应先转换为：

```text
Spawn(Task(f))
```

然后映射：

| 后端 | 实现 |
|---|---|
| Go | goroutine |
| JS | Promise / microtask scheduler |
| Interpreter | cooperative scheduler |

## 7.2 正式定义任务生命周期

当前“fire-and-forget”和“函数边界 join”两个描述存在概念冲突。

建议正式采用结构化任务作用域：

```text
go f()
```

创建当前 scope 的 child task。

scope 正常退出前等待它管理的 child tasks 完成。

真正 detached task 暂时不提供。

优点：

1. Human 更容易推理任务生命周期。
2. Agent 可以静态知道函数退出时后台任务是否仍存在。
3. Go / JS / Interpreter 更容易保持一致。
4. 测试具备确定退出条件。

如果未来真实场景需要 detached，再新增显式能力。

## 7.3 Channel 语义

建议固定：

```text
makeChan[T](capacity)
send(ch, value)
recv(ch) -> Result[T, error]
```

v0.x 暂时避免 select。

Scheduler 必须明确：

```text
send 何时挂起
recv 何时挂起
channel close 是否存在
任务全部 blocked 时如何定义 deadlock
程序结束条件
FIFO 顺序
```

建议在 Interpreter 中实现最小 cooperative scheduler，然后以它作为语言并发语义参考实现。

Go 和 JS 都对齐 reference scheduler 的可观察行为。

## 7.4 并发验收

必须增加独立 `conformance/concurrency/`：

```text
producer-consumer
multiple-producers
fifo
blocked-recv
blocked-send
nested-task
function-return
root-exit
deadlock
```

所有确定性测试三路一致。

对于明确允许的不确定输出，需要比较事件集合或 happens-before 约束，不能逐字节比较 stdout。

---

# 8. 规范治理重构

## 8.1 `specs/` 升为唯一语义真值源

建议目录：

```text
specs/
  language.md
  lexical.md
  grammar.md
  types.md
  values.md
  errors.md
  pattern-matching.md
  modules.md
  concurrency.md
  ffi.md
  runtime.md
  diagnostics.md
  agent-contract.md
```

`go/docs/spec.md` 逐步淘汰，只保留 implementation notes。

## 8.2 决策记录改成 ADR

当前 `decisions.md` 很有价值，但会持续增长。

建议继续保留索引，并逐步拆为：

```text
docs/adr/
  0001-language-positioning.md
  0002-value-semantics.md
  0003-error-model.md
  ...
```

每篇固定：

```text
Status
Context
Decision
Consequences
Alternatives
Compatibility
```

旧编号 1 到 64 可以一次性迁移，保留原编号映射。

## 8.3 版本唯一真值

新增仓库根：

```text
VERSION
```

CLI、README、build metadata 从这里生成或由构建脚本同步验证。

禁止分别手写版本号。

## 8.4 Feature Matrix 自动生成

建立机器可读：

```text
features.json
```

记录：

```text
parser
interpreter
go
js
spec
conformance
```

README 中能力表由该文件生成，消除“README 说未完成，代码已经实现”的漂移。

---

# 9. Conformance Test Suite

长期来看，测试应从“Go 实现的单元测试”升级为“语言规范测试”。

建议：

```text
conformance/
  syntax/
  types/
  values/
  errors/
  match/
  modules/
  concurrency/
  stdlib/
  ffi/
```

每个 case 可以包含：

```text
case.lang-go
expected.stdout
expected.stderr
expected.diagnostics.json
meta.json
```

`meta.json`：

```json
{
  "targets": ["interp", "go", "js"],
  "expect": "success",
  "deterministic": true
}
```

未来 Python/Rust 编译器实现只要跑同一套 corpus，就可以判断是否遵守语言规范。

这也会成为最适合 Coding Agent 学习 lang-go 行为的数据集。

---

# 10. 测试战略升级

现有差分测试继续作为核心资产，并增加五类测试。

## 10.1 Golden Tests

锁定：

```text
AST
HIR
IR
Diagnostics
Go output
JS output
Formatter output
```

## 10.2 Property Tests

验证不变量：

```text
fmt(fmt(x)) == fmt(x)
parse(print(ast)) 可重建等价 AST
copy(struct) 不共享字段
Option 永不产生 null
Result ? 保持错误值
```

## 10.3 Fuzzing

优先 fuzz：

```text
lexer
parser
formatter
json parser boundary
module loader
```

目标首先是：

```text
never panic
never hang
never silently corrupt AST
```

## 10.4 Differential Tests

继续：

```text
Interpreter
vs Go
vs JavaScript
```

以后再加入：

```text
Rust implementation
Python implementation
```

## 10.5 Metamorphic Tests

例如：

```text
源码改变量名
→ 输出不变

源码格式化
→ 输出不变

无副作用表达式增加括号
→ 输出不变
```

这类测试对于编译器和 Agent 自动修改特别有价值。

---

# 11. Agent Native Tooling 路线

v0.5 后逐步加入。

## v0.6

目标：语义级代码理解。

实现：

```text
call graph
symbol graph
module graph
type dependency graph
public API snapshot
```

命令示例：

```text
lang-go graph calls --json
lang-go graph modules --json
lang-go api snapshot --json
lang-go api diff old.json new.json
```

应用场景：

Agent 修改公共函数前，可以先知道影响范围。

## v0.7

目标：安全重构。

增加：

```text
extract function
inline local
organize imports
change function signature
move symbol
```

每一项都必须走 semantic graph，不接受全文本猜测。

## v0.8

目标：Verifier。

增加：

```text
lang-go verify
```

统一运行：

```text
fmt --check
check
test
conformance
api compatibility
effect diff
```

机器输出一个 Verification Report。

Coding Agent 每次改动只需要把 `verify --json` 作为最终闭环。

---

# 12. 标准库战略

继续保持“小核心 + 宿主委托”。

## Portable Core

建议只扩展已经出现多个真实消费方的能力：

```text
collections
strings
bytes
encoding
json
result/option helpers
math minimal
time abstraction
```

## Platform Libraries

分层：

```text
portable.*
host.go.*
host.js.*
```

或者仍采用 extern，但文档必须明确 portable 与 host-only。

## 标准库收录门槛

一个 API 进入 portable stdlib 前至少满足：

1. 两个真实消费方。
2. Go 与 JS 都能定义一致语义。
3. 能写 conformance test。
4. 不需要大量 runtime 魔法。

---

# 13. 真实项目 Dogfooding

语言不能只用 micro benchmark 和 hello world 验证。

建议维护三类真实样板。

## A. CLI 工具

继续扩大 linkpull 迁移。

验证：

```text
文件
HTTP
JSON
字符串
错误处理
模块
测试
```

## B. 小型 HTTP Service

实现一个 500 到 1500 行服务：

```text
routing
request validation
JSON
Result
concurrency
logging
```

目标是发现服务端语言缺口。

## C. 浏览器小应用

实现一个非常小的 Web App：

```text
DOM
fetch
state
事件
模块
npm interop
```

目标是证明 JS 后端拥有真实消费方，然后再决定 `.d.ts` ingestion 是否值得立项。

规则：

> 每增加一个语言 Feature，必须指出它来自哪个真实 Dogfood 痛点。

---

# 14. 90 天执行路线

## Week 1

主题：冻结与清理。

交付：

```text
VERSION
specs 真值源目录
Feature Matrix
旧文档状态修正
v0.3 architecture ADR
```

同时冻结新语法。

## Week 2

主题：Resolver。

交付：

```text
scope
symbol table
SymbolID
name resolution
import resolution
resolver diagnostics
```

## Week 3

主题：Type Checker I。

完成：

```text
primitive
collection
function
struct
enum
Option
Result
operators
```

## Week 4

主题：Type Checker II + HIR。

完成：

```text
generics
?
match exhaustiveness
Typed HIR
Go/JS backend 接入 HIR
```

发布 `v0.3.0`。

## Week 5

主题：Diagnostics Protocol。

完成稳定错误码、JSON schema、source range、related diagnostics。

## Week 6

主题：Inspect API。

完成 symbol、refs、type、imports 查询。

## Week 7

主题：IR 可观察性 + Effect Metadata。

完成 AST/HIR/IR dump 与 effects inspect。

## Week 8

主题：Agent Contract 实战。

让 Codex 或其他 Coding Agent 只依赖机器接口完成 10 个诊断任务。

发布 `v0.4.0`。

## Week 9

主题：Lossless Syntax。

保存 token、comments、trivia、span。

## Week 10

主题：Formatter 2.0。

达到注释无损、幂等、低 diff。

## Week 11

主题：Semantic Rename + Safe Fix。

至少完成 rename 和 3 到 5 个安全自动修复。

## Week 12

主题：Agent 修改闭环。

完成 20 个真实自动修改任务：

```text
inspect
edit
fmt
check
test
verify
```

发布 `v0.5.0`。

---

# 15. 6 个月里程碑

| 版本 | 核心主题 | 关键成果 |
|---|---|---|
| v0.3 | Semantic Core | Resolver + Type Checker + HIR/Core IR |
| v0.4 | Agent Contract | JSON Diagnostics + Inspect + Graph 基础 |
| v0.5 | Safe Editing | Lossless Syntax + Formatter 2.0 + Rename + Fix |
| v0.6 | Semantic Graph | Call/Symbol/Module/API Graph |
| v0.7 | Refactoring | 多种语义级自动重构 |
| v0.8 | Verification | `lang-go verify` + 完整 conformance |
| v0.9 | Hardening | 三类真实项目 dogfood、性能与稳定性治理 |
| v1.0 | Language Freeze | 语义兼容承诺 + 稳定 spec + 稳定 Agent Contract |

不要为了版本号赶进度。每个版本满足退出条件以后再打 tag。

---

# 16. v1.0 前明确暂缓

以下能力继续保持冻结，除非真实项目给出强证据：

```text
macro
inheritance
exception
null
operator overloading
HKT
ownership/borrow checker
LLVM backend
self-hosting
package registry
full debugger
full IDE
reflection
compile-time metaprogramming
```

另外，LSP 建议继续后置。

当 `Semantic API + Agent Contract` 稳定后，LSP 将成为一层协议适配，而不会反过来绑架编译器架构。

---

# 17. 项目结构建议

v0.5 左右可以逐渐演进到：

```text
language/
├─ VERSION
├─ README.md
├─ specs/
│  ├─ lexical.md
│  ├─ grammar.md
│  ├─ types.md
│  ├─ values.md
│  ├─ errors.md
│  ├─ modules.md
│  ├─ concurrency.md
│  ├─ ffi.md
│  └─ agent-contract.md
├─ docs/
│  ├─ adr/
│  ├─ learning/
│  └─ roadmap.md
├─ conformance/
├─ examples/
├─ implementations/
│  └─ go/
│     ├─ cmd/lang-go/
│     └─ internal/
│        ├─ syntax/
│        ├─ semantic/
│        ├─ types/
│        ├─ hir/
│        ├─ ir/
│        ├─ interp/
│        ├─ backend/go/
│        ├─ backend/js/
│        ├─ format/
│        └─ tooling/
└─ schemas/
   ├─ diagnostics.schema.json
   ├─ ast.schema.json
   ├─ hir.schema.json
   └─ agent-contract.schema.json
```

迁移需要渐进执行，不建议一次性大搬家。

---

# 18. 每个 Feature 的强制准入流程

未来任何新 Feature 先回答十个问题：

1. 哪个真实项目需要它？
2. Human 获得什么收益？
3. Agent 获得什么收益？
4. 是否降低局部可推理性？
5. Type Checker 如何表达？
6. HIR/Core IR 如何表达？
7. Interpreter 如何实现？
8. Go 与 JS 如何保持语义一致？
9. Conformance Test 怎么写？
10. 删除这个 Feature 会失去什么真实能力？

其中任意核心问题没有答案，Feature 默认暂缓。

---

# 19. Agent Friendliness 的量化指标

以后不要只凭“感觉适合 Agent”。

建议维护指标：

| 指标 | v0.5 目标 | v1.0 目标 |
|---|---:|---:|
| Diagnostics 带稳定错误码 | 100% | 100% |
| 语义信息可 JSON 查询 | 80% | 100% |
| Formatter 注释保留率 | 100% | 100% |
| Formatter 幂等 | 100% | 100% |
| 自动 rename 正确率 | 100% corpus | 100% conformance |
| Safe Fix 修改后 check 通过率 | 100% | 100% |
| 后端确定性 conformance | 95%+ | 100% 定义内语义 |
| Agent 小任务一次验证成功率 | 90%+ | 95%+ |
| Agent 误改同名符号 | 0 | 0 |
| Spec 覆盖核心语义 | 90% | 100% |

注意：“Agent 写代码准确率”受模型影响很大，因此语言项目更应测量它能控制的部分，例如诊断质量、语义 API、修改安全性和验证闭环。

---

# 20. 最重要的五条工程纪律

### 纪律 1

**Spec First。**

任何语义变更先写规范和 ADR，再写实现。

### 纪律 2

**Semantic Once。**

高级语义只允许在 semantic/HIR 层定义一次。

### 纪律 3

**Feature Must Pay Rent。**

没有真实消费方，不增加语言复杂度。

### 纪律 4

**Machine Verifiable。**

“看起来正确”不能作为验收。必须能通过 check、test、conformance 或结构化结果机械判定。

### 纪律 5

**Agent Interface Is Public API。**

一旦发布 JSON schema、error code、inspect contract，就按公共 API 管理版本。

---

# 21. 我对 lang-go 最终形态的建议

最终可以把语言理解为四层。

```text
┌─────────────────────────────────────┐
│ Human Layer                         │
│ 简洁传统语法、静态类型、Result、match │
├─────────────────────────────────────┤
│ Semantic Layer                      │
│ Symbols + Types + HIR + Effects     │
├─────────────────────────────────────┤
│ Agent Layer                         │
│ Inspect + Diagnostics + Refactor    │
│ Graph + Verify + JSON Contract      │
├─────────────────────────────────────┤
│ Execution Layer                     │
│ Interpreter + Go + JavaScript       │
└─────────────────────────────────────┘
```

Human 继续写正常的编程语言。

Coding Agent 获得比纯文本源码更可靠的程序语义视图。

Compiler 用统一 Semantic Core 保证多个后端共享同一套语言规则。

这条路线兼顾了传统编程语言几十年验证过的优势，也把 Coding Agent 时代真正新增的需求放在编译器工具接口和验证体系中解决。

---

# 22. 下一步执行顺序

如果从今天开始继续开发，建议严格按以下顺序：

```text
P0  冻结新语法
P0  specs 成为真值源
P0  Resolver
P0  Type Checker
P0  Typed HIR
P0  Core IR
P1  Interpreter/Go/JS 统一消费 IR
P1  Structured Diagnostics
P1  Agent Contract
P1  Lossless Syntax
P1  Formatter 2.0
P1  Semantic Rename
P1  Safe Fix
P2  Cooperative Scheduler
P2  Semantic Graph
P2  verify
P2  更多真实项目 Dogfood
P3  LSP
P3  package ecosystem
```

当前最应该避免的是继续优先增加 `select`、宏、更多语法糖、大量标准库或新的 backend。

先让语言拥有稳定的“语义脊柱”。

当 Semantic Core、Agent Contract、Conformance 三件事稳定以后，lang-go 才真正具备继续扩张的基础。

---

## 最终成功标准

v1.0 的成功不以“功能数量”衡量。

真正的成功状态应该是：

> 一个 Human 可以像使用传统现代语言一样自然地阅读和编写 lang-go；一个 Coding Agent 可以通过标准机器接口准确知道代码中的符号、类型、依赖、副作用和错误，并能进行语义级修改；每次修改都能由统一验证器机械确认；Interpreter、Go 与 JavaScript 后端对规范定义的行为保持一致。

达到这个状态以后，lang-go 才拥有一个真正区别于普通个人语言项目的长期方向。
