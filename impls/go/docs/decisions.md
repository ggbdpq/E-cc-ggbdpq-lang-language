# lang-go 设计决策台账

本文是 grilling 共识的原始记录（2026-09-17/18 两轮质询落定）。任何后续设计变更须在此留痕，不许静默漂移。

## 根定位

| # | 决定 | 理由（一句话） |
|---|------|----------------|
| 1 | **定位：个人统一工具链**（编译器学习为赠品），不求社区采纳 | 三域统一语言是五十年坟场（D/Nim/Haxe/KMP/V），只有"自用"定位活得下来（Haxe 先例）；用户全部历史是个人工具项目 |
| 2 | **系统上限：Go 级**（CLI/服务/工具链），v1 不建所有权系统 | 无内核场景；所有权系统是语言项目死亡头号原因，成本 ×5 |
| 3 | **前端：编译到 JS 起步**，热点 WASM 后议（演进为 JS 主 + WASM 逃逸舱） | 浏览器只吃 JS/WASM；真实前端场景全是 DOM + npm 生态，WASM-only 自断粮道 |
| 4 | **并发：goroutine 语义，函数永不染色**（无 async 关键字） | 染色是传染性债务；JS 侧映射到事件循环（v1 单线程协作式，见 #15） |
| 5 | **实现：Go 写编译器；transpile-first**（→Go、→JS 双后端）+ 树遍历解释器当 REPL/调试器 | 转译白拿两边生态，自验证最快；LLVM 是时间无底洞。先例：Haxe、Nim（C 后端）、Scala.js |
| 6 | **类型：静态强推断 + 结构化类型 + Result 无异常**；C 家族花括号、无分号、贴近 TS | 结构化类型从 TS 生态白拿兼容性；Result 与 `?` 是成对决定 |
| 7 | **验收线 M4**：用 lang-go 重写 linkpull 的 bilibili 提取器核心并测试全绿；落点 `vibe/labs/language/go/` | 有参照实现 + 现成测试，"行为等价"可机械判定，不靠感觉 |
| 8 | **工具链裁剪：只留编译器 + REPL + 格式化器**；v1 无 LSP/调试器/包管理器 | 烂尾点全在工具链打磨；活过第一年比什么都重要 |
| 9 | **名字：lang-go** | 用户拍板 |

## 第 2 轮（语义层）

| # | 决定 | 理由 |
|---|------|------|
| 10 | **GC 由宿主运行时承担**（→Go 用 Go GC，→JS 用引擎 GC），lang-go 不自带运行时 GC；v2 再议 arena 逃逸舱 | 随 #2×#5 的推论，转译架构白拿 |
| 11 | **值语义**：struct 赋值/传参/返回时拷贝；显式 `&T` 指针做共享可变；slice/map 引用语义；string 不可变 | "系统感"的成色所在；Go 派折中 |
| 12 | **消灭 null**：`Option[T]` + sum type（enum）+ 穷尽 match 整层采纳 | Result 已是 sum type，顺手整层上；相对 Go 的最大卖点 |
| 13 | **泛型：有界参数化 + 调用点推断**（Go 1.18 派，interface 作边界）；不做 HKT | Option/Result 自己就需要；够用且可转译 |
| 14 | **错误传播：`?` 后缀 + match 处理** | Result 语义的正确搭档，抄 Go 的啰嗦写法两头不讨好 |
| 15 | **宏：v1 完全没有** | 宏让双后端 codegen 和格式化器同时复杂一倍，M4 后有痛点再议 |
| 16 | **前端并发诚实退化**：→JS 后端单线程协作式（go→微任务、channel→队列），平台差异写进 spec | CSP 语义不承诺并行，差异可诚实记录；真并行热点走 WASM 后议 |
| 17 | **互操作：手写 extern 声明块**（→Go 链 Go 包，→JS 调 JS 全局/npm）；v1 不做 .d.ts 摄入 | Haxe/TS 早期验证过的最小可行方案 |
| 18 | **标准库：可移植核心 + 按后端委托**（→Go 映射 Go stdlib，→JS 映射内置对象/fetch），不自研轮子 | 转译架构最大捷径；两端行为微差记录在案 |
| 19 | **模块：目录=包，`lang-go.toml` 标模块根**，import 按根相对路径 | 一行解析顶住"哪里是根"的歧义 |
| 20 | **M4 项目**：重写 linkpull `video.go`+`wbi.go`+`md5.go`+`list.go`（纯 stdlib，约 650 行 + 149 行测试），经 →Go 后端跑同一批 fixture；编译器自带 `lang-go test` 跑 `*_test.lang-go` | 见 #7 |

