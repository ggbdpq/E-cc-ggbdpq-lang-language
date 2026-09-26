# A1-syntax 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

### W2 Formatter（2026-09-26）

- **Lang.Formatter 全量落地**（`impls/csharp/src/Lang.Formatter/Formatter.cs`，对照 rust
  lang-formatter / TS format.ts 证据逐条移植）：
  - 契约 API：`Formatter.Format(stmts, trivia, source) → string`（AST+trivia 规范化打印）；
    `Formatter.FormatSource(string) → FormatResult(Output, Diagnostic?)`（解析失败 E1001
    透传返回，formatter 自身零诊断、零 throw）。
  - 渲染为行缓冲：4 空格缩进、末尾恒单换行、空程序输出空串、CRLF 源归一为 LF；
    **行号一律由码点 offset 经 `SourceText.LineCol` 折算**（trivia / AST span 均为码点
    offset，astral 输入不挤歪注释贴附）。
  - 注释贴附策略：行首注释独立行 + 空行分组（最多一空行）；行尾注释两空格间隔追加；
    表达式内注释语句边界冲刷（位置后移、文本零丢失）；块尾注释原位保留在 `}` 之前。
    **已知陷阱规避（lang-rs 修法）**：批内首条注释前不补空行，使「空块 + `}` 后注释」
    幂等（FlushRemaining first 标志）；**fnlit type_params 保留**（`fn[T](x: T)`）。
  - 表达式优先级括号（PARSE-004 同表：低于门槛加括号、结合性保留、冗余括号规范化）；
    int Lit 十进制规范文本、float 保身份（整值补 `.0`）、字符串/ import 五转义
    `GoQuote`；`GoFormatFloat` = Go `strconv.FormatFloat(f,'g',-1,64)` 等价
    （shortest eprec=6，指数两位带符号；.NET "R" 最短往返数字与 Rust `{:e}` 同源归一，
    14 行 Go 证据表逐条锁定）。
  - `Lang.Formatter.csproj` 增 `InternalsVisibleTo("Lang.Formatter.Tests")`
    （GoFormatFloat/GoQuote 行为表测试，不扩大公共 API 面）。
- **测试**：`impls/csharp/tests/Lang.Formatter.Tests/`（xunit，**254 全绿**，TDD 先红后绿）：
  - 行为面 26 用例：期望值对照 Go format_test / rust 测试表（缩进规范化、末尾单换行、
    else-if 链扁平、方法/泛型/struct/enum、import/extern、chan 注解、括号最小化、
    hex/float 规范化、转义往返、match/fnlit 形态、fnlit type_params、集合字面量单行、
    确定性、CRLF、解析失败返诊断）。
  - 注释保留逐类（deliverable 3）：行尾 / 独立 / 文件头 / 块尾 / **字符串内伪注释**
    （trivia 流零注释硬断言）/ match 臂 / struct 字段 / fnlit 体 / 15 语句变体抽样
    前导+行尾 / 空块+尾注释幂等陷阱；统一「trivia 流每条 Comment 文本逐条出现在输出」
    零丢失硬检查 helper + 二次格式化幂等回归。
  - **幂等矩阵（deliverable 2）**：conformance 16 分类 **151 个 main.lang-go** +
    dogfood/langalgo **66 个 .lang-go** 逐文件六断言：①首次解析零错误 ②format→reparse→
    format 二次输出==首次 ③AST 形态（span 无关，独立 AstDump 结构 dump）相等
    ④同输入两次逐字节相等 ⑤重解析零错误 ⑥零 `\r` + 末尾单换行；**151/66 全数通过，
    零漂移**。计数锚测试锁定 151/66（文件增删须同步更新）。
  - astral（契约 §0）：三个测试类各带 🦀/𝕏/𠀀 用例（astral 标识符/字符串/注释文本
    逐字保真、码点折算下的空行分组与行尾贴附、astral 合成源全矩阵）。
