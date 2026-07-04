using PalDDD.Core.Logging;
using PalDDD.Messaging;
using PalDDD.Serialization;
using PalDDD.Testing;
using System.Diagnostics;
using System.Text.Json.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

// 鈹€鈹€鈹€ 娴嬭瘯鐢ㄨ法杩涚▼娑堟伅 鈹€鈹€鈹€

public sealed class OrderCreatedIntegrationEvent
{
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
}

public sealed class OrderCancelledIntegrationEvent
{
    public Guid OrderId { get; init; }
}

[JsonSerializable(typeof(OrderCreatedIntegrationEvent))]
[JsonSerializable(typeof(OrderCancelledIntegrationEvent))]
internal sealed partial class TransactionsJsonContext : JsonSerializerContext;

// 鈹€鈹€鈹€ OutboxMessage 娴嬭瘯 鈹€鈹€鈹€

public sealed class OutboxMessageTests
{
    [Test]
    public async Task NewMessage_HasDefaultValues()
    {
        var msg = new OutboxMessage();

        await Assert.That(msg.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(msg.Type).IsEqualTo("");
        await Assert.That(msg.Payload).IsEmpty();
        await Assert.That(msg.CreatedAt <= DateTimeOffset.UtcNow).IsTrue();
        await Assert.That(msg.ProcessedAt).IsNull();
        await Assert.That(msg.RetryCount).IsEqualTo(0);
        await Assert.That(msg.Error).IsNull();
    }

    [Test]
    public async Task Message_SetProperties()
    {
        var msg = new OutboxMessage
        {
            Type = "OrderCreated",
            Payload = "{\"orderId\":\"abc\"}"u8.ToArray(),
            RetryCount = 3,
            Error = "Connection timeout",
            ProcessedAt = DateTimeOffset.UtcNow
        };

        await Assert.That(msg.Type).IsEqualTo("OrderCreated");
        await Assert.That(msg.Payload).IsEquivalentTo("{\"orderId\":\"abc\"}"u8.ToArray());
        await Assert.That(msg.RetryCount).IsEqualTo(3);
        await Assert.That(msg.Error).IsEqualTo("Connection timeout");
        await Assert.That(msg.ProcessedAt).IsNotNull();
    }

    [Test]
    public async Task EachMessage_HasUniqueId()
    {
        var ids = new HashSet<Guid>();
        for (int i = 0; i < 100; i++)
            ids.Add(new OutboxMessage().Id);

        await Assert.That(ids.Count).IsEqualTo(100);
    }
}

public sealed class OutboxBatchProcessorTests
{
    [Test]
    public async Task ProcessBatchAsync_EmitsOutboxProcessActivity(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var builder = new MessageCatalogBuilder();
        var descriptor = builder.Add(
            TransactionsJsonContext.Default.OrderCreatedIntegrationEvent,
            name: "orders.order-created.v1");
        var catalog = builder.Build();
        var payload = "serialized-order-created-event"u8.ToArray();
        var message = new OutboxMessage
        {
            Type = descriptor.Name,
            Payload = payload,
            ContentType = descriptor.ContentType,
            SchemaVersion = descriptor.SchemaVersion
        };
        var processor = new OutboxBatchProcessor(
            new SingleMessageOutboxStore(message),
            new RecordingMessageBroker(),
            new FixedMessageSerializer(payload, new OrderCreatedIntegrationEvent()),
            catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions()),
            NullPalLogger<OutboxBatchProcessor>.Instance);

        await processor.ProcessBatchAsync(cancellationToken);

        var matching = listener.StoppedActivities.Where(a => a.OperationName == "Outbox Process").ToList();
        await Assert.That(matching).Count().IsGreaterThanOrEqualTo(1);
        var activity = matching.First(a =>
            a.GetTagItem("pal.outbox.batch_size") is int bs && bs == 1 &&
            a.GetTagItem("pal.outbox.processed") is int pr && pr == 1);
        await Assert.That(activity.GetTagItem("pal.outbox.batch_size")).IsEqualTo(1);
        await Assert.That(activity.GetTagItem("pal.outbox.processed")).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessBatchAsync_RecordsOutboxProcessedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.outbox.processed");
        var builder = new MessageCatalogBuilder();
        var descriptor = builder.Add(
            TransactionsJsonContext.Default.OrderCreatedIntegrationEvent,
            name: "orders.order-created.v1");
        var catalog = builder.Build();
        var payload = "serialized-order-created-event"u8.ToArray();
        var message = new OutboxMessage
        {
            Type = descriptor.Name,
            Payload = payload,
            ContentType = descriptor.ContentType,
            SchemaVersion = descriptor.SchemaVersion
        };
        var processor = new OutboxBatchProcessor(
            new SingleMessageOutboxStore(message),
            new RecordingMessageBroker(),
            new FixedMessageSerializer(payload, new OrderCreatedIntegrationEvent()),
            catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions()),
            NullPalLogger<OutboxBatchProcessor>.Instance);

        await processor.ProcessBatchAsync(cancellationToken);

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task ProcessBatchAsync_RecordsOutboxFailedMetricWhenMessageRetries(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.outbox.failed");
        var builder = new MessageCatalogBuilder();
        var descriptor = builder.Add(
            TransactionsJsonContext.Default.OrderCreatedIntegrationEvent,
            name: "orders.order-created.v1");
        var catalog = builder.Build();
        var payload = "serialized-order-created-event"u8.ToArray();
        var message = new OutboxMessage
        {
            Type = descriptor.Name,
            Payload = payload,
            ContentType = descriptor.ContentType,
            SchemaVersion = descriptor.SchemaVersion
        };
        var processor = new OutboxBatchProcessor(
            new SingleMessageOutboxStore(message, allowFailureTransitions: true),
            new ThrowingMessageBroker(),
            new FixedMessageSerializer(payload, new OrderCreatedIntegrationEvent()),
            catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions { MaxRetryCount = 3 }),
            NullPalLogger<OutboxBatchProcessor>.Instance);