## 里程碑

- **M1** spec.md 定稿（语法 + 语义）——已完成
- **M2** Go 实现解析器 + 树遍历解释器，REPL 可跑，`lang-go test` 可用——已完成
- **M3** 双转译后端（→Go、→JS）各自跑通；静态类型检查器落地——**→Go 后端已完成**（E2E 输出等价 4/4，穷尽检查前移到编译期）；→JS 后端与 import 未开始
- **M4** bilibili 提取器移植，`lang-go test` 全绿 = 验收——未开始

## M3 落地时的补充决策（2026-09-18 追加）

| # | 决定 | 理由 |
|---|------|------|
| 21 | **静态检查器不阻塞 →Go 转译**：类型错误交给 Go 编译器兜底，穷尽性检查随转译器先行落地，全量结构化检查器后置 | 转译产物的类型安全由目标编译器保证；M4 验收不依赖自研检查器 |
| 22 | **→Go 映射**：Result/Option 用泛型结构而非 `(T, error)` 元组 | 元组方案下"Result 存进变量/作实参"需拆两个变量，任意表达式位置的翻译不成立；泛型结构保证每个 lang-go 表达式映射到单个 Go 表达式 |
| 23 | **enum 载荷装箱为 `[]any`**（LangGoEnum） | 不同 case 载荷类型/数量不一，静态字段方案需要完整类型推断；装箱以少量动态成本换转译器简洁，match 绑定处按声明注解插类型断言 |
| 24 | **用户名加前缀**（函数 `lf_`、构造器 `le_`） | `map` 等用户名撞 Go 关键字；统一前缀免维护冲突表 |
| 25 | **转译约束**：参数注解必填、`?` 只在语句顶层、Result 函数 return 须为 Ok/Err | 每条都对应一个"无注解就写不出 Go 签名/展开"的硬约束，报错在转译期 |
| 26 | **数组包 `*LangGoArr[T]`** 而非直接用 Go slice | lang-go 的 push 是原位变更且别名可见；Go append 可能重分配破坏别名语义 |
| 27 | **等价验收**：解释器输出 vs Go 编译执行输出逐字节一致 | "后端跑通"从感觉变成机械判定；prelude 格式化函数逐字节镜像解释器 fmtVal |
| 28 | **语言侧新增函数类型语法 `fn(T): U`** | 函数值要跨后端传递就必须有可注解的类型；解释器继续忽略注解 |

## M4 落地时的补充决策（2026-09-18 追加）

| # | 决定 | 理由 |
|---|------|------|
| 29 | **位运算入语言**：中缀 `&` `\|` `^` `<<` `>>`，优先级对齐 Go（`<< >> &` 与 `*` 同级，`\| ^` 与 `+` 同级） | MD5 移植的硬前置；`&` 前缀仍是取引用，位置不冲突；无 uint32，32 位回绕用掩码惯用法 |
| 30 | **新增 `bytes(s)`/`char(n)` 内建** | UTF-8 字节与码点字符是 MD5/URL 编码的最小 I/O 面；再多的字符串库留到有第二个消费方再加 |
| 31 | **MD5 K 表固化为数据**（gofixture 从 Go 原版 init 公式现算生成，十进制内嵌） | 不为一张常量表给 lang-go 加 sin 与浮点回绕；常量出处由生成器文档化 |
| 32 | **目录即包**：`lang-go run`/`lang-go test` 装载当前目录全部非测试 `.lang-go`（文件名序），测试文件排最后 | M4 需要实现/测试分文件；无 import 语义前，目录合并就是包的最小形态，与「目录=包」约定（#19）一致 |
| 33 | **对照验收**：m4/gofixture 逐字拷贝 Go 原版纯函数并打印 fixtures，lang-go 移植的三条执行路径（解释器、→Go 编译、lang-go test）全部与其对齐 | 移植正确性的真值来自原版实现而非文档或记忆 |
| 34 | **转译器括号纪律**：二元/一元运算的子表达式生成时强制加括号 | lang-go 与 Go 优先级表存在残留差异（如 Go 的 `%` 高于 `+`），裸表达式会被 Go 重新结合；全括号是转译器标准做法，gofmt 之后可读性无损 |

