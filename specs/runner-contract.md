# lang-go Runner Contract

四套实现（`lang-go` Go / `lang-go-ts` TypeScript / `lang-go-py` Python / `lang-go-rs` Rust）
各提供一个可执行文件，遵守本文的统一接口。**输入一律为程序目录**（目录即包，
入口 `main.lang-go`），不接收零散文件；路径面与安全边界同 v0.1 约定。

跨实现验收标准：`conformance/` 套件（14 类 33 case，见 conformance/README.md）
全绿 + 本文五动词行为一致。

## 五动词

| 动词 | 接口 | 契约 |
|------|------|------|
| `compile <dir> --target go\|js` | 产物 + 退出码 | 产物可直接由宿主工具链执行；前端检查失败退出码 1 并输出诊断 |
| `check <dir> --json` | 契约 JSON | specs/agent-protocol.md 的 CheckJSON；诊断码/severity/primarySpan 不漂移 |
| `run <dir>` | stdout / stderr / exitCode | 与 conformance manifest 的 expect 逐字节可比；`fn main` 为入口 |
| `inspect <dir> --json` | 契约 JSON | symbols/refs/type/dependencies/effects/graph 查询面 |
| `verify <dir> [--json]` | 契约 JSON | parse → resolve → typecheck → hir → ir → effect → compile → execute → differential；失败输出失败阶段 + 已完成阶段 |

## 通用约束

1. **JSON 信封**：所有 `--json` 输出带 `schemaVersion` / `toolVersion` / `kind`
   （specs/agent-protocol.md）；字段只加不改不删，破坏性变更升版本。
2. **诊断稳定性**：码空间 E1xxx（词法）/ E2xxx（resolver）/ E3xxx（类型）/
   E4xxx（模式）跨实现共享；message 允许语言化差异。
3. **差分一致性**：同一合法程序在三后端的 stdout 逐字节一致、exitCode 一致、
   stderr 为空。例外仅限 spec 文档化的平台差异（当前唯一族：`go`/chan 的同步
   子集 vs 真 goroutine，spec §6.4）。
4. **确定性**：映射字面量保插入序；`jsonStringify` 键按字母序；诊断按
   （行，列，码）排序。任何实现不得引入未指定顺序。
5. **工具链边界**：compile/execute 依赖宿主工具链（go/node/…）时，缺失必须以
   结构化方式显式呈现（skipped），**不得静默跳过或谎报通过**。

## 版本状态

- v0.8：本文成文；Go 实现全部动词可用；TS/Python/Rust 侧 runner 属各自实现版本范围。
