# lang-go 实现侧摘要

> **真值声明（v0.9 起）**：语言语义的唯一真值源是仓库根 `specs/`（12 份主题规范 +
> rules.md Rule Catalog）。本文降级为实现侧摘要——管线架构、后端映射、工具命令、
> 版本历史。语义以 specs/ 为准；两者冲突时修本文。
>
> 状态：随实现同步修订。语法糖按"最小核 + 按需加"策略（见 decisions.md 末节）。

lang-go 是一门覆盖前端、后端与日常工具开发的个人语言：源码可转译为 Go（后端/系统侧）与 JS（前端侧），GC 与标准库能力委托给目标平台。本文定义 v0 的词法、语法与语义。

---

## 1. 设计基线（摘自 decisions.md）

| 维度 | 决定 |
|------|------|
| 类型 | 静态强类型 + 推断 + 结构化子类型；v0 解释器**不做静态检查**，注解为文档性且**可省略**，检查器随 M3 落地时收紧 |
| 错误 | `Result[T, E]` 值 + `?` 传播 + `match` 处理；**无异常** |
| null | **不存在**；可选值只有 `Option[T]` |
| 内存 | GC 由宿主承担（→Go 用 Go GC，→JS 用引擎 GC）；struct 值语义，slice/map 引用语义 |
| 并发 | `go` 语句 + `chan[T]`（v0.2 已交付，decisions #57/#59/#63）；解释器/→JS 为顺序确定的同步子集，→Go 真 goroutine（平台差异见 §6.4） |
| 元编程 | 无宏、无反射 |
| 模块 | 目录=包，`lang-go.toml` 标模块根；v0 解释器单文件运行 |

## 2. 词法

- 源文件为 UTF-8 文本，扩展名 `.lang-go`。
- 注释：`//` 行注释，无块注释（v0）。
- 标识符：Unicode 字母或 `_` 开头，后接字母/数字/`_`；单独的 `_` 是通配符记号。
- 关键字（15 个）：`let var fn return if else while for in match struct enum true false break continue`。
- 保留字（5 个）：`go chan import extern interface`。go/chan/import/extern 已启用（§3.2、§4、§5）；`interface` v0 使用即报错"尚未启用"。
- 整数字面量：十进制或十六进制 `0x`/`0X` 前缀（如 `0xFF`），类型 `int`（64 位）。浮点字面量：含 `.` 的数字串（`.` 后须为数字，故 `1..3` 是两个整数夹区间），类型 `float`（64 位）。无科学计数法（v0）。fmt 统一输出十进制。
- 字符串字面量：双引号，转义支持 `\n \t \r \" \\`；不可变；按 Unicode 码点索引/切片，`len()` 返回码点数。
- 无分号。**换行即语句结束**；仅当上一记号为运算符、逗号、冒号、`=>`、`..`、`.` 或开括号 `(` `[` `{` 时，该换行被忽略（续行）。因此多行字面量与多行实参表须在行尾留逗号。match 分支之间靠换行分隔，且 match 可作实参——其分支的换行同样存活（v0.1 实现时废弃了"括号深度内压制换行"的初版规则，因为它会吞掉实参位置 match 的分支分隔）。

## 3. 语法

程序 = 顶层声明的序列：`fn`、`struct`、`enum`、`let`/`var`（全局值）。入口为无参 `fn main()`；`lang-go run` 固定运行**当前目录**的 `main.lang-go`（v0.1 CLI 不收路径参数，见 §5），执行完顶层声明后调用 main，无 main 则按脚本顺序执行。参数与返回值的类型注解均可省略。

### 3.1 声明

```lang-go
fn add(a: int, b: int): int {
    return a + b
}

struct Point {
    x: int
    y: int
}

enum Shape {
    Circle(float, float)   // 载荷按位置声明
    Rect(float, float)
    Unit
}

// 泛型：类型参数跟在名字后，v0 解释器擦除处理
fn first[T](xs: [T]): Option[T] {
    if len(xs) == 0 {
        return None
    }
    return Some(xs[0])
}

// 方法：值接收者；接收者无冒号，Go 风格
fn (r Rect).area(): float {
    return r.w * r.h   // 示意：字段访问
}
```