## 第 4 轮 grilling 与 M4 收口（2026-09-18 追加，50 问全按推荐落定）

| # | 决定 | 理由 |
|---|------|------|
| 35 | **extern 形态**：`extern "go" { fn 签名 }` 手写声明块；解释器调用即报"仅转译可用"，→Go 生成 panic 桩 | 最小可行互操作面（#17 方向落地）；链接由 prelude/人工提供 |
| 36 | **标准库最小面**：`http.get(url, headers) → Result[HttpResp, error]`（4xx/5xx 也是 Ok）+ `jsonParse`/`jsonStringify` 动态值 + `isNone`；解释器与 →Go 各自实现、行为逐字节对齐 | video/list 移植的全部外部依赖；JSON null → `None`（无 null 语言的自然映射） |
| 37 | **JSON 数字**：无小数点即 int，否则 float；对象键排序、stringify 键按字母序 | 两端确定性（Go map 序随机，排序消歧）；大整数 >2^53 风险文档化 |
| 38 | **import**：仅 `"./相对路径"`（文件或目录）；装载器合并目录即包 + import 图；循环报错、菱形去重、越根报错 | 多文件 M4 的最小模块机制；lang-go.toml/多模块继续冻结 |
| 39 | **M4 收口范围**：video/list 的纯函数层（codecName/fpsOf/pickCodec/pageOf/parseArchives/listKindOf）移植 + fixture 化测试；probeVideo/probeList 网络编排不移植 | 网络编排属 linkpull 结构体层；纯函数层已覆盖全部判定逻辑 |
| 40 | **转译器补丁三则**：装箱值取字段走 `langGoField` 接口分派；any 迭代走 `langGoIter` 快照；FnLit 推断完整签名 + 函数值绑定注册参数类型 | 动态值（JSON/http 结果）在 any 边界的三个统一出口，替代逐点特判 |
| 41 | **明确不做**（本轮确认）：uint32、多返回值、三元 for、解构、defer、指针接收者方法、enum 字段名载荷、字符串插值、expect 断言库、self-host、性能目标 | 最小核纪律；每个消费方出现再议 |
| 42 | **git 提交策略**：按里程碑分批提交 lang-go 目录（M1+M2 / M3 / M4），lang-go.exe 与 out/ 进 .gitignore | 用户授权（grilling Q47）；回滚粒度对齐里程碑 |

## M5 第一批（2026-09-18 追加）

| # | 决定 | 理由 |
|---|------|------|
| 43 | **格式化器**：AST 重排 + 整行注释按行号挂回；硬契约 = 幂等 + 语义等价（测试锁定，语料为仓内全部 .lang-go）；限制 = 行尾注释与 match 臂内注释丢失 | 工具链三项承诺（#8）的最后一项兑现；注释移动不影响语义，完整保注释留到有第二个消费方 |
| 44 | **`lang-go fmt` 子命令**：就地格式化当前目录 .lang-go，打印改动文件 | 与 run/test 同款"当前目录"安全面 |
| 45 | **错误源码摘录**（Q22）：解析/词法错误附 `  | 源码行`；EOF 场景摘录回退最后非空行 | 定位错误不用翻文件；EOF 行号语义（末尾之后）保持并文档化 |
| 46 | **R7（CLI 子目录参数）搁置**：Mimosa 安全钩子对 os.Args→文件读取数据流零容忍（四连拦实证），对抗成本大于"少 cd 一次"的收益 | 保持 cd 工作流；若钩子策略变化可随时重启该项 |

## M5 第二批（2026-09-18 追加）

