# lang-go v1.0

一门确定性、类型化、Agent 可操作的编程语言：源码可转译到 Go 与 JS 执行，
拥有规范化的静态与动态语义、独立第二实现验证、151 个跨实现 Conformance Case。

```
Human 写起来自然 → Compiler 裁决严格 → Agent 精确操作 → 实现由 Conformance 裁决
```

## 安装

### 从源码构建

```bash
git clone https://github.com/ggbdpq/E-ggbdpq-gh-language.git
cd language/impls/go
go build -o lang-go ./cmd/lang-go
```

### 运行前置

| 后端 | 需要 |
|------|------|
| 解释器 / →Go | Go ≥ 1.21 |
| →JS | Node ≥ 18 |
| conformance | 两者都要 |

## 10 分钟快速上手

```bash
mkdir hello && cd hello
cat > main.lang-go <<'EOF'
fn main() {
    println("hello, lang-go")
}
EOF
lang-go run          # hello, lang-go
lang-go check        # 静态检查通过
lang-go build        # 生成 out/main.go（Go）
lang-go build js     # 生成 out/main.mjs（JS）
lang-go fmt          # 格式化
lang-go verify       # 全管线 + 四路差分验证
```

## 仓库结构

| 目录 | 用途 |
|------|------|
| **语言实现** | 四个独立实现，收在 `impls/` 下 |
| `impls/go/` | Go 参考实现（resolver → checker → HIR → IR + interp/gen/genjs/irinterp + contract/refactor） |
| `impls/typescript/` | TypeScript 独立实现（同套件 151/151 全绿） |
| `impls/python/` | Python 独立实现（151/151 全绿） |
| `impls/rust/` | Rust 独立实现（151×4 四路差分全绿，L6 Full Independent Parity） |
| **语言契约** | 跨实现共同真值 |
| `specs/` | **唯一语义真值源**（11 主题规范 + Rule Catalog + versioning + performance） |
| `conformance/` | **跨实现契约**（151 case，YAML manifest，四路差分） |
| `schemas/` | Agent Protocol JSON Schema |
| **资料与基建** | |
| `docs/` | 开发文档（计划 / ADR / 发布报告） |
| `dogfood/` | 真实项目验证（langalgo 算法库 1953 行） |
| `examples/` | 示例程序 |

## CLI 命令

| 命令 | 用途 |
|------|------|
| `lang-go run` | 运行当前目录 main.lang-go |
| `lang-go test` | 运行 test* 函数 |
| `lang-go check --json` | 静态检查（稳定码 + ruleId） |
| `lang-go fmt` | 格式化（幂等 + 注释 100% 保留） |
| `lang-go build [js]` | 转译到 Go / JS |
| `lang-go fix --safe` | 机械安全修复（事务闭环） |
| `lang-go verify [--json]` | 全管线 + 四路差分验证 |
| `lang-go inspect ... --json` | 语义图查询（symbols/callers/impact/effects…） |
| `lang-go refactor ... --json` | 语义重构（rename / organize imports / safe remove） |
| `lang-go dump ... --json` | AST / HIR / IR 输出 |

## 版本

v1.0.0（稳定版）。兼容策略见 specs/versioning.md。
Rule ID 与诊断码为跨实现契约：一经发布不可改名、不可复用、不可改义。
