using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.Messaging;
using PalDDD.Serialization;
using PalDDD.Testing;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 📤 OutboxProcessor 后台服务生命周期测试
// ═══════════════════════════════════════════════════════════════
// OutboxBatchProcessor 已有完整批处理单元测试，本文件只覆盖循环层：
// 1. 启动后按 PollInterval 轮询
// 2. 批处理异常不崩溃循环（CA1031 隔离）
// 3. 停止令牌优雅终止
// 4. 空队列不空转（依赖 PeriodicTimer 间隔）
// ═══════════════════════════════════════════════════════════════

public sealed class OutboxProcessorTests
{
    [Test]
    public async Task ExecuteAsync_PollsAtConfiguredInterval(CancellationToken cancellationToken)
    {
        var store = new CountingOutboxStore();
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 10,
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await processor.StartAsync(cts.Token);
        await Task.Delay(250, cancellationToken);
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.LeaseCallCount >= 3).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_BatchThrows_DoesNotCrashLoop(CancellationToken cancellationToken)
    {
        var store = new ThrowingOutboxStore();
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await processor.StartAsync(cts.Token);
        await Task.Delay(150, cancellationToken);
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.LeaseCallCount >= 3).IsTrue();
    }

    [Test]
    public async Task StopAsync_TerminatesWithinReasonableTime(CancellationToken cancellationToken)
    {
        var store = new CountingOutboxStore();
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance);

        await processor.StartAsync(cancellationToken);
        await Task.Delay(100, cancellationToken);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stopTask = processor.StopAsync(stopCts.Token);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
        await Assert.That(completed).IsSameReferenceAs(stopTask);
    }

    [Test]
    public async Task ExecuteAsync_EmptyQueue_StillPollsOnSchedule(CancellationToken cancellationToken)
    {
        var store = new CountingOutboxStore();
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await processor.StartAsync(cts.Token);
        await Task.Delay(200, cancellationToken);
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.LeaseCallCount >= 3).IsTrue();
        await Assert.That(store.MarkProcessedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ExecuteAsync_CancellationDuringTick_ContinuesWhenStopTokenIsNotCancelled(CancellationToken cancellationToken)
    {
        // 💡 下游 lease 抛 OperationCanceledException(非 stoppingToken) 时，
        //   ｜ PeriodicBackgroundProcessor 视为「下游取消但 Host 未关停」——静默忽略，不记 error。
        //   ｜ 因此应断言循环继续轮询（LeaseCallCount >= 2），且 ErrorCount == 0。
        var store = new CancellingOutboxStore();
        var logger = new CapturingLogger<OutboxProcessor>();
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, logger);

        await processor.StartAsync(cancellationToken);
        await Task.Delay(100, cancellationToken);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await processor.StopAsync(stopCts.Token);

        await Assert.That(store.LeaseCallCount >= 2).IsTrue();
        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    // ─── 辅助：构造 OutboxBatchProcessor（绕过 DI 反射构造）────────

    private static OutboxBatchProcessor BuildBatchProcessor(IPalOutboxStore store)
        => new(store,
            new NullMessageBroker(),
            new StubSerializer(),
            MessageCatalog.Empty,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions { MaxRetryCount = 3 }),
            NullPalLogger<OutboxBatchProcessor>.Instance);

    /// <summary>自定义 IServiceScopeFactory — 返回固定 OutboxBatchProcessor 实例</summary>
    private sealed class StubScopeFactory(OutboxBatchProcessor processor) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new StubScope(processor);
    }

    private sealed class StubScope(OutboxBatchProcessor processor) : IServiceScope
    {
        public IServiceProvider ServiceProvider => new StubServiceProvider(processor);

        public void Dispose()
        { }
    }

    private sealed class StubServiceProvider(OutboxBatchProcessor processor) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(OutboxBatchProcessor) ? processor : null;
    }

    // ─── 测试 stub ──────────────────────────────────────────────

    private sealed class CountingOutboxStore : IPalOutboxStore
    {
        public int LeaseCallCount;
        public int MarkProcessedCount;

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            Interlocked.Increment(ref LeaseCallCount);
            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);
        }

        public void AddMessage(OutboxMessage message)
        { }

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages) => new(0);

        public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
            => Interlocked.Increment(ref MarkProcessedCount);

        public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
        { }

        public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
        { }

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => new(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => new(0);
    }

    private sealed class ThrowingOutboxStore : IPalOutboxStore
    {
        public int LeaseCallCount;

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => throw new InvalidOperationException("store failure");

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            Interlocked.Increment(ref LeaseCallCount);
            throw new InvalidOperationException("lease failure");
        }

        public void AddMessage(OutboxMessage message)
        { }

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages) => new(0);

        public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
        { }

        public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
        { }

        public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
        { }

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => new(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => new(0);
    }

    private sealed class CancellingOutboxStore : IPalOutboxStore
    {
        public int LeaseCallCount;

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            Interlocked.Increment(ref LeaseCallCount);
            throw new OperationCanceledException(ct);
        }

        public void AddMessage(OutboxMessage message)
        { }

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages) => new(0);

        public void MarkProcessed(OutboxMessage message, DateTimeOffset processedAt)
        { }

        public void MarkDead(OutboxMessage message, string failureReason, DateTimeOffset deadAt)
        { }

        public void ReleaseForRetry(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt)
        { }

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => new(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => new(0);
    }

    private sealed class StubSerializer : IMessageSerializer
    {
        public string ContentType => ContentTypes.Json;

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null) => default;

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor) => default;

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => null;

        public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }

    private sealed class CapturingLogger<T> : IPalLogger<T>
    {
        public int ErrorCount;

        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(Exception ex, string message) => Interlocked.Increment(ref ErrorCount);
        public bool IsEnabled(LogLevel level) => true;
    }
}
