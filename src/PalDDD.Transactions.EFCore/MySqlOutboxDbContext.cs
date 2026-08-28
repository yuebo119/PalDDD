using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>MySQL outbox store — atomic lease with <c>FOR UPDATE SKIP LOCKED</c> (MySQL 8.0+).</summary>
/// <remarks>
/// ⚠️ 双时钟源（v8 声明）：GetPending 用 DB 时钟（UTC_TIMESTAMP）而 Lease 用应用时钟
/// （GetUtcNow）——时钟漂移时观测与租约路径的资格窗口不一致（观测侧容忍，无正确性影响）。
/// <para>
/// ⚠️ <b>MySQL 版本兼容声明（v38 P3，镜像姊妹 PalOrmOutboxStore.cs:112-116 的 v13 分叉声明）</b>：
/// 本栈租约 SQL（<see cref="LeasePendingMessagesAsync"/> 步骤 1）为 JOIN + <b>派生表内</b>
/// <c>FOR UPDATE SKIP LOCKED</c>——行锁位于派生表（derived table）子查询内，依赖 MySQL 派生表
/// 锁语义；MySQL 8.0.18 以下版本对派生表物化/锁下推行为有差异，SKIP LOCKED 可能失效。锁失效时
/// 退化为 last-writer-wins（并发双 worker 后写覆盖先写、败者回读空批，消息延迟至下轮租约）。
/// 正确性由 <c>(LockedBy, LockedUntil)</c> 租约 token + <c>RetryCount</c> 快照守卫（基类
/// FencedTarget 终态写，见 <see cref="OutboxDbContext"/>）兜底——败者租约被覆盖后其终态写
/// 影响 0 行。声明而非改写，最低支持版本维持 MySQL 8.0+。
/// </para>
/// </remarks>
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize); // v20 C-1：override 不调 base 使基类守卫死码化——四处补齐
        // 优化（二十四轮 OP-5）：可组合 FromSql——分页由 EF 生成（删手工 LIMIT）
        // 优化（二十五轮 API 扫描 EF-4）：AsNoTracking——只读契约（接口 doc 保证不进
        // Mark*+SaveChanges）；违反契约的突变将静默丢失
        return await OutboxMessages
            .FromSqlRaw(BuildPendingSql(), maxRetryCount)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
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
    /// ⚠️ 已知限制（八轮评审 P3，声明不修——PalORM 同款见 PalOrmOutboxStore.cs:98-104，v17 勘正行号锚）：回读按 (LockedBy, LockedUntil)
    /// 匹配——同一 owner 在同一 tick（until 完全相等，如 FakeTimeProvider 冻结时间）发起两次租约时，
    /// 第二次回读会混入第一次已锁定的批次。生产触发条件近乎为零（DATETIME(6) 微秒精度 +
    /// 单 owner 串行租约）；PG/SqlServer 走 RETURNING/OUTPUT 单语句天然免疫。
    /// </para>
    /// <para>
    /// 回读物化改用 AsNoTracking（v26 P3，对齐 SqliteOutbox CAS 路径）：<b>v16 勘正</b>——
    /// 三十四轮 ITM-210 token 化后基类 MarkProcessed/MarkDead 已是 FencedTarget +
    /// ExecuteUpdate 直写 DB（不再依赖 ChangeTracker），调用方（OutboxBatchProcessor）终态写
    /// 全部直写，跟踪物化无消费方。AcceptAllChanges 不再需要：旧路径
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize); // v22 C-3：v20 C-1 GetPending 补齐后 Lease 路径漏网
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

        // 步骤 2：按精确租约标识 (owner, until) 回读——AsNoTracking 物化（v26 P3：Mark* 已是
        // FencedTarget + ExecuteUpdate 直写，无跟踪消费方，见 remarks）；
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
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
#pragma warning restore EF1002
    }
}
