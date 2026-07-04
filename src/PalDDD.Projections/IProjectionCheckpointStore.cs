namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 投影检查点存储抽象
// ─────────────────────────────────────────────────────────────

public interface IProjectionCheckpointStore
{
    ValueTask<ProjectionCheckpoint?> GetAsync(
        string projectionName,
        string sourceName,
        string position,
        CancellationToken ct = default);

    ValueTask<ProjectionCheckpoint?> TryStartAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        TimeSpan processingTimeout,
        CancellationToken ct = default);

    ValueTask MarkCompletedAsync(
        ProjectionCheckpoint checkpoint,
        DateTimeOffset completedAt,
        CancellationToken ct = default);

    ValueTask MarkFailedAsync(
        ProjectionCheckpoint checkpoint,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default);

    ValueTask ResetAsync(
        string projectionName,
        string sourceName,
        CancellationToken ct = default);
}
