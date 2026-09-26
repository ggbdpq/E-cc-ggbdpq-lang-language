# A1-syntax 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

**第二波交付（2026-09-25）：lang-formatter 全部完成**

- `impls/rust/crates/lang-formatter/src/lib.rs`：公开 API `format(stmts: &[Stmt], trivia: &[Trivia], source: &SourceText) -> String`（交付书签名原形）+ 便捷入口 `format_source(&str) -> Result<String, Diagnostic>`。渲染为行缓冲（缩进 4 空格、末尾恒单换行、空程序输出空串），行号一律由 span 码点 offset 经 `SourceText::line_col` 权威折算（`source` 参数因此是实打实的消费方）。对照 impls/typescript/src/format/format.ts 逐行为移植；不依赖 Rustfmt。
- 注释贴附策略（对照 Go format.go/format_v05_test.go 证据）：行首注释独立行 + 空行分组（最多一空行）；行尾注释两空格贴语句末行；表达式内注释语句边界冲刷（match 臂 / struct 字段 / extern 内注释后移到下一语句边界，文本零丢失）；块尾注释 `}` 前原位保留。100% 注释保留。
- 测试（`cargo test -p lang-formatter` **31 通过 / 0 失败** = lib 28 + 语料矩阵 3）：clippy `-p lang-formatter -p lang-syntax --all-targets -- -D warnings` 干净；fmt `--check` 通过；`cargo test --workspace` 41 个目标全绿不破坏别人。`#![forbid(unsafe_code)]` 保持。
- **幂等矩阵**：conformance 16 分类 **151/151 全通过**（语料里所有 main.lang-go 均可解析，含 syntax 分类 13 个 run 类夹具；0 跳过）+ dogfood/langalgo **66/66 全通过**。每文件五重断言：二次输出==首次、产物可重解析、**span 无关 AST 形态相等（语义保留，tests 内置全变体 shape 对比器）**、两次解析 debug 形态相等、同输入两次 format 逐字节相等，另加注释多重集一致。
- Go `strconv.FormatFloat(f,'g',-1,64)` 等价实现（`go_format_float`：最短往返数字经 Rust `{:e}`、eprec=6 科学计数门槛、两位带符号指数；NaN/±Inf/±0 同文；整值浮点补 `.0` 保 float 身份）。语料浮点最大 123456.0，全落十进制路径，无科学计数往返风险。
- 注释保留专项（lib 内 12 个行为测试）：行尾两空格、连续注释、注释间空行分组、块尾、纯注释文件、空文件、CRLF 规范化、字符串内伪注释（不是注释、原样留在字面量）、match 臂 / struct 字段 / fnlit 体注释、确定性。

**首批交付（2026-09-25）全部完成**：`impls/rust/crates/lang-syntax/` 五模块 + conformance 夹具测试。

- `token.rs`：TokenKind 全列表（64 类 = 特殊 7 + 关键字 21 + 运算符/标点 36）+ `kind_name()` 中文契约串（逐字对照 TS token.ts）+ `Token { kind, lit, span }` + `keyword()` / `is_cont()`（LEX-008 续行表）。
- `lexer.rs`：码点扫描（Token.span 恒 0 基码点，经 `SourceText::line_col` 折算 line/column）；注释不产记号；hex 折十进制规范文本（lit 存十进制）；转义仅 `\n \t \r \" \\` 五种；续行落实（`is_cont` 后行首换行不产记号）；词法错误 = E1001 诊断（message 逐字对照 Go 复刻文本）。标识符/数字判别用内嵌 Unicode L*/Nd 精确闭区间表（见 SPEC-GAP 第 4 条）。
- `trivia.rs`：无损层 `scan_trivia()`——注释（text 去行尾 `\r`，span 抵 `\n`）/ 空白串 / 换行三类，字符串字面量内容跳过；供 formatter 100% 注释保留与 A6 安全编辑。
- `ast.rs`：Expr 18 / Pat 4 / TypeEx 5 / Stmt 15 + UnaryOp/BinaryOp(18)/AssignOp(5)，每节点带 `span`（含 Arm/EnumCase/Param/FieldLit/MapItem/TypeEx——见 SPEC-GAP 第 2 条）；`Expr::span()/Pat::span()/TypeEx::span()/Stmt::span()` 访问器。
- `parser.rs`：recursive descent + Pratt（PARSE-004 优先级表）、PARSE-006 控制头禁裸 struct 字面量（`no_struct` 计数）、赋值目标校验、match 臂换行分隔、extern/import/方法/泛型声明；解析错误 = E1001 诊断（行级锚），全链路无 panic。
- 测试：`cargo test -p lang-syntax` **60 通过 / 0 失败**（lib 58 + 夹具 2）；`cargo clippy -p lang-syntax --all-targets -- -D warnings` 干净；`cargo fmt -p lang-syntax` 已跑（`--check` 通过）。`#![forbid(unsafe_code)]` 保持。
- 夹具：`tests/conformance_syntax.rs` 跑真实 `conformance/syntax/` 13 个 case（case.yaml exitCode:0 → 断言解析通过；check 类断言 E1001），另验 hello-world 的 StrLit 解码值 = case.yaml 期望 stdout。

## 进行中

（无）

## 下一步

- （无；formatter 已收口，等协调人集成轮。后续消费方：lang-edit 安全编辑可复用同一 trivia 流 + 注释贴附判定。）

## 阻塞

（无）

## 已冻结接口登记

> 登记后改签名 = 破坏契约，走协调人集成轮。

