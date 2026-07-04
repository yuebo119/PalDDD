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

    /// <param name="transaction">可选共享事务（用于 UnitOfWork 模式）</param>
    /// <param name="jsonTypeInfo">可选 STJ source-generated type info；传入后持久化完整 <typeparamref name="TState"/> 快照。</param>
    public DapperSagaStateStore(
        DbConnection connection,
        DbTransaction? transaction = null,
        JsonTypeInfo<TState>? jsonTypeInfo = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction;
        _jsonTypeInfo = jsonTypeInfo;
    }

    public async ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var rows = await _connection.QueryAsync<SagaStateRow>(
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

        var now = TimeProvider.System.GetUtcNow();
        var until = now.Add(leaseDuration);
        await _connection.ExecuteAsync(
            new CommandDefinition(SqlTemplates.SagaLeaseActive, new { owner, until, now, n = batchSize }, _transaction, cancellationToken: ct)).ConfigureAwait(false);

        var rows = await _connection.QueryAsync<SagaStateRow>(
            new CommandDefinition(SqlTemplates.SagaSelectByLease, new { owner, until }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(Materialize).ToList();
    }

    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
    {
        var row = await _connection.QuerySingleOrDefaultAsync<SagaStateRow>(
            new CommandDefinition(SqlTemplates.SagaById, new { id = sagaId }, _transaction, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : Materialize(row);
    }

    /// <summary>UPSERT 持久化 — 存在则更新，不存在则插入</summary>
    public async ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        var existing = await GetByIdAsync(state.SagaId, ct).ConfigureAwait(false);
        var sagaData = SerializeState(state);
        if (existing is not null)
        {
            var rows = await _connection.ExecuteAsync(
                new CommandDefinition(
                    SqlTemplates.SagaUpdate,
                    new
                    {
                        cs = state.CurrentState,
                        st = (int)state.Status,
                        ca = state.CompletedAt,
                        err = state.Error,
                        ea = state.ErrorAt,
                        data = sagaData,
                        leasedBy = state.LeasedBy,
                        leasedUntil = state.LeasedUntil,
                        id = state.SagaId,
                        v = state.Version
                    },
                    _transaction,
                    cancellationToken: ct)).ConfigureAwait(false);

            if (rows > 0) state.Version++;
            return rows;
        }

        var inserted = await _connection.ExecuteAsync(
            new CommandDefinition(
                SqlTemplates.SagaInsert,
                new
                {
                    id = state.SagaId,
                    cs = state.CurrentState,
                    st = (int)state.Status,
                    ca = state.CreatedAt,
                    completedAt = state.CompletedAt,
                    err = state.Error,
                    ea = state.ErrorAt,
                    data = sagaData,
                    leasedBy = state.LeasedBy,
                    leasedUntil = state.LeasedUntil
                },
                _transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        return inserted;
    }

    private string? SerializeState(TState state)
        => _jsonTypeInfo is null ? null : JsonSerializer.Serialize(state, _jsonTypeInfo);

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
