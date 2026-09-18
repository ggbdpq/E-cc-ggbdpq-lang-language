# ling 路线图（剩余工程）

本文承接 decisions.md 的里程碑表，把"已交付报告里说过、但还没落到任何计划文档"的剩余工程显式化。状态截至 2026-09-18（M4 第一步完成后）。本文件是计划快照，不是决策记录——决策仍以 decisions.md 为准。

## 当前位置

| 里程碑 | 状态 |
|--------|------|
| M1 规范 + 决策台账 | 完成 |
| M2 解释器 + REPL + ling run/test | 完成 |
| M3 →Go 转译后端 + 编译期穷尽检查 | 完成（→JS 未做） |
| M4 移植 linkpull bilibili 提取器 | **完成**：md5/wbi/video/list 纯函数层全部移植；`ling test` 11/11；E2E（解释器 = →Go = gofixture 原版输出）全绿 |
| — R1 extern / R2 http+json / R3 import | **完成**（随 M4 收口交付） |

## 剩余工程

无——M5 已收口（R4 →JS 后端、R5 fmt、R6 `ling check`、Q22 摘录全部交付），v0.2.0 已打 tag。

后续候选（均无排期，消费方出现再议）：
- `go`/`chan` 并发（→JS 微任务退化、→Go 真 goroutine）
- extern 自动绑定 / .d.ts 摄入
- 性能（树遍历 → 字节码）
- tour 教程（Q45）

