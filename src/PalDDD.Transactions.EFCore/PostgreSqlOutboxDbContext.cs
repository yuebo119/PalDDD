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
    /// <remarks>PG 使用双引号引用标识符以区分大小写（WHERE-only——分页由可组合 LINQ 生成，二十四轮 OP-5）。</remarks>
    protected override string BuildPendingSql() => $$"""
        SELECT * FROM "OutboxMessages"
        WHERE "Status" = 0 AND "RetryCount" < {0}
          AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {{GetNowSql()}})
          AND ("LockedUntil" IS NULL OR "LockedUntil" <= {{GetNowSql()}})
        """;

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        // 优化（二十四轮 OP-5）：可组合 FromSql——OrderBy/Take 由 EF 生成 PG LIMIT
        // 优化（二十五轮 API 扫描 EF-2）：AsNoTracking——只读契约（接口 doc 保证不进
        // Mark*+SaveChanges）；违反契约的突变将静默丢失
        return await OutboxMessages
            .FromSqlRaw(BuildPendingSql(), maxRetryCount)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .AsNoTracking()
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
        // ITM-081 修复：补 owner 空白校验（对齐 SqlServerOutboxDbContext.LeasePendingMessagesAsync
        // 同款守卫）——缺守卫时空/空白 owner 会写入 "LockedBy" 列，破坏跨方言契约一致
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
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
