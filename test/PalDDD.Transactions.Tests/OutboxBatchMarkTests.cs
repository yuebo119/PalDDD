using System.Text.Json.Serialization;
using PalDDD.Core.Logging;
using PalDDD.Messaging;
using PalDDD.Serialization;
using PalDDD.Testing;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// 🧾 R4/P0-2（ADR-027，2026-10-14 性能实施轮）终态批量标记锁定测试
// ═══════════════════════════════════════════════════════════════
// 两组锁定：
// A. 处理器集成（stub store 双接口）——默认 1 逐条不变 / 阈值 >1 批量激活与批序=租约序 /
//    批末冲刷部分累积 / 发布失败逐条重试其余批量 / 流水线路径同款。
// B. InMemory 真实现——全批标记+终态清除 / 陈旧引用（successor 替换后）fencing 拒绝 /
//    null 元素守卫。
// Dapper/EF 栈的 SQL 正确性（行值守卫、混批防御）由
// PalDDD.Integration.Tests.OutboxBatchMarkSqliteTests（SQLite 真库，本地可跑）承载。
// ═══════════════════════════════════════════════════════════════

public sealed class OutboxBatchMarkProcessorTests
{
    [Test]
    public async Task BatchMark_SerialMode_PerMessagePathUnchanged()
    {
        var (store, _) = await RunBatchAsync(threshold: 1, messageCount: 5);

        await Assert.That(store.PerMessageProcessed.Count).IsEqualTo(5);
        await Assert.That(store.BatchCallSizes.Count).IsEqualTo(0);
        await Assert.That(store.ProcessedOrder.Count).IsEqualTo(5);
        await Assert.That(store.LeaseOrder.SequenceEqual(store.ProcessedOrder)).IsTrue();
    }

