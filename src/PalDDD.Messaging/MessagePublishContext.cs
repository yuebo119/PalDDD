// ─────────────────────────────────────────────────────────────
// 📦 MessagePublishContext — 发布追踪上下文
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging;

/// <summary>消息代理发布所携带的跨消息关联与 W3C 追踪上下文。</summary>
public readonly record struct MessagePublishContext(
    PalUlid? CorrelationId,
    PalUlid? CausationId,
    string? TraceParent,
    string? TraceState)
{
    /// <summary>空发布上下文，供无关联元数据的调用方使用。</summary>
    public static MessagePublishContext Empty { get; } = new(null, null, null, null);
}
