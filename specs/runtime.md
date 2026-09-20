# 运行时、效果与 FFI 规范（RUNTIME / EFFECT / FFI）

## RUNTIME-FAIL-001 运行时失败分类

以下情形为**运行时错误**：程序向 stderr 输出错误消息并以非零退出码终止（无栈回溯约定，消息文本允许实现差异）。分类与触发点：

| 类别 | 触发 |
|------|------|
| RUNTIME-INDEX-001 | 数组/字符串索引越界、切片端点非法；映射取不存在键（`has()` 探测是唯一合法探测方式） |
| RUNTIME-ARITH-002 | 整数除零/模零 |
| RUNTIME-EQ-003 | `==`/`!=` 作用于数组/映射/struct（仅标量与 enum 可比） |
| RUNTIME-ASSERT-004 | `assert(cond, msg?)` 条件为 false |
| RUNTIME-CHAN-001 | send 到满 channel |
| RUNTIME-CHAN-002 | 同步语义下 recv 空 channel（程序死锁，CHAN-RECV-003） |
| RUNTIME-MATCH-001 | 字面量 match 无臂匹配（MATCH-EXHAUSTIVE-002 的运行时面） |
| RUNTIME-TYPE-005 | any 边界断言失败（收窄到与静态事实矛盾的类型） |
| RUNTIME-JSON-001 | `jsonParse` 非法 JSON |

## RUNTIME-JSON-002 JSON 确定性

`jsonParse`：数字按整/浮分型，null → `None`，对象 → 映射（键按字母序载入），数组 → 数组。`jsonStringify`：映射键按**字母序**输出、None → null——两端逐字节确定，是跨实现硬契约。

## RUNTIME-OUTPUT-003 输出面

`println` 空格分隔 + 换行；`print` 无换行。标量格式化：int 十进制、float 无尾零（`1.0` → `1`）、bool `true/false`、字符串顶层不带引号（嵌套带引号）、enum `Tag(载荷)`/`Tag`、`Some(v)`/`None`/`Ok(v)`/`Err(e)`、`error{msg: "..."}`、struct `{field: value, ...}`、数组 `[a, b]`、映射 `{k: v}`（插入序）。格式化细节由 conformance case 逐条锁定。

## EFFECT-001 效果面

效果（PURE/IO/FS/NET/TIME/RANDOM/SPAWN/CHANNEL/FFI）从 IR 推导（唯一推导点：graph.Effects），供 Agent impact 查询与 effectWatch。v0.9 效果是**元数据**，不是约束系统（无效果类型注解、无静态效果检查——列入冻结外演进）。

## FFI-001 extern 冻结

`extern "go" { fn 签名 }` 声明宿主函数：解释器侧调用即报「仅转译可用」；→Go 侧生成 panic 桩（链接由 prelude/人工提供）。`lang-go bind` 生成声明草案（需人工审核）。FFI 面不进入 Conformance（平台绑定），仅语义存在性锁定。
