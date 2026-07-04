namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 投影检查点实体
// ─────────────────────────────────────────────────────────────

public enum ProjectionCheckpointStatus
{
    Processing = 0,
    Completed = 1,
    Failed = 2
}

public sealed class ProjectionCheckpoint
{
    public ProjectionCheckpoint(
        string projectionName,
        string sourceName,
        string position,
        ProjectionCheckpointStatus status,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(position);

        ProjectionName = projectionName;
        SourceName = sourceName;
        Position = position;
        Status = status;
        UpdatedAt = updatedAt;
    }

    public static ProjectionCheckpoint Rehydrate(
        string projectionName,
        string sourceName,
        string position,
        ProjectionCheckpointStatus status,
        DateTimeOffset updatedAt,
        DateTimeOffset leaseUntil,
        long revision,
        string? error)
    {
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), "Revision 不能小于 0。");

        return new ProjectionCheckpoint(projectionName, sourceName, position, status, updatedAt)
        {
            LeaseUntil = leaseUntil,
            Revision = revision,
            Error = error
        };
    }

    public string ProjectionName { get; }

    public string SourceName { get; }

    public string Position { get; }

    public ProjectionCheckpointStatus Status { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>租约过期时间戳。<c>&lt;= UpdatedAt</c> 表示租约已过期（失活）。</summary>
    public DateTimeOffset LeaseUntil { get; private set; }

    /// <summary>乐观并发令牌 — 单调递增，替代基于时间的并发令牌。</summary>
    /// <remarks>
    /// <c>long</c> 避免了 <c>DateTimeOffset</c> 作为并发令牌时的精度歧义：<br/>
    /// 同一毫秒内的两次并发写入必然因 <c>Revision</c> 值的差异而产生冲突，<br/>
    /// 从而防止静默覆写。
    /// </remarks>
    public long Revision { get; private set; }

    public string? Error { get; private set; }

    public void MarkProcessing(DateTimeOffset startedAt, TimeSpan processingTimeout)
    {
        Status = ProjectionCheckpointStatus.Processing;
        UpdatedAt = startedAt;
        LeaseUntil = startedAt + processingTimeout;
        Error = null;
        Revision++;
    }

    public void MarkCompleted(DateTimeOffset completedAt)
    {
        Status = ProjectionCheckpointStatus.Completed;
        UpdatedAt = completedAt;
        Error = null;
        Revision++;
    }

    public void MarkFailed(string error, DateTimeOffset failedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        Status = ProjectionCheckpointStatus.Failed;
        UpdatedAt = failedAt;
        Error = error;
        Revision++;
    }
}