### 3.2 语句

| 语句 | 形式 | 备注 |
|------|------|------|
| 绑定 | `let x = 表达式` / `let x: int = 5` | **不可重绑** |
| 可变绑定 | `var x = 0` | 可重绑 |
| 赋值 | `x = e`、`p.x = e`、`xs[i] = e`、`m[k] = v` | 字段与容器内容总是可变（与绑定可变性解耦，审查项） |
| 复合赋值 | `+=` `-=` `*=` `/=`（目标为变量/字段/索引） | `+=` 对数组是原位拼接（push 语义，别名可见）；let 绑定同样不可复合赋值（decisions #52） |
| 条件 | `if c { } else if c2 { } else { }` | 条件位置禁止裸 struct 字面量（见 3.4） |
| 循环 | `while c { }`；`for x in 迭代器 { }` | 支持 `break`/`continue` |
| 返回 | `return` / `return e` | 无值返回得到 `()`（unit） |
| 表达式语句 | `f(x)` | 求值后丢弃 |
| 并发 | `go <具名函数调用>` | fire-and-forget 后台任务（decisions #57）；→Go 真 goroutine + main 尾部 join，解释器/→JS 在语句处同步执行。实参在 go 语句处求值 |

`for` 迭代器：数组（元素）、映射（键）、字符串（单字符字符串）、区间 `lo..hi`（半开，int）。

### 3.3 表达式与优先级（低 → 高）

| 级 | 运算符 | 说明 |
|----|--------|------|
| 1 | `\|\|` | 短路 |
| 2 | `&&` | 短路 |
| 3 | `==` `!=` | 标量与 enum 可比；数组/映射/struct 不可比（运行时报错） |
| 4 | `<` `<=` `>` `>=` | 仅同类型数值/字符串 |
| 5 | `..` | 区间，半开 |
| 6 | `+` `-` `\|` `^` | 数值相加；`string + string` 拼接；`\|` `^` 为位运算（int） |
| 7 | `*` `/` `%` `&` `<<` `>>` | 除零/模零为运行时错误；`&` `<<` `>>` 为位运算（int） |
| 8 | 一元 `-` `!` `&` | `&` 仅可作用于变量（取引用）；前缀位置取引用、中缀位置位与，二者不冲突 |
| 9 | 后缀 `(` `)` `[` `]` `.` `?` | 调用、索引/切片 `a[i:j]`、字段、传播 |

位运算语义与 Go 的 int64 位运算一致（二进制补码）。优先级以 specs/grammar.md 表为准（`<< >> &` 高于 `+`——自有定义，非 Go 对齐）。无 uint32 类型，32 位回绕算法用「运算后 `& 4294967295` 掩码」表达（见 m4/md5.lang-go）。

**类型规则（v0 运行时强制）**：`int` 与 `float` **不隐式转换**，混用算术/比较即运行时错误，须显式 `int()`/`float()`。

### 3.4 struct 字面量与控制头

`Point{x: 1, y: 2}` 按字段名构造；`{"a": 1}` 是映射字面量（表达式位置）；`[1, 2]` 是数组字面量。为消除 Rust 式歧义：**if/while/match 头部与 for 迭代器位置禁止裸 struct 字面量**，加括号即可（`if (Point{x:1}).x > 0` 合法）。

### 3.5 match 与模式

`match` 是**表达式**。每臂 `模式 => 表达式`，臂间换行分隔。

| 模式 | 形式 | 说明 |
|------|------|------|
| 通配 | `_` | 匹配一切 |
| 字面量 | `1` `"x"` `true` | 用 `==` 语义比较 |
| 绑定 | 小写标识符 | 捕获整个被匹配值 |
| 构造 | 大写标识符，可带 `(子模式, ...)` | 对应 enum case；载荷按位置解构 |

**穷尽性**：被匹配值为 enum 且存在未被任何臂覆盖的 case（且无通配/绑定臂兜底）时，报运行时错误并列出缺失 case。M3 该检查前移至编译期。

```lang-go
let area = match s {
    Circle(r) => 3.14 * r * r
    Rect(w, h) => w * h
    Unit => 0.0
}
```

### 3.6 代数类型（内建预载）

