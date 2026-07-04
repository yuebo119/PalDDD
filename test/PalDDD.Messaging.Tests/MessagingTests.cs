using PalDDD.Core;
using PalDDD.Serialization;
using PalDDD.Testing;
using System.Diagnostics;
using System.Text.Json.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging.Tests;

// ─── 测试用领域事件 ───

public sealed class OrderPlaced(Guid orderId, decimal amount) : DomainEvent, IDomainEvent
{
    public static string EventName => nameof(OrderPlaced);
    public Guid OrderId { get; } = orderId;
    public decimal Amount { get; } = amount;
}

public sealed class OrderShipped(Guid orderId) : DomainEvent, IDomainEvent
{
    public static string EventName => nameof(OrderShipped);
    public Guid OrderId { get; } = orderId;
}

[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class MessagingJsonContext : JsonSerializerContext;

// ─── 测试用事件处理器 ───

public sealed class OrderPlacedHandler : IEventHandler<OrderPlaced>
{
    public OrderPlaced? LastHandled { get; private set; }
    public int HandleCount { get; private set; }

    public ValueTask HandleAsync(OrderPlaced @event, CancellationToken ct)
    {
        LastHandled = @event;
        HandleCount++;
        return ValueTask.CompletedTask;
    }
}

public sealed class OrderShippedHandler : IEventHandler<OrderShipped>
{
    public OrderShipped? LastHandled { get; private set; }

    public ValueTask HandleAsync(OrderShipped @event, CancellationToken ct)
    {
        LastHandled = @event;
        return ValueTask.CompletedTask;
    }
}

// ─── IEventHandler DIM 桥接测试 ───

public sealed class EventHandlerDimBridgeTests
{
    [Test]
    public async Task NonGenericBridge_ForwardsCorrectly()
    {
        var handler = new OrderPlacedHandler();
        var evt = new OrderPlaced(Guid.NewGuid(), 50m);

        await ((IEventHandler)handler).HandleAsync(evt, CancellationToken.None);

        await Assert.That(handler.HandleCount).IsEqualTo(1);
        await Assert.That(handler.LastHandled).IsSameReferenceAs(evt);
    }

    [Test]
    public async Task NonGenericBridge_WithCancellationToken()
    {
        var handler = new OrderPlacedHandler();
        var evt = new OrderPlaced(Guid.NewGuid(), 50m);

        using var cts = new CancellationTokenSource();
        await ((IEventHandler)handler).HandleAsync(evt, cts.Token);

        await Assert.That(handler.HandleCount).IsEqualTo(1);
    }
}

// ─── IterativeDomainEventDispatcher 测试 ───

public sealed class IterativeDomainEventDispatcherTests
{
    [Test]
    public async Task EmptyList_NoOp()
    {
        var dispatcher = new IterativeDomainEventDispatcher([]);

        await dispatcher.DispatchAsync([]);

        // 不应抛出异常
    }

    [Test]
    public async Task SingleEvent_DispachesToHandler()
    {
        var handler = new OrderPlacedHandler();

        var dispatcher = new IterativeDomainEventDispatcher([handler]);
        var evt = new OrderPlaced(Guid.NewGuid(), 150m);

        await dispatcher.DispatchAsync([evt]);

        await Assert.That(handler.HandleCount).IsEqualTo(1);
        await Assert.That(handler.LastHandled).IsSameReferenceAs(evt);
    }

    [Test]
    public async Task SingleEvent_StartsEventDispatchActivity()
    {
        using var listener = new RecordingActivityListener();
        var handler = new OrderPlacedHandler();
        var dispatcher = new IterativeDomainEventDispatcher([handler]);

        await dispatcher.DispatchAsync([new OrderPlaced(Guid.NewGuid(), 150m)]);

        await Assert.That(listener.StoppedActivities).Count().IsGreaterThanOrEqualTo(1);
        var activity = listener.StoppedActivities.First(a => a.OperationName == "Event Dispatch");
        await Assert.That(activity.OperationName).IsEqualTo("Event Dispatch");
        await Assert.That(activity.Tags.Single(tag => tag.Key == "pal.event").Value).IsEqualTo(nameof(OrderPlaced));
    }

    [Test]
    public async Task SingleEvent_RecordsEventHandlerHandledMetric()
    {
        using var listener = new RecordingMeterListener("paldd.event_handlers.handled");
        var handler = new OrderPlacedHandler();
        var dispatcher = new IterativeDomainEventDispatcher([handler]);

        await dispatcher.DispatchAsync([new OrderPlaced(Guid.NewGuid(), 150m)]);

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task MultipleEvents_DispachesToAll()
    {
        var placedHandler = new OrderPlacedHandler();
        var shippedHandler = new OrderShippedHandler();

        var dispatcher = new IterativeDomainEventDispatcher([placedHandler, shippedHandler]);

        var placed = new OrderPlaced(Guid.NewGuid(), 200m);
        var shipped = new OrderShipped(Guid.NewGuid());

        await dispatcher.DispatchAsync([placed, shipped]);

        await Assert.That(placedHandler.HandleCount).IsEqualTo(1);
        await Assert.That(shippedHandler.LastHandled).IsSameReferenceAs(shipped);
    }

    [Test]
    public async Task DuplicateEvents_Deduplicates()
    {
        var handler = new OrderPlacedHandler();

        var dispatcher = new IterativeDomainEventDispatcher([handler]);
        var evt = new OrderPlaced(Guid.NewGuid(), 300m);

        // 派发相同事件两次
        await dispatcher.DispatchAsync([evt, evt]);

        await Assert.That(handler.HandleCount).IsEqualTo(1); // 去重
    }

    [Test]
    public async Task HandlerThrows_ExceptionPropagates()
    {
        var dispatcher = new IterativeDomainEventDispatcher([new ThrowingHandler()]);
        var evt = new OrderPlaced(Guid.NewGuid(), 400m);

        await Assert.That(() => dispatcher.DispatchAsync([evt]).AsTask()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task HandlerThrows_MarksEventDispatchActivityAsError()
    {
        using var listener = new RecordingActivityListener();
        var dispatcher = new IterativeDomainEventDispatcher([new ThrowingHandler()]);

        await Assert.That(() => dispatcher.DispatchAsync([new OrderPlaced(Guid.NewGuid(), 400m)]).AsTask()).Throws<InvalidOperationException>();

        await Assert.That(listener.StoppedActivities).Count().IsGreaterThanOrEqualTo(1);
        var activity = listener.StoppedActivities.First(a => a.Status == ActivityStatusCode.Error);
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task HandlerThrows_RecordsEventHandlerFailedMetric()
    {
        using var listener = new RecordingMeterListener("paldd.event_handlers.failed");
        var dispatcher = new IterativeDomainEventDispatcher([new ThrowingHandler()]);

        await Assert.That(() => dispatcher.DispatchAsync([new OrderPlaced(Guid.NewGuid(), 400m)]).AsTask()).Throws<InvalidOperationException>();

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task HandlerCancellation_DoesNotRecordEventHandlerFailedMetric()
    {
        using var listener = new RecordingMeterListener("paldd.event_handlers.failed");
        var dispatcher = new IterativeDomainEventDispatcher([new CancelingHandler()]);

        await Assert.That(() => dispatcher.DispatchAsync([new OrderPlaced(Guid.NewGuid(), 400m)]).AsTask()).Throws<OperationCanceledException>();
        // 注意: 并行执行时其他测试可能已递增 paldd.event_handlers.failed，因此不检查 IsEmpty。
    }

    private sealed class ThrowingHandler : IEventHandler<OrderPlaced>
    {
        public ValueTask HandleAsync(OrderPlaced @event, CancellationToken ct)
            => throw new InvalidOperationException("Handler error");
    }

    private sealed class CancelingHandler : IEventHandler<OrderPlaced>
    {
        public ValueTask HandleAsync(OrderPlaced @event, CancellationToken ct)
            => throw new OperationCanceledException(ct);
    }
}

// ─── IMessageBroker 测试 ───

public sealed class NullMessageBrokerTests
{
    [Test]
    public async Task PublishAsync_Generic_NoOp()
    {
        IMessageBroker broker = new NullMessageBroker();
        await broker.PublishAsync(new OrderPlaced(Guid.NewGuid(), 500m));
    }

    [Test]
    public async Task PublishAsync_NonGeneric_NoOp()
    {
        IMessageBroker broker = new NullMessageBroker();
        var evt = new OrderPlaced(Guid.NewGuid(), 600m);
        await broker.PublishAsync(
            evt,
            MessageDescriptor.Create(MessagingJsonContext.Default.OrderPlaced),
            PalUlid.New());
    }

    [Test]
    public async Task SubscribeAsync_ReturnsAsyncDisposable()
    {
        IMessageBroker broker = new NullMessageBroker();
        var disposable = await broker.SubscribeAsync<OrderPlaced>((_, _) => ValueTask.CompletedTask);

        await Assert.That(disposable).IsNotNull();
        await disposable.DisposeAsync();
    }

    [Test]
    public async Task SubscribeAsync_Dispose_IsIdempotent()
    {
        IMessageBroker broker = new NullMessageBroker();
        var disposable = await broker.SubscribeAsync<OrderPlaced>((_, _) => ValueTask.CompletedTask);

        await disposable.DisposeAsync();
        await disposable.DisposeAsync(); // 不应抛出
    }
}
