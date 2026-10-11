using PalDDD.Core.Logging;
using PalDDD.Testing;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// ⚡ B-1（EPSL 地毯审计 2026-10-11）：Saga 租约批量释放
// ① 能力语义（InMemory 直测：owner 守卫 / 版本冲突 / 释放换代）
// ② 处理器接线（非超时常态路径收集到环后单语句释放——一次调用覆盖全批，
//    旧处理器零调用本接口，接线断言红证明）
// ③ 回落（存储未实现能力 → 逐条 SaveChangesAsync 路径保持，自定义存储向后兼容）
// ═══════════════════════════════════════════════════════════════

public sealed class SagaLeaseBatchReleaseTests
{
    private sealed class EmptySaga : Saga<LifecycleSagaState>
    { }

    [Test]
    public async Task InMemory_ReleaseLeases_OwnerGuardConflictAndVersionBump()
    {
        var store = new InMemorySagaStateStore<LifecycleSagaState>();
        var saga = new LifecycleSagaState();
        store.Add(saga);

        var leased = await store.LeaseActiveSagasAsync("owner-A", TimeSpan.FromMinutes(2), 10, CancellationToken.None);
        await Assert.That(leased.Count).IsEqualTo(1);
        var successor = leased[0];

        // 他 owner 释放：0 行（leased_by 防抢占守卫）
        var foreign = await store.ReleaseLeasesAsync("owner-B", [successor], CancellationToken.None);
        await Assert.That(foreign).IsEqualTo(0);

        // 本 owner 释放：1 行，租约清空 + 换代（租约 +1、释放 +1 → 共 +2）
        var released = await store.ReleaseLeasesAsync("owner-A", [successor], CancellationToken.None);
        await Assert.That(released).IsEqualTo(1);
        var after = await store.GetByIdAsync(successor.SagaId, CancellationToken.None);
        await Assert.That(after!.LeasedBy).IsNull();
        await Assert.That(after.LeasedUntil).IsNull();
        await Assert.That(after.Version).IsEqualTo(saga.Version + 2);

        // 旧快照重复释放：0 行（版本已换代，fencing 拦截）
        var stale = await store.ReleaseLeasesAsync("owner-A", [successor], CancellationToken.None);
        await Assert.That(stale).IsEqualTo(0);
    }

    /// <summary>能力接口间谍存储：委托 InMemory，计数批量/逐条调用。</summary>
    private sealed class BatchSpyStore : ISagaLeaseBatchStore<LifecycleSagaState>
    {
        private readonly InMemorySagaStateStore<LifecycleSagaState> _inner = new();

        public int ReleaseCalls { get; private set; }
        public int PerSagaSaveCalls { get; private set; }
        public IReadOnlyList<LifecycleSagaState>? LastBatch { get; private set; }

        public void Add(LifecycleSagaState state) => _inner.Add(state);

        public ValueTask<IReadOnlyList<LifecycleSagaState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
            => _inner.GetActiveSagasAsync(batchSize, ct);

        public ValueTask<IReadOnlyList<LifecycleSagaState>> LeaseActiveSagasAsync(
            string owner, TimeSpan leaseDuration, int batchSize, CancellationToken ct)
            => _inner.LeaseActiveSagasAsync(owner, leaseDuration, batchSize, ct);

        public ValueTask<LifecycleSagaState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
            => _inner.GetByIdAsync(sagaId, ct);

        public ValueTask<int> SaveChangesAsync(LifecycleSagaState state, CancellationToken ct)
        {
            PerSagaSaveCalls++;
            return _inner.SaveChangesAsync(state, ct);
        }

        public ValueTask<int> ReleaseLeasesAsync(string owner, IReadOnlyList<LifecycleSagaState> sagas, CancellationToken ct)
        {
            ReleaseCalls++;
            LastBatch = sagas;
            return _inner.ReleaseLeasesAsync(owner, sagas, ct);
        }
    }

    [Test]
    public async Task Processor_NonTimeoutSagas_ReleasedViaSingleBatchCall()
    {
        var store = new BatchSpyStore();
        var ids = new List<PalUlid>();
        for (var i = 0; i < 3; i++)
        {
            var saga = new LifecycleSagaState();
            store.Add(saga);
            ids.Add(saga.SagaId);
        }

        var processor = new SagaTimeoutProcessor<LifecycleSagaState>(
            store,
            new EmptySaga(),
            NullPalLogger<SagaTimeoutProcessor<LifecycleSagaState>>.Instance,
            new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions { TimeoutScanBatchSize = 64 }),
            TimeProvider.System);

        await processor.CheckTimeoutsAsync(CancellationToken.None);

        // 接线断言（旧处理器红：不收集不调用能力接口 → ReleaseCalls == 0）
        await Assert.That(store.ReleaseCalls).IsEqualTo(1);
        await Assert.That(store.LastBatch!.Count).IsEqualTo(3);
        await Assert.That(store.PerSagaSaveCalls).IsEqualTo(0);

        // 释放后果：三条 saga 租约全部清空
        foreach (var id in ids)
        {
            var state = await store.GetByIdAsync(id, CancellationToken.None);
            await Assert.That(state!.LeasedBy).IsNull();
        }
    }

    /// <summary>未实现能力的旧式存储：回落逐条路径。</summary>
    private sealed class LegacyStore : ISagaStateStore<LifecycleSagaState>
    {
        private readonly InMemorySagaStateStore<LifecycleSagaState> _inner = new();

        public int SaveCalls { get; private set; }

        public void Add(LifecycleSagaState state) => _inner.Add(state);

        public ValueTask<IReadOnlyList<LifecycleSagaState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
            => _inner.GetActiveSagasAsync(batchSize, ct);

        public ValueTask<IReadOnlyList<LifecycleSagaState>> LeaseActiveSagasAsync(
            string owner, TimeSpan leaseDuration, int batchSize, CancellationToken ct)
            => _inner.LeaseActiveSagasAsync(owner, leaseDuration, batchSize, ct);

        public ValueTask<LifecycleSagaState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
            => _inner.GetByIdAsync(sagaId, ct);

        public ValueTask<int> SaveChangesAsync(LifecycleSagaState state, CancellationToken ct)
        {
            SaveCalls++;
            return _inner.SaveChangesAsync(state, ct);
        }
    }

    [Test]
    public async Task Processor_LegacyStore_FallsBackToPerSagaRelease()
    {
        var store = new LegacyStore();
        for (var i = 0; i < 2; i++)
            store.Add(new LifecycleSagaState());

        var processor = new SagaTimeoutProcessor<LifecycleSagaState>(
            store,
            new EmptySaga(),
            NullPalLogger<SagaTimeoutProcessor<LifecycleSagaState>>.Instance,
            new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions { TimeoutScanBatchSize = 64 }),
            TimeProvider.System);

        await processor.CheckTimeoutsAsync(CancellationToken.None);

        // 回落路径：逐条保存（每 saga 一次），租约同样清空
        await Assert.That(store.SaveCalls).IsEqualTo(2);
        var actives = await store.GetActiveSagasAsync(10, CancellationToken.None);
        foreach (var saga in actives)
            await Assert.That(saga.LeasedBy).IsNull();
    }
}
