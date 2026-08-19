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
        return await SaveChangesAsync().ConfigureAwait(false);
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
            .ToListAsync(ct).ConfigureAwait(false);
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
    /// ITM-210 修复（三十二轮守卫 → 三十四轮 token 化）：关系型 provider 用 ExecuteUpdate 带租约守卫；
    /// 非 SQL 可翻译 provider（InMemory 测试）回退到条件加载 + 内存突变 + SaveChanges。<br/>
    /// <b>租约 token（三十四轮）</b>：持租调用方（<c>message.LockedBy</c> 非空）的终态写要求行内
    /// <c>(LockedBy, LockedUntil)</c> 与租约时捕获的标识对<b>完全匹配</b>——租约过期被重租（同 owner
    /// 复用或他 owner 接手）后，旧 worker 的终态写影响 0 行（<c>LockedUntil</c> 随每次租约单调变化，
    /// 充当 fencing token，免 DDL 加列）；无租约直呼（LockedBy 为 null，运维/测试路径）仅当行当前
    /// 未被租（<c>LockedBy IS NULL</c>）时放行。
    /// </remarks>
    public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            var affected = FencedTarget(message)
                .ExecuteUpdate(s => s
                    .SetProperty(m => m.ProcessedAt, processedAt)
                    .SetProperty(m => m.Status, OutboxStatus.Processed)
                    .SetProperty(m => m.Error, (string?)null)
                    .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedBy, (string?)null)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null));
            if (affected > 0) return;
        }
        catch (InvalidOperationException)
        {
            // ExecuteUpdate 不支持的 provider（EF InMemory 等）——回退到条件加载路径
        }

        // 兜底路径：ExecuteUpdate 未命中（行不存在/token 拒绝）或 provider 不支持——
        // 条件加载带同款 token 守卫（三十二轮修复复审：清理原 `!translated || true` 恒真条件）
        {
            var tracked = FencedTarget(message).FirstOrDefault();
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
    /// ITM-210 修复（三十二轮守卫 → 三十四轮 token 化）：同 <see cref="MarkProcessed"/>——
    /// 租约 token 匹配守卫 + 非关系型回退。
    /// </remarks>
    public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        var error = failureReason.Length > 2040 ? failureReason[..2040] : failureReason;

        try
        {
            var affected = FencedTarget(message)
                .ExecuteUpdate(s => s
                    .SetProperty(m => m.ProcessedAt, deadAt)
                    .SetProperty(m => m.Status, OutboxStatus.Dead)
                    .SetProperty(m => m.Error, error)
                    .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedBy, (string?)null)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null));
            if (affected > 0) return;
        }
        catch (InvalidOperationException)
        {
            // 同 MarkProcessed——非关系型 provider 回退
        }

        // 兜底路径：同 MarkProcessed（清理原 `!translated || true` 恒真条件）
        {
            var tracked = FencedTarget(message).FirstOrDefault();
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

    /// <summary>
    /// 租约 token 守卫目标集——持租调用方匹配 (LockedBy, LockedUntil) 标识对；
    /// 无租约直呼（LockedBy 为 null）仅放行当前未被租的行。
    /// </summary>
    /// <remarks>三十四轮 ITM-210 落地：原 <c>LockedBy IS NULL OR LockedBy == 原持有者</c> 守卫的
    /// "NULL 放行"分支正是 fencing 缺口——租约被释放（ReleaseForRetry/RequeueDead）后旧 worker
    /// 的终态写仍会命中；同 owner 复用（worker 重启）亦无防护。<c>LockedUntil</c> 随每次租约
    /// 单调变化（重租必在过期后，<c>新 until = 更晚的 now + duration &gt; 旧 until</c>），以
    /// 微秒精度存储（PG timestamptz / SQLite TEXT "O" 格式）下充当免 DDL 的 fencing token。</remarks>
    private IQueryable<OutboxMessage> FencedTarget(OutboxMessage message)
    {
        var originalOwner = message.LockedBy;
        var originalUntil = message.LockedUntil;
        var target = OutboxMessages.Where(m => m.Id == message.Id);
        return originalOwner is null
            ? target.Where(m => m.LockedBy == null)
            : target.Where(m => m.LockedBy == originalOwner && m.LockedUntil == originalUntil);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// P2 修复（八轮评审）：<c>ExecuteUpdate</c> 生成带守卫的单条 UPDATE；三十四轮 ITM-210
    /// 升级为租约 token 守卫（<c>(LockedBy, LockedUntil)</c> 标识对匹配，见
    /// <see cref="FencedTarget"/>）——租约过期被重租/释放后，旧 worker 的失败释放影响 0 行，
    /// 不再清掉新租约或误增 retry_count。<br/>
    /// RetryCount 递增与状态变更在同一 SQL 内原子完成；入参 <paramref name="message"/>
    /// 不被修改——若其为 ChangeTracker 跟踪实体，同步改内存会在后续 SaveChangesAsync
    /// 因 RetryCount 并发令牌失配抛出假冲突。<br/>
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

        FencedTarget(message)
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
        // ITM-216 修复（三十二轮）：retriedBy 截断兜底（截断族 2040）——Error 列上限 2048，
        // 超长 retriedBy 使 audit 串超列，ExecuteUpdateAsync 抛截断异常（对齐 MarkDead/ReleaseForRetry）
        var owner = retriedBy.Length > 256 ? retriedBy[..256] : retriedBy;
        var now = GetUtcNow();
        var audit = $"requeued by {owner} at {now:O}";

        return await OutboxMessages
            .Where(m => m.Id == messageId && m.Status == OutboxStatus.Dead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.Error, audit)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                .SetProperty(m => m.LockedBy, (string?)null)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    async ValueTask<int> IPalOutboxStore.SaveChangesAsync(CancellationToken ct)
        => await SaveChangesAsync(ct).ConfigureAwait(false);

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
