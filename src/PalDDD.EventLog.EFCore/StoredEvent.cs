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
        EventIdBin = data.EventId.ToByteArray();
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

    /// <summary>
    /// 16 字节二进制 EventId（M4 双写列）。旧行为 null 的存量行回退到 <see cref="EventId"/> 文本列。
    /// </summary>
    public byte[]? EventIdBin { get; private set; }

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
    public RecordedEvent ToRecordedEvent(bool useBinaryEventId = false)
        => CreateRecordedEvent(
            StreamName,
            StreamVersion,
            GlobalPosition,
            RecordedAt,
            EventId,
            EventIdBin,
            useBinaryEventId,
            requireBinaryEventId: false,
            EventName,
            SchemaVersion,
            ContentType,
            Payload,
            Metadata,
            ActorId,
            Reason,
            CorrelationId,
            CausationId,
            TraceParent,
            TraceState);

    /// <summary>共享转换核心：实体完整读取与二进制投影共用，避免审计字段逻辑分叉。</summary>
    internal static RecordedEvent CreateRecordedEvent(
        string streamName,
        long streamVersion,
        long globalPosition,
        DateTimeOffset recordedAt,
        PalUlid eventId,
        byte[]? eventIdBin,
        bool useBinaryEventId,
        bool requireBinaryEventId,
        string eventName,
        int schemaVersion,
        string contentType,
        byte[] payload,
        byte[] metadata,
        string? actorId,
        string? reason,
        PalUlid? correlationId,
        PalUlid? causationId,
        string? traceParent,
        string? traceState)
    {
        if (useBinaryEventId && requireBinaryEventId && eventIdBin is not { Length: 16 })
            throw new InvalidOperationException(
                $"Event at global position {globalPosition} (stream '{streamName}') has no 16-byte event_id_bin; "
                + "run the M4 binary EventId backfill before enabling binary reads.");

        // 2026-10-19 性能实施轮：审计字段全空时复用单例（对齐 Dapper/PalORM/JSONL 导入同款），
        // 回放 1M 事件少 1M 个 EventAuditMetadata 对象。
        var resolvedEventId = useBinaryEventId && eventIdBin is { Length: 16 }
            ? PalUlid.New(eventIdBin)
            : eventId;

        var audit = actorId is null
            && reason is null
            && correlationId is null
            && causationId is null
            && traceParent is null
            && traceState is null
                ? EventAuditMetadata.Empty
                : new EventAuditMetadata(actorId, reason, correlationId, causationId, traceParent, traceState);

        return RecordedEvent.RehydrateFromBytes(
            streamName,
            streamVersion,
            globalPosition,
            recordedAt,
            resolvedEventId,
            eventName,
            schemaVersion,
            contentType,
            payload,      // 引用传递，零拷贝
            metadata,     // 引用传递，零拷贝
            audit);
    }
}

#pragma warning restore CA1819
