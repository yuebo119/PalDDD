using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>SQLite outbox store — single-writer, no lock hints needed (WAL mode).</summary>
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
