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
        // 优化（二十四轮 OP-5）：可组合 FromSql——分页由 EF 生成（删手工 LIMIT）
        return await OutboxMessages
            .FromSqlRaw(BuildPendingSql(), maxRetryCount)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// P2/P3 修复（十七轮）：租约 SET 侧改 DB 时钟——读侧条件已用 <see cref="GetNowSql()"/>
    /// （UTC_TIMESTAMP()，DB 时钟），原 SET 侧 <c>until = GetUtcNow().Add(leaseDuration)</c>
    /// 取应用时钟：应用与 DB 时钟漂移时写入的租约会"立即过期"（应用慢）或"超长滞留"
    /// （应用快），且与读侧判定不同源。UPDATE 内联
    /// <c>DATE_ADD(UTC_TIMESTAMP(), INTERVAL {1} SECOND)</c>（{1}=租约秒数参数）与读侧同源。
    /// <para>
    /// 行锁由 SELECT ... FOR UPDATE SKIP LOCKED 持有，无需 SaveChangesAsync 的 RetryCount
    /// 并发令牌；内存对象同步应用时钟近似值供调用方快照（ReleaseForRetry 守卫依赖
    /// LockedBy），持久化真值以 DB 时钟为准——调用方后续 Mark* 路径都会覆盖这些字段。
    /// </para>
    /// <para>
    /// P3 修复（十七轮）：租约路径不调用 SaveChangesAsync（EF 乐观令牌管线），
    /// <see cref="DbUpdateConcurrencyException"/> 在此不可达，无需 SagaStateDbContext
    /// 式的并发冲突降级 catch——并发互斥由 FOR UPDATE SKIP LOCKED 行锁结构性保证
    /// （他实例跳过已锁行，不产生租约冲突）。
    /// </para>
    /// </remarks>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct)
    {
        // ITM-167 修复：leaseSeconds 边界守卫——leaseDuration 非正时租约秒数非正
        // （立即过期/永不过期语义错乱）；TotalSeconds 超过 int.MaxValue 时
        // (int)Math.Ceiling 在 unchecked 下回绕为负值，写入 INTERVAL 负数秒。Options 层
        // 已校验正数，此处是 Store 直调路径的防御性 fail-fast（与 Options 层校验不重复，
        // 各自覆盖 DI 启动期与运行时直调两类入口）。
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for MySQL DATE_ADD.");

        await using var transaction = await Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var messages = await OutboxMessages
            // 租约查询不可组合（FOR UPDATE SKIP LOCKED 行锁必须在子查询内 LIMIT 前锁定）——
            // 二十四轮 OP-5 收窄 BuildPendingSql 后此处方言专属全量内联。
            // EF1002 豁免：{{0}}/{{1}} 是 FromSqlRaw 参数占位符（值全部参数化），
            // GetNowSql() 为代码内字面量常量（UTC_TIMESTAMP()，PD12 框架边界）——无用户输入拼接
#pragma warning disable EF1002
            .FromSqlRaw(
                $$"""
                SELECT * FROM OutboxMessages
                WHERE Status = 0 AND RetryCount < {{0}}
                  AND (NextAttemptAt IS NULL OR NextAttemptAt <= {{GetNowSql()}})
                  AND (LockedUntil IS NULL OR LockedUntil <= {{GetNowSql()}})
                ORDER BY CreatedAt
                LIMIT {{1}} FOR UPDATE SKIP LOCKED
                """,
                maxRetryCount, batchSize)
#pragma warning restore EF1002
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // SET 侧 DB 时钟（见 remarks）；租约秒数向上取整避免亚秒租约被 ROUND 掉
        var leaseSeconds = (int)Math.Ceiling(leaseDuration.TotalSeconds);
        var untilApprox = GetUtcNow().Add(leaseDuration);
        foreach (var msg in messages)
        {
            msg.LockedBy = owner;
            msg.LockedUntil = untilApprox;
            // EF1003 豁免说明（十七轮）：{0}/{1}/{2} 是 FromSqlRaw 参数占位符（值全部参数化），
            // GetNowSql() 为代码内字面量常量（UTC_TIMESTAMP()，PD12 框架边界）——无用户输入拼接
#pragma warning disable EF1003
            await Database.ExecuteSqlRawAsync(
                "UPDATE OutboxMessages SET LockedBy = {0}, LockedUntil = DATE_ADD("
                    + GetNowSql() + ", INTERVAL {1} SECOND) WHERE Id = {2}",
                owner, leaseSeconds, msg.Id.ToString()).ConfigureAwait(false);
#pragma warning restore EF1003
        }

        // P2 修复（十八轮验证轮 F3）：租约改 ExecuteSqlRaw 后，FromSqlRaw 物化的跟踪实体
        // 残留 Modified 脏状态（LockedBy/LockedUntil 内存值未经 SaveChanges 落库）——
        // 后续调用方 SaveChangesAsync 会带着脏状态写入：与 ReleaseForRetry 的 ExecuteUpdate
        // （RetryCount 已在 DB +1）叠加时 RetryCount 并发令牌失配，DbUpdateConcurrencyException
        // 使同批后续消息的 Mark* 一并回滚（EF SaveChanges 整批原子）→ 重复发布。
        // AcceptAllChanges 把跟踪状态归位 Unchanged（DB 已是租约真值，内存近似值仅守卫快照用）。
        ChangeTracker.AcceptAllChanges();

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return messages;
    }
}
