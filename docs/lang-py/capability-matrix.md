# lang-go Capability Matrix → lang-py 目标映射

> 生成于 lang-py 开工侦察（Phase A）。真值源优先级：specs/ > conformance/ > go/ 参考实现。
> 状态：TODO / IMPLEMENTED / BLOCKED_SPEC_GAP / VERIFIED。
> VERIFIED = 已实现且通过 conformance 或与 Go 参考输出差分一致。

## 1. 语言核心

| Feature | Spec Rule | Go 位置 | Python 目标模块 | Conformance 覆盖 | 状态 |
|---|---|---|---|---|---|
| 记号/关键字/保留字 | LEX-001..004 | syntax/lex.go | lang_py.syntax.lexer | syntax/* | TODO |
| 整数/浮点/字符串字面量 | LEX-005..007 | syntax/lex.go | lang_py.syntax.lexer | io/println-formatting | TODO |
| 换行续行 | LEX-008 | lex.go isCont/emitNewline | lexer | syntax/* | TODO |
| 程序结构/声明/语句 | PARSE-001..003 | syntax/parse.go | lang_py.syntax.parser | 全部 | TODO |
| 运算符优先级（Go 对齐） | PARSE-004 | parse.go precOf | parser | collections/match | TODO |
| match 语法与模式 | PARSE-005 | parse.go parseMatch/parsePattern | parser | match/* | TODO |
| 控制头禁裸 struct 字面量 | PARSE-006 | parse.go noStruct | parser | syntax/* | TODO |
| 作用域/遮蔽/合并命名空间 | NAME-SCOPE/SHADOW/MERGE | semantic/resolver.go | lang_py.semantic.resolver | module/* | TODO |
| 重复定义/未定义名 | NAME-DUP/UNDEF | resolver.go | resolver | diagnostics/undefined-variable | TODO |
| 构造器唯一性/预载 | NAME-CTOR-005 | resolver.go prelude | resolver | diagnostics/* | TODO |
| 方法接收者/可变性/位置 | NAME-RECV/MUT/PLACE | resolver.go | resolver | diagnostics/* | TODO |
| 类型类别与可赋值性 | TYPE-KINDS/ASSIGN | types/type.go | lang_py.typesys.model | types/* | TODO |
| Any/Unknown 保守放行 | TYPE-ANY-002 | type.go laxAny | model+checker | types/* | TODO |
| 名义 struct/enum | TYPE-STRUCT-003 | type.go Equal(Sym 同一) | model | struct/* enum/* | TODO |
| 函数结构兼容 | TYPE-FN-004 | type.go Assignable(KFn) | model | closure/* | TODO |
| int/float 无隐式转换 | TYPE-ASSIGN-001 | infer.go arithType | checker | diagnostics/int-float-mixed | TODO |
| 注解规则（E3007/E3005） | TYPE-ANN-006 | checker.go fnBody | checker | generics/* | TODO |
| 成员/索引/键检查 | TYPE-MEMBER/INDEX | infer.go fieldType/checkIndexKey | checker | collections/* | TODO |
| 泛型擦除（用户泛型 fn） | GEN-DECL/INFER/SUBST | checker+gen | checker | generics/* | TODO |
| struct 浅拷贝/容器共享 | VALUE-COPY/SHARE | interp/value.go copyValue | lang_py.interp.values | struct/* collections/* | TODO |
| 引用 `&x` | VALUE-REF-003 | interp.go PtrVal | values+interp | types/* | TODO |
| 闭包按引用捕获 | VALUE-CAPTURE-004 | interp.go Env 链 | interp | closure/* | TODO |
| 字符串不可变/码点索引 | VALUE-IMMUT-005 | interp.go | interp | collections/* | TODO |
| 映射插入序 | VALUE-MAP-006 | value.go MapVal | values | collections/map-keys-values | TODO |
| unit | VALUE-UNIT-008 | interp.go UnitVal | values | — | TODO |
| 严格左到右求值 | EVAL-ORDER-001..006 | interp.go | interp | 全部 run case | TODO |
| 赋值地址组件先于右值 | EVAL-ASSIGN-003 | interp.go assign（v0.9 已修） | interp | collections/* | TODO |
| go 语句实参就地求值 | EVAL-GO-007 | parser+interp | interp | concurrency/* | TODO |
| 调用解析静态化 | CALL-RESOLVE/ARG/CLOSURE | interp.go | interp | 全部 | TODO |
| if/while/for-in/break/continue | CTRL-001..003 | interp.go | interp | collections/* | TODO |
| match 四分类+穷尽 | MATCH-001..004 | types/infer.go classifyMatch | checker | match/* diagnostics/* | TODO |
| Option/Result 预载+`?` | RESULT-001..006 | interp prelude+checker inferTry | checker+interp | option/* result/* | TODO |
| 模块根/import/合并/循环 | MODULE-001..007 | load/load.go | lang_py.loader.loader | module/* | TODO |
| Task 同步确定子集 | TASK-001..003 | interp.go GoStmt（同步执行） | interp | concurrency/* | TODO |
| chan 缓冲 1024/send/recv | CHAN-001..005 | interp/stdlib.go | lang_py.interp.stdlib | concurrency/chan-send-recv | TODO |
| 运行时失败分类 | RUNTIME-FAIL-001 | interp.go | interp | —（不进 run 比对） | TODO |
| JSON 确定性 | RUNTIME-JSON-001/002 | interp/stdlib.go | stdlib | io/json-roundtrip | TODO |
| 输出面格式化 | RUNTIME-OUTPUT-003 | interp/value.go fmtVal | values | io/println-formatting | TODO |
| 效果元数据 | EFFECT-001 | graph/effects.go | lang_py.graph.effects | inspect.effects | TODO |
| extern/bind 冻结面 | FFI-001 | interp.go+bind | interp+lang_py.bind | —（不进 conformance） | TODO |

## 2. 工具链 / Agent 面

| Feature | 契约 | Go 位置 | Python 目标模块 | 状态 |
|---|---|---|---|---|
| run（AST 解释器） | runner-contract 五动词 | interp+cli | lang_py.cli | TODO |
| test（*_test.lang-go, test* 函数） | MODULE-TEST-007 | cli runTests | cli | TODO |
| check / check --json | diagnostics.md + agent-protocol.md | pipeline+contract | lang_py.pipeline+contract | TODO |
| fmt（幂等+注释保留） | ADR-0003 | format/format.go | lang_py.formatter | TODO |
| build go/js | out/main.go|main.mjs | cli runBuild | cli | TODO |
| bind | FFI-001 | bind/bind.go | lang_py.bind | TODO |
| dump ast/hir/ir --json | agent-protocol §2 | contract/dump* | lang_py.contract.dump | TODO |
| inspect symbols/refs/type/dependencies/effects/callers/callees/impact/references/dependency | agent-protocol §2 | contract+graph | lang_py.contract+graph | TODO |
| fix --safe（TextEdit/WorkspaceEdit/sha256/闭环） | agent-protocol §6 | edit+fix | lang_py.edit+fix | TODO |
| refactor rename/organize-imports/safe-remove | agent-protocol §7 | refactor | lang_py.refactor | TODO |
| verify（九阶段+三路差分） | runner-contract | conformance/verify.go | lang_py.conformance.verify | TODO |
| conformance 套件 runner | runner-contract + manifest 子集 | conformance/runner.go+manifest.go | lang_py.conformance | TODO |
| REPL | v0.1 | cli repl | cli repl | TODO |
| IR 解释器（v0.9 WIP） | specs README 优先级 | go/internal/irinterp（未提交） | lang_py.irinterp | TODO |
| 诊断 ruleId 映射 | rules.md 27 码 | contract/rules.go | lang_py.contract.rules | TODO |

## 3. 精度与稳定性契约

| 契约点 | 内容 | 状态 |
|---|---|---|
| SourceSpan | rune 偏移 0 基、line/col 1 基、半开；AST/HIR 列级，诊断行级 | TODO |
| 诊断结构 | code/ruleId/severity/message/primarySpan/relatedSpans/help | TODO |
| 符号/节点 ID | 同源码+同版本逐字节稳定；插入式编号 | TODO |
| 输出排序 | refs 按 symbolId→file→offset；effects 固定序；诊断按（行，列，码） | TODO |
| int64 语义 | 解释器/→Go 64 位环绕；→JS BigInt 不环绕（参考实现自身差异，登记） | TODO |
| float 格式化 | Go strconv 'g' 最短表示（大数阈值 1e21，Python repr 为 1e16 → 需自实现） | TODO |
| 字符串 quote | Go strconv.Quote（嵌套字符串） | TODO |
| JSON 序列化 | Go json.Marshal：紧凑、map 键字母序、HTML 转义 <>&、None→null | TODO |
