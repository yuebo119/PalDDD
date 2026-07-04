using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 内存 Saga 状态存储 — 测试和单进程原型用
// ─────────────────────────────────────────────────────────────

/// <summary>内存 Saga 状态存储 — 用于测试和单进程原型。</summary>
public sealed class InMemorySagaStateStore<TState> : ISagaStateStore<TState>
    where TState : SagaState
{
    private readonly Lock _lock = new();
    private readonly Dictionary<PalUlid, TState> _states = [];

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        lock (_lock)
        {
            var active = _states.Values
                .Where(static s => s.Status == SagaStatus.Active)
                .OrderBy(s => s.CreatedAt)
                .Take(batchSize)
                .ToList();
            return ValueTask.FromResult<IReadOnlyList<TState>>(active);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var now = TimeProvider.System.GetUtcNow();
        var leasedUntil = now.Add(leaseDuration);
        lock (_lock)
        {
            var active = _states.Values
                .Where(s => s.Status == SagaStatus.Active
                    && (s.LeasedUntil is null || s.LeasedUntil <= now))
                .OrderBy(s => s.CreatedAt)
                .Take(batchSize)
                .ToList();

            foreach (var state in active)
            {
                state.LeasedBy = owner;
                state.LeasedUntil = leasedUntil;
            }

            return ValueTask.FromResult<IReadOnlyList<TState>>(active);
        }
    }

    /// <inheritdoc/>
    public ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
    {
        lock (_lock)
        {
            _states.TryGetValue(sagaId, out var state);
            return ValueTask.FromResult(state);
        }
    }

    /// <summary>将 Saga 状态添加到存储中（用于测试设置）。</summary>
    public void Add(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock) { _states[state.SagaId] = state; }
    }

    /// <inheritdoc/>
    /// <remarks>内存模式 — 状态修改直接作用于引用，此方法为 no-op。</remarks>
    public ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
        => ValueTask.FromResult(0);
}
