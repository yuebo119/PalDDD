using PalDDD.EventLog;
using System.Runtime.InteropServices;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.EventLog.Tests;

/// <summary>EventData 所有权转移入口（2026-10-20 性能轮）。</summary>
public sealed class EventDataOwnershipTests
{
    [Test]
    public async Task FromOwned_AliasesPayloadAndMetadata()
    {
        var payload = new byte[] { 1, 2, 3 };
        var metadata = new byte[] { 4, 5 };

        var data = EventData.FromOwned(
            PalUlid.New(), "probe.event.v1", 1, "application/json",
            payload, metadata, EventAuditMetadata.Empty);

        await Assert.That(TryGetExactArray(data.Payload, out var payloadArray)).IsTrue();
        await Assert.That(TryGetExactArray(data.Metadata, out var metadataArray)).IsTrue();
        await Assert.That(ReferenceEquals(payloadArray, payload)).IsTrue();
        await Assert.That(ReferenceEquals(metadataArray, metadata)).IsTrue();

        payload[0] = 9;
        await Assert.That(data.Payload.Span[0]).IsEqualTo((byte)9);
    }

    [Test]
    public async Task Constructor_CopiesPayloadAndMetadata()
    {
        var payload = new byte[] { 1, 2, 3 };
        var metadata = new byte[] { 4, 5 };

        var data = new EventData(
            PalUlid.New(), "probe.event.v1", 1, "application/json",
            payload, metadata, EventAuditMetadata.Empty);

        payload[0] = 9;
        metadata[0] = 9;
        await Assert.That(data.Payload.Span[0]).IsEqualTo((byte)1);
        await Assert.That(data.Metadata.Span[0]).IsEqualTo((byte)4);
    }

    private static bool TryGetExactArray(ReadOnlyMemory<byte> memory, out byte[]? array)
    {
        if (MemoryMarshal.TryGetArray(memory, out var segment)
            && segment.Array is { } value
            && segment.Offset == 0
            && segment.Count == value.Length)
        {
            array = value;
            return true;
        }

        array = null;
        return false;
    }
}
