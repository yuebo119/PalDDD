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
        catch (DbUpdateException)
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

    private async ValueTask SaveTerminalStateAsync(IdempotencyRecord record, CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            Entry(record).State = EntityState.Detached;
        }
    }

    private static void ValidateKeyParts(string operationName, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }
}
