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

/// <summary>一次“开始或读取”调用的结果类别。</summary>
public enum IdempotencyStartOutcome
{
    /// <summary>本次获得执行权（新建或回收过期/失败记录）。</summary>
    Started,

    /// <summary>已有活跃或已完成记录，<see cref="IdempotencyStartResult.Record"/> 非空。</summary>
    Existing,

    /// <summary>并发竞争导致本次未获得执行权，记录需再读（可为空）。</summary>
    Contended,
}

/// <summary>“开始或读取”结果：处理器据此避免 Get 与 TryStart 的重复读取。</summary>
public readonly record struct IdempotencyStartResult(
    IdempotencyStartOutcome Outcome,
    IdempotencyRecord? Record);

/// <summary>
/// 可选能力：一次调用同时完成“读现有记录”与“尝试开始执行”。<br/>
/// 实现方应在同一存储调用链内返回 Existing 记录（活跃/已完成）或 Started 记录，
/// 避免处理器先 Get 再 TryStart 的重复读。未实现该接口的 store 自动走旧两段路径。
/// </summary>
public interface IIdempotencyStartOrGetStore : IIdempotencyStore
{
    ValueTask<IdempotencyStartResult> StartOrGetAsync(
        string operationName,
        string key,
        DateTimeOffset now,
        IdempotencyPolicy policy,
        CancellationToken ct = default);
}