        await processor.ProcessBatchAsync(cancellationToken);

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task ProcessBatchAsync_WhenPublishCancels_PropagatesCancellation(CancellationToken cancellationToken)
    {
        var builder = new MessageCatalogBuilder();
        var descriptor = builder.Add(
            TransactionsJsonContext.Default.OrderCreatedIntegrationEvent,
            name: "orders.order-created.v1");
        var catalog = builder.Build();
        var payload = "serialized-order-created-event"u8.ToArray();
        var message = new OutboxMessage
        {
            Type = descriptor.Name,
            Payload = payload,
            ContentType = descriptor.ContentType,
            SchemaVersion = descriptor.SchemaVersion
        };
        var processor = new OutboxBatchProcessor(
            new SingleMessageOutboxStore(message, allowFailureTransitions: true),
            new CancelingMessageBroker(),
            new FixedMessageSerializer(payload, new OrderCreatedIntegrationEvent()),
            catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions { MaxRetryCount = 3 }),
            NullPalLogger<OutboxBatchProcessor>.Instance);

        await Assert.That(async () => await processor.ProcessBatchAsync(cancellationToken)).Throws<OperationCanceledException>();

        await Assert.That(message.RetryCount).IsEqualTo(0);
    }

    [Test]
    public async Task ProcessBatchAsync_WhenPersistenceCancels_PropagatesCancellation(CancellationToken cancellationToken)
    {
        var builder = new MessageCatalogBuilder();
        var descriptor = builder.Add(
            TransactionsJsonContext.Default.OrderCreatedIntegrationEvent,
            name: "orders.order-created.v1");
        var catalog = builder.Build();
        var payload = "serialized-order-created-event"u8.ToArray();
        var message = new OutboxMessage
        {
            Type = descriptor.Name,
            Payload = payload,
            ContentType = descriptor.ContentType,
            SchemaVersion = descriptor.SchemaVersion
        };
        var processor = new OutboxBatchProcessor(
            new SingleMessageOutboxStore(message, cancelSaveChanges: true),
            new RecordingMessageBroker(),
            new FixedMessageSerializer(payload, new OrderCreatedIntegrationEvent()),
            catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions()),
            NullPalLogger<OutboxBatchProcessor>.Instance);

        await Assert.That(async () => await processor.ProcessBatchAsync(cancellationToken)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ProcessBatchAsync_PublishesOutboxTraceContext(CancellationToken cancellationToken)
    {
        var builder = new MessageCatalogBuilder();
        var descriptor = builder.Add(
            TransactionsJsonContext.Default.OrderCreatedIntegrationEvent,
            name: "orders.order-created.v1");
        var catalog = builder.Build();
        var integrationEvent = new OrderCreatedIntegrationEvent
        {
            OrderId = Guid.Parse("0199898b-24e7-71d3-bff7-db1b30f7d4d4"),
            Amount = 125m
        };
        var payload = "serialized-order-created-event"u8.ToArray();
        var serializer = new FixedMessageSerializer(payload, integrationEvent);
        var message = new OutboxMessage
        {
            Type = descriptor.Name,
            Payload = payload,
            ContentType = descriptor.ContentType,
            SchemaVersion = descriptor.SchemaVersion,
            CorrelationId = Guid.Parse("66e6b674-fb93-44ac-8cc2-ee55407d7d4c"),
            CausationId = Guid.Parse("0ce9c186-ecc3-4dd3-8c32-afd2e6eba4a9"),
            TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            TraceState = "tenant=ordering"
        };
        var store = new SingleMessageOutboxStore(message);
        var broker = new RecordingMessageBroker();
        var processor = new OutboxBatchProcessor(
            store,
            broker,
            serializer,
            catalog,
            new FixedOptionsMonitor<OutboxOptions>(new OutboxOptions()),
            NullPalLogger<OutboxBatchProcessor>.Instance);

        await processor.ProcessBatchAsync(cancellationToken);

        await Assert.That(broker.MessageId).IsEqualTo(message.Id);
        await Assert.That(broker.Context.CorrelationId).IsEqualTo(message.CorrelationId);
        await Assert.That(broker.Context.CausationId).IsEqualTo(message.CausationId);
        await Assert.That(broker.Context.TraceParent).IsEqualTo(message.TraceParent);
        await Assert.That(broker.Context.TraceState).IsEqualTo(message.TraceState);
        await Assert.That(store.MarkProcessedCalled).IsTrue();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S927", Justification = "娴嬭瘯 stub 鍙傛暟鍚?outboxMessage 涓庢帴鍙?message 涓嶅悓锛岄伩鍏嶉伄钄芥崟鑾风殑鏋勯€犲嚱鏁板弬鏁?message.")]
    private sealed class SingleMessageOutboxStore(
        OutboxMessage message,
        bool allowFailureTransitions = false,
        bool cancelSaveChanges = false) : IPalOutboxStore
    {
        public bool MarkProcessedCalled { get; private set; }

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([message]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(
            int batchSize,
            string owner,
            TimeSpan leaseDuration,
            int maxRetryCount,
            CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([message]);

        public void AddMessage(OutboxMessage outboxMessage)
            => throw new InvalidOperationException("The test store is read-only.");

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages)
            => throw new InvalidOperationException("The test store is read-only.");

        public void MarkProcessed(OutboxMessage outboxMessage, DateTimeOffset processedAt)
        {
            if (!ReferenceEquals(message, outboxMessage))
                throw new InvalidOperationException("Expected same reference.");
            MarkProcessedCalled = true;
            outboxMessage.Status = OutboxStatus.Processed;
            outboxMessage.ProcessedAt = processedAt;
        }

        public void MarkDead(OutboxMessage outboxMessage, string failureReason, DateTimeOffset deadAt)
        {
            if (!allowFailureTransitions)
            {
                throw new InvalidOperationException("The message should publish successfully.");
            }

            outboxMessage.Status = OutboxStatus.Dead;
            outboxMessage.Error = failureReason;
            outboxMessage.ProcessedAt = deadAt;
        }

        public void ReleaseForRetry(OutboxMessage outboxMessage, string failureReason, DateTimeOffset nextAttemptAt)
        {
            if (!allowFailureTransitions)
            {
                throw new InvalidOperationException("The message should publish successfully.");
            }

            outboxMessage.Status = OutboxStatus.Pending;
            outboxMessage.Error = failureReason;
            outboxMessage.NextAttemptAt = nextAttemptAt;
        }

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct)
            => ValueTask.FromResult(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct)
            => cancelSaveChanges
                ? throw new OperationCanceledException("Save canceled.", ct)
                : ValueTask.FromResult(1);
    }

    private sealed class RecordingMessageBroker : IMessageBroker
    {
        public PalUlid MessageId { get; private set; }
        public MessagePublishContext Context { get; private set; }

        public ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
            => throw new InvalidOperationException("Outbox should use the descriptor-based publish overload.");

        public ValueTask PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct = default)
            => throw new InvalidOperationException("Outbox should pass MessagePublishContext.");

        public ValueTask PublishAsync(
            object message,
            MessageDescriptor descriptor,
            PalUlid messageId,
            MessagePublishContext context,
            CancellationToken ct = default)
        {
            MessageId = messageId;
            Context = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Subscribe is not used in this test.");
    }

    private sealed class ThrowingMessageBroker : IMessageBroker
    {
        public ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
            => throw new InvalidOperationException("Outbox should use the descriptor-based publish overload.");

        public ValueTask PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct = default)
            => throw new InvalidOperationException("Broker failed.");

        public ValueTask PublishAsync(
            object message,
            MessageDescriptor descriptor,
            PalUlid messageId,
            MessagePublishContext context,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Broker failed.");

        public ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Subscribe is not used in this test.");
    }

    private sealed class CancelingMessageBroker : IMessageBroker
    {
        public ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
            => throw new InvalidOperationException("Outbox should use the descriptor-based publish overload.");

        public ValueTask PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct = default)
            => throw new InvalidOperationException("Outbox should pass MessagePublishContext.");

        public ValueTask PublishAsync(
            object message,
            MessageDescriptor descriptor,
            PalUlid messageId,
            MessagePublishContext context,
            CancellationToken ct = default)
            => throw new OperationCanceledException("Publish canceled.");

        public ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
            Func<TMessage, CancellationToken, ValueTask> handler,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Subscribe is not used in this test.");
    }

    private sealed class FixedMessageSerializer(
        ReadOnlyMemory<byte> serializedPayload,
        object deserializedMessage) : IMessageSerializer
    {
        public string ContentType => ContentTypes.Json;

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null)
            => serializedPayload;

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor)
            => serializedPayload;

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
            => deserializedMessage;

        public TMessage? Deserialize<TMessage>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor)
            => (TMessage?)deserializedMessage;
    }
}

