using Microsoft.EntityFrameworkCore;

namespace PalDDD.Transactions;

/// <summary>SQLite outbox store — single-writer, no lock hints needed (WAL mode).</summary>
/// <remarks>
/// <b>租约原子性（评审 P1-3 修复）</b>：SQLite 不支持 <c>FOR UPDATE SKIP LOCKED</c>，
/// 旧实现"SELECT 跟踪 → 内存改 → SaveChanges"三步分离，两实例可同时读到同一批
/// Pending 消息并各自写入租约（重复发布）。现改为逐条 CAS 条件更新：
/// <c>ExecuteUpdateAsync</c> 以 <c>Id + Status==Pending + LockedUntil==原值</c> 为守卫
/// （等值比较在 EF SQLite 可翻译——ITM-261 实证仅有序比较不可翻译），
/// SQLite WAL 单写者串行化 UPDATE——后写实例守卫不匹配、影响 0 行、该消息被丢弃，
/// 与 MySQL/Dapper 栈的 (locked_by, locked_until) 双守卫语义对齐。
/// <para>
/// ⚠️ <b>批次语义（二轮评审验证轮披露）</b>：逐条 UPDATE 无显式包围事务——批次第 k 条
/// 抛异常（如 SQLITE_BUSY）时，已租的 0..k-1 条成为"已租未发布"，异常上抛至后台循环，
/// 这些消息需等 LeaseDuration 过期后由下一 tick 重租重试（<b>延迟，非丢失/重复</b>——
/// CAS 守卫保证不重复发布）。租约方法不包含在调用方的 SaveChanges 事务中属预期行为：
/// 租约的存活期本就应超越单次业务事务。
/// </para>
/// </remarks>
public abstract class SqliteOutboxDbContext(DbContextOptions options) : OutboxDbContext(options)
{
    /// <summary>按资格条件分页查询到期消息（GetPending/Lease 共用）。
    /// <para>
    /// 三十九轮 ITM-261 修复：EF Core 11 preview7 的 SQLite provider 不能翻译 DateTimeOffset 的
    /// <b>有序</b>比较（<c>&lt;=</c>）——等值比较可翻译（MarkProcessed/FencedTarget 的租约守卫不受影响），
    /// 因此 <c>NextAttemptAt &lt;= now</c> / <c>LockedUntil &lt;= now</c> 必须物化后内存过滤。
    /// 分页循环保证"时间过滤先于 Take"：首页全为未来重试/未到期租约时继续翻页直至填满
    /// batchSize 或耗尽（此前被测试本地重写遮蔽，重写版"SQL Take 后内存过滤"会少取批次）。
    /// </para>
    /// </summary>
    private async Task<List<OutboxMessage>> QueryEligibleAsync(
        int batchSize, int maxRetryCount, bool asNoTracking, CancellationToken ct)
    {
        var now = GetUtcNow();
        var result = new List<OutboxMessage>(batchSize);
        var skip = 0;
        while (result.Count < batchSize)
        {
            var query = asNoTracking ? OutboxMessages.AsNoTracking() : OutboxMessages;
            var page = await query
                .Where(m => m.Status == OutboxStatus.Pending && m.RetryCount < maxRetryCount)
                // ITM-261 续：EF SQLite 对 DateTimeOffset 连 ORDER BY 也不支持（"does not support
                // expressions of type 'DateTimeOffset' in ORDER BY clauses"）——改按 Id 排序：
                // ULID 的 Crockford Base32 字典序即创建时间序（规范级 sortable guarantee），
                // 与 CreatedAt 排序语义等价（均为创建序，分页确定性不受影响）。
                .OrderBy(m => m.Id)
                .Skip(skip).Take(batchSize)
                .ToListAsync(ct).ConfigureAwait(false);
            if (page.Count == 0) break;
            result.AddRange(page.Where(m =>
                (m.NextAttemptAt is null || m.NextAttemptAt <= now)
                && (m.LockedUntil is null || m.LockedUntil <= now)));
            skip += batchSize;
        }
        if (result.Count > batchSize) result.RemoveRange(batchSize, result.Count - batchSize);
        return result;
    }

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        int maxRetryCount,
        CancellationToken ct)
        // 优化（二十五轮 API 扫描 EF-5）：AsNoTracking——只读契约（接口 doc 保证不进
        // Mark*+SaveChanges）；违反契约的突变将静默丢失
        => await QueryEligibleAsync(batchSize, maxRetryCount, asNoTracking: true, ct).ConfigureAwait(false);

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
        var until = GetUtcNow().Add(leaseDuration);
        // 评审 P1-3 修复：租约不再走"SELECT 跟踪 → 内存改 → SaveChanges"（三步分离，
        // 两实例可同时租约同一批——见类头 remarks）。改为无跟踪读 + 逐条 CAS：
        // ExecuteUpdateAsync 的 WHERE 守卫（Id 等值 + Status==Pending + LockedUntil==读到的原值）
        // 全部为等值比较，EF SQLite 可翻译（ITM-261：仅 DateTimeOffset 有序比较不可翻译）。
        // LockedUntil 等值即版本守卫——另一实例先一步写入租约后本条影响 0 行，消息丢弃。
        var candidates = await QueryEligibleAsync(batchSize, maxRetryCount, asNoTracking: true, ct).ConfigureAwait(false);

        var leased = new List<OutboxMessage>(candidates.Count);
        foreach (var msg in candidates)
        {
            var originalLockedUntil = msg.LockedUntil;
            var affected = await OutboxMessages
                .Where(m => m.Id == msg.Id
                         && m.Status == OutboxStatus.Pending
                         && m.LockedUntil == originalLockedUntil)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(m => m.LockedBy, owner)
                          .SetProperty(m => m.LockedUntil, until),
                    ct).ConfigureAwait(false);
            if (affected > 0)
            {
                msg.LockedBy = owner;
                msg.LockedUntil = until;
                leased.Add(msg);
            }
        }
        return leased;
    }
}
