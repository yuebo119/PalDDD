// ─────────────────────────────────────────────────────────────
// 📦 EventData — 待持久化的事件数据载体
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.EventLog;

// ─────────────────────────────────────────────────────────────
// 待追加的事件数据
// ─────────────────────────────────────────────────────────────

/// <summary>待追加到事件流的事件 payload 与元数据。</summary>
public sealed class EventData
{
    private readonly byte[] _payload;
    private readonly byte[] _metadata;

    /// <summary>创建事件数据。</summary>
    public EventData(
        PalUlid eventId,
        string eventName,
        int schemaVersion,
        string contentType,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> metadata,
        EventAuditMetadata audit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(audit);

        EventId = eventId;
        EventName = eventName;
        SchemaVersion = schemaVersion;
        ContentType = contentType;
        _payload = payload.ToArray();
        _metadata = metadata.ToArray();
        Audit = audit;
    }

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

    /// <summary>
    /// 内部 byte[] 直接引用 — 供 <c>StoredEvent</c> 零拷贝传递使用。<br/>
    /// EventData 构造后数据不可变，因此引用传递安全。
    /// </summary>
    internal byte[] PayloadArray => _payload;

    internal byte[] MetadataArray => _metadata;

    /// <summary>审计与追踪元数据。</summary>
    public EventAuditMetadata Audit { get; }
}
