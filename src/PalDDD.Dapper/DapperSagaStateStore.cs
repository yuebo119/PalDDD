// ─────────────────────────────────────────────────────────────
// 💾 DapperSagaStateStore — Saga 状态持久化（UPSERT + 乐观并发控制）
// ─────────────────────────────────────────────────────────────
// AOT 状态：见下方"三十七轮勘正"块（v11 删除本旧块——"零反射/建议启用 Dapper.AOT SG"
// 与勘正事实及 ADR-020 裁决【启用不做】冲突）。
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
//   ✅ Dapper.AOT SG 未启用——经典反射(三十七轮勘正) QueryAsync<TState>/ExecuteAsync 拦截。
//   ✅ 原生 SQL — 所有 DML 在编译时确定。
// ─────────────────────────────────────────────────────────────

using Dapper;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PalDDD.Core;
using PalUlid = ByteAether.Ulid.Ulid;

using PalDDD.Transactions;
namespace PalDDD.Dapper;

public sealed class DapperSagaStateStore<TState> : ISagaStateStore<TState>
    where TState : SagaState, new()
{
    private readonly DbConnection _connection;
    private readonly DbTransaction? _transaction;
    /// <summary>生效事务（二轮评审 T5）：显式构造参数优先，否则查同连接 DapperUnitOfWork
    /// 的 ambient 活动事务——DI 解析的 Store（构造时无事务）也能参与 UoW 事务边界。</summary>
    private DbTransaction? Tx => _transaction ?? DapperAmbientTransaction.TryGet(_connection);

    private readonly JsonTypeInfo<TState>? _jsonTypeInfo;

    /// <summary>
    /// 数据库方言（P2 修复·八轮）——时间参数按方言格式化（见 <see cref="ToTimeParam"/>）。
    /// 默认 Sqlite，保持既有直接构造调用方（测试等）行为不变；
    /// DI（AddPalDapperTransactions）注册了 DapperDbType 单例，容器构造时注入真实方言。
    /// </summary>
    private readonly DapperDbType _dbType;

    /// <summary>时钟源——构造可选注入，默认 <see cref="TimeProvider.System"/>（与 PalOrmSagaStateStore 对齐）。</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 构造 Saga 状态存储。
    /// </summary>
    /// <param name="connection">数据库连接（本 Store 不持有其生命周期）。</param>
    /// <param name="transaction">可选共享事务（用于 UnitOfWork 模式）。</param>
    /// <param name="jsonTypeInfo">可选 STJ source-generated type info；传入后持久化完整 <typeparamref name="TState"/> 快照。</param>
    /// <param name="timeProvider">可选时钟注入，默认 <see cref="TimeProvider.System"/>（v35 P3：原三个 <c>&lt;param&gt;</c> 标签错位挂在本字段上，已迁移至本构造函数并补齐缺项）。</param>
    /// <param name="dbType">数据库方言——决定时间参数绑定格式（默认 Sqlite，见 <see cref="ToTimeParam"/>）。</param>
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
            new CommandDefinition(SqlTemplates.SagaActive, new { n = batchSize }, Tx, cancellationToken: ct)).ConfigureAwait(false);
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
        // v26 P3 守卫族：leaseDuration 边界守卫——v25 只修了 EFCore/PalORM 两姊妹
        //（SagaStateDbContext/PalOrmSagaStateStore LeaseActiveSagasAsync 同型漏网）。
        // v27 P3 勘正（B 片）：原注释"同样受秒数溢出影响"失实——Dapper 版绑定原生 @until 参数
        //（ToTimeParam 产 DateTimeOffset/"O" 串），无秒数换算，上界守卫无本版实害路径；守卫
        // 保留，理由改"防御直调路径 + 与姊妹对称"。非正租约（租约即刻过期/永不过期语义错乱）
        // 为实害。消息与双检形态对齐两姊妹同款
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration must be greater than zero.");
        if (leaseDuration.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "leaseDuration is too large to represent in whole seconds for the lease LeasedUntil value.");

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
            new CommandDefinition(leaseSql, new { owner, until = ToTimeParam(until), now = ToTimeParam(now), n = batchSize }, Tx, cancellationToken: ct)).ConfigureAwait(false);

        // 三十八轮 P3 声明（对齐 OutboxSelectByLease 的 ITM-109 格式）：两步租约回读按
        // (leased_by, leased_until) 匹配——同一 owner 在同一 tick（until 完全相等，如
        // FakeTimeProvider 冻结时间）发起两次租约时第二次回读会混入第一次已锁定的批次。
        // 生产触发条件近乎为零（DATETIME(6) 微秒精度 + 单 owner 串行租约）；PG 走
        // FOR UPDATE SKIP LOCKED 单语句天然免疫。残余窗口由 SagaUpdate 的 version 乐观锁兜底。
        var rows = await conn.QueryAsync<SagaStateRow>(
            new CommandDefinition(SqlTemplates.SagaSelectByLease, new { owner, until = ToTimeParam(until) }, Tx, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(Materialize).ToList();
    }

    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
    {
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QueryFirstOrDefaultAsync<SagaStateRow>(
            new CommandDefinition(SqlTemplates.SagaById, new { id = DapperAotInitializer.ToSqliteParameter(sagaId) }, Tx, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : Materialize(row);
    }

    /// <summary>UPSERT 持久化 — 存在则更新，不存在则插入</summary>
    public async ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        // v28 P3：存储层截断兜底（镜像同文件族 DapperOutboxStore.MarkDead/ReleaseForRetry
        // 的 2040 形态）——error 列上限 2048（EFCore 侧 DDL），调用层 SagaProcessor 保存前
        // 已 FailureReason.Normalize（2000）截断，此处防直调路径（未经 SagaProcessor 的
        // SaveChangesAsync）超长 Error 直传 SQL，跨栈共用表场景 EFCore 侧 2048 列写入失败。
        // 仅截断不归一空白——Error=null 是"未出错"语义，Normalize 会把 null 归一为
        // "(no message)" 破坏该语义，故不采用（UPDATE/INSERT 两处 err 赋值点共用此收口）。
        // v29 P3：改经 FailureReason.Truncate 共享收口——[..2040] 切片可能切半 UTF-16
        // 代理对（超长含 emoji 的 Error，镜像 Normalize 的 v8 代理对防御），末位高代理
        // 回退一位防孤立高代理入库（S1 五处截断点 + PalORM/EFCore Saga 姊妹同款）。
        // v38 P3：截断值先算局部变量、保存结果确认后才赋回 state.Error——原实现在保存
        // 结果未知前就变异调用方对象（UPDATE 版本冲突 rows=0 时 DB 未变而调用方 Error
        // 已被截断），对齐姊妹 DapperProjectionCheckpointStore.MarkCompletedAsync/
        // MarkFailedAsync 的"rows>0 才变异"形态；UPDATE/INSERT 两处 err 赋值点共用截断值。
        var truncatedError = FailureReason.Truncate(state.Error, 2040);

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
                        err = truncatedError,
                        ea = state.ErrorAt.HasValue ? ToTimeParam(state.ErrorAt.Value) : null,
                        data = sagaData,
                        leasedBy = state.LeasedBy,
                        leasedUntil = state.LeasedUntil.HasValue ? ToTimeParam(state.LeasedUntil.Value) : null,
                        id = DapperAotInitializer.ToSqliteParameter(state.SagaId),
                        v = state.Version
                    },
                    Tx,
                    cancellationToken: ct)).ConfigureAwait(false);

            if (rows > 0)
            {
                state.Version++;
                state.Error = truncatedError; // v38 P3：rows>0（DB 已更新）才回写调用方对象
            }
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
                        err = truncatedError,
                        ea = state.ErrorAt.HasValue ? ToTimeParam(state.ErrorAt.Value) : null,
                        data = sagaData,
                        leasedBy = state.LeasedBy,
                        leasedUntil = state.LeasedUntil.HasValue ? ToTimeParam(state.LeasedUntil.Value) : null
                    },
                    Tx,
                    cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException ex) when (IsUniqueConstraintViolation(ex))
        {
            // P2 修复：并发插入同一新 Saga 的 TOCTOU 兜底——唯一约束冲突转换为
            // 语义化并发异常（而非原始 provider 异常），调用方重读后走 UPDATE 路径即可
            throw new InvalidOperationException(
                $"Saga {state.SagaId} 被并发实例同时创建（主键冲突）——请重新加载后以 UPDATE 保存。", ex);
        }
        if (inserted > 0)
            state.Error = truncatedError; // v38 P3：INSERT 已落库才回写调用方对象（失败路径经异常上抛不达此处）
        return inserted;
    }

    /// <summary>
    /// INSERT 路径的并发插入兜底（P2 修复）：两个并发 SaveChangesAsync 保存同一新 Saga
    /// 都判 existing==null 都走 INSERT 时，第二个撞 saga_id 主键抛原始 provider 异常。
    /// 此处捕获唯一约束冲突并转换为带 SagaId 的语义化异常，调用方可区分"并发冲突"与"数据错误"。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075:This",
        Justification = "Provider 异常鸭子类型判定（与 DapperEventLog 同型）。裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级，不崩溃）。")]
    // v13 口径统一：本分类器含 SqlServer 2601/2627 分支——与 Inbox/Checkpoint 版的"本 Store 无
    // SqlServer 方言不含"注释口径不同。统一口径：分类器为跨 provider 鸭子类型判定（防未来扩方言），
    // 死分支是防御性保留（与 DapperDbType 无 SqlServer 值的现状不冲突）。
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
