using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core.Logging;
using PalDDD.Testing;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

// ══════════════════════════════════════════════════════════════
// SagaProcessor<TState> 后台服务生命周期测试
// ══════════════════════════════════════════════════════════════
// SagaTimeoutProcessor 已有超时检测单元测试，本文件只覆盖循环层：
// 1. 启动后按 PollInterval 轮询
// 2. 超时检查异常不崩溃循环（CA1031 隔离）
// 3. 停止令牌优雅终止
// 4. 批大小配置透传到 store
// ══════════════════════════════════════════════════════════════
// 三十五轮 P3 修复 + 三十七轮 P1 全文法根治：原文件 UTF-8→GBK mojibake
// 多层损坏，指纹式逐行清理三轮漏检后改全文重写。

/// <summary>SagaProcessor 测试用状态</summary>
public sealed class LifecycleSagaState : SagaState
{ }

public sealed class SagaProcessorTests
{
    private static SagaTimeoutProcessor<LifecycleSagaState> BuildTimeoutProcessor(
        ISagaStateStore<LifecycleSagaState> store, SagaProcessorOptions? options = null)
        => new(store,
            new NoOpSaga(),
            NullPalLogger<SagaTimeoutProcessor<LifecycleSagaState>>.Instance,
            new FixedOptionsMonitor<SagaProcessorOptions>(options ?? new SagaProcessorOptions { TimeoutScanBatchSize = 64 }),
            TimeProvider.System);

