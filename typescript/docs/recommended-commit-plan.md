# 建议提交序列（Commit Plan）——仅供人工执行，本工作流不提交

> 状态更新（最终同步）：本计划原覆盖的工作区代码改动（gen/genjs/hir/
> conformance 修复与新增工具面模块）已由并行 v1.0 工作流经
> 3d54571 → 4aea287 → 4dd235c 合并重写**入库**；原提交 1–5 的内容事实上已
> 随该批次落地。其后套件扩容至 132 case，本工作流又做了三个后端修复
> （未提交），当前工作区改动：
>
> ```text
>  M typescript/src/backends/gen.ts        # void fnlit 无返回函数化 + isVoidCall
>                                          # 函数值路径 + noneCtorFor 扩展 struct 字段位
>  M typescript/src/backends/genjs.ts      # 一元套一元括号化（--5n 语法错）
>  M typescript/docs/spec-gaps.md          # A1/A2/A5/D 关闭 + §D 套件缺陷台账
>  M typescript/docs/capability-matrix.md  # 验证数字同步 132 case
>  M typescript/docs/parity-report.md      # 同上
> ?? typescript/docs/recommended-commit-plan.md
> ?? typescript/tools/{case-diff,examples-diff,dump-go,gen-raw,check-js}.ts
> ```
>
> 按原始约束「不真正提交」，以下保留为后续微调的拆分建议。

## 建议序列（剩余部分）

```text
0. fix(backends): 132-case 扩容三修复
   body: (a) emitFnLit 无注解函数字面量不再写返回类型（Go 缺 return 编译错），
   isVoidCall 补函数值路径（void 调用不得 _ =）；(b) noneCtorFor 扩展到
   struct 字面量字段位（Slot{id: None} 的 LangGoOpt[int64] 收窄）；
   (c) genjs 一元套一元括号化（--5n 为 JS 自减语法错）。
   验证: closure/closure-loop-capture-var、match/match-option-in-struct、
   syntax/unary-negation-nested 三路一致；Final Sync conformance **132/132**
   （3 个 yaml 已由 v1.0 工作流修正为双侧实际输出）；examples+m4 10/10。
   注: Go 参考实现同套件 130/132——仍失败本提交修复的其中 2 个
   （closure-loop-capture-var / unary-negation-nested），本提交须先于或
   同批随 Go 侧 gen 修复落地。

1. docs(ts): SPEC-GAP 最终同步结论
   body: A1（条件非 bool → E3011，specs/control-flow.md 工作区修订后关闭）、
   A2（E3009 行号 0，case 期望同步后关闭）、A5（Err(e) 位置面，108 全绿后
   关闭）、D（观察项更新）；补 parity 数字 108/108。
   验证: 纯文档，无行为面。

2. docs(ts): capability-matrix / implementation-map / parity-report
   body: 验证现状节引用 4dd235c 后实测（conformance 108/108、examples+m4
   10/10、vitest 20/20、verify 九阶段冒烟）；implementation-map 为 Go 包 →
   TS 模块地图与有意结构差异。

3. chore(ts): 跨实现差分调试工具
   body: tools/examples-diff.ts（examples+m4 全量三路差分，goroutine 交错序
   按 TASK-DETERMINISM-003 记 NOTE）、tools/case-diff.ts（单 case 四路对比）、
   dump-go/gen-raw/check-js（发射产物速览）。
   验证: node tools/examples-diff.ts → 10 通过 0 失败。
```

## 提交信息模板

```text
docs(ts): spec-gaps 最终同步——E3011/E3009/Err(e) 位置面关闭

- specs/control-flow.md 修订（非 bool 条件 → E3011）后复跑：conformance
  108/108、examples+m4 10/10、vitest 20/20
- 详细台账见 typescript/docs/spec-gaps.md

Generated-by: GLM-5.3-Flash (ZCode)
```

## 执行约束

- 每条提交前：`tsc -p tsconfig.json && vitest run && node dist/cli/main.js conformance ../../conformance` 全绿。
- 不使用 `git add -A`（会扫进 dist/、.tmp-build/ 等临时物）；按文件显式 add。
- conformance/ 与 specs/ 属共享真值，任何 case/spec 变更须与 go 侧同批提交。
