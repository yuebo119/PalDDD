using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>SQLite outbox store — single-writer, no lock hints needed (WAL mode).</summary>
/// <remarks>
/// <b>租约原子性限制（ITM-004）</b>：SQLite 不支持 <c>FOR UPDATE SKIP LOCKED</c>，
/// 租约操作为"SELECT → 内存改 → SaveChanges"三步分离，无行级锁。
/// WAL 模式保证单写者串行写入，但不阻塞并发 SELECT 读阶段——多实例 Outbox processor 场景下
/// 可能出现读-改-写竞态窗口（两个实例读到同一批 Pending 消息）。
/// 兜底：RetryCount 并发令牌冲突 + SaveChanges 失败被 OutboxBatchProcessor 吞为 Warning。
/// <para><b>适用场景</b>：单实例部署或开发/测试环境。生产多实例请用 PostgreSQL/MySQL/SqlServer
/// （它们的 LeasePendingMessagesAsync 用 <c>FOR UPDATE SKIP LOCKED</c> 保证原子性）。</para>
/// </remarks>
public abstract class SqliteOutboxDbContext(DbContextOptions options) : OutboxDbContext(options)
{
    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        var now = GetUtcNow();
        // 优化（二十五轮 API 扫描 EF-5）：AsNoTracking——只读契约（接口 doc 保证不进
        // Mark*+SaveChanges）；违反契约的突变将静默丢失
        return await OutboxMessages
            .AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending && m.RetryCount < maxRetryCount)
            .Where(m => m.NextAttemptAt == null || m.NextAttemptAt <= now)
            .Where(m => m.LockedUntil == null || m.LockedUntil <= now)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);
    }

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
        var now = GetUtcNow();
        var until = now.Add(leaseDuration);
        // 优化（二十五轮 API 扫描 EF-5 配套）：租约不再复用 GetPendingMessagesAsync——
        // 其 AsNoTracking 化后，"SELECT → 内存改 → SaveChanges"三步租约（ITM-004，
        // 见类头 remarks）的突变将静默丢失（SaveChangesAsync 无跟踪条目 = 0 行写入，
        // RetryCount 令牌兜底也随之失效）。此处内联同条件跟踪查询，租约/兜底语义不变。
        var messages = await OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending && m.RetryCount < maxRetryCount)
            .Where(m => m.NextAttemptAt == null || m.NextAttemptAt <= now)
            .Where(m => m.LockedUntil == null || m.LockedUntil <= now)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var msg in messages)
        {
            msg.LockedBy = owner;
            msg.LockedUntil = until;
        }
        await SaveChangesAsync(ct).ConfigureAwait(false);
        return messages;
    }
}