// 鈹€鈹€鈹€ MessageCatalog 娴嬭瘯 鈹€鈹€鈹€

public sealed class MessageCatalogTests
{
    [Test]
    public async Task Builder_Generic_ThenFind_Succeeds()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(TransactionsJsonContext.Default.OrderCreatedIntegrationEvent);
        var catalog = builder.Build();

        var descriptor = catalog.Find(nameof(OrderCreatedIntegrationEvent));

        await Assert.That(descriptor).IsNotNull();
        await Assert.That(descriptor.ClrType).IsEqualTo(typeof(OrderCreatedIntegrationEvent));
    }

    [Test]
    public async Task Register_MultipleTypes_AllFound()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(TransactionsJsonContext.Default.OrderCreatedIntegrationEvent);
        builder.Add(TransactionsJsonContext.Default.OrderCancelledIntegrationEvent);
        var catalog = builder.Build();

        await Assert.That(catalog.Find(nameof(OrderCreatedIntegrationEvent))?.ClrType).IsEqualTo(typeof(OrderCreatedIntegrationEvent));
        await Assert.That(catalog.Find(nameof(OrderCancelledIntegrationEvent))?.ClrType).IsEqualTo(typeof(OrderCancelledIntegrationEvent));
    }

    [Test]
    public async Task Find_Unregistered_ReturnsNull()
    {
        var catalog = MessageCatalog.Empty;

        var descriptor = catalog.Find("NonExistentType");
        await Assert.That(descriptor).IsNull();
    }

    [Test]
    public async Task Add_Descriptors_ThenFind()
    {
        var builder = new MessageCatalogBuilder();

        builder.Add(new MessageDescriptor(
            nameof(OrderCreatedIntegrationEvent),
            typeof(OrderCreatedIntegrationEvent),
            TransactionsJsonContext.Default.OrderCreatedIntegrationEvent));
        builder.Add(new MessageDescriptor(
            nameof(OrderCancelledIntegrationEvent),
            typeof(OrderCancelledIntegrationEvent),
            TransactionsJsonContext.Default.OrderCancelledIntegrationEvent));
        var catalog = builder.Build();

        await Assert.That(catalog.Find(nameof(OrderCreatedIntegrationEvent))).IsNotNull();
        await Assert.That(catalog.Find(nameof(OrderCancelledIntegrationEvent))).IsNotNull();
    }

    [Test]
    public async Task BuiltCatalog_DoesNotChangeWhenBuilderAddsMoreMessages()
    {
        var builder = new MessageCatalogBuilder();
        builder.Add(TransactionsJsonContext.Default.OrderCreatedIntegrationEvent);
        var catalog = builder.Build();

        builder.Add(TransactionsJsonContext.Default.OrderCancelledIntegrationEvent);

        await Assert.That(catalog.Find(nameof(OrderCreatedIntegrationEvent))).IsNotNull();
        await Assert.That(catalog.Find(nameof(OrderCancelledIntegrationEvent))).IsNull();
    }
}

