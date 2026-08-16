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
    private readonly TimeProvider _timeProvider;

    /// <summary>构造内存存储（P3 修复：可选 clock 注入，默认 System——测试可冻结时间）。</summary>
    public InMemorySagaStateStore(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

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

        var now = _timeProvider.GetUtcNow();
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
    /// <remarks>
    /// P3 修复（十七轮）：返回值对齐 <see cref="ISagaStateStore{TState}.SaveChangesAsync"/>
    /// 契约——已跟踪（<see cref="Add"/> 或租约后存在于内部字典）返回 1，未跟踪返回 0。
    /// 原恒返回 0 使调用方的"0 行 = 乐观锁冲突"告警路径（SagaProcessor）在内存模式下
    /// 每次保存都误触发。
    /// </remarks>
    public ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            return ValueTask.FromResult(_states.ContainsKey(state.SagaId) ? 1 : 0);
        }
    }
}
