#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 🚧 changelog-check.sh — 变更日志结构门禁（release.md §十二 Phase 4）
# ═══════════════════════════════════════════════════════════════
#
# 用法：
#   bash scripts/changelog-check.sh            # 校验当前工作树
#
# 挂接点（docs/release.md）：
#   §4.1 本地必跑（发布前验证清单）+ §5.1 打 tag 前步骤 2.5
#   本门禁 FAIL 时禁止打 tag（CHANGELOG 先行纪律的机械强制，§九教训 1/2）
#
# 检查项：
#   C1 CHANGELOG 首个版本段必须是 [Unreleased]
#   C2 当前 VersionPrefix 若已存在同名 tag → 必须已有 [版本号] 转正段（防"未转正先打 tag"）
#   C3 最新发布段分类层顺序符合 §11.2（Added→Changed→Deprecated→Removed→Fixed→Security→Dependencies→Documentation→Tests）
#   C4 Tests 段禁止预估口径数字（"约 N"/"~N"/"预计"——§11.3 规则 1 只允许实测值）[WARN 不阻断]
#   C5 分类层禁内部术语泄漏（ITM-\d+ / PDDD\d+ / PAL(ID|MSG|ENUM)\d+ / v\d+ 轮次引用）[WARN 不阻断——分类层=最新发布段至附录之间]
# ═══════════════════════════════════════════════════════════════
set -euo pipefail
cd "$(dirname "$0")/.."

CHANGELOG="CHANGELOG.md"
FAIL=0
WARN=0
# P3 修复：通过数按实际执行的检查项累加——原汇总行 `$((4+1-WARN-FAIL))` 硬编码 5 项上限，
# 而 C4/C5 在"无已发布段/无附录"时整段跳过（既不 pass 也不 warn/fail），通过数虚报。
PASS=0

pass() { echo "PASS C$1: ${2:-}"; PASS=$((PASS+1)); }
fail() { echo "FAIL C$1: ${2:-}"; FAIL=$((FAIL+1)); }
warn() { echo "WARN C$1: ${2:-}"; WARN=$((WARN+1)); }

# ── C1 首个版本段 = [Unreleased] ─────────────────────────────
FIRST=$(grep -m1 -E '^## \[' "$CHANGELOG" || true)
if [ "$FIRST" = "## [Unreleased]" ]; then
    pass "1 CHANGELOG 首个版本段为 [Unreleased]"
else
    fail "1 CHANGELOG 首个版本段为 '$FIRST'——应为 [Unreleased]（[Unreleased] 段缺失即违反 §11.3 规则 5）"
fi

# ── C2 同名 tag 已存在 → 必须已转正 ──────────────────────────
VERSION=$(grep -oP '(?<=<VersionPrefix>)[^<]+' Directory.Build.props | head -1)
if [ -z "$VERSION" ]; then
    fail "2 无法从 Directory.Build.props 解析 VersionPrefix"
else
    if git rev-parse --verify --quiet "v${VERSION}^{commit}" >/dev/null 2>&1; then
        if grep -qE "^## \[${VERSION}\]" "$CHANGELOG"; then
            pass "2 版本 ${VERSION} 的 tag 已存在且 [${VERSION}] 段已转正"
        else
            fail "2 版本 ${VERSION} 已有本地 tag 但 CHANGELOG 无 [${VERSION}] 转正段——禁止打 tag（§九教训 2：v2.0.0 未转正实录）"
        fi
    else
        pass "2 版本 ${VERSION} 尚无 tag（[Unreleased] 累积期，转正义务在打 tag 时点）"
    fi
fi

# ── C3 最新发布段分类顺序（§11.2 固定顺序）────────────────────
ORDER=(Added Changed Deprecated Removed Fixed Security Dependencies Documentation Tests)
LATEST_LINE=$(grep -nE '^## \[' "$CHANGELOG" | grep -v 'Unreleased' | head -1 | cut -d: -f1)
if [ -z "$LATEST_LINE" ]; then
    warn "3 CHANGELOG 无已发布版本段（首次发布前），跳过顺序检查"
else
    APPENDIX_LINE=$(awk -v s="$LATEST_LINE" 'NR>s && /^### 附录/{print NR; exit}' "$CHANGELOG")
    END_LINE=${APPENDIX_LINE:-$(grep -nE '^## \[' "$CHANGELOG" | awk -F: -v s="$LATEST_LINE" '$1>s{print $1; exit}' | head -1)}
    [ -n "$END_LINE" ] || END_LINE=$(wc -l < "$CHANGELOG")
    SECTIONS=$(sed -n "${LATEST_LINE},${END_LINE}p" "$CHANGELOG" | grep -oE '^### [A-Za-z]+' | sed 's/^### //' || true)
    SEQUENCED=true
    PREV_IDX=0
    for s in $SECTIONS; do
        IDX=-1
        for i in "${!ORDER[@]}"; do [ "${ORDER[$i]}" = "$s" ] && IDX=$i && break; done
        if [ "$IDX" -lt 0 ]; then continue; fi   # 非标准分类（如中文主题段）跳过
        if [ "$IDX" -lt "$PREV_IDX" ]; then SEQUENCED=false; break; fi
        PREV_IDX=$IDX
    done
    if [ "$SEQUENCED" = true ]; then
        pass "3 最新发布段分类顺序符合 §11.2"
    else
        fail "3 最新发布段分类顺序违反 §11.2 固定顺序（Added→Changed→Deprecated→Removed→Fixed→Security→Dependencies→Documentation→Tests），实际: $(echo $SECTIONS | tr '\n' ' ')"
    fi
fi

# ── C4 Tests 段预估口径（WARN——数字真伪仍需人工对照实测，机械只能拦措辞）──
if [ -n "${LATEST_LINE:-}" ]; then
    # 先剔除"基线约 N"合法形态（历史基线允许约数），再匹配剩余预估措辞
    VAGUE=$(sed -n "${LATEST_LINE},${END_LINE:-\$}p" "$CHANGELOG" | sed -n '/^### Tests/,/^### \|^## [^[]/p' | sed -E 's/基线约 ?[0-9]+//g' | grep -E '约 ?[0-9]+|[~～][0-9]+|预计' || true)
    if [ -n "$VAGUE" ]; then
        warn "4 Tests 段含疑似预估口径（§11.3 规则 1 要求实测/精确推导值，请校准）: $(echo "$VAGUE" | head -2)"
    else
        pass "4 Tests 段无预估口径措辞"
    fi
fi

# ── C5 分类层内部术语泄漏（WARN——分类层 = 最新发布段至附录之间）──
# 注意：PDDD0xx/PALxxx 是公开分析器诊断 ID（消费者在构建警告中可见），属合法消费者术语，不拦
if [ -n "${LATEST_LINE:-}" ] && [ -n "${APPENDIX_LINE:-}" ]; then
    LEAK=$(sed -n "${LATEST_LINE},${APPENDIX_LINE}p" "$CHANGELOG" | grep -nE 'ITM-[0-9]+|(v[0-9]{2} 轮)|评审片|探针双红' | head -3 || true)
    if [ -n "$LEAK" ]; then
        warn "5 分类层疑似内部术语泄漏（应外置到附录层）: $(echo "$LEAK" | head -2)"
    else
        pass "5 分类层无内部术语泄漏"
    fi
fi

echo "═══ 结果: ${PASS} 通过 / ${WARN} 警告 / ${FAIL} 失败 ═══"
[ "$FAIL" -eq 0 ] || exit 1
