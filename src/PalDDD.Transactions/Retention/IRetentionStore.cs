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

    /// <summary>删除已处理且超过保留期的 Inbox 行（只删 Processed）。</summary>
    /// <remarks>
    /// 既有成员，保持必需——既有自定义实现者不受影响（ADR-039 的向后兼容取舍）。
    /// 需要连 Failed 行一起清理时用带 <c>includeFailed</c> 的重载。
    /// </remarks>
    ValueTask<int> DeleteExpiredInboxAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct = default);

    /// <summary>
    /// 删除已处理/已失败且超过保留期的 Inbox 行（ADR-039 / 审计 A2/G4）。
    /// </summary>
    /// <param name="includeFailed">
    /// <c>true</c> 时同时删除 <c>Failed</c> 行（<c>status IN (2,3)</c>），对齐 Outbox 的
    /// <c>includeDead</c> 先例；<c>false</c> 与无参重载等价（只删 <c>Processed</c>）。
    /// </param>
    /// <remarks>
    /// <b>默认实现（DIM）刻意 fail-visible</b>：未覆写本成员的实现者在
    /// <c>includeFailed: true</c> 时抛 <see cref="NotSupportedException"/>，而不是静默按
    /// <c>false</c> 执行——后者会让调用方以为 Failed 行已清理而实际仍在（假成功）。
    /// <c>includeFailed: false</c> 转发到既有成员，语义零变化。三个内置实现
    /// （Dapper / PalORM / EF Core）均覆写本成员。
    /// </remarks>
    ValueTask<int> DeleteExpiredInboxAsync(
        DateTimeOffset cutoff,
        int batchSize,
        bool includeFailed,
        CancellationToken ct = default)
        => includeFailed
            ? ValueTask.FromException<int>(new NotSupportedException(
                $"Retention store '{GetType().FullName}' does not implement failed-Inbox cleanup; " +
                "override DeleteExpiredInboxAsync(cutoff, batchSize, includeFailed, ct) to support it."))
            : DeleteExpiredInboxAsync(cutoff, batchSize, ct);

    /// <summary>删除已过期（<c>expires_at &lt; now</c>）的幂等记录。</summary>
    ValueTask<int> DeleteExpiredIdempotencyAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken ct = default);
}
