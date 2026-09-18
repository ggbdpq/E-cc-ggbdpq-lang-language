# ling 语言规范 v0（草案）

状态：**v0 草案，随 M2 解释器实现同步修订**。语法糖按"最小核 + 按需加"策略起草（见 decisions.md 末节），审查时逐条增删。

ling 是一门覆盖前端、后端与日常工具开发的个人语言：源码可转译为 Go（后端/系统侧）与 JS（前端侧），GC 与标准库能力委托给目标平台。本文定义 v0 的词法、语法与语义。

---

## 1. 设计基线（摘自 decisions.md）

| 维度 | 决定 |
|------|------|
| 类型 | 静态强类型 + 推断 + 结构化子类型；v0 解释器**不做静态检查**，注解为文档性且**可省略**，检查器随 M3 落地时收紧 |
| 错误 | `Result[T, E]` 值 + `?` 传播 + `match` 处理；**无异常** |
| null | **不存在**；可选值只有 `Option[T]` |
| 内存 | GC 由宿主承担（→Go 用 Go GC，→JS 用引擎 GC）；struct 值语义，slice/map 引用语义 |
| 并发 | goroutine 语义预留；v0 解释器顺序执行，`go`/`chan` 为保留字 |
| 元编程 | 无宏、无反射 |
| 模块 | 目录=包，`ling.toml` 标模块根；v0 解释器单文件运行 |

## 2. 词法

- 源文件为 UTF-8 文本，扩展名 `.ling`。
- 注释：`//` 行注释，无块注释（v0）。
- 标识符：Unicode 字母或 `_` 开头，后接字母/数字/`_`；单独的 `_` 是通配符记号。
- 关键字（15 个）：`let var fn return if else while for in match struct enum true false break continue`。
- 保留字（5 个，v0 使用即报错"尚未启用"）：`go chan import extern interface`。
- 整数字面量：十进制数字串，类型 `int`（64 位）。浮点字面量：含 `.` 的数字串（`.` 后须为数字，故 `1..3` 是两个整数夹区间），类型 `float`（64 位）。无科学计数法（v0）。
- 字符串字面量：双引号，转义支持 `\n \t \r \" \\`；不可变；按 Unicode 码点索引/切片，`len()` 返回码点数。
- 无分号。**换行即语句结束**；仅当上一记号为运算符、逗号、冒号、`=>`、`..`、`.` 或开括号 `(` `[` `{` 时，该换行被忽略（续行）。因此多行字面量与多行实参表须在行尾留逗号。match 分支之间靠换行分隔，且 match 可作实参——其分支的换行同样存活（v0.1 实现时废弃了"括号深度内压制换行"的初版规则，因为它会吞掉实参位置 match 的分支分隔）。

## 3. 语法

程序 = 顶层声明的序列：`fn`、`struct`、`enum`、`let`/`var`（全局值）。入口为无参 `fn main()`；`ling run` 固定运行**当前目录**的 `main.ling`（v0.1 CLI 不收路径参数，见 §5），执行完顶层声明后调用 main，无 main 则按脚本顺序执行。参数与返回值的类型注解均可省略。

### 3.1 声明

```ling
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
| 赋值 | `x = e`、`p.x = e`、`xs[i] = e`、`m[k] = v` | 仅 `=`；字段与容器内容总是可变（与绑定可变性解耦，审查项） |
| 条件 | `if c { } else if c2 { } else { }` | 条件位置禁止裸 struct 字面量（见 3.4） |
| 循环 | `while c { }`；`for x in 迭代器 { }` | 支持 `break`/`continue` |
| 返回 | `return` / `return e` | 无值返回得到 `()`（unit） |
| 表达式语句 | `f(x)` | 求值后丢弃 |

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

位运算语义与 Go 的 int64 位运算一致（二进制补码）；优先级亦对齐 Go（`<< >> &` 与 `*` 同级，`\| ^` 与 `+` 同级）。无 uint32 类型，32 位回绕算法用「运算后 `& 4294967295` 掩码」表达（见 m4/md5.ling）。

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

```ling
let area = match s {
    Circle(r) => 3.14 * r * r
    Rect(w, h) => w * h
    Unit => 0.0
}
```

### 3.6 代数类型（内建预载）

```ling
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
| `isNone` | `(any) → bool` | 判 `None`（JSON null 的 ling 表示） |
| `http.get` | `(url: string, headers: map[string]string) → Result[HttpResp, error]` | 网络错误=Err；任何响应（含 4xx/5xx）=Ok。`HttpResp{status: int, body: string}` 预载 |
| `assert` | `(cond, msg?)` | 失败即运行时错误，测试用 |
| `err` | `(msg: string) → error` | 构造 error |

