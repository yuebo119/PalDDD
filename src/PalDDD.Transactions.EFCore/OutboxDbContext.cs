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
        // 优化（二十五轮 API 扫描 EF-10）：AddRangeAsync 仅对依赖异步值生成（HiLo/Sequence）
        // 的主键必要——OutboxMessage 主键 Ulid 预设（HasConversion 字符串存储，无 DB 生成），
        // 同步 AddRange 免逐实体 async 状态机开销。
        OutboxMessages.AddRange(messages);
        return await SaveChangesAsync();
    }

    /// <inheritdoc/>
    public virtual async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        var now = GetUtcNow();
        // 优化（二十五轮 API 扫描 EF-1）：AsNoTracking 跳过 ChangeTracker 物化（免快照 +
        // 身份解析开销）。只读契约（IPalOutboxStore.GetPendingMessagesAsync doc：
        // "只用于观测/健康检查，不获取租约"，保证不进 Mark*+SaveChanges）；
        // 违反契约的突变将静默丢失（非跟踪实体不经 SaveChangesAsync 持久化）。
        return await OutboxMessages
            .AsNoTracking()
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
    /// <remarks>
    /// ITM-210 修复（三十二轮）：关系型 provider 用 ExecuteUpdate 带租约守卫；
    /// 非 SQL 可翻译 provider（InMemory 测试）回退到条件加载 + 内存突变 + SaveChanges。
    /// 守卫：WHERE Id == id AND (LockedBy IS NULL OR LockedBy == 原持有者)。
    /// </remarks>
    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        var originalOwner = message.LockedBy;
        var translated = false;
        try
        {
            var affected = OutboxMessages
                .Where(m => m.Id == message.Id
                    && (m.LockedBy == null || m.LockedBy == originalOwner))
                .ExecuteUpdate(s => s
                    .SetProperty(m => m.ProcessedAt, processedAt)
                    .SetProperty(m => m.Status, OutboxStatus.Processed)
                    .SetProperty(m => m.Error, (string?)null)
                    .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedBy, (string?)null)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null));
            translated = true;
            if (affected > 0) return;
        }
        catch (InvalidOperationException)
        {
            // ExecuteUpdate 不支持的 provider（EF InMemory 等）——回退到条件加载路径
        }

        if (!translated || true)
        {
            var tracked = OutboxMessages
                .Where(m => m.Id == message.Id
                    && (m.LockedBy == null || m.LockedBy == originalOwner))
                .FirstOrDefault();
            if (tracked is not null)
            {
                tracked.ProcessedAt = processedAt;
                tracked.Status = OutboxStatus.Processed;
                tracked.Error = null;
                tracked.NextAttemptAt = null;
                tracked.LockedBy = null;
                tracked.LockedUntil = null;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ITM-210 修复（三十二轮）：同 MarkProcessed——关系型 ExecuteUpdate 守卫 + 非关系型回退。
    /// </remarks>
    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        var error = failureReason.Length > 2040 ? failureReason[..2040] : failureReason;

        var originalOwner = message.LockedBy;
        var translated = false;
        try
        {
            var affected = OutboxMessages
                .Where(m => m.Id == message.Id
                    && (m.LockedBy == null || m.LockedBy == originalOwner))
                .ExecuteUpdate(s => s
                    .SetProperty(m => m.ProcessedAt, deadAt)
                    .SetProperty(m => m.Status, OutboxStatus.Dead)
                    .SetProperty(m => m.Error, error)
                    .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedBy, (string?)null)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null));
            translated = true;
            if (affected > 0) return;
        }
        catch (InvalidOperationException)
        {
            // 同 MarkProcessed——非关系型 provider 回退
        }

        if (!translated || true)
        {
            var tracked = OutboxMessages
                .Where(m => m.Id == message.Id
                    && (m.LockedBy == null || m.LockedBy == originalOwner))
                .FirstOrDefault();
            if (tracked is not null)
            {
                tracked.ProcessedAt = deadAt;
                tracked.Status = OutboxStatus.Dead;
                tracked.Error = error;
                tracked.NextAttemptAt = null;
                tracked.LockedBy = null;
                tracked.LockedUntil = null;
            }
        }
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

        // ITM-082 修复：存储层兜底截断（同款于 MarkDead 的 2040 截断）——Error 列 HasMaxLength(2048)，
        // 超长失败原因此前让 ExecuteUpdate 生成的 UPDATE 抛 provider 截断异常（PG 整条 UPDATE 失败 →
        // 消息滞留 Processing 且租约已过期 → 下轮重租后重试计数丢失；对齐 MarkDead 十七轮防御）
        var error = failureReason.Length > 2040 ? failureReason[..2040] : failureReason;

        var originalOwner = message.LockedBy;

        OutboxMessages
            .Where(m => m.Id == message.Id
                && (m.LockedBy == null || m.LockedBy == originalOwner))
            .ExecuteUpdate(s => s
                .SetProperty(m => m.RetryCount, m => m.RetryCount + 1)
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.Error, error)
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
    /// 构建待处理消息的公共 WHERE 模板（无分页——排序/限行由 EF 可组合 LINQ 生成）。<br/>
    /// 💡 优化（二十四轮 OP-5）：手工 LIMIT/TOP/OFFSET 分页曾引发十七轮 P1（T-SQL TOP 位置
    /// 非法）——改为 FromSqlRaw + OrderBy + Take 让 EF provider 生成各方言分页，消灭整类缺陷面。
    /// </summary>
    protected virtual string BuildPendingSql() => $$"""
        SELECT * FROM OutboxMessages
        WHERE Status = 0 AND RetryCount < {0}
          AND (NextAttemptAt IS NULL OR NextAttemptAt <= {{GetNowSql()}})
          AND (LockedUntil IS NULL OR LockedUntil <= {{GetNowSql()}})
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
