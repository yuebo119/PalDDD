// ─────────────────────────────────────────────────────────────
// 📤 OutboxDbContext — EF Core 发件箱存储（租约 + RetryCount 原子递增）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>EF Core 发件箱存储基础上下文。</summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
public abstract class OutboxDbContext(DbContextOptions options) : DbContext(options), IPalOutboxStore
{
    /// <summary>发件箱消息表</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <inheritdoc/>
    void IPalOutboxStore.AddMessage(OutboxMessage message)
        => OutboxMessages.Add(message);

    /// <inheritdoc/>
    async ValueTask<int> IPalOutboxStore.AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return 0;
        await OutboxMessages.AddRangeAsync(messages);
        return await SaveChangesAsync();
    }

    /// <inheritdoc/>
    public virtual async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        var now = GetUtcNow();
        return await OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending
                && m.RetryCount < maxRetryCount
                && (m.NextAttemptAt == null || m.NextAttemptAt <= now)
                && (m.LockedUntil == null || m.LockedUntil <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public abstract ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct);

    /// <inheritdoc/>
    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        message.ProcessedAt = processedAt;
        message.Status = OutboxStatus.Processed;
        message.Error = null;
        message.NextAttemptAt = null;
        message.LockedBy = null;
        message.LockedUntil = null;
    }

    /// <inheritdoc/>
    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        message.ProcessedAt = deadAt;
        message.Status = OutboxStatus.Dead;
        message.Error = failureReason;
        message.NextAttemptAt = null;
        message.LockedBy = null;
        message.LockedUntil = null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// RetryCount 在此方法内递增，确保与状态变更在同一 SaveChangesAsync 中原子持久化。
    /// 调用方（OutboxBatchProcessor）无需单独维护 RetryCount——存储保证计数与状态一致。
    /// </remarks>
    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        message.RetryCount++;
        message.Status = OutboxStatus.Pending;
        message.ProcessedAt = null;
        message.Error = failureReason;
        message.NextAttemptAt = nextAttemptAt;
        message.LockedBy = null;
        message.LockedUntil = null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 使用 <c>ExecuteUpdateAsync</c>（<c>RelationalQueryableExtensions</c> 扩展）
    /// 直接生成 UPDATE SQL，绕过 ChangeTracker，AOT 友好且无追踪开销。<br/>
    /// RetryCount 保留失败历史不重置；仅作用于 Status == Dead 的行。
    /// </remarks>
    public async ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retriedBy);
        var now = GetUtcNow();
        var audit = $"requeued by {retriedBy} at {now:O}";

        return await OutboxMessages
            .Where(m => m.Id == messageId && m.Status == OutboxStatus.Dead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.Error, audit)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                .SetProperty(m => m.LockedBy, (string?)null)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null), ct);
    }

    /// <inheritdoc/>
    async ValueTask<int> IPalOutboxStore.SaveChangesAsync(CancellationToken ct)
        => await SaveChangesAsync(ct);

    /// <summary>获取当前 UTC 时间，派生测试上下文可重写以控制时间</summary>
    protected virtual DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

    /// <summary>获取数据库特定的 NOW 函数（用于原始 SQL 查询），派生 provider 子类可重写</summary>
    protected virtual string GetNowSql() => "CURRENT_TIMESTAMP";

    /// <summary>
    /// 构建待处理消息的公共 WHERE + ORDER + LIMIT 模板。<br/>
    /// 💡 派生 provider 子类可使用此模板减少重复，仅需提供自己的 <see cref="GetNowSql"/>。
    /// </summary>
    /// <param name="limitClause">LIMIT 语法（如 "LIMIT {0}" 或 "TOP({0})"）</param>
    protected virtual string BuildPendingSql(string limitClause) => $$"""
        SELECT * FROM OutboxMessages
        WHERE Status = 0 AND RetryCount < {1}
          AND (NextAttemptAt IS NULL OR NextAttemptAt <= {{GetNowSql()}})
          AND (LockedUntil IS NULL OR LockedUntil <= {{GetNowSql()}})
        ORDER BY CreatedAt
        {{limitClause}}
        """;

    /// <summary>配置发件箱消息实体</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt, x.CreatedAt });
            e.Property(x => x.Id).HasConversion(v => v.ToString(), v => PalUlid.Parse(v));
            e.Property(x => x.CorrelationId).HasConversion(v => v.HasValue ? v.Value.ToString() : default(string?), v => v != null ? PalUlid.Parse(v) : default(PalUlid?));
            e.Property(x => x.CausationId).HasConversion(v => v.HasValue ? v.Value.ToString() : default(string?), v => v != null ? PalUlid.Parse(v) : default(PalUlid?));
            e.Property(x => x.Type).HasMaxLength(512);
            e.Property(x => x.ContentType).HasMaxLength(128);
            e.Property(x => x.TraceParent).HasMaxLength(128);
            e.Property(x => x.TraceState).HasMaxLength(512);
            e.Property(x => x.LockedBy).HasMaxLength(256);
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.Error).HasMaxLength(2048);
        });
    }
}
