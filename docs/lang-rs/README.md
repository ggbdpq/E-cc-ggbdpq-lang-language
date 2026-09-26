# lang-rs v1.0 独立实现计划包

本计划根据 2026-09-25 最新 `language` monorepo 重新制定。

关键基线：

```text
impls/go
impls/typescript
impls/python
impls/rust   ← 新实现
```

语言版本：`v1.0.0`

共享 Conformance：151 cases

最高裁决：

```text
specs > Rule Catalog > conformance > implementations
```

阅读顺序：

1. `docs/lang-rs/2026-09-25-lang-rs-基线快照.md`
2. `docs/lang-rs/2026-09-25-lang-rs-完整独立实现计划.md`
3. `docs/lang-rs/2026-09-25-lang-rs-完整复刻实施提示词.md`
4. `docs/lang-rs/2026-09-25-lang-rs-验收矩阵模板.md`
5. `docs/lang-rs/2026-09-25-lang-rs-Ownership-Runtime决策模板.md`
6. `docs/lang-rs/2026-09-25-lang-rs-SPEC冲突处理模板.md`
7. `docs/lang-rs/2026-09-25-lang-rs-对抗验证重点.md`

重要修正：

上一版计划中曾把 SourceSpan offset 写成 UTF-8 byte offset。

当前正式规范明确：

```text
offset = 0 基 Unicode code point / rune
line   = 1 基
column = 1 基
```

Rust 实现必须建立 byte ↔ code-point 映射，不能直接暴露 String byte index。

开发阶段继续采用 Full-Parity Working Tree，不提交 Git。完成后只生成推荐提交计划。
