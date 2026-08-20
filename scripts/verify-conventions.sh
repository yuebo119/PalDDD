#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# 🔬 verify-conventions.sh — Pal.DDD 规范验证脚本
# ═══════════════════════════════════════════════════════════════
#
# 用法：
#   bash scripts/verify-conventions.sh              # 全部检查（grep + build + test）
#   bash scripts/verify-conventions.sh --quick      # 仅 grep 静态检查（秒级）
#   bash scripts/verify-conventions.sh --build      # grep + build（不含 test）
#
# 安装为 git pre-commit hook（仅 grep 检查，秒级）：
#   git config core.hooksPath .githooks
#
# 检查项：
#   1. 零反射扫描（MakeGenericType / Activator / Assembly.GetTypes / Type.GetType）
#   2. async void 扫描
#   3. .Result / .Wait() 扫描（排除 IsCompletedSuccessfully 后）
#   4. TODO / HACK / FIXME 扫描
#   5. dotnet build 零错误零警告（--build 或默认模式）
#   6. dotnet test 零失败（默认模式）
# ═══════════════════════════════════════════════════════════════

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SRC_DIR="$ROOT_DIR/src"

# 解析参数
MODE="${1:-full}"  # full / --quick / --build

# 颜色
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

FAIL=0

check() {
    local name="$1"
    local pattern="$2"
    local path="$3"
    local exclude="${4:-}"  # 可选的排除模式（grep -v）

    local matches
    if [ -n "$exclude" ]; then
        matches=$(grep -rn --include='*.cs' "$pattern" "$path" 2>/dev/null \
            | grep -v '/obj/' | grep -v '/bin/' \
            | grep -v ':[[:space:]]*//' \
            | grep -v "$exclude" \
            || true)
    else
        matches=$(grep -rn --include='*.cs' "$pattern" "$path" 2>/dev/null \
            | grep -v '/obj/' | grep -v '/bin/' \
            | grep -v ':[[:space:]]*//' \
            || true)
    fi

    if [ -n "$matches" ]; then
        echo -e "${RED}❌ $name 违反${NC}"
        echo "$matches" | head -10
        echo ""
        FAIL=1
    else
        echo -e "${GREEN}✅ $name 通过${NC}"
    fi
}

echo "═══════════════════════════════════════════════════════════════"
echo "  Pal.DDD 规范验证（mode: $MODE）"
echo "═══════════════════════════════════════════════════════════════"
echo ""

# ── grep 静态检查（所有模式都执行）──────────────────────────────

# MakeGenericType 检查 — 排除已标注 [RequiresDynamicCode]/[RequiresUnreferencedCode] 的行
MGT_HITS=$(grep -rn --include='*.cs' 'MakeGenericType' "$SRC_DIR" 2>/dev/null \
    | grep -v '/obj/' | grep -v '/bin/' \
    | grep -v ':[[:space:]]*//' \
    | while IFS= read -r line; do
        file=$(echo "$line" | cut -d: -f1)
        lineno=$(echo "$line" | cut -d: -f2)
        # 检查当前行及前 30 行是否有 RequiresDynamicCode/RequiresUnreferencedCode 注解
        context=$(sed -n "$((lineno-30)),${lineno}p" "$file" 2>/dev/null)
        if ! echo "$context" | grep -q -e 'RequiresDynamicCode' -e 'RequiresUnreferencedCode'; then
            echo "$line"
        fi
    done || true)
if [ -n "$MGT_HITS" ]; then
    echo -e "${RED}❌ 零反射 (MakeGenericType) 违反${NC}"
    echo "$MGT_HITS" | head -10
    echo ""
    FAIL=1
else
    echo -e "${GREEN}✅ 零反射 (MakeGenericType) 通过${NC}"
fi

# Activator.CreateInstance 检查 — 同上
ACT_HITS=$(grep -rn --include='*.cs' 'Activator\.CreateInstance' "$SRC_DIR" 2>/dev/null \
    | grep -v '/obj/' | grep -v '/bin/' \
    | grep -v ':[[:space:]]*//' \
    | while IFS= read -r line; do
        file=$(echo "$line" | cut -d: -f1)
        lineno=$(echo "$line" | cut -d: -f2)
        context=$(sed -n "$((lineno-5)),${lineno}p" "$file" 2>/dev/null)
        if ! echo "$context" | grep -q -e 'RequiresDynamicCode' -e 'RequiresUnreferencedCode'; then
            echo "$line"
        fi
    done || true)
if [ -n "$ACT_HITS" ]; then
    echo -e "${RED}❌ 零反射 (Activator.CreateInstance) 违反${NC}"
    echo "$ACT_HITS" | head -10
    echo ""
    FAIL=1
else
    echo -e "${GREEN}✅ 零反射 (Activator.CreateInstance) 通过${NC}"
fi

check "零反射 (Assembly.GetTypes)" "Assembly\.GetTypes" "$SRC_DIR"
check "零反射 (Type.GetType(string))" "Type\.GetType(" "$SRC_DIR"

check "禁止 async void" "async void" "$SRC_DIR"

