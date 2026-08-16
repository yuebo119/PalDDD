#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 📸 review-snapshot.sh — 评审基线快照
# ═══════════════════════════════════════════════════════════════
#
# 用法：bash scripts/review-snapshot.sh
# 产出：评审报告首行所需的基线数据（commit + 项目/文件/测试数 + 关键计数）
# 目的：消除元审计 R3（过期快照）和 R8（采信记忆）—— 所有评审断言锚定同一快照
#
# 评审报告首行应粘贴本脚本输出：
#   > 评审基线：bash scripts/review-snapshot.sh 的输出
# ═══════════════════════════════════════════════════════════════

set -euo pipefail

# 位置无关仓库根发现（元审计脚本#1-3/GAP31 修复）：向上查找含 PalDDD.slnx 的目录——
# 同一文件在根 scripts/ 与 .ai/scripts/ 均可直接运行，杜绝搬移后 cd 层数不匹配的整体 no-op
_ai_root_find() { local d="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; while [ "$d" != "/" ] && [ ! -f "$d/PalDDD.slnx" ]; do d="$(dirname "$d")"; done; printf '%s' "$d"; }
ROOT="$(_ai_root_find)"
cd "$ROOT"

echo "═══ 评审基线快照 ═══"
echo "时间: $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
echo "Commit: $(git rev-parse HEAD)"
echo "分支: $(git rev-parse --abbrev-ref HEAD)"
echo ""

echo "── 项目计数 ──"
SRC_PROJECTS=$(find src -name "*.csproj" -not -path "*/obj/*" | wc -l | tr -d ' ')
TEST_PROJECTS=$(find test -name "*.csproj" -not -path "*/obj/*" -not -path "test/PalDDD.Testing/*" | wc -l | tr -d ' ')
echo "源项目数: $SRC_PROJECTS"
echo "测试项目数(排除Testing): $TEST_PROJECTS"
echo ""

echo "── 文件计数 ──"
SRC_FILES=$(find src -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" | wc -l | tr -d ' ')
TEST_FILES=$(find test -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*" | wc -l | tr -d ' ')
echo "源文件数: $SRC_FILES"
echo "测试文件数: $TEST_FILES"
echo ""

echo "── 架构守护 ──"
ARCH_TESTS=$(grep -cE "\[Test\]" test/PalDDD.DependencyInjection.Tests/ArchitectureBoundaryTests.cs 2>/dev/null || echo "?")
PDDD_RULES=$(grep -rohE "PDDD0[0-9]+" src/PalDDD.Analyzers/ --include="*.cs" 2>/dev/null | grep -v obj | sort -u | wc -l | tr -d ' ')
echo "架构边界测试用例数: $ARCH_TESTS"
echo "PDDD 诊断规则数: $PDDD_RULES"
echo ""

echo "── AOT 配置 ──"
AOT_TRUE=$(grep -rl "IsAotCompatible.*true" src/ --include="*.csproj" 2>/dev/null | grep -v obj | wc -l | tr -d ' ')
AOT_FALSE=$(grep -rl "IsAotCompatible.*false" src/ --include="*.csproj" 2>/dev/null | grep -v obj | wc -l | tr -d ' ')
echo "IsAotCompatible=true 项目: $AOT_TRUE"
echo "IsAotCompatible=false 项目: $AOT_FALSE"
echo ""

echo "── 异常过滤 ──"
CATCH_ALL=$(grep -rn "catch (Exception" src/ --include="*.cs" 2>/dev/null | grep -v obj | grep -v bin | grep -v ".pal" | wc -l | tr -d ' ')
OCE_FILTER=$(grep -rn "OperationCanceledException" src/ --include="*.cs" 2>/dev/null | grep -v obj | grep -v bin | grep -v ".pal" | wc -l | tr -d ' ')
echo "catch(Exception) 总数: $CATCH_ALL"
echo "OperationCanceledException 引用数: $OCE_FILTER"
echo ""

echo "── 测试状态（需手动运行 dotnet test 获取精确通过/失败数）──"
echo "命令: 逐项目 dotnet test <project>.csproj --no-build --nologo（MTP 一次一个，批量会 handshake exit 5）"
echo ""

echo "═══ 快照结束 — 粘贴以上内容到评审报告首行 ═══"