    [Test]
    public async Task BatchMark_AutoMode_EnablesBatchWhenStoreSupportsIt()
    {
        var (store, _) = await RunBatchAsync(
            threshold: 1, messageCount: 5, deliveryMode: OutboxDeliveryMode.Auto);

        // Auto：store 实现 IBatchMarkOutboxStore → 阈值抬到 16，批末一次冲刷
        await Assert.That(store.BatchCallSizes.Count).IsEqualTo(1);
        await Assert.That(store.BatchCallSizes[0]).IsEqualTo(5);
        await Assert.That(store.PerMessageProcessed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BatchMark_DelayedWriteStore_FallsBackToPerMessagePersist()
    {
        // R14：store 实现 IBatchMarkOutboxStore 但 RequiresSaveAfterMark=true（延迟写）——
        // 批量路径全程不调 PersistSingle，终态永不落库，故必须回落逐条标记 + 逐条持久化。
        var (store, _) = await RunBatchAsync(
            threshold: 1, messageCount: 5, deliveryMode: OutboxDeliveryMode.Auto,
            requiresSaveAfterMark: true);

        await Assert.That(store.BatchCallSizes.Count).IsEqualTo(0);
        await Assert.That(store.PerMessageProcessed.Count).IsEqualTo(5);
    }

    [Test]
    public async Task BatchMark_TimeBoundFlush_BeforeBatchEnd()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var (store, _) = await RunBatchAsync(
            threshold: 100,
            messageCount: 5,
            deliveryMode: OutboxDeliveryMode.Auto,
            timeProvider: fakeTime,
            onPublish: () => fakeTime.Advance(TimeSpan.FromMilliseconds(600)));

        // 每次发布推进 600ms，1s 时间界在第二条后触发；第二条与批末各一次冲刷。
        await Assert.That(store.BatchCallSizes.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(store.BatchCallSizes.Sum()).IsEqualTo(5);
        await Assert.That(store.PerMessageProcessed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BatchMark_ThresholdFlushes_BatchesInLeaseOrder()
    {
        var (store, _) = await RunBatchAsync(threshold: 4, messageCount: 8);

        // 两批（4+4）——批量激活且批序 == 租约序（保序声明，T3.1 同族）
        await Assert.That(store.BatchCallSizes.Count).IsEqualTo(2);
        await Assert.That(store.BatchCallSizes[0]).IsEqualTo(4);
        await Assert.That(store.BatchCallSizes[1]).IsEqualTo(4);
        await Assert.That(store.PerMessageProcessed.Count).IsEqualTo(0);
        await Assert.That(store.LeaseOrder.SequenceEqual(store.ProcessedOrder)).IsTrue();
        await Assert.That(store.ReleaseForRetryOrder.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BatchMark_PartialAccumulation_FlushedAtBatchEnd()
    {
        var (store, _) = await RunBatchAsync(threshold: 10, messageCount: 5);

        // 未满阈值的残留在批末冲刷（finally 语义）
        await Assert.That(store.BatchCallSizes.Count).IsEqualTo(1);
        await Assert.That(store.BatchCallSizes[0]).IsEqualTo(5);
        await Assert.That(store.PerMessageProcessed.Count).IsEqualTo(0);
        await Assert.That(store.LeaseOrder.SequenceEqual(store.ProcessedOrder)).IsTrue();
    }

    [Test]
    public async Task BatchMark_PublishFailure_FailedRetriedOthersBatched()
    {
        var (store, _) = await RunBatchAsync(threshold: 4, messageCount: 8, failAtIndex: 3);

        // 失败消息走逐条 ReleaseForRetry；其余 7 条批量标记（4+3：满批冲刷 + 批末冲刷）
        await Assert.That(store.ReleaseForRetryOrder.Count).IsEqualTo(1);
        await Assert.That(store.ReleaseForRetryOrder[0]).IsEqualTo(store.LeaseOrder[3]);
        await Assert.That(store.ProcessedOrder.Count).IsEqualTo(7);
        await Assert.That(store.ProcessedOrder.Contains(store.LeaseOrder[3])).IsFalse();
        await Assert.That(store.PerMessageProcessed.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BatchMark_PipelinedPath_BatchMarksInStartOrder()
    {
        var (store, _) = await RunBatchAsync(
            threshold: 3, messageCount: 10, window: 4, deliveryMode: OutboxDeliveryMode.Auto);

        await Assert.That(store.BatchCallSizes.Sum()).IsEqualTo(10);
        await Assert.That(store.PerMessageProcessed.Count).IsEqualTo(0);
        await Assert.That(store.LeaseOrder.SequenceEqual(store.ProcessedOrder)).IsTrue();
    }

    [Test]
    public async Task RawPublish_SkipsDeserializeAndReserialize()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add<BatchMarkMessage>(BatchMarkJsonContext.Default.BatchMarkMessage, "test.batchmark.v1");
        var catalog = builder.Build();
        var serializer = new ThrowingDeserializeSerializer();
        var store = new RecordingBatchStore(5);
        var broker = new RawBroker(serializer, catalog);
        var processor = new OutboxBatchProcessor(
            store, broker, serializer, catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
            {
                BatchSize = 5,
                DeliveryMode = OutboxDeliveryMode.Serial,
                MaxRetryCount = 3,
            }),
            NullPalLogger<OutboxBatchProcessor>.Instance);

        await processor.ProcessBatchAsync(CancellationToken.None);

        await Assert.That(broker.RawCount).IsEqualTo(5);
        await Assert.That(store.ProcessedOrder.Count).IsEqualTo(5);
    }

    [Test]
    public async Task BatchMark_PartialFencingRejection_EmitsWarning()
    {
        // R13 F16：store 按契约返回 affected < 批大小（部分 fencing 拒绝）时，处理器
        // 丢弃返回值——未命中行仍 Pending 并持过期租约，到期重租重发（重复投递），
        // 全程零日志零指标。修复后至少一条带 EventId 的 Warning。
        var builder = new MessageCatalogBuilder();
        builder.Add<BatchMarkMessage>(BatchMarkJsonContext.Default.BatchMarkMessage, "test.batchmark.v1");
        var catalog = builder.Build();
        var serializer = new NonNullSerializer();
        var store = new RecordingBatchStore(4, partialBatchReject: true);
        var logger = new CaptureLogger();
        var processor = new OutboxBatchProcessor(
            store, new NullBroker(-1, serializer, catalog), serializer, catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
            {
                BatchSize = 4,
                BatchMarkThreshold = 2,
                BatchMarkFlushInterval = TimeSpan.FromSeconds(1),
                DeliveryMode = OutboxDeliveryMode.Serial,
                MaxRetryCount = 3,
            }),
            logger);

        await processor.ProcessBatchAsync(CancellationToken.None);

        await Assert.That(logger.Rows.Any(r =>
            r.EventId == PalLogEventIds.OutboxBatchMarkFailed
            && r.Message.Contains("partially", StringComparison.Ordinal))).IsTrue();
    }

    private sealed class CaptureLogger : IPalLogger<OutboxBatchProcessor>
    {
        public List<(int EventId, string Message)> Rows { get; } = [];

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;

        public void Debug(string m) => Rows.Add((0, m));

        public void Information(string m) => Rows.Add((0, m));

        public void Warning(string m) => Rows.Add((0, m));

        public void Error(Exception e, string m) => Rows.Add((0, m));

        public void Debug(Microsoft.Extensions.Logging.EventId id, string m, params object?[] a) => Rows.Add((id.Id, m));

        public void Information(Microsoft.Extensions.Logging.EventId id, string m, params object?[] a) => Rows.Add((id.Id, m));

        public void Warning(Microsoft.Extensions.Logging.EventId id, string m, params object?[] a) => Rows.Add((id.Id, m));

        public void Error(Exception e, Microsoft.Extensions.Logging.EventId id, string m, params object?[] a) => Rows.Add((id.Id, m));
    }

    private static async Task<(RecordingBatchStore Store, NullBroker Broker)> RunBatchAsync(
        int threshold, int messageCount, int failAtIndex = -1, int window = 1,
        OutboxDeliveryMode deliveryMode = OutboxDeliveryMode.Serial,
        TimeProvider? timeProvider = null,
        Action? onPublish = null,
        bool requiresSaveAfterMark = false)
    {
        var builder = new MessageCatalogBuilder();
        builder.Add<BatchMarkMessage>(BatchMarkJsonContext.Default.BatchMarkMessage, "test.batchmark.v1");
        var catalog = builder.Build();
        var serializer = new NonNullSerializer();
        var store = new RecordingBatchStore(messageCount, requiresSaveAfterMark: requiresSaveAfterMark);
        var broker = new NullBroker(failAtIndex, serializer, catalog, onPublish);
        var processor = new OutboxBatchProcessor(
            store, broker, serializer, catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions
            {
                BatchSize = messageCount,
                BatchMarkThreshold = threshold,
                BatchMarkFlushInterval = TimeSpan.FromSeconds(1),
                DeliveryMode = deliveryMode,
                MaxInFlightPublishes = window,
                MaxRetryCount = 3,
            }),
            NullPalLogger<OutboxBatchProcessor>.Instance,
            timeProvider);

        await processor.ProcessBatchAsync(CancellationToken.None);
        return (store, broker);
    }

    // ─── stubs ─────────────────────────────────────────────────

    private sealed class NullBroker(int failAtIndex, IMessageSerializer serializer, IMessageCatalog catalog, Action? onPublish = null)
        : MessageBrokerBase(serializer, catalog)
    {
        private int _started;

        public override ValueTask PublishAsync(
            object message, MessageDescriptor msgDescriptor, PalUlid messageId,
            MessagePublishContext context, CancellationToken ct = default)
        {
            if (failAtIndex >= 0 && Interlocked.Increment(ref _started) - 1 == failAtIndex)
                throw new InvalidOperationException("broker publish failure (test)");
            onPublish?.Invoke();
            return ValueTask.CompletedTask;
        }

        public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("批量标记测试不订阅");

        public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("批量标记测试不订阅");
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

    /// <summary>反序列化恒抛——raw 路径不应触碰它。</summary>
    private sealed class ThrowingDeserializeSerializer : IMessageSerializer
    {
        public string ContentType => ContentTypes.Json;

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null) => default;

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor) => default;

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
            => throw new InvalidOperationException("raw publish path must not deserialize");

        public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
            => throw new InvalidOperationException("raw publish path must not deserialize");
    }

    /// <summary>支持 raw 发布的 broker：对象发布一律判红，raw 发布计数。</summary>
    private sealed class RawBroker(IMessageSerializer serializer, IMessageCatalog catalog)
        : MessageBrokerBase(serializer, catalog)
    {
        public int RawCount { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "接口成员必须为实例成员。")]
        public override bool SupportsRawPublish => true;

        public override ValueTask PublishRawAsync(
            ReadOnlyMemory<byte> payload, MessageDescriptor descriptor, PalUlid messageId,
            MessagePublishContext context, CancellationToken ct = default)
        {
            RawCount++;
            return ValueTask.CompletedTask;
        }

        public override ValueTask PublishAsync(
            object message, MessageDescriptor msgDescriptor, PalUlid messageId,
            MessagePublishContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("raw-capable broker must receive raw payloads");

        public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();

        public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>租约固定批 + 逐条/批量标记调用形态记录（IPalOutboxStore + IBatchMarkOutboxStore）。</summary>
    private sealed class RecordingBatchStore(
        int messageCount, bool partialBatchReject = false, bool requiresSaveAfterMark = false)
        : IPalOutboxStore, IBatchMarkOutboxStore
    {
        public readonly List<PalUlid> LeaseOrder = [];
        public readonly List<PalUlid> ProcessedOrder = [];
        public readonly List<PalUlid> ReleaseForRetryOrder = [];
        public readonly List<PalUlid> PerMessageProcessed = [];
        public readonly List<int> BatchCallSizes = [];

        // R14：批量标记能力的前提是「立即写库」——本替身默认即时记录（等价内置四栈语义）；
        // requiresSaveAfterMark=true 模拟自定义延迟写 store（不得进批量路径）。
        public bool RequiresSaveAfterMark => requiresSaveAfterMark;

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
            int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            var until = DateTimeOffset.UtcNow.Add(leaseDuration);
            List<OutboxMessage> leased = new(messageCount);
            for (var i = 0; i < messageCount; i++)
            {
                var msg = new OutboxMessage { Id = PalUlid.New(), Type = "test.batchmark.v1", Payload = [], ContentType = "application/json", LockedBy = owner, LockedUntil = until };
                LeaseOrder.Add(msg.Id);
                leased.Add(msg);
            }
            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(leased);
        }

        public ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default) => ValueTask.FromResult(messages.Count);

        public ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default)
        {
            PerMessageProcessed.Add(message.Id);
            ProcessedOrder.Add(message.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> MarkProcessedBatchAsync(IReadOnlyList<OutboxMessage> messages, DateTimeOffset processedAt, CancellationToken ct = default)
        {
            BatchCallSizes.Add(messages.Count);
            // R13 F16：模拟 fencing 部分拒绝——部分行租约已过期/被重租，affected < 批大小
            var affected = partialBatchReject ? messages.Count - 1 : messages.Count;
            for (var i = 0; i < affected; i++)
                ProcessedOrder.Add(messages[i].Id);
            return ValueTask.FromResult(affected);
        }

        public ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default)
        {
            ReleaseForRetryOrder.Add(message.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => ValueTask.FromResult(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }
}

public sealed class InMemoryBatchMarkTests
{
    [Test]
    public async Task BatchMark_AllLeased_MarkedAndEvicted()
    {
        var store = new InMemoryOutboxStore();
        var msgs = Enumerable.Range(0, 5).Select(_ => new OutboxMessage { Type = "t", Payload = [] }).ToList();
        await store.AddMessagesAsync(msgs);
        var leased = await store.LeasePendingMessagesAsync(10, "worker-a", TimeSpan.FromMinutes(1), 10, CancellationToken.None);

        var affected = await store.MarkProcessedBatchAsync(leased, DateTimeOffset.UtcNow, CancellationToken.None);

        await Assert.That(affected).IsEqualTo(5);
        foreach (var msg in leased)
        {
            await Assert.That(msg.Status).IsEqualTo(OutboxStatus.Processed);
            await Assert.That(msg.LockedBy).IsNull();
        }
        // 终态清除（R1/P2-7）：再租约为空
        var next = await store.LeasePendingMessagesAsync(10, "worker-b", TimeSpan.FromMinutes(1), 10, CancellationToken.None);
        await Assert.That(next.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BatchMark_StaleReference_SuccessorOnlyMarked()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-14T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new InMemoryOutboxStore(fakeTime);
        var msg = new OutboxMessage { Type = "t", Payload = [] };
        await store.AddMessageAsync(msg);

        var first = (await store.LeasePendingMessagesAsync(1, "worker-a", TimeSpan.FromSeconds(30), 10, CancellationToken.None))[0];
        fakeTime.Advance(TimeSpan.FromSeconds(31)); // 租约过期 → 重租产生 successor，旧引用失活
        var second = (await store.LeasePendingMessagesAsync(1, "worker-b", TimeSpan.FromSeconds(30), 10, CancellationToken.None))[0];

        var staleAffected = await store.MarkProcessedBatchAsync([first], DateTimeOffset.UtcNow, CancellationToken.None);
        var freshAffected = await store.MarkProcessedBatchAsync([second], DateTimeOffset.UtcNow, CancellationToken.None);

        await Assert.That(staleAffected).IsEqualTo(0); // fencing：旧引用（successor 替换后）拒绝
        await Assert.That(first.Status).IsEqualTo(OutboxStatus.Pending); // 零内存变异
        await Assert.That(freshAffected).IsEqualTo(1);
        await Assert.That(second.Status).IsEqualTo(OutboxStatus.Processed);
    }

    [Test]
    public async Task BatchMark_NullElement_ThrowsArgumentNullException()
    {
        var store = new InMemoryOutboxStore();
        var msg = new OutboxMessage { Type = "t", Payload = [], LockedBy = "w", LockedUntil = DateTimeOffset.UtcNow.AddMinutes(1) };

        await Assert.That(async () =>
            await store.MarkProcessedBatchAsync([msg, null!], DateTimeOffset.UtcNow, CancellationToken.None).AsTask())
            .Throws<ArgumentNullException>();
    }
}

/// <summary>批量标记测试消息（命名空间层级声明，SYSLIB1032 嵌套链规避）。</summary>
public sealed record BatchMarkMessage(string Value);

[JsonSerializable(typeof(BatchMarkMessage))]
internal sealed partial class BatchMarkJsonContext : JsonSerializerContext;
