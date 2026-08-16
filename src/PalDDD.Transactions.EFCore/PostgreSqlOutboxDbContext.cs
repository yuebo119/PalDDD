using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>PostgreSQL outbox store — atomic lease with <c>FOR UPDATE SKIP LOCKED</c>.</summary>
public abstract class PostgreSqlOutboxDbContext(DbContextOptions options) : OutboxDbContext(options)
{
    /// <inheritdoc />
    /// <remarks>
    /// P1 修复（二十一轮）：直接用 NOW()（timestamptz）——原 "NOW() AT TIME ZONE 'UTC'" 返回
    /// naive timestamp，与 timestamptz 列比较时 PG 按 session TimeZone 解释 naive 侧：
    /// session tz≠UTC 时消息资格迟滞 N 小时 + 租约早 N 小时过期 → 他实例重租 → 重复发布。
    /// Docker 官方镜像默认 UTC 掩盖；timestamptz 的绝对值语义与 session tz 无关。
    /// </remarks>
    protected override string GetNowSql() => "NOW()";

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
