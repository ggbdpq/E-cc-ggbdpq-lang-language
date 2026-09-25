# 版本与兼容策略（v1.0 起）

lang-go 的版本承诺。**语义真值在 specs/；本文件定义「改动的许可边界」。**

## 1. 版本号

SemVer：`MAJOR.MINOR.PATCH`（RC 后缀：`1.0.0-rc.1`）。

**唯一版本源**：Go 参考实现 `impls/go/internal/version/version.go` 的 `Version` 常量。
CLI `lang-go version`、Agent Contract 的 `toolVersion`、发布 tag 三者同源。
其他实现（lang-ts/lang-py）的 `toolVersion` 在同一 `1.x.y` 语义下独立打点，
其**诊断/语义契约版本**随 specs/ 而非随实现。

## 2. 各层兼容承诺

| 层 | Patch（1.0.1） | Minor（1.1.0） | Major（2.0.0） |
|----|----------------|----------------|----------------|
| 语法 | 不变 | 只增（新语法不影响既有合法程序含义） | 允许破坏 |
| 静态语义（valid/invalid 判定） | 不变 | 只收紧为实现-defined 面；不使既有合法程序非法 | 允许破坏 |
| 动态语义（可观察行为） | bug 修复（且属规范明确行为） | 只增能力 | 允许破坏 |
| Rule ID | 不变 | 只追加 | 不可改义/复用；废弃保留编号 |
| 诊断码 | 不变；message 文案可改善 | 只追加新码 | 不可复用/改类别 |
| Agent Protocol（schemaVersion 1） | 可加 optional 字段 | 同左；新查询命令 | 删 required 字段/改义 → schemaVersion +1 |
| SourceSpan | 不变（0 基码点 offset、1 基行/列） | 不变 | 允许破坏 |
| Conformance manifest 格式 | 不变 | 向后兼容扩展（新可选字段/新 mode） | 允许破坏 |
| CLI 子命令面 | 不变 | 只增子命令/flag | 允许破坏 |
| →Go/→JS 生成源码 | 不承诺逐字节稳定（行为稳定即可），单版本内确定性 | 同左 | 不承诺 |
| HIR/IR dump | 不承诺跨版本稳定（诊断/调试用，非契约） | — | — |

## 3. 冻结面（v1.0 起需 ADR + Major 或明示 Minor 才能动）

- Rule Catalog 既有条目语义；预载代数类型（Option/Result/error 的 case 名与序）；
  求值顺序（EVAL-*）；值语义（VALUE-*）；并发确定性边界（TASK/CHAN）；无 close、
  无 pub、Any/Unknown 边界、unit 位置（v0.9 F 级冻结项延续）。

## 4. 变更流程

任何语义变更必须同轮闭环：**Spec → ADR → Rule（如涉）→ 诊断码（如涉）→
Conformance → 参考实现 → 第二实现**。缺一不合入 main。

## 5. 弃用流程

Minor 版本宣布弃用（诊断/文档标注 deprecated + 替代物）→ 至少保留一个 Minor
周期 → Major 才可移除。Rule ID/诊断码永不删除，只标 deprecated。
