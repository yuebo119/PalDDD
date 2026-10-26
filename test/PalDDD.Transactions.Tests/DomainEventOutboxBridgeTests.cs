using PalDDD.Core;
using PalDDD.Serialization;
using System.Text.Json.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions.Tests;

/// <summary>
/// F4（2026-10-21 架构评审）：栈无关域事件桥——Dapper/PalORM 用户不再需要自行复制
/// catalog 查找、序列化、追踪字段与事件清理逻辑。
/// </summary>
public sealed class DomainEventOutboxBridgeTests
{
    private static readonly MessageDescriptor Descriptor = MessageDescriptor.Create(
        BridgeJsonContext.Default.BridgeEvent, "test.bridge.v1", schemaVersion: 1);

    [Test]
    public async Task PersistAsync_WritesAllEventsAndClearsAggregate()
    {
        var store = new InMemoryOutboxStore();
        var bridge = new DomainEventOutboxBridge(new SingleCatalog(Descriptor), new PassthroughSerializer());
        var aggregate = new BridgeEntity { Id = 1 };
        aggregate.AddEvent(new BridgeEvent());
        aggregate.AddEvent(new BridgeEvent());

        var written = await bridge.PersistAsync([aggregate], store, CancellationToken.None);

        await Assert.That(written).IsEqualTo(2);
        await Assert.That(aggregate.HasDomainEvents).IsFalse();
        var pending = await store.GetPendingMessagesAsync(10, 100, CancellationToken.None);
        await Assert.That(pending.Count).IsEqualTo(2);
        await Assert.That(pending[0].Type).IsEqualTo(Descriptor.Name);
        await Assert.That(pending[0].Payload.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task PersistAsync_WhenStoreThrows_KeepsEventsForRetry()
    {
        var bridge = new DomainEventOutboxBridge(new SingleCatalog(Descriptor), new PassthroughSerializer());
        var aggregate = new BridgeEntity { Id = 2 };
        aggregate.AddEvent(new BridgeEvent());

        await Assert.That(async () => await bridge.PersistAsync([aggregate], new ThrowingOutboxStore(), CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(aggregate.HasDomainEvents).IsTrue();
    }

    [Test]
    public async Task BuildMessage_UnregisteredEvent_Throws()
    {
        var bridge = new DomainEventOutboxBridge(new SingleCatalog(Descriptor), new PassthroughSerializer());
        var other = new UnregisteredEvent();

        await Assert.That(() => { bridge.BuildMessage(other); }).Throws<InvalidOperationException>();
    }

    private sealed class SingleCatalog(MessageDescriptor descriptor) : IMessageCatalog
    {
        public IReadOnlyList<MessageDescriptor> Descriptors => [descriptor];

        public MessageDescriptor? Find(string name) => name == descriptor.Name ? descriptor : null;

        public MessageDescriptor? Find(string name, int schemaVersion)
            => name == descriptor.Name && schemaVersion == descriptor.SchemaVersion ? descriptor : null;

        public MessageDescriptor? Find(Type type) => type == descriptor.ClrType ? descriptor : null;
    }

    private sealed class PassthroughSerializer : IMessageSerializer
    {
        public string ContentType => "application/json";

        public ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message, MessageDescriptor? descriptor = null)
            => "payload"u8.ToArray();

        public ReadOnlyMemory<byte> Serialize(object message, MessageDescriptor descriptor)
            => "payload"u8.ToArray();

        public object? Deserialize(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => null;

        public T? Deserialize<T>(ReadOnlySpan<byte> payload, MessageDescriptor descriptor) => default;
    }

    private sealed class ThrowingOutboxStore : IPalOutboxStore
    {
        public ValueTask AddMessageAsync(OutboxMessage message, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated store failure");

        public ValueTask<int> AddMessagesAsync(IReadOnlyList<OutboxMessage> messages, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated store failure");

        public ValueTask<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask<IReadOnlyList<OutboxMessage>> LeasePendingMessagesAsync(int batchSize, string owner, TimeSpan leaseDuration, int maxRetryCount, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public ValueTask MarkProcessedAsync(OutboxMessage message, DateTimeOffset processedAt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask MarkDeadAsync(OutboxMessage message, string failureReason, DateTimeOffset deadAt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask ReleaseForRetryAsync(OutboxMessage message, string failureReason, DateTimeOffset nextAttemptAt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<int> RequeueDeadAsync(PalUlid messageId, DateTimeOffset nextAttemptAt, string retriedBy, CancellationToken ct) => ValueTask.FromResult(0);

        public ValueTask<int> SaveChangesAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }
}

internal sealed class BridgeEntity : Entity
{
    public int Id { get; set; }

    public void AddEvent(DomainEvent domainEvent) => RaiseEvent(domainEvent);
}

internal sealed class BridgeEvent : DomainEvent, IDomainEvent
{
    public static string EventName => "test.bridge.v1";
}

internal sealed class UnregisteredEvent : DomainEvent, IDomainEvent
{
    public static string EventName => "test.unregistered.v1";
}

[JsonSerializable(typeof(BridgeEvent))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext;
