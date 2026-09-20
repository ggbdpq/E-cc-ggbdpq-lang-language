# Semantic Rule Catalog

lang-go 的稳定规则编号总表。Rule ID 是跨实现契约的一部分：spec 引用它陈述语义、
诊断 JSON 携带它（`ruleId`）、Conformance manifest 声明它验证哪条规则、Agent 经
check --json 获取它。**Rule ID 一经发布不可改义；废弃保留编号**。

规则分三级：
- **N**（Normative）规范强制，违反即诊断或运行时错误；
- **D**（Defined）已定义行为（如求值顺序），实现必须一致；
- **F**（Frozen）v0.9 冻结面（不扩展、不收窄），变更须经 ADR。

## 诊断码 ↔ Rule 映射（27 码全量）

| 诊断码 | Rule ID | 规则 | 主题文件 |
|--------|---------|------|----------|
| E1001 | LEX-ALL | 词法/解析错误（含未启用保留字） | lexical-grammar.md |
| E2001 | NAME-DUP-003 | 同作用域重复定义 | name-resolution.md |
| E2002 | NAME-UNDEF-004 | 未定义名（含未知 struct/构造器；go 目标须顶层函数 EVAL-GO-007） | name-resolution.md |
| E2003 | NAME-CTOR-005 | 用户构造器重名 | name-resolution.md |
| E2004 | NAME-RECV-006 | 方法接收者不是已知 struct | name-resolution.md |
| E2005 | NAME-MUT-007 | 对不可变绑定赋值 | name-resolution.md |
| E2006 | NAME-CTOR-005 | case 与内建名冲突 | name-resolution.md |
| E2007 | NAME-PLACE-008 | break/continue 在循环外 | name-resolution.md |
| E2008 | NAME-PLACE-008 | return 在函数外 | name-resolution.md |
| E2010 | NAME-NEST-009 | 嵌套类型声明 | name-resolution.md |
| E3001 | TYPE-ASSIGN-001 | 类型不匹配（赋值/参数/返回/字段/条件/运算） | type-system.md |
| E3002 | TYPE-ASSIGN-001 | int/float 无隐式转换 | type-system.md |
| E3003 | CALL-ARG-002 | 调用参数个数不匹配 | expressions.md |
| E3004 | TYPE-MEMBER-007 | 字段/方法不存在 | type-system.md |
| E3005 | RESULT-TRY-003 | `?` 位置非法或函数未注解 Result | option-result.md |
| E3006 | RESULT-RETURN-004 | Result 函数 return 形态 | option-result.md |
| E3007 | TYPE-ANN-006 | 参数缺类型注解（转译目标） | type-system.md |
| E3008 | RESULT-TRY-003 | `?` 操作数须为 Result | option-result.md |
| E3009 | GEN-SUBST-003 | Result 第二类型参数须为 error | type-system.md |
| E3010 | CTRL-FORIN-003 | 类型不可迭代 | control-flow.md |
| E3011 | TYPE-INDEX-008 | 一元/逻辑运算不适用 | type-system.md |
| E3012 | VALUE-REF-003 | `&` 仅限变量 | values.md |
| E3013 | TYPE-INDEX-008 | 索引端点须为 int | type-system.md |
| E3014 | TYPE-INDEX-008 | 映射键类型 | type-system.md |
| E4001 | MATCH-EXHAUSTIVE-002 | match 不穷尽（enum，编译期） | control-flow.md |
| E4002 | MATCH-KIND-001 | enum/代数类型混入字面量臂 | control-flow.md |
| E4003 | MATCH-KIND-001 | Result/Option/enum 模式混用 | control-flow.md |

（E2009 空号保留，历史未分配；新诊断码只能追加不能改义。）

## 已定义行为（D）与冻结面（F）

