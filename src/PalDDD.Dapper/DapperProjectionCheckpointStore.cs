// ─────────────────────────────────────────────────────────────
// 📽️ DapperProjectionCheckpointStore — 投影 Checkpoint 的 Dapper 实现
// ─────────────────────────────────────────────────────────────
using Dapper;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

using PalDDD.Projections;
namespace PalDDD.Dapper;

/// <summary>Dapper 投影 checkpoint 存储 — 实现 <see cref="IProjectionCheckpointStore"/>。</summary>
public sealed class DapperProjectionCheckpointStore : IProjectionCheckpointStore
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    private readonly string _insertSql;
    private readonly DapperDbType _dbType;

    /// <param name="transaction">可选共享事务（用于 UnitOfWork 模式）。</param>
    public DapperProjectionCheckpointStore(
        DbConnection connection,
        DapperDbType dbType = DapperDbType.Sqlite,
        DbTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _transaction = transaction;
        _insertSql = dbType == DapperDbType.MySql ? InsertMySql : InsertDefault;
        _dbType = dbType;
    }

    public async ValueTask<ProjectionCheckpoint?> GetAsync(
        string projectionName,
        string sourceName,
        string position,
        CancellationToken ct = default)
    {
        ValidateKeyParts(projectionName, sourceName, position);
        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ProjectionCheckpointRow>(
            new CommandDefinition(
                SelectOne,
                new { projectionName, sourceName, position },
                _transaction,
                cancellationToken: ct)).ConfigureAwait(false) is { } row
            ? row.ToCheckpoint()
            : null;
    }

    public async ValueTask<ProjectionCheckpoint?> TryStartAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        TimeSpan processingTimeout,
        CancellationToken ct = default)
    {
        ValidateKeyParts(projectionName, sourceName, position);
        // ITM-107 修复：processingTimeout 必须非负——负值使租约即刻过期
        // （leaseUntil = startedAt + timeout < startedAt），僵尸抢占语义失效。
        // 允许 TimeSpan.Zero：租约即刻过期是方言探针"超时接管（timeout=0）可重入"的
        // 合法测试语义（与 DapperInboxStore 同款无正数约束的 Inbox 路径对齐）；
        // 仅禁止负值（leaseUntil 早于 startedAt 的退化状态）。
        // TimeSpan 不实现 INumberBase，用显式比较。
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");
        var leaseUntil = startedAt + processingTimeout;
        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);

        var inserted = await connection.ExecuteAsync(
            new CommandDefinition(
                _insertSql,
                new { projectionName, sourceName, position, status = ProjectionCheckpointStatus.Processing, startedAt = ToTimeParam(startedAt), leaseUntil = ToTimeParam(leaseUntil) },
                _transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        if (inserted == 1)
        {
            var checkpoint = new ProjectionCheckpoint(
                projectionName,
                sourceName,
                position,
                ProjectionCheckpointStatus.Processing,
                startedAt);
            checkpoint.MarkProcessing(startedAt, processingTimeout);
            return checkpoint;
        }

        var existing = await GetAsync(projectionName, sourceName, position, ct).ConfigureAwait(false);
        if (existing is null || existing.Status == ProjectionCheckpointStatus.Completed)
            return null;

        if (existing.Status == ProjectionCheckpointStatus.Processing && existing.LeaseUntil > startedAt)
            return null;

        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                MarkProcessing,
                new
                {
                    projectionName,
                    sourceName,
                    position,
                    startedAt = ToTimeParam(startedAt),
                    leaseUntil = ToTimeParam(leaseUntil),
                    revision = existing.Revision
                },
                _transaction,
                cancellationToken: ct)).ConfigureAwait(false);

        if (rows == 0)
            return null;

        existing.MarkProcessing(startedAt, processingTimeout);
        return existing;
    }

    public async ValueTask MarkCompletedAsync(
        ProjectionCheckpoint checkpoint,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                MarkCompleted,
                new
                {
                    checkpoint.ProjectionName,
                    checkpoint.SourceName,
                    checkpoint.Position,
                    completedAt = ToTimeParam(completedAt),
                    checkpoint.Revision
                },
                _transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        // P2 修复：乐观并发（WHERE revision=@Revision）冲突时 rows=0，DB 状态未变——
        // 不再无条件变更本地对象，避免调用方误以为落库成功（对齐 EFCore 版 detach 语义）
        if (rows > 0)
            checkpoint.MarkCompleted(completedAt);
    }

    public async ValueTask MarkFailedAsync(
        ProjectionCheckpoint checkpoint,
        string failureReason,
        DateTimeOffset failedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                MarkFailed,
                new
                {
                    checkpoint.ProjectionName,
                    checkpoint.SourceName,
                    checkpoint.Position,
                    failedAt = ToTimeParam(failedAt),
                    error = failureReason,
                    checkpoint.Revision
                },
                _transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        // P2 修复：同 MarkCompletedAsync——并发冲突（rows=0）时不变更本地对象
        if (rows > 0)
            checkpoint.MarkFailed(failureReason, failedAt);
    }

    public async ValueTask ResetAsync(
        string projectionName,
        string sourceName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                Reset,
                new { projectionName, sourceName },
                _transaction,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// P2 修复（ToMySqlParameter 接线补齐）：按方言选择时间参数格式。
    /// <para>
    /// P2/P3 修复（十七轮）：返回 <c>object</c>（DateTimeOffset 装箱一次）是刻意的收口防线——
    /// 强类型返回会诱导调用方绕过本方法自行格式化，方言错配（PG text OID / MySQL session tz）
    /// 将重新进入；五 Store 同款声明（Outbox/Inbox/Saga/EventLog/Checkpoint）。装箱开销相对 SQL 执行成本可忽略。
    /// </para>
    /// </summary>
    private object ToTimeParam(DateTimeOffset value)
        => _dbType switch
        {
            DapperDbType.MySql => DapperAotInitializer.ToMySqlParameter(value),
            // P1 修复（八轮评审）：PG 传原生 DateTimeOffset——Npgsql 映射 timestamptz；
            // "O" string 按 text OID 发送，timestamptz <= text 无比较运算符，WHERE 必炸 42883
            DapperDbType.PostgreSql => value,
            _ => DapperAotInitializer.ToSqliteParameter(value)
        };

    private async ValueTask<DbConnection> EnsureOpenAsync(CancellationToken ct = default)
    {
        var connection = _connection;
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static void ValidateKeyParts(string projectionName, string sourceName, string position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(position);
    }

    private const string SelectOne = """
        SELECT projection_name AS ProjectionName,
               source_name AS SourceName,
               position AS Position,
               status AS Status,
               updated_at AS UpdatedAt,
               lease_until AS LeaseUntil,
               revision AS Revision,
               error AS Error
        FROM projection_checkpoints
        WHERE projection_name = @projectionName
          AND source_name = @sourceName
          AND position = @position
        """;

    private const string InsertDefault = """
        INSERT INTO projection_checkpoints (
            projection_name, source_name, position, status, updated_at, lease_until, revision, error)
        VALUES (@projectionName, @sourceName, @position, @status, @startedAt, @leaseUntil, 1, NULL)
        ON CONFLICT DO NOTHING
        """;

    private const string InsertMySql = """
        INSERT IGNORE INTO projection_checkpoints (
            projection_name, source_name, position, status, updated_at, lease_until, revision, error)
        VALUES (@projectionName, @sourceName, @position, @status, @startedAt, @leaseUntil, 1, NULL)
        """;

    private const string MarkProcessing = """
        UPDATE projection_checkpoints
        SET status = 0,
            updated_at = @startedAt,
            lease_until = @leaseUntil,
            revision = revision + 1,
            error = NULL
        WHERE projection_name = @projectionName
          AND source_name = @sourceName
          AND position = @position
          AND revision = @revision
          AND status <> 1
        """;

    private const string MarkCompleted = """
        UPDATE projection_checkpoints
        SET status = 1,
            updated_at = @completedAt,
            revision = revision + 1,
            error = NULL
        WHERE projection_name = @ProjectionName
          AND source_name = @SourceName
          AND position = @Position
          AND revision = @Revision
        """;

    private const string MarkFailed = """
        UPDATE projection_checkpoints
        SET status = 2,
            updated_at = @failedAt,
            revision = revision + 1,
            error = @error
        WHERE projection_name = @ProjectionName
          AND source_name = @SourceName
          AND position = @Position
          AND revision = @Revision
          AND status <> 1
        """;

    private const string Reset = """
        DELETE FROM projection_checkpoints
        WHERE projection_name = @projectionName
          AND source_name = @sourceName
        """;

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "Dapper 运行时通过反射实例化此 DTO 用于 QuerySingleOrDefaultAsync<ProjectionCheckpointRow> 物化。")]
    private sealed class ProjectionCheckpointRow
    {
        public string ProjectionName { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string Position { get; set; } = "";
        public ProjectionCheckpointStatus Status { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset LeaseUntil { get; set; }
        public long Revision { get; set; }
        public string? Error { get; set; }

        public ProjectionCheckpoint ToCheckpoint()
            => ProjectionCheckpoint.Rehydrate(
                ProjectionName,
                SourceName,
                Position,
                Status,
                UpdatedAt,
                LeaseUntil,
                Revision,
                Error);
    }
}
