using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalDDD.Core.Logging;
using PalDDD.Messaging;
using PalDDD.Serialization;
using PalDDD.Transactions;
using System.Diagnostics;

namespace PalDDD.Benchmarks;

/// <summary>
/// 出站唤醒信号量具（2026-10-22 性能轮）：回答「写入到被标记发布成功的延迟是多少」。
/// </summary>
/// <remarks>
/// <para>
/// <b>口径</b>：InMemory store + <see cref="NullMessageBroker"/> + 真实
/// <see cref="OutboxProcessor"/>/<see cref="OutboxBatchProcessor"/> 全链路，PollInterval 设为
/// 5 分钟（远大于采样窗口）——只有唤醒信号能触发 tick，因此测得的是信号路径本身的延迟，
/// 不含数据库往返与 broker 网络（那些属部署量纲，用 <c>--delivery-pg</c>/<c>--delivery-mysql</c> 车道）。
/// </para>
/// <para>
/// <b>不硬断言毫秒阈值</b>（计时只作趋势，EPSL 纪律）；只断言三条结构性事实：
/// 每条消息都被标记、消息被标记前曾发生租约、100 条突发写入不会被拆成 100 次 tick。
/// </para>
/// <para>
/// ⚠️ 信号用本文件内的等价实现（容量 1 的 <see cref="SemaphoreSlim"/>）：真实
/// <c>OutboxWakeUpSignal</c> 是 internal，基准工程无 IVT；两者是同一形态的原语，
/// 差异只在类型名（<c>SemaphoreSlim(0,1)</c> + 合并 Release）。
/// </para>
/// </remarks>
internal static class OutboxWakeUpProbe
{
    private const int LatencySamples = 500;
    private const int BurstMessages = 100;

    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        var catalog = new MessageCatalogBuilder()
            .Add(MessageDescriptor.Create(BenchJsonContext.Default.BenchOrderPlaced, name: "bench.wakeup"))
            .Build();
        var descriptor = catalog.Find("bench.wakeup")
            ?? throw new InvalidOperationException("wakeup probe: descriptor missing");

