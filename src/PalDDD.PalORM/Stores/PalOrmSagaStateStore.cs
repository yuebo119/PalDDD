using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PalORM;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// SagaState Store 的 PalORM 实现 —— 双泛型核心基类（TProvider 方言 + TState Saga 状态）。
/// <para>
/// <b>SagaStateRow 未注册为实体</b>（开放泛型 TState 无法 [Table] 注册）——
/// 查询路径用 <see cref="DbDataReader"/> 手动映射（QueryAsync/QueryFirstAsync 对未注册类型返回空对象）。
/// </para>
/// <para>
/// <b>Saga 快照</b>：开放泛型 TState 在编译期未知，<c>[OwnedJson]</c> 无法静态绑定 ——
/// 保留手写 <see cref="JsonSerializer"/>.Serialize(state, <see cref="_jsonTypeInfo"/>) 序列化整 TState 到 <c>saga_data</c> 列。
/// </para>
/// <para>
/// <b>乐观锁</b>：<c>version</c> 列（int）—— UPDATE 时手写 <c>WHERE version = @expected</c>，
/// 0 行返回视为冲突；不走 UpdateAsync（SagaStateRow 非注册实体，无 [ConcurrencyCheck]）。
/// </para>
/// </summary>
public class PalOrmSagaStateStore<TProvider, TState> : ISagaStateStore<TState>
    where TProvider : IDbProvider
    where TState : SagaState, new()
{
    [SuppressMessage("Performance", "CA1051:Do not declare visible instance fields",
        Justification = "框架库基类 —— 派生类需直接访问 Session。")]
    protected readonly DataSession<TProvider> Session;

    private readonly JsonTypeInfo<TState>? _jsonTypeInfo;
    private readonly TimeProvider _clock;

    /// <summary>
    /// ITM-127（PG jsonb 42804）：PG 的 saga_data 为 jsonb 列，text 参数无隐式赋值转换——
    /// PG 方言派生类覆写为 true 后，INSERT/UPDATE 对快照参数加 CAST(... AS jsonb)
    /// （对齐 Dapper SqlTemplates.SagaInsertPG/SagaUpdatePG）。SQLite/MySQL 列类型收 text 无此需要。
    /// </summary>
    protected virtual bool RequiresJsonbCast => false;

    /// <summary>构造 Saga Store。</summary>
    /// <param name="session">Scoped 数据库会话。</param>
    /// <param name="jsonTypeInfo">可选 STJ 源生成上下文。</param>
    /// <param name="clock">可选时间提供者（默认 System）；用于租约时间一致性（P1-8 修复）。</param>
    public PalOrmSagaStateStore(DataSession<TProvider> session, JsonTypeInfo<TState>? jsonTypeInfo = null, TimeProvider? clock = null)
    {
        Session = session;
        _jsonTypeInfo = jsonTypeInfo;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
    {
        // P3 修复（八轮）：与 Dapper/EFCore 姊妹实现对齐——batchSize 非正直接拒绝
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // SagaStateRow 未注册 —— 用 GetRawConnection + 手动 reader
        // 三十四轮（中断态超时兜底）：观测查询与 Lease 同步纳入 AwaitingHumanDecision（5）
        await using var cmd = Session.GetRawConnection().CreateCommand();
        cmd.CommandText = "SELECT saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until FROM saga_states WHERE status IN (@p0, @p1) ORDER BY created_at LIMIT @p2";
        AddParam(cmd, "@p0", (int)SagaStatus.Active);
        AddParam(cmd, "@p1", (int)SagaStatus.AwaitingHumanDecision);
        AddParam(cmd, "@p2", batchSize);
        return await ReadSagasAsync(cmd, ct);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner, TimeSpan leaseDuration, int batchSize, CancellationToken ct)
    {
        // ITM-163 修复：补 owner 空白守卫（对齐 InMemorySagaStateStore/SagaStateDbContext/DapperSagaStateStore）
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        // P3 修复（八轮）：与 Dapper/EFCore 姊妹实现对齐——batchSize 非正直接拒绝
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var now = _clock.GetUtcNow();  // P1-8：用注入的 Clock 替代硬编码 UtcNow
        var until = now + leaseDuration;

        // Saga 租约：UPDATE 子查询
        // MySQL 不支持 UPDATE...WHERE id IN (SELECT...LIMIT) —— 用 JOIN 替代
        if (TProvider.SupportsReturningClause)
        {
            // PG/SQLite 路径
            // ITM-173 修复（二十九轮）：PG 子查询补 FOR UPDATE SKIP LOCKED——对齐
            // Dapper SagaLeaseActivePG（SqlTemplates.cs），消除多 worker 并发租约的
            // 互相阻塞与后到者覆盖先到者 leased_by（ITM-076 实测现象）。SQLite 无
            // FOR UPDATE 语法（库级单写者串行，无需），按 Dialect 分支。
            // ⚠️ 分支构造完整 FormattableString：PalORM 的 ExecuteAsync(FormattableString)
            // 把每个插值参数化为 @p{N}（PD18）——若把 SQL 片段放插值里会生成
            // `LIMIT @p5 @p6` 语法错误（实证：SQLite near "@p5"）；又因 ExecuteAsync
            // 仅接受 FormattableString，无法用字符串拼接，故按方言分支整句构造。
            if (TProvider.Dialect == global::PalORM.SqlDialect.PostgreSql)
            {
                await Session.ExecuteAsync(
                    $"UPDATE saga_states SET leased_by = {owner}, leased_until = {until} WHERE saga_id IN (SELECT saga_id FROM saga_states WHERE status IN ({(int)SagaStatus.Active}, {(int)SagaStatus.AwaitingHumanDecision}) AND (leased_until IS NULL OR leased_until <= {now}) ORDER BY created_at LIMIT {batchSize} FOR UPDATE SKIP LOCKED)",
                    ct);
            }
            else
            {
                await Session.ExecuteAsync(
                    $"UPDATE saga_states SET leased_by = {owner}, leased_until = {until} WHERE saga_id IN (SELECT saga_id FROM saga_states WHERE status IN ({(int)SagaStatus.Active}, {(int)SagaStatus.AwaitingHumanDecision}) AND (leased_until IS NULL OR leased_until <= {now}) ORDER BY created_at LIMIT {batchSize})",
                    ct);
            }
        }
        else
        {
            // MySQL 特化路径：JOIN 子查询
            await Session.ExecuteAsync(
                $"UPDATE saga_states t JOIN (SELECT saga_id FROM saga_states WHERE status IN ({(int)SagaStatus.Active}, {(int)SagaStatus.AwaitingHumanDecision}) AND (leased_until IS NULL OR leased_until <= {now}) ORDER BY created_at LIMIT {batchSize}) AS sub ON t.saga_id = sub.saga_id SET t.leased_by = {owner}, t.leased_until = {until}",
                ct);
        }

        // 按 lease 标识回读（手动 reader）
        // ⚠️ 已知限制（八轮评审 P3，声明不修）：MySQL 两步路径回读按 (leased_by, leased_until) 匹配——
        // 同一 owner 在同一 tick（until 完全相等，如 FakeTimeProvider 冻结时间）发起两次租约时，
        // 第二次回读会混入第一次已锁定的批次。生产触发条件近乎为零（DATETIME(6) 微秒精度 + 单 owner
        // 串行租约）；PG/SQLite 走单语句 UPDATE 天然免疫。候选 id 预取需 IN 列表参数化，PalORM 的
        // FormattableString 路径不支持（详见 PalOrmOutboxStore.LeasePendingMessagesAsync 同款声明）。
        // ITM-076 实测结论（2026-08-16 双连接探针，5 轮）：跨 owner 并发 UPDATE...JOIN 时
        // derived table 按语句开始快照物化，后到者会覆盖先到者的 leased_by（last-writer-wins），
        // 但"双 worker 同批回读"窗口未复现——被覆盖方的回读按 (owner, until) 已不再匹配。
        // 残余窗口由 SaveChangesAsync 的 version 乐观锁兜底（与 Dapper SagaLeaseActiveMySql
        // 声明的"由 SagaUpdate 的 version 乐观锁兜底"对齐）——重复保存被版本冲突拒绝。
        // 回读补状态守卫：已被处理方标记终态（Completed 等）的租约残留行不再混入
        //（三十四轮：守卫集与租约集同步扩为 Active + AwaitingHumanDecision）。
        await using var cmd = Session.GetRawConnection().CreateCommand();
        cmd.CommandText = "SELECT saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until FROM saga_states WHERE leased_by = @p0 AND leased_until = @p1 AND status IN (@p2, @p3) ORDER BY created_at";
        AddParam(cmd, "@p0", owner);
        AddParam(cmd, "@p1", until);
        AddParam(cmd, "@p2", (int)SagaStatus.Active);
        AddParam(cmd, "@p3", (int)SagaStatus.AwaitingHumanDecision);
        return await ReadSagasAsync(cmd, ct);
    }

    /// <inheritdoc />
    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
    {
        await using var cmd = Session.GetRawConnection().CreateCommand();
        cmd.CommandText = "SELECT saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until FROM saga_states WHERE saga_id = @p0";
        AddParam(cmd, "@p0", sagaId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return Materialize(ReadSagaRow(reader));
    }

    /// <inheritdoc />
    public async ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
    {
        // ITM-163 修复：补 state null 守卫（对齐 InMemorySagaStateStore/DapperSagaStateStore/SagaStateDbContext）
        ArgumentNullException.ThrowIfNull(state);

        // ITM-228 修复（三十二轮）：JsonTypeInfo null 时 saga_data 写 NULL——
        // 业务字段（CustomerId 等）全部丢失。fail-fast 比静默丢数据更诚实。
        if (_jsonTypeInfo is null)
            throw new InvalidOperationException(
                $"PalOrmSagaStateStore<TProvider,TState> requires JsonTypeInfo<TState> to persist saga_data. " +
                "Register with AddPalSagaStore<TState>(jsonTypeInfo) or pass it to the constructor. " +
                "Without it, SaveChangesAsync silently drops all business fields (ITM-228).");

        var existing = await GetByIdAsync(state.SagaId, ct);

        // saga_data：STJ 手写序列化（开放泛型 TState，[OwnedJson] 不可用）
        var jsonData = JsonSerializer.Serialize(state, _jsonTypeInfo);

        if (existing is null)
        {
            // P2 修复（八轮）：并发插入同一新 Saga 的 TOCTOU 兜底（与 DapperSagaStateStore 对齐）——
            // PalORM Session.ExecuteAsync 直透底层 provider 异常（DataSession.Query.cs 无包装），
            // 唯一约束冲突转换为语义化并发异常，调用方重读后走 UPDATE 路径即可
            try
            {
                await Session.ExecuteAsync(
                    RequiresJsonbCast
                        ? (FormattableString)$"INSERT INTO saga_states (saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until) VALUES ({state.SagaId.ToString()}, {state.CurrentState}, {(int)state.Status}, {state.CreatedAt}, {state.CompletedAt}, {state.Error}, {state.ErrorAt}, {state.Version}, CAST({jsonData} AS jsonb), {state.LeasedBy}, {state.LeasedUntil})"
                        : (FormattableString)$"INSERT INTO saga_states (saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until) VALUES ({state.SagaId.ToString()}, {state.CurrentState}, {(int)state.Status}, {state.CreatedAt}, {state.CompletedAt}, {state.Error}, {state.ErrorAt}, {state.Version}, {jsonData}, {state.LeasedBy}, {state.LeasedUntil})",
                    ct);
            }
            catch (DbException ex) when (IsUniqueConstraintViolation(ex))
            {
                throw new InvalidOperationException(
                    $"Saga {state.SagaId} 被并发实例同时创建（主键冲突）——请重新加载后以 UPDATE 保存。", ex);
            }
            return 1;
        }

        // P2 修复（乐观锁快照）：expectedVersion 取调用方加载时的 state.Version（内存快照），
        // 而非本次新读的 existing.Version（DB 最新快照）——后者使"加载后被他人改过"的窗口
        // 检测失效（本次读到的已是 bump 后版本，比较恒过，退化为最后写者胜）。
        // 与 EFCore 版 Version concurrency token（内存快照比对）语义对齐。
        var expectedVersion = state.Version;
        var affected = await Session.ExecuteAsync(
            RequiresJsonbCast
                ? (FormattableString)$"UPDATE saga_states SET current_state = {state.CurrentState}, status = {(int)state.Status}, completed_at = {state.CompletedAt}, version = version + 1, error = {state.Error}, error_at = {state.ErrorAt}, saga_data = CAST({jsonData} AS jsonb), leased_by = {state.LeasedBy}, leased_until = {state.LeasedUntil} WHERE saga_id = {state.SagaId.ToString()} AND version = {expectedVersion}"
                : (FormattableString)$"UPDATE saga_states SET current_state = {state.CurrentState}, status = {(int)state.Status}, completed_at = {state.CompletedAt}, version = version + 1, error = {state.Error}, error_at = {state.ErrorAt}, saga_data = {jsonData}, leased_by = {state.LeasedBy}, leased_until = {state.LeasedUntil} WHERE saga_id = {state.SagaId.ToString()} AND version = {expectedVersion}",
            ct);
        if (affected > 0) state.Version++;
        return affected;
    }

    /// <summary>从 DbDataReader 读取多行 Saga。</summary>
    private async Task<List<TState>> ReadSagasAsync(DbCommand cmd, CancellationToken ct)
    {
        var result = new List<TState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(Materialize(ReadSagaRow(reader)));
        }
        return result;
    }

    /// <summary>从 reader 当前行读取 SagaStateRow（手动映射，避免 QueryAsync 对未注册类型返回空）。</summary>
    private static SagaStateRow ReadSagaRow(DbDataReader reader) => new()
    {
        SagaId = reader.GetString(0),
        CurrentState = reader.GetString(1),
        Status = reader.GetInt32(2),
        CreatedAt = reader.GetDateTime(3),
        CompletedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
        Error = reader.IsDBNull(5) ? null : reader.GetString(5),
        ErrorAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
        Version = reader.GetInt32(7),
        SagaData = reader.IsDBNull(8) ? null : reader.GetString(8),
        LeasedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
        LeasedUntil = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
    };

    /// <summary>SagaStateRow → TState（JSON 反序列化 + 元数据覆盖）。
    /// <para>当 <c>_jsonTypeInfo</c> 为 null 或 SagaData 为空时，从数据库列恢复 SagaId/CreatedAt（init-only 通过 object initializer 赋值）。
    /// 其余可变属性在下方逐行覆盖。与 DapperSagaStateStore.Materialize 行为对齐。</para>
    /// </summary>
    private TState Materialize(SagaStateRow row)
    {
        var sagaId = PalUlid.Parse(row.SagaId);
        TState state;
        if (_jsonTypeInfo is not null && !string.IsNullOrEmpty(row.SagaData))
        {
            state = JsonSerializer.Deserialize(row.SagaData!, _jsonTypeInfo!) ?? new TState { SagaId = sagaId, CreatedAt = row.CreatedAt };
        }
        else
        {
            state = new TState { SagaId = sagaId, CreatedAt = row.CreatedAt };
        }

        state.CurrentState = row.CurrentState;
        state.Status = (SagaStatus)row.Status;
        state.Version = row.Version;
        state.CompletedAt = row.CompletedAt;
        state.Error = row.Error;
        state.ErrorAt = row.ErrorAt;
        state.LeasedBy = row.LeasedBy;
        state.LeasedUntil = row.LeasedUntil;
        return state;
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// INSERT 路径的并发插入兜底（P2 修复·八轮）：两个并发 SaveChangesAsync 保存同一新 Saga
    /// 都判 existing==null 都走 INSERT 时，第二个撞 saga_id 主键抛原始 provider 异常。
    /// 此处捕获唯一约束冲突并转换为带 SagaId 的语义化异常，调用方可区分"并发冲突"与"数据错误"
    /// （与 DapperSagaStateStore.IsUniqueConstraintViolation 同型）。
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:This",
        Justification = "Provider 异常鸭子类型判定。裁剪后 GetProperty 返回 null → 判定 false → 原始 provider 异常原样上抛（安全降级，不崩溃）。")]
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

            // P2 修复（二十一轮）：补 SqliteException 类型限定（镜像 InboxDbContext 十七轮修复，PD17）——裸消息匹配误判非唯一约束异常
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

    /// <summary>临时 SagaStateRow（内部用，不注册为实体）。</summary>
    private sealed class SagaStateRow
    {
        public string SagaId { get; set; } = "";
        public string CurrentState { get; set; } = "Initial";
        public int Status { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset? ErrorAt { get; set; }
        public int Version { get; set; }
        public string? SagaData { get; set; }
        public string? LeasedBy { get; set; }
        public DateTimeOffset? LeasedUntil { get; set; }
    }
}
