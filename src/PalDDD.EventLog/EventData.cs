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

    /// <summary>创建事件数据（防御性复制 payload 与 metadata）。</summary>
    public EventData(
        PalUlid eventId,
        string eventName,
        int schemaVersion,
        string contentType,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> metadata,
        EventAuditMetadata audit)
        : this(eventId, eventName, schemaVersion, contentType, payload.ToArray(), metadata.ToArray(), audit)
    {
    }

    private EventData(
        PalUlid eventId,
        string eventName,
        int schemaVersion,
        string contentType,
        byte[] payload,
        byte[] metadata,
        EventAuditMetadata audit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(audit);

        EventId = eventId;
        EventName = eventName;
        SchemaVersion = schemaVersion;
        ContentType = contentType;
        _payload = payload;
        _metadata = metadata;
        Audit = audit;
    }

    /// <summary>
    /// 以所有权转移方式创建事件数据（2026-10-20 性能轮）：直接引用传入数组，不再复制。
    /// <para>
    /// 契约：调用方交出数组所有权后不得再修改或复用该数组。序列化器产出的精确数组
    /// （<c>IMessageSerializer.SerializeToArray</c>）可直接传入，省去一次整载荷复制；
    /// 来自池化缓冲的视图不得走本入口。需要保留原数组所有权时请使用
    /// <see cref="EventData(PalUlid, string, int, string, ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, EventAuditMetadata)"/>。
    /// </para>
    /// </summary>
    public static EventData FromOwned(
        PalUlid eventId,
        string eventName,
        int schemaVersion,
        string contentType,
        byte[] payload,
        byte[] metadata,
        EventAuditMetadata audit)
        => new(eventId, eventName, schemaVersion, contentType, payload, metadata, audit);

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
    /// <para>
    /// v65 P3（别名共享声明）：本属性返回的是构造期 <c>ToArray()</c> 得到的<b>同一数组实例</b>
    /// （非防御性拷贝，零拷贝语义所需），与 <see cref="Payload"/> 共享底层缓冲区。数组本身可变，
    /// "不可变"是<b>公开 API 契约</b>（<see cref="Payload"/> 暴露为 <see cref="ReadOnlyMemory{T}"/>），
    /// 非运行时强制——internal 消费方（StoredEvent 等）<b>不得</b>写此数组；一旦外泄写入会同时
    /// 污染所有引用同一 EventData 的路径。需要外部传递时请走 <see cref="Payload"/> 只读视图。
    /// </para>
    /// </summary>
    internal byte[] PayloadArray => _payload;

    internal byte[] MetadataArray => _metadata;

    /// <summary>审计与追踪元数据。</summary>
    public EventAuditMetadata Audit { get; }
}