        var signal = new ProbeSignal();
        // 真实 InMemory store 注入信号：唤醒由 store 的写入路径触发（与生产同形）。
        var inner = new InMemoryOutboxStore(wakeUpSignal: signal);
        var store = new TimedStore(inner);
        var options = new FixedOptions(new OutboxOptions
        {
            // 5 分钟轮询：只有信号能唤醒。若信号失效，采样会在 1 秒超时处失败。
            PollInterval = TimeSpan.FromMinutes(5),
            BatchSize = 100,
            MaxRetryCount = 3
        });
        var batchProcessor = new OutboxBatchProcessor(
            store, new NullMessageBroker(), new StubSerializer(), catalog, options,
            NullPalLogger<OutboxBatchProcessor>.Instance);
        var processor = new OutboxProcessor(
            new StubScopeFactory(batchProcessor), options, NullPalLogger<OutboxProcessor>.Instance,
            timeProvider: TimeProvider.System, wakeUpSignal: signal);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await processor.StartAsync(cts.Token);
        try
        {
            // 让循环停在「等信号」点（StartAsync 返回时已停在首个 await）。
            await Task.Delay(50);

            var latencies = new double[LatencySamples];
            for (var i = 0; i < LatencySamples; i++)
            {
                var message = new OutboxMessage
                {
                    Type = descriptor.Name,
                    SchemaVersion = descriptor.SchemaVersion,
                    ContentType = ContentTypes.Json,
                    Payload = [1, 2, 3]
                };
                var before = Volatile.Read(ref store.MarkedCount);
                var start = Stopwatch.GetTimestamp();
                await inner.AddMessageAsync(message);
                await SpinUntilAsync(() => Volatile.Read(ref store.MarkedCount) > before, TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
                latencies[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            Array.Sort(latencies);
            var mean = latencies.Average();
            var p50 = Percentile(latencies, 0.50);
            var p95 = Percentile(latencies, 0.95);
            var p99 = Percentile(latencies, 0.99);

            // 突发写入：验证信号与自适应排空不会把一批写入拆成逐条 tick。
            var leaseBefore = Volatile.Read(ref store.LeaseCalls);
            var markedBefore = Volatile.Read(ref store.MarkedCount);
            for (var i = 0; i < BurstMessages; i++)
            {
                await inner.AddMessageAsync(new OutboxMessage
                {
                    Type = descriptor.Name,
                    SchemaVersion = descriptor.SchemaVersion,
                    ContentType = ContentTypes.Json,
                    Payload = [1, 2, 3]
                });
            }
            await SpinUntilAsync(
                () => Volatile.Read(ref store.MarkedCount) - markedBefore >= BurstMessages,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var burstTicks = Volatile.Read(ref store.LeaseCalls) - leaseBefore;

            Console.WriteLine(
                $"WAKEUP sample={LatencySamples} min={latencies[0]:F3}ms p50={p50:F3}ms p95={p95:F3}ms p99={p99:F3}ms mean={mean:F3}ms");
            Console.WriteLine(
                $"WAKEUP burst={BurstMessages} ticks={burstTicks} (逐条重构形态下应为 {BurstMessages})");

            // 结构性断言（不计时）：全部消息被标记、突发合并可见。
            if (Volatile.Read(ref store.MarkedCount) < LatencySamples + BurstMessages)
                throw new InvalidOperationException("wakeup probe: not all messages were marked");
            if (burstTicks >= BurstMessages)
                throw new InvalidOperationException(
                    $"wakeup probe: burst was split into {burstTicks} ticks (batching regressed)");

            Console.WriteLine("WAKEUP PASS");
            return 0;
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await processor.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static double Percentile(double[] sorted, double q)
        => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];

    private static async Task SpinUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var spinner = new SpinWait();
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException("wakeup probe: condition not reached within timeout");
            if (spinner.NextSpinWillYield)
                await Task.Yield();
            else
                spinner.SpinOnce();
        }
    }

    /// <summary>与 <c>OutboxWakeUpSignal</c> 等价的信号（容量 1，Notify 合并）。</summary>
    private sealed class ProbeSignal : IOutboxWakeUpSignal
    {
        private readonly SemaphoreSlim _signal = new(0, 1);

        public void Notify()
        {
            if (_signal.CurrentCount != 0)
                return;
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
        }

        public ValueTask<bool> WaitAsync(TimeSpan maxWait, CancellationToken ct)
            => new(_signal.WaitAsync(maxWait, ct));
    }

    /// <summary>记录租约与标记次数的 InMemory 装饰器（探针观测面）。</summary>
    private sealed class TimedStore(InMemoryOutboxStore inner)
        : IPalOutboxStore, IImmediateWriteOutboxStore, IBatchMarkOutboxStore
    {
        public int LeaseCalls;
        public int MarkedCount;

        public bool RequiresSaveAfterMark => false;

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => inner.GetPendingMessagesAsync(batchSize, maxRetryCount, ct);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
            int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            Interlocked.Increment(ref LeaseCalls);
            return inner.LeasePendingMessagesAsync(batchSize, owner, leaseDuration, maxRetryCount, ct);
        }

        public ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default)
            => inner.AddMessageAsync(message, ct);

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default)
            => inner.AddMessagesAsync(messages, ct);

        public async ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default)
        {
            await inner.MarkProcessedAsync(message, processedAt, ct).ConfigureAwait(false);
            Interlocked.Increment(ref MarkedCount);
        }

        public async ValueTask<int> MarkProcessedBatchAsync(
            IReadOnlyList<OutboxMessage> messages, DateTimeOffset processedAt, CancellationToken ct = default)
        {
            var affected = await inner.MarkProcessedBatchAsync(messages, processedAt, ct).ConfigureAwait(false);
            Interlocked.Add(ref MarkedCount, affected);
            return affected;
        }

        public ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default)
            => inner.MarkDeadAsync(message, failureReason, deadAt, ct);

        public ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default)
            => inner.ReleaseForRetryAsync(message, failureReason, nextAttemptAt, ct);

        public ValueTask<int> RequeueDeadAsync(ByteAether.Ulid.Ulid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
            => inner.RequeueDeadAsync(messageId, nextAttemptAt, retriedBy, ct);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => inner.SaveChangesAsync(ct);
    }

    private sealed class StubSerializer : IMessageSerializer
    {
        public string ContentType => ContentTypes.Json;

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null) => default;

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor) => default;

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => null;

        public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }

    private sealed class FixedOptions(OutboxOptions value) : IOptionsMonitor<OutboxOptions>
    {
        public OutboxOptions CurrentValue { get; } = value;

        public OutboxOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<OutboxOptions, string?> listener) => null;
    }

    private sealed class StubScopeFactory(OutboxBatchProcessor processor) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(processor);

        private sealed class Scope(OutboxBatchProcessor processor) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(processor);

            public void Dispose() { }

            private sealed class Provider(OutboxBatchProcessor processor) : IServiceProvider
            {
                public object? GetService(Type serviceType)
                    => serviceType == typeof(OutboxBatchProcessor) ? processor : null;
            }
        }
    }
}
