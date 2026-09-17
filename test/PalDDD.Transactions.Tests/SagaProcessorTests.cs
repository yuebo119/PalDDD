using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        // 审计 2026-09-17 T-2：原挂钟 400ms 在高载下 flaky——改为 FakeTimeProvider 驱动 tick。
        var store = new CountingSagaStore();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new SagaStubScopeFactory(BuildTimeoutProcessor(store));
        var options = new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            TimeoutScanBatchSize = 64
        });
        var processor = new SagaProcessor<LifecycleSagaState>(
            scopeFactory, options, NullPalLogger<SagaProcessor<LifecycleSagaState>>.Instance, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minCalls: 2, interval: TimeSpan.FromMilliseconds(50));
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.GetActiveCallCount >= 2).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_StoreThrows_DoesNotCrashLoop(CancellationToken cancellationToken)
    {
        var store = new ThrowingSagaStore();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new SagaStubScopeFactory(BuildTimeoutProcessor(store));
        var options = new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            TimeoutScanBatchSize = 64
        });
        var processor = new SagaProcessor<LifecycleSagaState>(
            scopeFactory, options, NullPalLogger<SagaProcessor<LifecycleSagaState>>.Instance, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minCalls: 3, interval: TimeSpan.FromMilliseconds(20));
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.GetActiveCallCount >= 3).IsTrue();
    }

    // 全仓扫描修复：失败回调（OnTickFailed → logger.Error）**自身**抛出时，轮询循环仍不得中断。
    // 基类的 CA1031 抑制理由与 OnTickFailed 的 XML doc 均声明「基类保证循环不中断」，
    // 但修复前该异常会从 catch 块内逃逸把循环打死——与声明相反。与上一测试的区别：
    // 上一测试是 store 抛（回调仅记日志），本测试把回调自身也变成抛异常源。
    [Test]
    public async Task ExecuteAsync_TickFailedCallbackThrows_DoesNotCrashLoop(CancellationToken cancellationToken)
    {
        var store = new ThrowingSagaStore();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new SagaStubScopeFactory(BuildTimeoutProcessor(store));
        var options = new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            TimeoutScanBatchSize = 64
        });
        var processor = new SagaProcessor<LifecycleSagaState>(
            scopeFactory, options, new ThrowingLogger<SagaProcessor<LifecycleSagaState>>(), timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minCalls: 3, interval: TimeSpan.FromMilliseconds(20));
        await processor.StopAsync(cancellationToken);

        // 循环存活 ⇒ 轮询计数继续增长（修复前回调异常逃逸，计数停在第 1 次）
        await Assert.That(store.GetActiveCallCount >= 3).IsTrue();
    }

    /// <summary>错误通道即抛的日志器——让 OnTickFailed 自身成为异常源（见上方测试）</summary>
    private sealed class ThrowingLogger<T> : IPalLogger<T>
    {
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(Exception ex, string message) => throw new InvalidOperationException("logger sink failed");
        public bool IsEnabled(LogLevel level) => true;
    }

    [Test]
    public async Task StopAsync_TerminatesWithinReasonableTime(CancellationToken cancellationToken)
    {
        var store = new CountingSagaStore();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new SagaStubScopeFactory(BuildTimeoutProcessor(store));
        var options = new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            TimeoutScanBatchSize = 64
        });
        var processor = new SagaProcessor<LifecycleSagaState>(
            scopeFactory, options, NullPalLogger<SagaProcessor<LifecycleSagaState>>.Instance, timeProvider: timeProvider);

        await processor.StartAsync(cancellationToken);
        await TickUntilAsync(timeProvider, store, minCalls: 1, interval: TimeSpan.FromMilliseconds(50));

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
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new SagaStubScopeFactory(BuildTimeoutProcessor(store,
            new SagaProcessorOptions { TimeoutScanBatchSize = expectedBatchSize }));
        var options = new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(30),
            TimeoutScanBatchSize = expectedBatchSize
        });
        var processor = new SagaProcessor<LifecycleSagaState>(
            scopeFactory, options, NullPalLogger<SagaProcessor<LifecycleSagaState>>.Instance, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minCalls: 1, interval: TimeSpan.FromMilliseconds(30));
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.LastBatchSize == expectedBatchSize).IsTrue();
    }

    /// <summary>确定性 tick：假时钟驱动间隔，真实等待仅用于调度让步。</summary>
    private static async Task TickUntilAsync(
        FakeTimeProvider timeProvider,
        CountingSagaStoreBase store,
        int minCalls,
        TimeSpan interval,
        int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref store.GetActiveCallCount) < minCalls
               && Environment.TickCount64 < deadline)
        {
            timeProvider.AdvanceNowAndTriggerTimers(interval);
            await Task.Delay(10);
        }
    }

    // 测试 stub

    private abstract class CountingSagaStoreBase : ISagaStateStore<LifecycleSagaState>
    {
        public int GetActiveCallCount;
        public int LastBatchSize;

        public abstract ValueTask<IReadOnlyList<LifecycleSagaState>> GetActiveSagasAsync(int batchSize, CancellationToken ct);

        public virtual ValueTask<IReadOnlyList<LifecycleSagaState>> LeaseActiveSagasAsync(
            string owner,
            TimeSpan leaseDuration,
            int batchSize,
            CancellationToken ct)
            => GetActiveSagasAsync(batchSize, ct);

        public ValueTask<LifecycleSagaState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
            => ValueTask.FromResult<LifecycleSagaState?>(null);

        public ValueTask<int> SaveChangesAsync(LifecycleSagaState state, CancellationToken ct) => new(0);
    }

    /// <summary>计数 Saga store — 返回空列表，记录调用次数与批大小</summary>
    private sealed class CountingSagaStore : CountingSagaStoreBase
    {
        public override ValueTask<IReadOnlyList<LifecycleSagaState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
        {
            Interlocked.Increment(ref GetActiveCallCount);
            LastBatchSize = batchSize;
            return ValueTask.FromResult<IReadOnlyList<LifecycleSagaState>>([]);
        }
    }

    /// <summary>抛异常：Saga store 模拟超时检查失败</summary>
    private sealed class ThrowingSagaStore : CountingSagaStoreBase
    {
        public override ValueTask<IReadOnlyList<LifecycleSagaState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
        {
            Interlocked.Increment(ref GetActiveCallCount);
            throw new InvalidOperationException("store failure");
        }
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