| # | 决定 | 理由 |
|---|------|------|
| 47 | **→JS 映射：int 用 BigInt** | JS 原生位运算是 32 位有符号，number 方案会破坏 MD5 的掩码惯用法；BigInt 让 64 位语义与解释器逐位一致 |
| 48 | **→JS 生成全部 async**（http 委托是异步；同步调用 await 无害） | lang-go 无函数染色，async 传染被 await-everything 策略吸收；性能非目标 |
| 49 | **`lang-go check` = 复用转译器前置检查**，不写独立检查器 | 穷尽/?/注解/未定义名四项检查 →JS 转译器已实现，检查即"转译一遍丢产物"，零新逻辑 |
| 50 | **struct 值语义在 →JS 用 langGoCopy 三时机**（绑定/传参/返回；数组与映射共享） | 与解释器 copyValue 调用点一一对应 |
| 51 | **v0.2.0 tag**：M4 验收 + 双后端 + fmt/check 工具链齐备 | Q46 授权的打点条件已满足 |

## v0.2.0 后续（2026-09-19 追加）

| # | 决定 | 理由 |
|---|------|------|
| 52 | **复合赋值 `+=` `-=` `*=` `/=`**（Q26 批准项兑现）：目标可为变量/字段/索引；`+=` 对数组是原位拼接（push 语义） | gualang 对比时发现批准未兑现；数组拼接语义与 gualang 直觉一致；let 绑定不可复合赋值（与 `=` 同规） |
| 53 | **gualang 对比结论**：教学脚本语言（动态类型/闭源 IDE/JS 系全引用）与 lang-go（静态注解/三路等价/值语义）定位正交；其"库写在本语言里"（wave.gua）是 lang-go 尚未覆盖的生态位 | 对比报告 2026-09-19；用户库机制列入后续候选 |

## 完成度审计补遗（2026-09-19 追加）

| # | 决定 | 理由 |
|---|------|------|
| 54 | **十六进制字面量 `0x/0X` 兑现**（Q28）：词法统一转十进制记号，fmt 输出十进制 | 源码可读性收益兑现；fmt 单一形态保证幂等 |
| 55 | **`lang-go build [go\|js]` 兑现**（Q21）：静态检查通过后写 out/main.go 或 out/main.mjs | 产物落盘供人工审查与外部工具链（go build/node）消费；写路径常量，无穿越面 |
| 56 | **Q23 REPL `:format` 判定跳过** | REPL 无持久 buffer，格式化对象不存在；等价能力已由 `lang-go fmt` 覆盖 |

## 明确不做（v1）

null、异常、宏、继承、隐式数值转换、包管理器、LSP、调试器、LLVM、指针算术、.d.ts 自动摄入。

## 并发第一批（2026-09-19 追加，候选清单全部批准）

| # | 决定 | 理由 |
|---|------|------|
| 57 | **`go <调用>` 语句**：fire-and-forget 后台任务；→Go 真 goroutine + WaitGroup，函数边界与进程退出前 join；→JS/解释器单线程同步执行（顺序确定的语义子集）；仅允许具名函数调用 | 并发最小切片；chan 的阻塞语义需要独立设计轮（候选继续） |
| 58 | **性能（字节码）永久搁置**、**extern 自动绑定 / .d.ts 摄入转候选** | Q44 正确性优先，无真实慢点不动；自动绑定是独立编译器前端量级，消费方出现再立项 |

## 并发第二批：chan（2026-09-19 追加）

| # | 决定 | 理由 |
|---|------|------|
| 59 | **chan 最小面**：`chan[T]` 类型注解（→Go 装箱 `chan any`，T 仅文档）+ `makeChan()`/`send(ch, v)`/`recv(ch) → Result[T, error]` 内建。**非阻塞语义**：send 不阻塞（→Go 缓冲 1024、满则运行时错）；recv 空即 Err（→Go select-default、→JS/解释器队列判空） | 三路一致的可测语义：阻塞语义在单线程解释器无法忠实模拟，非阻塞队列可用且确定；跨 goroutine 时序依赖标记为 v0 不定 |
| 60 | **`lang-go check` 的转译约束覆盖扩展**：channel 示例的参数注解缺失被 check 拦下（chan[T] 解析 + goTypeWith 同步落地） | check 即转译前置检查的设计（#49）自然获得新语法覆盖（v0.3 起由 #66 取代） |

