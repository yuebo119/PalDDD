using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>SQLite outbox store — single-writer, no lock hints needed (WAL mode).</summary>
/// <remarks>
/// <b>租约原子性（评审 P1-3 修复 → 2026-09-19 批量化，decision-2026-09-17）</b>：
/// SQLite 不支持 <c>FOR UPDATE SKIP LOCKED</c>，历史实现先后经历两代——
/// ①"SELECT 跟踪 → 内存改 → SaveChanges"三步分离（两实例可重复租约，正确性缺陷）；
/// ②逐条 CAS 条件更新（<c>ExecuteUpdateAsync</c> 等值守卫，N 次往返的 N+1 形态）。
/// 现为<b>单语句批量租约</b>：<c>UPDATE … WHERE Id IN (子查询内重估资格谓词) ORDER BY
/// CreatedAt LIMIT batch</c>，SQLite WAL 单写者串行化 UPDATE，资格谓词对已租行失效即丢弃，
/// 与 Dapper SQLite / PalORM SQLite / MySQL EF 的单语句形态同契约（ADR-020 三栈姊妹）。
/// 时间参数直传原生 DateTimeOffset——与 EF SaveChanges 写入格式自洽（2026-09-19 spike
/// 实证：谓词不恒真、等值守卫回读命中）；<c>"O"</c> 文本参数路径已实证排除（provider 写
/// 空格分隔格式，"O" 参数使谓词恒真——错租，正确性级）。
/// <para>
/// ⚠️ <b>批次语义（2026-09-19 随批量化变更）</b>：整批为单条 UPDATE，语句级原子即
/// all-or-nothing——原逐条形态"批中第 k 条失败时已租的 0..k-1 条等租约过期重试（延迟
/// 非丢失）"的窗口不复存在（decision §2.5-5 裁决：原两难自动消解）。回读按
/// <c>(LockedBy, LockedUntil)</c> 等值守卫取回（ITM-109 姊妹先例），不重跑资格子查询。
/// </para>
/// <para>
/// ⚠️ <b>租约/查询谓词的 UTC 前提</b>：时间列 TEXT 文本序比较在偏移一致时等于时间序——
/// 本栈 lease 链路时间源恒 <c>GetUtcNow()</c>（+00:00）自洽；<c>NextAttemptAt</c>/
/// <c>LockedUntil</c> 的时间谓词与 <c>GetPendingMessagesAsync</c> 下推 SQL 的
/// <c>ORDER BY CreatedAt</c>（2026-09-19 增读的列——CreatedAt 由消息构造方写入，
/// 默认 GetUtcNow，init 属性可显式传非零偏移）共享该前提；外部写入非零偏移会破坏
/// 文本序（详见 OutboxSqliteConcurrencyTests 的 LockedUntilUtc 前提锁定测试）。
/// </para>
/// </remarks>
public abstract class SqliteOutboxDbContext(DbContextOptions options) : OutboxDbContext(options)
{
    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
    {
        // 守卫：随 2026-09-19 谓词下推从 QueryEligibleAsync 移入（override 完全替换基类
        // virtual，基类守卫对本派生类不可达——ITM-659 形态）；字面量 0 对应
        // OutboxStatus.Pending（ITM-659：非正 maxRetryCount 使谓词恒假，静默空返回无诊断）
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetryCount);

        var now = GetUtcNow();