```lang-go
// 由解释器预载，用户无需声明
enum Option[T]  { Some(T)  None }
enum Result[T, E] { Ok(T)  Err(E) }
struct error { msg: string }
```

- `err("boom")` 构造 `error` 值；`Ok(v)`/`Err(e)`/`Some(v)`/`None` 是全局构造器，case 名全局唯一，用户 enum 与其重名即报错。
- `?` 后缀：操作数须为 `Result`。`Ok(v)` 解出 `v`；`Err(e)` 把 `e` 从当前函数**传播出去**。v0 解释器语义：Err 传播到**函数边界**时自动合成该函数的 `Err` 返回值（与 Result 语言的通行语义一致，调用方继续 `?`/`match`）；在**顶层语句**（任何函数之外）使用 `?` 则运行时报错。M3 静态检查器将要求带 `?` 的函数声明返回 `Result`。
- 习惯用法：业务错误统一 `Result[T, error]`。

### 3.7 值语义

- struct 在**绑定、传参、返回**时整体拷贝（嵌套 struct 递归拷贝；字段中的 slice/map/指针共享）。
- `&x` 取变量引用；通过引用读写字段/索引即读写原值。无指针算术。
- slice（`[T]`）、map（`map[K]V`）、string 为引用/共享语义；map 迭代按插入序。

### 3.8 闭包与函数值

`fn` 出现在表达式位置即匿名函数，捕获定义处环境（按引用）。函数是值，可赋给绑定、存入 struct 字段、作参数传递。

## 4. 内建函数（可移植核心 v0）

| 函数 | 签名（示意） | 说明 |
|------|--------------|------|
| `println` / `print` | `(...any)` | 求值并空格分隔输出；前者带换行 |
| `len` | `(string\|[T]\|map[K]V\|区间) → int` | 字符串按码点 |
| `str` / `int` / `float` | `(x) → ...` | 显式转换；`int("4x")` 报错，不做隐式转换的出口 |
| `keys` / `values` / `has` | `(map, ...)` | `has(m, k)` 探测键 |
| `push` | `(arr, v...) → arr` | 原位追加（引用语义） |
| `bytes` | `(string) → [int]` | UTF-8 字节序列（MD5/URL 编码用） |
| `char` | `(int) → string` | 码点转单字符字符串 |
| `jsonParse` | `(string) → any` | 动态 JSON 值；数字按整/浮分型，null → `None`，对象 → 映射（键排序），数组 → 数组 |
| `jsonStringify` | `(any) → string` | 标量/数组/映射/None→null；映射键按字母序输出（两端确定） |
| `isNone` | `(any) → bool` | 判 `None`（JSON null 的 lang-go 表示） |
| `http.get` | `(url: string, headers: map[string]string) → Result[HttpResp, error]` | 网络错误=Err；任何响应（含 4xx/5xx）=Ok。`HttpResp{status: int, body: string}` 预载 |
| `makeChan` / `send` / `recv` | `()` / `(ch, v)` / `(ch) → Result[T, error]` | chan 最小面（decisions #59/#63）。`chan[T]` 类型注解（→Go 装箱 `chan any`，T 仅文档）；send 不阻塞（缓冲 1024，满则运行时错）；**recv 阻塞直到有值**——解释器/→JS 的 go 同步执行语义下 recv 时要么有值、要么永远无人 send（死锁程序错误 → Err"channel 空"），→Go 原生阻塞、确无 send 方时 Go runtime panic 兜底 |
| `assert` | `(cond, msg?)` | 失败即运行时错误，测试用 |
| `err` | `(msg: string) → error` | 构造 error |

标准库委托（decisions #18/#37）：解释器侧直接用 Go stdlib 实现，→Go 侧由 prelude 提供行为一致的同名实现；JSON 对象键序、`jsonStringify` 输出两端逐字节一致（E2E 锁定）。`extern "go" { fn 签名 }` 声明宿主函数：解释器侧调用即报"仅转译可用"，→Go 侧生成 panic 桩（链接由 prelude/人工提供）。

M2b 起：内建映射到后端宿主（`http`、`json`、`time` 等，见 decisions.md #18）。extern 声明块随 M3。

