# lang-ts SPEC-GAP 清单

> 记录三类内容：(a) v0.9/v1.0 尚未裁定的规范缺口；(b) 参考实现（go/）自身与
> specs 冲突、lang-ts 如实镜像的点；(c) lang-ts 宿主（TS/Node）特有风险及其
> 处置。最终契约同步后仍开放的项保留编号，供 v1.0 工作流关闭时回填。

## A. 规范 vs 参考实现的既有分歧（lang-ts 镜像参考行为）

1. ~~条件非 bool 的诊断码~~ **已关闭（最终同步）**——specs/control-flow.md
   工作区修订为「非 bool 条件 → E3011」，rules.md 同步；conformance case
   期望改 E3011。lang-ts 现行为（E3011）即最终规范，无需改动。
2. ~~Result 第二参非 error 的诊断行号~~ **已关闭（最终同步）**——conformance
   case 期望改 line 0，与参考/lang-ts 现行为一致。
3. **match 臂绑定写入 scopes[0]**——checker 将 match 臂/for-in 循环变量的
   绑定类型写入全局层而非块层（历史行为）。lang-ts 逐字复刻并在源码注释
   标注。无已知 case 触发差异；若 v1.0 收口块作用域，双侧同步。
4. **Effects fixpoint 实际未生效**——graph/effects.go 中 `symToUnit` 永为空
   （`_ = symToUnit`），调用图传播是死代码，效果只含直接效果。lang-ts 镜像
   「仅直接效果」并在 effects.ts 注明。specs/runtime.md EFFECT-001 声称
   「沿调用图 fixpoint 传播」——规范与实现间缺口，待 ADR 裁决。
5. ~~`Err(e)` 出现在非 Result 函数~~ **已关闭（最终同步）**——v1.0 收口后
   conformance 108 case 全绿（error-value-construction run 模式通过），
   位置面行为双侧一致。
6. **None 构造器跨函数实参的类型实参**——参考 `emitCtor None` 只看当前函数
   类型参数/返回注解，跨函数传 `f(None)`（f: fn(Option[int])）生成的 Go 不
   编译（LangGoOpt[any] ≠ LangGoOpt[int64]）。lang-ts 在**实参收窄位**按目标
   参数类型特化 `LangGoNone[X]()`（gen.ts `noneCtorFor`），比参考多修一处；
   这是行为对齐 specs（三路差分）的最小修复，参考修 Go 时建议同款。

## B. Go 侧已文档化、lang-ts 同样成立的平台差

1. **→Go 真并发的交错序**——TASK-DETERMINISM-003 / runner-contract 约束 3。
   `examples/goroutine` 与 conformance `concurrency/spawn-eval-args-immediately`
   在 →Go 路输出与同步子集不同且自身 flaky。lang-ts 差分工具按文档化例外
   计（NOTE），conformance 失败计入但双侧一致。
2. **运行时错误措辞**——越界/除零等 stderr 文本允许实现差异（v0.8 起 run
   模式只测正确程序）。lang-ts 错误文案按 Go 逐字实现，但措辞不作为契约。

## C. lang-ts 宿主风险处置（Phase J 专项）

1. **JS Number ≠ 语言 float 输出**——`String(v)` 在科学计数区间与 Go `%'g'`
   不一致、`1e-7` 指数位数不同。处置：jsPrelude `langGoFloatStr` 实测对齐
   （最短舍入数字 + exp<-4‖>=6）；配套单测锁 0.1/1e21/1e-05/123456 等边界。
2. **JSON.stringify ≠ Go encoding/json**——缺 HTML 转义、int64 经 Number 丢
   精度、键序为 UTF-16 序。处置：jsPrelude 自实现 `langGoJSONMarshal`
   （码点序排序、`\u003c` 族转义、bigint 直出、float 按 encoding/json 阈值
   1e-6/1e21）；单测锁 `{"n":9223372036854775807}`。
