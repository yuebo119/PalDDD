#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 🔬 refine-scan.sh — Pal.DDD 精炼扫描 v7（24项）
# ═══════════════════════════════════════════════════════════════
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/src"

echo "═══════ 一类:减法 ═══════"
echo "A1 AssemblyInfo: $(find "$SRC" -name AssemblyInfo.cs -not -path '*/obj/*' | wc -l | tr -d ' ')"
echo "A2 GlobalUsings: $(find "$SRC" -name GlobalUsings.cs -not -path '*/obj/*' | wc -l | tr -d ' ')"
echo "A3 标记接口(≤10行): $(find "$SRC" -name '*.cs' -not -path '*/obj/*' -exec wc -l {} \; 2>/dev/null | awk '$1<=10 && $1>0' | wc -l | tr -d ' ')"
echo "A5 冗余using密度: $(grep -rn '^using ' "$SRC" --include='*.cs' 2>/dev/null | wc -l | tr -d ' ') 行"

echo ""
echo "═══════ 二类:现代化 ═══════"
echo "M1 集合表达式: $(grep -rn 'new List<()\|new Dictionary<()\|Array.Empty' "$SRC" --include='*.cs' 2>/dev/null | grep -cv 'Frozen\|obj/' || echo 0)"
echo "M2 主构造函数: $(grep -rn 'private readonly.*=.*?? throw' "$SRC" --include='*.cs' 2>/dev/null | grep -cv 'obj/' || echo 0)"
echo "M3 required: $(grep -rn '{ get; }' "$SRC" --include='*.cs' 2>/dev/null | grep 'public' | grep -cv 'set\|init\|static\|=>' || echo 0)"
echo "M5 string.Format: $(grep -rn 'string\.Format\|String\.Format' "$SRC" --include='*.cs' 2>/dev/null | grep -cv 'obj/' || echo 0)"
echo "M6 ThrowIfNull: $(grep -rn 'throw new ArgumentNullException' "$SRC" --include='*.cs' 2>/dev/null | grep -cv '///\|Suppress\|obj/' || echo 0)"

echo ""
echo "═══════ 三类:优化 ═══════"
echo "O1 FrozenDictionary: $(grep -rn 'new Dictionary<' "$SRC" --include='*.cs' 2>/dev/null | grep -cv 'Frozen\|obj/' || echo 0)"
echo "O2 无预分配: $(grep -rn 'new List<()\|new Dictionary<()' "$SRC" --include='*.cs' 2>/dev/null | grep -cv 'obj/' || echo 0)"
echo "O3 ToArray/ToList: $(grep -rn '\.ToArray()\|\.ToList()' "$SRC" --include='*.cs' 2>/dev/null | grep -cv 'obj/\|InMemory\|Test' || echo 0)"
echo "O6 string+=: $(grep -rn '+= .*\"' "$SRC" --include='*.cs' 2>/dev/null | grep -cv 'obj/\|StringBuilder' || echo 0)"

echo ""
echo "═══ 精炼完成 ═══"