## 5. 模块与测试约定

- **模块根** = 放 `lang-go.toml` 的目录；该文件 v0 仅作标记，无构建配置。
- **导入**：`import "./x"`（仅同模块相对路径；目标是 `x.lang-go` 文件或 `x/` 目录）。装载器在合并前递归展开：目录即包 + import 图合并为一个程序；循环导入报错，菱形导入去重，越出模块根目录报错。`lang-go.toml` 仍免（v0 以 cwd 为模块根）。
- **CLI（v0.1）**：`lang-go run` 与 `lang-go test` 均按**目录即包**装载——当前目录全部非测试 `.lang-go` 按文件名序合并为一个程序，`lang-go test` 再把 `*_test.lang-go` 追加在后并依序执行所有 `test` 前缀无参函数，任一断言失败则退出码 1。CLI 不接收路径参数（安全边界：文件输入无 `..` 穿越面）。普通 `lang-go run` 不执行 `*_test.lang-go`。
- **静态检查**：`lang-go check` 走独立语义前端（v0.3，decisions #66 取代 #49）：resolver（名字解析）→ 类型检查器。诊断带稳定码：
  - `E1xxx` 词法/解析（如 E1001 解析错误）
  - `E2xxx` 名字解析：E2001 重复定义、E2002 未定义名、E2003 构造器重名、E2004 方法接收者未知 struct、E2005 对不可变绑定赋值、E2006 case 与内建名冲突、E2007 break/continue 在循环外、E2008 return 在函数外、E2010 嵌套类型声明
  - `E3xxx` 类型系统：E3001 类型不匹配、E3002 int/float 无隐式转换、E3003 调用参数个数、E3004 字段/方法不存在、E3005 `?` 位置/注解约束、E3006 Result 函数 return 形态、E3007 参数缺注解、E3008 `?` 操作数须 Result、E3009 Result 第二参数须 error、E3010 不可迭代、E3011 一元/逻辑运算不适用、E3012 `&` 仅限变量、E3013 索引须 int、E3014 映射键类型
  - `E4xxx` 模式匹配：E4001 match 不穷尽、E4002 enum/代数类型混字面量臂、E4003 模式混用

  诊断码是稳定协议：只能追加，不能改义（v0.4 JSON 协议依赖）。
  `lang-go build` 先检查后转译；转译目标只做平台映射（后端不再做名字解析、
  类型推断或语义校验）。顶层仅允许声明（fn/struct/enum/let/extern/import），
  脚本式顶层语句仅解释器可执行（转译目标会显式拒绝，见 ADR-0001）。

## 6. 平台差异与 →Go 映射（M3 起生效）

### 6.1 →Go 后端映射表

| lang-go 概念 | Go 产物 |
|-----------|---------|
| `int` / `float` | `int64` / `float64`（不隐式转换，与 lang-go 一致） |
| struct | 原生 Go struct（值语义白拿）+ `langGoString` 方法供 println 格式化 |
| 方法 | 原生 Go 方法（值接收者） |
| 用户 enum | `LangGoEnum{Tag string, Vals []any}`（载荷装箱），构造器 `le_<enum>_<case>` |
| `Option[T]` / `Result[T, error]` | 泛型 `LangGoOpt[T]` / `LangGoRes[T]`（第二参数仅支持 error） |
| 数组 `[T]` | `*LangGoArr[T]`（引用语义、push 原位可见，Go append 语义不满足故包一层） |
| 映射 `map[K]V` | `*LangGoMap`（插入序保持，镜像解释器语义） |
| 闭包 / 函数值 | 原生 Go 闭包 / `func` 值 |
| `?` 传播 | `if !r.ok { return LangGoResErr[T](r.e) }`（编译期展开） |
| match（表达式位置） | 立即调用的函数字面量 |
| 用户名 | 函数加 `lf_` 前缀、enum 构造器加 `le_` 前缀，防撞 Go 关键字与运行时名 |

### 6.2 →Go 转译约束（违反即编译期报错）

