namespace PalDDD.Projections.EventLog.Tests;

using PalDDD.EventLog;
using PalDDD.Projections.EventLog;
using PalDDD.Serialization;
using PalDDD.Serialization.Json;
using PalDDD.Testing;
using System.Diagnostics;
using System.Text.Json.Serialization;

[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class EventLogReplayJsonContext : JsonSerializerContext;

public sealed class EventLogReplaySourceTests
{
    private static readonly string[] DefaultPositionValues = ["0", "1"];
    [Test]
    public async Task ReadAsync_DeserializesStreamEventsInStreamVersionOrder(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var descriptor = CreateDescriptor();
        var first = new OrderPlaced(Guid.Parse("22b66804-55d3-42e9-86f5-a8951062d127"));
        var second = new OrderPlaced(Guid.Parse("aa522e51-9d25-4d5d-b978-8d88e988d6a7"));
        await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent(first, serializer, descriptor),
                CreateEvent(second, serializer, descriptor)
            ],
            cancellationToken);
        var source = new EventLogReplaySource<OrderPlaced>(log, serializer, descriptor);

        var events = await ReadAllAsync(
            source.ReadAsync("ordering-order-1", cancellationToken));

        await Assert.That(events.Select(e => e.Position)).IsEquivalentTo(DefaultPositionValues);
        await Assert.That(events.Select(e => e.Message)).IsEquivalentTo(new[] { first, second });
        foreach (var e in events)
            await Assert.That(e.SourceName).IsEqualTo("ordering-order-1");
    }

    [Test]
    public async Task ProjectionRebuilder_RebuildsProjectionFromEventLogStream(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var descriptor = CreateDescriptor();
        await log.AppendAsync(
            "ordering-order-2",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent(new OrderPlaced(Guid.Parse("8954afae-e50e-4569-b8d3-44cc8ad41a10")), serializer, descriptor),
                CreateEvent(new OrderPlaced(Guid.Parse("019988ed-396d-70e9-9cd0-5cc35b67c177")), serializer, descriptor)
            ],
            cancellationToken);
        var checkpointStore = new InMemoryProjectionCheckpointStore();
        var handler = new CountingProjectionHandler();
        var processor = new ProjectionProcessor<OrderPlaced>(handler, checkpointStore);
        var source = new EventLogReplaySource<OrderPlaced>(log, serializer, descriptor);
        var rebuilder = new ProjectionRebuilder<OrderPlaced>(
            handler.ProjectionName,
            "ordering-order-2",
            source,
            checkpointStore,
            processor);

        var replayed = await rebuilder.RebuildAsync(cancellationToken);

        await Assert.That(replayed).IsEqualTo(2);
        await Assert.That(handler.OrderIds).IsEquivalentTo(new[]
        {
            Guid.Parse("8954afae-e50e-4569-b8d3-44cc8ad41a10"),
            Guid.Parse("019988ed-396d-70e9-9cd0-5cc35b67c177")
        });
        await Assert.That(await checkpointStore.GetAsync(
            "order-summary",
            "ordering-order-2",
            "1",
            cancellationToken)).IsNotNull();
    }

    [Test]
    public async Task ReadAsync_EmitsReplayReadActivity(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var log = new InMemoryEventLog();
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var descriptor = CreateDescriptor();
        await log.AppendAsync(
            "ordering-order-3",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent(new OrderPlaced(Guid.Parse("90153724-45d6-4495-858d-76961b92d9d5")), serializer, descriptor),
                CreateEvent(new OrderPlaced(Guid.Parse("c3ad58c5-0188-4e70-bb9d-3326a89a4e4b")), serializer, descriptor)
            ],
            cancellationToken);
        var source = new EventLogReplaySource<OrderPlaced>(log, serializer, descriptor);

        var events = await ReadAllAsync(source.ReadAsync("ordering-order-3", cancellationToken));

        await Assert.That(listener.StoppedActivities.Any(a => a.OperationName == "Event Replay Read")).IsTrue();
        // Use read_count > 0 to distinguish from error activities on same stream
        var activity = listener.StoppedActivities.First(a =>
            a.OperationName == "Event Replay Read" &&
            a.GetTagItem("pal.replay.read_count") is 2 &&
            string.Equals(a.GetTagItem("pal.replay.source") as string, "ordering-order-3", StringComparison.Ordinal));
        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(activity.GetTagItem("pal.replay.source")).IsEqualTo("ordering-order-3");
        await Assert.That(activity.GetTagItem("pal.replay.event")).IsEqualTo("orders.order-placed.v1");
        await Assert.That(activity.GetTagItem("pal.replay.message_type")).IsEqualTo(typeof(OrderPlaced).FullName);
        await Assert.That(activity.GetTagItem("pal.replay.read_count")).IsEqualTo(2);
    }

    [Test]
    public async Task ReadAsync_RecordsReplayReadMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.replay.read");
        var log = new InMemoryEventLog();
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var descriptor = CreateDescriptor();
        await log.AppendAsync(
            "ordering-order-4",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent(new OrderPlaced(Guid.Parse("640b4852-f35b-4940-9f01-5b244862bfb1")), serializer, descriptor),
                CreateEvent(new OrderPlaced(Guid.Parse("2cf38ce1-0887-47fe-96e1-60786d8c59fd")), serializer, descriptor)
            ],
            cancellationToken);
        var source = new EventLogReplaySource<OrderPlaced>(log, serializer, descriptor);

        _ = await ReadAllAsync(source.ReadAsync("ordering-order-4", cancellationToken));

        await Assert.That(listener.Measurements).Contains(2);
    }

    [Test]
    public async Task ReadAsync_PreservesReplayAuditMetadata(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var descriptor = CreateDescriptor();
        var audit = new EventAuditMetadata(
            "user-123",
            "rebuild order projection",
            Guid.Parse("ae9ce6d5-1263-43a0-a7f2-263f15d57e64"),
            Guid.Parse("9ea92652-d96f-4700-8d99-a92a6beb87d7"),
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            "tenant=ordering");
        await log.AppendAsync(
            "ordering-order-7",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent(
                    new OrderPlaced(Guid.Parse("f9c04e27-2d6d-4c1b-87f7-21afdfaa31e4")),
                    serializer,
                    descriptor,
                    audit)
            ],
            cancellationToken);
        var source = new EventLogReplaySource<OrderPlaced>(log, serializer, descriptor);

        var replayEvents = await ReadAllAsync(
            source.ReadAsync("ordering-order-7", cancellationToken));
        await Assert.That(replayEvents).HasSingleItem();
        var replayEvent = replayEvents[0];

        await Assert.That(replayEvent.Audit.ActorId).IsEqualTo("user-123");
        await Assert.That(replayEvent.Audit.Reason).IsEqualTo("rebuild order projection");
        await Assert.That(replayEvent.Audit.CorrelationId).IsEqualTo(Guid.Parse("ae9ce6d5-1263-43a0-a7f2-263f15d57e64"));
        await Assert.That(replayEvent.Audit.CausationId).IsEqualTo(Guid.Parse("9ea92652-d96f-4700-8d99-a92a6beb87d7"));
        await Assert.That(replayEvent.Audit.TraceParent).IsEqualTo("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        await Assert.That(replayEvent.Audit.TraceState).IsEqualTo("tenant=ordering");
    }

    [Test]
    public async Task ReadAsync_WithMismatchedContract_FailsFast(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var descriptor = CreateDescriptor();
        await log.AppendAsync(
            "ordering-order-3",
            ExpectedStreamVersion.NoStream,
            [
                new EventData(
                    Guid.NewGuid(),
                    "orders.order-cancelled.v1",
                    descriptor.SchemaVersion,
                    descriptor.ContentType,
                    serializer.Serialize(new OrderPlaced(Guid.Parse("6d022fbc-20b6-48f7-b9af-7ca5e35e45ec")), descriptor),
                    metadata: ReadOnlyMemory<byte>.Empty,
                    EventAuditMetadata.Empty)
            ],
            cancellationToken);
        var source = new EventLogReplaySource<OrderPlaced>(log, serializer, descriptor);

        var exception = await Assert.That(async () =>
        {
            await foreach (var _ in source.ReadAsync("ordering-order-3", cancellationToken))
            {
            }
        }).Throws<EventReplayException>();

        await Assert.That(exception!.Message).Contains("does not match descriptor name");
        await Assert.That(exception!.Message).Contains("ordering-order-3");
    }

    [Test]
    public async Task ReadAsync_WithMismatchedContract_MarksReplayActivityAsError(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var log = new InMemoryEventLog();
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var descriptor = CreateDescriptor();
        await log.AppendAsync(
            "ordering-order-5",
            ExpectedStreamVersion.NoStream,
            [
                new EventData(
                    Guid.NewGuid(),
                    "orders.order-cancelled.v1",
                    descriptor.SchemaVersion,
                    descriptor.ContentType,
                    serializer.Serialize(new OrderPlaced(Guid.Parse("9d07edb8-675b-44d0-975d-8523af240dc4")), descriptor),
                    metadata: ReadOnlyMemory<byte>.Empty,
                    EventAuditMetadata.Empty)
            ],
            cancellationToken);
        var source = new EventLogReplaySource<OrderPlaced>(log, serializer, descriptor);

        await Assert.That(async () =>
        {
            await foreach (var _ in source.ReadAsync("ordering-order-5", cancellationToken))
            {
            }
        }).Throws<EventReplayException>();

        await Assert.That(listener.StoppedActivities.Any(a => a.OperationName == "Event Replay Read")).IsTrue();
        var activity = listener.StoppedActivities.First(a => a.OperationName == "Event Replay Read" && (string?)a.GetTagItem("pal.replay.source") == "ordering-order-5");
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.StatusDescription).Contains("does not match descriptor name");
    }

    [Test]
    public async Task ReadAsync_WhenPayloadCannotBeDeserialized_RecordsReplayFailedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.replay.failed");
        var log = new InMemoryEventLog();
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var descriptor = CreateDescriptor();
        await log.AppendAsync(
            "ordering-order-6",
            ExpectedStreamVersion.NoStream,
            [
                new EventData(
                    Guid.NewGuid(),
                    descriptor.Name,
                    descriptor.SchemaVersion,
                    descriptor.ContentType,
                    "not-json"u8.ToArray(),
                    metadata: ReadOnlyMemory<byte>.Empty,
                    EventAuditMetadata.Empty)
            ],
            cancellationToken);
        var source = new EventLogReplaySource<OrderPlaced>(log, serializer, descriptor);

        await Assert.That(async () =>
        {
            await foreach (var _ in source.ReadAsync("ordering-order-6", cancellationToken))
            {
            }
        }).Throws<EventReplayException>();

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task ReadAsync_WhenPayloadCannotBeDeserialized_ThrowsStructuredException(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();
        var serializer = new JsonMessageSerializer(MessageCatalog.Empty);
        var descriptor = CreateDescriptor();
        await log.AppendAsync(
            "ordering-order-4",
            ExpectedStreamVersion.NoStream,
            [
                new EventData(
                    Guid.NewGuid(),
                    descriptor.Name,
                    descriptor.SchemaVersion,
                    descriptor.ContentType,
                    "not-json"u8.ToArray(),
                    metadata: ReadOnlyMemory<byte>.Empty,
                    EventAuditMetadata.Empty)
            ],
            cancellationToken);
        var source = new EventLogReplaySource<OrderPlaced>(log, serializer, descriptor);

        var exception = await Assert.That(async () =>
        {
            await foreach (var _ in source.ReadAsync("ordering-order-4", cancellationToken))
            {
            }
        }).Throws<EventReplayException>();

        await Assert.That(exception!.Message).Contains("ordering-order-4");
        await Assert.That(exception!.Message.ToUpperInvariant()).Contains("COULD NOT BE DESERIALIZED");
        await Assert.That(exception!.InnerException).IsNotNull();
    }

    private static MessageDescriptor CreateDescriptor()
        => MessageDescriptor.Create(
            EventLogReplayJsonContext.Default.OrderPlaced,
            name: "orders.order-placed.v1");

    private static EventData CreateEvent(
        OrderPlaced message,
        JsonMessageSerializer serializer,
        MessageDescriptor descriptor,
        EventAuditMetadata? audit = null)
        => new(
            Guid.NewGuid(),
            descriptor.Name,
            descriptor.SchemaVersion,
            descriptor.ContentType,
            serializer.Serialize(message, descriptor),
            metadata: ReadOnlyMemory<byte>.Empty,
            audit ?? EventAuditMetadata.Empty);

    private static async Task<List<ReplayEvent<OrderPlaced>>> ReadAllAsync(
        IAsyncEnumerable<ReplayEvent<OrderPlaced>> events)
    {
        var result = new List<ReplayEvent<OrderPlaced>>();
        await foreach (var @event in events)
            result.Add(@event);

        return result;
    }
}

public sealed record OrderPlaced(Guid OrderId);

internal sealed class CountingProjectionHandler : IProjectionHandler<OrderPlaced>
{
    public List<Guid> OrderIds { get; } = [];

    public string ProjectionName => "order-summary";

    public ValueTask ProjectAsync(OrderPlaced message, ProjectionContext context, CancellationToken ct = default)
    {
        OrderIds.Add(message.OrderId);
        return ValueTask.CompletedTask;
    }
}
