#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 🔍 scripts/check-ide.sh — IDE 风格检查（秒级·匹配 VS 编辑器绿下划线）
# ═══════════════════════════════════════════════════════════════
# 用法: bash scripts/check-ide.sh
# 等价: VS 中 Error List 窗口的 "Message" 级别 IDE 建议
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "═══ IDE 风格检查（VS 编辑器绿下划线等价） ═══"
dotnet format style --verify-no-changes 2>&1 | tail -5
echo ""
echo "═══ IDE 分析器检查（VS 灯泡建议等价） ═══"
dotnet format analyzers --verify-no-changes 2>&1 | tail -5
