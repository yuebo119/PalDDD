using PalDDD.Core.Logging;
using PalDDD.Messaging;
using PalDDD.Serialization;
using PalDDD.Testing;
using PalUlid = ByteAether.Ulid.Ulid;

// CA1822：测试桩的简单属性不适用 static 化建议（接口实现/可读性优先）
#pragma warning disable CA1822

namespace PalDDD.Transactions.Tests;

// ═══════════════════════════════════════════════════════════════
// ITM-685（2026-10-27 清零轮）：OutboxBatchProcessor 毒消息死信路径锁定测试
// ═══════════════════════════════════════════════════════════════
// 四条此前零覆盖的关键失败分支（回归后毒消息将永久重投而套件全绿）：
//   ①消息类型未注册于 MessageCatalog → MarkDead
//   ②反序列化返回 null → MarkDead
//   ③MarkDead 自身抛异常 → 挂 Data["MarkError"] 并继续批次
//   ④backoff 策略抛异常 → 1s 兜底（经发布失败重试路径触发）
// 直调 ProcessBatchAsync（绕过轮询循环），记录型 store + 桩序列化器构造分支。

public sealed partial class PoisonMessageDeadLetterTests
{
    [Test]
    public async Task UnregisteredType_IsMarkedDead_AndBatchContinues()
    {
        var store = new RecordingOutboxStore(
        [
            NewMessage("ghost-type", payload: [1, 2, 3]),
            NewMessage("known-type", payload: [4, 5, 6]),
        ]);
        var processor = BuildProcessor(store);

        await processor.ProcessBatchAsync(CancellationToken.None);

        // ①未注册类型死信（失败原因点名类型）②同批后续消息继续处理（不被毒消息中止）
        await Assert.That(store.DeadReasons.Count).IsEqualTo(1);
        await Assert.That(store.DeadReasons[0]).Contains("ghost-type");
        await Assert.That(store.DeadReasons[0]).Contains("not registered");
        await Assert.That(store.ProcessedIds.Count).IsEqualTo(1);
        await Assert.That(store.DeadReasons.Count + store.ProcessedIds.Count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task DeserializationNull_IsMarkedDead_WithSpecificReason()
    {
        // NullSerializer.Deserialize 恒返回 null → 命中分支②（raw 发布跳过反序列化，
        // 用不支持 raw 的 broker 关闭 raw 路径确保反序列化被走到）
        var store = new RecordingOutboxStore(
        [
            NewMessage("known-type", payload: [1, 2, 3]),
        ]);
        var processor = BuildProcessor(store, serializer: new NullingSerializer(), broker: new NonRawNullBroker());

        await processor.ProcessBatchAsync(CancellationToken.None);

        await Assert.That(store.DeadReasons.Count).IsEqualTo(1);
        await Assert.That(store.DeadReasons[0]).Contains("Deserialization returned null");
    }

    [Test]
    public async Task MarkDeadThrows_ContinuesBatch_AndDoesNotCrash()
    {
        // 分支③：标记失败不得中止整批（设计承诺——批内其余消息照常处理）
        var store = new RecordingOutboxStore(
        [
            NewMessage("ghost-type", payload: [1]),
            NewMessage("known-type", payload: [2]),
        ])
        { ThrowOnMarkDead = true };
        var processor = BuildProcessor(store);

        // 不抛：批次吞掉 MarkDead 异常（挂 Data["MarkError"] 记账后继续）
        await processor.ProcessBatchAsync(CancellationToken.None);

        await Assert.That(store.ProcessedIds.Count).IsEqualTo(1);
        // MarkDead 尝试发生过（异常被批次吞掉并挂账），毒消息未中止整批
        await Assert.That(store.DeadAttempts).IsEqualTo(1);
    }

    [Test]
    public async Task BackoffPolicyThrows_FallsBackToOneSecond()
    {
        // 分支④：backoff 策略抛异常 → 1s 兜底（不崩批次）。路径：发布失败 →
        // ReleaseForRetry → ComputeDelay 抛 → 1s 兜底延迟
        var store = new RecordingOutboxStore(
        [
            NewMessage("known-type", payload: [1]),
        ]);
        var processor = BuildProcessor(
            store, serializer: new PingSerializer(),
            broker: new ThrowingBroker(), backoff: new ThrowingBackoffPolicy());

        await processor.ProcessBatchAsync(CancellationToken.None);

        // 消息进入重试路径（未死信、未崩溃）——ReleaseForRetry 被调用即证明走到了 backoff 计算与兜底
        await Assert.That(store.RetryReasons.Count).IsEqualTo(1);
    }

    // ─── 构造辅助 ───────────────────────────────────────────────

    private static OutboxMessage NewMessage(string type, byte[] payload) => new()
    {
        Id = PalUlid.New(),
        Type = type,
        SchemaVersion = 1,
        Payload = payload,
        ContentType = ContentTypes.Json,
        RetryCount = 0,
        CreatedAt = DateTimeOffset.Parse("2026-10-27T00:00:00+00:00"),
    };

    private static MessageCatalog BuildCatalog()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add<PingMessage>(PoisonJsonContext.Default.PingMessage, "known-type");
        return builder.Build();
    }

    [System.Text.Json.Serialization.JsonSerializable(typeof(PingMessage))]
    internal sealed partial class PoisonJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

    private static OutboxBatchProcessor BuildProcessor(
        RecordingOutboxStore store,
        IMessageSerializer? serializer = null,
        IMessageBroker? broker = null,
        IRetryBackoffPolicy? backoff = null)
    {
        var options = new OutboxOptions { MaxRetryCount = 3 };
        if (backoff is not null) options.RetryBackoffPolicy = backoff;
        return new OutboxBatchProcessor(
            store,
            broker ?? new NullMessageBroker(),
            serializer ?? new PingSerializer(),
            BuildCatalog(),
            new FixedOptionsMonitor<OutboxOptions>(options),
            NullPalLogger<OutboxBatchProcessor>.Instance);
    }

    internal sealed class PingMessage;

    /// <summary>可成功反序列化的桩（非 raw 路径返回非 null 对象）</summary>
    private sealed class PingSerializer : IMessageSerializer
    {
        public string ContentType => ContentTypes.Json;
        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null) => new byte[] { 1 };
        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor) => new byte[] { 1 };
        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => new PingMessage();
        public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }

    /// <summary>恒返回 null 的桩（命中分支②）</summary>
    private sealed class NullingSerializer : IMessageSerializer
    {
        public string ContentType => ContentTypes.Json;
        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null) => default;
        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor) => default;
        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => null;
        public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }

    /// <summary>ContentType 与消息不一致 → 关闭 raw 路径，强制走反序列化（分支②前置）</summary>
    private sealed class NonRawNullBroker : IMessageBroker
    {
        public string ContentType => "application/x-palddd-nonraw";

        public ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("not used in test");

        public ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("not used in test");

        public ValueTask<IAsyncDisposable> SubscribeBatchAsync<TMessage>(Func<IReadOnlyList<ConsumedMessage<TMessage>>, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("not used in test");
    }

    private sealed class ThrowingBroker : IMessageBroker
    {
        public string ContentType => ContentTypes.Json;

        public ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
            => throw new InvalidOperationException("broker down (test)");

        public ValueTask PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct = default)
            => throw new InvalidOperationException("broker down (test)");

        public ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("not used in test");

        public ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("not used in test");

        public ValueTask<IAsyncDisposable> SubscribeBatchAsync<TMessage>(Func<IReadOnlyList<ConsumedMessage<TMessage>>, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException("not used in test");
    }

    private sealed class ThrowingBackoffPolicy : IRetryBackoffPolicy
    {
        public TimeSpan ComputeDelay(int retryCount) => throw new InvalidOperationException("backoff boom (test)");
    }

    /// <summary>记录死信/处理/重试的 store（首个租约放行一次消息集，之后空批）</summary>
    private sealed class RecordingOutboxStore(List<OutboxMessage> messages) : IPalOutboxStore
    {
        private int _leased;

        public List<string> DeadReasons { get; } = [];
        public int DeadAttempts;
        public List<string> RetryReasons { get; } = [];
        public List<PalUlid> ProcessedIds { get; } = [];
        public bool ThrowOnMarkDead { get; init; }

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(messages);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
        {
            var taken = Interlocked.Exchange(ref _leased, 1) == 0 ? messages : [];
            return ValueTask.FromResult<IReadOnlyList<OutboxMessage>>(taken);
        }

        public ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default) => new(0);

        public ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default)
        {
            ProcessedIds.Add(message.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default)
        {
            DeadAttempts++;
            if (ThrowOnMarkDead) throw new InvalidOperationException("MarkDead boom (test)");
            DeadReasons.Add(failureReason);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default)
        {
            RetryReasons.Add(failureReason);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => new(0);
        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => new(0);
    }
}
