using System.Diagnostics.CodeAnalysis;
using PalORM;
using PalDDD.PalORM.Models;
using PalDDD.Projections;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// Projection Checkpoint Store 的 PalORM 实现 —— 双泛型核心基类（全程手写 SQL）。
/// <para>
/// <b>复合主键限制</b>：表 <c>projection_checkpoints</c> 是三列复合主键 (projection_name, source_name, position) ——
/// PALORM019 拒绝复合主键实体注册。本 Store 不注册实体，全程 <see cref="DataSession{TProvider}"/>.<c>ExecuteAsync</c>
/// + <c>QueryFirstAsync&lt;CheckpointRow&gt;</c> 手写 SQL。
/// </para>
/// <para>
/// <b>乐观锁</b>：<c>revision</c> 列（long）—— UPDATE 时手写 <c>WHERE revision = @expected AND status &lt;&gt; Completed</c>，
/// 0 行返回视为冲突/抢占失败。
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
        // 显式 AS 别名到 PascalCase —— 与 Dapper 实现一致（不依赖 snake_case 自动映射）
        try
        {
            var row = await Session.QueryFirstAsync<CheckpointRow>(
                $"SELECT projection_name AS ProjectionName, source_name AS SourceName, position AS Position, status AS Status, updated_at AS UpdatedAt, lease_until AS LeaseUntil, revision AS Revision, error AS Error FROM projection_checkpoints WHERE projection_name = {projectionName} AND source_name = {sourceName} AND position = {position}",
                ct);
            return row.ToDomain();
        }
        catch (InvalidOperationException)
        {
            return null;  // 无行
        }
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint?> TryStartAsync(
        string projectionName, string sourceName, string position,
        DateTimeOffset startedAt, TimeSpan processingTimeout, CancellationToken ct = default)
    {
        var leaseUntil = startedAt + processingTimeout;
        var statusProcessing = (int)ProjectionCheckpointStatus.Processing;

        // 方言分叉：PG/SQLite 用 ON CONFLICT DO NOTHING；MySQL 用 INSERT IGNORE
        // 三元运算符会退化为 string 而非 FormattableString —— 必须分支独立 $"..." 字面量
        var affected = TProvider.SupportsReturningClause
            ? await Session.ExecuteAsync($"INSERT INTO projection_checkpoints (projection_name, source_name, position, status, updated_at, lease_until, revision, error) VALUES ({projectionName}, {sourceName}, {position}, {statusProcessing}, {startedAt}, {leaseUntil}, 1, NULL) ON CONFLICT DO NOTHING", ct)
            : await Session.ExecuteAsync($"INSERT IGNORE INTO projection_checkpoints (projection_name, source_name, position, status, updated_at, lease_until, revision, error) VALUES ({projectionName}, {sourceName}, {position}, {statusProcessing}, {startedAt}, {leaseUntil}, 1, NULL)", ct);

        if (affected > 0)
        {
            // 新插入成功 —— 直接返回 Processing 检查点
            // 用 Rehydrate 工厂（领域类型只读属性；不能 object initializer）
            return ProjectionCheckpoint.Rehydrate(
                projectionName, sourceName, position,
                ProjectionCheckpointStatus.Processing, startedAt,
                leaseUntil, 1, null);
        }

        // 冲突 —— 回查现有决定返回语义
        var existing = await GetAsync(projectionName, sourceName, position, ct);
        if (existing is null) return null;

        // 已完成 → 跳过
        if (existing.Status == ProjectionCheckpointStatus.Completed) return null;

        // 仍在 Processing 且租约未过期 → 跳过
        if (existing.Status == ProjectionCheckpointStatus.Processing && existing.LeaseUntil > startedAt)
            return null;

        // 抢占（过期租约或 Failed）—— 条件 UPDATE：revision 乐观锁 + status<>Completed 守卫
        var expectedRevision = existing.Revision;
        var statusCompleted = (int)ProjectionCheckpointStatus.Completed;
        affected = await Session.ExecuteAsync(
            $"UPDATE projection_checkpoints SET status = {statusProcessing}, updated_at = {startedAt}, lease_until = {leaseUntil}, revision = revision + 1, error = NULL WHERE projection_name = {projectionName} AND source_name = {sourceName} AND position = {position} AND revision = {expectedRevision} AND status <> {statusCompleted}",
            ct);
        if (affected == 0) return null;

        existing.MarkProcessing(startedAt, processingTimeout);  // 领域方法：内部 Revision++ + LeaseUntil 赋值
        return existing;
    }

    /// <inheritdoc />
    public async ValueTask MarkCompletedAsync(ProjectionCheckpoint checkpoint, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        var expectedRevision = checkpoint.Revision;
        var statusCompleted = (int)ProjectionCheckpointStatus.Completed;
        await Session.ExecuteAsync(
            $"UPDATE projection_checkpoints SET status = {statusCompleted}, updated_at = {completedAt}, revision = revision + 1, error = NULL WHERE projection_name = {checkpoint.ProjectionName} AND source_name = {checkpoint.SourceName} AND position = {checkpoint.Position} AND revision = {expectedRevision}",
            ct);
        checkpoint.MarkCompleted(completedAt);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(ProjectionCheckpoint checkpoint, string failureReason, DateTimeOffset failedAt, CancellationToken ct = default)
    {
        var expectedRevision = checkpoint.Revision;
        var statusFailed = (int)ProjectionCheckpointStatus.Failed;
        var statusCompleted = (int)ProjectionCheckpointStatus.Completed;
        // status<>Completed 守卫 —— 不覆盖已完成的检查点
        await Session.ExecuteAsync(
            $"UPDATE projection_checkpoints SET status = {statusFailed}, updated_at = {failedAt}, revision = revision + 1, error = {failureReason} WHERE projection_name = {checkpoint.ProjectionName} AND source_name = {checkpoint.SourceName} AND position = {checkpoint.Position} AND revision = {expectedRevision} AND status <> {statusCompleted}",
            ct);
        checkpoint.MarkFailed(failureReason, failedAt);
    }

    /// <inheritdoc />
    public async ValueTask ResetAsync(string projectionName, string sourceName, CancellationToken ct = default)
    {
        // 按 (projection_name, source_name) 删除该投影该源的所有 position —— 用于重建投影
        await Session.ExecuteAsync(
            $"DELETE FROM projection_checkpoints WHERE projection_name = {projectionName} AND source_name = {sourceName}",
            ct);
    }
}