public sealed class InboxProcessorTests
{
    [Test]
    public async Task TryProcessAsync_EmitsInboxProcessActivityWhenProcessed(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var store = new RecordingInboxStore();
        var processor = new InboxProcessor(
            store,
            new FixedOptionsMonitor<InboxOptions>(new InboxOptions
            {
                DefaultConsumerName = "orders",
                ProcessingTimeout = TimeSpan.FromSeconds(7)
            }),
            NullPalLogger<InboxProcessor>.Instance,
            TimeProvider.System);

        var processed = await processor.TryProcessAsync(
            "message-1",
            static (_, _) => ValueTask.CompletedTask,
            "payload",
            cancellationToken);

        var matching = listener.StoppedActivities.Where(a => a.OperationName == "Inbox Process").ToList();
        await Assert.That(matching).Count().IsGreaterThanOrEqualTo(1);
        var activity = matching.First(a =>
            (string?)a.GetTagItem("pal.inbox.result") == "processed" &&
            string.Equals(a.GetTagItem("pal.inbox.consumer") as string, "orders", StringComparison.Ordinal));
        await Assert.That(processed).IsTrue();
        await Assert.That(activity.GetTagItem("pal.inbox.consumer")).IsEqualTo("orders");
        await Assert.That(activity.GetTagItem("pal.inbox.message_id")).IsEqualTo("message-1");
        await Assert.That(activity.GetTagItem("pal.inbox.result")).IsEqualTo("processed");
    }

    [Test]
    public async Task TryProcessAsync_EmitsInboxProcessActivityWhenSkipped(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var store = new DuplicateAwareInboxStore();
        var processor = new InboxProcessor(
            store,
            new FixedOptionsMonitor<InboxOptions>(new InboxOptions()),
            NullPalLogger<InboxProcessor>.Instance,
            TimeProvider.System);
        await processor.TryProcessAsync(
            "orders",
            "message-1",
            static (_, _) => ValueTask.CompletedTask,
            "payload",
            cancellationToken);

        var skipped = await processor.TryProcessAsync(
            "orders",
            "message-1",
            static (_, _) => ValueTask.CompletedTask,
            "payload",
            cancellationToken);

        var matching = listener.StoppedActivities.Where(a =>
            a.OperationName == "Inbox Process" &&
            string.Equals(a.GetTagItem("pal.inbox.result") as string, "skipped", StringComparison.Ordinal) &&
            string.Equals(a.GetTagItem("pal.inbox.consumer") as string, "orders", StringComparison.Ordinal)).ToList();
        await Assert.That(matching).Count().IsGreaterThanOrEqualTo(1);
        var activity = matching.First();
        await Assert.That(skipped).IsFalse();
        await Assert.That(activity.GetTagItem("pal.inbox.consumer")).IsEqualTo("orders");
        await Assert.That(activity.GetTagItem("pal.inbox.message_id")).IsEqualTo("message-1");
    }

    [Test]
    public async Task TryProcessAsync_RecordsInboxProcessedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.inbox.processed");
        var store = new RecordingInboxStore();
        var processor = new InboxProcessor(
            store,
            new FixedOptionsMonitor<InboxOptions>(new InboxOptions()),
            NullPalLogger<InboxProcessor>.Instance,
            TimeProvider.System);

        await processor.TryProcessAsync(
            "orders",
            "message-1",
            static (_, _) => ValueTask.CompletedTask,
            "payload",
            cancellationToken);

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task TryProcessAsync_RecordsInboxSkippedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.inbox.skipped");
        var store = new DuplicateAwareInboxStore();
        var processor = new InboxProcessor(
            store,
            new FixedOptionsMonitor<InboxOptions>(new InboxOptions()),
            NullPalLogger<InboxProcessor>.Instance,
            TimeProvider.System);
        await processor.TryProcessAsync(
            "orders",
            "message-1",
            static (_, _) => ValueTask.CompletedTask,
            "payload",
            cancellationToken);

        await processor.TryProcessAsync(
            "orders",
            "message-1",
            static (_, _) => ValueTask.CompletedTask,
            "payload",
            cancellationToken);

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task TryProcessAsync_RecordsInboxFailedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.inbox.failed");
        var store = new FailingAwareInboxStore();
        var processor = new InboxProcessor(
            store,
            new FixedOptionsMonitor<InboxOptions>(new InboxOptions()),
            NullPalLogger<InboxProcessor>.Instance,
            TimeProvider.System);

