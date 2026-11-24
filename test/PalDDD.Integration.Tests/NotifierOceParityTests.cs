// ─────────────────────────────────────────────────────────────
// 🧪 PG 通知器 OCE 分类奇偶性（R18 审计发现缺陷 2 / 缺陷 3）
// ─────────────────────────────────────────────────────────────
// 缺陷 2：Dapper 栈 v18 B1（6e105ed）已补「非关停 OCE 记 Error」分支，PalORM 栈
// 复制时漏抄；非关停 OCE 逃出 async Task 成未观察异常，零 Error 日志。
// 缺陷 3：Dapper 栈 OCE 重连退避用 CancellationToken.None，与普通 Exception 分支的
// stoppingToken 不对称，关停无法从退避退出（与 8c9e3a6 提交说明矛盾）。
//
// 为什么用源码级不变式：两处均属上游 listener 线程调度不可控的竞态面（R15 已证伪
// 过一例行为探针），本仓同族（KafkaConsumeHealthWiringTests）确立源码级形态。
// 判据逐行扫描并跳过 // 注释行，防「注释掉也算命中」的假绿。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Integration.Tests;

/// <summary>
/// PG 通知器两栈 OCE 分类奇偶性的源码级不变式（R18 缺陷 2/3 锁定）。
/// </summary>
public sealed class NotifierOceParityTests
{
    /// <summary>
    /// 两栈 FireBatchProcessAsync 的 catch 分类必须对等：Dapper 有三分支
    /// （关停 OCE / 非关停 OCE 记 Error / 通用 Exception），PalORM 修前只有两支，
    /// 缺非关停 OCE 分支。本测试在修前红。
    /// </summary>
    [Test]
    public async Task BothStacks_FireBatchProcessAsync_HaveSameCatchBranches()
    {
        var dapper = await ReadCodeAsync("src", "PalDDD.Dapper.PostgreSql", "PostgreSqlOutboxNotifier.cs");
        var palOrm = await ReadCodeAsync("src", "PalDDD.PalORM.PostgreSql", "PalOrmPostgreSqlOutboxNotifier.cs");

        var dapperBody = ExtractMethodBody(dapper, "private async Task FireBatchProcessAsync");
        var palOrmBody = ExtractMethodBody(palOrm, "private async Task FireBatchProcessAsync");
        await Assert.That(dapperBody is not null).IsTrue().Because("未定位 Dapper 版 FireBatchProcessAsync");
        await Assert.That(palOrmBody is not null).IsTrue().Because("未定位 PalORM 版 FireBatchProcessAsync");

        // 非关停 OCE 分类：catch (OperationCanceledException oce) 后跟 logger.Error
        await Assert.That(dapperBody!.Contains("catch (OperationCanceledException oce)", StringComparison.Ordinal)).IsTrue()
            .Because("Dapper 版应有非关停 OCE 分类分支（基线）");
        await Assert.That(palOrmBody!.Contains("catch (OperationCanceledException oce)", StringComparison.Ordinal)).IsTrue()
            .Because("PalORM 版必须补齐非关停 OCE 分类（修前缺，异常成未观察且零日志）");
    }

    /// <summary>
    /// Dapper 版 ExecuteAsync 的 OCE 退避与普通 Exception 退避必须用同一取消源。
    /// 修前 OCE 分支用 CancellationToken.None，本测试红。
    /// </summary>
    [Test]
    public async Task DapperExecuteAsync_OceBackoff_UsesStoppingToken()
    {
        var code = await ReadCodeAsync("src", "PalDDD.Dapper.PostgreSql", "PostgreSqlOutboxNotifier.cs");
        var oceBranch = code.IndexOf("var oceDelay =", StringComparison.Ordinal);
        await Assert.That(oceBranch).IsGreaterThan(-1).Because("未定位 OCE 退避分支");

        var nextDelay = code.IndexOf("Task.Delay(", oceBranch, StringComparison.Ordinal);
        await Assert.That(nextDelay).IsGreaterThan(oceBranch).Because("未定位 OCE 退避 Task.Delay");

        // 直接看 OCE 分支内 Task.Delay 调用：必须使用 stoppingToken 而非 None。
        // 截取到该 catch 块的 break 前（不足 200 字符则取到末尾）。
        var tail = code[nextDelay..];
        var blockEnd = tail.IndexOf("catch (OperationCanceledException) { break; }", StringComparison.Ordinal);
        var call = blockEnd > 0 ? tail[..blockEnd] : tail[..Math.Min(200, tail.Length)];
        await Assert.That(call.Contains("stoppingToken", StringComparison.Ordinal)).IsTrue()
            .Because("OCE 退避必须响应宿主关停（修前用 CancellationToken.None，关停多耗最长 30s）");
        await Assert.That(call.Contains("CancellationToken.None", StringComparison.Ordinal)).IsFalse()
            .Because("OCE 退避不得用 None token");
    }

    private static async Task<string> ReadCodeAsync(params string[] segments)
    {
        var path = Path.Combine([FindRepoRoot(), .. segments]);
        var lines = await File.ReadAllLinesAsync(path);
        return string.Join('\n', lines.Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static string? ExtractMethodBody(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;
        var brace = code.IndexOf('{', start);
        if (brace < 0) return null;
        var depth = 0;
        for (var i = brace; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[brace..(i + 1)];
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("未定位仓库根（PalDDD.slnx）");
    }
}
