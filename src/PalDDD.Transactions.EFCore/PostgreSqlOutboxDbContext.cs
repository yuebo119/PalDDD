using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>PostgreSQL outbox store — atomic lease with <c>FOR UPDATE SKIP LOCKED</c>.</summary>
public abstract class PostgreSqlOutboxDbContext(DbContextOptions options) : OutboxDbContext(options)
{
    /// <inheritdoc />
    protected override string GetNowSql() => "NOW() AT TIME ZONE 'UTC'";

    /// <inheritdoc />
    /// <remarks>PG 使用双引号引用标识符以区分大小写。</remarks>
    protected override string BuildPendingSql(string limitClause) => $$"""
        SELECT * FROM "OutboxMessages"
        WHERE "Status" = 0 AND "RetryCount" < {1}
          AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {{GetNowSql()}})
          AND ("LockedUntil" IS NULL OR "LockedUntil" <= {{GetNowSql()}})
        ORDER BY "CreatedAt"
        {{limitClause}}
        """;

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
        var sec = (int)Math.Ceiling(leaseDuration.TotalSeconds);
        var nowSql = GetNowSql();
#pragma warning disable EF1002 // FromSqlRaw with trusted provider-specific NOW expression
        return await OutboxMessages
            .FromSqlRaw(
                $@"UPDATE ""OutboxMessages""
                  SET ""LockedBy"" = {{2}}, ""LockedUntil"" = {nowSql} + ({{3}} * INTERVAL '1 second')
                  WHERE ""Id"" IN (
                      SELECT ""Id"" FROM ""OutboxMessages""
                      WHERE ""Status"" = 0 AND ""RetryCount"" < {{1}}
                        AND (""NextAttemptAt"" IS NULL OR ""NextAttemptAt"" <= {nowSql})
                        AND (""LockedUntil"" IS NULL OR ""LockedUntil"" <= {nowSql})
                      ORDER BY ""CreatedAt""
                      LIMIT {{0}}
                      FOR UPDATE SKIP LOCKED
                  )
                  RETURNING *", batchSize, maxRetryCount, owner, sec)
            .ToListAsync(ct);
#pragma warning restore EF1002
    }
}
