using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Idempotency;

/// <summary>EF Core 幂等存储基础上下文。</summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
public abstract class IdempotencyDbContext(DbContextOptions options) : DbContext(options), IIdempotencyStore
{
    /// <summary>幂等执行记录表</summary>
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <inheritdoc/>
    public async ValueTask<IdempotencyRecord?> GetAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ValidateKeyParts(operationName, key);

        var record = await IdempotencyRecords.SingleOrDefaultAsync(
            x => x.OperationName == operationName && x.Key == key, ct);
        if (record is null)
            return null;

        // 过期记录视为不存在 —— 但不在读路径中删除（避免读 API 隐含写入与锁竞争）。
        // 删除是 GC 任务的职责（基于 ExpiresAt 索引批量清理），不嵌入读路径。
        if (record.ExpiresAt > now)
            return record;

        Entry(record).State = EntityState.Detached;
        return null;
    }

    /// <inheritdoc/>
    public async ValueTask<IdempotencyRecord?> TryStartAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        IdempotencyPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateKeyParts(operationName, key);

        var record = await IdempotencyRecords.SingleOrDefaultAsync(
            x => x.OperationName == operationName && x.Key == key, ct);

        if (record is null)
            return await TryCreateRecordAsync(operationName, key, now, policy, ct);

        if (record.ExpiresAt <= now
            || record.Status == IdempotencyRecordStatus.Failed
            || (record.Status == IdempotencyRecordStatus.Processing && record.LockedUntil <= now))
        {
            return await TryReuseRecordAsync(record, now, policy, ct);
        }

        return null;
    }

    /// <inheritdoc/>
    public async ValueTask MarkCompletedAsync(
        IdempotencyRecord record,
        ReadOnlyMemory<byte> responsePayload,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        AttachIfDetached(record);
        record.MarkCompleted(responsePayload, completedAt);
        await SaveTerminalStateAsync(record, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask MarkFailedAsync(
        IdempotencyRecord record,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        AttachIfDetached(record);
        record.MarkFailed(failureReason, failedAt);
        await SaveTerminalStateAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>配置幂等记录实体</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<IdempotencyRecord>(e =>
        {
            e.HasKey(x => new { x.OperationName, x.Key });
            e.Property(x => x.OperationName).HasMaxLength(256);
            e.Property(x => x.Key).HasMaxLength(256);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.UpdatedAt).IsConcurrencyToken();
            e.Property(x => x.ResponsePayload)
                .HasConversion(
                    value => value.HasValue ? value.Value.ToArray() : null,
                    value => value == null ? null : new ReadOnlyMemory<byte>(value));
            e.Property(x => x.Error).HasMaxLength(2048);
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => new { x.Status, x.LockedUntil });
        });
    }

    private async ValueTask<IdempotencyRecord?> TryCreateRecordAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        IdempotencyPolicy policy,
        CancellationToken ct)
    {
        var record = new IdempotencyRecord(
            operationName,
            key,
            IdempotencyRecordStatus.Processing,
            now.Add(policy.ProcessingTimeout),
            now.Add(policy.Retention),
            now);
        IdempotencyRecords.Add(record);

        try
        {
            await SaveChangesAsync(ct);
            return record;
        }
        // ITM-065：仅唯一约束冲突返回 null（语义=他人已持有）；连接故障/超时等其他
        // DbUpdateException 必须上抛，否则基础设施故障被误判为幂等冲突，错误路径放大为请求丢失。
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            Entry(record).State = EntityState.Detached;
            return null;
        }
    }

    private async ValueTask<IdempotencyRecord?> TryReuseRecordAsync(
        IdempotencyRecord record,
        DateTimeOffset now,
        IdempotencyPolicy policy,
        CancellationToken ct)
    {
        record.MarkProcessing(now.Add(policy.ProcessingTimeout), now.Add(policy.Retention), now);

        try
        {
            await SaveChangesAsync(ct);
            return record;
        }
        catch (DbUpdateConcurrencyException)
        {
            Entry(record).State = EntityState.Detached;
            return null;
        }
    }

    private void AttachIfDetached(IdempotencyRecord record)
    {
        if (Entry(record).State == EntityState.Detached)
            IdempotencyRecords.Attach(record);
    }

    /// <summary>
    /// 判定 DbUpdateException 是否为唯一约束冲突（跨 provider 鸭子类型）。
    /// <para>与 EventLogDbContext/InboxDbContext 的实现对齐（ITM-003 同型，ITM-065 引入第三处）。</para>
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

            // SQLite: Microsoft.Data.Sqlite.SqliteException 消息包含 "UNIQUE constraint"
            // P2 修复（二十一轮）：补 SqliteException 类型限定（镜像 InboxDbContext 十七轮修复，PD17）——
            // 裸消息匹配在 TryCreateRecordAsync 主路径误判为幂等冲突返回 null 是请求丢失语义（ITM-065 要防的）
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

    private async ValueTask SaveTerminalStateAsync(IdempotencyRecord record, CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // P2 文档声明（七轮评审）：终态写入的并发冲突静默吞掉（Detach 后返回）——
            // 语义：另一并发执行者已写入终态（Completed 或 Failed），本方操作已实际完成，
            // 只是终态标记被抢先。at-least-once 语义下这是可接受的（操作本身幂等）。
            // 调用方收到 Executed 返回值——DB 终态可能是另一节点写入的 Completed 或 Failed。
            Entry(record).State = EntityState.Detached;
        }
    }

    private static void ValidateKeyParts(string operationName, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }
}
