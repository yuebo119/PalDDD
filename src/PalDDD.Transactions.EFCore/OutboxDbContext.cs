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
        // P1 修复（二十一轮）：存储层兜底截断——Error 列 HasMaxLength(2048)，超长让 SaveChanges
        // 抛截断异常且毒实体滞留 ChangeTracker（对齐 InboxDbContext.MarkFailedAsync 十七轮防御）
        message.Error = failureReason.Length > 2040 ? failureReason[..2040] : failureReason;
        message.NextAttemptAt = null;
        message.LockedBy = null;
        message.LockedUntil = null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// P2 修复（八轮评审）：改用 <c>ExecuteUpdate</c> 生成带守卫的单条 UPDATE——
    /// <c>WHERE Id == id AND (LockedBy IS NULL OR LockedBy == 原持有者)</c>（原持有者从入参捕获）。
    /// 此前的"内存修改 + 后续 SaveChangesAsync"模式在租约被抢占（过期后他实例已 re-lease）
    /// 时会用 <c>LockedBy = null</c> 覆盖新持有者的租约；守卫下被抢占时影响 0 行，不覆盖。<br/>
    /// RetryCount 递增与状态变更在同一 SQL 内原子完成，不再依赖后续 <c>SaveChangesAsync</c>；
    /// 入参 <paramref name="message"/> 不再被修改——若其为 ChangeTracker 跟踪实体，同步改内存
    /// 会在后续 SaveChangesAsync 因 RetryCount 并发令牌失配抛出假冲突。<br/>
    /// ⚠️ <b>Provider 约束（九轮评审声明）</b>：<c>ExecuteUpdate</c> 需要关系型 provider
    /// （SQLite/PG/MySQL/SqlServer 等）；EF InMemory/Cosmos 不支持，本方法会抛
    /// <see cref="InvalidOperationException"/>——非关系型测试场景请用 Dapper/PalORM/InMemory 适配器。
    /// </remarks>
    public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        var originalOwner = message.LockedBy;

        OutboxMessages
            .Where(m => m.Id == message.Id
                && (m.LockedBy == null || m.LockedBy == originalOwner))
            .ExecuteUpdate(s => s
                .SetProperty(m => m.RetryCount, m => m.RetryCount + 1)
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.Error, failureReason)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                .SetProperty(m => m.LockedBy, (string?)null)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null));
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

    /// <summary>配置发件箱消息实体。</summary>
    /// <remarks>
    /// ⚠️ <b>派生类注意（P3-4）</b>：重写 <c>OnModelCreating</c> 时必须调用
    /// <c>base.OnModelCreating(modelBuilder)</c>，否则 <c>RetryCount.IsConcurrencyToken()</c>
    /// 等配置会静默失效。参考 <c>EventLogDbContext.OnModelCreating</c> 的调用模式。
    /// </remarks>
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

            // 🔴 P1 修复 (2026-07-28): RetryCount 作为并发令牌，与 PalORM 的 [ConcurrencyCheck]RetryCount 对齐。
            // OutboxMessage 领域类型无 Version 字段，使用 RetryCount（int，PALORM012 兼容）作为乐观并发版本号。
            // 当两个 processor 同时拉取并 lease 同一条消息时，SaveChangesAsync 第二次提交会因 RetryCount 不匹配
            // 抛出 DbUpdateConcurrencyException，从而避免重复处理。
            e.Property(x => x.RetryCount).IsConcurrencyToken();
        });
    }
}
