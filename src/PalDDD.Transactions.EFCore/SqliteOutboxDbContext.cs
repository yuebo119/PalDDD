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
        return await OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending && m.RetryCount < maxRetryCount)
            .Where(m => m.NextAttemptAt == null || m.NextAttemptAt <= now)
            .Where(m => m.LockedUntil == null || m.LockedUntil <= now)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct)
    {
        var now = GetUtcNow();
        var until = now.Add(leaseDuration);
        var messages = await GetPendingMessagesAsync(batchSize, maxRetryCount, ct);

        foreach (var msg in messages)
        {
            msg.LockedBy = owner;
            msg.LockedUntil = until;
        }
        await SaveChangesAsync(ct);
        return messages;
    }
}