        await Assert.That(async () => await processor.TryProcessAsync(
                "orders",
                "message-1",
                static (_, _) => throw new InvalidOperationException("handler failed"),
                "payload",
                cancellationToken)).Throws<InvalidOperationException>();

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task TryProcessAsync_WhenHandlerFails_MarksActivityAsError(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var store = new FailingAwareInboxStore();
        var processor = new InboxProcessor(
            store,
            new FixedOptionsMonitor<InboxOptions>(new InboxOptions()),
            NullPalLogger<InboxProcessor>.Instance,
            TimeProvider.System);

        var exception = await Assert.That(async () => await processor.TryProcessAsync(
                "orders",
                "message-1",
                static (_, _) => throw new InvalidOperationException("handler failed"),
                "payload",
                cancellationToken)).Throws<InvalidOperationException>();

        var matching = listener.StoppedActivities.Where(a => a.OperationName == "Inbox Process").ToList();
        await Assert.That(matching).Count().IsGreaterThanOrEqualTo(1);
        var activity = matching.First(a =>
            (string?)a.GetTagItem("pal.inbox.result") == "failed" &&
            string.Equals(a.GetTagItem("pal.inbox.consumer") as string, "orders", StringComparison.Ordinal));
        await Assert.That(exception!.Message).IsEqualTo("handler failed");
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.StatusDescription).Contains("handler failed");
        await Assert.That(activity.GetTagItem("pal.inbox.result")).IsEqualTo("failed");
    }

    [Test]
    public async Task TryProcessAsync_UsesConfiguredDefaultConsumerAndProcessingTimeout()
    {
        var store = new RecordingInboxStore();
        var processor = new InboxProcessor(
            store,
            new FixedOptionsMonitor<InboxOptions>(new InboxOptions
            {
                DefaultConsumerName = "orders",
                ProcessingTimeout = TimeSpan.FromSeconds(7)
            }),
            NullPalLogger<InboxProcessor>.Instance,
            TimeProvider.System);

        var handled = false;
        var processed = await processor.TryProcessAsync(
            "message-1",
            (string message, CancellationToken _) =>
            {
                handled = message == "payload";
                return ValueTask.CompletedTask;
            },
            "payload");

        await Assert.That(processed).IsTrue();
        await Assert.That(handled).IsTrue();
        await Assert.That(store.ConsumerName).IsEqualTo("orders");
        await Assert.That(store.MessageId).IsEqualTo("message-1");
        await Assert.That(store.ProcessingTimeout).IsEqualTo(TimeSpan.FromSeconds(7));
        await Assert.That(store.MarkProcessedCalled).IsTrue();
    }

    [Test]
    public async Task IdempotentMessageConsumer_SkipsDuplicateMessage(CancellationToken cancellationToken)
    {
        var store = new DuplicateAwareInboxStore();
        var inbox = new InboxProcessor(
            store,
            new FixedOptionsMonitor<InboxOptions>(new InboxOptions()),
            NullPalLogger<InboxProcessor>.Instance,
            TimeProvider.System);
        var calls = 0;

        var first = await inbox.TryProcessAsync(
            "order-projection",
            "message-1",
            (string payload, CancellationToken _) =>
            {
                calls++;
                return ValueTask.CompletedTask;
            },
            "payload",
            cancellationToken);

        var second = await inbox.TryProcessAsync(
            "order-projection",
            "message-1",
            (string payload, CancellationToken _) =>
            {
                calls++;
                return ValueTask.CompletedTask;
            },
            "payload",
            cancellationToken);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(calls).IsEqualTo(1);
    }

    private sealed class RecordingInboxStore : IInboxStore
    {
        private InboxMessage? _message;

        public string? ConsumerName { get; private set; }
        public string? MessageId { get; private set; }
        public TimeSpan ProcessingTimeout { get; private set; }
        public bool MarkProcessedCalled { get; private set; }

        public ValueTask<InboxMessage?> TryStartProcessingAsync(
            string consumerName,
            string messageId,
            DateTimeOffset now,
            TimeSpan processingTimeout,
            CancellationToken ct)
        {
            ConsumerName = consumerName;
            MessageId = messageId;
            ProcessingTimeout = processingTimeout;
            _message = new InboxMessage
            {
                ConsumerName = consumerName,
                MessageId = messageId,
                Status = InboxStatus.Processing,
                ProcessingStartedAt = now,
                Attempts = 1
            };
            return ValueTask.FromResult<InboxMessage?>(_message);
        }

        public async ValueTask MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct)
        {
            await Assert.That(message).IsSameReferenceAs(_message);
            MarkProcessedCalled = true;
            message.Status = InboxStatus.Processed;
            message.ProcessedAt = processedAt;
        }

        public ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
            => throw new InvalidOperationException("The handler should not fail in this test.");
    }

    private sealed class DuplicateAwareInboxStore : IInboxStore
    {
        private readonly HashSet<(string ConsumerName, string MessageId)> _processed = [];

        public ValueTask<InboxMessage?> TryStartProcessingAsync(
            string consumerName,
            string messageId,
            DateTimeOffset now,
            TimeSpan processingTimeout,
            CancellationToken ct)
        {
            if (_processed.Contains((consumerName, messageId)))
                return ValueTask.FromResult<InboxMessage?>(null);

            return ValueTask.FromResult<InboxMessage?>(new InboxMessage
            {
                ConsumerName = consumerName,
                MessageId = messageId,
                Status = InboxStatus.Processing,
                ReceivedAt = now,
                ProcessingStartedAt = now,
                Attempts = 1
            });
        }

        public ValueTask MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct)
        {
            _processed.Add((message.ConsumerName, message.MessageId));
            message.Status = InboxStatus.Processed;
            message.ProcessedAt = processedAt;
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
            => throw new InvalidOperationException("The handler should not fail in this test.");
    }

    private sealed class FailingAwareInboxStore : IInboxStore
    {
        private InboxMessage? _message;

        public ValueTask<InboxMessage?> TryStartProcessingAsync(
            string consumerName,
            string messageId,
            DateTimeOffset now,
            TimeSpan processingTimeout,
            CancellationToken ct)
        {
            _message = new InboxMessage
            {
                ConsumerName = consumerName,
                MessageId = messageId,
                Status = InboxStatus.Processing,
                ReceivedAt = now,
                ProcessingStartedAt = now,
                Attempts = 1
            };
            return ValueTask.FromResult<InboxMessage?>(_message);
        }

        public ValueTask MarkProcessedAsync(InboxMessage message, DateTimeOffset processedAt, CancellationToken ct)
            => throw new InvalidOperationException("The handler should fail in this test.");

        public async ValueTask MarkFailedAsync(InboxMessage message, string failureReason, CancellationToken ct)
        {
            await Assert.That(message).IsSameReferenceAs(_message);
            message.Status = InboxStatus.Failed;
            message.LastError = failureReason;
        }
    }
}

