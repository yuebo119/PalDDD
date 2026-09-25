namespace PalDDD.Hosting.AspNetCore.Tests;

// ═══════════════════════════════════════════════════════════════
// 🧪 Verify 约定自检 — VerifyChecks.Run()
// ═══════════════════════════════════════════════════════════════
// Verify 官方约定的机器检查：ModuleInitializer 设置位置、命名约定、received
// 文件处理等一旦偏离官方推荐形态即在此测试失败，而非等到某次快照比对异常才暴露。
// 本仓 Verify 用法面为 Hosting.AspNetCore.Tests 的 3 个 .verified.txt 快照
// （公共 API 快照由自实现 PublicApiSnapshot 承载，不走 Verify）。
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Verify 配置约定自检 — 检测本仓 Verify 用法是否偏离官方推荐形态。
/// </summary>
public class VerifyChecksTests
{
    /// <summary>执行 Verify 官方约定检查（约定偏离即失败）。</summary>
    [Test]
    public Task Verify_ConfigurationFollowsOfficialConventions() => VerifyChecks.Run();
}
