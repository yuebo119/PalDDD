using System.Text.Json.Serialization;
using PalDDD.Core.Logging;
using PalDDD.Messaging;
using PalDDD.Serialization;
using PalDDD.Testing;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 🚇 T3.1 发布确认流水线窗口锁定测试（2026-10-11 优化实施轮）
// ═══════════════════════════════════════════════════════════════
// 三件锁定（窗口 >1 才激活；默认 1 的串行行为由既有 OutboxProcessorTests 覆盖）：
// 1. 满批窗口处理：全部消息 processed 且标记顺序 == 租约顺序（FIFO 出窗保序）
// 2. 窗口上限实测：in-flight 并发峰值 ≤ 窗口且 > 1（流水线激活的直接证明）
// 3. 窗口下单条发布失败：失败消息走 ReleaseForRetry、其余照常 processed（失败语义镜像串行）
// ═══════════════════════════════════════════════════════════════

public sealed class OutboxPublishPipeliningTests
{
    [Test]
    public async Task Pipelined_FullBatch_AllProcessedInStartOrder()
    {
        var (store, broker) = await RunBatchAsync(window: 4, messageCount: 8);

        await Assert.That(store.ProcessedOrder.Count).IsEqualTo(8);
        await Assert.That(store.LeaseOrder.SequenceEqual(store.ProcessedOrder)).IsTrue();
        await Assert.That(store.ReleaseForRetryOrder.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Pipelined_InFlightPeak_BoundedByWindow_AndGreaterThanOne()
    {
        var (_, broker) = await RunBatchAsync(window: 4, messageCount: 12);

        await Assert.That(broker.MaxObservedInFlight).IsGreaterThan(1);
        await Assert.That(broker.MaxObservedInFlight).IsLessThanOrEqualTo(4);
    }

    [Test]
    public async Task Pipelined_SinglePublishFailure_RetriedOthersProcessed()
    {
        var (store, _) = await RunBatchAsync(window: 4, messageCount: 8, failAtIndex: 3);

        await Assert.That(store.ProcessedOrder.Count).IsEqualTo(7);
        await Assert.That(store.ReleaseForRetryOrder.Count).IsEqualTo(1);
        await Assert.That(store.ReleaseForRetryOrder[0]).IsEqualTo(store.LeaseOrder[3]);
        await Assert.That(store.ProcessedOrder.Contains(store.LeaseOrder[3])).IsFalse();
    }

    private static async Task<(RecordingOutboxStore Store, PipeliningDelayBroker Broker)> RunBatchAsync(
        int window, int messageCount, int failAtIndex = -1)
    {
        var builder = new MessageCatalogBuilder();
        builder.Add<PipelinedMessage>(PipeliningJsonContext.Default.PipelinedMessage, "test.pipelined.v1");
        var catalog = builder.Build();
        var serializer = new NonNullSerializer();
        var store = new RecordingOutboxStore(messageCount);
        var broker = new PipeliningDelayBroker(TimeSpan.FromMilliseconds(30), failAtIndex, serializer, catalog);
        var processor = new OutboxBatchProcessor(
            store, broker, serializer, catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
            {
                BatchSize = messageCount,
                MaxInFlightPublishes = window,
                MaxRetryCount = 3,
            }),
            NullPalLogger<OutboxBatchProcessor>.Instance);

        await processor.ProcessBatchAsync(CancellationToken.None);
        return (store, broker);
    }

    // ─── stubs ─────────────────────────────────────────────────
    // PipelinedMessage/PipeliningJsonContext 在文件末命名空间层级声明（SYSLIB1032 嵌套链规避）

    /// <summary>固定延迟发布 + 并发峰值追踪；failAtIndex 的发布按启动序计数命中即抛。</summary>
    private sealed class PipeliningDelayBroker(
        TimeSpan latency, int failAtIndex, IMessageSerializer serializer, IMessageCatalog catalog)
        : MessageBrokerBase(serializer, catalog)
    {
        private int _inFlight;
        private int _started;

        public int MaxObservedInFlight;

        public override async ValueTask PublishAsync(
            object message, MessageDescriptor msgDescriptor, PalUlid messageId,
            MessagePublishContext context, CancellationToken ct = default)
        {
            var entered = Interlocked.Increment(ref _inFlight);
            // 峰值更新（非原子读改写——观测桩，低估方向安全：只可能少计不多计）
            if (entered > Volatile.Read(ref MaxObservedInFlight))
                Volatile.Write(ref MaxObservedInFlight, entered);
            try
            {
                if (failAtIndex >= 0 && Interlocked.Increment(ref _started) - 1 == failAtIndex)
                    throw new InvalidOperationException("broker publish failure (test)");
                if (latency > TimeSpan.Zero)
                    await Task.Delay(latency, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("流水线测试不订阅");

        public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("流水线测试不订阅");
    }

    /// <summary>反序列化恒返回非 null（全路径激活——目录查找/发布/标记链不走死信捷径）。</summary>
    private sealed class NonNullSerializer : IMessageSerializer
    {
        public string ContentType => ContentTypes.Json;

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null) => default;

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor) => default;

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => new object();

        public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }

    /// <summary>租约固定批 + 终态标记顺序记录（ProcessedOrder / ReleaseForRetryOrder）。</summary>
    private sealed class RecordingOutboxStore(int messageCount) : IPalOutboxStore
    {
        public readonly List<PalUlid> LeaseOrder = [];
        public readonly List<PalUlid> ProcessedOrder = [];
        public readonly List<PalUlid> ReleaseForRetryOrder = [];

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            var messages = new OutboxMessage[messageCount];
            for (var i = 0; i < messageCount; i++)
            {
                messages[i] = new OutboxMessage
                {
                    Type = "test.pipelined.v1",
                    SchemaVersion = 1,
                    Payload = "{}"u8.ToArray(),
                    ContentType = "application/json",
                    Status = OutboxStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                LeaseOrder.Add(messages[i].Id);
            }
            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(messages);
        }

        public ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default) => new(messages.Count);

        public ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default)
        {
            lock (ProcessedOrder) ProcessedOrder.Add(message.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default)
        {
            lock (ReleaseForRetryOrder) ReleaseForRetryOrder.Add(message.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => new(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => new(messageCount);
    }
}

// SYSLIB1032：嵌套 JsonSerializerContext 需 partial 包含链——命名空间层级作载体（QpsJsonContext 同款）
internal sealed record PipelinedMessage(PalUlid OrderId);

[JsonSerializable(typeof(PipelinedMessage))]
internal sealed partial class PipeliningJsonContext : JsonSerializerContext;
