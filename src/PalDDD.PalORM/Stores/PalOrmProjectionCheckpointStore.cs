using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using PalORM;
using PalDDD.Projections;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// Projection Checkpoint Store 的 PalORM 实现 —— 双泛型核心基类（全程手写 SQL）。
/// <para>
/// <b>复合主键限制</b>：表 <c>projection_checkpoints</c> 是三列复合主键 —— PALORM019 拒绝实体注册。
/// <see cref="GetAsync"/> 用 <see cref="DbDataReader"/> 手动映射（QueryFirstAsync 对未注册类型返回空对象）。
/// </para>
/// </summary>
public class PalOrmProjectionCheckpointStore<TProvider> : IProjectionCheckpointStore
    where TProvider : IDbProvider
{
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需直接访问 Session。")]
    protected readonly DataSession<TProvider> Session;

    /// <summary>构造 Projection Checkpoint Store。</summary>
    public PalOrmProjectionCheckpointStore(DataSession<TProvider> session) => Session = session;

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint?> GetAsync(
        string projectionName, string sourceName, string position, CancellationToken ct = default)
    {
        // CheckpointRow 未注册为实体（复合主键 PALORM019 拒绝）—— 用 GetRawConnection + 手动 reader
        await using var cmd = Session.GetRawConnection().CreateCommand();
        cmd.CommandText = "SELECT projection_name, source_name, position, status, updated_at, lease_until, revision, error FROM projection_checkpoints WHERE projection_name = @p0 AND source_name = @p1 AND position = @p2";
        AddParam(cmd, "@p0", projectionName);
        AddParam(cmd, "@p1", sourceName);
        AddParam(cmd, "@p2", position);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return ProjectionCheckpoint.Rehydrate(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            (ProjectionCheckpointStatus)reader.GetInt32(3),
            reader.GetDateTime(4),
            reader.GetDateTime(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint?> TryStartAsync(
        string projectionName, string sourceName, string position,
        DateTimeOffset startedAt, TimeSpan processingTimeout, CancellationToken ct = default)
    {
        var leaseUntil = startedAt + processingTimeout;
        var statusProcessing = (int)ProjectionCheckpointStatus.Processing;

        // 方言分叉：PG/SQLite 用 ON CONFLICT DO NOTHING；MySQL 用 INSERT IGNORE
        var affected = TProvider.SupportsReturningClause
            ? await Session.ExecuteAsync($"INSERT INTO projection_checkpoints (projection_name, source_name, position, status, updated_at, lease_until, revision, error) VALUES ({projectionName}, {sourceName}, {position}, {statusProcessing}, {startedAt}, {leaseUntil}, 1, NULL) ON CONFLICT DO NOTHING", ct)
            : await Session.ExecuteAsync($"INSERT IGNORE INTO projection_checkpoints (projection_name, source_name, position, status, updated_at, lease_until, revision, error) VALUES ({projectionName}, {sourceName}, {position}, {statusProcessing}, {startedAt}, {leaseUntil}, 1, NULL)", ct);

        if (affected > 0)
        {
            return ProjectionCheckpoint.Rehydrate(
                projectionName, sourceName, position,
                ProjectionCheckpointStatus.Processing, startedAt,
                leaseUntil, 1, null);
        }

        var existing = await GetAsync(projectionName, sourceName, position, ct);
        if (existing is null) return null;
        if (existing.Status == ProjectionCheckpointStatus.Completed) return null;
        if (existing.Status == ProjectionCheckpointStatus.Processing && existing.LeaseUntil > startedAt)
            return null;

        var expectedRevision = existing.Revision;
        var statusCompleted = (int)ProjectionCheckpointStatus.Completed;
        affected = await Session.ExecuteAsync(
            $"UPDATE projection_checkpoints SET status = {statusProcessing}, updated_at = {startedAt}, lease_until = {leaseUntil}, revision = revision + 1, error = NULL WHERE projection_name = {projectionName} AND source_name = {sourceName} AND position = {position} AND revision = {expectedRevision} AND status <> {statusCompleted}",
            ct);
        if (affected == 0) return null;

        existing.MarkProcessing(startedAt, processingTimeout);
        return existing;
    }

    /// <inheritdoc />
    public async ValueTask MarkCompletedAsync(ProjectionCheckpoint checkpoint, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        var expectedRevision = checkpoint.Revision;
        var statusCompleted = (int)ProjectionCheckpointStatus.Completed;
        var affected = await Session.ExecuteAsync(
            $"UPDATE projection_checkpoints SET status = {statusCompleted}, updated_at = {completedAt}, revision = revision + 1, error = NULL WHERE projection_name = {checkpoint.ProjectionName} AND source_name = {checkpoint.SourceName} AND position = {checkpoint.Position} AND revision = {expectedRevision}",
            ct);
        // 修复覆盖残留：对齐 Dapper 版同方法（rows>0 才变更本地对象）——
        // 乐观锁冲突时 DB 未变，不假装落库成功
        if (affected > 0)
            checkpoint.MarkCompleted(completedAt);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(ProjectionCheckpoint checkpoint, string failureReason, DateTimeOffset failedAt, CancellationToken ct = default)
    {
        var expectedRevision = checkpoint.Revision;
        var statusFailed = (int)ProjectionCheckpointStatus.Failed;
        var statusCompleted = (int)ProjectionCheckpointStatus.Completed;
        var affected = await Session.ExecuteAsync(
            $"UPDATE projection_checkpoints SET status = {statusFailed}, updated_at = {failedAt}, revision = revision + 1, error = {failureReason} WHERE projection_name = {checkpoint.ProjectionName} AND source_name = {checkpoint.SourceName} AND position = {checkpoint.Position} AND revision = {expectedRevision} AND status <> {statusCompleted}",
            ct);
        if (affected > 0)
            checkpoint.MarkFailed(failureReason, failedAt);
    }

    /// <inheritdoc />
    public async ValueTask ResetAsync(string projectionName, string sourceName, CancellationToken ct = default)
    {
        await Session.ExecuteAsync(
            $"DELETE FROM projection_checkpoints WHERE projection_name = {projectionName} AND source_name = {sourceName}",
            ct);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
