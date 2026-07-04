#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# 📊 CI 覆盖率 + 报告生成
# ─────────────────────────────────────────────────────────────
# 用法：./ci-coverage.sh
# 输出：TestResults/coverage.cobertura.xml（Cobertura 格式）
#       TestResults/coverage-report/（HTML 报告）
#
# 💡 覆盖率阈值：
#   · Line >= 80%
#   · Branch >= 70%
# ─────────────────────────────────────────────────────────────
set -e

echo "=== Pal.DDD CI Coverage ==="

# 1. 构建
echo ">> Building..."
dotnet build PalDDD.slnx --nologe -v q

# 2. 测试 + 覆盖率收集
echo ">> Running tests with coverage..."
dotnet test PalDDD.slnx \
    --nologo \
    --no-build \
    -v q \
    --collect:"XPlat Code Coverage" \
    --results-directory TestResults

# 3. 合并报告
echo ">> Merging coverage reports..."
dotnet tool run reportgenerator \
    -reports:TestResults/**/coverage.cobertura.xml \
    -targetdir:TestResults/coverage-report \
    -reporttypes:Html

echo "=== Coverage complete ==="
echo "Report: TestResults/coverage-report/index.html"