## extern 自动绑定（2026-09-19 追加，#58 候选转正第一批）

| # | 决定 | 理由 |
|---|------|------|
| 61 | **`lang-go bind`**（包目录经环境变量 `LANG_GO_BIND_DIR` 提供，CLI 维持不收路径参数的安全边界）：go/parser 解析导出函数签名生成 extern "go" 声明草案。映射：基本类型直映（uint 系→int，文档化截断）、[]T→[T]（[]byte→[int]）、(T, error)→Result[T, error]；指针/struct/接口/map/chan/func/多返回值跳过并列原因；跳过 _test.go。**草案非免审产物**——声明需人工审核，实现仍由 prelude/宿主提供 | 自动绑定最小切片：解析走标准库 go/parser（无 exec/go doc 依赖）；草案定位是审核起点而非成品绑定；CLI 维持不收路径参数的安全边界，目录固定为当前目录 bind.d/（常量路径，任何输入不进遍历数据流）|
| 62 | **.d.ts 摄入评估结论：维持候选，不立项** | TS 类型声明语言（泛型条件类型/重载/命名空间）解析量级远超 go 签名；当前无浏览器端消费场景，立项即无人消费——与 #41 最小核纪律同判 |
## 并发第三批：recv 阻塞语义（2026-09-19 追加，设计轮）

| # | 决定 | 理由 |
|---|------|------|
| 63 | **recv 升级为阻塞语义**（修订 #59 的非阻塞 recv）：`recv(ch)` 阻塞直到有值；`Result[T, error]` 返回保留。三路映射：→Go 用原生 `<-ch`（缓冲 1024 与 send 满则错不变）；解释器/→JS 零代码改动——go 同步执行语义（#57/#16）下 recv 执行时要么已有值、要么永远无人 send。**死锁行为分歧文档化**：recv 空且无任务会 send = 死锁程序错误，解释器/→JS 立即 Err("channel 空")，→Go 挂待、确无 send 方时由 Go runtime panic 兜底；E2E 等价测试只覆盖必有值程序。**select 与 tryRecv 后置**：多路等待/轮询需要真调度模型，等第二个消费方出现再议（#41 纪律） | 非阻塞 recv 使「go 生产者 + 主流程 recv」在 →Go 下是时序竞争（send 前到达即空 Err）——v0 唯一安全模式只剩 join 后 recv；阻塞 recv 让 go-then-recv 成为三路一致的确定性模式，正面消灭 #59 标记的「跨 goroutine 时序 v0 不定」；解释器/→JS 的同步 go 语义使阻塞在这两路天然满足，改动面仅 →Go prelude 一行 |

## 统一改名 lang-go（2026-09-19 追加）

| # | 决定 | 理由 |
|---|------|------|
| 64 | **项目/语言/工具链统一命名 lang-go**（修订 #9 的初名 ling，用户拍板）：go module `ling`→`lang-go`（全部 import 路径随动）、CLI `ling.exe`→`lang-go.exe`（version/REPL 横幅/提示符 `lang-go>`/help 文本同步）、源文件扩展名 `.ling`→`.lang-go`（`*_test.ling` 约定随动为 `*_test.lang-go`）、运行时标识符前缀 `lingXxx`/`LingXxx`→`langGoXxx`/`LangGoXxx`（prelude 函数、LingRes/LingEnum 等类型、JS 标记属性 `__lingStruct`→`__langGoStruct`、防撞守卫 `HasPrefix(name,"Ling")`→`"LangGo"`）、环境变量约定 `LING_BIND_DIR`→`LANG_GO_BIND_DIR`。**输出等价判据不受影响**：stdout 格式（`Ok(...)` 等）不含名字，E2E 逐字节等价全部维持。**已知权衡**：扩展名绑定实现载体名，未来 lang-rs/lang-py/lang-ts 复用语料时面对 `.lang-go` 文件；代码注释内自称用驼峰 `langGo`（与标识符同形），文档/CLI 用连字符 `lang-go` | 用户指令的全面命名一致性；改名纯机械替换（词表预检排除 calling/grilling 等英文误伤词），行为零变化由全量回归锁定 |

