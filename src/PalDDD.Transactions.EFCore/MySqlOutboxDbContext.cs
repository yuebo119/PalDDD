using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>MySQL outbox store — atomic lease with <c>FOR UPDATE SKIP LOCKED</c> (MySQL 8.0+).</summary>
public abstract class MySqlOutboxDbContext(DbContextOptions options) : OutboxDbContext(options)
{
    /// <inheritdoc />
    protected override string GetNowSql() => "UTC_TIMESTAMP()";

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        return await OutboxMessages
            .FromSqlRaw(BuildPendingSql("LIMIT {0}"), batchSize, maxRetryCount)
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
        var until = GetUtcNow().Add(leaseDuration);
        await using var transaction = await Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var messages = await OutboxMessages
            .FromSqlRaw(
                BuildPendingSql("LIMIT {0} FOR UPDATE SKIP LOCKED"), batchSize, maxRetryCount)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var msg in messages)
        {
            msg.LockedBy = owner;
            msg.LockedUntil = until;
        }

        await SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return messages;
    }
}
