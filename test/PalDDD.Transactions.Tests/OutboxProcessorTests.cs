using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
// 1. 启动后按 PollInterval 轮询（FakeTimeProvider 驱动，确定性）
// 2. 批处理异常不崩溃循环（CA1031 隔离）
// 3. 停止令牌优雅终止
// 4. 空队列不空转（依赖轮询间隔）
// ═══════════════════════════════════════════════════════════════

public sealed class OutboxProcessorTests
{
    [Test]
    public async Task ExecuteAsync_PollsAtConfiguredInterval(CancellationToken cancellationToken)
    {
        var store = new CountingOutboxStore();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 10,
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minLeases: 2, interval: TimeSpan.FromMilliseconds(50));
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.LeaseCallCount >= 2).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_BatchThrows_DoesNotCrashLoop(CancellationToken cancellationToken)
    {
        var store = new ThrowingOutboxStore();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minLeases: 3, interval: TimeSpan.FromMilliseconds(20));
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.LeaseCallCount >= 3).IsTrue();
    }

    [Test]
    public async Task StopAsync_TerminatesWithinReasonableTime(CancellationToken cancellationToken)
    {
        var store = new CountingOutboxStore();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance, timeProvider: timeProvider);

        await processor.StartAsync(cancellationToken);
        await TickUntilAsync(timeProvider, store, minLeases: 1, interval: TimeSpan.FromMilliseconds(50));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stopTask = processor.StopAsync(stopCts.Token);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
        await Assert.That(completed).IsSameReferenceAs(stopTask);
    }

    [Test]
    public async Task ExecuteAsync_EmptyQueue_StillPollsOnSchedule(CancellationToken cancellationToken)
    {
        var store = new CountingOutboxStore();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minLeases: 2, interval: TimeSpan.FromMilliseconds(40));
        await processor.StopAsync(cancellationToken);

        await Assert.That(store.LeaseCallCount >= 2).IsTrue();
        await Assert.That(store.MarkProcessedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ExecuteAsync_CancellationDuringTick_ContinuesWhenStopTokenIsNotCancelled(CancellationToken cancellationToken)
    {
        // 💡 下游 lease 抛 OperationCanceledException(非 stoppingToken) 时，
        //   ｜ 轮询循环视为「下游取消但 Host 未关停」——静默忽略，不记 error。
        var store = new CancellingOutboxStore();
        var logger = new CapturingLogger<OutboxProcessor>();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, logger, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minLeases: 2, interval: TimeSpan.FromMilliseconds(20));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await processor.StopAsync(stopCts.Token);

        await Assert.That(store.LeaseCallCount >= 2).IsTrue();
        await Assert.That(logger.ErrorCount).IsEqualTo(0);
    }

    // ─── T1.2 自适应排空（2026-10-11 优化实施轮）─────────────────
    // 锁定测试三件：满批立即续批（不等整间隔）/ 未满批维持整间隔 / PollInterval 热读生效。
    // 改前红证明：旧循环恒按 PollInterval 延迟，1ms 推进不可能触发续批。

    [Test]
    public async Task ExecuteAsync_FullBatch_ContinuesWithoutWaitingInterval(CancellationToken cancellationToken)
    {
        // 满批条数对齐 BuildBatchProcessor 默认 BatchSize=100（积压信号按该值判定）
        var store = new FixedCountBatchStore(returnCount: 100);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 10,
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        // 首轮按整间隔触发（循环是 delay-then-tick 形态）；再静默一轮让在途 tick 落定
        await TickUntilAsync(timeProvider, store, minLeases: 1, interval: TimeSpan.FromMilliseconds(50));
        timeProvider.AdvanceNowAndTriggerTimers(TimeSpan.FromMilliseconds(50));
        await Task.Delay(100);
        var baseline = Volatile.Read(ref store.LeaseCallCount);
        // 排空判别（强式）：假时钟总推进量限制在 10ms（远小于 50ms 间隔）内必须完成 2 次续批
        // ——旧形态（恒 50ms 延迟）在 10ms 推进下定时器不可能到期，本断言结构性红
        for (var i = 0; i < 10 && Volatile.Read(ref store.LeaseCallCount) < baseline + 2; i++)
        {
            timeProvider.AdvanceNowAndTriggerTimers(TimeSpan.FromMilliseconds(1));
            await Task.Delay(20);
        }
        await processor.StopAsync(cancellationToken);

        await Assert.That(Volatile.Read(ref store.LeaseCallCount) >= baseline + 2).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_PartialBatch_WaitsFullInterval(CancellationToken cancellationToken)
    {
        // 99/100 未满批（对齐 BuildBatchProcessor 默认 BatchSize=100）
        var store = new FixedCountBatchStore(returnCount: 99);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 10,
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minLeases: 1, interval: TimeSpan.FromMilliseconds(50));
        // 静默期：整间隔推进一次 + 真实让步，让 TickUntil 期间多推进排队的在途 tick 全部落定
        timeProvider.AdvanceNowAndTriggerTimers(TimeSpan.FromMilliseconds(50));
        await Task.Delay(100);
        var baseline = Volatile.Read(ref store.LeaseCallCount);
        // 未满批（9/10）→ 无排空信号：1ms 推进 × 6（共 6ms，远小于 50ms）不得产生新租约；
        // 容忍至多 1 个静默期在途残留 tick（TickUntil 多推进的尾巴），排空模式下 6ms 内
        // 连发 ≥2 只可能来自 50ms 定时器被 1ms 推进穿透——那是被测行为面之外
        for (var i = 0; i < 6; i++)
        {
            timeProvider.AdvanceNowAndTriggerTimers(TimeSpan.FromMilliseconds(1));
            await Task.Delay(10);
        }
        await Assert.That(Volatile.Read(ref store.LeaseCallCount)).IsLessThan(baseline + 2);
        // 整间隔推进 → 恢复轮询
        await TickUntilAsync(timeProvider, store, minLeases: baseline + 1, interval: TimeSpan.FromMilliseconds(50));
        await processor.StopAsync(cancellationToken);

        await Assert.That(Volatile.Read(ref store.LeaseCallCount) >= baseline + 1).IsTrue();
    }

    [Test]
    public async Task PollInterval_HotUpdate_TakesEffectWithoutRestart(CancellationToken cancellationToken)
    {
        var store = new FixedCountBatchStore(returnCount: 0);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-17T12:00:00+00:00"));
        var scopeFactory = new StubScopeFactory(BuildBatchProcessor(store));
        var options = new MutableOptionsMonitor(new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            BatchSize = 10,
            MaxRetryCount = 3
        });
        var processor = new OutboxProcessor(scopeFactory, options, NullPalLogger<OutboxProcessor>.Instance, timeProvider: timeProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await processor.StartAsync(cts.Token);
        await TickUntilAsync(timeProvider, store, minLeases: 1, interval: TimeSpan.FromMilliseconds(20));
        // 切换到 200ms：先按旧间隔推进两次冲刷既有 pending timer，再断言旧间隔不再触发
        for (var i = 0; i < 2; i++)
        {
            timeProvider.AdvanceNowAndTriggerTimers(TimeSpan.FromMilliseconds(20));
            await Task.Delay(10);
        }
        options.CurrentValue = new OutboxOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(200),
            BatchSize = 10,
            MaxRetryCount = 3
        };
        var baseline = Volatile.Read(ref store.LeaseCallCount);
        for (var i = 0; i < 4; i++)
        {
            timeProvider.AdvanceNowAndTriggerTimers(TimeSpan.FromMilliseconds(20));
            await Task.Delay(10);
        }
        // 旧形态（构造期定格）在此必然继续以 20ms 节奏触发（4 次推进 ≈ +4 tick）——
        // 本断言即红证明锚点；热读形态下 80ms < 200ms 至多 1 个在途残留
        await Assert.That(Volatile.Read(ref store.LeaseCallCount)).IsLessThan(baseline + 2);
        await TickUntilAsync(timeProvider, store, minLeases: baseline + 1, interval: TimeSpan.FromMilliseconds(200));
        await processor.StopAsync(cancellationToken);

        await Assert.That(Volatile.Read(ref store.LeaseCallCount) >= baseline + 1).IsTrue();
    }

    /// <summary>
    /// 确定性 tick：反复 AdvanceNowAndTriggerTimers + 短 yield，直到租约次数达标。
    /// 真实等待仅用于调度让步，不承担轮询间隔语义（间隔由假时钟驱动）。
    /// </summary>
    private static async Task TickUntilAsync(
        FakeTimeProvider timeProvider,
        CountingOutboxStoreBase store,
        int minLeases,
        TimeSpan interval,
        int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref store.LeaseCallCount) < minLeases
               && Environment.TickCount64 < deadline)
        {
            timeProvider.AdvanceNowAndTriggerTimers(interval);
            // T-21 定标（2026-09-22）：这里的 10ms **不是轮询等待，是调度让出**——推进 fake 时间
            // 只触发 timer 回调的**调度**，处理器侧的异步工作（Lease→计数）仍需真实线程被调度
            // 才能推进。故不可改成 `Task.Yield()`：Yield 只让出续体、不保证后台线程获得时间片，
            // 会把"等真实调度"变成"空转"，反而**引入** flaky（原"去墙钟"清单把它列为待改项
            // 属前提误判——该清单里 2 处是 raw string 坏样本文本、7 处是 broker 就绪等待）。
            // 若未来要消除它，须改的是处理器的调度模型（如改为同步驱动），不是替换这一行。
            await Task.Delay(10);
        }
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

    private abstract class CountingOutboxStoreBase : IPalOutboxStore
    {
        public int LeaseCallCount;
        public int MarkProcessedCount;

        public abstract ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct);

        public abstract ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct);

        public ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default) => new(0);

        public ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref MarkProcessedCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => new(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => new(0);
    }

    private sealed class CountingOutboxStore : CountingOutboxStoreBase
    {
        public override ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public override ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            Interlocked.Increment(ref LeaseCallCount);
            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);
        }
    }

    private sealed class ThrowingOutboxStore : CountingOutboxStoreBase
    {
        public override ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => throw new InvalidOperationException("store failure");

        public override ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            Interlocked.Increment(ref LeaseCallCount);
            throw new InvalidOperationException("lease failure");
        }
    }

    /// <summary>T1.2：每次租约恒返回固定条数（returnCount 等于 BatchSize 即满批形态）。</summary>
    private sealed class FixedCountBatchStore(int returnCount) : CountingOutboxStoreBase
    {
        public override ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public override ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            Interlocked.Increment(ref LeaseCallCount);
            var messages = new OutboxMessage[returnCount];
            for (var i = 0; i < returnCount; i++)
                messages[i] = new OutboxMessage();
            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(messages);
        }
    }

    /// <summary>T1.2：可变 CurrentValue 的 IOptionsMonitor 桩（热读测试）。</summary>
    private sealed class MutableOptionsMonitor(OutboxOptions initial) : IOptionsMonitor<OutboxOptions>
    {
        public OutboxOptions CurrentValue { get; set; } = initial;

        public OutboxOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<OutboxOptions, string?> listener) => null;
    }

    private sealed class CancellingOutboxStore : CountingOutboxStoreBase
    {
        public override ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public override ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            Interlocked.Increment(ref LeaseCallCount);
            throw new OperationCanceledException(ct);
        }
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
