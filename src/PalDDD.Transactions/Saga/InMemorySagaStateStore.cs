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
        // ITM-215 修复（三十二轮）：ct 对齐——对照 InMemoryOutbox/InMemoryInbox 均已 ThrowIfCancellationRequested，
        // 本类 4 个异步方法此前全部忽略 ct（同步完成但契约要求响应取消）
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        lock (_lock)
        {
            // 三十七轮 A3：对齐 LeaseActiveSagasAsync（:60）与 EFCore 姊妹（SagaStateDbContext）——
            // 观测查询与 Lease 同步纳入 AwaitingHumanDecision（HITL 中断态超时兜底扫描依赖此查询）
            var active = _states.Values
                .Where(static s => s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision)
                .OrderBy(s => s.CreatedAt)
                .Take(batchSize)
                .ToList();
            return ValueTask.FromResult<IReadOnlyList<TState>>(active);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(  // PERF-008:负值由Options校验(ITM-166)
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        // ITM-215 修复（三十二轮）：ct 对齐（见 GetActiveSagasAsync）
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var leasedUntil = now.Add(leaseDuration);
        lock (_lock)
        {
            // 三十四轮（中断态超时兜底）：扫描集扩 AwaitingHumanDecision——中断态 Saga
            // 配置了步骤 Timeout 且超期时由 SagaTimeoutProcessor.IsTimedOut 门控补偿；
            // 未配置 Timeout 则 IsTimedOut 恒 false，仅经历租约获取/释放（显式无限等待契约）
            var active = _states.Values
                .Where(s => (s.Status == SagaStatus.Active || s.Status == SagaStatus.AwaitingHumanDecision)
                    && (s.LeasedUntil is null || s.LeasedUntil <= now))
                .OrderBy(s => s.CreatedAt)
                .Take(batchSize)
                .ToList();

            // v25 P3 行为族 B1：successor 替换——对齐 InMemoryOutboxStore 的 ITM-174 模式。
            // 原实现原地直写字典条目（调用方与存储共享同一引用）：worker A 租约到期后
            // worker B 重租同一实例，A 的 SaveChangesAsync 无任何校验即可覆盖 B 的活跃
            // 租约（僵尸写回），且"0 行 = 乐观锁冲突"契约路径恒不可达。TState 为抽象用户
            // 子类无法 new——经 CloneForLease（MemberwiseClone，非反射）产生后继实例替换
            // 字典条目；Version 递增作为 fencing 代（重租即换代，旧持有者 Version 必然
            // 落后，其 SaveChangesAsync 见下方不匹配返回 0）。
            // ⚠️ v26 P3：CloneForLease 为浅拷贝——新旧实例共享 StepStartedAt/ExecutedStepKeys
            // 容器，并发写安全未保障（僵尸与新持有者并发写集合可抛）；Version fencing
            // 不受影响（标量隔离）。深隔离需后续破坏性变更，详见 SagaState.CloneForLease remarks。
            var leased = new List<TState>(active.Count);
            foreach (var state in active)
            {
                var successor = (TState)state.CloneForLease();
                successor.LeasedBy = owner;
                successor.LeasedUntil = leasedUntil;
                successor.Version = state.Version + 1;
                _states[state.SagaId] = successor;
                leased.Add(successor);
            }

            return ValueTask.FromResult<IReadOnlyList<TState>>(leased);
        }
    }

    /// <inheritdoc/>
    public ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
    {
        // ITM-215 修复（三十二轮）：ct 对齐（见 GetActiveSagasAsync）
        ct.ThrowIfCancellationRequested();
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
    /// 契约——未跟踪（不在内部字典）返回 0，已跟踪返回 1。
    /// 原恒返回 0 使调用方的"0 行 = 乐观锁冲突"告警路径（SagaProcessor）在内存模式下
    /// 每次保存都误触发。
    /// v25 P3 行为族 B1：Version 乐观锁——字典条目与传入 state 的 <see cref="SagaState.Version"/>
    /// 不匹配（租约被 successor 抢占，或已被其他持有者保存递增）时返回 0，调用方的冲突
    /// 告警路径可达；匹配则递增 Version 并以传入实例为最新字典条目，返回 1。
    /// </remarks>
    public ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        // ITM-215 修复（三十二轮）：ct 对齐（见 GetActiveSagasAsync）
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            // v25 P3 行为族 B1：0 行路径三态——未跟踪 / Version 落后（已被 successor
            // 替换或他实例保存）均返回 0；匹配时 Version++（fencing 代推进）
            if (!_states.TryGetValue(state.SagaId, out var current))
                return ValueTask.FromResult(0);
            if (current.Version != state.Version)
                return ValueTask.FromResult(0);
            state.Version++;
            _states[state.SagaId] = state;
            return ValueTask.FromResult(1);
        }
    }
}
