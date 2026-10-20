using System.Text;
using PalDDD.EventLog;
using PalDDD.Projections.EventLog;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Projections.EventLog.Tests;

public sealed class EventStreamJsonLinesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ExportImport_RoundTripsAuditAndLargePayloadAcrossFlushBoundary(CancellationToken ct)
    {
        var largePayload = new byte[128 * 1024];
        for (var i = 0; i < largePayload.Length; i++)
            largePayload[i] = (byte)(i % 251);

        var events = new[]
        {
            CreateEvent("orders", 1, 1, "OrderCreated", [1, 2, 3], [4, 5], "actor-1", "reason-1"),
            CreateEvent("orders", 2, 2, "OrderPaid", largePayload, [9], "actor-2", "reason-2"),
            CreateEvent("orders", 3, 3, "OrderShipped", [], [], null, null)
        };

        using var output = new MemoryStream();
        await EventStreamJsonLines.ExportAsync(ToAsync(events, ct), output, ct);
        output.Position = 0;

        var imported = new List<EventData>();
        await foreach (var item in EventStreamJsonLines.ImportAsync(output, ct))
            imported.Add(item);

        await Assert.That(imported.Count).IsEqualTo(3);
        await Assert.That(imported[0].EventId).IsEqualTo(events[0].EventId);
        await Assert.That(imported[0].Audit.ActorId).IsEqualTo("actor-1");
        await Assert.That(imported[1].Payload.ToArray()).IsEquivalentTo(largePayload);
        await Assert.That(imported[1].Audit.Reason).IsEqualTo("reason-2");
        await Assert.That(imported[2].Payload.Length).IsEqualTo(0);
        await Assert.That(imported[2].Metadata.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Import_ToleratesCrLfAndBlankLines(CancellationToken ct)
    {
        var evt = CreateEvent("orders", 1, 1, "OrderCreated", [1], [], null, null);
        using var output = new MemoryStream();
        await EventStreamJsonLines.ExportAsync(ToAsync([evt], ct), output, ct);
        var json = Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\n');
        var withNoise = "\r\n" + json.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n\r\n";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(withNoise));
        var imported = new List<EventData>();
        await foreach (var item in EventStreamJsonLines.ImportAsync(input, ct))
            imported.Add(item);

        await Assert.That(imported.Count).IsEqualTo(1);
        await Assert.That(imported[0].EventId).IsEqualTo(evt.EventId);
    }

    [Test]
    public async Task Import_MalformedLine_ThrowsReplayException(CancellationToken ct)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{not-json}\n"));

        var ex = await Assert.That(async () =>
        {
            await foreach (var _ in EventStreamJsonLines.ImportAsync(input, ct))
            {
            }
        }).Throws<EventReplayException>();

        await Assert.That(ex!.Message).Contains("line 1");
    }

    private static async IAsyncEnumerable<RecordedEvent> ToAsync(
        IEnumerable<RecordedEvent> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var evt in events)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return evt;
        }
    }

    private static RecordedEvent CreateEvent(
        string streamName,
        long streamVersion,
        long globalPosition,
        string eventName,
        byte[] payload,
        byte[] metadata,
        string? actorId,
        string? reason)
        => RecordedEvent.Rehydrate(
            streamName,
            streamVersion,
            globalPosition,
            Now,
            PalUlid.New(),
            eventName,
            schemaVersion: 1,
            contentType: "application/json",
            payload,
            metadata,
            new EventAuditMetadata(
                actorId,
                reason,
                actorId is null ? (PalUlid?)null : PalUlid.New(),
                null,
                actorId is null ? null : "00-trace-parent",
                null));
}