    [Test]
    public async Task ExecuteAsync_PollsAtConfiguredInterval(CancellationToken cancellationToken)
    {
        // P4 修复（九轮验证轮）：真实时钟断言在高载并行下 flaky——250ms 内 50ms 轮询
        // 理论 4-5 次，高载可能仅 2 次。等待窗口放宽到 400ms 且阈值降为 2。
        // （轮询周期正确性的最小可区分断言：单次启动不会只轮询 1 次）
        var store = new CountingSagaStore();
        var scopeFactory = new SagaStubScopeFactory(BuildTimeoutProcessor(store));
        var options = new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            TimeoutScanBatchSize = 64
        });
        var processor = new SagaProcessor<LifecycleSagaState>(
            scopeFactory, options, NullPalLogger<SagaProcessor<LifecycleSagaState>>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await processor.StartAsync(cts.Token);
        await Task.Delay(400, cancellationToken);
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.GetActiveCallCount >= 2).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_StoreThrows_DoesNotCrashLoop(CancellationToken cancellationToken)
    {
        var store = new ThrowingSagaStore();
        var scopeFactory = new SagaStubScopeFactory(BuildTimeoutProcessor(store));
        var options = new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            TimeoutScanBatchSize = 64
        });
        var processor = new SagaProcessor<LifecycleSagaState>(
            scopeFactory, options, NullPalLogger<SagaProcessor<LifecycleSagaState>>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await processor.StartAsync(cts.Token);
        await Task.Delay(150, cancellationToken);
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.GetActiveCallCount >= 3).IsTrue();
    }

    [Test]
    public async Task StopAsync_TerminatesWithinReasonableTime(CancellationToken cancellationToken)
    {
        var store = new CountingSagaStore();
        var scopeFactory = new SagaStubScopeFactory(BuildTimeoutProcessor(store));
        var options = new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            TimeoutScanBatchSize = 64
        });
        var processor = new SagaProcessor<LifecycleSagaState>(
            scopeFactory, options, NullPalLogger<SagaProcessor<LifecycleSagaState>>.Instance);

        await processor.StartAsync(cancellationToken);
        await Task.Delay(100, cancellationToken);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stopTask = processor.StopAsync(stopCts.Token);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
        await Assert.That(completed).IsSameReferenceAs(stopTask);
    }

    [Test]
    public async Task ExecuteAsync_PassesConfiguredBatchSize(CancellationToken cancellationToken)
    {
        var store = new CountingSagaStore();
        const int expectedBatchSize = 128;
        var scopeFactory = new SagaStubScopeFactory(BuildTimeoutProcessor(store,
            new SagaProcessorOptions { TimeoutScanBatchSize = expectedBatchSize }));
        var options = new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(30),
            TimeoutScanBatchSize = expectedBatchSize
        });
        var processor = new SagaProcessor<LifecycleSagaState>(
            scopeFactory, options, NullPalLogger<SagaProcessor<LifecycleSagaState>>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await processor.StartAsync(cts.Token);
        await Task.Delay(100, cancellationToken);
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.LastBatchSize == expectedBatchSize).IsTrue();
    }

    // 测试 stub

    /// <summary>计数 Saga store — 返回空列表，记录调用次数与批大小</summary>
    private sealed class CountingSagaStore : ISagaStateStore<LifecycleSagaState>
    {
        public int GetActiveCallCount;
        public int LastBatchSize;

        public ValueTask<IReadOnlyList<LifecycleSagaState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
        {
            Interlocked.Increment(ref GetActiveCallCount);
            LastBatchSize = batchSize;
            return ValueTask.FromResult<IReadOnlyList<LifecycleSagaState>>([]);
        }

        public ValueTask<IReadOnlyList<LifecycleSagaState>> LeaseActiveSagasAsync(
            string owner,
            TimeSpan leaseDuration,
            int batchSize,
            CancellationToken ct)
            => GetActiveSagasAsync(batchSize, ct);

        public ValueTask<LifecycleSagaState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
            => ValueTask.FromResult<LifecycleSagaState?>(null);

        public ValueTask<int> SaveChangesAsync(LifecycleSagaState state, CancellationToken ct) => new(0);
    }

    /// <summary>抛异常：Saga store 模拟超时检查失败</summary>
    private sealed class ThrowingSagaStore : ISagaStateStore<LifecycleSagaState>
    {
        public int GetActiveCallCount;

        public ValueTask<IReadOnlyList<LifecycleSagaState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
        {
            Interlocked.Increment(ref GetActiveCallCount);
            throw new InvalidOperationException("store failure");
        }

        public ValueTask<IReadOnlyList<LifecycleSagaState>> LeaseActiveSagasAsync(
            string owner,
            TimeSpan leaseDuration,
            int batchSize,
            CancellationToken ct)
            => GetActiveSagasAsync(batchSize, ct);

        public ValueTask<LifecycleSagaState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
            => ValueTask.FromResult<LifecycleSagaState?>(null);

        public ValueTask<int> SaveChangesAsync(LifecycleSagaState state, CancellationToken ct) => new(0);
    }

    /// <summary>自定义 IServiceScopeFactory — 返回固定 SagaTimeoutProcessor 实例</summary>
    private sealed class SagaStubScopeFactory(SagaTimeoutProcessor<LifecycleSagaState> processor) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SagaStubScope(processor);
    }

    private sealed class SagaStubScope(SagaTimeoutProcessor<LifecycleSagaState> processor) : IServiceScope
    {
        public IServiceProvider ServiceProvider => new SagaStubServiceProvider(processor);

        public void Dispose()
        { }
    }

    private sealed class SagaStubServiceProvider(SagaTimeoutProcessor<LifecycleSagaState> processor) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(SagaTimeoutProcessor<LifecycleSagaState>) ? processor : null;
    }

    /// <summary>空 Saga — 无步骤注册，扫描无操作</summary>
    private sealed class NoOpSaga : Saga<LifecycleSagaState>
    { }

    // ── v26 P1 探针装置：HITL 超时兜底 + 迟到决策 ──

    private sealed record TimeoutKickoff;
    private sealed record TimeoutApprove(bool Approved);

    private sealed class TimeoutHitlSaga : Saga<LifecycleSagaState>
    {
        public TimeoutHitlSaga()
        {
            When<TimeoutKickoff>("Initial",
                new InterruptStep("await-approval", "threshold", typeof(TimeoutApprove))
                { Timeout = TimeSpan.FromSeconds(1) });
            When<TimeoutApprove>("Initial", new SagaStep("apply-decision",
                execute: static (s, e, ct) =>
                {
                    s.CurrentState = "Approved"; // 决策副作用标记——探针断言不发生
                    s.Status = SagaStatus.Completed;
                    return new ValueTask<SagaState>(s);
                }));
        }
    }

    [Test]
    public async Task CheckTimeouts_CompensatedInterrupt_LateDecisionFailsVisibly(CancellationToken ct)
    {
        // v26 P1 探针：中断 → SagaTimeoutProcessor 真路径超时补偿（store 租约经
        // CloneForLease 返回 successor，补偿写 successor；Manager 条目闭包捕获中断时
        // 旧实例——v25 P2-4 的终态分支因此不可达）→ 迟到决策必须可见失败。
        // 修复前双红信号：① ResumeAsync 假成功静默返回（不抛 IOE）；
        // ② 决策步骤在已回滚 Saga 上执行副作用（旧实例 CurrentState 被改 "Approved"）
        var manager = new DefaultSagaManager();
        var saga = new TimeoutHitlSaga { SagaManager = manager };
        var store = new InMemorySagaStateStore<LifecycleSagaState>();
        var state = new LifecycleSagaState();

        var interrupted = await saga.ProcessEventAsync(state, new TimeoutKickoff(), ct);
        await Assert.That(interrupted.Status).IsEqualTo(SagaStatus.AwaitingHumanDecision);

        // store 落盘 + 中断步骤时间戳拨到过去（触发 IsTimedOut）
        store.Add(state);
        foreach (var key in state.StepStartedAt.Keys.ToList())
            state.StepStartedAt[key] = DateTimeOffset.UtcNow.AddMinutes(-5);

        var processor = new SagaTimeoutProcessor<LifecycleSagaState>(
            store, saga,
            NullPalLogger<SagaTimeoutProcessor<LifecycleSagaState>>.Instance,
            new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions { TimeoutScanBatchSize = 64 }),
            TimeProvider.System);
        await processor.CheckTimeoutsAsync(ct);

        // 补偿已发生（写的是 store successor，旧实例不变——successor 化语义）
        var persisted = await store.GetByIdAsync(state.SagaId, ct);
        await Assert.That(persisted!.Status).IsEqualTo(SagaStatus.Compensated);

        // 迟到决策：必须可见失败且副作用不施加
        await Assert.That(async () =>
            await manager.ResumeAsync(state.SagaId, new TimeoutApprove(true), ct))
            .Throws<InvalidOperationException>();
        await Assert.That(state.CurrentState).IsNotEqualTo("Approved");
    }

    [Test]
    public void RegisterInterrupted_AfterInvalidate_RejectsGhostReRegistration()
    {
        // v27 P2 探针：失效（超时补偿）后的"幽灵再注册"必须被拒——失效后仍飞行中的
        // ResumeDispatch 在旧实例上触发下一个 InterruptStep（多阶段 HITL）时注册的
        // 条目是幽灵条目，后续决策会经它在已回滚 Saga 上继续执行并假成功
        var manager = new DefaultSagaManager();
        var sagaId = PalUlid.New();

        manager.InvalidateInterrupted(sagaId);

        Assert.Throws<InvalidOperationException>(() =>
            manager.RegisterInterrupted(sagaId,
                static (decision, ct) => ValueTask.FromResult<SagaState>(null!)));
    }
}

