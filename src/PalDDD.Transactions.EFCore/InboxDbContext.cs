// ─────────────────────────────────────────────────────────────
// 📥 InboxDbContext — EF Core 收件箱存储（(ConsumerName,MessageId) 唯一约束）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// EF Core 收件箱存储
// ─────────────────────────────────────────────────────────────

/// <summary>EF Core 收件箱存储基础上下文。</summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
public abstract class InboxDbContext(
    DbContextOptions options,
    IPalLogger<InboxDbContext>? logger = null) : DbContext(options), IInboxStore
{
    // P3 修复（十七轮）：失败原因入库截断上限（对齐 InboxProcessor.MaxFailureReasonLength）
    // ——LastError 列上限 2048（见 OnModelCreating），调用方未截断时存储层兜底
    private const int MaxFailureReasonLength = 2000;

    private readonly IPalLogger<InboxDbContext>? _logger = logger;

    /// <summary>收件箱消息表</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <inheritdoc/>
    async ValueTask<InboxMessage?> IInboxStore.TryStartProcessingAsync(
        string consumerName,
        string messageId,
        DateTimeOffset now,
        TimeSpan processingTimeout,
        CancellationToken ct)
    {
        // v17 P1 修复：补空白键守卫（ITM-163 四姊妹中唯一漏网——Dapper:65/InMemory:32/PalORM:39
        // 均有）。空串键可创建幂等行却无法命中正常消息；契约对齐其余三实现抛 ArgumentException。
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var record = await InboxMessages.SingleOrDefaultAsync(
            x => x.ConsumerName == consumerName && x.MessageId == messageId, ct).ConfigureAwait(false);

        if (record is null)
        {
            record = new InboxMessage
            {
                ConsumerName = consumerName,
                MessageId = messageId,
                Status = InboxStatus.Processing,
                Attempts = 1,
                ReceivedAt = now,
                ProcessingStartedAt = now
            };
            InboxMessages.Add(record);

            try
            {
                await SaveChangesAsync(ct).ConfigureAwait(false);
                return record;
            }
            catch (DbUpdateException ex) when (!IsUniqueConstraintViolation(ex))
            {
                // v10 P3-1：瞬时异常（连接断/超时）上抛前 Detach——长驻 DbContext 下重试
                // Add 同键实体抛 identity conflict（对齐 EventLogDbContext 三十八轮姊妹形态）
                Entry(record).State = EntityState.Detached;
                throw;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // 唯一约束冲突（(ConsumerName,MessageId) 已存在）—— 幂等路径：分离并重查。
                // P2/P3 修复（十七轮）：回查改 SingleOrDefaultAsync + null→return null（对齐 PalORM 版）——
                // 冲突行由并发消费者插入，但其事务可能尚未提交（如 MySQL REPEATABLE READ 快照下
                // 本事务不可见），SingleAsync 此处会抛 InvalidOperationException 掩盖幂等语义；
                // 查不到按"他人正在处理"处理，返回 null 让调用方走重投递。
                Entry(record).State = EntityState.Detached;
                record = await InboxMessages.SingleOrDefaultAsync(
                    x => x.ConsumerName == consumerName && x.MessageId == messageId, ct).ConfigureAwait(false);
                if (record is null)
                    return null;
            }
        }

        if (record.Status == InboxStatus.Processed)
        {
            // v26 P3 修复：读路径无效分支 Detach——跟踪查询物化的实体滞留会在长驻 scope
            // 下随活跃 key 数累积（镜像 ProjectionCheckpointDbContext 三十八轮同分支）
            Entry(record).State = EntityState.Detached;
            return null;
        }

        if (record.Status == InboxStatus.Processing
            && record.ProcessingStartedAt.HasValue
            && (now - record.ProcessingStartedAt.Value) < processingTimeout)
        {
            // v26 P3 修复：同上——他人活跃 Processing 未超时分支
            Entry(record).State = EntityState.Detached;
            return null;
        }

        record.Status = InboxStatus.Processing;
        record.Attempts++;
        record.LastError = null;
        record.ProcessingStartedAt = now;
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            Entry(record).State = EntityState.Detached;
            return null;
        }
        catch (DbUpdateException)
        {
            // v26 P2/P3 修复：抢占路径非并发瞬时故障上抛前 Detach——record 已被变异为
            // Modified（Status=Processing 等四项），滞留 ChangeTracker 会被下次无关
            // SaveChanges 提交为幽灵 Processing 态（镜像 IdempotencyDbContext 三十八轮
            // P2 全修样板；v10 P3-1 只修了第一个 SaveChanges 新建路径）
            Entry(record).State = EntityState.Detached;
            throw;
        }

        return record;
    }

    /// <inheritdoc/>
    async ValueTask IInboxStore.MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        // v16 P3-2 姊妹对称：仅 Processing 态可终态化（对齐 Dapper SqlTemplates:211/PalORM
        // :172 的 AND status=Processing 守卫）——重复标记/Completed 后误标不再静默 revision+1。
        // 注意与 OutboxDbContext.RequeueDeadAsync 已有的同型守卫形态一致（防御深度补齐，
        // 触发需调用方序列 bug——正常管线只租约后标记一次）
        if (message.Status != InboxStatus.Processing)
            return;
        AttachIfDetached(message);
        message.Status = InboxStatus.Processed;
        message.ProcessedAt = processedAt;
        message.LastError = null;
        await SaveTerminalStateAsync(message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    async ValueTask IInboxStore.MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        if (message.Status != InboxStatus.Processing)
            return; // v16 P3-2 姊妹对称：仅 Processing 态可标失败（对齐三栈）

        // P3 修复（十七轮）：入库前截断到 2000——超出 LastError 列上限的失败原因会让
        // 终态保存本身抛 DbUpdateException，掩盖原始处理失败（存储层兜底防御）
        if (failureReason.Length > MaxFailureReasonLength)
            failureReason = failureReason[..MaxFailureReasonLength];

        AttachIfDetached(message);
        message.Status = InboxStatus.Failed;
        message.LastError = failureReason;
        await SaveTerminalStateAsync(message, ct).ConfigureAwait(false);
    }

    /// <summary>配置收件箱实体 — MessageId 唯一约束是幂等的核心</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ConsumerName, x.MessageId }).IsUnique();
            e.Property(x => x.MessageId).HasMaxLength(256);
            e.Property(x => x.ConsumerName).HasMaxLength(256);
            e.Property(x => x.LastError).HasMaxLength(2048);
            e.Property(x => x.ProcessingStartedAt).IsConcurrencyToken();
            e.HasIndex(x => x.ProcessedAt);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.Status, x.ProcessingStartedAt });
        });
    }

    /// <summary>
    /// 分离态消息附加回变更跟踪器。
    /// P3-SRC-109 声明调用方义务：message 实例由 TryStartProcessingAsync 返回后不得跨
    /// DbContext 复用——同键实例已被本上下文跟踪时 <c>Attach</c> 抛 InvalidOperationException
    ///（EF Core 跟踪冲突：同一键已有不同实例被跟踪），调用方须保证同一上下文内单实例流转。
    /// </summary>
    private void AttachIfDetached(InboxMessage message)
    {
        if (Entry(message).State == EntityState.Detached)
            InboxMessages.Attach(message);
    }

    private async ValueTask SaveTerminalStateAsync(InboxMessage message, CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
            // v25 P3 行为族 B6：保存成功后 Detach（与 IdempotencyDbContext B5 同族修复；
            // 镜像 ProjectionCheckpointDbContext:115-118）——终态已落库，长驻 ChangeTracker
            // 不残留本条目（无界增长）；后续对同一 message 的 Mark* 自带 AttachIfDetached 兜底。
            Entry(message).State = EntityState.Detached;
        }
        catch (DbUpdateConcurrencyException)
        {
            // 记录被另一个消费者修改（例如被僵尸回收路径抢占）。
            // 我们尝试写入的终态现在已经过时 —— 分离实体并将此
            // 作为警告上报，以便运维人员关联同一 MessageId 上的并发处理。
            Entry(message).State = EntityState.Detached;
            _logger?.Warning($"Inbox: terminal state for message {message.MessageId} (consumer {message.ConsumerName}) was overwritten by a concurrent processor; the record is detached without persisting the local terminal state.");
        }
        catch (DbUpdateException)
        {
            // v27 P2 修复：非并发瞬时故障上抛前 Detach——message 已被变异为 Modified（终态
            // 字段），滞留 ChangeTracker 会被下次无关 SaveChanges 幽灵提交（镜像
            // IdempotencyDbContext.SaveTerminalStateAsync 三十八轮全修样板）
            Entry(message).State = EntityState.Detached;
            throw;
        }
    }

    /// <summary>
    /// 判断 DbUpdateException 是否由唯一约束冲突引起（(ConsumerName,MessageId) 重复插入）。
    /// <para>通过反射鸭子类型读取 provider 异常属性，避免对具体 provider 包的硬依赖。
    /// 与 EventLogDbContext.IsUniqueConstraintViolation 实现对齐（ITM-003）。</para>
    /// <para>非唯一约束的 DbUpdateException（字段长度溢出/null/连接断开等）不被捕获，原样向上传播。</para>
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            var type = inner.GetType();
            var typeName = type.Name;

            // PostgreSQL: Npgsql.PostgresException.SqlState == "23505"
            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(inner) is string sqlState
                && sqlState == "23505")
            {
                return true;
            }

            // MySQL: MySqlException.Number == 1062（ER_DUP_ENTRY）或 1586
            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int mysqlNumber
                && (mysqlNumber == 1062 || mysqlNumber == 1586))
            {
                return true;
            }

            // SQL Server: SqlException.Number == 2601 (unique index) 或 2627 (unique constraint / PK)
            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int sqlServerNumber
                && (sqlServerNumber == 2601 || sqlServerNumber == 2627))
            {
                return true;
            }

            // SQLite: SqliteException 消息包含 "UNIQUE constraint"
            // P3 修复（十七轮）：补 typeName 前置——原兜底无类型约束，任意 provider 的
            // 异常消息恰好含 "UNIQUE constraint" 文案（如自定义异常/ORM 透传）会被
            // 误判为幂等冲突走重查路径；限定 SqliteException 后其余 provider 原样传播
            var message = inner.Message;
            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(message)
                && message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