| Rule ID | 级 | 摘要 |
|---------|----|------|
| LEX-001..008 | D | 记号/注释/标识符/关键字/字面量/续行（lexical-grammar.md） |
| PARSE-001..006 | D | 程序结构/语句/优先级/match 语法/控制头禁裸 struct 字面量（grammar.md） |
| NAME-SCOPE-001 / NAME-SHADOW-002 / NAME-MERGE-010 | D | 作用域、shadowing、合并命名空间（name-resolution.md） |
| TYPE-KINDS-001 / TYPE-STRUCT-003 / TYPE-FN-004 / TYPE-CONV-005 | D | 类型类别、名义 struct/enum、函数结构兼容、显式转换（type-system.md） |
| TYPE-ANY-002 | N+F | Any=动态边界 / Unknown=推断不出，二者不得互换或作逃生口（type-system.md） |
| GEN-DECL-001 / GEN-INFER-002 / GEN-SUBST-003 | D+F | 泛型擦除语义；用户泛型 struct/enum 冻结不支持（type-system.md） |
| VALUE-COPY-001 / VALUE-SHARE-002 / VALUE-REF-003 / VALUE-CAPTURE-004 / VALUE-IMMUT-005 / VALUE-MAP-006 / VALUE-TASKSHARE-007 / VALUE-UNIT-008 | D | 值/引用/捕获/插入序/unit（values.md） |
| EVAL-ORDER-001 / EVAL-CALL-002 / EVAL-ASSIGN-003 / EVAL-LITERAL-004 / EVAL-INDEX-005 / EVAL-SHORTCUT-006 / EVAL-GO-007 | D | 严格左到右；赋值 target 地址先于右值；短路；go 实参就地求值（expressions.md） |
| CALL-RESOLVE-001 / CALL-ARG-002 / CALL-CLOSURE-003 | D | 静态解析、实参传递、闭包调用（expressions.md） |
| CTRL-IF-001 / CTRL-WHILE-002 / CTRL-FORIN-003 / CTRL-RETURN-004 | D | 条件（非 bool → E3011）/循环/迭代协议/返回（control-flow.md） |
| MATCH-KIND-001 / MATCH-EXHAUSTIVE-002 / MATCH-BIND-003 / MATCH-EXPR-004 | D+N | match 分类/穷尽性/绑定/表达式地位（control-flow.md） |
| RESULT-PRELUDE-001 / RESULT-NULL-002 / RESULT-TRY-003 / RESULT-RETURN-004 / RESULT-CHAN-005 / RESULT-MATCH-006 | D+N | 预载代数类型/无 null/`?` 传播（option-result.md） |
| MODULE-ROOT-001 .. MODULE-TEST-007 | D | 模块根/import/合并/循环/初始化序/可见性（F：MODULE-VIS-006 无 pub 语法）（modules.md） |
| TASK-MODEL-001 / TASK-SPAWN-002 / TASK-DETERMINISM-003 | N | Task 模型：同步确定子集为规范保证面，真并行属调度自由度（concurrency.md） |
| CHAN-CREATE-001 / CHAN-SEND-002 / CHAN-RECV-003 / CHAN-CLOSE-004 / CHAN-MAP-005 | D+F | 缓冲 1024、send 不阻塞、recv 阻塞、无 close 冻结（concurrency.md） |
| RUNTIME-FAIL-001 / RUNTIME-JSON-002 / RUNTIME-OUTPUT-003 | D | 运行时失败分类、JSON 确定性、输出面（runtime.md） |
| EFFECT-001 | D+F | 效果为元数据非约束系统（runtime.md） |
| FFI-001 | F | extern/bind 存在性锁定，不进 Conformance（runtime.md） |

## 冲突裁决

规则冲突 → specs/ 内逐条修正并升级本表；实现与规范冲突 → 以 specs/ 为准修实现；
实现间冲突 → 以 conformance/ 裁决，case 不足时先补 case 再修实现。任何 Rule 语义
变更必须同步：spec 正文、本表、诊断码（若涉）、Conformance case、ADR 记录。
