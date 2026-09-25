#!/usr/bin/env bash
# install.sh — 一键安装全部依赖并构建
# 用法: bash scripts/install.sh [--skip-ts] [--skip-py]
#   --skip-ts  跳过 TypeScript（pnpm 不可用时）
#   --skip-py  跳过 Python（pip 不可用时）

set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SKIP_TS=false
SKIP_PY=false
for arg in "$@"; do
    case "$arg" in
        --skip-ts) SKIP_TS=true ;;
        --skip-py) SKIP_PY=true ;;
    esac
done

echo "=== lang-go 依赖安装与构建 ==="
echo "根目录: $ROOT"
echo ""

FAIL=0

# —— Go 参考实现 ——
echo "--- [1/3] Go 参考实现 ---"
cd "$ROOT/impls/go"
if command -v go &>/dev/null; then
    echo "[go] go mod download"
    go mod download
    echo "[go] go build"
    go build -o lang-go.exe ./cmd/lang-go
    echo "[go] 构建成功: impls/go/lang-go.exe"
    echo "[go] 运行测试..."
    go test ./... -count=1 2>&1 | grep -cE "^ok" | xargs -I{} echo "[go] {} 个包测试绿"
else
    echo "[go] 错误: go 命令不可用"
    FAIL=1
fi
echo ""

# —— TypeScript 独立实现 ——
if $SKIP_TS; then
    echo "--- [2/3] TypeScript（跳过） ---"
else
    echo "--- [2/3] TypeScript 独立实现 ---"
    cd "$ROOT/impls/typescript"
    if command -v pnpm &>/dev/null; then
        echo "[ts] pnpm install"
        pnpm install
    elif command -v npm &>/dev/null; then
        echo "[ts] npm install（pnpm 不可用，降级 npm）"
        npm install
    else
        echo "[ts] 错误: pnpm/npm 均不可用"
        FAIL=1
        echo ""
        cd "$ROOT/impls/go"
        if [ -f lang-go.exe ]; then
            echo "[go] 继续 Go 侧..."
        else
            exit 1
        fi
    fi
    if command -v npx &>/dev/null; then
        echo "[ts] tsc build"
        npx tsc -p tsconfig.json
        echo "[ts] 构建成功: impls/typescript/dist/cli/main.js"
    fi
fi
echo ""

# —— Python 独立实现 ——
if $SKIP_PY; then
    echo "--- [3/3] Python（跳过） ---"
else
    echo "--- [3/3] Python 独立实现 ---"
    cd "$ROOT/impls/python"
    if command -v pip &>/dev/null; then
        echo "[py] pip install -e ."
        pip install -e . --quiet 2>&1 | tail -1 || echo "[py] pip install 失败（骨架期实现，可忽略）"
    elif command -v pip3 &>/dev/null; then
        echo "[py] pip3 install -e ."
        pip3 install -e . --quiet 2>&1 | tail -1 || echo "[py] pip3 install 失败（骨架期实现，可忽略）"
    else
        echo "[py] 跳过（pip 不可用）"
    fi
fi
echo ""

# —— 验证 ——
echo "--- 验证 ---"
cd "$ROOT/impls/go"
if [ -f lang-go.exe ]; then
    VER=$(./lang-go.exe version)
    echo "lang-go CLI: $VER"
fi
if [ -f "$ROOT/impls/typescript/dist/cli/main.js" ]; then
    echo "lang-ts CLI: impls/typescript/dist/cli/main.js 可用"
fi

echo ""
if [ $FAIL -ne 0 ]; then
    echo "=== 安装完成（部分失败，见上方日志） ==="
    exit 1
fi
echo "=== 安装完成 ==="
echo "下一步: bash scripts/clean.sh --deep && bash scripts/install.sh 可从零重建全部依赖"
