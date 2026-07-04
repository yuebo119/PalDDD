using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>SQL Server outbox store with atomic lease acquisition.</summary>
public abstract class SqlServerOutboxDbContext(DbContextOptions options) : OutboxDbContext(options)
{
    /// <inheritdoc />
    protected override string GetNowSql() => "SYSUTCDATETIME()";

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        return await OutboxMessages
            .FromSqlRaw(BuildPendingSql("TOP({0})"), batchSize, maxRetryCount)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var leaseSeconds = (int)Math.Ceiling(leaseDuration.TotalSeconds);
        var nowSql = GetNowSql();
#pragma warning disable EF1002 // FromSqlRaw with trusted provider-specific NOW expression
        return await OutboxMessages
            .FromSqlRaw(
                $@";WITH candidates AS (
                      SELECT TOP({{0}}) *
                      FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                      WHERE Status = 0 AND RetryCount < {{1}}
                        AND (NextAttemptAt IS NULL OR NextAttemptAt <= {nowSql})
                        AND (LockedUntil IS NULL OR LockedUntil <= {nowSql})
                      ORDER BY CreatedAt
                  )
                  UPDATE candidates
                  SET LockedBy = {{2}}, LockedUntil = DATEADD(second, {{3}}, {nowSql})
                  OUTPUT INSERTED.*", batchSize, maxRetryCount, owner, leaseSeconds)
                .ToListAsync(ct);
#pragma warning restore EF1002
    }
}