# .Result 检查（排除 IsCompletedSuccessfully 后的安全路径）
RESULT_HITS=$(grep -rn --include='*.cs' '\.Result' "$SRC_DIR" 2>/dev/null \
    | grep -v '/obj/' | grep -v '/bin/' \
    | grep -v ':[[:space:]]*//' \
    | while IFS= read -r line; do
        file=$(echo "$line" | cut -d: -f1)
        lineno=$(echo "$line" | cut -d: -f2)
        context=$(sed -n "$((lineno-3)),${lineno}p" "$file" 2>/dev/null)
        if ! echo "$context" | grep -q 'IsCompletedSuccessfully'; then
            echo "$line"
        fi
    done || true)
if [ -n "$RESULT_HITS" ]; then
    echo -e "${RED}❌ .Result 使用（非 IsCompletedSuccessfully 快速路径）${NC}"
    echo "$RESULT_HITS" | head -5
    echo ""
    FAIL=1
else
    echo -e "${GREEN}✅ .Result 仅在 IsCompletedSuccessfully 后使用${NC}"
fi

# .Wait() 检查
WAIT_HITS=$(grep -rn --include='*.cs' '\.Wait()' "$SRC_DIR" 2>/dev/null \
    | grep -v '/obj/' | grep -v '/bin/' \
    | grep -v ':[[:space:]]*//' \
    || true)
if [ -n "$WAIT_HITS" ]; then
    echo -e "${RED}❌ .Wait() 使用${NC}"
    echo "$WAIT_HITS" | head -5
    echo ""
    FAIL=1
else
    echo -e "${GREEN}✅ .Wait() 未使用${NC}"
fi

check "禁止 TODO/HACK/FIXME" "TODO\|HACK\|FIXME\|WORKAROUND" "$SRC_DIR"

# --quick 模式：仅 grep 检查，跳过 build/test
if [ "$MODE" = "--quick" ]; then
    echo ""
    echo "═══════════════════════════════════════════════════════════════"
    if [ "$FAIL" -eq 0 ]; then
        echo -e "${GREEN}  ✅ 静态检查通过（--quick 模式，跳过 build/test）${NC}"
    else
        echo -e "${RED}  ❌ 静态检查未通过${NC}"
    fi
    echo "═══════════════════════════════════════════════════════════════"
    exit $FAIL
fi

# ── dotnet build（--build 和 full 模式）─────────────────────────
echo ""
echo "═══════════════════════════════════════════════════════════════"
echo "  构建"
echo "═══════════════════════════════════════════════════════════════"
echo ""

cd "$ROOT_DIR"
# 三十五轮 A2 修复：退出码先行 + 锚定匹配。原判定 `grep -qE "0 (个错误|Error)"` 是子串匹配——
# "10 个错误"包含"0 个错误"子串，错误/警告数以 0 结尾时坏构建假通过（门禁假绿族）。
BUILD_OUTPUT=$(dotnet build PalDDD.slnx 2>&1)
BUILD_RC=$?

# 双语兼容：锚定数字边界（前面不是数字），"0 个错误"/"0 Error(s)" 才算零
if [ $BUILD_RC -eq 0 ] \
    && echo "$BUILD_OUTPUT" | grep -qE "(^|[^0-9])0( 个错误| 个警告| Error| Warning)" \
    && ! echo "$BUILD_OUTPUT" | grep -qE "[1-9][0-9]* (个错误|个警告|Error|Warning)"; then
    echo -e "${GREEN}✅ dotnet build 零错误零警告${NC}"
else
    echo -e "${RED}❌ dotnet build 有错误或警告（rc=$BUILD_RC）${NC}"
    echo "$BUILD_OUTPUT" | tail -10
    FAIL=1
fi

# --build 模式：跳过 test
if [ "$MODE" = "--build" ]; then
    echo ""
    echo "═══════════════════════════════════════════════════════════════"
    if [ "$FAIL" -eq 0 ]; then
        echo -e "${GREEN}  ✅ 验证通过（--build 模式，跳过 test）${NC}"
    else
        echo -e "${RED}  ❌ 验证未通过${NC}"
    fi
    echo "═══════════════════════════════════════════════════════════════"
    exit $FAIL
fi

# ── dotnet test（full 模式）─────────────────────────────────────
echo ""
echo "═══════════════════════════════════════════════════════════════"
echo "  测试"
echo "═══════════════════════════════════════════════════════════════"
echo ""

if [ "$FAIL" -eq 0 ]; then
    # MTP 手写协议：一次只跑一个测试项目（slnx 批量触发 VSTest 握手 exit 5，
    # 2026-08-16 终验轮 B-3 实测复现）；PalDDD.Testing 为支持库非测试项目
    TEST_FAIL=0
    for csproj in $(find test -name '*.csproj' ! -name 'PalDDD.Testing.csproj' | sort); do
        echo "  测试: $csproj"
        if ! dotnet test "$csproj" --no-restore --no-build >/dev/null 2>&1; then
            echo -e "${RED}❌ $csproj 测试失败${NC}"
            TEST_FAIL=1
        fi
    done
    if [ "$TEST_FAIL" -eq 0 ]; then
        echo -e "${GREEN}✅ dotnet test 零失败（全部测试项目逐个执行）${NC}"
    else
        FAIL=1
    fi
fi

echo ""
echo "═══════════════════════════════════════════════════════════════"
if [ "$FAIL" -eq 0 ]; then
    echo -e "${GREEN}  ✅ 规范验证全部通过${NC}"
    echo "═══════════════════════════════════════════════════════════════"
    exit 0
else
    echo -e "${RED}  ❌ 规范验证未通过，请修复上述问题${NC}"
    echo "═══════════════════════════════════════════════════════════════"
    exit 1
fi
