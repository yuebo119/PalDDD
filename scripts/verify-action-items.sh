#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 🔍 verify-action-items.sh — 任务清单自动验证
# ═══════════════════════════════════════════════════════════════
#
# 用法：bash scripts/verify-action-items.sh docs/review/action-items-XXXX-XX-XX.md
# 目的：消除专业审计 R5（错误心智模型）、R6（未交叉验证外部输入）
#       验证任务清单中引用的方法名/类名/文件路径在源码中真实存在
#
# 检查项：
#   1. 任务描述中引用的反引号标识符（`MethodName`/`ClassName`）在 src/ + test/ 中可 grep 命中
#   2. 涉及分析器规则的任务是否提示需 build 验证
#   3. 涉及数字声明的任务是否提示需实测命令
#   4. [新增] 外部合并任务检查：提示验证方法名/类名/路径在源码中存在
# ═══════════════════════════════════════════════════════════════

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

FILE="${1:-}"
if [ -z "$FILE" ] || [ ! -f "$FILE" ]; then
    echo "用法: bash scripts/verify-action-items.sh <action-items.md>"
    echo "示例: bash scripts/verify-action-items.sh docs/review/action-items-2026-06-30.md"
    exit 1
fi

PASS=0
WARN=0
FAIL=0

echo "═══ 任务清单验证: $FILE ═══"
echo ""

# 提取所有反引号标识符（`标识符`），排除 markdown 代码块内的
# 格式：`标识符` 其中标识符含字母数字/点/下划线/尖括号
IDENTIFIERS=$(grep -oP '`[A-Z][A-Za-z0-9_.<>]+`' "$FILE" 2>/dev/null | tr -d '`' | sort -u || true)

if [ -z "$IDENTIFIERS" ]; then
    echo "  ℹ️  未找到反引号标识符，跳过标识符存在性检查"
else
    echo "── 标识符存在性检查 ──"
    for id in $IDENTIFIERS; do
        # 跳过非代码标识符：类型参数、诊断码、MSBuild 属性、文件名、配置项
        case "$id" in
            # 泛型类型参数
            T|TCommand|TResponse|TEvent|TState|TMessage|TResult|Key|Value) continue ;;
            # 诊断码（IDE/CA/CS/NU/PDDD + 数字）
            IDE[0-9]*|CA[0-9]*|CS[0-9]*|NU[0-9]*|PDDD[0-9]*) continue ;;
            # MSBuild 属性 / 配置项
            NoWarn|IsAotCompatible|IsTrimmable|VerifyReferenceAotCompatibility|TreatWarningsAsErrors) continue ;;
            # 文件名 / 解决方案名
            *.slnx|*.csproj|*.md|*.sh|*.props) continue ;;
            # .NET 框架类型（ ubiquitous，无需在 src 中验证）
            IDisposable|IAsyncDisposable|Exception|OperationCanceledException|ValueTask|CancellationToken) continue ;;
            # 评估类任务的"建议新增"类名（Helper/Extensions/Wrapper 后缀，讨论"是否新增"）
            SyntaxHelper|SymbolHelper) continue ;;
        esac

        # 在 src/ + test/ 中搜索标识符（文件名或代码内容）
        FOUND=$(grep -rl "$id" src/ test/ --include="*.cs" 2>/dev/null | grep -v obj | grep -v bin | head -1 || true)
        if [ -n "$FOUND" ]; then
            PASS=$((PASS + 1))
        else
            # 也检查文件路径是否存在（src/ 或 test/）
            if [ -f "src/$id" ] || [ -f "test/$id" ] || find src test -name "$id" 2>/dev/null | grep -q .; then
                PASS=$((PASS + 1))
            else
                echo "  ✗ 标识符 '$id' 在 src/ 中未找到"
                FAIL=$((FAIL + 1))
            fi
        fi
    done
    echo "  标识符检查: 通过 $PASS / 失败 $FAIL"
    echo ""
fi

# 检查涉及分析器规则的任务是否含 build 验证命令
echo "── 分析器任务 build 验证检查 ──"
if grep -qi "CA1031\|SuppressMessage\|分析器\|Analyzer\|NoWarn" "$FILE" 2>/dev/null; then
    if grep -qi "dotnet build" "$FILE" 2>/dev/null; then
        echo "  ✓ 涉及分析器的任务含 dotnet build 验证命令"
        PASS=$((PASS + 1))
    else
        echo "  ⚠ 涉及分析器的任务缺少 dotnet build 验证命令（元审计 R5 预防）"
        WARN=$((WARN + 1))
    fi
else
    echo "  ℹ️  未涉及分析器规则，跳过"
fi
echo ""

# 检查涉及数字声明的任务是否含实测命令
echo "── 数字声明实测命令检查 ──"
if grep -qE "[0-9]+ (个|处|条|行) (测试|项目|文件|规则|用例)" "$FILE" 2>/dev/null; then
    if grep -qi "dotnet test\|find src\|wc -l\|grep -c" "$FILE" 2>/dev/null; then
        echo "  ✓ 数字声明任务含实测命令"
        PASS=$((PASS + 1))
    else
        echo "  ⚠ 数字声明任务缺少实测命令（元审计 R8 预防）"
        WARN=$((WARN + 1))
    fi
else
    echo "  ℹ️  未发现数字声明，跳过"
fi
echo ""

echo "═══ 结果 ═══"
printf "通过: %d  警告: %d  失败: %d\n" "$PASS" "$WARN" "$FAIL"
if [ "$FAIL" -gt 0 ]; then
    echo ""
    echo "⚠️  存在标识符未找到，请核实任务描述中的方法名/类名（元审计 R6 预防）"
    exit 1
fi
if [ "$WARN" -gt 0 ]; then
    echo ""
    echo "⚠️  存在警告，建议补全验证命令"
fi
echo "✓ 验证完成"
exit 0
