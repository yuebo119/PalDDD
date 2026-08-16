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

                // 僵尸（处理中 + 租约已过期）或失败 — 抢占复用。
                // P3 修复（十七轮）：新实例隔离——原路径直接复用字典内 existing 实例并返回，
                // 被抢占的原持有者仍持同一引用，其 MarkCompleted 可通过 ReferenceEquals 守卫
                // （守卫对"同一实例"恒放行，抢占失效）。改为：Rehydrate 复制字段创建后继实例 +
                // MarkProcessing，字典替换为后继实例——旧引用自此不再是字典当前持有者，
                // 其标记被守卫静默忽略（对齐 EFCore 版 Revision 隔离语义）。
                var successor = ProjectionCheckpoint.Rehydrate(
                    existing.ProjectionName,
                    existing.SourceName,
                    existing.Position,
                    existing.Status,
                    existing.UpdatedAt,
                    existing.LeaseUntil,
                    existing.Revision,
                    existing.Error);
                successor.MarkProcessing(startedAt, processingTimeout);
                _checkpoints[key] = successor;
                return ValueTask.FromResult<ProjectionCheckpoint?>(successor);
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
            // P3 修复（八轮评审）：所有权/终态守卫——本存储共享同一实例（Revision 数值比对恒等，
            // 等价守卫为引用一致 + 状态机）：字典中非同一实例（Reset 后遗留旧引用；十七轮起另含
            // 被抢占的僵尸/失败旧持有者——TryStartAsync 抢占时已换新实例，见其注释）、或已离开
            // Processing（Completed 终态不可翻转；Failed 待 TryStartAsync 回收）时，被抢占者的
            // 标记不生效，静默返回。
            if (!IsCurrentLeaseHolder(checkpoint))
                return ValueTask.CompletedTask;

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
            // P3 修复（八轮评审）：所有权/终态守卫——同 MarkCompletedAsync（Completed 终态不可翻转为
            // Failed；十七轮起另拦截被抢占后残留的旧引用）
            if (!IsCurrentLeaseHolder(checkpoint))
                return ValueTask.CompletedTask;

            checkpoint.MarkFailed(failureReason, failedAt);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 判定传入 checkpoint 是否仍为字典当前持有的活跃租约实例
    /// （引用一致 + Processing 状态；须在 <see cref="_lock"/> 内调用）。
    /// </summary>
    private bool IsCurrentLeaseHolder(ProjectionCheckpoint checkpoint)
    {
        var key = new Key(checkpoint.ProjectionName, checkpoint.SourceName, checkpoint.Position);
        return _checkpoints.TryGetValue(key, out var current)
            && ReferenceEquals(current, checkpoint)
            && current.Status == ProjectionCheckpointStatus.Processing;
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
