using System.Text.RegularExpressions;

namespace PalDDD.DependencyInjection.Tests;

// ═══════════════════════════════════════════════════════════════
// 测试规范门禁守卫（MIG-T6/T7 下沉，承接 .ai/scripts/test-gate.sh 与 post-fix-check.sh）
// ═══════════════════════════════════════════════════════════════
// 承接来源（任务清单 docs/design/script-migration-tasks.md）：
//   test-gate.sh  T6  DROP TABLE 清理模式 → 已知命中白名单活账本（6 处注入探针串）
//   test-gate.sh  T8  基准作业配置注释 → 扫描面勘正：bash 只查 Program.cs 的 SimpleJob，
//                     而本项目 Program.cs 早已不含任何 Job 特性（恒空集 PASS = no-op 门）；
//                     C# 版扫 bench 全部 .cs 的 SimpleJob/RunJob 特性并加活跃性断言
//   test-gate.sh  T11 bench 与测试环境变量分离 → bench 禁用 PALDDD_TEST_*（加活跃性：test/ 必须在用）
//   post-fix-check.sh 三项零残留判定 → TODO:/FIXME/NotImplementedException、throw ex;、
//                     裸截断 [..2000/2040/256]（收口点 FailureReason 行除外）
//
// 与 bash 版的判定差异（有意收紧，方向为更严）：
//   T6 bash 是计数阈值（>5 WARN 不阻断，当前 6 处恰在 WARN）——C# 收紧为命中行白名单
//      双向相等：新增未防护 DROP TABLE 红，清理掉一处也红（提醒缩账本）。
//   T6/T11 的目录存在性：bash 静默跳过（[ -d ] || continue / 无文件即 SKIP）——C# 改 FAIL。
// ═══════════════════════════════════════════════════════════════

public sealed class TestGateGuardTests
{
    private static readonly string Root = FindRepositoryRoot();

