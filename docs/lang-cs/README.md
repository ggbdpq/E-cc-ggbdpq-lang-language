# lang-cs v1.0 独立实现计划包

第五套独立实现的计划包，目标目录 `impls/csharp/`，宿主 C# / .NET 10。

与 lang-rs 计划包的关系：**lang-rs 计划（`docs/lang-rs/2026-09-25-lang-rs-完整独立实现计划.md`）是本计划的全量母本**——
目标定义、裁决顺序、Git 工作方式、35 个 Phase、L0-L6 等级、最终验收清单全部沿用，此处只写差异（Δ）。
阅读顺序：

1. `docs/lang-rs/2026-09-25-lang-rs-完整独立实现计划.md`（母本，必读）
2. `2026-09-25-lang-cs-基线快照.md`（第五实现时的仓库基线）
3. `2026-09-25-lang-cs-计划差异.md`（C# 宿主的全部差异点：拓扑/依赖/对抗轴/技术裁定）
4. `interface-contract.md`（协调人冻结的接口契约，六智能体共同编码锚）
5. `docs/lang-rs/2026-09-25-lang-rs-对抗验证重点.md`（对抗重点沿用，UTF-16 轴增补见计划差异）

关键基线：

```text
impls/go / typescript / python / rust   ← 四套既有实现（rust 已达 L6：151×4 全绿）
impls/csharp                            ← 本计划目标
```

语言版本 v1.0.0；共享 Conformance 151 case；最高裁决 `specs > Rule Catalog > conformance > implementations`。
开发期 Full-Parity Working Tree，零 git 提交；完成后只产出五份报告（同 lang-rs §54）。
