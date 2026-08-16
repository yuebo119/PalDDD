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
dotnet build PalDDD.slnx --nologo -v q

# 2. 测试 + 覆盖率收集
# MTP 手写协议：一次一个测试项目 + MTP 原生 --coverage；
# 旧写法（slnx 批量 + --collect:"XPlat Code Coverage"）触发 VSTest 握手 exit 5
# （2026-08-16 终验轮 B-2 实测复现）。PalDDD.Testing 为支持库非测试项目。
echo ">> Running tests with coverage..."
mkdir -p TestResults
for csproj in $(find test -name '*.csproj' ! -name 'PalDDD.Testing.csproj' | sort); do
  name="$(basename "$csproj" .csproj)"
  echo ">> $name"
  dotnet test "$csproj" \
      --nologo \
      --no-build \
      -v q \
      --coverage \
      --coverage-output "TestResults/coverage.$name.cobertura.xml" \
      --coverage-output-format cobertura
done

# 3. 恢复本地工具清单（固定 ReportGenerator 版本，见 .config/dotnet-tools.json）
echo ">> Restoring local tools..."
dotnet tool restore

# 4. 合并报告
echo ">> Merging coverage reports..."
dotnet tool run reportgenerator \
    -reports:TestResults/coverage.*.cobertura.xml \
    -targetdir:TestResults/coverage-report \
    -reporttypes:Html

echo "=== Coverage complete ==="
echo "Report: TestResults/coverage-report/index.html"
