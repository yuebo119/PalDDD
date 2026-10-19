using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 内存 Saga 状态存储 — 测试和单进程原型用
// ─────────────────────────────────────────────────────────────

/// <summary>内存 Saga 状态存储 — 用于测试和单进程原型。</summary>
public sealed class InMemorySagaStateStore<TState> : ISagaStateStore<TState>, ISagaLeaseBatchStore<TState>
    where TState : SagaState
{
    private readonly Lock _lock = new();
    private readonly Dictionary<PalUlid, TState> _states = [];
    // EPSL 地毯审计 2026-10-16 B 批 #23：活跃索引导——GetActive/Lease 的扫描源。
    // 原形态每 tick 全量扫 _states.Values（含终态），扫描成本随历史终态数无界增长
    // （长跑原型真实劣化）。终态条目仍保留在 _states（GetByIdAsync 公开返回终态是
    // 恢复链路/断言依赖的行为契约，清除会改可观测行为——审计记录的用户决策点），
    // 扫描改走索引后 O(活跃)。成员不变量在 Add/SaveChangesAsync 两个受理点按落库
    // 状态自纠（Active/AwaitingHumanDecision 在索引，其余不在），不依赖调用方守约。
    private readonly HashSet<PalUlid> _activeIds = [];
    private readonly TimeProvider _timeProvider;

    /// <summary>构造内存存储（P3 修复：可选 clock 注入，默认 System——测试可冻结时间）。</summary>
    public InMemorySagaStateStore(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>状态是否参与活跃扫描（与 GetActive/Lease 的谓词一致：Active + AWD）。</summary>
    private static bool IsScannable(TState state)
        => state.Status == SagaStatus.Active || state.Status == SagaStatus.AwaitingHumanDecision;

    /// <summary>按落库状态自纠索引成员（lock 内调用）。</summary>
    private void SyncActiveIndex(TState state)
    {
        if (IsScannable(state))
            _activeIds.Add(state.SagaId);
        else
            _activeIds.Remove(state.SagaId);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// v65 P3（活引用声明）：本方法返回字典内的<b>活引用</b>（与 <see cref="GetByIdAsync"/>
    /// 一致），非快照克隆——调用方<b>不得变异</b>返回对象的属性：内存栈不校验 Version，
    /// 直接改 Status/Error 等会绕开乐观锁污染存储态。姊妹 <see cref="LeaseActiveSagasAsync"/>
    /// 因 fencing 需要经 CloneForLease 产生 successor 实例（Version 换代），与观测查询语义不同。
    /// 持久化栈（EFCore/Dapper/PalORM）各查询天然物化新实例，无此共享契约。
    /// </remarks>
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
            // B23：扫描走活跃索引（O(活跃)）——语义与原全量扫 + 状态谓词等价
            var active = new List<TState>(Math.Min(batchSize, _activeIds.Count));
            foreach (var id in _activeIds)
            {
                if (_states.TryGetValue(id, out var state))
                    active.Add(state);
            }
            active.Sort(static (a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
            if (active.Count > batchSize)
                active.RemoveRange(batchSize, active.Count - batchSize);
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

        // v36 P3：now 在 lock 外取值——与姊妹 InMemoryOutboxStore.LeasePendingMessagesAsync
        //（lock 内取）不一致；fencing 正确性不受影响（仅 leasedUntil 时间戳微早），
        // 保留现状并声明（对齐 InMemoryOutboxStore.RequeueDeadAsync 的 lock 外声明先例）
        var now = _timeProvider.GetUtcNow();
        var leasedUntil = now.Add(leaseDuration);
        lock (_lock)
        {
            // B23：扫描走活跃索引（O(活跃)）——语义与原全量扫 + 状态谓词等价
            // （索引成员即谓词为真的集合，Add/SaveChanges 受理点自纠维持）。
            // v44 P3 饿死边界（行为不变，声明保留）：CreatedAt 固定排序 + Take(batchSize) 下，
            // 无 Timeout 的滞留 AWD saga 永不离场（非超时分支仅清租约）——积压 ≥ batchSize
            // 时排在其后的 saga 每轮取不到名额；生产栈 NextAttemptAt 过滤不占名额无此问题，
            // 本栈定位测试/原型，接受该边界不修。
            var active = new List<TState>(Math.Min(batchSize, _activeIds.Count));
            foreach (var id in _activeIds)
            {
                if (!_states.TryGetValue(id, out var state))
                    continue; // 索引残留防御（正常不可达——移除与落库同帧发生）
                if (state.LeasedUntil is not null && state.LeasedUntil > now)
                    continue;
                active.Add(state);
            }
            active.Sort(static (a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
            if (active.Count > batchSize)
                active.RemoveRange(batchSize, active.Count - batchSize); // 等价原 OrderBy().Take(batchSize) 语义

            // v25 P3 行为族 B1：successor 替换——对齐 InMemoryOutboxStore 的 ITM-174 模式。
            // 原实现原地直写字典条目（调用方与存储共享同一引用）：worker A 租约到期后
            // worker B 重租同一实例，A 的 SaveChangesAsync 无任何校验即可覆盖 B 的活跃
            // 租约（僵尸写回），且"0 行 = 乐观锁冲突"契约路径恒不可达。TState 为抽象用户
            // 子类无法 new——经 CloneForLease（MemberwiseClone，非反射）产生后继实例替换
            // 字典条目；Version 递增作为 fencing 代（重租即换代，旧持有者 Version 必然
            // 落后，其 SaveChangesAsync 见下方不匹配返回 0）。
            // ⚠️ v26 P3：CloneForLease 为浅拷贝——新旧实例共享 StepStartedAt/ExecutedStepKeys
            // 容器，并发写安全未保障（僵尸与新持有者并发写集合可抛）；Version fencing
            // 不受影响（标量隔离）。深隔离需后续破坏性变更，详见 SagaState.CloneForLease remarks
            //（含 2026-09-19 M3-3 回滚勘正：僵尸执行过的步骤必须能被后继者补偿）。
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
        lock (_lock)
        {
            _states[state.SagaId] = state;
            SyncActiveIndex(state); // B23：初始状态入索引（终态添加不参与扫描，与原谓词一致）
        }
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
            SyncActiveIndex(state); // B23：落库状态转移是索引成员唯一权威时点（如 Completed 出索引）
            return ValueTask.FromResult(1);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// EPSL 地毯审计 2026-10-11 B-1：镜像 <see cref="SaveChangesAsync"/> 的 Version fencing
    /// 形态——字典当前条目与传入快照版本一致且仍归本 owner 持有（未被 successor 抢占、
    /// 未被保存换代）才释放；释放即一次写入 = Version 换代（对齐持久化栈 version+1）。
    /// 变异字典条目（current）而非传入快照——两者在扫描路径同引用，直调路径以字典为准。
    /// </remarks>
    public ValueTask<int> ReleaseLeasesAsync(string owner, IReadOnlyList<TState> sagas, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(sagas);
        ct.ThrowIfCancellationRequested();

        var released = 0;
        lock (_lock)
        {
            foreach (var saga in sagas)
            {
                if (_states.TryGetValue(saga.SagaId, out var current)
                    && current.Version == saga.Version
                    && string.Equals(current.LeasedBy, owner, StringComparison.Ordinal))
                {
                    current.LeasedBy = null;
                    current.LeasedUntil = null;
                    current.Version++;
                    released++;
                }
            }
        }
        return ValueTask.FromResult(released);
    }
}
