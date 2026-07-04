// ─────────────────────────────────────────────────────────────
// 🧪 InMemoryProjectionCheckpointStore — 内存 Checkpoint 存储
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 内存检查点存储
// ─────────────────────────────────────────────────────────────

public sealed class InMemoryProjectionCheckpointStore : IProjectionCheckpointStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Key, ProjectionCheckpoint> _checkpoints = [];

    public ValueTask<ProjectionCheckpoint?> GetAsync(
        string projectionName,
        string sourceName,
        string position,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateKeyParts(projectionName, sourceName, position);

        lock (_lock)
        {
            _checkpoints.TryGetValue(new Key(projectionName, sourceName, position), out var checkpoint);
            return ValueTask.FromResult(checkpoint);
        }
    }

    public ValueTask<ProjectionCheckpoint?> TryStartAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        TimeSpan processingTimeout,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateKeyParts(projectionName, sourceName, position);

        lock (_lock)
        {
            var key = new Key(projectionName, sourceName, position);
            if (_checkpoints.TryGetValue(key, out var existing))
            {
                // 已完成的位置不再重复处理。
                if (existing.Status == ProjectionCheckpointStatus.Completed)
                    return ValueTask.FromResult<ProjectionCheckpoint?>(null);

                // 正在处理中 — 租约尚未到期。
                if (existing.Status == ProjectionCheckpointStatus.Processing && existing.LeaseUntil > startedAt)
                    return ValueTask.FromResult<ProjectionCheckpoint?>(null);

                // 僵尸（处理中 + 租约已过期）或失败 — 重新使用。
                existing.MarkProcessing(startedAt, processingTimeout);
                return ValueTask.FromResult<ProjectionCheckpoint?>(existing);
            }

            var checkpoint = new ProjectionCheckpoint(
                projectionName,
                sourceName,
                position,
                ProjectionCheckpointStatus.Processing,
                startedAt);
            checkpoint.MarkProcessing(startedAt, processingTimeout); // 设置 LeaseUntil + Revision
            _checkpoints.Add(key, checkpoint);
            return ValueTask.FromResult<ProjectionCheckpoint?>(checkpoint);
        }
    }

    public ValueTask MarkCompletedAsync(
        ProjectionCheckpoint checkpoint,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            checkpoint.MarkCompleted(completedAt);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(
        ProjectionCheckpoint checkpoint,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            checkpoint.MarkFailed(failureReason, failedAt);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(
        string projectionName,
        string sourceName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        lock (_lock)
        {
            List<Key> keysToRemove = [];
            foreach (var key in _checkpoints.Keys)
            {
                if (key.ProjectionName == projectionName && key.SourceName == sourceName)
                    keysToRemove.Add(key);
            }

            foreach (var key in keysToRemove)
                _checkpoints.Remove(key);
        }

        return ValueTask.CompletedTask;
    }

    private static void ValidateKeyParts(string projectionName, string sourceName, string position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(position);
    }

    private readonly record struct Key(string ProjectionName, string SourceName, string Position);
}
