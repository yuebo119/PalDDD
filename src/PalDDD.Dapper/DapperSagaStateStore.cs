// ─────────────────────────────────────────────────────────────
// 💾 DapperSagaStateStore — Saga 状态持久化（UPSERT + 乐观并发控制）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ Dapper snake_case 映射 — 纯字符串操作，零反射。
//   ✅ 原生 SQL — 所有 DML 在编译时确定。
//   ✅ 完整 TState 快照通过调用方传入 JsonTypeInfo<TState>，使用 STJ source generation。
//   ⚠️ 建议配合 Dapper.AOT Source Generator 使用以获得完全 NativeAOT 兼容。
//
// 💡 什么是 Saga？
//   ｜ Saga 是一种分布式事务模式，将一个跨多个服务的长业务流程
//   ｜ 拆分为一系列本地事务，每个步骤有对应的补偿操作。
//   ｜ 例如"下单→扣库存→扣款"：如果扣款失败，Saga 补偿恢复库存。
//
// 💡 乐观并发控制（Optimistic Concurrency Control）：
//   ｜ UPDATE 使用 WHERE version=@v 条件——只有版本号匹配时才执行更新。
//   ｜ 如果版本号不匹配（被其他实例修改），更新影响 0 行。
//
// 💡 UPSERT 语义：
//   ｜ SaveChangesAsync 内部先查询后决定：存在→UPDATE（版本号自增），不存在→INSERT
// ─────────────────────────────────────────────────────────────
//   ✅ Dapper.AOT SG 处理所有 QueryAsync<TState>/ExecuteAsync 拦截。
//   ✅ 原生 SQL — 所有 DML 在编译时确定。
// ─────────────────────────────────────────────────────────────

using Dapper;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PalUlid = ByteAether.Ulid.Ulid;

using PalDDD.Transactions;
namespace PalDDD.Dapper;

