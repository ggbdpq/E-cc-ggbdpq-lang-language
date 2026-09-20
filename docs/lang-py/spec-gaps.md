# SPEC-GAP 与实现差异登记（lang-py）

> 本文件登记 lang-py 复刻过程中遇到的「规范未裁定 / 参考实现未定 / 跨实现固有差异」。
> 只有 specs/ 与 conformance/ 能裁决语义；此处每项标注处置与关闭条件。
> 状态：OPEN（等待裁决）/ RESOLVED-MATCH（按参考实现行为对齐并记录）/ NOT_APPLICABLE。

## GAP-001 int 溢出行为（OPEN → RESOLVED-MATCH）

- 事实：LEX-005 规定 int 为 64 位有符号，但未规定算术溢出行为。
- 参考实现：AST 解释器与 →Go 用 Go int64（环绕补码）；→JS 用 BigInt（不环绕）。
- 处置：lang-py 解释器/→Go 路径按 int64 环绕实现（与解释器/→Go 一致）；→JS 路径与参考实现的
  BigInt 一样不环绕。conformance 语料不含溢出用例，三路差分不受影响。
- 关闭条件：spec 增补溢出条款后同步。

## GAP-002 float→string 格式化阈值（RESOLVED-MATCH）

- 事实：RUNTIME-OUTPUT-003 只说「无尾零」，未定义科学计数法切换阈值。
- 参考实现：Go `strconv.FormatFloat(x,'g',-1,64)`——指数 < -4 或 ≥ 21 走 %e。
  Python repr 阈值不同（≥ 1e16 即科学计数）。这是跨实现硬契约点。
- 处置：lang-py 自实现 Go 'g' 语义（最短有效数字 + Go 切换阈值 + Go 的 e+/e- 指数拼法），
  用 float 格式化差分向量与 Go 输出逐字节比对。

## GAP-003 嵌套字符串 quote（RESOLVED-MATCH）

- 事实：RUNTIME-OUTPUT-003 只说「嵌套带引号」，转义形态未定义。
- 处置：按参考实现 Go `strconv.Quote` 复刻（`"`、`\`、\n\t\r 及非打印字符的 \xNN/\uNNNN 形态）。

## GAP-004 JSON 序列化细节（RESOLVED-MATCH）

- 事实：RUNTIME-JSON-002 锁定键序与 None→null，但字符串转义/浮点形态未展开。
- 参考实现：Go `json.Marshal`——紧凑分隔、HTML 转义 `<>&`、指数清洁（e+09→e+9 形态按 Go 规则）。
- 处置：lang-py 手写 JSON 序列化器复刻上述行为，io/json-roundtrip + 追加差分向量锁定。

## GAP-005 v0.9 irinterp 四路差分（OPEN，跟随并行工作流）

- 事实：go/internal/irinterp/ 为并行 v0.9 工作流的未提交 WIP；runner contract 仍是三路。
- 处置：lang-py 同步实现 IR 解释器（复用 interp 值模型），供四路差分；
  verify 呈现层仍按 runner contract 三动词面，不发明第五动词。
- 关闭条件：v0.9 提交后做 Final Contract Sync。

## GAP-006 ToolVersion（RESOLVED-MATCH）

- 事实：contract.ToolVersion = "0.6.0"（未随 v0.7–v0.9 递增）；agent-protocol 说 toolVersion 与
  `lang-go version` 同源。
- 处置：lang-py 使用同值 "0.6.0"，保证 inspect/check JSON 与参考工具逐字节同构；
  schemaVersion="1"。参考实现调整版本时同步。

## GAP-007 运行时错误文本（NOT_APPLICABLE，按既定口径）

- 事实：RUNTIME-FAIL-001 允许运行时错误消息实现差异；conformance run 模式不含错误程序。
- 处置：lang-py 错误消息对齐 Go 参考解释器文本（「第N行: …」），供人类可读与单测断言；
  不作为跨实现契约比对面。

## GAP-008 目录布局命名（RESOLVED）

- 仓库 README 结构表登记实现目录为 `python/`（runner contract 命名可执行文件 lang-go-py）；
  docs/lang-py 计划文档推荐 `lang-py/`。取仓库 README 口径：`python/` 目录 + `lang_py` 包。

## GAP-009 `?` 在嵌套表达式位置的运行时形态（RESOLVED-MATCH）

- 事实：E3005 已静态拒绝非顶层 `?`；静态检查通过后 IR 中 Try 仅存于 let/return/赋值顶层。
- 处置：解释器直接按 AST 语义（Err 信号边界合成）；无额外裁决需求。

## GAP-010 顶层脚本模式（RESOLVED-MATCH）

- 事实：PARSE-001 说明释器接受顶层语句、转译目标显式拒绝（ADR-0001）。
- 处置：lang-py 解释器支持脚本序执行，→Go/→JS 后端对顶层语句报错（与 gen/genjs 的
  「仅声明可转译」约束一致）。

## GAP-011 深递归宿主限制（RESOLVED）

- Python 默认递归深度 1000，深 AST/深递归程序会触宿主 RecursionError。
- 处置：CLI 入口提高 recursionlimit（100000）；语义上无差别。宿主栈耗尽按实现故障处理，
  不转换为语言级诊断。