## 语法糖策略

最小核 + 按需加：spec 审查时逐条增删，不预埋"以后可能要"的糖。v0 已按此裁掉的：字符串插值、三元运算符、`+=` 复合赋值、多模式 `A | B` 臂、模式守卫、块注释。

## v0.3 Semantic Core（2026-09-20 追加）

| # | 决定 | 理由 |
|---|------|------|
| 65 | **语义层四件套**：internal/semantic（resolver，稳定 SymbolID，值/类型双命名空间）→ internal/types（checker，统一 Type 模型）→ internal/hir（Typed HIR：NodeId/Span/Symbol/Type，脱糖唯一发生地）→ internal/ir（Core IR：局部槽位化 + 迭代协议 + match 分类结论固化） | 三份查名/两份约束检查的重复实现收敛为单点定义；为多宿主实现（TS/Python/Rust）提供宿主无关契约；详见 docs/adr/0001 |
| 66 | **check 走独立前端**（取代 #49）：`lang-go check` = resolver + 类型检查器，诊断带稳定码（E1xxx 词法/解析、E2xxx resolver、E3xxx 类型、E4xxx 模式）；不再依赖 →JS 转译器 | 检查能力寄生在后端里无法独立演进；v0.3 gate 明确要求 check 与 JS generator 解耦 |
| 67 | **转译约束收编**：参数注解必填（E3007）、? 位置/约束（E3005/E3008）、Result 函数 return 形态（E3006，含 Ok/Err 仅限 return）、match 分类与穷尽（E4001-E4003）全部上移到 types；后端删除对应实现 | 逐字重复的五组函数/规则单点化；后端只做平台映射 |
| 68 | **分歧收口**：未定义名统一编译期 E2002（此前 →JS 路径完全不查，spec 声称覆盖属漂移）；嵌套类型声明 E2010（此前 →JS 静默丢弃）；Result 单臂 match E4001（此前 interp 运行时错 / →Go·→JS 语句位置静默 no-op）；enum match 混字面量臂 E4002（从双后端行为） | 静默丢语义/三路分歧不可接受；一律收紧为编译期显式拒绝 + 回归测试 |
| 69 | **checker 保守性铁律**：Any/Unknown/TypeArg 参与即放行；容器与代数类型按元素协变（运行时容器是擦除的）；fn 参数逆协变 + 返回协变 | 现有合法程序零误杀由 TestCorpus零诊断机械锁定（examples + m4） |
| 70 | **interp 保持消费 AST**（v0.3 不迁 IR 执行）：解释器继续作为参考语义；迁移通道 = 共享前端诊断（check 面）；v0.4+ 评估 IR 执行 | IR 执行器是高风险重写，v0.3 gate 只要求迁移通道；差分测试兜底语义一致 |
| 70b | **Go 后端 gt()（生成侧 Go 类型追踪）**：取代旧 infer() 的角色——langGoIndex/langGoSlice/langGoField 等运行时边界为 any、langGoTo 收窄后的局部为目标类型、+ 传播字符串；语义类型与运行时形态分离 | 后端不再做「类型推断」，只做「类型映射 + 动态边界追踪」；这是 IR 契约的关键部分 |

## 语法糖策略

## v0.4 Agent Contract（2026-09-20 追加）

