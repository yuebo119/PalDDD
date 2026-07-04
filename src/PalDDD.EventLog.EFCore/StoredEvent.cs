// ─────────────────────────────────────────────────────────────
// 💿 StoredEvent — EF Core 持久化事件实体
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.EventLog;

#pragma warning disable CA1819 // Properties should not return arrays — internal EF Core entity, byte[] is required for native zero-converter mapping


// ─────────────────────────────────────────────────────────────
// EF Core 存储事件实体
// ─────────────────────────────────────────────────────────────

/// <summary>EF Core entity for durable event log entries.</summary>
public sealed class StoredEvent
{
    private StoredEvent()
    {
        StreamName = string.Empty;
        EventName = string.Empty;
        ContentType = string.Empty;
        Payload = [];
        Metadata = [];
    }

    private StoredEvent(
        string streamName,
        long streamVersion,
        long globalPosition,
        DateTimeOffset recordedAt,
        EventData data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(data);

        StreamName = streamName;
        StreamVersion = streamVersion;
        GlobalPosition = globalPosition;
        RecordedAt = recordedAt;
        EventId = data.EventId;
        EventName = data.EventName;
        SchemaVersion = data.SchemaVersion;
        ContentType = data.ContentType;
        Payload = data.PayloadArray;
        Metadata = data.MetadataArray;
        ActorId = data.Audit.ActorId;
        Reason = data.Audit.Reason;
        CorrelationId = data.Audit.CorrelationId;
        CausationId = data.Audit.CausationId;
        TraceParent = data.Audit.TraceParent;
        TraceState = data.Audit.TraceState;
    }

    /// <summary>Create a stored event from append input.</summary>
    public static StoredEvent From(
        string streamName,
        long streamVersion,
        long globalPosition,
        DateTimeOffset recordedAt,
        EventData data)
        => new(streamName, streamVersion, globalPosition, recordedAt, data);

    /// <summary>Stream that owns the event.</summary>
    public string StreamName { get; private set; }

    /// <summary>Zero-based version within the stream.</summary>
    public long StreamVersion { get; private set; }

    /// <summary>Zero-based global append position.</summary>
    public long GlobalPosition { get; private set; }

    /// <summary>UTC timestamp assigned when the event was recorded.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>Stable event identifier.</summary>
    public PalUlid EventId { get; private set; }

    /// <summary>Stable wire event name.</summary>
    public string EventName { get; private set; }

    /// <summary>Wire schema version.</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>Payload content type.</summary>
    public string ContentType { get; private set; }

    /// <summary>Serialized event payload.</summary>
    public byte[] Payload { get; private set; }

    /// <summary>Serialized event metadata.</summary>
    public byte[] Metadata { get; private set; }

    /// <summary>User, service, or process that caused the event.</summary>
    public string? ActorId { get; private set; }

    /// <summary>Human-readable reason for the state transition.</summary>
    public string? Reason { get; private set; }

    /// <summary>Cross-message correlation identifier.</summary>
    public PalUlid? CorrelationId { get; private set; }

    /// <summary>Identifier of the command or event that caused this event.</summary>
    public PalUlid? CausationId { get; private set; }

    /// <summary>W3C traceparent captured from the append context.</summary>
    public string? TraceParent { get; private set; }

    /// <summary>W3C tracestate captured from the append context.</summary>
    public string? TraceState { get; private set; }

    /// <summary>Rehydrate the public recorded event model — 零拷贝 byte[] 引用传递（P2）</summary>
    public RecordedEvent ToRecordedEvent()
        => RecordedEvent.RehydrateFromBytes(
            StreamName,
            StreamVersion,
            GlobalPosition,
            RecordedAt,
            EventId,
            EventName,
            SchemaVersion,
            ContentType,
            Payload,      // 引用传递，零拷贝
            Metadata,     // 引用传递，零拷贝
            new EventAuditMetadata(ActorId, Reason, CorrelationId, CausationId, TraceParent, TraceState));
}

#pragma warning restore CA1819