- 冻结：`lang_syntax::Token`：`{ kind: TokenKind, lit: String, span: Span }`（契约 §3 原形）。
- 冻结：`lang_syntax::TokenKind`：64 变体 enum（Eof…RBracket；形状见 src/token.rs，与 TS token.ts 全列表一一对应）。
- 冻结：`lang_syntax::kind_name(TokenKind) -> &'static str`：错误消息契约面，逐字对照 TS kindName。
- 冻结：`lang_syntax::keyword(&str) -> Option<TokenKind>`、`lang_syntax::is_cont(TokenKind) -> bool`。
- 冻结：`lang_syntax::lex(&SourceText, SourceId) -> Result<Vec<Token>, Diagnostic>`。
- 冻结：`lang_syntax::parse(&SourceText, SourceId) -> Result<Vec<Stmt>, Diagnostic>`、`parse_tokens(Vec<Token>, SourceId) -> 同形`。
- 冻结：`lang_syntax::Trivia`：`{ kind: TriviaKind(Comment|Whitespace|Newline), span: Span, text: String }` + `scan_trivia(&SourceText, SourceId) -> Vec<Trivia>`。
- 冻结：`lang_syntax::ast`：`Expr`(18 变体，struct variant 带 span+载荷) / `Pat`(4) / `TypeEx`(5) / `Stmt`(15) / `UnaryOp`(3) / `BinaryOp`(18) / `AssignOp`(5) / `Param{span,name,typ}` / `Arm{span,pat,body}` / `EnumCase{span,name,payloads}` / `FieldLit{span,name,value}` / `MapItem{span,key,value}`；FnLit/FnDecl/StructDecl/EnumDecl 带 `type_params: Vec<String>`，FnDecl 带 `recv: Option<Param>`；`Stmt::Extern.fns: Vec<Stmt>`（每项恒 `Stmt::FnDecl`，body 空）。
- 冻结：`span()` 访问器：`Expr::span()/Pat::span()/TypeEx::span()/Stmt::span() -> Span`。
- 冻结（第二波追加）：`lang_formatter::format(stmts: &[Stmt], trivia: &[Trivia], source: &SourceText) -> String`：规范化打印唯一入口；`source` 承担码点→行号权威折算。
- 冻结（第二波追加）：`lang_formatter::format_source(&str) -> Result<String, Diagnostic>`：parse + scan_trivia + format 便捷组合；解析失败透传 E1001。

## SPEC-GAP / 契约疑义（等协调人裁决，均已按现状实现并在测试锁定）

1. **契约 §3 计数笔误（文档修正，非行为）**：「TokenKind 56 类」实为 **64**（7+21+36）；「BinaryOp 17」实为 **18**。TS token.ts/ast.ts 全列表即真值，本实现按全列表落地。请集成轮更正契约数字，防止后续 Agent 按错误数字写 exhaustive 测试。
2. **Arm/EnumCase span**：TS 证据（复刻 Go）构造时 span 置零值；契约 §3 要求「每个节点带 span」。本实现填**真实 span**（超集，不影响语义与诊断码）。
3. **Diagnostic.message 形态**：TS 把「第N行: 」前缀与源码摘录拼进错误文本；本实现 message 只含 Go 复刻文本本体，行号进 `primary_span`（v0.4 行级锚：column 恒 1、offset 0）。前缀/摘录属渲染层，由消费方从 span.line 派生。
4. **L*/Nd 判别精确性**：Rust std `is_alphabetic/is_numeric` 走 Alphabetic/N 派生性质，比 Go `IsLetter/IsDigit`（L*/Nd）宽（会放行上标数字、组合记号进标识符）。内嵌 `unicode_tables.rs`（Python unicodedata，Unicode 16.0 全平面扫描生成，`gen_unicode_tables.py` 可再生成）+ 二分查找；副作用是 Unicode 版本差（Go 1.27 vs 16.0）在最新码点处可能有极小窗口，conformance 面无影响。
5. **顶层 `chan` 保留字行为**：LEX-004 说 `chan` 已启用，但 TS/Go 证据在顶层语句位拒绝 `chan`（「保留字 chan 尚未启用（规划于 M3+）」），chan 仅在类型位置（`chan[int]`）可用、表达式位置 `chan` 记号不可作 primary。已如实复刻证据；语义面影响为零，如需放开由协调人裁决。

## 第二波偏差登记（formatter，与 TS/Go 证据的刻意差异，均测试锁定）

1. **lang-syntax 根重导出 `scan_trivia`（公共 API 小补，登记理由）**：冻结登记里 `scan_trivia` 与 `Trivia/TriviaKind` 并列在 `lang_syntax::` 名下，但第一波 lib.rs 只重导出了类型。本波在 `lang_syntax` 根补 `pub use trivia::{Trivia, TriviaKind, scan_trivia};`——纯增量重导出，不动任何既有路径（`lang_syntax::trivia::scan_trivia` 照旧可用），lang-syntax 原测试全绿。
2. **`flush_remaining` 批内首条注释前不补空行（幂等修正）**：证据实现的空行分组依赖 `lastSrcLine`，而该游标在「空块 + 注释物理位于 `}` 之后」场景跨 `}` 跳变——首次格式化把注释收进块内后块尾行号反转，二次格式化空行移位，破坏 `format(format(x))==format(x)`。修正后首条块尾注释恒紧贴块内末行，批内相邻注释的空行分组保留；R23 幂等在全部语料 + 对抗样本（含本修正目标场景 `fn main() {\n}\n\n// A\n\n// B\n`）上成立。
3. **函数字面量保留 `type_params`**：TS/Go 证据渲染 fnlit 时丢弃 `[T]`（`fn(x: T)`），重解析 AST 即漂移、泛型信息丢失。本实现输出 `fn[T](x: T): T`，AST 形态往返相等。函数声明的签名渲染与证据逐字一致。