标准库委托（decisions #18/#37）：解释器侧直接用 Go stdlib 实现，→Go 侧由 prelude 提供行为一致的同名实现；JSON 对象键序、`jsonStringify` 输出两端逐字节一致（E2E 锁定）。`extern "go" { fn 签名 }` 声明宿主函数：解释器侧调用即报"仅转译可用"，→Go 侧生成 panic 桩（链接由 prelude/人工提供）。

M2b 起：内建映射到后端宿主（`http`、`json`、`time` 等，见 decisions.md #18）。extern 声明块随 M3。

## 5. 模块与测试约定

- **模块根** = 放 `ling.toml` 的目录；该文件 v0 仅作标记，无构建配置。
- **导入**：`import "./x"`（仅同模块相对路径；目标是 `x.ling` 文件或 `x/` 目录）。装载器在合并前递归展开：目录即包 + import 图合并为一个程序；循环导入报错，菱形导入去重，越出模块根目录报错。`ling.toml` 仍免（v0 以 cwd 为模块根）。
- **CLI（v0.1）**：`ling run` 与 `ling test` 均按**目录即包**装载——当前目录全部非测试 `.ling` 按文件名序合并为一个程序，`ling test` 再把 `*_test.ling` 追加在后并依序执行所有 `test` 前缀无参函数，任一断言失败则退出码 1。CLI 不接收路径参数（安全边界：文件输入无 `..` 穿越面）。普通 `ling run` 不执行 `*_test.ling`。

## 6. 平台差异与 →Go 映射（M3 起生效）

### 6.1 →Go 后端映射表

| ling 概念 | Go 产物 |
|-----------|---------|
| `int` / `float` | `int64` / `float64`（不隐式转换，与 ling 一致） |
| struct | 原生 Go struct（值语义白拿）+ `lingString` 方法供 println 格式化 |
| 方法 | 原生 Go 方法（值接收者） |
| 用户 enum | `LingEnum{Tag string, Vals []any}`（载荷装箱），构造器 `le_<enum>_<case>` |
| `Option[T]` / `Result[T, error]` | 泛型 `LingOpt[T]` / `LingRes[T]`（第二参数仅支持 error） |
| 数组 `[T]` | `*LingArr[T]`（引用语义、push 原位可见，Go append 语义不满足故包一层） |
| 映射 `map[K]V` | `*LingMap`（插入序保持，镜像解释器语义） |
| 闭包 / 函数值 | 原生 Go 闭包 / `func` 值 |
| `?` 传播 | `if !r.ok { return LingResErr[T](r.e) }`（编译期展开） |
| match（表达式位置） | 立即调用的函数字面量 |
| 用户名 | 函数加 `lf_` 前缀、enum 构造器加 `le_` 前缀，防撞 Go 关键字与运行时名 |

### 6.2 →Go 转译约束（违反即编译期报错）

1. 顶层函数、方法、函数字面量的**参数必须有类型注解**（解释器无此要求）。
2. `?` 只能出现在 `let`/`return`/赋值的**顶层**；带 `?` 的函数必须注解为 `Result[T, error]`。
3. `Result` 函数的 `return` 须为 `Ok(...)` / `Err(...)`。
4. enum 的 match 只支持构造/通配模式；字面量模式的 match 只支持非 enum 值。
5. **match 穷尽性在编译期检查**（解释器侧仍是运行时检查——这是静态检查器的第一块落地）。

### 6.3 并行度与标准库委托

| 差异点 | →Go 后端 | →JS 后端（未实现） |
|--------|----------|----------|
| 并行度 | 真 goroutine 并行 | 单线程协作式：`go`→微任务、channel→队列 |
| GC | Go 运行时 | JS 引擎 |
| 标准库委托 | Go stdlib | 内置对象 + fetch |
| 整数 | int64 | number（安全整数范围内一致） |

### 6.4 验收方式

→Go 后端的判据是**输出等价**：同一 ling 程序，解释器执行与转译后 Go 编译执行的 stdout 逐字节一致（`internal/gen` 的 E2E 测试机械化此判定，当前覆盖 examples 全部四个程序）。

## 7. 明确不做（v1）

null、异常、宏、继承、隐式数值转换、包管理器、LSP、调试器、LLVM、指针算术、.d.ts 自动摄入。

## 8. 里程碑

- **M1** 本规范 + 决策台账（已完成）
- **M2** Go 实现：词法器、解析器、树遍历解释器、REPL、`ling run`/`ling test`（本轮交付）
- **M3** →Go、→JS 双转译后端 + 静态类型检查器 + import
- **M4** 移植 linkpull bilibili 提取器核心（`video/wbi/md5/list`），测试全绿即验收