1. 顶层函数、方法、函数字面量的**参数必须有类型注解**（解释器无此要求）。
2. `?` 只能出现在 `let`/`return`/赋值的**顶层**；带 `?` 的函数必须注解为 `Result[T, error]`。
3. `Result` 函数的 `return` 须为 `Ok(...)` / `Err(...)`。
4. enum 的 match 只支持构造/通配模式；字面量模式的 match 只支持非 enum 值。
5. **match 穷尽性在编译期检查**（解释器侧仍是运行时检查——这是静态检查器的第一块落地）。

### 6.3 →JS 后端映射（v0.2 起生效）

| lang-go 概念 | JS 产物 |
|-----------|---------|
| `int` | **BigInt**（64 位语义精确，位运算/掩码惯用法与解释器一致——JS 原生位运算是 32 位，不能用 number） |
| `float` | number |
| 数组 / 映射 | Array / Map（保插入序，语义契合） |
| struct | plain object（带 `__langGoStruct` 名）；赋值/传参/返回处 `langGoCopy` 值拷贝（数组/映射共享） |
| enum / Option / Result / error | 带标记属性的对象（`__langGoEnum` 等） |
| `&x` 取引用 | 直接生成 `x`（JS 对象赋值即共享，语义天然对齐） |
| 方法调用 | `lf_<Struct>_<name>(obj, ...)`（方法名须全局唯一） |
| async | 全部函数生成 async（http 委托是异步；同步调用 await 无害） |
| `==` / 排序 | `langGoEq` / 原生比较（`==` 不能用 JS 原生——BigInt 与 number 不等价） |

验收方式同 →Go：解释器输出 vs Node 执行产物输出逐字节一致（examples 五程序 + m4 全家桶对齐 gofixture）。

### 6.4 并行度与标准库委托

| 差异点 | →Go 后端 | →JS 后端 |
|--------|----------|----------|
| 并行度 | 真 goroutine 并行（`go` 语句 + WaitGroup join） | 全 async 单线程；`go` 同步执行（顺序确定子集） |
| chan | 原生 `chan any`（缓冲 1024，满则运行时错）；recv 原生阻塞，死锁由 Go runtime panic 兜底 | 队列模拟（send 不阻塞；go 同步语义下 recv 时要么有值要么死锁 Err，与解释器一致） |
| GC | Go 运行时 | JS 引擎 |
| 标准库委托 | Go stdlib | 内置对象 + fetch（Node 18+） |
| 整数 | int64 | BigInt；JSON 解析数字为 number（>2^53 精度丢失，文档化差异） |
| 浮点打印 | Go `'g'` | JS toString（极端指数场景存在差异，等价测试避开） |

### 6.5 验收方式

两个后端的判据同为**输出等价**：同一 lang-go 程序，解释器执行与转译产物执行（Go 编译 / Node）的 stdout 逐字节一致（`internal/gen` 与 `internal/genjs` 的 E2E 测试机械化此判定，覆盖 examples 全部程序与 m4 移植全家桶）。

## 7. 明确不做（v1）

null、异常、宏、继承、隐式数值转换、包管理器、LSP、调试器、LLVM、指针算术、.d.ts 自动摄入。

## 8. 工具：lang-go fmt（v0.2）

`lang-go fmt` 就地格式化当前目录全部 `.lang-go`（不递归），打印改动的文件名。规则：4 空格缩进、运算符两侧空格、match 臂缩进一级、`else if` 链扁平化、整行注释保留并按缩进对齐、浮点字面量保持小数点（`1.0` 不塌缩为 `1`）。两个硬契约由测试锁定：**幂等**（连跑两次结果一致，语料为仓内全部 .lang-go）与**语义等价**（格式化前后解释器输出逐字节一致）。限制：行尾注释与 match 臂内注释不保留（注释位置移动，不影响语义）。

错误信息约定：解析/词法错误附出错行摘录（`  | 源码行`）；EOF 处报错的行号为输入末尾之后，摘录回退到最后非空行。

## 9. 实现架构（v0.3）