| # | 决定 | 理由 |
|---|------|------|
| 71 | **契约层单一边界**：internal/contract 是内部模型 → 稳定 JSON 的唯一转换层（禁 import gen/genjs/interp，go list -deps 可验）；统一信封 schemaVersion/toolVersion/kind；dump 与 inspect 共用 Node 形态 | Agent 只依赖公开契约；内部 struct 重构不破坏协议 |
| 72 | **列级 Span 落地**：Token.Start/End（rune 偏移）+ Parser prevEnd/prevLine 跟踪（next/accept 双出口）+ 全节点盖 Span + load 逐文件盖章 Span.File（修复多文件归属缺失）；Span 与 Line 并存，后端零改动 | v0.4 协议需要 offset/line/column；Line 字段保留使既有消费方（interp/format/semantic）零风险 |
| 73 | **Effect Metadata 从 IR 推导**：直接效果来自 IR 节点（println/print→IO、http.get→NET、chan 内建→CHANNEL、extern→FFI、go→SPAWN），沿调用图 fixpoint 传播；fnlit 效果归属定义所在函数（保守）；FS/TIME/RANDOM 保留枚举 | 禁止源码字符串搜索；IR 是调用关系真值源；无生产者的效果显式保留 |
| 74 | **dump ir 不含 span**（span 见 dump.hir）：IR 保持精简执行视图；nodeId 由契约层分配 | 避免 IR 元数据膨胀；协议在 v0.4.x 内可加字段 |
| 75 | **诊断定位 v0.4 为行级**（primarySpan.column 恒 1）：列级 span 基础已落地（#72），诊断收紧到列是 v0.5 目标 | 结构即契约（字段已冻结），精度提升为兼容变更 |
| 76 | **版本单一源**：CLI version 复用 contract.ToolVersion | 消除 version 三处漂移（v0.3 漂移 #2 的根治） |

## v0.5 Safe Editing（2026-09-20 追加）

| # | 决定 | 理由 |
|---|------|------|
| 77 | **轻量 Lossless 模型**：syntax.ScanComments（注释 trivia 流）+ 节点 Span（v0.4）+ 源码文本 = 可复原信息全集；不引入 red-green tree | Prompt 02 许可轻量方案；当前规模下完整 lossless tree 成本无对应收益；ADR-0003 |
| 78 | **Formatter 注释插回**：行缓冲渲染；行首注释独立行（保留空行分组）、行尾注释两空格规范分隔追加语句末行、表达式内注释语句边界冲刷（位置后移文本零丢失）、块尾注释原位保留；100% 保留 + 幂等 + 语义等价三契约由 corpus 级测试机械锁定 | 行尾/表达式内/块尾注释丢失是 v0.3 自认限制（spec §9）；Agent 高频修改要求零丢失 |
| 79 | **结构化编辑与 fix --safe**：internal/edit（TextEdit/WorkspaceEdit/sha256 前置校验/重叠拒绝/rune 偏移）+ fix --safe 三修复（重复 import 去重、未使用 import 删除——import 行不计入引用的保守判定、行尾空白/EOF 换行）+ 事务闭环（analyze→plan→precondition→apply→parse→resolve→typecheck，失败即 STOP） | Agent 编辑的唯一载体是结构化编辑；哈希前置防源码漂移误写；三修复均有机械安全性论证 |
| 80 | **装载器 done 键归一化**：根目录文件登记 done 时同时登记带/不带扩展名两键；根文件间冗余互引转为静默去重，装载链内真循环仍报错 | 修复「根文件 + 同目录 import」双载导致的 E2001 误报（fix E2E 暴露的潜在 bug）；根文件自动装载语义下该冗余本无害 |

## v0.6 Semantic Graph（2026-09-20 追加）

| # | 决定 | 理由 |
|---|------|------|
| 81 | **语义图**：internal/graph 以 SymbolID 为节点（function/method/variable/parameter/struct/enum/constructor），边五类（call/references/writes/pattern+type 经 references/depends_on）+ 文件级 imports；call/spawn/method 边来自 HIR（已解析符号 + Span），引用边来自 AST+resolver；impact 按 direct/transitive 分级并附证据，明示保守性（证据集≠完整证明）；效果推导从 contract 迁入 graph（唯一实现） | Agent 的「谁调用/调用谁/改它影响什么」问题全部可机答；纯内存图（Prompt 03 禁 graph DB）；字段/case 以节点 Fields 列表表达，不建伪符号 |

## v0.7 Refactoring（2026-09-20 追加）

