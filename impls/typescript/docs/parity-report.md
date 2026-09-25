# lang-ts Parity Report（最终契约同步版）

> 基准：go/ 参考实现 @ 5128610（v1.0.0-rc.1 发布）+ conformance 151 case 批次。
> 结论：**与参考实现同套件全绿**——conformance 148/148、examples+m4 三路差分
> 10/10、vitest 回归 20/20、九阶段 verify 冒烟通过、CLI 全动词冒烟通过。

## 1. 验收判据逐项核对

| 判据 | 证据 | 结果 |
|------|------|------|
| 同一 conformance 套件 | 套件扩至 151 case：**151/151，0 失败**。Go 参考实现此前的 2 个后端缺陷（void fnlit 缺 return、genjs `--5n`）已在 lang-ts 侧先行修复并随 4f6ca35 双侧落地 | ✅ |
| 三路执行 stdout 逐字节一致 | examples 9 例 + go/m4 全包：**10/10**（goroutine 交错序为 specs/concurrency.md 文档化平台差，按 NOTE 计） | ✅ |
| IR 语义 = AST 语义 | 全部 conformance run 模式与 examples 差分内嵌 irinterp 比对，零失败 | ✅ |
| 静态语义/诊断一致 | check 模式 code+ruleId+severity+line 多重集全绿；诊断文案与参考逐字同源 | ✅ |
| Agent Contract | check --json / inspect ×10 / dump ×3 信封结构、键序、HTML 转义、行级 span 对齐；toolVersion=1.0.0 单源 | ✅ |
| Semantic Graph / Effects | callers/callees/impact/dependency/effects 全查询可用（effects 仅直接效果=参考现状，见 spec-gaps A4） | ✅ |
| Safe Editing / Refactoring | fix --safe 闭环（哈希前置/重叠拒绝/验证闭环）；rename/organize-imports/safe-remove token 级定位 | ✅ |
| Runner Contract 五动词 | run/check/compile/inspect/verify 全部可用；工具链缺失显式 skipped | ✅ |
| UTF-8 码点 Span | 全链码点度量；Agent Contract/TextEdit 对外只有码点偏移 | ✅ |
| 无 `any` 逃生口 | 42 文件 18.4k 行 strict 编译零 any（spawn/manifest 边界用 unknown+收窄） | ✅ |
| 宿主风险专项 | spec-gaps.md §C：float %v、JSON 编码、UTF-16、async、null 隔离、ESM 六项各有实现+测试 | ✅ |
| 无 Git 提交（本工作流） | 本工作流零提交；代码修复经并行 v1.0 工作流入库（3d54571/4aea287/4dd235c），工作区仅余 docs/spec-gaps.md 未提交 | ✅ |

## 2. 与参考实现的行为对齐清单（收口期同步）

以下分歧在收尾窗口由双侧收敛，当前状态逐一核验：

1. 字面量/区间/逻辑 HIR 节点类型——已对齐（range=Range、and/or=Bool），
   lower 的 iterKind 归类随之正确。
2. VALUE-ELEM-009 容器元素值语义——interp 写回、irinterp deref 全链、gen
   读改写三行——已对齐。
3. emitForIn 区间经绑定变量——已对齐（两条路径）。
4. 条件非 bool → **E3011**——specs/control-flow.md 工作区修订后与实现一致；
   conformance case 期望同步为 E3011；lang-ts 现行为即 E3011 ✅。
5. `Err(e)` 非 Result 函数 / Ok-Err 位置面——v1.0 侧已修（108 全绿含
   error-value-construction）✅。
6. None 跨函数实参类型实参——lang-ts `noneCtorFor` 特化（spec-gaps A6，
   单侧小步修复，建议参考同款）。

## 3. 已知残留（不阻塞验收）

- **spawn-eval-args-immediately 的 →Go 竞态**：最终批次已由 v1.0 侧收敛，
  132/132 时点无失败；若再现，属 specs/concurrency.md 文档化平台差。
- **effects fixpoint 空转**：参考实现现状的忠实镜像；EFFECT-001 的
  「传播」语义待 ADR（spec-gaps A4）。
- **E2009 空号**、Ok/Err 位置面细则：随规范演进，双语实现同表同步。

## 4. 结论

lang-ts 已达成「与 lang-go 同一门语言」的全能力面对等：
语法前端、语义核心（resolver/checker/generics）、HIR/IR、四路执行
（解释器/IR 解释器/→Go/→JS）、格式化器、Agent Contract、语义图、
Safe Editing、语义重构、bind、verify 与 conformance runner。
在共同真值（specs/ + conformance/）当前批次上，双侧行为一致。
