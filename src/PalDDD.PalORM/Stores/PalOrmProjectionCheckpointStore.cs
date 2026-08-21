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
        // ITM-163 修复：补 key 空白守卫（对齐 DapperProjectionCheckpointStore/InMemoryProjectionCheckpointStore）
        ValidateKeyParts(projectionName, sourceName, position);
        // 复合主键表全程手写 SQL —— GetRawConnection + 手动 reader（QueryFirstAsync 对未注册类型返回空对象）
        // P2/P3 修复（十七轮）：修正矛盾注释——CheckpointRow 投影 DTO 已删除（Models 死代码，Store 从未使用）
        await using var cmd = Session.GetRawConnection().CreateCommand();
        cmd.CommandText = "SELECT projection_name, source_name, position, status, updated_at, lease_until, revision, error FROM projection_checkpoints WHERE projection_name = @p0 AND source_name = @p1 AND position = @p2";
        AddParam(cmd, "@p0", projectionName);
        AddParam(cmd, "@p1", sourceName);
        AddParam(cmd, "@p2", position);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

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
        // ITM-163 修复：补 key 空白守卫（对齐 DapperProjectionCheckpointStore/InMemoryProjectionCheckpointStore）
        ValidateKeyParts(projectionName, sourceName, position);
        // ITM-133 修复：processingTimeout 必须非负（对齐 DapperProjectionCheckpointStore ITM-107 同款）——
        // 负值使 leaseUntil = startedAt + timeout < startedAt，僵尸抢占语义失效
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");
        var leaseUntil = startedAt + processingTimeout;
        var statusProcessing = (int)ProjectionCheckpointStatus.Processing;

        // 方言分叉：PG/SQLite 用 ON CONFLICT DO NOTHING；MySQL 用普通 INSERT + 唯一约束冲突异常捕获。
        // 三十八轮 P1 回归修复：三十七轮 A1 的 ON DUPLICATE KEY UPDATE revision = revision +
        // affected > 0 判断在 MySqlConnector 默认 UseAffectedRows=false（found rows）下冲突时也返回 1，
        // 伪造 Processing checkpoint 绕过 Completed 判断/租约判断/CAS 抢占。现对齐 PalOrmIdempotencyStore
        // ITM-228 同款模式：冲突抛 1062 → affected=0 → 回查分支。
        int affected;
        if (TProvider.SupportsReturningClause)
        {
            affected = await Session.ExecuteAsync($"INSERT INTO projection_checkpoints (projection_name, source_name, position, status, updated_at, lease_until, revision, error) VALUES ({projectionName}, {sourceName}, {position}, {statusProcessing}, {startedAt}, {leaseUntil}, 1, NULL) ON CONFLICT DO NOTHING", ct).ConfigureAwait(false);
        }
        else
        {
            try
            {
                affected = await Session.ExecuteAsync($"INSERT INTO projection_checkpoints (projection_name, source_name, position, status, updated_at, lease_until, revision, error) VALUES ({projectionName}, {sourceName}, {position}, {statusProcessing}, {startedAt}, {leaseUntil}, 1, NULL)", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDuplicateKeyError(ex))
            {
                affected = 0; // 唯一约束冲突——记录已存在，非错误
            }
        }

        if (affected > 0)
        {
            return ProjectionCheckpoint.Rehydrate(
                projectionName, sourceName, position,
                ProjectionCheckpointStatus.Processing, startedAt,
                leaseUntil, 1, null);
        }

        var existing = await GetAsync(projectionName, sourceName, position, ct).ConfigureAwait(false);
        if (existing is null) return null;
        if (existing.Status == ProjectionCheckpointStatus.Completed) return null;
        if (existing.Status == ProjectionCheckpointStatus.Processing && existing.LeaseUntil > startedAt)
            return null;

        var expectedRevision = existing.Revision;
        var statusCompleted = (int)ProjectionCheckpointStatus.Completed;
        affected = await Session.ExecuteAsync(
            $"UPDATE projection_checkpoints SET status = {statusProcessing}, updated_at = {startedAt}, lease_until = {leaseUntil}, revision = revision + 1, error = NULL WHERE projection_name = {projectionName} AND source_name = {sourceName} AND position = {position} AND revision = {expectedRevision} AND status <> {statusCompleted}",
            ct).ConfigureAwait(false);
        if (affected == 0) return null;

        existing.MarkProcessing(startedAt, processingTimeout);
        return existing;
    }

    /// <inheritdoc />
    public async ValueTask MarkCompletedAsync(ProjectionCheckpoint checkpoint, DateTimeOffset completedAt, CancellationToken ct = default)
    {
        // ITM-163 修复：补 checkpoint null 守卫（对齐 DapperProjectionCheckpointStore/InMemoryProjectionCheckpointStore）
        ArgumentNullException.ThrowIfNull(checkpoint);
        var expectedRevision = checkpoint.Revision;
        var statusCompleted = (int)ProjectionCheckpointStatus.Completed;
        var affected = await Session.ExecuteAsync(
            $"UPDATE projection_checkpoints SET status = {statusCompleted}, updated_at = {completedAt}, revision = revision + 1, error = NULL WHERE projection_name = {checkpoint.ProjectionName} AND source_name = {checkpoint.SourceName} AND position = {checkpoint.Position} AND revision = {expectedRevision}",
            ct).ConfigureAwait(false);
        // 修复覆盖残留：对齐 Dapper 版同方法（rows>0 才变更本地对象）——
        // 乐观锁冲突时 DB 未变，不假装落库成功
        if (affected > 0)
            checkpoint.MarkCompleted(completedAt);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(ProjectionCheckpoint checkpoint, string failureReason, DateTimeOffset failedAt, CancellationToken ct = default)
    {
        // ITM-163 修复：补 checkpoint null + failureReason 空白守卫（对齐 DapperProjectionCheckpointStore/InMemoryProjectionCheckpointStore）
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        var expectedRevision = checkpoint.Revision;
        var statusFailed = (int)ProjectionCheckpointStatus.Failed;
        var statusCompleted = (int)ProjectionCheckpointStatus.Completed;
        var affected = await Session.ExecuteAsync(
            $"UPDATE projection_checkpoints SET status = {statusFailed}, updated_at = {failedAt}, revision = revision + 1, error = {failureReason} WHERE projection_name = {checkpoint.ProjectionName} AND source_name = {checkpoint.SourceName} AND position = {checkpoint.Position} AND revision = {expectedRevision} AND status <> {statusCompleted}",
            ct).ConfigureAwait(false);
        if (affected > 0)
            checkpoint.MarkFailed(failureReason, failedAt);
    }

    /// <inheritdoc />
    public async ValueTask ResetAsync(string projectionName, string sourceName, CancellationToken ct = default)
    {
        // ITM-163 修复：补 names 空白守卫（对齐 DapperProjectionCheckpointStore/InMemoryProjectionCheckpointStore）
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        await Session.ExecuteAsync(
            $"DELETE FROM projection_checkpoints WHERE projection_name = {projectionName} AND source_name = {sourceName}",
            ct).ConfigureAwait(false);
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static void ValidateKeyParts(string projectionName, string sourceName, string position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(position);
    }

    /// <summary>
    /// 三十八轮 P1 回归修复：判定异常是否为唯一约束冲突（MySQL 1062/1586、PG 23505、SQLite UNIQUE、SqlServer 2601/2627）。
    /// 仅捕获重复键——其他错误原样上抛。与 PalOrmIdempotencyStore/PalOrmInboxStore 同型。
    /// </summary>
    [UnconditionalSuppressMessage("Aot", "IL2075:RequiresDynamicallyAccessedMembers",
        Justification = "PalORM 适配层为非 AOT（IsAotCompatible=false）；反射读取 provider 异常属性用于错误分类。")]
    private static bool IsDuplicateKeyError(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var type = ex.GetType();
            var typeName = type.Name;

            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(ex) is int mysqlNum
                && (mysqlNum == 1062 || mysqlNum == 1586))
                return true;

            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(ex) is string pgState
                && pgState == "23505")
                return true;

            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && ex.Message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
                return true;

            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(ex) is int sqlNum
                && (sqlNum == 2601 || sqlNum == 2627))
                return true;
        }
        return false;
    }
}
