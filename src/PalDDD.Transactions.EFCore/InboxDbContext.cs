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
        var record = await InboxMessages.SingleOrDefaultAsync(
            x => x.ConsumerName == consumerName && x.MessageId == messageId, ct);

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
                await SaveChangesAsync(ct);
                return record;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // 唯一约束冲突（(ConsumerName,MessageId) 已存在）—— 幂等路径：分离并重查。
                Entry(record).State = EntityState.Detached;
                record = await InboxMessages.SingleAsync(
                    x => x.ConsumerName == consumerName && x.MessageId == messageId, ct);
            }
        }

        if (record.Status == InboxStatus.Processed)
            return null;

        if (record.Status == InboxStatus.Processing
            && record.ProcessingStartedAt.HasValue
            && (now - record.ProcessingStartedAt.Value) < processingTimeout)
        {
            return null;
        }

        record.Status = InboxStatus.Processing;
        record.Attempts++;
        record.LastError = null;
        record.ProcessingStartedAt = now;
        try
        {
            await SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            Entry(record).State = EntityState.Detached;
            return null;
        }

        return record;
    }

    /// <inheritdoc/>
    async ValueTask IInboxStore.MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        AttachIfDetached(message);
        message.Status = InboxStatus.Processed;
        message.ProcessedAt = processedAt;
        message.LastError = null;
        await SaveTerminalStateAsync(message, ct);
    }

    /// <inheritdoc/>
    async ValueTask IInboxStore.MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        AttachIfDetached(message);
        message.Status = InboxStatus.Failed;
        message.LastError = failureReason;
        await SaveTerminalStateAsync(message, ct);
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

    private void AttachIfDetached(InboxMessage message)
    {
        if (Entry(message).State == EntityState.Detached)
            InboxMessages.Attach(message);
    }

    private async ValueTask SaveTerminalStateAsync(InboxMessage message, CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 记录被另一个消费者修改（例如被僵尸回收路径抢占）。
            // 我们尝试写入的终态现在已经过时 —— 分离实体并将此
            // 作为警告上报，以便运维人员关联同一 MessageId 上的并发处理。
            Entry(message).State = EntityState.Detached;
            _logger?.Warning($"Inbox: terminal state for message {message.MessageId} (consumer {message.ConsumerName}) was overwritten by a concurrent processor; the record is detached without persisting the local terminal state.");
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
            var message = inner.Message;
            if (!string.IsNullOrEmpty(message)
                && message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