- **质量门**：`dotnet test` 254/254 绿（Lang.Syntax.Tests 95/95、Lang.Core.Tests 13/13
  无回归）；`dotnet build -c Release -warnaserror` Lang.Formatter / 其测试项目 /
  Lang.Syntax 均 0 警告 0 错误；`dotnet format --verify-no-changes` 两项目干净。
  整 `LangCs.slnx` Release 当前唯一报错项目为 `Lang.Refactor`（引用 A5 在建的
  Lang.Loader，非本域改动所致）。
- **移植坑登记（供后续 Agent）**：C# 里方法名与 AST 类型同名（`Pat`/`Stmt`/`Expr`）
  时，方法组作 `Select()` 实参撞 CS0119——方法名加后缀规避（`PatStr`/`DStmt`）。

### W1 首批交付（2026-09-26）

- **Lang.Syntax 全量落地**（`impls/csharp/src/Lang.Syntax/`）：
  - `Token.cs`：TokenKind 64 类全表 + `TokenKinds.KindName` 中文契约文本（逐字对照 rust
    lang-syntax `kind_name` / TS `kindName`）+ `KeywordOf`（LEX-004：16 关键字 + 5 保留字）+
    `IsCont`（LEX-008 续行表 22 项）+ `Token(Kind, Lit, Span)`。int 的 Lit 存十进制规范文本
    （十六进制折算），string 的 Lit 存解码后值。
  - `Lexer.cs`：`Lexer.Lex(SourceText, SourceId) → LexResult`。注释/标识符/关键字/int/float/
    string/五转义/换行续行；lossless Trivia 见 Trivia.cs。**内部 char index，Token.Span 一律
    0 基码点半开区间，经 `SourceText.CharIndexToCp + LineCol` 折算**；astral（🦀𝕏𠀀）用例锁定。
    词法错误 = Diagnostic E1001 行级锚，首错即停，零 throw。
  - `Trivia.cs`：`TriviaScanner.Scan → IReadOnlyList<Trivia>`（Comment/Whitespace/Newline，
    字符串字面量内的 `//` 不算；CRLF 注释 text 去 `\r`、span 抵 `\n` 前）。
  - `Ast.cs` + `Parser.cs`：见冻结登记。PARSE-004 Pratt 优先级 9 级、PARSE-006 NoStruct
    计数器（括号恢复）、E1001 message 逐字对照 Go/TS 证据（「期望 X，得到 Y」「整数溢出: …」
    「match 分支之间需要换行」等全表锁定）。
- **测试**：`impls/csharp/tests/Lang.Syntax.Tests/`（xunit，95 个测试全绿）。TDD 先红
  （API 缺失编译红）后绿。覆盖：Unicode 码点 span（**astral：🦀/𝕏/𠀀 的词法 span、
  AST 节点 span、EOF 定位**）、CRLF 行列、续行、转义、全角数字 Nd 口径、优先级抽样
  （| + 左结合、<< 高于 +）、赋值目标链、保留字、诊断形态（E1001/LEX-ALL/行锚）；
  conformance/syntax 13 个 case 全部作为夹具测试（逐 case 解析零错误 + 结构断言）。
- **质量门**：`dotnet test` 95/95 绿（Lang.Core.Tests 12/12 亦绿）；`dotnet build -c Release
  -warnaserror` Lang.Syntax 与测试项目 0 警告 0 错误；`dotnet format --verify-no-changes` 干净。
  整解决方案 Release 构建当前唯一报错项目为 `Lang.Hir`（A3 在建 WIP，非本域改动所致）。

## 进行中

（无）

## 下一步

- （W2 后）Formatter 首批交付完成；待协调人把 `tests/Lang.Formatter.Tests/Lang.Formatter.Tests.csproj`
  追加进 `LangCs.slnx`（与 W1 的 Lang.Syntax.Tests 同批），此前用
  `dotnet test tests/Lang.Formatter.Tests/Lang.Formatter.Tests.csproj` 单独跑。
- 可选后续（未排期）：`Lang.Cli` 接 `fmt` 子命令时直接消费 `Formatter.FormatSource`。

## 阻塞

（无）

## 已冻结接口登记

> 均在 `Lang.Syntax` 命名空间；消费方按此对接，改签名走协调人集成轮。

