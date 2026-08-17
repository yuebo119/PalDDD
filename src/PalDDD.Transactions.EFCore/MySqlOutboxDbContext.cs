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
        // 优化（二十五轮 API 扫描 EF-4）：AsNoTracking——只读契约（接口 doc 保证不进
        // Mark*+SaveChanges）；违反契约的突变将静默丢失
        return await OutboxMessages
            .FromSqlRaw(BuildPendingSql(), maxRetryCount)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 优化（二十五轮 API 扫描 EF-7）：PalORM 同款两步法替代"SELECT FOR UPDATE SKIP LOCKED →
    /// 逐行 ExecuteSqlRawAsync UPDATE"的 N+1 往返——步骤 1 单条 JOIN-UPDATE 原子锁定批次
    /// （FOR UPDATE SKIP LOCKED 行锁保留在 JOIN 子查询内，多实例互斥语义不变），
    /// 步骤 2 按精确租约标识回读。两步合计固定 2 次数据库往返，与批次大小无关（原为 1+N）。
    /// <para>
    /// 时钟语义（对齐 PalOrmOutboxStore MySQL 路径）：now/until 全部为应用侧 DateTimeOffset 参数——
    /// 单一时钟源后，二十一轮修复的"MySQL 版读侧 DB 时钟 + SET 侧应用时钟混用"漂移问题不复存在。
    /// SET 侧放弃 DB 时钟 DATE_ADD(UTC_TIMESTAMP())：其写入的精确值应用侧不知道，
    /// 回读 <c>WHERE LockedUntil = DATE_ADD(...)</c> 因微秒差无法精确匹配；
    /// 统一 <c>@until</c> 参数（MySqlConnector 映射 DATETIME(6)，ToTimeParam 方言分派已证此路径）
    /// 以应用时钟一致性换精确回读（PalORM 同款取舍）。
    /// </para>
    /// <para>
    /// ⚠️ 已知限制（八轮评审 P3，对齐 PalORM :78-84 声明不修）：回读按 (LockedBy, LockedUntil)
    /// 匹配——同一 owner 在同一 tick（until 完全相等，如 FakeTimeProvider 冻结时间）发起两次租约时，
    /// 第二次回读会混入第一次已锁定的批次。生产触发条件近乎为零（DATETIME(6) 微秒精度 +
    /// 单 owner 串行租约）；PG/SqlServer 走 RETURNING/OUTPUT 单语句天然免疫。
    /// </para>
    /// <para>
    /// 回读物化保持跟踪（与 PG/SqlServer 租约路径同因）：调用方 OutboxBatchProcessor 的
    /// MarkProcessed/MarkDead 是内存突变 + SaveChangesAsync 持久化（仅 ReleaseForRetry 十七轮
    /// 改为 ExecuteUpdate），依赖 ChangeTracker——AsNoTracking 会使 Mark* 突变静默丢失
    /// （消息永留租约态 → 租约过期重租 → 重复发布）。AcceptAllChanges 不再需要：旧路径
    /// FromSqlRaw 物化后内存改 LockedBy/LockedUntil 产生 Modified 脏状态需归位；
    /// 两步法回读值即 DB 真值，物化即 Unchanged。
    /// </para>
    /// <para>
    /// 事务边界（对齐 PalORM）：两条语句自动提交，无显式事务——JOIN-UPDATE 单语句原子，
    /// 回读按精确租约标识 (owner, until) 过滤，不依赖与 UPDATE 的快照隔离。
    /// </para>
    /// </remarks>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
        int batchSize,
        string owner,
        TimeSpan leaseDuration,
        int maxRetryCount,
        CancellationToken ct)
    {
        // P3 修复（二十六轮验证轮 W1 前在 nit）：owner 空白守卫——对齐 PG（:53）/SqlServer（:43）
        // 的 ITM-081 跨方言对齐（MySQL 漏网）；空 owner 产生无归属租约
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        // ITM-167 修复：leaseDuration 边界守卫——leaseDuration 非正时租约秒数非正
        // （立即过期/永不过期语义错乱）；TotalSeconds 超过 int.MaxValue 时
        // until = now.Add(leaseDuration) 的秒数语义溢出。Options 层已校验正数，
        // 此处是 Store 直调路径的防御性 fail-fast（与 Options 层校验不重复，
        // 各自覆盖 DI 启动期与运行时直调两类入口）。
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for the lease LockedUntil value.");

        var now = GetUtcNow();
        var until = now.Add(leaseDuration);

        // 步骤 1（EF-7）：单条 JOIN-UPDATE 原子租约——原 SELECT 版的 FOR UPDATE SKIP LOCKED
        // 行锁移入 JOIN 子查询（MySQL 8.0+ 派生表锁语义），多实例互斥不变。
        // ⚠️ 占位符修正（二十五轮顺带）：旧版 $$"""...{{0}}...""" 中 {{0}} 是 C# 插值
        // （int 0 求值内联，非 FromSqlRaw 参数占位符）——生成 "RetryCount < 0"（恒假）+
        // "LIMIT 1"，maxRetryCount/batchSize 参数被忽略、租约恒空。本版用非插值 raw string，
        // {N} 为字面占位符（值全部参数化，无用户输入拼接——EF1003 豁免同旧版模式）。
#pragma warning disable EF1003
        // 注意：带 ct 必须走 (sql, IEnumerable<object>, ct) 重载——params 版会把 ct 装箱进
        // SQL 参数数组（取消语义静默失效）
        await Database.ExecuteSqlRawAsync(
            """
            UPDATE OutboxMessages t
            JOIN (
                SELECT id FROM OutboxMessages
                WHERE Status = 0 AND RetryCount < {0}
                  AND (NextAttemptAt IS NULL OR NextAttemptAt <= {1})
                  AND (LockedUntil IS NULL OR LockedUntil <= {1})
                ORDER BY CreatedAt
                LIMIT {2} FOR UPDATE SKIP LOCKED
            ) AS sub ON t.id = sub.id
            SET t.LockedBy = {3}, t.LockedUntil = {4}
            """,
            new object[] { maxRetryCount, now, batchSize, owner, until }, ct).ConfigureAwait(false);
#pragma warning restore EF1003

        // 步骤 2：按精确租约标识 (owner, until) 回读——跟踪物化（Mark* 语义所需，见 remarks）；
        // {0}/{1} 为 FromSqlRaw 字面参数占位符（值全部参数化，无用户输入拼接——EF1002 豁免）
#pragma warning disable EF1002
        return await OutboxMessages
            .FromSqlRaw(
                """
                SELECT * FROM OutboxMessages
                WHERE LockedBy = {0} AND LockedUntil = {1}
                ORDER BY CreatedAt
                """,
                owner, until)
            .ToListAsync(ct).ConfigureAwait(false);
#pragma warning restore EF1002
    }
}