        // 谓词下推（decision-2026-09-19，取代原 QueryEligibleAsync 的"整页物化+内存时间
        // 过滤+翻页填满"形态）：资格谓词与 Lease 单语句的子查询逐字同族，ITM-261（EF LINQ
        // 不能翻译 DateTimeOffset 有序比较/排序）不适用于手写 SQL；时间参数直传原生
        // DateTimeOffset（与写入格式自洽——ExecuteSqlAsync 路径 spike 已证，FromSqlRaw
        // 非组合式物化由 decision-2026-09-19 前置二 spike 验证：SQL 内谓词+ORDER BY+LIMIT
        // 直接 ToListAsync 可行、实体完整映射）。
        // 排序键 CreatedAt：对齐 Lease 与三栈 GetPending 先例（Dapper/PalORM/EF-MySQL 均
        // SQL 内 ORDER BY created_at）；原 Id 序（ULID 创建序）与本序在单进程下一致
        //（表征测试 GetPending_OrdersByIdAscending 双态绿）。非组合式调用——FromSqlRaw 后
        // 不再接 Where/OrderBy（组合会触发 ITM-261 翻译），仅 AsNoTracking + ToListAsync。
        // 优化（二十五轮 API 扫描 EF-5）：AsNoTracking——只读契约（接口 doc 保证不进
        // Mark*+SaveChanges）；违反契约的突变将静默丢失。
        // 伸缩性边界（反方发现④，决策文档在案）：OR 谓词下 (Status,NextAttemptAt,CreatedAt)
        // 索引第三列不保序——SQL 内可能单次全扫+临时 B-tree 排序；本形态消除的是 N 页往返
        // 与逐页重复物化（O(表·页数)→单语句），非"与表大小无关"。
        return await OutboxMessages
            .FromSqlRaw("""
                SELECT * FROM OutboxMessages
                WHERE Status = 0 AND RetryCount < {0}
                  AND (NextAttemptAt IS NULL OR NextAttemptAt <= {1})
                  AND (LockedUntil IS NULL OR LockedUntil <= {1})
                ORDER BY CreatedAt
                LIMIT {2}
                """, maxRetryCount, now, batchSize)
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
        // ITM-216 修复（三十二轮）：owner 空白守卫——对照 PG（ITM-081）/SqlServer/MySql 同款，
        // 缺守卫时空/空白 owner 写入 LockedBy 列破坏跨方言契约一致
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        // 三十八轮 P3 修复：补齐三姊妹已有守卫（ITM-081/167/216 对齐系列漏网项）——
        // leaseDuration 非正时租约即刻过期/永不过期语义错乱；TotalSeconds 超过 int.MaxValue
        // 时 until = now.Add(leaseDuration) 的秒数语义溢出。Options 层已校验正数，
        // 此处是 Store 直调路径的防御性 fail-fast（与 MySqlOutboxDbContext 同款）。
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for the lease LockedUntil value.");
        // 守卫随 2026-09-19 批量化从 QueryEligibleAsync 移入（Lease 不再走该共享入口，
        // ITM-659 守卫族对本方法的覆盖不得因此丢失）；字面量 0 对应 OutboxStatus.Pending
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetryCount);

        var now = GetUtcNow();
        var until = now.Add(leaseDuration);

        // 单语句批量租约（decision-2026-09-17 裁决 shape 2，2026-09-19 实施）：
        // 资格谓词在子查询内重估（与 GetPendingMessagesAsync 下推 SQL 的谓词同一族），
        // SQLite WAL 单写者串行化 UPDATE——并发租约使谓词对已租行失效，语义与
        // Dapper SQLite / PalORM SQLite 的单语句形态同契约。三点前提均经 2026-09-19
        // spike 验证：① ExecuteSqlInterpolated 的原生 DateTimeOffset 参数与 EF
        // SaveChanges 写入格式自洽（空格分隔 TEXT，谓词不恒真）；② 等值守卫回读
        // 命中；③ EF LINQ 不能翻译 DateTimeOffset 有序比较（ITM-261）不适用于手写
        // SQL——文本序比较在 raw SQL 内合法。
        // ORDER BY CreatedAt：对齐 Dapper/PalORM/MySQL-EF 三栈先例（SqlTemplates OutboxLeaseUpdateSqlite
        // 同款）；ITM-261 只限 LINQ 的 ORDER BY 翻译。ULID Id 序与创建序等价（QueryEligibleAsync 注释），
        // 故与 GetPending 路径（Id 序分页）语义一致。
        // ExecuteSqlAsync（EF 11 统一形态，FormattableString 重载自动参数化——
        // ExecuteSqlInterpolatedAsync 已标 Obsolete）
        await Database.ExecuteSqlAsync($"""
            UPDATE OutboxMessages
            SET LockedBy = {owner}, LockedUntil = {until}
            WHERE Id IN (
                SELECT Id FROM OutboxMessages
                WHERE Status = 0 AND RetryCount < {maxRetryCount}
                  AND (NextAttemptAt IS NULL OR NextAttemptAt <= {now})
                  AND (LockedUntil IS NULL OR LockedUntil <= {now})
                ORDER BY CreatedAt
                LIMIT {batchSize})
            """, ct).ConfigureAwait(false);

        // 等值守卫回读（Dapper/PalORM 同款两步形态，ITM-109 姊妹先例）：按租约标识取回，
        // 不重跑资格子查询——同 tick 并发时另一 worker 的行 owner 不同不会混入；
        // 同 owner 同 until 的两次租约会互相可见（姊妹栈已接受语义）。
        // 等值比较 LINQ 可翻译（ITM-261：仅 DateTimeOffset 有序比较不可翻译）；
        // ORDER BY Id 因 ITM-261 对 DateTimeOffset 排序翻译的限制走 ULID 创建序。
        var leased = await OutboxMessages.AsNoTracking()
            .Where(m => m.LockedBy == owner && m.LockedUntil == until)
            .OrderBy(m => m.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return leased;
    }
}
