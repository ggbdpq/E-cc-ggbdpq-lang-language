# lang-ts 实现地图（Go 包 → TypeScript 模块）

> 原则：禁止逐文件翻译。核心禁止 `any` 逃生口；tsc strict 全开
> （noUncheckedIndexedAccess / exactOptionalPropertyTypes / verbatimModuleSyntax）。
> 跨包零 `any`；`unknown` 仅在 manifest 解析与 spawn 边界收窄。

```text
go/internal/                typescript/src/
─────────────────────       ─────────────────────────────────────
syntax/                     syntax/
  span.go                     span.ts            # 码点数组 + lineStarts；posAt
  lex.go                      lexer.ts           # LexError 文案逐字；hex→十进制规范文本
  ast.go                      ast.ts             # 判别联合（readonly；Span 值类型）
  parse.go                    parser.ts          # ParseError(Incomplete/snippet)；zeroSpan 复刻
  comments.go                 comments.ts
  walk.go                     walk.ts
semantic/                   semantic/
  symbol.go                   symbols.ts         # SymbolId brand；SymbolImpl 可变构建→readonly 视图
  resolver.go                 resolver.ts        # 四遍：prelude→types→fns→globals→bodies
  diagnostic.go               common/diagnostic.ts
types/                      types/
  type.go                     model.ts           # 单例原始类型；equal/assignable/typeStr
  checker.go infer.go anno.go checker.ts anno.ts
  helpers.go                  （opName/joinNames 内联）
hir/                        hir/
  hir.go                      model.ts           # HirNode{id,line,span,sym,typ}
  build.go                    build.ts           # NodeID 先序；构造点求值序逐一复刻
  （v0.9 WalkProg）           walk.ts
ir/                         ir/
  ir.go                       model.ts
  lower.go                    lower.ts           # 槽位同一性；iterKind；FnLit 继承作用域链
irinterp/（v0.9）            irinterp/
  irinterp.go                 irinterp.ts        # frame 槽位 + globalSlots(SymID)；复用值模型
interp/                     interp/
  value.go                    value.ts           # Val 判别联合；goFormatFloat/goQuote/goJSON*
  interp.go interp.go         interp.ts          # Env/Signal/assignAddr(writeBack)
  builtin.go stdlib.go        builtin.ts         # CORE_BUILTINS + makeStdlib；受控 JSON 编解码
  errors（内联）               errors.ts
gen/                        backends/
  gen.go                      gen.ts             # gt 局部 Go 形态；langGoTo 收窄；noneCtorFor
  prelude.go                  goprelude.ts       # 参考同款运行时文本（模板字面量注入）
genjs/                      backends/
  genjs.go                    genjs.ts
  prelude.go                  jsprelude.ts       # 同款 + float/JSON 修正（见 spec-gaps C1/C2）
format/                     format/
  format.go                   format.ts          # 注释 trivia 插回；幂等
load/                       loader/
  load.go                     loader.ts
pipeline/                   pipeline/
  pipeline.go                 pipeline.ts        # parseSource/checkStmts/analyzeStmts/DiagList
edit/                       edit/
  edit.go                     edit.ts            # sha256 前置；重叠拒绝；降序原子应用
fix/                        fix/
  fix.go                      fix.ts
contract/                   contract/
  model.go rules.go           rules.ts           # 27 码映射
  check.go dumpast.go dump.go contract.ts      # emitJSON=MarshalIndent+HTML 转义
  inspect.go effects.go       （effects → graph/effects.ts）
  graph.go                    （impact 形态在 cli/main.ts 内联组装）
graph/                      graph/
  graph.go build.go query.go  graph.ts
  effects.go                  effects.ts         # 仅直接效果（镜像参考现状）
refactor/                   refactor/
  locate.go refs.go rename.go refactor.ts
  remove.go imports.go        （organizeImports 同文件）
bind/                       bind/
  bind.go（go/parser）        bind.ts            # 轻量 Go 签名扫描（FFI 面）
conformance/                conformance/
  manifest.go runner.go       manifest.ts        # 受限 YAML；run=四方差分；check=多重集
  verify.go                   verify.ts
version/（v1.0 Gate E）      version/
  version.go                  version.ts         # 1.0.0 单一版本源
cmd/lang-go/cli.go          cli/main.ts          # 全动词 + REPL；自称 lang-ts
go test *_test.go           tests/unit/ tests/differential/（vitest）
```

## 与参考实现的有意结构差异

1. **一个模块一个职责包**（Go 的 contract/inspect+dump+check 拆为 contract.ts
   单文件 + graph 形态组装在 CLI）——协议形态不变，减少跨文件跳转。
2. **错误通道**：Go 用 `error` 值承载控制流信号（signal/tryErr），TS 用异常
   类（Signal/TryErr/RtError）——执行路径等价，finally/rest 语义更直白。
3. **预载定义单源**：interp.ts `prelude()` 与 irinterp.ts 常量表显式并列
   （对应 Go v1.0 Gate D1 的 internal/prelude 纯数据表）；case 序契约由测试
   锁定。
4. **诊断单源**：E 码常量在 common/diagnostic.ts，resolver/types/pipeline 共
   同引用；Rule 映射独立于 contract（rules.ts），与 Go 的双实现点对应。
