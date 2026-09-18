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

```
R4 →JS 转译后端（第二张映射表；并发退化、int64 精度、DOM extern）
R5 格式化器（工具链三项承诺之一，decisions #8，尚未动工）
R6 最小静态检查器（未定义名 + ? 约束 + 穷尽前移的完整版）
R7 CLI 放开子目录参数（ling test m4/，cwd containment 内）
        │
        ▼
M5 收口（内容已定：R4–R7 + tour 教程 + v0.2.0 tag，见 decisions #41/#46）
```

### R4 →JS 后端
- ESM 输出 + 无依赖 prelude.js；int64 用 number（2^53 安全线内，spec §6.3 已声明）。
- 字符串/数组/映射映射原生（Map 保插入序，语义契合）。
- `go`/`chan` v0.2 遇到即报未支持（微任务调度留到有真实场景）。

### R5 格式化器
- gofmt 式全量重排；缩进 4 空格（与现有 examples 一致）；34+ 个 .ling 文件当语料。

### R6 最小静态检查器
- 不做完整类型系统：未定义名、`?` 函数返回 Result 约束、match 穷尽（穷尽已在转译器有，前移到解释器路径共用）。

### R7 CLI 子目录
- `ling run/test <子目录>`，仍限制在 cwd 树内（维持 Mimosa 约束下的安全面）。

