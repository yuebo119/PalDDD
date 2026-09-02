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

    // v54 P2：持久层物化构造（internal）——PalORM 手动 reader 物化需携带 DB 真值 Revision
    //（public ctor 恒 0 仅供新建记录；CAS 基准若用 0 对已换代记录恒失配）。经
    // InternalsVisibleTo 供 PalDDD.PalORM 消费，不进公共 API 面。
    internal IdempotencyRecord(
        string operationName,
        string key,
        IdempotencyRecordStatus status,
        DateTimeOffset lockedUntil,
        DateTimeOffset expiresAt,
        DateTimeOffset updatedAt,
        long revision)
        : this(operationName, key, status, lockedUntil, expiresAt, updatedAt)
    {
        Revision = revision;
    }

    /// <summary>
    /// 回放恢复（internal，v54 P2）——从持久层重建终态字段而<b>不递增</b> <see cref="Revision"/>。
    /// 领域 <c>Mark*</c> 是状态转移语义必递增；持久层回放是"恢复原状"，CAS 基准必须保持 DB 原值
    ///（此前 GetAsync 回放借用 Mark* 使内存 Revision 偏移 +1，后续 CAS 恒失配）。
    /// </summary>
    internal void RestoreTerminalState(ReadOnlyMemory<byte>? responsePayload, string? error)
    {
        ResponsePayload = responsePayload?.ToArray();
        Error = error;
    }

    public string OperationName { get; }

    public string Key { get; }

    public IdempotencyRecordStatus Status { get; private set; }

    public DateTimeOffset LockedUntil { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>乐观并发令牌 — 单调递增，替代基于时间的并发令牌（v53 P2）。</summary>
    /// <remarks>
    /// 镜像 <c>ProjectionCheckpoint.Revision</c>：<c>DateTimeOffset</c> 时间戳令牌受 DB 列
    /// 精度截断影响（PG 微秒/datetime2 100ns），同刻双 worker 回收同一过期租约时 CAS 基准
    /// 相等可双双命中（幂等核心承诺被绕过）；<c>long</c> 每次状态转移必递增，同刻双写必然冲突。
    /// </remarks>
    public long Revision { get; private set; }

    public ReadOnlyMemory<byte>? ResponsePayload { get; private set; }

    public string? Error { get; private set; }

    public void MarkProcessing(DateTimeOffset lockedUntil, DateTimeOffset expiresAt, DateTimeOffset updatedAt)
    {
        Revision++;
        Status = IdempotencyRecordStatus.Processing;
        LockedUntil = lockedUntil;
        ExpiresAt = expiresAt;
        UpdatedAt = updatedAt;
        Error = null;
        ResponsePayload = null;
    }

    public void MarkCompleted(ReadOnlyMemory<byte> responsePayload, DateTimeOffset completedAt)
    {
        Revision++;
        Status = IdempotencyRecordStatus.Completed;
        UpdatedAt = completedAt;
        ResponsePayload = responsePayload.ToArray();
        Error = null;
    }

    public void MarkFailed(string error, DateTimeOffset failedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        Revision++;
        Status = IdempotencyRecordStatus.Failed;
        UpdatedAt = failedAt;
        Error = error;
    }
}