public sealed class DapperSagaStateStore<TState> : ISagaStateStore<TState>
    where TState : SagaState, new()
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    private readonly JsonTypeInfo<TState>? _jsonTypeInfo;

    /// <summary>
    /// 数据库方言（P2 修复·八轮）——时间参数按方言格式化（见 <see cref="ToTimeParam"/>）。
    /// 默认 Sqlite，保持既有直接构造调用方（测试等）行为不变；
    /// DI（AddPalDapperTransactions）注册了 DapperDbType 单例，容器构造时注入真实方言。
    /// </summary>
    private readonly DapperDbType _dbType;

    /// <param name="transaction">可选共享事务（用于 UnitOfWork 模式）</param>
    /// <param name="jsonTypeInfo">可选 STJ source-generated type info；传入后持久化完整 <typeparamref name="TState"/> 快照。</param>
    /// <param name="dbType">数据库方言——决定时间参数绑定格式（默认 Sqlite，见 <see cref="ToTimeParam"/>）。</param>
    private readonly TimeProvider _timeProvider;

    public DapperSagaStateStore(
        DbConnection connection,
        DbTransaction? transaction = null,
        JsonTypeInfo<TState>? jsonTypeInfo = null,
        TimeProvider? timeProvider = null,
        DapperDbType dbType = DapperDbType.Sqlite)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction;
        _jsonTypeInfo = jsonTypeInfo;
        // P3 修复（时钟双轨清零）：可选注入，默认 System——与 PalOrmSagaStateStore 对齐
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dbType = dbType;
    }

    public async ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SagaStateRow>(
            new CommandDefinition(SqlTemplates.SagaActive, new { n = batchSize }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(Materialize).ToList();
    }

    public async ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var until = now.Add(leaseDuration);
        // P1 修复（十一轮·实测发现）：MySQL 不支持 UPDATE ... WHERE id IN (SELECT ... LIMIT)——
        // JOIN 形态替代（对齐 PalORM 版）；SQLite/PG 支持子查询内 LIMIT 保持原状
        // P2/P3 修复（十七轮）：PG 分支改用 SKIP LOCKED 变体——与 DapperOutboxStore 租约 PG 路径
        // 同款（多 worker 并发租约跳过彼此锁定的行，而非阻塞后拿到空批次）；
        // MySQL READ COMMITTED 两步窗口由 SagaUpdate 的 version 乐观锁兜底；SQLite 单写者无影响
        var leaseSql = _dbType switch
        {
            DapperDbType.PostgreSql => SqlTemplates.SagaLeaseActivePG,
            DapperDbType.MySql => SqlTemplates.SagaLeaseActiveMySql,
            _ => SqlTemplates.SagaLeaseActive
        };
        await conn.ExecuteAsync(
            new CommandDefinition(leaseSql, new { owner, until = ToTimeParam(until), now = ToTimeParam(now), n = batchSize }, _transaction, cancellationToken: ct)).ConfigureAwait(false);

        var rows = await conn.QueryAsync<SagaStateRow>(
            new CommandDefinition(SqlTemplates.SagaSelectByLease, new { owner, until = ToTimeParam(until) }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(Materialize).ToList();
    }

    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
    {
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QueryFirstOrDefaultAsync<SagaStateRow>(
            new CommandDefinition(SqlTemplates.SagaById, new { id = DapperAotInitializer.ToSqliteParameter(sagaId) }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : Materialize(row);
    }

    /// <summary>UPSERT 持久化 — 存在则更新，不存在则插入</summary>
    public async ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        var existing = await GetByIdAsync(state.SagaId, ct).ConfigureAwait(false);
        var sagaData = SerializeState(state);
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var rows = await conn.ExecuteAsync(
                new CommandDefinition(
                    // P1 修复（十一轮·实测发现）：PG 的 saga_data JSONB 列需显式 CAST（text→jsonb 无赋值转换）
                    _dbType == DapperDbType.PostgreSql ? SqlTemplates.SagaUpdatePG : SqlTemplates.SagaUpdate,
                    new
                    {
                        cs = state.CurrentState,
                        st = (int)state.Status,
                        ca = state.CompletedAt.HasValue ? ToTimeParam(state.CompletedAt.Value) : null,
                        err = state.Error,
                        ea = state.ErrorAt.HasValue ? ToTimeParam(state.ErrorAt.Value) : null,
                        data = sagaData,
                        leasedBy = state.LeasedBy,
                        leasedUntil = state.LeasedUntil.HasValue ? ToTimeParam(state.LeasedUntil.Value) : null,
                        id = DapperAotInitializer.ToSqliteParameter(state.SagaId),
                        v = state.Version
                    },
                    _transaction,
                    cancellationToken: ct)).ConfigureAwait(false);

            if (rows > 0) state.Version++;
            return rows;
        }

        int inserted;
        try
        {
            inserted = await conn.ExecuteAsync(
                new CommandDefinition(
                    // P1 修复（十一轮·实测发现）：PG 的 saga_data JSONB 列需显式 CAST（text→jsonb 无赋值转换）
                    _dbType == DapperDbType.PostgreSql ? SqlTemplates.SagaInsertPG : SqlTemplates.SagaInsert,
                    new
                    {
                        id = DapperAotInitializer.ToSqliteParameter(state.SagaId),
                        cs = state.CurrentState,
                        st = (int)state.Status,
                        ca = ToTimeParam(state.CreatedAt),
                        completedAt = state.CompletedAt.HasValue ? ToTimeParam(state.CompletedAt.Value) : null,
                        err = state.Error,
                        ea = state.ErrorAt.HasValue ? ToTimeParam(state.ErrorAt.Value) : null,
                        data = sagaData,
                        leasedBy = state.LeasedBy,
                        leasedUntil = state.LeasedUntil.HasValue ? ToTimeParam(state.LeasedUntil.Value) : null
                    },
                    _transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException ex) when (IsUniqueConstraintViolation(ex))
        {
            // P2 修复：并发插入同一新 Saga 的 TOCTOU 兜底——唯一约束冲突转换为
            // 语义化并发异常（而非原始 provider 异常），调用方重读后走 UPDATE 路径即可
            throw new InvalidOperationException(
                $"Saga {state.SagaId} 被并发实例同时创建（主键冲突）——请重新加载后以 UPDATE 保存。", ex);
        }
        return inserted;
    }

    /// <summary>
    /// INSERT 路径的并发插入兜底（P2 修复）：两个并发 SaveChangesAsync 保存同一新 Saga
    /// 都判 existing==null 都走 INSERT 时，第二个撞 saga_id 主键抛原始 provider 异常。
    /// 此处捕获唯一约束冲突并转换为带 SagaId 的语义化异常，调用方可区分"并发冲突"与"数据错误"。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:This",
        Justification = "Provider 异常鸭子类型判定（与 DapperEventLog 同型）。裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级，不崩溃）。")]
    private static bool IsUniqueConstraintViolation(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            var type = inner.GetType();
            var typeName = type.Name;

            if (typeName.Equals("PostgresException", StringComparison.Ordinal)
                && type.GetProperty("SqlState")?.GetValue(inner) is string sqlState
                && sqlState == "23505")
            {
                return true;
            }

            if (typeName.Equals("MySqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int mysqlNumber
                && (mysqlNumber == 1062 || mysqlNumber == 1586))
            {
                return true;
            }

            if (typeName.Equals("SqlException", StringComparison.Ordinal)
                && type.GetProperty("Number")?.GetValue(inner) is int sqlServerNumber
                && (sqlServerNumber == 2601 || sqlServerNumber == 2627))
            {
                return true;
            }

            // SQLite: Microsoft.Data.Sqlite.SqliteException 消息包含 "UNIQUE constraint"
            // ITM-192 修复（三十轮）：补 SqliteException 类型限定（镜像 DapperEventLog
            // ITM-188 / PalORM / EFCore 姊妹，PD17）——裸消息匹配会把文案恰好含该词组的
            // 非唯一约束异常误判为并发冲突 → 转 InvalidOperationException 掩盖真实数据错误。
            var message = inner.Message;
            if (typeName.Equals("SqliteException", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(message)
                && message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string? SerializeState(TState state)
        => _jsonTypeInfo is null ? null : JsonSerializer.Serialize(state, _jsonTypeInfo);

    /// <summary>
    /// P2 修复（八轮评审）：按方言选择时间参数格式（与 DapperOutboxStore.ToTimeParam 同型统一）——
    /// MySQL：DATETIME(6) 列与带偏移 "O" 格式比较依赖 session tz，统一无偏移 UTC；
    /// PG：原生 <see cref="DateTimeOffset"/> 参数——Npgsql 映射 timestamptz，"O" 格式 string
    /// 按 text OID 发送，timestamptz 与 text 间无比较运算符，租约 WHERE 必炸；
    /// Sqlite：维持 "O" 格式 string（既有行为）。
    /// <para>
    /// P2/P3 修复（十七轮）：返回 <c>object</c>（DateTimeOffset 装箱一次）是刻意的收口防线——
    /// 强类型返回会诱导调用方绕过本方法自行格式化，方言错配（PG text OID / MySQL session tz）
    /// 将重新进入；五 Store 同款声明（Outbox/Inbox/Saga/EventLog/Checkpoint）。装箱开销相对 SQL 执行成本可忽略。
    /// </para>
    /// </summary>
    private object ToTimeParam(DateTimeOffset value) => _dbType switch
    {
        DapperDbType.MySql => DapperAotInitializer.ToMySqlParameter(value),
        DapperDbType.PostgreSql => value,
        _ => DapperAotInitializer.ToSqliteParameter(value)
    };

    /// <summary>
    /// 确保数据库连接已打开（异步版本，避免线程池阻塞）。
    /// 连接生命周期由 DI 容器管理的 Scoped DbConnection 控制，此处不负责关闭。
    /// 与 DapperOutboxStore/DapperInboxStore/DapperEventLog/DapperProjectionCheckpointStore 保持一致。
    /// </summary>
    private async ValueTask<DbConnection> EnsureOpenAsync(CancellationToken ct = default)
    {
        var conn = _connection;
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private TState Materialize(SagaStateRow row)
    {
        var state = row.SagaData is not null && _jsonTypeInfo is not null
            ? JsonSerializer.Deserialize(row.SagaData, _jsonTypeInfo) ?? new TState { SagaId = row.SagaId, CreatedAt = row.CreatedAt }
            : new TState { SagaId = row.SagaId, CreatedAt = row.CreatedAt };

        state.CurrentState = row.CurrentState;
        state.Status = (SagaStatus)row.Status;
        state.CompletedAt = row.CompletedAt;
        state.Error = row.Error;
        state.ErrorAt = row.ErrorAt;
        state.Version = row.Version;
        state.LeasedBy = row.LeasedBy;
        state.LeasedUntil = row.LeasedUntil;
        return state;
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "Dapper 运行时通过 QueryAsync<T> 实例化此行类型用于物化。")]
    private sealed class SagaStateRow
    {
        public PalUlid SagaId { get; init; }
        public string CurrentState { get; init; } = string.Empty;
        public int Status { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public string? Error { get; init; }
        public DateTimeOffset? ErrorAt { get; init; }
        public int Version { get; init; }
        public string? SagaData { get; init; }
        public string? LeasedBy { get; init; }
        public DateTimeOffset? LeasedUntil { get; init; }
    }
}
