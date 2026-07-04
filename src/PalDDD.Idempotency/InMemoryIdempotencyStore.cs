// ─────────────────────────────────────────────────────────────
// 🧪 InMemoryIdempotencyStore — 内存幂等存储（测试/原型）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Idempotency;

// ─────────────────────────────────────────────────────────────
// 内存幂等存储
// ─────────────────────────────────────────────────────────────

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Key, IdempotencyRecord> _records = [];

    public ValueTask<IdempotencyRecord?> GetAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateKeyParts(operationName, key);

        lock (_lock)
        {
            var recordKey = new Key(operationName, key);
            if (!_records.TryGetValue(recordKey, out var record))
                return ValueTask.FromResult<IdempotencyRecord?>(null);

            if (record.ExpiresAt <= now)
            {
                _records.Remove(recordKey);
                return ValueTask.FromResult<IdempotencyRecord?>(null);
            }

            return ValueTask.FromResult<IdempotencyRecord?>(record);
        }
    }

    public ValueTask<IdempotencyRecord?> TryStartAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        IdempotencyPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ct.ThrowIfCancellationRequested();
        ValidateKeyParts(operationName, key);

        lock (_lock)
        {
            var recordKey = new Key(operationName, key);
            if (_records.TryGetValue(recordKey, out var existing))
            {
                if (existing.ExpiresAt <= now)
                {
                    _records.Remove(recordKey);
                }
                else if (existing.Status == IdempotencyRecordStatus.Failed
                    || (existing.Status == IdempotencyRecordStatus.Processing && existing.LockedUntil <= now))
                {
                    existing.MarkProcessing(now.Add(policy.ProcessingTimeout), now.Add(policy.Retention), now);
                    return ValueTask.FromResult<IdempotencyRecord?>(existing);
                }
                else
                {
                    return ValueTask.FromResult<IdempotencyRecord?>(null);
                }
            }

            var record = new IdempotencyRecord(
                operationName,
                key,
                IdempotencyRecordStatus.Processing,
                now.Add(policy.ProcessingTimeout),
                now.Add(policy.Retention),
                now);
            _records.Add(recordKey, record);
            return ValueTask.FromResult<IdempotencyRecord?>(record);
        }
    }

    public ValueTask MarkCompletedAsync(
        IdempotencyRecord record,
        ReadOnlyMemory<byte> responsePayload,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            record.MarkCompleted(responsePayload, completedAt);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(
        IdempotencyRecord record,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            record.MarkFailed(failureReason, failedAt);
        }

        return ValueTask.CompletedTask;
    }

    private static void ValidateKeyParts(string operationName, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }

    private readonly record struct Key(string OperationName, string IdempotencyKey);
}
