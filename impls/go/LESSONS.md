# Lessons

规则长自真实失败：每条教训 = 规则 + 为什么 + 证据（文件/commit/复现方式）+ Seen N×。
同一条教训在 3 个不同日期重现，升格为 AGENTS.md 必查项。
从第一条真实教训开始记，不预写占位条目。

## 活跃教训

- 规则：输出设备错误必须从 `Run` 返回。为什么：忽略 `fmt.Fprint/Fprintln` 的错误会把输出失败报告成运行成功。证据：`internal/irinterp/irinterp_test.go:TestRunPropagatesOutputError`；旧代码 `print` / `println` 均报 `Run() error = <nil>, want output unavailable`。Seen 1×（2026-10-10）。
- 规则：控制流块的转译错误必须逐层返回。为什么：遗漏 `emitBlock` 的错误使非法嵌套语句产生残缺 JS，且顶层/嵌套语义不一致。证据：`internal/genjs/genjs_test.go:TestTranspileJSPropagatesBlockError`；旧代码 `if_then` / `if_else` / `while_body` 均返回 `<nil>`，顶层正确报错。Seen 1×（2026-10-10）。

- 规则：诊断提取与宿主错误包装必须保留 error 链。为什么：裸类型断言丢掉包装后的 `ErrorList`，`%v` 把 JSON 的宿主错误变成不可识别的文本。证据：`TestDiagnosticsPreservesWrappedErrors/wrapped` 旧代码 `count = 0, want 1`；`TestJSONParsePreservesSyntaxError` 与 `TestJSONStringifyPreservesValueError` 旧包装无法匹配对应 JSON 错误类型。Seen 1×（2026-10-10）。

- 规则：单类别自动修复后先编译，补齐新 API 必需的 import。为什么：为隔离格式器使用 `--no-config --enable-only errorlint --fix` 时，替换成 `errors.As` 不会自动添加 `errors`。证据：`internal/irinterp/irinterp.go` 编译报 `undefined: errors`，补充标准库 import 后恢复。Seen 1×（2026-10-10）。
