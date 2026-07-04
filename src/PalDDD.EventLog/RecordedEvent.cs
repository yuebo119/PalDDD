// ─────────────────────────────────────────────────────────────
// 📋 RecordedEvent — 已持久化事件的只读投影（零拷贝读取）
// ─────────────────────────────────────────────────────────────
//
// 💡 双构造路径分工：写入路径与零拷贝读取路径，详 docs/decisions/006-recorded-event-zero-copy-read.md
//   ｜ - EventData 构造（写入）：从 EventData 拷贝 payload/metadata
//   ｜ - RehydrateFromBytes（读取）：直接引用 byte[]，每事件省 2 次分配，对标 P0
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// 已记录的事件 — 从存储读取后的只读视图
// ─────────────────────────────────────────────────────────────

/// <summary>追加写事件流中已记录的事件。</summary>
public sealed class RecordedEvent
{
    private readonly byte[] _payload;
    private readonly byte[] _metadata;

    // ── EventData 构造路径（写入时使用，详见 ADR-006）──

    internal RecordedEvent(
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
        _payload = data.Payload.ToArray();
        _metadata = data.Metadata.ToArray();
        Audit = data.Audit;
    }

    // ── 零拷贝读取构造路径（从存储读取时使用，跳过 EventData 中转，详见 ADR-006）──

    internal RecordedEvent(
        string streamName,
        long streamVersion,
        long globalPosition,
        DateTimeOffset recordedAt,
        PalUlid eventId,
        string eventName,
        int schemaVersion,
        string contentType,
        byte[] payload,
        byte[] metadata,
        EventAuditMetadata audit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(audit);

        StreamName = streamName;
        StreamVersion = streamVersion;
        GlobalPosition = globalPosition;
        RecordedAt = recordedAt;
        EventId = eventId;
        EventName = eventName;
        SchemaVersion = schemaVersion;
        ContentType = contentType;
        _payload = payload;      // 引用赋值，零拷贝
        _metadata = metadata;    // 引用赋值，零拷贝
        Audit = audit;
    }

    /// <summary>从持久化存储重水化已记录事件（公共 API — ReadOnlyMemory 入参）。</summary>
    public static RecordedEvent Rehydrate(
        string streamName,
        long streamVersion,
        long globalPosition,
        DateTimeOffset recordedAt,
        PalUlid eventId,
        string eventName,
        int schemaVersion,
        string contentType,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> metadata,
        EventAuditMetadata audit)
    {
        // 通过 EventData 中转（保守路径，对外 API 保持稳定）
        return RehydrateFromBytes(
            streamName, streamVersion, globalPosition, recordedAt,
            eventId, eventName, schemaVersion, contentType,
            payload.ToArray(), metadata.ToArray(), audit);
    }

    /// <summary>
    /// 零拷贝重水化路径 — 从存储读取时直接传入 byte[] 引用。<br/>
    /// 跳过 <c>EventData</c> 中转，消除 2 次 <c>ToArray()</c> 拷贝。
    /// </summary>
    internal static RecordedEvent RehydrateFromBytes(
        string streamName,
        long streamVersion,
        long globalPosition,
        DateTimeOffset recordedAt,
        PalUlid eventId,
        string eventName,
        int schemaVersion,
        string contentType,
        byte[] payload,
        byte[] metadata,
        EventAuditMetadata audit)
        => new(
            streamName, streamVersion, globalPosition, recordedAt,
            eventId, eventName, schemaVersion, contentType,
            payload, metadata, audit);

    /// <summary>拥有该事件的流名称。</summary>
    public string StreamName { get; }

    /// <summary>流内零基版本号。</summary>
    public long StreamVersion { get; }

    /// <summary>全局零基追加位置。</summary>
    public long GlobalPosition { get; }

    /// <summary>事件记录时分配的 UTC 时间戳。</summary>
    public DateTimeOffset RecordedAt { get; }

    /// <summary>稳定的事件标识符。</summary>
    public PalUlid EventId { get; }

    /// <summary>稳定的 wire 事件名。</summary>
    public string EventName { get; }

    /// <summary>wire schema 版本号。</summary>
    public int SchemaVersion { get; }

    /// <summary>payload 内容类型。</summary>
    public string ContentType { get; }

    /// <summary>已序列化的事件 payload。</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    /// <summary>已序列化的事件元数据。</summary>
    public ReadOnlyMemory<byte> Metadata => _metadata;

    /// <summary>审计与追踪元数据。</summary>
    public EventAuditMetadata Audit { get; }
}
