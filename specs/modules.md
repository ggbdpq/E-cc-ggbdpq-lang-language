# 模块规范（MODULE）

## MODULE-ROOT-001 模块根

放 `lang-go.toml` 的目录为模块根（v0.9 该文件仅作标记）。运行/检查以**目录即包**：当前目录为包根。

## MODULE-IMPORT-002 import 解析

`import "./x"` 仅支持同模块相对路径：从**当前文件所在目录**解析，目标为 `x.lang-go` 文件或 `x/` 目录（目录包 = 目录内全部 .lang-go 按文件名序合并）。越出模块根 → 装载错误。非 `./` 前缀 → 装载错误。

## MODULE-MERGE-003 合并与命名空间

import 递归展开后合并为单一程序、单一顶层命名空间（NAME-MERGE-010）；导入的符号直接以原名可见（无限定符）。合并顺序：被导入文件先于导入者，同目录按文件名序。

## MODULE-CYCLE-004 循环与菱形

循环导入 → 装载错误；菱形导入（A→B→C、A→D→C）去重，C 只载入一次。

## MODULE-INIT-005 初始化顺序

顶层 `let`/`var` 与顶层语句按合并顺序（MODULE-MERGE-003）依次求值/执行；fn/struct/enum 声明提升（先于任何初始化）。此序为规范行为（可观察：顶层 println 的输出顺序）。

## MODULE-VIS-006 可见性

v0.9 无 `pub` 语法：包内全部顶层声明对 import 方可见（导出面 = 全部顶层声明）。收窄可见性列入 v1.0 后演进候选（需要时经 ADR 增补语法）。

## MODULE-TEST-007 测试约定

`*_test.lang-go` 仅 `lang-go test` 装载；`test` 前缀无参函数自动执行，`assert` 失败 → 退出码 1。普通 run 不装载测试文件。