```text
Source
 ↓ Lexer / Parser（AST：保留用户写法，fmt/REPL/解释器消费）
 ↓ semantic/resolver（符号表 + 引用绑定，稳定 SymbolID）
 ↓ types/checker（统一类型模型 + 语义校验，稳定诊断码）
 ↓ hir（Typed HIR：NodeId/行号/Symbol/Type；脱糖唯一发生地）
 ↓ ir（Core IR：后端契约，局部槽位 + 迭代协议 + match 分类结论）
 ├─ interp（树遍历解释器，v0.3 仍消费 AST＝参考语义）
 ├─ gen   （IR → Go，只做映射与 any 边界追踪）
 └─ genjs （IR → JS，只做映射）
```

规范/语义决策见 docs/decisions.md（#65 起）与仓库根 docs/adr/0001；
语料判据：同一程序三路 stdout 逐字节一致（examples + m4 全量差分）。

## 10. Agent Contract（v0.4）

机器接口统一 JSON 信封（schemaVersion/toolVersion/kind），实现于 internal/contract：
`check/inspect symbols·refs·type·dependencies·effects/dump ast·hir·ir --json`。
诊断带稳定码（§5）与行级 primarySpan；列级 span 见 dump.ast/dump.hir；
Effect Metadata（PURE/IO/FS/NET/TIME/RANDOM/SPAWN/CHANNEL/FFI）从 IR 推导。
协议规范：仓库根 specs/agent-protocol.md + schemas/agent-protocol.schema.json。

## 11. Safe Editing（v0.5）

Formatter 硬契约升级为三条（测试机械锁定，语料 = 仓库全部 .lang-go + 对抗样本）：
**注释 100% 保留**（行首/行尾/表达式内/块尾全形状；行尾两空格规范分隔、
表达式内位置后移文本零丢失）、**幂等**、**语义等价**。
结构化编辑：TextEdit/WorkspaceEdit（sha256 前置校验 + rune 偏移 + 重叠拒绝）。
`lang-go fix --safe [--dry-run] [--json]`：重复 import 去重、未使用 import 删除
（保守判定）、行尾空白/EOF 换行规范化，编辑事务失败即停。见 ADR-0003。

## 12. Semantic Graph（v0.6）

内存语义图（internal/graph）：节点 = 符号（function/method/variable/parameter/
struct/enum/constructor，附 name/module/span/type/effects/fields）；边 =
call（HIR 已解析方法符号）/ references（read，AST+resolver）/ writes（赋值目标）/
spawn / type（StructLit）/ depends_on·imports（文件级）。查询：
callers/callees（--depth BFS）/ references（按符号过滤）/ dependency（文件级）/
impact（direct + transitive + effectWatch + modules，附证据，保守估计）。
真值源为 semantic/types/HIR/IR，禁止源码字符串搜索。

## 13. Verification（v0.8）

`lang-go verify [--json]`：对当前目录程序走全管线——parse → resolve → typecheck
→ hir（结构完整性）→ ir → effect（自 IR 推导）→ compile（→Go 可编译 + →JS 可生成）
→ execute（四路执行）→ differential（stdout 逐字节一致）。任一阶段失败即停，
结构化输出阶段明细，退出码 1。

一致性套件在仓库顶层 `conformance/`（独立于实现）：v0.9 起 50 case 携带 ruleIds
（specs/rules.md），四路差分 = AST 参考解释器 / IR 解释器（internal/irinterp，
v0.9 新增）/ →Go / →JS；run 模式断言四方 stdout 逐字节一致 + exitCode 0 + stderr
空，check 模式断言诊断 code+ruleId+severity+primarySpan.line 多重集（message 允许
漂移）。语义真值源自 v0.9 起为仓库根 specs/（唯一真值，ADR-0004）。runner contract（compile/check/run/inspect/verify 五动词）
见 specs/runner-contract.md——四套实现（Go/TS/Python/Rust）的共同验收标准：
任何实现跑完同一套件全绿即视为行为一致。

## 14. 里程碑

- **M1** 本规范 + 决策台账（已完成）
- **M2** Go 实现：词法器、解析器、树遍历解释器、REPL、`lang-go run`/`lang-go test`（已完成）
- **M3** →Go、→JS 双转译后端 + 静态类型检查器 + import（已完成）
- **M4** 移植 linkpull bilibili 提取器核心（已完成，v0.2.0）
- **v0.3** Semantic Core：resolver/类型检查器/HIR/IR + check 独立前端（已完成）
