#!/usr/bin/env bash
# clean.sh — 一键清除全部依赖与缓存
# 用法: bash scripts/clean.sh [--deep]
#   默认: 清除构建产物与包目录（可安全重装）
#   --deep: 额外清除 Go 模块缓存 / 构建缓存 / pip 缓存 / pnpm store（不可逆，重装需重新下载）

set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEEP=false
[[ "${1:-}" == "--deep" ]] && DEEP=true

echo "=== lang-go 依赖与缓存清理 ==="
echo "根目录: $ROOT"
$DEEP && echo "模式: 深度清理（含工具链缓存）" || echo "模式: 标准清理"

# —— TypeScript（pnpm）——
if [ -d "$ROOT/impls/typescript/node_modules" ]; then
    echo "[typescript] 删除 node_modules/"
    rm -rf "$ROOT/impls/typescript/node_modules"
fi
if [ -d "$ROOT/impls/typescript/dist" ]; then
    echo "[typescript] 删除 dist/"
    rm -rf "$ROOT/impls/typescript/dist"
fi
find "$ROOT/impls/typescript/src" -name "*.tsbuildinfo" -delete 2>/dev/null || true

# —— Python（pip）——
find "$ROOT/python" -type d -name "__pycache__" -exec rm -rf {} + 2>/dev/null || true
find "$ROOT/python" -type d -name "*.egg-info" -exec rm -rf {} + 2>/dev/null || true
if [ -d "$ROOT/impls/python/.venv" ]; then
    echo "[python] 删除 .venv/"
    rm -rf "$ROOT/impls/python/.venv"
fi
if [ -d "$ROOT/impls/python/.tmp-build" ]; then
    echo "[python] 删除 .tmp-build/"
    rm -rf "$ROOT/impls/python/.tmp-build"
fi

# —— Go ——
if [ -d "$ROOT/impls/go/lang-go.exe" ]; then
    rm -f "$ROOT/impls/go/lang-go.exe"
fi
if [ -d "$ROOT/impls/go/out" ]; then
    rm -rf "$ROOT/impls/go/out"
fi

# —— langalgo / dogfood 构建产物 ——
if [ -d "$ROOT/dogfood/langalgo/out" ]; then
    rm -rf "$ROOT/dogfood/langalgo/out"
fi

# —— 通用垃圾 ——
find "$ROOT" -type f -name "*.tsbuildinfo" -delete 2>/dev/null || true
find "$ROOT" -type d -name ".pytest_cache" -exec rm -rf {} + 2>/dev/null || true

# —— 深度清理（工具链全局缓存）——
if $DEEP; then
    echo ""
    echo "--- 深度清理（不可逆，重装需重新下载） ---"
    echo "[go] 清除构建缓存 + 测试缓存"
    go clean -cache -testcache 2>/dev/null || echo "  (go 不可用，跳过)"
    echo "[go] 清除模块缓存"
    go clean -modcache 2>/dev/null || echo "  (go 不可用，跳过)"
    echo "[pip] 清除缓存"
    pip cache purge 2>/dev/null || pip3 cache purge 2>/dev/null || echo "  (pip 不可用，跳过)"
    if command -v pnpm &>/dev/null; then
        echo "[pnpm] 清除 store"
        pnpm store prune 2>/dev/null || echo "  (pnpm store 清除失败，跳过)"
    fi
fi

echo ""
echo "=== 清理完成 ==="
$DEEP || echo "提示: 使用 --deep 可额外清除 Go/pip/pnpm 工具链缓存"
