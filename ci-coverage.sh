#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
# 📊 CI 覆盖率 + 报告生成 + 全局阈值门禁
# ─────────────────────────────────────────────────────────────
# 用法：./ci-coverage.sh
# 输出：TestResults/coverage.cobertura.xml（Cobertura 格式）
#       TestResults/coverage-report/（HTML 报告）
# 退出码：全局行覆盖率 < COVERAGE_THRESHOLD（默认 0.65）→ 退出 1（fail-closed）
#
# 💡 门禁历史：评审 P1-2——test-coverage-baseline.md 曾声称"全局行覆盖率
#   不低于 65%"门禁但从未实现（守卫空转）。现于本脚本实现全局行覆盖率阈值；
#   单模块 5% 降幅规则仍靠评审轮人工核对基线表（自动化需逐模块基线快照）。
#   CI 集成（ci.yml 调用本脚本）为后续项：全量覆盖率收集会显著拉长流水线，
#   需维护者裁决后启用。
# ─────────────────────────────────────────────────────────────
set -e

# 阈值可被环境变量覆盖（本地放宽/CI 收紧）
COVERAGE_THRESHOLD="${COVERAGE_THRESHOLD:-0.65}"

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

# 4. 合并报告（Cobertura 供第 5 步门禁解析，Html 供人工审阅）
echo ">> Merging coverage reports..."
dotnet tool run reportgenerator \
    -reports:TestResults/coverage.*.cobertura.xml \
    -targetdir:TestResults/coverage-report \
    -reporttypes:"Html;Cobertura"

# 5. 全局行覆盖率门禁（fail-closed，评审 P1-2）
#    解析合并后 Cobertura 顶层 <coverage line-rate="0.xxx">——该值是
#    ReportGenerator 对全部模块行覆盖的加权汇总，与基线口径一致。
echo ">> Enforcing global line coverage threshold: ${COVERAGE_THRESHOLD}"
merged_cobertura="TestResults/coverage-report/Cobertura.xml"
if [[ ! -f "$merged_cobertura" ]]; then
  echo "ERROR: merged cobertura report not found at $merged_cobertura" >&2
  exit 1
fi
line_rate=$(grep -o '<coverage[^>]*line-rate="[0-9.]*"' "$merged_cobertura" | head -1 | grep -o 'line-rate="[0-9.]*"' | grep -o '[0-9.]*$')
if [[ -z "$line_rate" ]]; then
  echo "ERROR: could not parse line-rate from $merged_cobertura (report format drift?)" >&2
  exit 1
fi
echo ">> Global line coverage: ${line_rate} (threshold ${COVERAGE_THRESHOLD})"
if awk -v a="$line_rate" -v b="$COVERAGE_THRESHOLD" 'BEGIN{exit !(a<b)}'; then
  echo "FAIL: global line coverage ${line_rate} is below threshold ${COVERAGE_THRESHOLD}" >&2
  exit 1
fi

echo "=== Coverage complete (gate PASSED) ==="
echo "Report: TestResults/coverage-report/index.html"
