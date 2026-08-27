using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>SQL Server outbox store with atomic lease acquisition.</summary>
/// <remarks>
/// ⚠️ <b>废弃预告（精炼裁决 2026-08-26）</b>：本类型零测试覆盖（全仓无 SqlServer 测试容器/
/// 断言）、无 src 消费——框架以未验证状态发布该方言基类。v3.0 移除；若你在使用且需要保留，
/// 请提 issue 附测试方案（Testcontainers SqlServer 覆盖租约/终态写后可解除废弃）。
/// </remarks>
[Obsolete("零测试覆盖的未验证方言基类（精炼裁决 2026-08-26）——v3.0 移除；需要保留请提 issue 附测试方案。", error: false)]
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize); // v20 C-1：override 不调 base 使基类守卫死码化——四处补齐
        // 优化（二十四轮 OP-5）：可组合 FromSql——分页由 EF SqlServer provider 生成
        // （正确放置 OFFSET…FETCH）。手工分页曾引发八轮 P1（TOP 位置非法）——整类缺陷面消灭
        // 优化（二十五轮 API 扫描 EF-3）：AsNoTracking——只读契约（接口 doc 保证不进
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
                .ToListAsync(ct).ConfigureAwait(false);
#pragma warning restore EF1002
    }
}
