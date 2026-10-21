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
//   ｜ SaveChangesAsync 先 UPDATE（已有行 1 次往返）；影响 0 行再 INSERT（新行 2 次往返）；
//   ｜ 唯一冲突返回 0（并发下的可重试信号）。不是「先查询后决定」。
// ─────────────────────────────────────────────────────────────
//   ✅ Dapper.AOT 已启用（experiment/dapper-aot-full）——QueryAsync<TState>/ExecuteAsync
//     调用点经拦截器生成代码接管（三十七轮勘正的经典反射口径已随启用退役）。
//   ✅ 原生 SQL — 所有 DML 在编译时确定。
// ─────────────────────────────────────────────────────────────

using Dapper;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PalDDD.Core;
using PalUlid = ByteAether.Ulid.Ulid;

using PalDDD.Transactions;
using static PalDDD.Dapper.DapperSqlErrorClassifier;
namespace PalDDD.Dapper;

public sealed class DapperSagaStateStore<TState> : ISagaStateStore<TState>, ISagaLeaseBatchStore<TState>, ISagaScanCursorStore<TState>, IDueTimeSagaStore<TState>, ISagaBatchStateStore<TState>
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
            SqlTemplates.SagaActive, new { n = batchSize }, Tx).ConfigureAwait(false);
        return rows.Select(Materialize).ToList();
    }

    public ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct)
        => LeaseCoreAsync(owner, leaseDuration, batchSize, dueOnly: false, dueNow: null, ct);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<TState>> LeaseDueSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        DateTimeOffset now,
        CancellationToken ct)
        => LeaseCoreAsync(owner, leaseDuration, batchSize, dueOnly: true, dueNow: now, ct);

    private async ValueTask<IReadOnlyList<TState>> LeaseCoreAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        bool dueOnly,
        DateTimeOffset? dueNow,
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
        var now = dueNow ?? _timeProvider.GetUtcNow();
        var until = now.Add(leaseDuration);
        // P1 修复（十一轮·实测发现）：MySQL 不支持 UPDATE ... WHERE id IN (SELECT ... LIMIT)——
        // JOIN 形态替代（对齐 PalORM 版）；SQLite/PG 支持子查询内 LIMIT 保持原状
        // P2/P3 修复（十七轮）：PG 分支改用 SKIP LOCKED 变体——与 DapperOutboxStore 租约 PG 路径
        // 同款（多 worker 并发租约跳过彼此锁定的行，而非阻塞后拿到空批次）；
        // MySQL READ COMMITTED 两步窗口由 SagaUpdate 的 version 乐观锁兜底；SQLite 单写者无影响
        // EPSL 地毯审计 2026-10-16 B 批 #3：PG/SQLite 改单语句 UPDATE ... RETURNING *
        // （镜像 DapperOutboxStore 租约 PG/SQLite 路径先例）——省 SagaSelectByLease 回读
        // 往返，且按行锁语义返回刚锁定行，两步回读按 (leased_by, leased_until) 匹配的同
        // tick 混批窗口（三十八轮 P3 声明，ITM-109 同族）在此形态下不存在；回读的
        // status IN (0,5) 守卫（ITM-134）由子查询谓词隐含满足，语义不减。
        // MySQL 无 RETURNING，维持两步。
        if (_dbType == DapperDbType.PostgreSql)
        {
            var returningRows = await conn.QueryAsync<SagaStateRow>(
                dueOnly ? SqlTemplates.SagaLeaseActiveDuePGReturning : SqlTemplates.SagaLeaseActivePGReturning,
                new { owner, until = ToTimeParam(until), now = ToTimeParam(now), n = batchSize }, Tx).ConfigureAwait(false);
            // RETURNING 无排序保证——物化后按 CreatedAt 稳定排序，保留原两步回读
            // SagaSelectByLease 的 ORDER BY created_at 契约（B 批 #3）
            return returningRows.Select(Materialize).OrderBy(static s => s.CreatedAt).ToList();
        }
        if (_dbType == DapperDbType.Sqlite)
        {
            var returningRows = await conn.QueryAsync<SagaStateRow>(
                dueOnly ? SqlTemplates.SagaLeaseActiveDueSqliteReturning : SqlTemplates.SagaLeaseActiveSqliteReturning,
                new { owner, until = ToTimeParam(until), now = ToTimeParam(now), n = batchSize }, Tx).ConfigureAwait(false);
            return returningRows.Select(Materialize).OrderBy(static s => s.CreatedAt).ToList();
        }

        var leaseSql = _dbType switch
        {
            DapperDbType.PostgreSql => dueOnly ? SqlTemplates.SagaLeaseActiveDuePG : SqlTemplates.SagaLeaseActivePG,
            DapperDbType.MySql => dueOnly ? SqlTemplates.SagaLeaseActiveDueMySql : SqlTemplates.SagaLeaseActiveMySql,
            _ => dueOnly ? SqlTemplates.SagaLeaseActiveDue : SqlTemplates.SagaLeaseActive
        };
        await conn.ExecuteAsync(
            leaseSql, new { owner, until = ToTimeParam(until), now = ToTimeParam(now), n = batchSize }, Tx).ConfigureAwait(false);

        // 三十八轮 P3 声明（对齐 OutboxSelectByLease 的 ITM-109 格式）：两步租约回读按
        // (leased_by, leased_until) 匹配——同一 owner 在同一 tick（until 完全相等，如
        // FakeTimeProvider 冻结时间）发起两次租约时第二次回读会混入第一次已锁定的批次。
        // 生产触发条件近乎为零（DATETIME(6) 微秒精度 + 单 owner 串行租约）。残余窗口由
        // SagaUpdate 的 version 乐观锁兜底。（PG/SQLite 已走上方 RETURNING 单语句路径，
        // 本两步形态仅 MySQL。）
        var rows = await conn.QueryAsync<SagaStateRow>(
            SqlTemplates.SagaSelectByLease, new { owner, until = ToTimeParam(until) }, Tx).ConfigureAwait(false);
        return rows.Select(Materialize).ToList();
    }

    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
    {
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        // v66 命名勘正：ToSqliteParameter(Ulid) 名带 Sqlite 但实为方言无关 string 绑定
        //（Crockford Base32 文本，SQLite TEXT / PG text / MySQL CHAR 列均按纯文本比较，
        // 无方言分派需求——与 ToTimeParam 的时间参数不同）。本文件三处 SagaId 绑定
        //（ GetById / Update / Insert）全方言共用；方法名沿用 DapperAotInitializer 适配器族
        // 历史命名（见 DapperAotInitializer.ToSqliteParameter）。
        var row = await conn.QueryFirstOrDefaultAsync<SagaStateRow>(
            SqlTemplates.SagaById, new { id = DapperAotInitializer.ToSqliteParameter(sagaId) }, Tx).ConfigureAwait(false);
        return row is null ? null : Materialize(row);
    }

    /// <summary>UPSERT 持久化 — 存在则更新，不存在则插入</summary>
    public ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SaveCoreAsync(state, Tx, ct);
    }

    /// <summary>UPSERT 核心 — 允许批量路径传入本地事务（无 ambient 事务时 SaveMany 自建）。</summary>
    private async ValueTask<int> SaveCoreAsync(TState state, DbTransaction? tx, CancellationToken ct)
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

        // EPSL 地毯审计 2026-10-11 B-3：UPDATE 先行——原「全行 SELECT + 全量反序列化
        //（GetByIdAsync→Materialize）做存在性判断」把 O(快照大小) 的网络传输与 JSON
        // 反序列化花在只做 null 判断的结果上（扫描器非超时释放路径每 saga 一次）。
        // 新形态：乐观锁 UPDATE 先行；0 行时轻量存在性探测（COUNT，无数据传输）区分
        // 「版本冲突」（行在 → 返回 0）与「新行」（INSERT 路径）。行为等价
        ///（0/1/并发插入异常三态不变）；新行路径多一次探测往返（Saga 创建为冷路径）。
        var sagaData = SerializeState(state);
        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.ExecuteAsync(

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
                    nextCheckAt = state.NextCheckAt.HasValue ? ToTimeParam(state.NextCheckAt.Value) : null,
                    leasedBy = state.LeasedBy,
                    leasedUntil = state.LeasedUntil.HasValue ? ToTimeParam(state.LeasedUntil.Value) : null,
                    id = DapperAotInitializer.ToSqliteParameter(state.SagaId),
                    v = state.Version
                },
                tx).ConfigureAwait(false);

        if (rows > 0)
        {
            state.Version++;
            state.Error = truncatedError; // v38 P3：rows>0（DB 已更新）才回写调用方对象
            return rows;
        }

        // 2026-10-19 性能实施轮：UPDATE 0 行后直接尝试 INSERT，唯一冲突即行已存在
        //（版本冲突或并发创建），返回 0 走乐观冲突契约。原实现先发 COUNT 探测再 INSERT，
        // 新 Saga 路径固定 3 次往返；去掉探测后成功新建降为 2 次，已有行更新维持 1 次。
        int inserted;
        try
        {
            inserted = await conn.ExecuteAsync(

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
                        nextCheckAt = state.NextCheckAt.HasValue ? ToTimeParam(state.NextCheckAt.Value) : null,
                        leasedBy = state.LeasedBy,
                        leasedUntil = state.LeasedUntil.HasValue ? ToTimeParam(state.LeasedUntil.Value) : null
                    },
                    tx).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 行已存在（版本冲突或并发创建同一新 Saga）——统一返回 0（乐观冲突契约），
            // 调用方重新加载后走 UPDATE 路径。
            return 0;
        }
        if (inserted > 0)
            state.Error = truncatedError; // v38 P3：INSERT 已落库才回写调用方对象（失败路径经异常上抛不达此处）
        return inserted;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// EPSL 地毯审计 2026-10-11 B-1：单语句批量释放（IN 列表 + leased_by 守卫 + 换代）。
    /// 2026-10-21 真库探针修正：原 `IN @ids` 在 Dapper.AOT + Npgsql 下被绑成单个数组参数，
    /// PG 生成 `IN $n`（语法错误 42601）；改为显式 `@idN` 参数列表，三方言同形。
    /// </remarks>
    public async ValueTask<int> ReleaseLeasesAsync(string owner, IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return 0;

        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var (sql, parameters) = BuildReleaseLeasesSql(owner, sagas, checkedAt: null);
        return await conn.ExecuteAsync(sql, parameters, Tx).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>扫描轮转：释放时把 leased_until 写成本次扫描时刻（过去），legacy 租约按
    /// leased_until NULLS FIRST 排序，已扫描行退到未扫描行之后，避免固定前 N 条饿死。</remarks>
    public async ValueTask<int> ReleaseLeasesAsync(
        string owner,
        IReadOnlyList<TState> sagas,
        DateTimeOffset checkedAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return 0;

        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        var (sql, parameters) = BuildReleaseLeasesSql(owner, sagas, checkedAt);
        return await conn.ExecuteAsync(sql, parameters, Tx).ConfigureAwait(false);
    }

    /// <summary>构造释放 SQL：显式 @idN 列表，避免 Dapper.AOT 把数组绑成单个 PG 参数。</summary>
    private (string Sql, DynamicParameters Parameters) BuildReleaseLeasesSql(
        string owner,
        IReadOnlyList<TState> sagas,
        DateTimeOffset? checkedAt)
    {
        var dp = new DynamicParameters();
        dp.Add("owner", owner);
        if (checkedAt is { } at)
            dp.Add("checkedAt", ToTimeParam(at));

        var sb = new StringBuilder(160 + sagas.Count * 32);
        sb.Append("UPDATE saga_states SET leased_by=NULL, leased_until=")
          .Append(checkedAt is null ? "NULL" : "@checkedAt")
          .Append(", version=version+1 WHERE leased_by=@owner AND saga_id IN (");
        for (var i = 0; i < sagas.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var name = "@id" + i;
            sb.Append(name);
            dp.Add("id" + i, DapperAotInitializer.ToSqliteParameter(sagas[i].SagaId));
        }
        sb.Append(')');
        return (sb.ToString(), dp);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 到期推进只写 next_check_at + version，避免逐 Saga 全快照序列化与 EF ChangeTracker
    /// 扫描；无 ambient 事务时整批一个事务提交（有则并入调用方事务）。
    /// </remarks>
    public async ValueTask<int> AdvanceNextCheckAtAsync(string owner, IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return 0;

        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        DbTransaction? ownTx = null;
        var tx = Tx;
        if (tx is null)
        {
            ownTx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            tx = ownTx;
        }

        try
        {
            // SQLite 单语句参数上限 999：每行 3 参数，按 300 行分块；PG/MySQL 同块宽。
            const int chunkRows = 300;
            var affected = 0;
            var fullyApplied = true;
            for (var start = 0; start < sagas.Count; start += chunkRows)
            {
                var count = Math.Min(chunkRows, sagas.Count - start);
                var (sql, parameters) = BuildAdvanceNextCheckBatch(owner, sagas, start, count);
                var rows = await conn.ExecuteAsync(sql, parameters, tx).ConfigureAwait(false);
                affected += rows;
                if (rows != count)
                    fullyApplied = false;
            }

            if (ownTx is not null)
                await ownTx.CommitAsync(ct).ConfigureAwait(false);

            // 全命中才回写内存版本（部分冲突时调用方应重新加载；处理器随后只做租约释放）。
            if (fullyApplied)
            {
                foreach (var saga in sagas)
                    saga.Version++;
            }
            return affected;
        }
        finally
        {
            // 未提交即异常时 Dispose 自动回滚；ambient 事务不在此处置。
            if (ownTx is not null)
                await ownTx.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 构造单语句多行 NextCheckAt 推进（每块最多 300 行）。
    /// PG: UPDATE ... FROM (VALUES ...)；SQLite: UPDATE ... FROM (VALUES ...)；
    /// MySQL: UPDATE ... JOIN (SELECT ... UNION ALL ...)。
    /// </summary>
    private (string Sql, DynamicParameters Parameters) BuildAdvanceNextCheckBatch(
        string owner,
        IReadOnlyList<TState> sagas,
        int start,
        int count)
    {
        var dp = new DynamicParameters();
        dp.Add("owner", owner);
        var sb = new StringBuilder(64 + count * 56);

        if (_dbType == DapperDbType.MySql)
        {
            sb.Append("UPDATE saga_states s JOIN (");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(" UNION ALL ");
                sb.Append("SELECT @id").Append(i)
                  .Append(" AS saga_id, @next").Append(i)
                  .Append(" AS next_check_at, @v").Append(i)
                  .Append(" AS version");
                AddAdvanceParams(dp, sagas[start + i], i);
            }
            sb.Append(") v ON s.saga_id = v.saga_id SET s.next_check_at = v.next_check_at, s.version = s.version + 1 WHERE s.version = v.version AND s.leased_by = @owner");
        }
        else if (_dbType == DapperDbType.PostgreSql)
        {
            sb.Append("UPDATE saga_states AS s SET next_check_at = v.next_check_at, version = s.version + 1 FROM (VALUES ");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("(@id").Append(i).Append("::text, CAST(@next").Append(i)
                  .Append(" AS timestamptz), CAST(@v").Append(i).Append(" AS integer))");
                AddAdvanceParams(dp, sagas[start + i], i);
            }
            sb.Append(") AS v(saga_id, next_check_at, version) WHERE s.saga_id = v.saga_id AND s.version = v.version AND s.leased_by = @owner");
        }
        else
        {
            // SQLite UPDATE ... FROM：用 SELECT/UNION ALL 派生表（SQLite 不接受
            // FROM (VALUES ...) AS v(col1,col2) 的列别名列表）。
            sb.Append("UPDATE saga_states SET next_check_at = v.next_check_at, version = saga_states.version + 1 FROM (");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(" UNION ALL ");
                sb.Append("SELECT @id").Append(i).Append(" AS saga_id, @next").Append(i)
                  .Append(" AS next_check_at, @v").Append(i).Append(" AS version");
                AddAdvanceParams(dp, sagas[start + i], i);
            }
            sb.Append(") AS v WHERE saga_states.saga_id = v.saga_id AND saga_states.version = v.version AND saga_states.leased_by = @owner");
        }

        return (sb.ToString(), dp);
    }

    private void AddAdvanceParams(DynamicParameters dp, TState saga, int index)
    {
        dp.Add($"id{index}", (string)DapperAotInitializer.ToSqliteParameter(saga.SagaId));
        dp.Add($"next{index}", saga.NextCheckAt.HasValue ? ToTimeParam(saga.NextCheckAt.Value) : null);
        dp.Add($"v{index}", saga.Version);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<int>> SaveManyAsync(IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sagas);
        if (sagas.Count == 0) return [];

        var conn = await EnsureOpenAsync(ct).ConfigureAwait(false);
        DbTransaction? ownTx = null;
        var tx = Tx;
        if (tx is null)
        {
            ownTx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            tx = ownTx;
        }

        try
        {
            var results = new int[sagas.Count];
            var pending = new List<(int Index, TState State)>(sagas.Count);
            for (var i = 0; i < sagas.Count; i++)
            {
                // 新 Saga（Version=0）走插入路径；已有行按版本批量 UPDATE。
                if (sagas[i].Version == 0)
                    results[i] = await SaveCoreAsync(sagas[i], tx, ct).ConfigureAwait(false);
                else
                    pending.Add((i, sagas[i]));
            }

            // PG/MySQL 每块 500 行、SQLite 每块 90 行（11 参数/行，避开 999 上限）。
            var chunkRows = _dbType == DapperDbType.Sqlite ? 90 : 500;
            for (var start = 0; start < pending.Count; start += chunkRows)
            {
                var count = Math.Min(chunkRows, pending.Count - start);
                var (sql, parameters) = BuildSaveManyBatch(pending, start, count);
                var rows = await conn.ExecuteAsync(sql, parameters, tx).ConfigureAwait(false);

                // 全命中才回写内存；部分冲突返回 0（已落库的子集不谎报成功，下次重载后重试）。
                if (rows != count) continue;
                for (var i = 0; i < count; i++)
                {
                    var (index, state) = pending[start + i];
                    state.Version++;
                    state.Error = FailureReason.Truncate(state.Error, 2040);
                    results[index] = 1;
                }
            }

            if (ownTx is not null)
                await ownTx.CommitAsync(ct).ConfigureAwait(false);
            return results;
        }
        finally
        {
            if (ownTx is not null)
                await ownTx.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 构造单语句多行 UPSERT 更新（仅 Version&gt;0 的已有行）：
    /// PG 用 UPDATE ... FROM (VALUES ...)，SQLite 用 UPDATE ... FROM (SELECT ... UNION ALL ...)，
    /// MySQL 用 UPDATE ... JOIN (SELECT ... UNION ALL ...)。
    /// </summary>
    private (string Sql, DynamicParameters Parameters) BuildSaveManyBatch(
        IReadOnlyList<(int Index, TState State)> sagas,
        int start,
        int count)
    {
        var dp = new DynamicParameters();
        var sb = new StringBuilder(200 + count * 220);

        if (_dbType == DapperDbType.PostgreSql)
        {
            sb.Append("UPDATE saga_states AS s SET current_state = v.cs, status = v.st, completed_at = v.ca, error = v.err, error_at = v.ea, version = s.version + 1, saga_data = CAST(v.data AS jsonb), next_check_at = v.next, leased_by = v.lb, leased_until = v.lu FROM (VALUES ");
            AppendSaveManyRows(sb, dp, sagas, start, count, pg: true);
            sb.Append(") AS v(id, cs, st, ca, err, ea, version, data, next, lb, lu) WHERE s.saga_id = v.id AND s.version = v.version");
        }
        else if (_dbType == DapperDbType.MySql)
        {
            sb.Append("UPDATE saga_states s JOIN (");
            AppendSaveManyRows(sb, dp, sagas, start, count, pg: false);
            sb.Append(") v ON s.saga_id = v.id SET s.current_state = v.cs, s.status = v.st, s.completed_at = v.ca, s.error = v.err, s.error_at = v.ea, s.version = s.version + 1, s.saga_data = v.data, s.next_check_at = v.next, s.leased_by = v.lb, s.leased_until = v.lu WHERE s.version = v.version");
        }
        else
        {
            // SQLite 不接受 FROM (VALUES ...) AS v(col,...) 的列别名列表；用 SELECT/UNION ALL。
            sb.Append("UPDATE saga_states SET current_state = v.cs, status = v.st, completed_at = v.ca, error = v.err, error_at = v.ea, version = saga_states.version + 1, saga_data = v.data, next_check_at = v.next, leased_by = v.lb, leased_until = v.lu FROM (");
            AppendSaveManyRows(sb, dp, sagas, start, count, pg: false);
            sb.Append(") AS v WHERE saga_states.saga_id = v.id AND saga_states.version = v.version");
        }

        return (sb.ToString(), dp);
    }

    private void AppendSaveManyRows(
        StringBuilder sb,
        DynamicParameters dp,
        IReadOnlyList<(int Index, TState State)> sagas,
        int start,
        int count,
        bool pg)
    {
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                sb.Append(pg ? ", " : " UNION ALL ");

            var state = sagas[start + i].State;
            var id = "@id" + i;
            var cs = "@cs" + i;
            var st = "@st" + i;
            var ca = "@ca" + i;
            var err = "@err" + i;
            var ea = "@ea" + i;
            var v = "@v" + i;
            var data = "@data" + i;
            var next = "@next" + i;
            var lb = "@lb" + i;
            var lu = "@lu" + i;

            sb.Append(pg ? "(CAST(" : "SELECT ").Append(id);
            if (pg)
                sb.Append(" AS text), CAST(").Append(cs).Append(" AS text), CAST(").Append(st)
                  .Append(" AS integer), CAST(").Append(ca).Append(" AS timestamptz), CAST(")
                  .Append(err).Append(" AS text), CAST(").Append(ea).Append(" AS timestamptz), CAST(")
                  .Append(v).Append(" AS integer), CAST(").Append(data).Append(" AS text), CAST(")
                  .Append(next).Append(" AS timestamptz), CAST(").Append(lb).Append(" AS text), CAST(")
                  .Append(lu).Append(" AS timestamptz))");
            else
                sb.Append(" AS id, ").Append(cs).Append(" AS cs, ").Append(st).Append(" AS st, ")
                  .Append(ca).Append(" AS ca, ").Append(err).Append(" AS err, ").Append(ea)
                  .Append(" AS ea, ").Append(v).Append(" AS version, ").Append(data)
                  .Append(" AS data, ").Append(next).Append(" AS next, ").Append(lb)
                  .Append(" AS lb, ").Append(lu).Append(" AS lu");

            dp.Add("id" + i, (string)DapperAotInitializer.ToSqliteParameter(state.SagaId));
            dp.Add("cs" + i, state.CurrentState);
            dp.Add("st" + i, (int)state.Status);
            dp.Add("ca" + i, state.CompletedAt.HasValue ? ToTimeParam(state.CompletedAt.Value) : null);
            dp.Add("err" + i, FailureReason.Truncate(state.Error, 2040));
            dp.Add("ea" + i, state.ErrorAt.HasValue ? ToTimeParam(state.ErrorAt.Value) : null);
            dp.Add("v" + i, state.Version);
            dp.Add("data" + i, SerializeState(state));
            dp.Add("next" + i, state.NextCheckAt.HasValue ? ToTimeParam(state.NextCheckAt.Value) : null);
            dp.Add("lb" + i, state.LeasedBy);
            dp.Add("lu" + i, state.LeasedUntil.HasValue ? ToTimeParam(state.LeasedUntil.Value) : null);
        }
    }

    // ITM-796（2026-09-19）：IsUniqueConstraintViolation 私有副本收口至
    // DapperSqlErrorClassifier（码集并集；SqlServer 防御分支与 v13 口径统一注释
    // 随类迁移）——IL2075 抑制随类迁移，调用点经 using static 解析

    /// <summary>序列化 saga_data 快照。未注册 JsonTypeInfo 时 fail-fast（对齐 PalORM
    /// ITM-228 同位置修复，decision-2026-09-19-saga-snapshot-failfast）——原「静默返回
    /// null」使 saga_data 写 NULL、业务字段全部丢失且无诊断，跨栈契约分叉已收口。</summary>
    private string SerializeState(TState state)
    {
        if (_jsonTypeInfo is null)
            throw new InvalidOperationException(
                $"DapperSagaStateStore<TState> requires JsonTypeInfo<TState> to persist saga_data. " +
                "Register with AddPalDapperSagaSnapshot(jsonTypeInfo) or pass it to the constructor. " +
                "Without it, SaveChangesAsync silently drops all business fields (ITM-228 / decision-2026-09-19).");
        return JsonSerializer.Serialize(state, _jsonTypeInfo);
    }

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
        // v3 轮姊妹收口（对齐 PalOrmSagaStateStore ITM-228/P3c 同款 fail-fast）：saga_data 为
        // 字面 "null" JSON 文本时 Deserialize 返回 null——原 `?? new TState` 兜底会静默丢全部
        // 业务字段、后续 SaveChangesAsync 以空状态覆写扩散损坏。改抛语义化异常；else 分支
        // （saga_data 列为 NULL 或空串的元数据行）是合法语义，保留兜底。
        // 2026-10-21 评审 F4：空串与 PalORM 的 !string.IsNullOrEmpty 对齐——原 `is not null`
        // 会把空串送进 JsonSerializer.Deserialize("") 抛 JsonException，同一行跨栈读取结果分叉。
        var state = !string.IsNullOrEmpty(row.SagaData) && _jsonTypeInfo is not null
            ? JsonSerializer.Deserialize(row.SagaData, _jsonTypeInfo)
              ?? throw new InvalidOperationException(
                  $"Saga {row.SagaId} 的 saga_data 为损坏的 'null' 文本——拒绝静默降级空状态（对齐 PalOrmSagaStateStore fail-fast 契约）。")
            : new TState { SagaId = row.SagaId, CreatedAt = row.CreatedAt };

        state.CurrentState = row.CurrentState;
        state.Status = (SagaStatus)row.Status;
        state.CompletedAt = row.CompletedAt;
        state.Error = row.Error;
        state.ErrorAt = row.ErrorAt;
        state.Version = row.Version;
        state.LeasedBy = row.LeasedBy;
        state.LeasedUntil = row.LeasedUntil;
        state.NextCheckAt = row.NextCheckAt;
        return state;
    }

}

// 🔬 AOT 实验分支：原 SagaStateRow 为泛型类内嵌类型，DAP051 拒绝（generic-by-containment，
//    生成器无法为封闭泛型内的 Row 生成物化代码）——迁出至命名空间级非泛型类型（字段全部
//    具体类型，不依赖 TState）。internal 可见性。
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Dapper（AOT 拦截器）编译期引用此行类型生成物化代码。")]
internal sealed class SagaStateRow
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
    public DateTimeOffset? NextCheckAt { get; init; }
}
