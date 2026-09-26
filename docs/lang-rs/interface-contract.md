# lang-rs 接口契约 v1（协调人冻结）

> 本文件是六个开发智能体共同编码的锚。改动本文件只能由协调人在集成轮执行。
> 行为真值顺序：`specs/ > specs/rules.md > conformance/ > 各实现`。Go/TS/Py 只是行为证据，禁止复制宿主语义。

## 1. Crate 拓扑与所有权

```text
lang-core ← lang-syntax ← lang-semantic ← lang-hir ← lang-ir
                ↓              ↓             ↓        ↓
            lang-runtime ——— lang-interp — backends — lang-verify — lang-cli
```

| Agent | 独占 crate（只能改这些） | 首批交付 |
|---|---|---|
| 协调人 | `lang-core`、workspace `Cargo.toml`、`docs/lang-rs/*` | SourceText/Span/ID/Diagnostic |
| A1 Syntax | `lang-syntax`、`lang-formatter` | token/lexer/parser/AST/trivia |
| A2 Semantic | `lang-types`、`lang-semantic` | 类型格、resolver、checker、诊断发射 |
| A3 HIR/IR | `lang-hir`、`lang-ir` | HIR/IR 模型、validator、lowering |
| A4 Runtime | `lang-runtime`、`lang-interp` | Value/Place/Ref/Closure/Task/Chan、双解释器 |
| A5 Backend/Tooling | `lang-backend-go`、`lang-backend-js`、`lang-loader`、`lang-cli` | 双后端、模块装载、CLI 骨架 |
| A6 Agent/Verify | `lang-contract`、`lang-graph`、`lang-edit`、`lang-refactor`、`lang-verify`、`lang-conformance` | DTO/效果不动点/图/安全编辑/重构/conformance runner |

依赖方向已在各 Cargo.toml 预接线；禁止改别人的 Cargo.toml。需要新依赖只能改自己 crate 的 Cargo.toml，且先查 workspace 依赖表（`serde`/`serde_json`/`serde_yaml`/`indexmap` 已就绪）。

## 2. 已冻结接口（lang-core）

```rust
// ids.rs：全部 newtype(u32)，Display 直出数字
SourceId, NodeId, SymbolId, TypeId, FunctionId, LocalId

// span.rs —— 对外契约一律码点（specs/source-span.md）
Position { offset: u32, line: u32, column: u32 }   // offset 0 基码点；line/column 1 基
Span { source: SourceId, start: Position, end: Position }  // 半开 [start, end)，允许 start==end

// source.rs
SourceText::new(path: impl Into<String>, text: String) -> SourceText
SourceText::byte_to_cp(&self, byte: usize) -> u32      // 内部 parser 用字节，出边界前必须转码点
SourceText::cp_to_byte(&self, cp: u32) -> usize
SourceText::line_col(&self, cp: u32) -> (u32, u32)     // 1 基；换行只认 \n，\r 归上一行内容

// diagnostic.rs —— 结构即契约（specs/diagnostics.md）
Severity { Error, Warning }
DiagnosticCode { 27 个变体 E1001..E4003 }，as_str() -> &'static str
RuleId { rules.md 全量变体 }，as_str() -> &'static str
Diagnostic { code, rule_id, severity, message: String,
             primary_span: Option<Span>,   // v0.4 行级：column 恒 1、offset 未填（填 0）
             related_spans: Vec<Span>, help: String }
```

诊断码 ↔ Rule 映射（`DiagnosticCode::rule()`）按 `specs/rules.md` 表逐行实现，E1001→LEX-ALL 等 27 条，测试锁定。

## 3. 语法面冻结（A1 拥有定义，全员按此消费）

TokenKind 64 类（`impls/typescript/src/syntax/token.ts` 为证据）：关键字 21（let var fn return if else while for in match struct enum true false break continue go chan import extern interface）+ eof newline ident int float string underscore + 运算符/标点 36（+ - * / % = == != < <= > >= && || ! & | ^ << >> += -= *= /= ? . .. , : => ( ) { } [ ]）。
`Token { kind, lit: String, span: Span }`（int 的 lit 存十进制规范文本；string 的 lit 存解码后值）。

