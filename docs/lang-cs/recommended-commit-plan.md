# lang-cs Recommended Commit Plan

> 仅计划，不执行。开发期零 git 提交已满足；由人工审阅后实施。

## 与 lang-rs 提交计划（docs/lang-rs/recommended-commit-plan.md）的差异

拓扑同构（14 笔，依赖序一致），仅替换宿主细节，并吸收 lang-rs 实测教训：

1. **dev/normal 依赖咬合**：`Lang.Interp` 测试引用 `Lang.Conformance`、`Lang.Conformance` 依赖 `Lang.Loader`、`Lang.Cli` 依赖 Edit/Refactor——提交序必须为
   core → syntax → types+semantic → hir → contract+graph → runtime → ir → backends → loader+formatter → interp+conformance+verify → edit+refactor → cli → docs → release
   （lang-rs 实测：3 次重排才收敛；本计划已按最终序写死，实施前仍建议逐前缀 worktree `dotnet build` 抽验）。
2. **每前缀可编译验证**：`git worktree add` + `dotnet build -warnaserror`（注意 worktree 路径用正斜杠避免 MSYS 转换坑）。
3. CI（最后一笔）：`actions/setup-dotnet@v4`（dotnet-version: 10.0.x）+ build/test/format 三门禁 + go/node setup（差分依赖）。
4. Message 序列沿用 lang-rs 计划的 14 笔命名（rust→cs：`chore(cs): bootstrap …` / `feat(cs-syntax): …` 等），
   尾注保持 `Generated-by: GLM-5.3-Flash (ZCode)`。
5. 根 README 目录表补 `impls/csharp/` 行（第五实现）随最后一笔入库。
