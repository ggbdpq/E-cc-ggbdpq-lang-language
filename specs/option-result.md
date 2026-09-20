# Option / Result 规范（RESULT）

## RESULT-PRELUDE-001 预载代数类型

```lang-go
enum Option[T]    { Some(T)  None }
enum Result[T, E] { Ok(T)  Err(E) }
struct error { msg: string }
```

由实现预载，用户无须声明；case 名全局唯一（NAME-CTOR-005）。`err(msg)` 构造 error 值。业务错误统一 `Result[T, error]`。

## RESULT-NULL-002 无 null

语言不存在 null；「无值」只有 `Option[T]`。JSON 解析的 null 映射为 `None`（RUNTIME-JSON-001）。

## RESULT-TRY-003 `?` 传播

`?` 后缀操作数须为 Result → **E3008**；`Ok(v)` 解出 v 继续，`Err(e)` 从当前函数传播出去：在**函数边界**自动合成该函数的 `Err` 返回值（调用方继续 `?`/match）。约束：

- `?` 只能出现在 `let`/`return`/赋值的顶层 → **E3005**（位置非法）
- 所在函数必须注解返回 `Result[T, error]` → **E3005**
- 顶层语句（函数外）使用 `?` → 静态拒绝

## RESULT-RETURN-004 Result 函数 return 形态

注解为 `Result[T, error]` 的函数，`return` 须为 `Ok(...)`/`Err(...)` → **E3006**；`Ok`/`Err` 仅可用于该位置。

## RESULT-CHAN-005 recv 的 Result

`recv(ch)` 返回 `Result[T, error]`：有值为 `Ok(v)`；channel 空（同步语义下无人 send）为 `Err("channel 空")`（CHAN-RECV-003）。

## RESULT-MATCH-006 处理习惯

Result 必须被显式处理（`?` 传播或 match 解构）；语言不提供忽略错误的隐式通道——未处理的 Result 值作为表达式语句丢弃是合法但 discouraged（无警告，v0.9 冻结）。
