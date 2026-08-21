#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 🔍 scripts/check-all.sh — 全量检查（IDE+CA+编译）
# ═══════════════════════════════════════════════════════════════
# 用法: bash scripts/check-all.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "═══════ 1/3 IDE 风格 ═══════"
# ITM-233 修复：dotnet format 退出码必须传播——此前 grep -c || true 把 format 失败吞掉
FORMAT_OUTPUT=$(dotnet format 2>&1) || FORMAT_EXIT=$?
FORMAT_EXIT=${FORMAT_EXIT:-0} style --verify-no-changes PalDDD.slnx 2>&1)
FORMAT_EXIT=$?
IDE_COUNT=$(printf '%s\n' "$FORMAT_OUTPUT" | grep -c "error\|warning" || true)
echo "  IDE 建议: $IDE_COUNT 项"
if [ "$FORMAT_EXIT" -ne 0 ]; then
    echo "  ❌ dotnet format 退出码 $FORMAT_EXIT——存在格式违规"
    exit 1
fi

echo "═══════ 2/3 CA 分析 ═══════"
dotnet build PalDDD.slnx -c Debug --nologo 2>&1 | tail -3

echo "═══════ 3/3 编译 ═══════"
ERROR_COUNT=$(dotnet build PalDDD.slnx -c Debug --nologo 2>&1 | grep -c "error CS" || true)
echo "  编译错误: $ERROR_COUNT 项"
echo "═══ 完成 ═══"
