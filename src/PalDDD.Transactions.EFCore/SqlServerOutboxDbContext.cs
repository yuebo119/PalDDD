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
            // P1 修复（八轮评审）：T-SQL 的 TOP 只能位于 SELECT 与列列表之间，不能出现在
            // ORDER BY 之后（BuildPendingSql 把 limitClause 追加在 ORDER BY 后）——此前
            // 生成 "ORDER BY CreatedAt TOP(@p0)" 运行必抛语法异常。OFFSET…FETCH 是
            // T-SQL 2012+ 中合法位于 ORDER BY 之后的限行语法。
            .FromSqlRaw(BuildPendingSql("OFFSET 0 ROWS FETCH NEXT {0} ROWS ONLY"), batchSize, maxRetryCount)
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
