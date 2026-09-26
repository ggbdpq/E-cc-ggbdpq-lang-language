# lang-rs SPEC-GAP 台账

登记规则：发现 spec 未写死 / spec 与实现冲突 / 三实现共同可疑行为，先查 `specs/`，查不到即登记，
格式 `SPEC-GAP-RS-XXX`（XXX 三位序号）。分类：OPEN / RESOLVED_BY_SPEC / RESOLVED_BY_CONFORMANCE /
IMPLEMENTATION_BUG / MULTI_IMPL_SHARED_BUG / NOT_APPLICABLE。冲突详情按
`2026-09-25-lang-rs-SPEC冲突处理模板.md` 展开。**影响语义的 OPEN gap 未清零不得宣布 L6。**

| ID | 主题 | 描述 | 分类 | 状态 |
|---|---|---|---|---|
| SPEC-GAP-RS-001 | 输出面·嵌套字符串引号转义 | RUNTIME-OUTPUT-003 只定「嵌套带引号」未定死转义细节；实现证据互相分歧：→Go prelude 用 strconv.Quote（非打印 Unicode→`\uNNNN`/`\UNNNNNNNN`、DEL→`\x7f`、U+2028/2029 转义），→JS prelude 用 JSON.stringify（同码点直出），TS interp goQuote = Quote 语义。同一 println 在两后端对含 U+2028/DEL/NBSP 的嵌套字符串输出逐字节不同 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 两后端 fmt 层各自锁定其目标语义；conformance 语料须避开分歧码点，或裁决统一为 Quote 语义） |
| SPEC-GAP-RS-002 | jsonStringify·浮点格式 | Go encoding/json 与 TS interp：abs∈[1e-6,1e21) 定点（1e8→`100000000`），否则 e 形负指数去前导零（1e-7→`1e-7`）；→JS prelude langGoJSONFloat 的 f 段误走 'g' 形（1e8→`1e+08`）。另 -0.0：Go json→`-0`、TS interp→`0`、jsprelude→`-0` 三方分歧。RUNTIME-JSON-002 要求跨实现逐字节确定但未写死这些点的规范值 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 以 TS interp 为规范源：f 段定点、-0.0→`0`；语料避开分歧点或裁决） |
| SPEC-GAP-RS-003 | jsonStringify·非字符串键 | 映射键非 string 时三方分歧：TS interp 报运行时错误 `JSON 对象键须为 string，得到 int`；→JS prelude 把键按顶层字符串形直出（`String(k)` 化）；→Go prelude 直接 panic（接口断言失败，消息非受控）。jsonParse 产物只有 string 键，实际可达性低但语义面未定 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006，五实现同步随本波落地）｜原口径：lang-rs 取 interp 语义：报错；错误消息进冻结接口） |
| SPEC-GAP-RS-004 | 词法·顶层 chan 保留字 | 证据实现在顶层语句位拒绝 `chan`（仅类型位置可用），与 LEX-004「已启用保留字」的表述存在张力 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 如实复刻证据行为；待 spec 裁决是否放开） |
| SPEC-GAP-RS-005 | AST span·Arm/EnumCase | TS 证据中 match 臂/enum case 节点 span 为零值（参考实现构造缺陷）；契约要求每节点带 span | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 填真实 span，为证据超集；dump ast 对齐时注意） |
| SPEC-GAP-RS-006 | 运行时·int(float) 越界 | float→int 转换越界（含 ±inf、NaN）行为 spec 未写死 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 暂按 amd64 证据 MinInt64；改 builtin_int 一处可换口径） |
| SPEC-GAP-RS-007 | 运行时·float NaN 比较 | NaN 参与比较的运行时行为 spec 未写死 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 暂按 IEEE；同上可换口径） |
| SPEC-GAP-RS-008 | 文档漂移·conformance 分类数 | conformance/README 写「14 类」，实测 16 类 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：DOC_DRIFT；lang-rs 以实测为准） |
| SPEC-GAP-RS-009 | conformance·YAML 锚点 | serde_yaml 不拒 YAML 锚点/引用；manifest 若出现锚点，跨实现解析器行为未定 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 解析端按 serde_yaml 语义；必要时 manifest 侧禁锚点） |
| SPEC-GAP-RS-010 | conformance·无位置诊断 | 套件真值中存在 line:0 无位置诊断（check 类），行级契约下 0 行语义未定 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 暂按 0 值透传） |
| SPEC-GAP-RS-011 | 检查·赋值顶层 `?` | RESULT-TRY-003 明文允许 `?` 出现在「let/return/赋值的顶层」，但 TS/Go 检查器在赋值路径走 infer（误发 E3005）；Go interp 的 `?` 是通用表达式求值（eval 内 `case *syntax.TryEx`），运行时赋值顶层 `?` 实际可用——spec 与运行时一致、与静态检查证据冲突 | IMPLEMENTATION_BUG | RESOLVED_BY_SPEC（ADR-0005：赋值右值顶层 `?` 合法；lang-rs 已按此实现，lang-cs 直接采纳；TS/Go checker 误报修复由各自维护节奏决定） |
| SPEC-GAP-RS-012 | 检查·块内绑定类型写入全局层 | 证据检查器把块内 let/循环变量/match 臂绑定的类型写进全局层（Go 历史行为，TS 注释明说如实复刻），跨块同名绑定会发生类型泄漏，违背 NAME-SCOPE-001/MATCH-BIND-003 的词法作用域语义 | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 写入当前块作用域；30/30 diagnostics 夹具行为与证据一致；若裁决从证据，改 Checker 三处写入点） |
| SPEC-GAP-RS-013 | 泛型·用户泛型 struct/enum 无诊断码 | GEN-DECL-001 说「用户泛型 struct/enum v0.9 冻结不支持」，但 27 码表无对应码，TS/Go 证据静默容忍（类型参数按擦除占位参与检查） | RESOLVED_BY_SPEC | RESOLVED_BY_SPEC（ADR-0006）｜原口径：lang-rs 同证据口径：声明不报错，T 解析为 TypeParam 擦除占位；如需硬拒绝请裁决补码） |