    // ─────────────────────────────────────────────────────────────
    // 通用：仓库根定位 + 构建产物过滤（对齐 TechDebtGuardTests 同款基建）
    // ─────────────────────────────────────────────────────────────

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PalDDD.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Unable to locate PalDDD.slnx.");
    }

    private static bool IsNotBuildArtifact(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    /// <summary>目录下全部 .cs 文件相对路径（正斜杠归一，排序稳定，排除构建产物）。</summary>
    private static List<string> EnumerateCsFiles(string relativeDir) =>
        Directory.EnumerateFiles(Path.Combine(Root, relativeDir), "*.cs", SearchOption.AllDirectories)
            .Where(IsNotBuildArtifact)
            .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    // ═════════════════════════════════════════════════════════════
    // T6：外部 DB 测试清理检查（DROP TABLE 需 finally 关联/IF EXISTS 兜底）
    // ═════════════════════════════════════════════════════════════

    /// <summary>T6 扫描目录（两个集成测试项目——bash 同清单，存在性收紧为 FAIL）。</summary>
    private static readonly string[] s_dropTableScanDirs =
    [
        "test/PalDDD.Integration.Tests",
        "test/PalDDD.Messaging.Integration.Tests",
    ];

    /// <summary>
    /// 已知 DROP TABLE 命中白名单（活账本）：6 处全部是 SQL 注入测试的恶意输入字符串
    /// （[Arguments]/断言字面量），并非真实清理语句——bash 的行级启发式（排除 finally/IF EXISTS）
    /// 无法识别字符串语境，故以白名单固化当前实况。
    /// </summary>
    private static readonly string[] s_knownDropTableHits =
    [
        "test/PalDDD.Integration.Tests/DapperBulkCopyTests.cs:9",
        "test/PalDDD.Integration.Tests/DapperBulkCopyTests.cs:47",
        "test/PalDDD.Integration.Tests/PostgreSqlShardingTests.cs:42",
        "test/PalDDD.Integration.Tests/PostgreSqlSoftDeleteTests.cs:36",
        "test/PalDDD.Integration.Tests/PostgreSqlSoftDeleteTests.cs:41",
        "test/PalDDD.Integration.Tests/PostgreSqlSoftDeleteTests.cs:43",
    ];

    /// <summary>
    /// T6 全量判定：DROP TABLE 行且不在 finally 关联（行含 finally 启发式）也无 IF EXISTS
    /// 兜底的命中集合，必须与白名单双向相等——bash 阈值（>5 WARN）对当前 6 处注入探针
    /// 恒 WARN，活账本使其显式化：新增未防护 DROP TABLE 红，清理一处也红（提醒缩账本）。
    /// </summary>
    [Test]
    public async Task DropTableUsage_MatchesKnownLedger()
    {
        foreach (var dir in s_dropTableScanDirs)
        {
            if (!Directory.Exists(Path.Combine(Root, dir)))
                Assert.Fail($"T6 扫描目录不存在（存在性断言——目录改名/移动后须更新 s_dropTableScanDirs）: {dir}");
        }

        var hits = new List<string>();
        foreach (var dir in s_dropTableScanDirs)
        {
            foreach (var file in EnumerateCsFiles(dir))
            {
                var lines = File.ReadAllLines(Path.Combine(Root, file));
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!line.Contains("DROP TABLE", StringComparison.Ordinal))
                        continue;
                    // bash 同口径启发式：行含 finally（清理路径）或 IF EXISTS（幂等兜底）不算残留
                    if (line.Contains("finally", StringComparison.Ordinal))
                        continue;
                    if (line.Contains("IF EXISTS", StringComparison.Ordinal))
                        continue;
                    hits.Add($"{file}:{i + 1}");
                }
            }
        }
        hits.Sort(StringComparer.Ordinal);

        var expected = s_knownDropTableHits.OrderBy(h => h, StringComparer.Ordinal).ToList();
        if (!expected.SequenceEqual(hits))
        {
            var added = hits.Except(expected).ToList();
            var resolved = expected.Except(hits).ToList();
            Assert.Fail(
                "DROP TABLE 命中账本漂移（T6——DROP TABLE 须在 finally 中或带 IF EXISTS 兜底）:\n" +
                $"  新增命中（确认是否注入探针串：是则入 s_knownDropTableHits，否则补 finally/IF EXISTS）: {string.Join(", ", added)}\n" +
                $"  已清理（请从 s_knownDropTableHits 移除）: {string.Join(", ", resolved)}");
        }
    }

    // ═════════════════════════════════════════════════════════════
    // T8：基准作业配置必须携带理由注释
    // ═════════════════════════════════════════════════════════════

    /// <summary>基准作业配置特性：SimpleJob（bash 原口径）+ Short/Medium/Long RunJob（本项目实际形态）。</summary>
    private static readonly Regex s_benchJobAttribute =
        new(@"\[(SimpleJob|(?:Short|Medium|Long)RunJob)", RegexOptions.Compiled);

    /// <summary>
    /// T8 注释判定（P3 批 URL 勘正）：窗口行含 // 或 /* 即视为理由注释；但行含 ://
    ///（http(s)://、ftp:// 等 URL scheme）时其中的 // 是 URL 组成而非注释形态——
    /// 旧口径裸 Contains("//") 把纯 URL 行误判为"有理由注释"，对窗口内只有 URL 的
    /// 作业配置误放行。最简式（行级 Contains 近似，任务书 P3 口径）：行含 :// 整行
    /// 不作 // 注释论（行内注释附 URL 的形态会偏严计入违规——收紧方向，高召回优先）。
    /// </summary>
    internal static bool LineLooksLikeComment(string line) =>
        (line.Contains("//", StringComparison.Ordinal) && !line.Contains("://", StringComparison.Ordinal))
        || line.Contains("/*", StringComparison.Ordinal);

    /// <summary>T8 判定器红绿矩阵（负向自证）：纯 URL 行不算理由（旧口径误放行点）；
    /// 真注释与块注释行仍算。</summary>
    [Test]
    public async Task RationaleCommentDetector_HandlesUrlFalsePositive()
    {
        // 红形态（旧 Contains("//") 误放行）：窗口行只含 URL——URL 中的 // 不是注释
        await Assert.That(LineLooksLikeComment("https://learn.microsoft.com/en-us/dotnet/api/benchmarkdotnet")).IsFalse();
        await Assert.That(LineLooksLikeComment("参考 http://example.com/rationale")).IsFalse();
        // 绿形态：真注释（// 与 /*）
        await Assert.That(LineLooksLikeComment("// ShortRun：迭代预算说明")).IsTrue();
        await Assert.That(LineLooksLikeComment("/* 理由块注释")).IsTrue();
    }

    /// <summary>
    /// T8 全量判定（扫描面勘正）：bench 全部 .cs 中每个 SimpleJob/RunJob 特性行的前 3 行内
    /// 必须有注释（// 或 /*，URL 中的 // 不算——见 <see cref="LineLooksLikeComment"/>）。
    /// 活跃性断言 ≥1 处——bash 只查 Program.cs 的 SimpleJob，
    /// 该文件早已不含任何 Job 特性，恒空集 PASS = no-op 门（下沉时勘正，对齐 #18 路径勘正先例）。
    /// </summary>
    [Test]
    public async Task BenchmarkJobConfigs_CarryRationaleComments()
    {
        var files = EnumerateCsFiles("bench");
        await Assert.That(files.Count).IsGreaterThan(0); // 扫描面存在性

        var violations = new List<string>();
        var jobCount = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(Path.Combine(Root, file));
            for (var i = 0; i < lines.Length; i++)
            {
                if (!s_benchJobAttribute.IsMatch(lines[i]))
                    continue;
                jobCount++;
                // 前 3 行窗口 [max(0, i-3), i-1]（bash awk 同口径）
                var windowStart = Math.Max(0, i - 3);
                var hasComment = Enumerable.Range(windowStart, i - windowStart)
                    .Any(j => LineLooksLikeComment(lines[j]));
                if (!hasComment)
                    violations.Add($"{file}:{i + 1}");
            }
        }

        // 活跃性：bench 确实存在作业配置特性（0 处说明特性形态漂移，锚空转）
        await Assert.That(jobCount).IsGreaterThan(0);

        if (violations.Count > 0)
            Assert.Fail(
                $"以下基准作业配置缺少理由注释（T8——前 3 行内须有 // 或 /*）:\n{string.Join("\n", violations)}");
    }

    // ═════════════════════════════════════════════════════════════
    // T11：bench 与测试环境变量分离（PALDDD_TEST_* 仅在 test/ 使用）
    // ═════════════════════════════════════════════════════════════

    private static readonly Regex s_testOnlyEnvVars =
        new("PALDDD_TEST_(PG|MYSQL|KAFKA|RABBIT)", RegexOptions.Compiled);

    /// <summary>
    /// 守卫测试自身路径（自指污染排除）：本文件含 s_testOnlyEnvVars 的锚串
    /// "PALDDD_TEST_..."——不排除时下方活跃性断言被传感器文件自身恒满足（空转检测失效）。
    /// </summary>
    private const string GuardTestSelfPath = "test/PalDDD.DependencyInjection.Tests/TestGateGuardTests.cs";

    /// <summary>
    /// T11 全量判定：bench 不得引用测试专用环境变量 PALDDD_TEST_*（基准应独立配置）；
    /// 反向活跃性——test/ 必须确实在用 PALDDD_TEST_*（环境变量名整体漂移时传感器空转检测）。
    /// </summary>
    [Test]
    public async Task Benchmarks_AvoidTestOnlyEnvironmentVariables()
    {
        var benchFiles = EnumerateCsFiles("bench");
        await Assert.That(benchFiles.Count).IsGreaterThan(0); // 扫描面存在性

        var violations = benchFiles
            .Where(f => s_testOnlyEnvVars.IsMatch(File.ReadAllText(Path.Combine(Root, f))))
            .ToList();
        if (violations.Count > 0)
            Assert.Fail(
                $"基准代码引用了测试专用环境变量 PALDDD_TEST_*（T11——基准应独立配置）:\n{string.Join("\n", violations)}");

        // 活跃性：test/ 下 PALDDD_TEST_* 确实被使用（0 处说明变量名整体漂移，禁词锚空转；
        // 排除守卫测试自身——传感器文件含锚串，见 GuardTestSelfPath）
        var testUses = EnumerateCsFiles("test")
            .Where(f => f != GuardTestSelfPath)
            .Any(f => s_testOnlyEnvVars.IsMatch(File.ReadAllText(Path.Combine(Root, f))));
        if (!testUses)
            Assert.Fail("test/ 下未发现任何 PALDDD_TEST_(PG|MYSQL|KAFKA|RABBIT) 引用（T11 活跃性——变量名整体漂移须同步更新 s_testOnlyEnvVars）");
    }

    // ═════════════════════════════════════════════════════════════
    // post-fix-check 三项零残留判定（MIG-T7 下沉）
    // ═════════════════════════════════════════════════════════════

    /// <summary>裸字符串截断残留（[..2000/2040/256]）；收口点 FailureReason 行除外（bash 同口径）。</summary>
    private static readonly Regex s_rawTruncationPattern =
        new(@"\[..(2000|2040|256)\]", RegexOptions.Compiled);

    /// <summary>
    /// post-fix-check 三项判定（bash 同口径，src/ 全部 .cs 排除构建产物）：
    /// ① TODO:/FIXME/NotImplementedException 零残留；
    /// ② throw ex; 零残留（重置堆栈）；
    /// ③ 裸截断 [..2000/2040/256] 零残留（FailureReason 收口行除外）。
    /// </summary>
    [Test]
    public async Task PostFixResiduals_StayZero()
    {
        var files = EnumerateCsFiles("src");
        await Assert.That(files.Count).IsGreaterThan(0); // 扫描面存在性

        var todoHits = new List<string>();
        var throwExHits = new List<string>();
        var rawTruncationHits = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(Path.Combine(Root, file));
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // bash 同口径：TODO 带冒号+空格（'TODO: '），与注释形态的 tech-debt-scan #2 互补
                if (line.Contains("TODO: ", StringComparison.Ordinal)
                    || line.Contains("FIXME", StringComparison.Ordinal)
                    || line.Contains("NotImplementedException", StringComparison.Ordinal))
                    todoHits.Add($"{file}:{i + 1}");
                if (line.Contains("throw ex;", StringComparison.Ordinal))
                    throwExHits.Add($"{file}:{i + 1}");
                if (s_rawTruncationPattern.IsMatch(line)
                    && !line.Contains("FailureReason", StringComparison.Ordinal))
                    rawTruncationHits.Add($"{file}:{i + 1}");
            }
        }

        if (todoHits.Count > 0 || throwExHits.Count > 0 || rawTruncationHits.Count > 0)
            Assert.Fail(
                "post-fix 三项残留（须零残留）:\n" +
                $"  TODO:/FIXME/NotImplementedException {todoHits.Count} 处:\n{string.Join("\n", todoHits)}\n" +
                $"  throw ex;（堆栈重置）{throwExHits.Count} 处:\n{string.Join("\n", throwExHits)}\n" +
                $"  裸截断 [..2000/2040/256] {rawTruncationHits.Count} 处:\n{string.Join("\n", rawTruncationHits)}");
    }
}
