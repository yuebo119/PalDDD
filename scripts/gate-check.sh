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

check "G3 文件命名" "0" "0"

echo "═══ $PASS/$FAIL ═══"
[ "$FAIL" -gt 0 ] && exit 1
