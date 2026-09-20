# Agent Protocol —— 机器接口契约（v0.4）

> 状态：**v1 草案**（随 v0.4.0 冻结字段）。实现：`go/internal/contract`（唯一转换边界）。
> Agent 只允许依赖本协议，不读取编译器内部 Go struct、debug dump 或 backend 私有类型。

## 1. 顶层信封

所有机器输出为统一 JSON 信封：

```json
{
  "schemaVersion": "1",
  "toolVersion": "0.4.0",
  "kind": "inspect.symbols"
}
```

- `schemaVersion`：协议版本。v0.4.x 内字段只加不改不删；破坏性变更升 `"2"`。
- `toolVersion`：工具版本，与 `lang-go version` 同源。
- `kind`：`check | dump.ast | dump.hir | dump.ir | inspect.symbols | inspect.refs | inspect.type | inspect.dependencies | inspect.effects`。
- 错误负载：`"error": "<人类可读原因>"`，配非零退出码（定位失败/参数缺失=1 或 2）。

## 2. 命令

| 命令 | 输出字段 |
|------|----------|
| `lang-go check --json` | `success`, `diagnostics[]` |
| `lang-go dump ast\|hir\|ir --json` | `nodes[]` |
| `lang-go inspect symbols --json` | `symbols[]` |
| `lang-go inspect refs --json` | `references[]` |
| `lang-go inspect type --file <f> --line <n> --column <n> --json` | `nodes[0]`（最内层命中节点）；未命中 → error，退出码 1 |
| `lang-go inspect dependencies --json` | `dependencies[]` |
| `lang-go inspect effects --json` | `effects[]` |

输入约定：命令作用于**当前目录模块**（不收路径参数，与 run/test 安全边界一致）；
`--file` 仅作归属过滤。

## 3. 核心对象

### SourceSpan

```json
{"file":"main.lang-go","start":{"offset":0,"line":1,"column":1},"end":{"offset":12,"line":1,"column":13}}
```

- `offset`：0 基 **rune** 偏移（非字节）；`line`/`column`：1 基。详见 specs/source-span.md。

### Symbol（inspect.symbols）

`symbolId`（编译内稳定，见 §4）、`name`、`kind`（function/method/variable/parameter/struct/enum/constructor）、
`definition`（Span）、`type`（语义类型字符串，dyn 值为 `any`/`unknown`）、`visibility`（public=顶层声明 / local）、
`module`（定义文件相对路径）。预载符号（内建/Option/Result 等）不输出。

### Reference（inspect.refs）

`symbolId`、`span`、`referenceKind`：`read | write | call | pattern | type`（StructLit 对类型的引用）。
排序：symbolId → file → offset。

### Node（dump.ast / dump.hir / dump.ir）

`nodeId`、`kind`、`span`（dump.ir 节点不含 span——span 见 dump.hir，IR 为精简执行视图）、
`type`（语义类型；AST/HIR/IR 均标注）、`name`（标识符/运算符/调用名）、`value`（字面量文本）、
`children[]`（有序）。kind 枚举见 schemas/inspect.schema.json（如 int_lit/binary/match/spawn/ctor/try…）。

### Dependency（inspect.dependencies）

`module`（文件相对路径）+ `imports[]`（该文件声明的 import 路径原文，依出现序）。

### Effect（inspect.effects）

`symbolId`、`name`、`effects[]`（按固定序 PURE,IO,FS,NET,TIME,RANDOM,SPAWN,CHANNEL,FFI 去重输出；
无直接/传播效果时为 `[PURE]`）。推导规则：直接效果来自 IR（println/print→IO、http.get→NET、
makeChan/send/recv→CHANNEL、extern→FFI、go 语句→SPAWN），沿调用图 fixpoint 传播；
函数字面量效果归属定义所在函数（保守近似）。FS/TIME/RANDOM 为保留枚举——当前无生产者。

## 4. ID 稳定性

- 同一源码内容 + 同一工具版本：SymbolID / NodeID / 全部 JSON 输出**逐字节稳定**（测试锁定）。
- 跨编辑版本不承诺 ID 不变（v0.4 明示；节点为插入式编号，前插会移位）。
- 禁止暴露内存地址或 map 迭代序。

## 5. 确定性与兼容

- 输出按结构体序/显式排序，`encoding/json` 稳定序列化。
- 文本子命令输出不受本协议影响。
- 本协议按公共 API 管理：变更须更新 schemas/ 与本文件，并走 golden 再生流程。

## 6. 编辑事务（v0.5 Safe Editing）

```text
inspect → plan → precondition check → edit → format → parse → resolve → typecheck → test → verify
```

- 编辑载体：`TextEdit{file, startOffset, endOffset, replacement}`（rune 偏移，与 SourceSpan 同度量）；
  事务为 `WorkspaceEdit{edits[], preconditions[]}`，precondition 含 `fileHash`（sha256 hex）。
- 前置校验失败（源码已变化）→ 拒绝写入；重叠区间 → 拒绝；缺前置校验 → 拒绝。
- `lang-go fix --safe` 内置闭环：analyze → plan → precondition → apply → parse →
  resolve → typecheck；任一步失败输出结构化 error（`stage: apply|verify`）并退出码 1。
  `--dry-run` 只输出 WorkspaceEdit 不落盘。
