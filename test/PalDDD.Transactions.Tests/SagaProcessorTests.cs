using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core.Logging;
using PalDDD.Testing;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺?
// 鈴?SagaProcessor<TState> 鍚庡彴鏈嶅姟鐢熷懡鍛ㄦ湡娴嬭瘯
// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺?
// SagaTimeoutProcessor 宸叉湁瓒呮椂妫€娴嬪崟鍏冩祴璇曪紝鏈枃浠跺彧瑕嗙洊寰幆灞傦細
// 1. 鍚姩鍚庢寜 PollInterval 杞
// 2. 瓒呮椂妫€鏌ュ紓甯镐笉宕╂簝寰幆锛圕A1031 闅旂锛?
// 3. 鍋滄浠ょ墝浼橀泤缁堟
// 4. 鎵瑰ぇ灏忛厤缃紶閫掑埌 store
// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺?

/// <summary>SagaProcessor 娴嬭瘯鐢ㄧ姸鎬?/summary>
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
        await Task.Delay(250, cancellationToken);
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.GetActiveCallCount >= 3).IsTrue();
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

    // 鈹€鈹€鈹€ 娴嬭瘯 stub 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    /// <summary>璁℃暟 Saga store 鈥?杩斿洖绌哄垪琛紝璁板綍璋冪敤娆℃暟涓庢壒澶у皬</summary>
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

    /// <summary>鎶涘紓甯?Saga store 鈥?妯℃嫙瓒呮椂妫€鏌ュけ璐?/summary>
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

    /// <summary>鑷畾涔?IServiceScopeFactory 鈥?杩斿洖鍥哄畾 SagaTimeoutProcessor 瀹炰緥</summary>
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

    /// <summary>鏃犳搷浣?Saga 鈥?涓嶆敞鍐屼换浣曠姸鎬佽浆鎹紝IsTimedOut 姘歌繙杩斿洖 false</summary>
    private sealed class NoOpSaga : Saga<LifecycleSagaState>
    { }
}