| # | 决定 | 理由 |
|---|------|------|
| 82 | **rename symbol 落地（P0）**：基于 SymbolId + resolver 引用表的编辑集构建（定义位 token 扫描 + 引用位 Ident Span），冲突检测保守拒绝（合并程序内其他同名符号）；fn/method/全局/局部/参数支持，struct/enum/ctor/字段显式拒绝列 P1 | 禁止 Regex Rename（Prompt 04）；shadowing 由 SymbolID 天然区分；保守拒绝优于误改 |
| 83 | **organize imports 落地（P0）**：同文件 import 去重 + 未使用删除（保守：文件内非 import 行出现路径最后段名即视为使用）+ 按路径排序；行尾注释随行移动 | 注释随行保留；装载器对 done 文件跳过，删除冗余 import 语义零变化 |
| 84 | **safe remove 落地（P0）**：Semantic Graph 证明零入边才允许删除顶层 fn / 全局变量；有引用即拒绝并输出引用证据（Span 列表）；方法/字段删除列 P1 | 「不确定就拒绝」（Prompt 04）；拒绝时给出引用计数与位置 |

## v0.7 Refactoring（2026-09-20 追加）

| # | 决定 | 理由 |
|---|------|------|
| 85 | **rename symbol（P0）**：基于 SymbolId + resolver 引用集（Ident Span 精确定位）+ 冲突保守拒绝（合并程序内其他同名即拒）。fn/method/全局/局部/参数支持；struct/enum/ctor/字段显式拒绝列 P1 | 禁止 Regex Rename；shadowing 由 SymbolID 天然区分 |
| 86 | **organize imports + safe remove**：import 去重/未使用删除/排序（注释随行）；safe remove 仅顶层 fn/全局变量零入边才删（有引用附证据拒绝） | Prompt 04 P0 三项全覆盖；保守拒绝优于误改 |

## v0.8 Verification（2026-09-20 追加）

| # | 决定 | 理由 |
|---|------|------|
| 87 | **verify 十阶段管线**：parse→resolve→typecheck→hir→ir→effect→compile→execute→differential，任一阶段失败即停；effect 自 IR 推导（graph.Effects 唯一入口，实现序在 ir 后）；HIR/IR validate 为结构完整性（遍历覆盖/产物非空），深度不变量随 v0.9 fuzz 加固 | 语言正确性不再由 Go 实现自己宣布；差分=解释器/→Go/→JS stdout 逐字节一致 |
| 88 | **conformance 套件即跨实现契约**：顶层 conformance/ 独立于实现；manifest 为 YAML 受限子集（超集报错）；run 模式三后端 stdout 逐字节一致+exitCode 0+stderr 空，check 模式诊断 code+severity+line 多重集（message 允许漂移）；runner contract（compile/check/run/inspect/verify）为四实现统一接口 | 共同真值；运行时错误文本与 go/chan 平台差异为文档化例外（conformance/README.md） |

## v0.9 Semantic Integrity（2026-09-20 追加）

| # | 决定 | 理由 |
|---|------|------|
| 89 | **specs/ 唯一真值源 + Rule Catalog**：11 份主题规范 + rules.md（N/D/F 三级，27 诊断码全量映射 Rule ID）；go/docs/spec.md 降为实现侧摘要 | 规则可引用：spec↔diagnostic↔conformance↔agent 四方同源；冲突由 spec+conformance 裁决 |
| 90 | **诊断携带 ruleId**（contract.Diagnostic.RuleID，映射唯一实现 contract/rules.go）；manifest 支持 case 级 ruleIds 与诊断 ruleId 比对 | schemaVersion 1 只加不改；Agent 可从 check --json 追溯到规范条目 |
| 91 | **IR 解释器 + 四路差分**（irinterp 复用 interp 值/Fmt/CallBuiltin）；闭包捕获经 lower 作用域继承显式保留在 IR 中 | IR 语义独立验证；宿主闭包行为不再充当规范（决策 5 的收口） |
| 92 | **Task/Channel 宿主无关模型**（specs/concurrency.md）：同步确定子集=规范保证面，真并行=调度自由度；求值顺序成文 EVAL-*（赋值 target 地址先于右值） | go f() 语法保留不新增关键字；跨实现只锁可观察行为 |

## 错误传播边界（2026-10-10）

`irinterp.Machine.Run` 的输出写入失败沿既有 error 返回边界传播，与 AST 参考解释器一致。`genjs.TranspileJS` 的控制流块复用 `emitBlock` 的错误返回；任一块转译失败即停止，不把残缺 JS 当作成功产物。标准库表驱动回归分别通过源码 → `Run` 和源码 → `TranspileJS` 验证这两个公开边界。
