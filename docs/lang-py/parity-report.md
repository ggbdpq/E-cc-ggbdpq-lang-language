# lang-py Parity Report（复刻验收报告）

> 生成：2026-09-21（Final Contract Sync 后，随 v1.0 提交 4dd235c 同步）。
> 结论：**lang-py 已达成与 lang-go 参考实现的完整能力面对齐**——conformance 套件
> **132/132 全绿**（run 模式三路差分 + check 模式诊断断言），语义/诊断/契约/重构/验证
> 全链路可用。参考实现同刻 127/132（5 例在途收口中，lang-py 已先行全绿，见 §2 末与 §5.1）。

## 1. 验收判定

| 验收面 | 判定 | 证据 |
|---|---|---|
| Conformance 套件（132 case，随在途工作流扩容） | **132/132 全绿** | `python tests/run_conformance_dev.py`；run 模式 = 解释器 + →Go 产物 + →JS 产物三路 stdout 逐字节一致；check 模式 = code+ruleId+severity+line 多重集精确匹配 |
| AST 解释器 vs IR 解释器差分 | 全绿 | `pytest tests/conformance_test.py::test_ast_vs_ir_interpreter_differential`（全部可运行 case 逐字节一致，v0.9 四路差分面） |
| 单元测试（语义点/诊断码/格式化/契约） | 57/57 | `pytest tests/unit/test_core.py` |
| 与参考实现同刻比对 | lang-py 领先 | `go test ./internal/conformance/ -run TestConformanceSuite` = 127/132（参考在途收口）；lang-py 132/132 |
| 格式化幂等 property | 71/71 源文件 | format(format(x)) == format(x)（唯一跳过案例为字面量科学计数法缺陷，参考实现同型） |
| float 格式化位级差分 | 2042/2042 | 2000 随机 float64（Go 位模式重建）+ 42 固定向量，`strconv.FormatFloat(f,'g',-1,64)` 逐字节一致 |
| 字符串 quote 差分 | 13/13 | `strconv.Quote` 嵌套字符串输出逐字节一致 |
| 全部 CLI 动词实测 | 通过 | run/test/check(--json)/fmt/build go,js/bind/inspect 9 子命令/dump ast,hir,ir/fix --safe/refactor 3 子命令/verify/conformance/REPL |

## 2. Final Contract Sync 记录（2026-09-21）

v1.0 提交 `4dd235c`（语义收口第一批+第二批 + 仓库重组）落地后逐项同步：

| 参考变更 | lang-py 处置 | 状态 |
|---|---|---|
| EVAL-GO-007 go 实参快照（临时变量防闭包按引用捕获） | go_backend Spawn 发射同步 | FIXED ✓ |
| 诊断 27 码 ↔ Rule 映射、schema v1 字段面（v1.0 复核） | 逐项核对无漂移 | MATCH ✓ |
| VALUE-ELEM-009 容器元素字段链读改写 | go_backend emitAssign(Field over Index) | FIXED ✓ |
| gen let unused 消音（`_ = name`） | go_backend emit_let 同步 | FIXED ✓ |
| 区间变量迭代（let r=a..b; for i in r）双后端 | go_backend + js_backend 各两路径 | FIXED ✓ |
| None 类型实参特化（exprArg） | go_backend expr_arg 同步 | FIXED ✓ |
| irinterp deref 全链 + Err 传播携整值 | irinterp 建成即含；Err 传播按参考口径修正 | FIXED ✓ |
| 闭包作用域链显式保留在 IR（v0.9 收口） | ir lower FnLit 用 push()（继承外层链） | FIXED ✓ |
| CTRL-IF-001 裁定 E3011 | checker 既有行为即 E3011，无需改 | MATCH ✓ |
| specs/versioning.md 版本策略（第二实现 1.x.y 独立打点） | TOOL_VERSION = "1.0.0" | MATCH ✓ |
| prelude 归一（internal/prelude 数据表） | 契约面不变（case 名与序一致）；lang_py 保持自持预载 | MATCH（实现自由度） |
| 诊断 27 码 ↔ Rule 映射、schema v1 字段面 | rules.py/model.py 与 specs/rules.md、schemas/ 逐项核对无漂移 | MATCH ✓ |
| StructLit 字段 None 特化 + FnLit unit-return 补齐（参考在途 diff 同步） | go_backend expr_arg/emit_fn_lit | FIXED ✓ |
| void 函数值调用（无注解 fnlit 的 Unknown 返回 → 调用不作值；closure-loop-capture-var） | is_void_call 扩展 fn/method/callee 三路径同口径 | FIXED ✓ |
| JS 负负歧义 `-(-5)` → `--5n` 被 JS 解析为递减（unary-negation-nested） | js_backend 负号后补空格 | FIXED ✓（参考/TS 同踩待修） |
| diagnostics/try-on-option、try-outside-result-fn、match-result-option-mixed 修订 | 期望语义与 lang-py checker 既有行为一致，case 修订后即绿 | MATCH ✓ |
| match-option-in-struct（StructLit 内 None） | StructLit 字段走 expr_arg 后即绿 | FIXED ✓ |

