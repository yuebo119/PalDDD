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
/// <para>
/// <b>时间戳读取（ITM-242）</b>：MySQL DATETIME 回读的 DateTime Kind=Unspecified，隐式
/// DateTime→DateTimeOffset 转换对 Unspecified 套<b>本地时区偏移</b>（探针实测 -8h 累积回拨）。
/// <see cref="GetAsync"/> 统一 <see cref="DateTime.SpecifyKind"/>(Utc) 使隐式转换套 offset 0
///（Npgsql timestamptz / SQLite 带偏移文本均返回 Kind=Utc，幂等无害）。
/// </para>
/// <para>
/// <b>raw command 事务（ITM-243）</b>：手动 reader 查询经 <see cref="CreateRawCommand"/> 创建命令并挂接
/// <c>PalOrmAmbientTransaction</c>（IUnitOfWork 事务边界 Session 键控传导）——MySQL 活动事务下
/// 未挂 cmd.Transaction 的命令抛 InvalidOperationException（MySqlConnector 严格校验）。
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
        // ITM-243：经 CreateRawCommand 挂接 IUnitOfWork 环境事务
        await using var cmd = CreateRawCommand();
        cmd.CommandText = "SELECT projection_name, source_name, position, status, updated_at, lease_until, revision, error FROM projection_checkpoints WHERE projection_name = @p0 AND source_name = @p1 AND position = @p2";
        AddParam(cmd, "@p0", projectionName);
        AddParam(cmd, "@p1", sourceName);
        AddParam(cmd, "@p2", position);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        return ProjectionCheckpoint.Rehydrate(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            (ProjectionCheckpointStatus)reader.GetInt32(3),
            GetUtc(reader, 4),
            // ITM-270（R41，对齐 DapperProjectionCheckpointStore 三十八轮 P3）：NULL lease_until 行
            // （手工运维插入；Dapper 正式 DDL 的该列可空，跨栈共用表场景 NULL 合法）容错为 default
            // 而非抛 SqlNullValueException——防御性容错，当前 PalORM 写入路径不产生 NULL
            reader.IsDBNull(5) ? default : GetUtc(reader, 5),
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
            catch (Exception ex) when (SqlErrorClassifier.IsUniqueKeyViolation(ex))
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

    /// <summary>
    /// ITM-242：读回时间戳并标记 UTC。MySQL DATETIME 回读的 DateTime Kind=Unspecified，
    /// 隐式 DateTime→DateTimeOffset 转换对 Unspecified 套本地时区偏移（探针实测 -8h 累积回拨）；
    /// SpecifyKind(Utc) 使隐式转换套 offset 0。Npgsql timestamptz 与 SQLite 带偏移文本
    /// 均返回 Kind=Utc（探针实测幂等）。
    /// </summary>
    private static DateTimeOffset GetUtc(DbDataReader reader, int ordinal)
        => DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);

    /// <summary>
    /// ITM-243：创建 raw command 并挂接 IUnitOfWork 活动事务。GetRawConnection().CreateCommand()
    /// 的手动命令不自动 enlist（PalORM 仅 ExecuteAsync 路径自动挂 GetActiveTransaction()），
    /// MySQL 活动事务下未挂接抛 InvalidOperationException（MySqlConnector 严格校验，探针实测）。
    /// </summary>
    private DbCommand CreateRawCommand()
    {
        var cmd = Session.GetRawConnection().CreateCommand();
        var tx = PalOrmAmbientTransaction.TryGet(Session);
        if (tx is not null) cmd.Transaction = tx;
        return cmd;
    }

    private static void ValidateKeyParts(string projectionName, string sourceName, string position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(position);
    }

}
