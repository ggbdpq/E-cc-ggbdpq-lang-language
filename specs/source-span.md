# Source Span —— 源码定位契约（v0.4）

> 状态：随 v0.4.0 冻结结构。实现：`impls/go/internal/syntax`（Position/Span）、`impls/go/internal/contract`（JSON 形态）。

## 1. JSON 形态

```json
{
  "file": "main.lang-go",
  "start": {"offset": 0, "line": 1, "column": 1},
  "end":   {"offset": 12, "line": 1, "column": 13}
}
```

- `offset`：**0 基 rune 偏移**（不是字节——中文/emoji 环境下列与偏移按 Unicode 码点计）。
- `line`：1 基；`column`：1 基（rune 计）。
- 区间半开：`[start, end)`；空节点/诊断锚点允许 start == end。
- `file`：模块根相对路径（`/` 分隔），由装载器在解析后盖章（v0.3 已知缺口的修复）。

## 2. 精度承诺（v0.4）

| 层 | 精度 |
|----|------|
| AST（dump ast） | **列级**：start=首记号起点，end=末记号终点 |
| HIR（dump hir） | 列级（继承自 AST 盖章） |
| 诊断 primarySpan | **行级**（column 恒 1，offset 未填）——v0.5 收紧到列 |
| IR（dump ir） | 不含 span（IR 为精简执行视图） |

## 3. 稳定性

同一源码 + 同一工具版本，全部 span 逐字节稳定。编辑源码后 span 按新解析结果重算，
不做差异保持（v0.5 Lossless 层的目标）。