- `enum TokenKind`（64 类，成员名与 rust token.rs 一致）；
  `static class TokenKinds`：`string KindName(TokenKind)`、`TokenKind? KeywordOf(string)`、
  `bool IsCont(TokenKind)`；`sealed record Token(TokenKind Kind, string Lit, Span Span)`。
- `static class Lexer`：`LexResult Lex(SourceText, SourceId)`；
  `sealed record LexResult(IReadOnlyList<Token> Tokens, Diagnostic? Error)`（Ok = Error is null）。
- `enum TriviaKind { Comment, Whitespace, Newline }`；`sealed record Trivia(TriviaKind, Span, string)`；
  `static class TriviaScanner`：`IReadOnlyList<Trivia> Scan(SourceText, SourceId)`。
- **AST 形态决定（冻结）**：abstract record 层次 + 嵌套 sealed record 子型，即 `Expr.IntLit`
  双段命名；每节点 span 统一暴露 `abstract Span Span { get; }`，位置参数首项命名 `Sp` 承载。
  变体全集：Expr 18（IntLit/FloatLit/StrLit/BoolLit/Ident/Unary/Binary/Range/Call/Index/Slice/
  Field/Try/StructLit/MapLit/ArrLit/Match/FnLit）、Pat 4（Wild/Bind/Lit/Case）、TypeEx 5
  （Named/Arr/Map/Ptr/Fn）、Stmt 15（Let/Assign/ExprStmt/Return/If/While/ForIn/Break/Continue/
  FnDecl/StructDecl/EnumDecl/Go/Import/Extern）+ `UnaryOp`(3)/`BinaryOp`(18)/`AssignOp`(5) +
  共用小节点 `Param/FieldLit/MapItem/Arm/EnumCase`（含 `Span`）。语义对齐 rust ast.rs
  （契约强制超集：Arm/EnumCase/TypeEx/Param 一律真实 span）。
- `static class Parser`：`ParseResult Parse(SourceText, SourceId)`、
  `ParseResult ParseTokens(IReadOnlyList<Token>, SourceId)`（空流补合成 EOF，不 throw）；
  `sealed record ParseResult(IReadOnlyList<Stmt> Program, Diagnostic? Error)`。
  错误统一 `Diagnostic.New(DiagnosticCode.E1001, …, Diagnostic.AtLine(...))` 行级锚。
- 分派约定：switch expression 穷尽各变体；C# 开放类层次必须有终臂，兜底只允许
  `throw InvalidOperationException` 硬失败，禁静默 default 吞新变体（ParserTests 已示范）。
- `Lang.Formatter` 命名空间（W2 冻结）：
  `sealed record FormatResult(string Output, Diagnostic? Error)`（`Ok => Error is null`，
  失败时 Output 为空串、Error 为透传的 E1001）；
  `static class Formatter`：`FormatResult FormatSource(string src)`、
  `string Format(IReadOnlyList<Stmt> stmts, IReadOnlyList<Trivia> trivia, SourceText source)`
  （trivia 传 TriviaScanner.Scan 全量即可，formatter 自取 Comment 类；空程序输出空串）。
  内部辅助 `GoFormatFloat`/`GoQuote` 为 internal（InternalsVisibleTo 仅开给本域测试项目）。

## SPEC-GAP

1. **Lang.Core `SourceText.CharIndexToCp` 文末边界**（协调人领地，本地已 workaround）：
   `charIndex == LengthChars`（文末）时二分返回 `LengthCp - 1` 而非 `LengthCp`（空文本时返回
   -1 并使 `LineCol` 越界）。契约未言明该边界，但「charIndex → 码点」在文末应折算为码点总数。
   建议 Core 侧修正 + 补空文本/文末用例；Lang.Syntax/TriviaScanner 的 `Pos` 已本地按
   「charIndex ≥ Length ⇒ cp = LengthCp」修正，Core 修正后此 workaround 可原样移除。
2. **契约 §3 文本勘误**（继承 rust lang-syntax 已登记口径）：TokenKind 实为 64 类
   （7 + 21 关键字保留字 + 36 运算符标点）；BinaryOp 实为 18 个；LEX-004 关键字枚举列 16 词
   （规范写「15」为笔误）。均以全列表为准。
