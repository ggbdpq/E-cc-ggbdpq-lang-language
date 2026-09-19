# specs —— 语言无关规范

跨实现共享的语义规范。真值源目前是 `../go/docs/spec.md` 与 `../go/docs/decisions.md`（70 条决策台账）；
某主题在三方实现（go/python/rust…）需要共同遵守时，把相应章节抽离沉淀到这里，实现侧只留引用。

v0.3 起已宿主无关化的部分：诊断码分段（E1xxx/E2xxx/E3xxx/E4xxx，见 `../go/docs/spec.md` §5）、
Core IR 后端契约（见 `../go/internal/ir/`——多宿主实现只需实现同一 IR 契约）、
架构决策 `../docs/adr/0001-semantic-core.md`。

## 待沉淀主题（骨架，按需充实，不做预填）

- Lexer（词法：记号、字面量、换行即语句结束、续行规则）
- Token（记号分类与关键字/保留字表）
- Parser（文法、优先级表、match 臂换行约定）
- AST（节点清单与位置信息）
- Interpreter（求值规则、值语义、闭包捕获）
- Type System（注解、推断边界、chan[T] 装箱、Result/Option）
- Error Model（Result + `?` 传播、运行时错误分类、死锁行为）
- Runtime（go 语句与 chan 阻塞语义、三路平台差异矩阵）

规则：每个主题一份文档；规范变更先改 `../go/docs/decisions.md` 留痕，再同步到这里。
