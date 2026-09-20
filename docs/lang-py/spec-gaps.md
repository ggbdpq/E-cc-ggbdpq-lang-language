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

## GAP-005 irinterp 四路差分（RESOLVED-MATCH，v1.0 已收口）

- 事实：v1.0 提交 4dd235c 将 irinterp 定为正式四路差分面，并补 deref 全链。
- 处置：lang_py.irinterp 复用 interp 值模型实现同一机器；verify 的 execute/differential
  阶段纳入 IR 路；AST-vs-IR 差分对全部 conformance case 逐字节一致（pytest 锁定；套件滚动扩容）。
- 状态：已关闭。

## GAP-006 ToolVersion（RESOLVED-MATCH，随 versioning.md 更新）

- 事实：v1.0 起版本唯一版本源为 go/internal/version（1.0.0），specs/versioning.md 规定
  第二实现 toolVersion 在同一 1.x.y 语义下独立打点，契约版本随 specs/。
- 处置：lang-py TOOL_VERSION = "1.0.0"，schemaVersion = "1"。

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

## GAP-012 CTRL-IF-001 诊断码裁定（RESOLVED-MATCH）

- v1.0 裁定：if/while 条件非 bool → E3011（TYPE-INDEX-008）。lang-py checker 自侦察起
  即按 E3011 实现，与裁定一致，无需改动。

## GAP-013 go 语句实参快照（RESOLVED-MATCH）

- v1.0 语义收口：EVAL-GO-007 实参在 go 语句处就地求值并快照进临时变量，
  goroutine 闭包不得按引用捕获调用方可变局部（修复 →Go 闭包时序缺陷，
  conformance concurrency/spawn-eval-args-immediately 锁定）。
- 处置：lang-py →Go 后端同步该发射形态；解释器本就按值传参天然满足。
- 状态：132/132 全绿（含参考侧在途收口的 5 例，lang-py 先行实现正确语义）。

## GAP-014 容器元素字段链赋值（RESOLVED-MATCH）

- v1.0 语义收口（VALUE-ELEM-009）：xs[i].f = v 为读改写（容器元素按值语义整体回写）。
- 处置：lang-py →Go 后端同步 langGoIndex 读出→字段写→langGoSetIndex 回写的发射形态。

## GAP-010 顶层脚本模式（RESOLVED-MATCH）

- 事实：PARSE-001 说明释器接受顶层语句、转译目标显式拒绝（ADR-0001）。
- 处置：lang-py 解释器支持脚本序执行，→Go/→JS 后端对顶层语句报错（与 gen/genjs 的
  「仅声明可转译」约束一致）。

## GAP-011 深递归宿主限制（RESOLVED）

- Python 默认递归深度 1000，深 AST/深递归程序会触宿主 RecursionError。
- 处置：CLI 入口提高 recursionlimit（100000）；语义上无差别。宿主栈耗尽按实现故障处理，
  不转换为语言级诊断。
