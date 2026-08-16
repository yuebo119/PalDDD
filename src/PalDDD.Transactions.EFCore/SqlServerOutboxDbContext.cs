using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>SQL Server outbox store with atomic lease acquisition.</summary>
public abstract class SqlServerOutboxDbContext(DbContextOptions options) : OutboxDbContext(options)
{
    /// <inheritdoc />
    /// <remarks>
    /// P1 修复（二十一轮）：SYSDATETIMEOFFSET()（datetimeoffset）——原 SYSUTCDATETIME() 返回
    /// datetime2，与 datetimeoffset 列（DateTimeOffset 映射）比较时按类型优先级隐式转换
    /// 附加服务器本地偏移：服务器 tz≠UTC 时同 PG naive 时间戳一样产生租约漂移 [推断]。
    /// datetimeoffset 间比较按 UTC 瞬时值，与服务器时区无关。
    /// </remarks>
    protected override string GetNowSql() => "SYSDATETIMEOFFSET()";

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
        // ITM-167 修复：leaseSeconds 边界守卫（同 MySqlOutboxDbContext——防御 Store 直调
        // 路径的负值/超大值，Options 层已校验正数，此处为运行时 fail-fast）。
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for SQL Server DATEADD.");

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
