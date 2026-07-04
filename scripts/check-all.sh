#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 🔍 scripts/check-all.sh — 全量检查（IDE+CA+编译）
# ═══════════════════════════════════════════════════════════════
# 用法: bash scripts/check-all.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "═══════ 1/3 IDE 风格 ═══════"
dotnet format style --verify-no-changes PalDDD.slnx 2>&1 | grep -c "error\|warning" | xargs -I{} echo "  IDE 建议: {} 项"

echo "═══════ 2/3 CA 分析 ═══════"
dotnet build PalDDD.slnx -c Debug --nologo 2>&1 | tail -3

echo "═══════ 3/3 编译 ═══════"
dotnet build PalDDD.slnx -c Debug --nologo 2>&1 | grep -c "error CS" | xargs -I{} echo "  编译错误: {} 项"
echo "═══ 完成 ═══"
