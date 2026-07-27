using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ByteAether.Ulid;
using PalORM;
using PalDDD.PalORM.Models;
using PalDDD.Transactions;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.PalORM.Stores;

/// <summary>
/// SagaState Store 的 PalORM 实现 —— 双泛型核心基类（TProvider 方言 + TState Saga 状态）。
/// <para>
/// <b>双泛型 DI 限制</b>：.NET DI 容器开放泛型注册要求实现 arity ≤ 服务 arity。
/// <c>ISagaStateStore&lt;TState&gt;</c> 单参数 —— 此基类双参数（TProvider+TState），
/// 由方言包提供中间类固化 TProvider，如：
/// <c>SqliteSagaStateStore&lt;TState&gt; : PalOrmSagaStateStore&lt;SqliteProvider, TState&gt;</c>，
/// 然后 <c>services.AddScoped(typeof(ISagaStateStore&lt;&gt;), typeof(SqliteSagaStateStore&lt;&gt;))</c>。
/// </para>
/// <para>
/// <b>Saga 快照</b>：开放泛型 TState 在编译期未知，<c>[OwnedJson]</c> 无法静态绑定 ——
/// 保留手写 <see cref="JsonSerializer"/>.Serialize(state, <see cref="_jsonTypeInfo"/>) 序列化整 TState 到 <c>saga_data</c> 列。
/// 构造函数注入可选 <see cref="JsonTypeInfo{TState}"/>；为 null 则不持久化完整快照（与 Dapper 实现一致）。
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

    /// <summary>构造 Saga Store。</summary>
    /// <param name="session">Scoped 数据库会话。</param>
    /// <param name="jsonTypeInfo">可选 STJ 源生成 <see cref="JsonTypeInfo{TState}"/>；null 表示不持久化完整 saga_data 快照。</param>
    public PalOrmSagaStateStore(DataSession<TProvider> session, JsonTypeInfo<TState>? jsonTypeInfo = null)
    {
        Session = session;
        _jsonTypeInfo = jsonTypeInfo;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
    {
        // SagaStatus.Active=0 —— 按 Dapper 表契约
        var rows = await Session.QueryAsync<SagaStateRow>(
            $"SELECT * FROM saga_states WHERE status = {(int)SagaStatus.Active} ORDER BY created_at LIMIT {batchSize}",
            ct);
        return rows.Select(Materialize).ToList()!;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner, TimeSpan leaseDuration, int batchSize, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;  // Saga 没有 TimeProvider 注入（与 Dapper 实现一致）
        var until = now + leaseDuration;

        // Saga 租约：UPDATE 子查询（无 RETURNING 路径 —— 与 Dapper 实现一致，不分支）
        await Session.ExecuteAsync(
            $"UPDATE saga_states SET leased_by = {owner}, leased_until = {until} WHERE saga_id IN (SELECT saga_id FROM saga_states WHERE status = {(int)SagaStatus.Active} AND (leased_until IS NULL OR leased_until <= {now}) ORDER BY created_at LIMIT {batchSize})",
            ct);

        var rows = await Session.QueryAsync<SagaStateRow>(
            $"SELECT * FROM saga_states WHERE leased_by = {owner} AND leased_until = {until} ORDER BY created_at",
            ct);
        return rows.Select(Materialize).ToList()!;
    }

    /// <inheritdoc />
    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
    {
        try
        {
            var row = await Session.QueryFirstAsync<SagaStateRow>(
                $"SELECT * FROM saga_states WHERE saga_id = {sagaId.ToString()}",
                ct);
            return Materialize(row);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
    {
        var existing = await GetByIdAsync(state.SagaId, ct);

        // saga_data：STJ 手写序列化（开放泛型 TState，[OwnedJson] 不可用）
        var jsonData = _jsonTypeInfo is null ? null : JsonSerializer.Serialize(state, _jsonTypeInfo);

        if (existing is null)
        {
            // INSERT 路径
            await Session.ExecuteAsync(
                $"INSERT INTO saga_states (saga_id, current_state, status, created_at, completed_at, error, error_at, version, saga_data, leased_by, leased_until) VALUES ({state.SagaId.ToString()}, {state.CurrentState}, {(int)state.Status}, {state.CreatedAt}, {state.CompletedAt}, {state.Error}, {state.ErrorAt}, {state.Version}, {jsonData}, {state.LeasedBy}, {state.LeasedUntil})",
                ct);
            return 1;
        }

        // UPDATE 路径 —— 手写 WHERE version=@expected 乐观锁
        // 不走 UpdateAsync（SagaStateRow 非注册实体）；不依赖 [ConcurrencyCheck]
        var expectedVersion = existing.Version;
        var affected = await Session.ExecuteAsync(
            $"UPDATE saga_states SET current_state = {state.CurrentState}, status = {(int)state.Status}, completed_at = {state.CompletedAt}, version = version + 1, error = {state.Error}, error_at = {state.ErrorAt}, saga_data = {jsonData}, leased_by = {state.LeasedBy}, leased_until = {state.LeasedUntil} WHERE saga_id = {state.SagaId.ToString()} AND version = {expectedVersion}",
            ct);
        if (affected > 0)
        {
            state.Version++;  // 内存对象同步
        }
        return affected;
    }

    /// <summary>SagaStateRow → TState（JSON 反序列化 + 元数据覆盖）。</summary>
    private TState Materialize(SagaStateRow row)
    {
        TState state;
        if (_jsonTypeInfo is not null && !string.IsNullOrEmpty(row.SagaData))
        {
            state = JsonSerializer.Deserialize(row.SagaData!, _jsonTypeInfo!)!;
        }
        else
        {
            state = new TState();
        }

        // 元数据覆盖（即使是反序列化的状态也要覆盖，确保与 DB 一致）
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
}
