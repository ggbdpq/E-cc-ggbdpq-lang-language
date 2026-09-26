# lang-cs 接口契约 v1（协调人冻结）

> 六个开发智能体共同编码的锚。改动本文件只能由协调人在集成轮执行。
> 行为真值顺序：`specs/ > specs/rules.md > conformance/ > 各实现`。既有四实现只是行为证据，禁止复制宿主语义。

## 0. 本实现的对抗轴（必须内化）

**UTF-16 码点世界**：C# `string` 是 UTF-16 code unit 序列，astral 码点（🦀𝕏𠀀 类）占 2 个 char。
任何「char index 当 offset」的写法在 astral 输入上静默错位。纪律：

- 只允许 parser 内部使用 char index；任何对外（Span/诊断/编辑/dump）必须经 `Lang.Core.SourceText` 折算；
- 自己写的每个测试类都必须至少有一条 astral 用例（🦀 或 𝕏）；
- 禁 `async/await` 进入语言语义面（Task/Channel 是协作式同步子集）；
- 语言泛型是**擦除**语义（GEN-*）——禁用 C# reified 泛型承载语言泛型，只许容器内部使用。

## 1. 项目拓扑与所有权（20 项目，依赖已接线，禁改他人 csproj）

| Agent | 独占项目 | 首批交付 |
|---|---|---|
| 协调人 | `Lang.Core`、`LangCs.sln`、`Directory.Build.props`、`docs/lang-cs/*` | ✅ 已完成（12 测试全绿） |
| A1 Syntax | `Lang.Syntax`、`Lang.Formatter` | Token/Lexer/Parser/AST/Trivia |
| A2 Semantic | `Lang.Types`、`Lang.Semantic` | 类型格 + Resolver + Checker（E2/E3/E4 全量） |
| A3 HIR/IR | `Lang.Hir`、`Lang.Ir` | HIR/IR 模型 + Validator + Lowering |
| A4 Runtime | `Lang.Runtime`、`Lang.Interp` | Value/Place/Ref/Closure/Task/Chan + 双解释器 |
| A5 Backend/Tooling | `Lang.BackendGo`、`Lang.BackendJs`、`Lang.Loader`、`Lang.Cli` | 输出规则层、模块图、CLI |
| A6 Agent/Verify | `Lang.Contract`、`Lang.Graph`、`Lang.Edit`、`Lang.Refactor`、`Lang.Verify`、`Lang.Conformance` | DTO/效果不动点/图/安全编辑/重构/runner |

测试项目放 `tests/<项目名>.Tests/`（xunit），同属该 Agent 领地。参考 lang-rs 的 dev-dep 咬合教训：
`Lang.Interp` 的 conformance 夹具测试需要 `Lang.Conformance` 的判定器——用 `tests/` 项目引用解决，
不要在 src 项目间制造环。

## 2. 已冻结接口（Lang.Core，直接消费）

```csharp
readonly record struct SourceId/NodeId/SymbolId/TypeId/FunctionId/LocalId(uint Value)
readonly record struct Position(uint Offset, uint Line, uint Column)   // 0 基码点 / 1 基行列
readonly record struct Span(SourceId Source, Position Start, Position End)  // 半开区间
sealed class SourceText {
    SourceText(string path, string text);          // 构造即建索引
    int LengthCp { get; }                          // 码点数（≠ string.Length）
    int CharIndexToCp(int charIndex);              // 代理对中间向下取整
    int CpToCharIndex(int cp);                     // 越界回文末
    (int Line, int Column) LineCol(int cp);        // 1 基；\n 换行，\r 属上一行
}
enum Severity { Error, Warning }
enum DiagnosticCode { 27 变体 }   // .CodeText() → "E2002"；.Rule() → RuleId（27 行契约映射，已测）
enum RuleId { 97 变体 }           // .RuleText() → "NAME-SHADOW-002"
sealed record Diagnostic { Code, RuleId, Severity, Message, PrimarySpan?, RelatedSpans, Help }
                                   // Diagnostic.New() 固定 error + 推导 ruleId；AtLine() 行级锚
```

## 3. 语法面冻结（A1 拥有定义，全员按此消费）

与 `docs/lang-rs/interface-contract.md` §3 完全同构：TokenKind 64 类、Token{Kind, Lit, Span}
（int 的 Lit 存十进制规范文本、string 的 Lit 存解码后值）、AST = Expr 18 / Pat 4 / TypeEx 5 /
Stmt 15 + 三组 Op，每节点带 Span。C# 形态：`abstract record Expr` + 嵌套 record 子型（或 discriminate
enum + record 双轨，A1 定并在 status 登记）；穷尽分派用 switch expression，禁 default 兜底吞新变体。
错误消息文本契约：E1001 等 message 逐字对照 `impls/typescript/src/syntax/`（Go 复刻文本）。

## 4. 接口登记制度

跨项目公共类型（IR 指令集、Value 层次、HIR 节点、Analysis 入口、loader API、DTO schema）落地前
在 `docs/lang-cs/status/<agent>.md` 的「已冻结接口登记」写一行。登记后改签名只能走协调人集成轮。
消费方发现缺口：在 status 记 BLOCKED，禁止改 owner 的文件。

## 5. 工程纪律

- TDD：每切片先红后绿；期望值来自 specs/conformance/手算，禁拿实现当期望。
- 完成块判据：`dotnet test`（自己域全绿）+ `dotnet build -c Release -warnaserror` + `dotnet format --verify-no-changes`。
- 禁 `Dictionary`/`HashSet` 迭代序外泄（map 用插入序结构，公开集合显式排序）；禁 `async`；
  禁 `unsafe`（csproj 已禁）；禁 `throw` 表达用户源码错误（一律 Diagnostic / 受控错误类型，内部不变量另论）。
- 禁 git 写操作；禁动 `specs/ conformance/ schemas/ impls/` 其他实现。
- SPEC 冲突：登记进自己 status 的 SPEC-GAP 节（协调人统一并入 `docs/lang-cs/spec-gaps.md`）。
- **继承 lang-rs 的教训清单**：赋值降形压栈序冻结合同 + 地址组件保 place 身份（B1）；构造类指令
  pop_n 栈序；预载定义 id（error=StructDefId(0)/Option=EnumDefId(0)/Result=EnumDefId(1)）按名映射；
  formatter「空块+尾注释」幂等陷阱；fnlit type_params 必须保留。

## 6. 行为参考（只读证据）

specs/ 真值优先；证据 `impls/typescript/src/`（结构最清晰）、`impls/go/internal/`（参考实现）、
`impls/rust/crates/`（最新对抗审查结论 + 各 status 文件的降形决策表）。
对抗重点：`docs/lang-rs/2026-09-25-lang-rs-对抗验证重点.md` + 本契约 §0。

## 7. 本机工具链（每次 bash 调用前 export）

```bash
export DOTNET_ROOT="D:\DevTools\Languages\Backend\Dotnet"
export PATH="/d/DevTools/Languages/Backend/Dotnet:/d/DevTools/Languages/Backend/Golang/Go/bin:$PATH"
```

dotnet 10.0.401 / go 1.27.0（→Go 差分）/ node v24.21.0 / python 3.14.7。在 `impls/csharp` 下跑 dotnet 命令。
