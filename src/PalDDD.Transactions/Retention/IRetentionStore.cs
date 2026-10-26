// ─────────────────────────────────────────────────────────────
// 🧹 IRetentionStore — 历史表分批删除能力
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// 历史表保留清理的存储能力（2026-10-20 性能轮）。实现必须单语句批量删除，
/// 返回实际删除行数；零行表示没有更多可删数据。
/// <para>
/// <c>batchSize</c> 是单语句删除的行数上限，必须 ≥ 1（R11 F18：SQLite 的
/// <c>LIMIT -1</c> 表示无上限，负值会一次删光积压；0 在 MySQL/PG 与「无更多数据」
/// 不可区分）。实现须在入口拒绝非法值。
/// </para>
/// </summary>
public interface IRetentionStore
{
    /// <summary>删除已处理且超过保留期的 Outbox 行。</summary>
    ValueTask<int> DeleteExpiredOutboxAsync(
        DateTimeOffset cutoff,
        int batchSize,
        bool includeDead,
        CancellationToken ct = default);

    /// <summary>删除已处理且超过保留期的 Inbox 行。</summary>
    ValueTask<int> DeleteExpiredInboxAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct = default);

    /// <summary>删除已过期（<c>expires_at &lt; now</c>）的幂等记录。</summary>
    ValueTask<int> DeleteExpiredIdempotencyAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken ct = default);
}
