// ─────────────────────────────────────────────────────────────
// 📽️ DapperProjectionCheckpointStore — 投影 Checkpoint 的 Dapper 实现
// ─────────────────────────────────────────────────────────────
using Dapper;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using PalDDD.Core;
using static PalDDD.Dapper.DapperSqlErrorClassifier;

using PalDDD.Projections;
namespace PalDDD.Dapper;

/// <summary>Dapper 投影 checkpoint 存储 — 实现 <see cref="IProjectionCheckpointStore"/>。</summary>
public sealed class DapperProjectionCheckpointStore : IProjectionCheckpointStore, IResumableProjectionCheckpointStore
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    /// <summary>生效事务（二轮评审 T5）：显式构造参数优先，否则查同连接 DapperUnitOfWork
    /// 的 ambient 活动事务——DI 解析的 Store（构造时无事务）也能参与 UoW 事务边界。</summary>
    private DbTransaction? Tx => _transaction ?? DapperAmbientTransaction.TryGet(_connection);

    private readonly string _insertSql;
    private readonly DapperDbType _dbType;

    /// <param name="transaction">可选共享事务（用于 UnitOfWork 模式）。</param>
    /// <param name="dbType">数据库方言（选择 INSERT 语法与时间参数格式）。
    /// v33 P3 声明：默认 Sqlite 仅为既有直连调用方兼容；非 SQLite 用户必须显式传 dbType
    ///（PG 报类型错误可见，MySQL 静默时差混存）。</param>
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
        return await connection.QueryFirstOrDefaultAsync<ProjectionCheckpointRow>(

                SelectOne,
                new { projectionName, sourceName, position },
                Tx).ConfigureAwait(false) is { } row
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

        // 2026-10-21：PG/SQLite 走单语句 UPSERT + RETURNING。新建、失败重试、租约过期接管
        // 三种“取得执行权”的情形由一条语句判定并落库，零行即 Completed 或租约未过期。
        // 原路径为 INSERT（冲突后）+ 回读 + CAS 更新，冲突侧 3 次往返。
        if (_dbType is DapperDbType.PostgreSql or DapperDbType.Sqlite)
            return await TryStartAtomicAsync(
                projectionName, sourceName, position, startedAt, leaseUntil, ct).ConfigureAwait(false);

        // 三十八轮 P1 回归修复：MySQL 路径 InsertMySql 为普通 INSERT——唯一约束冲突抛
        // MySqlException(1062)，捕获后 inserted=0 转下方 existing 回查分支（Completed 判断/
        // 租约判断/CAS 抢占语义恢复可达）。原 ON DUPLICATE KEY UPDATE revision = revision +
        // inserted == 1 判断在 MySqlConnector 默认 UseAffectedRows=false（found rows）下
        // 冲突时也返回 1，伪造 Processing checkpoint。PG/SQLite（ON CONFLICT DO NOTHING）
        // 冲突不抛异常，此 catch 仅 MySQL 可达。
        int inserted;
        try
        {
            inserted = await connection.ExecuteAsync(

                    _insertSql,
                    // 2026-09-14 CI 修复：枚举参数显式 int 化——Dapper.AOT 生成拦截器把枚举
                    // 直传驱动，PG 拒绝（InvalidCastException: Writing values of
                    // 'ProjectionCheckpointStatus' is not supported）；经典路径驱动容忍，
                    // 本地盲区（DialectProbe 本地 Skip + 探针未覆盖 Checkpoint）由 CI 容器暴露。
                    // 对齐全 Store 既有模式（Saga `(int)state.Status` 等）。
                    new { projectionName, sourceName, position, status = (int)ProjectionCheckpointStatus.Processing, startedAt = ToTimeParam(startedAt), leaseUntil = ToTimeParam(leaseUntil) },
                    Tx).ConfigureAwait(false);
        }
        catch (DbException ex) when (_dbType == DapperDbType.MySql && IsUniqueConstraintViolation(ex))
        {
            inserted = 0; // 唯一约束冲突——记录已存在，非错误
        }
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
                Tx).ConfigureAwait(false);

        if (rows == 0)
            return null;

        existing.MarkProcessing(startedAt, processingTimeout);
        return existing;
    }

    /// <summary>
    /// PG/SQLite 原子取得检查点执行权：INSERT 与条件 UPDATE 合一，返回 revision 即成功。
    /// 条件与逐语句路径逐条等价：Completed（1）不再翻转；Processing（0）仅在租约过期时接管；
    /// Failed（2）可重试。
    /// </summary>
    private async ValueTask<ProjectionCheckpoint?> TryStartAtomicAsync(
        string projectionName,
        string sourceName,
        string position,
        DateTimeOffset startedAt,
        DateTimeOffset leaseUntil,
        CancellationToken ct)
    {
        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var revision = await connection.QueryFirstOrDefaultAsync<long?>(
            UpsertProcessing,
            new
            {
                projectionName,
                sourceName,
                position,
                startedAt = ToTimeParam(startedAt),
                leaseUntil = ToTimeParam(leaseUntil),
                processing = (int)ProjectionCheckpointStatus.Processing,
                completed = (int)ProjectionCheckpointStatus.Completed
            },
            Tx).ConfigureAwait(false);

        return revision is { } startedRevision
            ? ProjectionCheckpoint.Rehydrate(
                projectionName,
                sourceName,
                position,
                ProjectionCheckpointStatus.Processing,
                startedAt,
                leaseUntil,
                startedRevision,
                error: null)
            : null;
    }

    /// <summary>PG/SQLite 原子取得执行权 SQL（revision 返回即本次成功）。</summary>
    private const string UpsertProcessing = """
        INSERT INTO projection_checkpoints (
            projection_name, source_name, position, status, updated_at, lease_until, revision, error)
        VALUES (@projectionName, @sourceName, @position, @processing, @startedAt, @leaseUntil, 1, NULL)
        ON CONFLICT (projection_name, source_name, position) DO UPDATE
        SET status = @processing,
            updated_at = @startedAt,
            lease_until = @leaseUntil,
            revision = projection_checkpoints.revision + 1,
            error = NULL
        WHERE projection_checkpoints.status <> @completed
          AND (projection_checkpoints.status <> @processing
               OR projection_checkpoints.lease_until IS NULL
               OR projection_checkpoints.lease_until <= @startedAt)
        RETURNING revision
        """;

    public async ValueTask MarkCompletedAsync(
        ProjectionCheckpoint checkpoint,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(

                MarkCompleted,
                new
                {
                    checkpoint.ProjectionName,
                    checkpoint.SourceName,
                    checkpoint.Position,
                    completedAt = ToTimeParam(completedAt),
                    checkpoint.Revision
                },
                Tx).ConfigureAwait(false);
        // P2 修复：乐观并发（WHERE revision=@revision）冲突时 rows=0，DB 状态未变——
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
        // v22 B 批：截断兜底——PD24 管线截断族
        // v27 P3（B 片 N2）：守卫前置——原截断先于 null/空白守卫执行，null 输入抛 NRE 而非
        // ArgumentException（对照姊妹 DapperOutboxStore.MarkDead 的守卫在前形态）
        // ITM-638 修复（跨栈契约一致）：Truncate(2040) 改 FailureReason.Normalize——PalORM
        // PalOrmProjectionCheckpointStore.MarkFailedAsync 与 EFCore ProjectionCheckpointDbContext
        // 均走 Normalize（截 MaxLength=2000 + 空白归一 "(no message)"），Dapper 用 2040 使同输入
        // 跨栈存储值分叉；Normalize 内部复用 Truncate（含 UTF-16 代理对守卫），无需另行处理截断。
        failureReason = FailureReason.Normalize(failureReason);

        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(

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
                Tx).ConfigureAwait(false);
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

                Reset,
                new { projectionName, sourceName },
                Tx).ConfigureAwait(false);
    }

    /// <summary>增量回放续读位置（R6/P0-3，ADR-028）——单语句聚合（方言 CAST position 为数值）：
    /// MIN(非 Completed 位置) / 无洞时 MAX(位置)+1 / 无行 null 三态；数值 CAST 失败（PG 对非数值
    /// 串抛错）按优化面回退语义返回 null 走全量回放。语义详见接口 remarks。</summary>
    public async ValueTask<string?> GetResumePositionAsync(
        string projectionName,
        string sourceName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var connection = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var castType = _dbType switch
        {
            DapperDbType.PostgreSql => "BIGINT",
            DapperDbType.MySql => "SIGNED", // MySQL CAST 目标类型为 SIGNED/UNSIGNED（无裸 INTEGER）
            _ => "INTEGER",
        };
        var sql = $"SELECT MIN(CASE WHEN status <> 1 THEN CAST(position AS {castType}) END) AS FirstIncomplete, MAX(CAST(position AS {castType})) AS MaxPosition FROM projection_checkpoints WHERE projection_name = @projectionName AND source_name = @sourceName";
        try
        {
#pragma warning disable S2077 // sql 由方言常量 castType 构造，值侧参数化
            var row = await connection.QueryFirstOrDefaultAsync<ResumeRow>(
                sql, new { projectionName, sourceName }, Tx).ConfigureAwait(false);
#pragma warning restore S2077
            if (row is null)
                return null; // 理论不可达（聚合恒返一行）——防御性按无行处理
            var resume = row.FirstIncomplete
                ?? (row.MaxPosition is { } max ? max + 1 : (long?)null);
            return resume is { } value
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }
        catch (DbException)
        {
            // PG 对非数值 Position 串 CAST 抛错（22007 族）——数值契约外的脏数据，
            // 续读是优化面：回退全量回放（语义兜底恒安全）
            return null;
        }
    }

    /// <summary>续读聚合行（Dapper 物化 DTO）。</summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "Dapper 运行时通过 AOT 拦截器/物化管线实例化此 DTO。")]
    internal sealed class ResumeRow
    {
        public long? FirstIncomplete { get; set; }
        public long? MaxPosition { get; set; }
    }


    // ITM-796（2026-09-19）：IsUniqueConstraintViolation 私有副本收口至
    // DapperSqlErrorClassifier（码集并集）——IL2075 抑制随类迁移，调用点经
    // using static 解析至类成员

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
        INSERT INTO projection_checkpoints (
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
          AND status = 0 -- v69 勘正：仅 Processing 可完成（对齐 EFCore v53 口径——v62 的 status<>1 允许 Failed→Completed 翻转，与 EFCore"Failed/Completed 实例的 MarkCompleted 不再翻转"注释矛盾；测试 ProjectionCheckpoint_MarkCompleted_FailedSnapshot_DoesNotFlip 锁定）
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
        Justification = "Dapper 运行时通过 AOT 拦截器/物化管线实例化此 DTO 用于 QueryFirstOrDefaultAsync<ProjectionCheckpointRow> 物化。")]
    internal sealed class ProjectionCheckpointRow
    {
        public string ProjectionName { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string Position { get; set; } = "";
        public ProjectionCheckpointStatus Status { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        // 三十八轮 P3 修复：DB 出现 NULL lease_until 行（手工运维插入等）时非空物化直接抛
        // Dapper 物化异常——改 nullable 并在 ToCheckpoint 兜底 default（当前写入路径不产生
        // NULL，此为防御性容错）
        public DateTimeOffset? LeaseUntil { get; set; }
        public long Revision { get; set; }
        public string? Error { get; set; }

        public ProjectionCheckpoint ToCheckpoint()
            => ProjectionCheckpoint.Rehydrate(
                ProjectionName,
                SourceName,
                Position,
                Status,
                UpdatedAt,
                LeaseUntil ?? default,
                Revision,
                Error);
    }

}