3. **UTF-16 偏移**——JS 字符串索引是 code unit，specs/source-span.md 是
   rune 偏移。处置：词法入口统一 `Array.from` 码点数组，全链（Span/Trivia/
   TextEdit/manifest 解析）只消费码点度量；模式首字节大写判定复刻 Go 的
   UTF-8 首字节 Lu 行为。
4. **Promise/async**——生成 JS 全函数 async 只为吸收染色（decisions #48），
   语义仍单线程同步子集；interpreter/irinterp 无任何 await。微任务序不进入
   语言语义。
5. **undefined/null 隔离**——语言值模型无 null；Result/Option 用显式
   `{ok,has}` 形态，宿主 undefined 仅存在于生成 JS 的信封字段，不参与判定。
6. **ESM/CJS**——全仓 ESM（`"type": "module"`），生成 JS 为 ESM（与 →JS
   runner 一致），无第二形态。
7. **http.get 解释器侧**——Node 无同步 fetch，Go 参考为同步阻塞 HTTP。TS
   侧当前为显式运行时错误（curl 子进程方案弃用：FFI-001 口径平台绑定、
   不进 Conformance，无行为契约）。列入 v1.x 可选项。

## D. conformance 扩容批次（132 case）的套件缺陷（双侧一致，待 v1.0 修 yaml）

v1.0 工作流把套件扩到 132 case 后新增 6 个失败；lang-ts 与 Go 参考实现
（4dd235c 构建的 check/三路）**逐案取证同因同形**：

1. `closure/closure-loop-capture-var` / `match/match-option-in-struct` /
   `syntax/unary-negation-nested`——参考生成的 Go 分别有 missing return /
   LangGoNone[any] / `--5n` 缺陷。lang-ts 已在实现侧修复（emitFnLit 无注解
   → 无返回函数；noneCtorFor 扩展到 struct 字段位；genjs 一元括号化），
   三路一致，比参考多修三处；修复以未提交形式留证（gen.ts/genjs.ts）。
   截至 4dd235c，Go 参考实现同套件 130/132，仍失败其中 2 个
   （closure-loop-capture-var / unary-negation-nested；match-option-in-struct
   已随收口关闭），go/internal/gen/gen.go 工作区修复进行中。
2. ~~`try-on-option` / `try-outside-result-fn` / `match-result-option-mixed`
   的 yaml 期望缺陷~~ **已关闭**——v1.0 工作流已把三个 yaml 修正为双侧
   实际诊断输出（E3005@3+E3008@3、E3005@2+E3006@2、E4003@2+E3006@8），
   lang-ts 复跑 132/132。

## E. 观察项（不阻塞）

- ~~v1.0 收口期 Go 侧套件自身有红~~ 收口批次（4dd235c）落地后 Final Sync
  复跑：lang-ts conformance **108/108**、examples+m4 三路差分 **10/10**、
  vitest **20/20**。
- diagnostics 套件扩容后新增的 assign-to-unknown-var / index-not-int /
  map-key-float / non-iterable / unknown-field / unknown-struct 等case，
  lang-ts 诊断码/行号/规则多重集全部一次通过。
- 套件第三轮扩容（151 case）：§D.2 yaml 修正落地、§D.1 三处多修随
  4f6ca35 双侧落地后，lang-ts **151/151，0 失败**。连续复跑仅见 1 例
  →Go 真 goroutine 时序波动（负载相关），同属 TASK-DETERMINISM-003
  文档化调度自由度，非 lang-ts 缺陷。
- CLI 全动词冒烟（fmt/fix/refactor×3/bind/test/REPL/build，23 断言）
  全绿；REPL 修复 async 化后管道求值回显正常。
- 仓库顶 README「实现状态」表仍写 typescript 未开始；typescript/ 已入库
  （4aea287），README 行由 monorepo 侧维护，不在本实现目录改动。
