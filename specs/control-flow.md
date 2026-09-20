# 控制流与模式匹配规范（CTRL / MATCH）

## CTRL-IF-001 条件

条件表达式必须为 bool（非 bool 条件 → E3011）；条件位置禁止裸 struct 字面量（PARSE-006）。else if 链按序求值条件，首个 true 分支执行。

## CTRL-WHILE-002 while

每次迭代前求值条件；条件 false 退出。体可 break/continue。

## CTRL-FORIN-003 for-in 迭代协议

`for x in iter`：迭代对象按类型归类——数组（元素，索引序）、映射（键，插入序）、字符串（单字符字符串，码点序）、区间 `lo..hi`（半开，int，lo≥hi 时空）。其余类型 → **E3010**。循环变量每轮重新绑定；break 跳出、continue 进入下一轮。

## CTRL-RETURN-004 返回

`return` 立即退出当前函数；无值返回 `()`。`return` 在函数外 → **E2008**。Result 函数的 return 须为 `Ok(...)`/`Err(...)` → **E3006**（见 option-result.md）。

## MATCH-KIND-001 match 分类

match 按被匹配值静态类型分四类：字面量 match（int/string/bool）、enum match、Result match、Option match。混用形态（enum 值配字面量臂 → **E4002**；Result/Option/不同 enum 模式混用 → **E4003**）静态拒绝。

## MATCH-EXHAUSTIVE-002 穷尽性

enum match 在**编译期**检查穷尽：存在未被任何臂覆盖的 case 且无通配/绑定臂兜底 → **E4001**（诊断列出缺失 case）。字面量 match 的穷尽性不做静态检查：无臂匹配时运行时错误（RUNTIME-MATCH-001）。

## MATCH-BIND-003 臂绑定与载荷

构造模式按位置解构载荷，子绑定名为新绑定（作用域限本臂）；单一子模式可写裸绑定名捕获载荷。绑定不可遮蔽同名已有绑定之外再声明（臂内新作用域）。

## MATCH-EXPR-004 match 是表达式

臂体表达式的类型须一致（或可统一到 match 的期望类型）；match 可出现在任何表达式位置（含实参、赋值右值、return）。
