using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>SQLite outbox store — single-writer, no lock hints needed (WAL mode).</summary>
/// <remarks>
/// <b>租约原子性限制（ITM-004）</b>：SQLite 不支持 <c>FOR UPDATE SKIP LOCKED</c>，
/// 租约操作为"SELECT → 内存改 → SaveChanges"三步分离，无行级锁。
/// WAL 模式保证单写者串行写入，但不阻塞并发 SELECT 读阶段——多实例 Outbox processor 场景下
/// 可能出现读-改-写竞态窗口（两个实例读到同一批 Pending 消息）。
/// <para>
/// ⚠️ <b>三十八轮 P2 修正（声明失真）：租约写入（LeasePending）无并发防护</b>——
/// 两实例可同时持同一批租约导致重复发布。原 remarks 声称的"RetryCount 并发令牌冲突兜底"
/// 实为 no-op：租约写入只改 LockedBy/LockedUntil 不触碰 RetryCount，
/// <c>UPDATE WHERE RetryCount=@orig</c> 恒匹配、检测不到竞态。
/// 仅靠部署边界约束（单实例/开发测试）。多实例请用 PG/MySQL/SqlServer 实现
/// （它们的 LeasePendingMessagesAsync 用 <c>FOR UPDATE SKIP LOCKED</c> 保证原子性）。
/// </para>
/// </remarks>
public abstract class SqliteOutboxDbContext(DbContextOptions options) : OutboxDbContext(options)
{
    /// <summary>按资格条件分页查询到期消息（GetPending/Lease 共用）。
    /// <para>
    /// 三十九轮 ITM-261 修复：EF Core 11 preview7 的 SQLite provider 不能翻译 DateTimeOffset 的
    /// <b>有序</b>比较（<c>&lt;=</c>）——等值比较可翻译（MarkProcessed/FencedTarget 的租约守卫不受影响），
    /// 因此 <c>NextAttemptAt &lt;= now</c> / <c>LockedUntil &lt;= now</c> 必须物化后内存过滤。
    /// 分页循环保证"时间过滤先于 Take"：首页全为未来重试/未到期租约时继续翻页直至填满
    /// batchSize 或耗尽（此前被测试本地重写遮蔽，重写版"SQL Take 后内存过滤"会少取批次）。
    /// </para>
    /// </summary>
    private async Task<List<OutboxMessage>> QueryEligibleAsync(
        int batchSize, int maxRetryCount, bool asNoTracking, CancellationToken ct)
    {
        var now = GetUtcNow();
        var result = new List<OutboxMessage>(batchSize);
        var skip = 0;
        while (result.Count < batchSize)
        {
            var query = asNoTracking ? OutboxMessages.AsNoTracking() : OutboxMessages;
            var page = await query
                .Where(m => m.Status == OutboxStatus.Pending && m.RetryCount < maxRetryCount)
                // ITM-261 续：EF SQLite 对 DateTimeOffset 连 ORDER BY 也不支持（"does not support
                // expressions of type 'DateTimeOffset' in ORDER BY clauses"）——改按 Id 排序：
                // ULID 的 Crockford Base32 字典序即创建时间序（规范级 sortable guarantee），
                // 与 CreatedAt 排序语义等价（均为创建序，分页确定性不受影响）。
                .OrderBy(m => m.Id)
                .Skip(skip).Take(batchSize)
                .ToListAsync(ct).ConfigureAwait(false);
            if (page.Count == 0) break;
            result.AddRange(page.Where(m =>
                (m.NextAttemptAt is null || m.NextAttemptAt <= now)
                && (m.LockedUntil is null || m.LockedUntil <= now)));
            skip += batchSize;
        }
        if (result.Count > batchSize) result.RemoveRange(batchSize, result.Count - batchSize);
        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
        // 优化（二十五轮 API 扫描 EF-5）：AsNoTracking——只读契约（接口 doc 保证不进
        // Mark*+SaveChanges）；违反契约的突变将静默丢失
        => await QueryEligibleAsync(batchSize, maxRetryCount, asNoTracking: true, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct)
    {
        // ITM-216 修复（三十二轮）：owner 空白守卫——对照 PG（ITM-081）/SqlServer/MySql 同款，
        // 缺守卫时空/空白 owner 写入 LockedBy 列破坏跨方言契约一致
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        // 三十八轮 P3 修复：补齐三姊妹已有守卫（ITM-081/167/216 对齐系列漏网项）——
        // leaseDuration 非正时租约即刻过期/永不过期语义错乱；TotalSeconds 超过 int.MaxValue
        // 时 until = now.Add(leaseDuration) 的秒数语义溢出。Options 层已校验正数，
        // 此处是 Store 直调路径的防御性 fail-fast（与 MySqlOutboxDbContext 同款）。
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for the lease LockedUntil value.");
        var until = GetUtcNow().Add(leaseDuration);
        // 优化（二十五轮 API 扫描 EF-5 配套）：租约不复用 GetPendingMessagesAsync——
        // 其 AsNoTracking 化后，"SELECT → 内存改 → SaveChanges"三步租约（ITM-004，
        // 见类头 remarks）的突变将静默丢失（SaveChangesAsync 无跟踪条目 = 0 行写入，
        // RetryCount 令牌兜底也随之失效）。此处用跟踪查询，租约/兜底语义不变。
        var messages = await QueryEligibleAsync(batchSize, maxRetryCount, asNoTracking: false, ct).ConfigureAwait(false);

        foreach (var msg in messages)
        {
            msg.LockedBy = owner;
            msg.LockedUntil = until;
        }
        await SaveChangesAsync(ct).ConfigureAwait(false);
        return messages;
    }
}
