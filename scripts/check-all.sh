#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 🔍 scripts/check-all.sh — 全量检查（IDE+CA+编译）
# ═══════════════════════════════════════════════════════════════
# 用法: bash scripts/check-all.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "═══════ 1/3 IDE 风格 ═══════"
# ITM-172 修复：grep -c 零匹配返回 1，set -euo pipefail 下会中止脚本——
# 计数与输出拆两步，grep 用 || true 兜底
IDE_COUNT=$(dotnet format style --verify-no-changes PalDDD.slnx 2>&1 | grep -c "error\|warning" || true)
echo "  IDE 建议: $IDE_COUNT 项"

echo "═══════ 2/3 CA 分析 ═══════"
dotnet build PalDDD.slnx -c Debug --nologo 2>&1 | tail -3

echo "═══════ 3/3 编译 ═══════"
ERROR_COUNT=$(dotnet build PalDDD.slnx -c Debug --nologo 2>&1 | grep -c "error CS" || true)
echo "  编译错误: $ERROR_COUNT 项"
echo "═══ 完成 ═══"