AST 变体（Rust enum，exhaustive match）：
- `Expr` 18：IntLit(i64) FloatLit(f64) StrLit BoolLit Ident Unary(- ! &) Binary Range Call Index Slice Field Try StructLit MapLit ArrLit Match FnLit
- `BinaryOp` 18：== != < <= > >= + - * / % & | ^ << >> && ||；`AssignOp` 5：= += -= *= /=
- `Pat` 4：Wild Bind Lit Case；`TypeEx` 5：Named Arr Map Ptr Fn
- `Stmt` 15：Let Assign Expr Return If While ForIn Break Continue FnDecl StructDecl EnumDecl Go Import Extern
- 每个节点带 `span: Span`；FnLit/FnDecl/StructDecl/EnumDecl 带 `type_params: Vec<String>`；FnDecl 带 `recv: Option<Param>`

## 4. 各 crate 冻结前的接口登记制度

跨 crate 消费的公共签名（IR 指令集、Value enum、HIR 节点、语义分析入口、loader API、DTO schema）落地前必须在 `status-board.md` 自己的 section 登记一行「冻结：<crate>::<item>：<一句话形状>」。登记后改签名 = 破坏契约，只能走协调人集成轮。消费方发现签名缺口：先在 status-board 记 BLOCKED，不许直接改 owner 的文件。

## 5. 工程纪律

- TDD：每个切片先写会失败的测试（红）再实现（绿）。测试放 crate 内 `#[cfg(test)] mod tests` 或 `tests/`。期望值来自 specs/conformance/手算真值，禁止拿实现当期望（防同义反复）。
- 完成块判据：`cargo test -p <自己 crate>` 全绿 + `cargo clippy -p <crate> -- -D warnings` 干净 + `cargo fmt -p <crate>` 已跑。环境变量见 §7。
- `#![forbid(unsafe_code)]` 已在每个 lib.rs。禁 `unwrap/expect/panic!` 处理用户源码错误（内部不变量破坏用 internal error）；公开集合排序确定性（BTreeMap/IndexMap/显式 sort，禁 HashMap 迭代序外泄）；避免 `Rc<RefCell<_>>`/`dyn Any`/clone 泛滥（计划 §6）。
- 禁 git 写操作。禁动 `impls/` 其他实现目录与 `specs/ conformance/ schemas/`。
- SPEC 冲突：查 `docs/lang-rs/2026-09-25-lang-rs-SPEC冲突处理模板.md`，登记进 `docs/lang-rs/spec-gaps.md`（SPEC-GAP-RS-XXX），等协调人裁决，禁止为 parity 复制三实现共同错误。

## 6. 行为参考（只读证据，不复制源码）

- 语义问题先读 `specs/<主题>.md`；spec 没写死 → 登记 SPEC-GAP，不猜。
- 实现证据：`impls/typescript/src/`（结构最清晰）、`impls/go/internal/`（参考实现）、`impls/python/src/`。
- 对抗重点：`docs/lang-rs/2026-09-25-lang-rs-对抗验证重点.md`（scope/effects/closure/generic/reference/eval-order/Unicode/output/concurrency）。

## 7. 本机工具链（迁移后布局，每次 cargo/go 调用前 export）

```bash
export CARGO_HOME="D:\DevTools\Languages\Backend\Rust\.cargo"
export RUSTUP_HOME="D:\DevTools\Languages\Backend\Rust\.rustup"
export PATH="/d/DevTools/Languages/Backend/Rust/.cargo/bin:/d/DevTools/Languages/Backend/Golang/Go/bin:$PATH"
```

Rust 1.98.1 stable-msvc，Go 1.27.0（→Go 差分/go build 用），node v24.21.0，python 3.14.7。
