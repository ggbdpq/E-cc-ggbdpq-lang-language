# lang-py Implementation Map（Python 工程范式设计）

> 独立实现声明：本映射只记录「语义点 → 模块」的对应关系供审计对照，
> 不是 Go 源码的机械翻译表。Python 侧按主流范式重新设计：
> frozen dataclass 语义树、显式 Visitor（match 派发）、Result 用返回值不用异常承载、
> 宿主异常仅表示实现故障或语言级运行时错误的统一载体（LangRuntimeError / control signal）。

## 目录

```text
python/
├── pyproject.toml            # PEP 621；console_scripts: lang-go-py = lang_py.cli:main
├── src/lang_py/
│   ├── errors.py             # LangError 族：ParseError/SemanticError(s)/LangRuntimeError/信号
│   ├── syntax/               # tokens.py span.py lexer.py ast.py parser.py walk.py
│   ├── loader/loader.py      # 目录即包 + import 展开（环检测/菱形去重/containment）
│   ├── semantic/             # symbols.py resolver.py diagnostics.py
│   ├── typesys/              # model.py anno.py infer.py checker.py diagnostics.py
│   ├── hir/                  # nodes.py builder.py walk.py dump.py
│   ├── ir/                   # nodes.py lower.py dump.py
│   ├── irinterp/machine.py   # v0.9 IR 解释器（复用 interp 值模型与内建）
│   ├── interp/               # values.py builtins.py stdlib.py interp.py（AST 参考 Oracle）
│   ├── backends/             # go_prelude.py go_backend.py js_prelude.py js_backend.py
│   ├── pipeline.py           # check/analyze/compile + 诊断格式化（E1/E2/E3/E4 分段）
│   ├── contract/             # model.py rules.py check.py inspect.py dumpast.py dump.py effects.py
│   ├── graph/                # builder.py query.py effects.py
│   ├── edit/edit.py          # TextEdit/WorkspaceEdit/apply/sha256
│   ├── fix/fix.py            # 机械安全修复 + 事务闭环
│   ├── refactor/             # locate.py rename.py imports.py remove.py
│   ├── bind/bind.py          # extern "go" 声明草案
│   ├── formatter/formatter.py# 幂等格式化 + 注释 100% 保留（Lossless trivia 层）
│   ├── conformance/          # manifest.py runner.py verify.py
│   └── cli/                  # main.py（命令编排，无语义） repl.py
└── tests/                    # unit/ golden/ differential/ conformance_test.py
```

## 关键设计决策（Python 范式，非 Go 翻译）

| 语义点 | Go 做法 | lang-py 做法 |
|---|---|---|
| AST/HIR/IR 节点 | struct + interface | frozen dataclass（不可变，杜绝别名泄漏）；HIR/IR 节点含 `meta: NodeMeta`（id/span/sym/type） |
| 多态分派 | type switch | `match` 语句按节点类分派（结构性单分派，无需注册表） |
| 引用绑定表 | map[Expr]*Symbol（指针键） | dict[id(node) → Symbol] 于解析上下文中（编译期内有效），或节点带 slot——采用节点带 `ref` 字段更直接 |
| SymbolID | int 自增 | `SymbolId` NewType，自增分配，同序 |
| 多返回值 (Value, error) | Go 元组 | 返回 `Value`，语言级运行时错误 raise `LangRuntimeError`；控制流信号 raise `SignalReturn/Break/Continue/Err`（内部机制，不穿越 CLI 边界） |
| MapVal 插入序 | keys 切片 + map | 单 dict（插入序）+ key 编码 `i:/s:/b:`，快照列表按需生成 |
| int64 环绕 | Go 原生 | `wrap64()` 于算术点（GAP-001） |
| float 格式化 | strconv 'g' | `format_g()` 自实现（GAP-002） |
| goroutine | go 语句同步执行 | 解释器直接同步调用（TASK-SPAWN-002 确定子集） |
| chan | 队列模拟 | deque(maxlen 未强制)——满 1024 报运行时错误 |
| gofmt 产物 | go/format | Python 侧生成已符合 gofmt 排版的文本（缩进/tab、块结构固定），不调用外部工具；产物可 gofmt 校验 |
| 并发安全 | 不涉及 | 单线程解释器；后端产物由宿主工具链执行（subprocess 显式 UTF-8） |