// 鈹€鈹€鈹€ SagaState 娴嬭瘯 鈹€鈹€鈹€

public sealed class OrderSagaState : SagaState
{
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
}

public sealed class SagaStateTests
{
    [Test]
    public async Task NewState_HasDefaultValues()
    {
        var state = new OrderSagaState();

        await Assert.That(state.SagaId).IsNotEqualTo(Guid.Empty);
        await Assert.That(state.CurrentState).IsEqualTo("Initial");
        await Assert.That(state.Status).IsEqualTo(SagaStatus.Active);
        await Assert.That(state.CompletedAt).IsNull();
        await Assert.That(state.Version).IsEqualTo(0);
        await Assert.That(state.Error).IsNull();
        await Assert.That(state.ExecutedStepKeys).IsEmpty();
    }

    [Test]
    public async Task State_CanTransition()
    {
        var state = new OrderSagaState();
        state.CurrentState = "Processing";
        state.Version = 1;

        await Assert.That(state.CurrentState).IsEqualTo("Processing");
        await Assert.That(state.Version).IsEqualTo(1);
    }

    [Test]
    public async Task State_CanBeCompleted()
    {
        var state = new OrderSagaState
        {
            Status = SagaStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await Assert.That(state.Status).IsEqualTo(SagaStatus.Completed);
        await Assert.That(state.CompletedAt).IsNotNull();
    }
}

public sealed class SagaTimeoutProcessorTests
{
    [Test]
    public async Task CheckTimeoutsAsync_UsesConfiguredBatchSize()
    {
        var store = new RecordingSagaStateStore();
        var processor = new SagaTimeoutProcessor<OrderSagaState>(
            store,
            new OrderFulfillmentSaga(),
            NullPalLogger<SagaTimeoutProcessor<OrderSagaState>>.Instance,
            new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions
            {
                TimeoutScanBatchSize = 17
            }),
            TimeProvider.System);

        await processor.CheckTimeoutsAsync(CancellationToken.None);

        await Assert.That(store.BatchSize).IsEqualTo(17);
    }

    [Test]
    public async Task CheckTimeoutsAsync_RecordsSagaCompensatedMetric()
    {
        using var listener = new RecordingMeterListener("paldd.saga.compensated");
        var state = new OrderSagaState
        {
            CurrentState = "Waiting"
        };
        state.StepStartedAt["Waiting|OrderPlacedSagaEvent"] = DateTimeOffset.UnixEpoch;
        var store = new RecordingSagaStateStore([state]);
        var processor = new SagaTimeoutProcessor<OrderSagaState>(
            store,
            new TimedOutSaga(),
            NullPalLogger<SagaTimeoutProcessor<OrderSagaState>>.Instance,
            new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions()),
            TimeProvider.System);

        await processor.CheckTimeoutsAsync(CancellationToken.None);

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task CheckTimeoutsAsync_RecordsCompensationFailedStatus()
    {
        var state = new OrderSagaState
        {
            CurrentState = "Waiting"
        };
        state.StepStartedAt["Waiting|OrderPlacedSagaEvent"] = DateTimeOffset.UnixEpoch;
        state.ExecutedStepKeys.Add("Waiting|OrderPlacedSagaEvent");
        var store = new RecordingSagaStateStore([state]);
        var processor = new SagaTimeoutProcessor<OrderSagaState>(
            store,
            new CompensationFailureSaga(),
            NullPalLogger<SagaTimeoutProcessor<OrderSagaState>>.Instance,
            new FixedOptionsMonitor<SagaProcessorOptions>(new SagaProcessorOptions()),
            TimeProvider.System);

        await processor.CheckTimeoutsAsync(CancellationToken.None);

        await Assert.That(state.Status).IsEqualTo(SagaStatus.CompensationFailed);
        await Assert.That(state.CurrentState).IsEqualTo("CompensationFailed");
        await Assert.That(state.Error).IsNotNull();
        await Assert.That(state.ErrorAt).IsNotNull();
    }

    private sealed class RecordingSagaStateStore(IReadOnlyList<OrderSagaState>? states = null)
        : ISagaStateStore<OrderSagaState>
    {
        public int BatchSize { get; private set; }

        public ValueTask<IReadOnlyList<OrderSagaState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
        {
            BatchSize = batchSize;
            return ValueTask.FromResult(states ?? Array.Empty<OrderSagaState>());
        }

        public ValueTask<IReadOnlyList<OrderSagaState>> LeaseActiveSagasAsync(
            string owner,
            TimeSpan leaseDuration,
            int batchSize,
            CancellationToken ct)
        {
            BatchSize = batchSize;
            return ValueTask.FromResult(states ?? Array.Empty<OrderSagaState>());
        }

        public ValueTask<OrderSagaState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
            => ValueTask.FromResult<OrderSagaState?>(null);

        public ValueTask<int> SaveChangesAsync(OrderSagaState state, CancellationToken ct)
            => ValueTask.FromResult(0);
    }

