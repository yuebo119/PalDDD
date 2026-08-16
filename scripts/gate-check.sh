#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/src"; DOCS="$ROOT/docs"
PASS=0; FAIL=0
check() {
  local d="$1" a="${2:-0}" e="$3"
  if [ "$a" = "$e" ]; then echo "  ✅ $d: $a"; PASS=$((PASS+1))
  else echo "  ❌ $d: $a (期望$e)"; FAIL=$((FAIL+1)); fi
}
echo "═══ 门禁 ═══"

check "G1 异常sealed" "$(grep -r 'public.*class.*Exception' "$SRC" --include="*.cs" -l | xargs grep -L 'sealed\|abstract' 2>/dev/null | grep -v 'Middleware\|Extensions' | wc -l | tr -d ' ')" "0"

c=0
find "$SRC" -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" -not -path "*SourceGen*" -not -path "*Analyzers*" | while read f; do
  first=$(head -1 "$f" 2>/dev/null)
  case "$first" in
    ""|"using "*|"namespace "*) ;;
    "//"*) ;;
    *) echo "$f";;
  esac
done | wc -l > /tmp/g2_count.txt
check "G2 文件头" "$(cat /tmp/g2_count.txt 2>/dev/null | tr -d ' ' || echo 0)" "0"

# ITM-172 修复：G3 由硬编码 "0" "0" 恒过改为真实检查——
# src/ 下 .cs 文件名只允许 [A-Za-z0-9._-]（PascalCase 命名约定，排除 bin/obj）
g3_bad=$(find "$SRC" -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" \
  | while read -r f; do
      n=$(basename "$f")
      case "$n" in
        *[!A-Za-z0-9._-]*) echo "$f";;
      esac
    done | wc -l | tr -d ' ')
check "G3 文件命名" "$g3_bad" "0"

echo "═══ $PASS/$FAIL ═══"
# ITM-172 修复：原 `[ "$FAIL" -gt 0 ] && exit 1` 在 FAIL=0 时 AND-list 返回 1，
# 导致"全绿却退出码 1"（CI 回退门禁必挂）——显式 if 修正
if [ "$FAIL" -gt 0 ]; then
    exit 1
fi
exit 0
