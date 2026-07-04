// ─────────────────────────────────────────────────────────────
// 💾 IIdempotencyStore — 幂等记录持久化抽象
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Idempotency;

// ─────────────────────────────────────────────────────────────
// 幂等存储接口
// ─────────────────────────────────────────────────────────────

public interface IIdempotencyStore
{
    ValueTask<IdempotencyRecord?> GetAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        CancellationToken ct = default);

    ValueTask<IdempotencyRecord?> TryStartAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        IdempotencyPolicy policy,
        CancellationToken ct = default);

    ValueTask MarkCompletedAsync(
        IdempotencyRecord record,
        ReadOnlyMemory<byte> responsePayload,
        DateTimeOffset completedAt,
        CancellationToken ct = default);

    ValueTask MarkFailedAsync(
        IdempotencyRecord record,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default);
}
