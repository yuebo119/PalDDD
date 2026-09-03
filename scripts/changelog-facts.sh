#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 📋 changelog-facts.sh — 变更日志事实收集器（release.md §十二 Phase 1）
# ═══════════════════════════════════════════════════════════════
#
# 用法：
#   bash scripts/changelog-facts.sh <from-tag> [to-ref=HEAD]
#   bash scripts/changelog-facts.sh v2.1.0          # 收集 v2.1.0..HEAD 事实
#
# 定位（docs/release.md §十一/§十二）：
#   只收集「机械可验证事实」，不生成最终文案——文案由起草者按 §11.4 模板
#   基于本清单撰写。每条事实带可复查命令，禁止在事实清单之外的来源编造条目。
#
# 产出段：
#   1 范围与提交分布        5 废弃扫描
#   2 公共 API 变更         6 ADR 与文档增删
#   3 新增分析器诊断        7 依赖变更
#   4 脚本/工作流增删       8 测试面板实测（提醒，不自动跑）
#   9 当前 [Unreleased] 段
# ═══════════════════════════════════════════════════════════════
set -euo pipefail
cd "$(dirname "$0")/.."

FROM="${1:?用法: bash scripts/changelog-facts.sh <from-tag> [to-ref]}"
TO="${2:-HEAD}"

git rev-parse --verify --quiet "$FROM^{commit}" >/dev/null || { echo "❌ from-tag 不存在: $FROM"; exit 1; }

echo "# 变更日志事实清单（${FROM}..${TO}）"
echo "> 生成: $(date '+%Y-%m-%d %H:%M') · 每条事实附可复查命令 · 文案撰写规范见 docs/release.md §十一/§十二"
echo

# ── 1 范围与提交分布 ──────────────────────────────────────────
COUNT=$(git log --oneline "${FROM}..${TO}" | wc -l | tr -d ' ')
TO_COMMIT=$(git log -1 --format='%h %s' "$TO")
FROM_COMMIT=$(git log -1 --format='%h' "$FROM")
echo "## 1 范围与提交分布"
echo "- 范围：${FROM}(${FROM_COMMIT}) → ${TO}（${TO_COMMIT%% *}），共 ${COUNT} 个提交"
echo "- 复查: git log --oneline ${FROM}..${TO}"
echo "- 类型分布（提交主题前缀，供 Fixed/Added 分族参考）："
git log --pretty=format:'%s' "${FROM}..${TO}" | sed -E 's/[：:].*//' | sort | uniq -c | sort -rn | sed 's/^/    /'
echo

# ── 2 公共 API 变更（快照 diff，核心 12 程序集口径）────────────
SNAP="test/PalDDD.Core.Tests/Snapshots/core-packages-public-api.txt"
echo "## 2 公共 API 变更（快照口径：核心 12 程序集；Analyzers 诊断见段 3）"
API_DIFF=$(git diff "${FROM}..${TO}" -- "$SNAP" | grep -E '^[+-]' | grep -vE '^[+-][+-]' | grep -v 'Version=2\.[0-9]' || true)
if [ -n "$API_DIFF" ]; then
    echo "$API_DIFF" | sed 's/^/    /'
    echo "  ⚠️ 删除行（-）= 签名变更/移除，必须逐条在 Added/Changed/Removed 分类交代"
else
    echo "    （无变更）"
fi
echo "- 复查: git diff ${FROM}..${TO} -- $SNAP | grep -v Version="
echo

# ── 3 新增分析器诊断（线索级，需打开 descriptor 核对语义）────────
echo "## 3 新增分析器诊断（线索，起草前须读 PalDiagnostics.cs 对应 descriptor）"
DIAG=$(git diff "${FROM}..${TO}" -- src/PalDDD.Core/PalDiagnostics.cs | grep -E '^\+.*PAL[A-Z]+[0-9]{3}' | grep -vE '^\+\+\+' | grep -oE 'PAL[A-Z]+[0-9]{3}' | sort -u || true)
if [ -n "$DIAG" ]; then echo "$DIAG" | sed 's/^/    /'; else echo "    （无新增）"; fi
echo

# ── 4 脚本 / 工作流增删 ───────────────────────────────────────
echo "## 4 脚本与工作流增删（A=新增 D=删除）"
WF=$(git diff --name-status "${FROM}..${TO}" -- scripts/ .github/workflows/ || true)
if [ -n "$WF" ]; then echo "$WF" | sed 's/^/    /'; else echo "    （无）"; fi
echo

# ── 5 废弃扫描 ───────────────────────────────────────────────
echo "## 5 废弃扫描（diff 新增 [Obsolete] 行——仅供定位，语义须读原文件核实）"
OBS=$(git diff "${FROM}..${TO}" -- src/ | grep -E '^\+.*\[Obsolete' | grep -vE '^\+\+\+' || true)
if [ -n "$OBS" ]; then
    echo "$OBS" | sed 's/^/    /'
    echo "  ⚠️ 废弃条目必须写明移除版本窗口与理由（§11.3 规则 3）"
else
    echo "    （无新增废弃）"
fi
echo

# ── 6 ADR 与文档增删 ─────────────────────────────────────────
echo "## 6 ADR 与文档增删"
DOCS=$(git diff --name-status "${FROM}..${TO}" -- docs/ *.md || true)
if [ -n "$DOCS" ]; then echo "$DOCS" | sed 's/^/    /'; else echo "    （无）"; fi
echo

# ── 7 依赖变更 ───────────────────────────────────────────────
echo "## 7 依赖变更（Directory.Packages.props + 适配层引用）"
DEPS=$(git diff "${FROM}..${TO}" -- Directory.Packages.props | grep -E '^[+-]' | grep -vE '^[+-][+-]' | grep -E 'PackageVersion|PalORM' || true)
if [ -n "$DEPS" ]; then echo "$DEPS" | sed 's/^/    /'; else echo "    （中央版本无变更——注意排查各 csproj 直接引用与 docs/release.md §二 依赖表）"; fi
echo "- 复查: git diff ${FROM}..${TO} -- '**/*.csproj' 'Directory.Packages.props'"
echo

# ── 8 测试面板实测（不自动跑——发布前验证阶段已有实测值）─────────
echo "## 8 测试面板实测值（§11.3 规则 1：只允许写实测数字，禁止预估）"
echo "  ▶ 发布前验证（§4.1）跑完后回填："
echo "    for p in \$(find test -name '*.Tests.csproj' | sort); do dotnet test \"\$p\" -c Release --no-restore --no-build --nologo; done"
echo "  ▶ 回填格式：16 项目总计 N = 本机通过 X + 环境依赖 Y（JSON 报告归类，CI Testcontainers 权威判定）；基线 = 上一版 Tests 段实测值"
echo

# ── 9 当前 [Unreleased] 段 ───────────────────────────────────
echo "## 9 当前 [Unreleased] 段（转正原料——按 §11.4 模板分类重写，不是原样搬运）"
sed -n '/^## \[Unreleased\]/,/^## \[/{/^## \[/!p}' CHANGELOG.md | sed '/^$/d' | head -40
echo "    （完整段自查: sed -n '/^## \\[Unreleased\\]/,/^## \\[/p' CHANGELOG.md）"
echo
echo "═══ 事实清单结束 → 按 docs/release.md §十二 Phase 2-4 核验/起草/校验 ═══"
