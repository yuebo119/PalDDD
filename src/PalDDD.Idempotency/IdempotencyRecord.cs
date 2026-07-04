namespace PalDDD.Idempotency;

// ─────────────────────────────────────────────────────────────
// 幂等执行记录
// ─────────────────────────────────────────────────────────────

public enum IdempotencyRecordStatus
{
    Processing = 0,
    Completed = 1,
    Failed = 2
}

public sealed class IdempotencyRecord
{
    public IdempotencyRecord(
        string operationName,
        string key,
        IdempotencyRecordStatus status,
        DateTimeOffset lockedUntil,
        DateTimeOffset expiresAt,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        OperationName = operationName;
        Key = key;
        Status = status;
        LockedUntil = lockedUntil;
        ExpiresAt = expiresAt;
        UpdatedAt = updatedAt;
    }

    public string OperationName { get; }

    public string Key { get; }

    public IdempotencyRecordStatus Status { get; private set; }

    public DateTimeOffset LockedUntil { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public ReadOnlyMemory<byte>? ResponsePayload { get; private set; }

    public string? Error { get; private set; }

    public void MarkProcessing(DateTimeOffset lockedUntil, DateTimeOffset expiresAt, DateTimeOffset updatedAt)
    {
        Status = IdempotencyRecordStatus.Processing;
        LockedUntil = lockedUntil;
        ExpiresAt = expiresAt;
        UpdatedAt = updatedAt;
        Error = null;
        ResponsePayload = null;
    }

    public void MarkCompleted(ReadOnlyMemory<byte> responsePayload, DateTimeOffset completedAt)
    {
        Status = IdempotencyRecordStatus.Completed;
        UpdatedAt = completedAt;
        ResponsePayload = responsePayload.ToArray();
        Error = null;
    }

    public void MarkFailed(string error, DateTimeOffset failedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        Status = IdempotencyRecordStatus.Failed;
        UpdatedAt = failedAt;
        Error = error;
    }
}
