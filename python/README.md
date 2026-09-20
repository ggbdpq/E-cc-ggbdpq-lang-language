# python/ —— lang-go 语言的 Python 独立实现（lang-py）

同一门 lang-go 语言，宿主语言为 Python。语义真值源是仓库顶层
[specs/](../specs/) 与 [conformance/](../conformance/)；本实现与其余实现
（go/ typescript/）共用同一套验收判据。

## 状态

**完整能力面已对齐参考实现**：conformance 套件 108/108 全绿（与 Go 参考
同刻同基线），含三路差分（解释器 / →Go / →JS）与诊断契约断言。
验收明细见 [docs/lang-py/parity-report.md](../docs/lang-py/parity-report.md)。

## 快速上手

```bash
cd python
# 全部动词（等效 lang-go 的 CLI 面；可执行名 lang-go-py）
PYTHONPATH=src python -m lang_py.cli.main run
PYTHONPATH=src python -m lang_py.cli.main check --json
PYTHONPATH=src python -m lang_py.cli.main verify
PYTHONPATH=src python -m lang_py.cli.main conformance   # 跑跨实现套件

# 测试
python tests/run_conformance_dev.py   # conformance 108 case（含差分）
python -m pytest tests -q             # 单测 + conformance + AST/IR 差分
```

安装为命令行工具：`pip install -e python`（提供 `lang-go-py` 入口）。

## 结构

| 目录 | 职责 |
|---|---|
| `src/lang_py/syntax/` | tokens / span / lexer / parser / AST / walk / 注释 trivia |
| `src/lang_py/loader/` | 目录即包 + import 展开（环检测/菱形去重/越界拒绝） |
| `src/lang_py/semantic/` | 符号表与名字解析（E2xxx，SymbolId 稳定编号） |
| `src/lang_py/typesys/` | 类型模型 / 注解 / 推断 / 检查器（E3xxx/E4xxx + ruleId） |
| `src/lang_py/hir/` `ir/` | Typed HIR 与 Core IR（构建/降级/遍历） |
| `src/lang_py/interp/` | AST 解释器（参考语义 Oracle；值模型与输出格式化硬契约） |
| `src/lang_py/irinterp/` | Core IR 解释器（四路差分面） |
| `src/lang_py/backends/` | →Go / →JS 转译（prelude 内联产物） |
| `src/lang_py/contract/` | Agent Contract：JSON 信封 / DTO / ruleId / dump |
| `src/lang_py/graph/` | 语义图（call/reference/write/spawn/type 边）与效果推导 |
| `src/lang_py/edit/` `fix/` `refactor/` | 结构化编辑事务 / 机械安全修复 / 语义重构 |
| `src/lang_py/formatter/` | 幂等格式化器（注释 100% 保留） |
| `src/lang_py/conformance/` | manifest 解析 / case runner / verify 九阶段管线 |
| `src/lang_py/cli/` | 全部动词编排 + REPL（不承载语义） |
| `tests/` | 单元 / conformance / AST-vs-IR 差分 / 解释器-vs-→JS 差分 |

## 设计约定

- AST/HIR/IR 节点为 dataclass（身份语义，等价 Go 指针键）；引用与类型表以
  节点身份为键，仅单次编译内有效。
- 标量值即宿主 int/float/bool/str（动态边界）；复合值显式类——数组/映射引用
  语义，struct/enum 值语义（递归拷贝）。int 一律 64 位环绕。
- 语言级诊断走 `Diagnostic` 聚合（可 raise 的 `DiagnosticList`）；宿主异常仅承载
  实现故障与运行时失败；解释器控制流信号在函数/循环边界消化，不穿越 CLI。
- 诊断 `code + ruleId + severity + primarySpan` 与输出排序是跨实现契约；
  message 文本对齐参考实现便于人工对照。
