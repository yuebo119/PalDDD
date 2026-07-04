namespace PalDDD.EventLog.Tests;

using PalDDD.Testing;
using System.Diagnostics;
using System.Text;
using PalUlid = ByteAether.Ulid.Ulid;

public sealed class EventLogTests
{
    private static readonly long[] DefaultPositions = [0, 1];
    private static readonly string[] OrderEventNames = ["orders.order-submitted.v1", "orders.order-paid.v1"];
    private static readonly string[] DefaultStreamNames = ["ordering-order-1", "billing-payment-1"];
    [Test]
    public async Task AppendAsync_RecordsStreamVersionsAndGlobalPositions(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();

        var first = await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent("orders.order-submitted.v1", "one")],
            cancellationToken);
        var second = await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.Exact(0),
            [CreateEvent("orders.order-paid.v1", "two")],
            cancellationToken);

        await Assert.That(first.FirstStreamVersion).IsEqualTo(0);
        await Assert.That(first.LastStreamVersion).IsEqualTo(0);
        await Assert.That(first.FirstGlobalPosition).IsEqualTo(0);
        await Assert.That(second.FirstStreamVersion).IsEqualTo(1);
        await Assert.That(second.LastStreamVersion).IsEqualTo(1);
        await Assert.That(second.FirstGlobalPosition).IsEqualTo(1);
    }

    [Test]
    public async Task AppendAsync_EmitsEventLogAppendActivity(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var log = new InMemoryEventLog();

        var result = await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent("orders.order-submitted.v1", "one"),
                CreateEvent("orders.order-paid.v1", "two")
            ],
            cancellationToken);

        await Assert.That(listener.StoppedActivities.Any(a => a.OperationName == "EventLog Append")).IsTrue();
        var activity = listener.StoppedActivities.First(a =>
            a.OperationName == "EventLog Append" &&
            string.Equals(a.GetTagItem("pal.eventlog.stream") as string, "ordering-order-1", StringComparison.Ordinal));
        await Assert.That(result.FirstStreamVersion).IsEqualTo(0);
        await Assert.That(activity.GetTagItem("pal.eventlog.stream")).IsEqualTo("ordering-order-1");
        await Assert.That(activity.GetTagItem("pal.eventlog.event_count")).IsEqualTo(2);
        await Assert.That(activity.GetTagItem("pal.eventlog.first_stream_version")).IsEqualTo(0L);
        await Assert.That(activity.GetTagItem("pal.eventlog.last_stream_version")).IsEqualTo(1L);
        await Assert.That(activity.GetTagItem("pal.eventlog.first_global_position")).IsEqualTo(0L);
        await Assert.That(activity.GetTagItem("pal.eventlog.last_global_position")).IsEqualTo(1L);
    }

    [Test]
    public async Task AppendAsync_RecordsEventLogAppendedMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.eventlog.appended");
        var log = new InMemoryEventLog();

        await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent("orders.order-submitted.v1", "one"),
                CreateEvent("orders.order-paid.v1", "two")
            ],
            cancellationToken);

        await Assert.That(listener.Measurements).Contains(2);
    }

    [Test]
    public async Task AppendAsync_WithStaleExpectedVersion_ThrowsConcurrencyException(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();
        await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent("orders.order-submitted.v1", "one")],
            cancellationToken);

        var exception = await Assert.That(() =>
            log.AppendAsync(
                "ordering-order-1",
                ExpectedStreamVersion.NoStream,
                [CreateEvent("orders.order-paid.v1", "two")],
                cancellationToken).AsTask()).Throws<EventStreamConcurrencyException>();

        await Assert.That(exception!.StreamName).IsEqualTo("ordering-order-1");
        await Assert.That(exception!.ActualVersion).IsEqualTo(0);
    }

    [Test]
    public async Task ReadStreamAsync_ReplaysEventsInStreamVersionOrder(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();
        await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent("orders.order-submitted.v1", "one"),
                CreateEvent("orders.order-paid.v1", "two")
            ],
            cancellationToken);

        var events = await ReadAllAsync(log.ReadStreamAsync("ordering-order-1", cancellationToken: cancellationToken));

        await Assert.That(events.Select(e => e.StreamVersion)).IsEquivalentTo(DefaultPositions);
        await Assert.That(events.Select(e => e.EventName)).IsEquivalentTo(OrderEventNames);
    }

    [Test]
    public async Task ReadStreamAsync_EmitsEventLogReadStreamActivity(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var log = new InMemoryEventLog();
        await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [
                CreateEvent("orders.order-submitted.v1", "one"),
                CreateEvent("orders.order-paid.v1", "two")
            ],
            cancellationToken);

        var events = await ReadAllAsync(log.ReadStreamAsync(
            "ordering-order-1",
            fromVersion: 1,
            cancellationToken: cancellationToken));

        await Assert.That(listener.StoppedActivities.Any(a => a.OperationName == "EventLog ReadStream")).IsTrue();
        var activity = listener.StoppedActivities.First(a =>
            a.OperationName == "EventLog ReadStream" &&
            string.Equals(a.GetTagItem("pal.eventlog.stream") as string, "ordering-order-1", StringComparison.Ordinal));
        await Assert.That(events).HasSingleItem();
        await Assert.That(activity.GetTagItem("pal.eventlog.stream")).IsEqualTo("ordering-order-1");
        await Assert.That(activity.GetTagItem("pal.eventlog.from_stream_version")).IsEqualTo(1L);
        await Assert.That(activity.GetTagItem("pal.eventlog.read_count")).IsEqualTo(1);
    }

    [Test]
    public async Task ReadAllAsync_ReplaysEventsInGlobalAppendOrder(CancellationToken cancellationToken)
    {
        var log = new InMemoryEventLog();
        await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent("orders.order-submitted.v1", "one")],
            cancellationToken);
        await log.AppendAsync(
            "billing-payment-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent("billing.payment-authorized.v1", "two")],
            cancellationToken);

        var events = await ReadAllAsync(log.ReadAllAsync(cancellationToken: cancellationToken));

        await Assert.That(events.Select(e => e.GlobalPosition)).IsEquivalentTo(DefaultPositions);
        await Assert.That(events.Select(e => e.StreamName)).IsEquivalentTo(DefaultStreamNames);
    }

    [Test]
    public async Task ReadAllAsync_EmitsEventLogReadAllActivity(CancellationToken cancellationToken)
    {
        using var listener = new RecordingActivityListener();
        var log = new InMemoryEventLog();
        await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent("orders.order-submitted.v1", "one")],
            cancellationToken);
        await log.AppendAsync(
            "billing-payment-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent("billing.payment-authorized.v1", "two")],
            cancellationToken);

        var events = await ReadAllAsync(log.ReadAllAsync(
            fromPosition: 1,
            cancellationToken: cancellationToken));

        await Assert.That(listener.StoppedActivities.Any(a => a.OperationName == "EventLog ReadAll")).IsTrue();
        var activity = listener.StoppedActivities.First(a =>
            a.OperationName == "EventLog ReadAll" &&
            a.GetTagItem("pal.eventlog.from_global_position") is 1L);
        await Assert.That(events).HasSingleItem();
        await Assert.That(activity.GetTagItem("pal.eventlog.from_global_position")).IsEqualTo(1L);
        await Assert.That(activity.GetTagItem("pal.eventlog.read_count")).IsEqualTo(1);
    }

    [Test]
    public async Task ReadAllAsync_RecordsEventLogReadMetric(CancellationToken cancellationToken)
    {
        using var listener = new RecordingMeterListener("paldd.eventlog.read");
        var log = new InMemoryEventLog();
        await log.AppendAsync(
            "ordering-order-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent("orders.order-submitted.v1", "one")],
            cancellationToken);
        await log.AppendAsync(
            "billing-payment-1",
            ExpectedStreamVersion.NoStream,
            [CreateEvent("billing.payment-authorized.v1", "two")],
            cancellationToken);

        _ = await ReadAllAsync(log.ReadAllAsync(
            fromPosition: 1,
            cancellationToken: cancellationToken));

        await Assert.That(listener.Measurements).Contains(1);
    }

    [Test]
    public async Task AuditMetadata_CapturesCurrentTraceContext()
    {
        using var activity = new Activity("event-log-test");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.TraceStateString = "tenant=ordering";
        activity.Start();

        var metadata = EventAuditMetadata.Capture(
            actorId: "user-123",
            reason: "submit order",
            correlationId: PalUlid.New(Guid.Parse("ae9ce6d5-1263-43a0-a7f2-263f15d57e64")),
            causationId: PalUlid.New(Guid.Parse("9ea92652-d96f-4700-8d99-a92a6beb87d7")));

        await Assert.That(metadata.ActorId).IsEqualTo("user-123");
        await Assert.That(metadata.Reason).IsEqualTo("submit order");
        await Assert.That(metadata.CorrelationId).IsEqualTo(PalUlid.New(Guid.Parse("ae9ce6d5-1263-43a0-a7f2-263f15d57e64")));
        await Assert.That(metadata.CausationId).IsEqualTo(PalUlid.New(Guid.Parse("9ea92652-d96f-4700-8d99-a92a6beb87d7")));
        await Assert.That(metadata.TraceParent).StartsWith("00-");
        await Assert.That(metadata.TraceState).IsEqualTo("tenant=ordering");
    }

    private static EventData CreateEvent(string name, string payload)
        => new(
            PalUlid.New(),
            name,
            schemaVersion: 1,
            "application/json",
            Encoding.UTF8.GetBytes(payload),
            metadata: ReadOnlyMemory<byte>.Empty,
            EventAuditMetadata.Empty);

    private static async Task<List<RecordedEvent>> ReadAllAsync(IAsyncEnumerable<RecordedEvent> events)
    {
        var result = new List<RecordedEvent>();
        await foreach (var @event in events)
            result.Add(@event);

        return result;
    }
}