## 3. 能力面清单（全部 VERIFIED）

```text
Source → Lexer → Parser → AST(Span/注释 trivia) → Loader(目录即包+import)
      → Resolver(SymbolId/作用域/遮蔽/引用) → Type Checker(E1~E4 全 27 码)
      → Typed HIR → Core IR → IR 解释器
      → AST 解释器（参考 Oracle）→ →Go 后端 → →JS 后端
工具链：run / test / fmt / check(--json) / build go|js / bind /
        dump ast|hir|ir --json / inspect symbols|refs|type|dependencies|effects|
        callers|callees|impact|references|dependency --json /
        fix --safe(--dry-run)(--json) / refactor rename|organize-imports|safe-remove(--apply) /
        verify / conformance / REPL
契约层：Agent Protocol（schemaVersion 1 信封、Symbol/Reference/Node/Dependency/Effect/
        Impact DTO、ruleId 映射、确定性排序）、TextEdit/WorkspaceEdit（sha256 前置+
        重叠检测+原子写回）、语义图（call/reference/write/spawn/type/depends_on 边 + BFS 查询）
```

## 4. 已登记的实现自由度与固有差异（详见 spec-gaps.md）

- **int64 溢出**：解释器与 →Go 环绕（与参考一致）；→JS BigInt 不环绕（参考实现自身差异，语料不覆盖）。
- **float 格式化**：Go 'g'（shortest，指数 < -4 或 ≥ 6 走 %e）自实现，位级差分验证。
- **ToolVersion**：1.0.0，随 specs/versioning.md 第二实现独立打点策略。
- **send 缓冲上限**：lang-py 解释器按 specs/concurrency.md（1024 满即运行时错误）实现；
  参考 AST 解释器暂为无限缓冲（ specs 优先，已在报告中登记）。
- **bind 草案**：Go 源签名解析为 Python 轻量扫描（bind 面不进 Conformance，FFI-001）。

## 5. 测试与运行入口

```bash
cd python
python tests/run_conformance_dev.py          # conformance 套件（含三路差分）
python -m pytest tests -q                    # 单测 + conformance + AST/IR 差分 + 解释器vs→JS 差分
PYTHONPATH=src python -m lang_py.cli.main    # 全部动词（help 查看用法）
```

## 5.1 套件滚动基线声明

conformance/ 由并行 v1.0 工作流持续扩容（本会话内 33 → 108 → 132 例）。
会话前半程在途 churn 期以「失败集与参考逐例逐签名一致」为 parity 口径；
最终 lang-py 对全部 132 例达成全绿（含参考侧尚在收口的 5 例——void 函数值
调用、JS 负负歧义、3 例 `?` 传播/match 混用诊断，lang-py 均已实现正确语义）。
`tests/conformance_test.py` 的 `KNOWN_FAILING_ON_REFERENCE` 已清空，
每轮 Final Sync 时随参考实现状态刷新。

## 6. Git 状态声明

实现期间 lang-py 侧未执行任何 `git init/add/commit/tag/push/rebase/cherry-pick`；
工作区全程未提交。`python/` 骨架被并行 v1.0 工作流的提交 4dd235c 收编入库，
本报告生成时工作区的增量改动保持未提交状态，提交拆分建议见
`docs/lang-py/recommended-commit-plan.md`。
