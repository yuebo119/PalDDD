// ─────────────────────────────────────────────────────────────
// 📦 MessagePublishContext — 发布追踪上下文
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging;

/// <summary>消息代理发布所携带的跨消息关联与 W3C 追踪上下文。</summary>
/// <param name="ExtraHeaders">
/// ADR-039 / 审计 A3：调用方自定义业务头（租户/版本等）。<c>null</c>（默认）表示无额外头——
/// 默认路径零分配（不构造字典）。两 broker 的写侧按需追加；键名冲突时<b>框架头优先</b>
/// （traceparent/tracestate/correlation-id/causation-id 不可被覆盖）。
/// </param>
public readonly record struct MessagePublishContext(
    PalUlid? CorrelationId,
    PalUlid? CausationId,
    string? TraceParent,
    string? TraceState,
    IReadOnlyDictionary<string, string?>? ExtraHeaders = null)
{
    /// <summary>空发布上下文，供无关联元数据的调用方使用。</summary>
    public static MessagePublishContext Empty { get; } = new(null, null, null, null);
}