    private sealed class TimedOutSaga : Saga<OrderSagaState>
    {
        public TimedOutSaga()
        {
            When("Waiting", typeof(OrderPlacedSagaEvent), new SagaStep(
                "WaitForPayment",
                (state, evt, ct) => ValueTask.FromResult(state),
                (state, ct) =>
                {
                    state.CurrentState = "Compensated_WaitForPayment";
                    return ValueTask.CompletedTask;
                },
                TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class CompensationFailureSaga : Saga<OrderSagaState>
    {
        public CompensationFailureSaga()
        {
            When("Waiting", typeof(OrderPlacedSagaEvent), new SagaStep(
                "WaitForPayment",
                (state, evt, ct) => ValueTask.FromResult(state),
                (_, _) => throw new InvalidOperationException("Compensation failed"),
                TimeSpan.FromSeconds(1)));
        }
    }
}

// 鈹€鈹€鈹€ SagaStep 娴嬭瘯 鈹€鈹€鈹€

public sealed class SagaStepTests
{
    [Test]
    public async Task Ctor_SetsProperties()
    {
        var step = new SagaStep(
            "CreateOrder",
            (state, evt, ct) => ValueTask.FromResult(state),
            (state, ct) => ValueTask.CompletedTask,
            TimeSpan.FromMinutes(5));

        await Assert.That(step.Name).IsEqualTo("CreateOrder");
        await Assert.That((object?)step.ExecuteAsync).IsNotNull();
        await Assert.That((object?)step.CompensateAsync).IsNotNull();
        await Assert.That(step.Timeout).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task Ctor_WithoutCompensation()
    {
        var step = new SagaStep(
            "ShipOrder",
            (state, evt, ct) => ValueTask.FromResult(state));

        await Assert.That(step.Name).IsEqualTo("ShipOrder");
        await Assert.That((object?)step.CompensateAsync).IsNull();
        await Assert.That(step.Timeout).IsNull();
    }
}

// 鈹€鈹€鈹€ Saga 娴嬭瘯 鈹€鈹€鈹€

public sealed class OrderFulfillmentSaga : Saga<OrderSagaState>
{
    public OrderFulfillmentSaga()
    {
        When("Initial", typeof(OrderPlacedSagaEvent), new SagaStep(
            "ValidateOrder",
            async (state, evt, ct) =>
            {
                state.CurrentState = "Validated";
                state.Version++;
                return state;
            },
            compensate: async (state, ct) =>
            {
                state.CurrentState = "Compensated_Validate";
            }));

        When("Validated", typeof(PaymentProcessedEvent), new SagaStep(
            "ProcessPayment",
            async (state, evt, ct) =>
            {
                state.CurrentState = "Paid";
                state.Version++;
                return state;
            },
            compensate: async (state, ct) =>
            {
                state.CurrentState = "Compensated_Payment";
            }));

        When("Paid", new SagaStep(
            "CompleteOrder",
            async (state, evt, ct) =>
            {
                state.CurrentState = "Completed";
                state.Status = SagaStatus.Completed;
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.Version++;
                return state;
            }));
    }
}

public sealed class OrderPlacedSagaEvent
{
    public Guid OrderId { get; init; }
    public decimal Amount { get; init; }
}

public sealed class PaymentProcessedEvent
{
    public Guid OrderId { get; init; }
}

public sealed class SagaTests
{
    [Test]
    public async Task HandleEvent_ExactMatch_Transitions()
    {
        var saga = new OrderFulfillmentSaga();
        var state = new OrderSagaState();

        var result = await saga.HandleEventAsync(state, new OrderPlacedSagaEvent(), CancellationToken.None);

        await Assert.That(result.CurrentState).IsEqualTo("Validated");
        await Assert.That(result.Version).IsEqualTo(1);
    }

    [Test]
    public async Task HandleEvent_NoMatch_ReturnsCurrentState()
    {
        var saga = new OrderFulfillmentSaga();
        var state = new OrderSagaState { CurrentState = "Initial" };

        // 鐢ㄤ笉鍖归厤鐨勪簨浠剁被鍨嬭皟鐢?
        var result = await saga.HandleEventAsync(state, new PaymentProcessedEvent(), CancellationToken.None);

        // 搴旇淇濇寔 "Initial"锛堟病鏈夊尮閰嶇殑杞崲锛?
        await Assert.That(result.CurrentState).IsEqualTo("Initial");
        await Assert.That(result).IsSameReferenceAs(state);
    }

    [Test]
    public async Task HandleEvent_FullWorkflow()
    {
        var saga = new OrderFulfillmentSaga();
        var state = new OrderSagaState();

        // Step 1: Initial 鈫?Validated
        state = await saga.HandleEventAsync(state, new OrderPlacedSagaEvent(), CancellationToken.None);
        await Assert.That(state.CurrentState).IsEqualTo("Validated");

        // Step 2: Validated 鈫?Paid
        state = await saga.HandleEventAsync(state, new PaymentProcessedEvent(), CancellationToken.None);
        await Assert.That(state.CurrentState).IsEqualTo("Paid");

        // Step 3: Paid 鈫?Completed (any event)
        state = await saga.HandleEventAsync(state, new OrderPlacedSagaEvent(), CancellationToken.None);
        await Assert.That(state.CurrentState).IsEqualTo("Completed");
        await Assert.That(state.Status).IsEqualTo(SagaStatus.Completed);
    }

    [Test]
    public async Task Compensate_WithExecutedSteps_RunsCompensationHandlers()
    {
        var saga = new OrderFulfillmentSaga();
        var state = new OrderSagaState();
        state.ExecutedStepKeys.Add("Initial|OrderPlacedSagaEvent");

        await saga.CompensateAsync(state, CancellationToken.None);

        // 琛ュ伩鎵ц宸叉敞鍐岀殑琛ュ伩澶勭悊鍣?鈥?ValidateOrder 姝ラ鐨?compensate 璁剧疆 CurrentState
        await Assert.That(state.CurrentState).IsEqualTo("Compensated_Validate");
    }

    [Test]
    public async Task IsTimedOut_WithinTimeout_ReturnsFalse()
    {
        var saga = new OrderFulfillmentSaga();
        var state = new OrderSagaState();
        var now = state.CreatedAt.AddMinutes(1); // 1鍒嗛挓鍚?

        var isTimedOut = saga.IsTimedOut(state, now, out var steps);

        await Assert.That(isTimedOut).IsFalse();
        await Assert.That(steps).IsEmpty();
    }

    [Test]
    public async Task CompensateAsync_NoCompensationHandlers_Succeeds()
    {
        // 鍒涘缓娌℃湁琛ュ伩鐨?Saga
        var saga = new NoCompensationSaga();
        var state = new OrderSagaState();

        await saga.CompensateAsync(state);
        // 涓嶅簲鎶涘嚭寮傚父
    }

    /// <summary>楠岃瘉 ProcessEventAsync 琛ュ伩鎵€鏈夊凡鎵ц姝ラ锛堣€岄潪浠呭綋鍓嶆楠わ級</summary>
    [Test]
    public async Task ProcessEventAsync_CompensatesAllExecutedSteps()
    {
        var saga = new OrderFulfillmentSaga();
        var state = new OrderSagaState();

        // 鎵嬪姩妯℃嫙宸叉墽琛屾楠わ細Validated 鍜?Paid
        state.ExecutedStepKeys.Add("Initial|OrderPlacedSagaEvent");
        state.ExecutedStepKeys.Add("Validated|PaymentProcessedEvent");
        state.CurrentState = "Paid";

        // 绗笁涓楠や細澶辫触锛團ailingStep锛?
        var failingSaga = new FailingSaga();
        var failingState = new OrderSagaState();
        failingState.ExecutedStepKeys.Add("Initial|OrderPlacedSagaEvent");
        failingState.ExecutedStepKeys.Add("Validated|PaymentProcessedEvent");
        failingState.CurrentState = "Paid";

        // 楠岃瘉澶辫触鏃朵細琛ュ伩宸叉墽琛屾楠わ紝涓旀姏鍑?AggregateException 鍖呭惈鎵€鏈夐噸璇曞け璐?
        var aggEx = await Assert.That(async () =>
        {
            await failingSaga.ProcessEventAsync(failingState, new OrderPlacedSagaEvent { OrderId = Guid.NewGuid(), Amount = 100 });
        }).Throws<AggregateException>();
        foreach (var ex in aggEx!.InnerExceptions)
        {
            await Assert.That(ex).IsTypeOf<InvalidOperationException>();
        }
    }

    [Test]
    public async Task ProcessEventAsync_RecordsSagaCompletedMetric()
    {
        using var listener = new RecordingMeterListener("paldd.saga.completed");
        var saga = new OrderFulfillmentSaga();
        var state = new OrderSagaState { CurrentState = "Paid" };

        await saga.ProcessEventAsync(state, new OrderPlacedSagaEvent(), CancellationToken.None);

        await Assert.That(listener.Measurements).Contains(1);
    }

    private sealed class NoCompensationSaga : Saga<OrderSagaState>
    {
        public NoCompensationSaga()
        {
            When("Initial", new SagaStep(
                "SimpleStep",
                (s, e, ct) => ValueTask.FromResult(s)));
        }
    }

    private sealed class FailingSaga : Saga<OrderSagaState>
    {
        public FailingSaga()
        {
            When("Initial", typeof(OrderPlacedSagaEvent), new SagaStep(
                "ValidateOrder",
                (state, evt, ct) =>
                {
                    state.CurrentState = "Validated";
                    return ValueTask.FromResult(state);
                },
                compensate: (state, ct) =>
                {
                    state.CurrentState = "Compensated_Validate";
                    return ValueTask.CompletedTask;
                }));

            When("Validated", typeof(PaymentProcessedEvent), new SagaStep(
                "ProcessPayment",
                (state, evt, ct) =>
                {
                    state.CurrentState = "Paid";
                    return ValueTask.FromResult(state);
                },
                compensate: (state, ct) =>
                {
                    state.CurrentState = "Compensated_Payment";
                    return ValueTask.CompletedTask;
                }));

            When("Paid", new SagaStep(
                "FailingStep",
                (state, evt, ct) => throw new InvalidOperationException("Step failed"),
                compensate: (state, ct) =>
                {
                    state.CurrentState = "Compensated_Failing";
                    return ValueTask.CompletedTask;
                }));
        }
    }
}

// 鈹€鈹€鈹€ InboxMessage 娴嬭瘯 鈹€鈹€鈹€

public sealed class InboxMessageTests
{
    [Test]
    public async Task NewMessage_HasDefaultValues()
    {
        var msg = new InboxMessage();

        await Assert.That(msg.Id).IsEqualTo(0);
        await Assert.That(msg.MessageId).IsEqualTo("");
        await Assert.That(msg.Status).IsEqualTo(InboxStatus.Pending);
        await Assert.That(msg.ReceivedAt <= DateTimeOffset.UtcNow).IsTrue();
        await Assert.That(msg.ProcessedAt).IsNull();
        await Assert.That(msg.ProcessingStartedAt).IsNull();
    }

    [Test]
    public async Task Message_SetProperties()
    {
        var msg = new InboxMessage
        {
            MessageId = "msg-001",
            ProcessedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        await Assert.That(msg.MessageId).IsEqualTo("msg-001");
        await Assert.That(msg.ProcessedAt).IsNotNull();
        await Assert.That(msg.ProcessedAt!.Value.Year).IsEqualTo(2026);
    }
}
