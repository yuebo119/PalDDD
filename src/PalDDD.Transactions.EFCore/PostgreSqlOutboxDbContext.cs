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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize); // v20 C-1：override 不调 base 使基类守卫死码化——四处补齐
        // ITM-659 守卫族收口：maxRetryCount 非正守卫（batchSize 守卫姊妹，镜像 PalORM/Dapper/
        // 基类）——非正值使 RetryCount < maxRetryCount 恒假（RetryCount >= 0），SQL 静默空
        // 返回无诊断（直调路径防御性 fail-fast；Options 层已校验正数）
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetryCount);
        // 优化（二十四轮 OP-5）：可组合 FromSql——OrderBy/Take 由 EF 生成 PG LIMIT
        // 优化（二十五轮 API 扫描 EF-2）：AsNoTracking——只读契约（接口 doc 保证不进
        // Mark*+SaveChanges）；违反契约的突变将静默丢失
        return await OutboxMessages
            .FromSqlRaw(BuildPendingSql(), maxRetryCount)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize); // v22 C-3：v20 C-1 GetPending 补齐后 Lease 路径漏网
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetryCount); // ITM-659：maxRetryCount 非正守卫（同上 GetPending）
        // ITM-081 修复：补 owner 空白校验（对齐 SqlServerOutboxDbContext.LeasePendingMessagesAsync
        // 同款守卫）——缺守卫时空/空白 owner 会写入 "LockedBy" 列，破坏跨方言契约一致
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        // ITM-216 修复（三十二轮）：leaseDuration 边界守卫——对照 SqlServerOutboxDbContext
        // 同款（Options 层已校验正数，此处为防御 Store 直调路径；TotalSeconds > int.MaxValue
        // 时 (int) 转换溢出为负值，生成"租约即刻过期"的失效租约）
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for PostgreSQL INTERVAL.");
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
            // v26 P3：AsNoTracking 物化——调用方（OutboxBatchProcessor）的 Mark*/ReleaseForRetry
            // 均为 FencedTarget + ExecuteUpdate 直写 DB（不依赖 ChangeTracker），租约回读实体仅
            // 用于内存 fencing 判断（LockedBy/LockedUntil 原值），无跟踪消费方；镜像
            // SqliteOutbox CAS 路径的 AsNoTracking 形态（Sqlite 租约实体本就无跟踪且全链路工作）
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
#pragma warning restore EF1002
    }
}
