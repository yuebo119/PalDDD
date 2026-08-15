// ─────────────────────────────────────────────────────────────
// 📦 MessageConsumeContext — 消费追踪上下文
// ─────────────────────────────────────────────────────────────
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PalDDD.Messaging;

// ─────────────────────────────────────────────────────────────
// 消费侧追踪上下文（发布侧 MessagePublishContext 的消费端镜像）
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 消费侧消息上下文 — 从 Broker 消息头还原的跨消息关联与 W3C 追踪元数据的只读视图。<br/>
/// 发布侧通过 <see cref="MessagePublishContext"/> 写入的追踪头，由
/// <see cref="IMessageBroker.SubscribeAsync{TMessage}(Func{TMessage, MessageConsumeContext?, CancellationToken, ValueTask}, CancellationToken)"/>
/// 的消费上下文重载在消费端还原为该类型，用于延续跨上下文 trace 与因果链。
/// </summary>
public sealed class MessageConsumeContext(
    string? correlationId,
    string? causationId,
    string? traceParent,
    string? traceState,
    IReadOnlyDictionary<string, string?> headers)
{
    /// <summary>关联 ID（header <c>x-correlation-id</c>；RabbitMQ 路径兜底读 BasicProperties.CorrelationId）；消息未携带时为 null。</summary>
    public string? CorrelationId { get; } = correlationId;

    /// <summary>因果上游消息 ID（header <c>x-causation-id</c>）；消息未携带时为 null。</summary>
    public string? CausationId { get; } = causationId;

    /// <summary>W3C 追踪父标识（header <c>traceparent</c>）；消息未携带时为 null。</summary>
    public string? TraceParent { get; } = traceParent;

    /// <summary>W3C 追踪状态（header <c>tracestate</c>）；消息未携带时为 null。</summary>
    public string? TraceState { get; } = traceState;

    /// <summary>全部消息头的字符串只读视图（字节头按 UTF-8 解码，未知值类型解码为 null）；无头时为空字典。</summary>
    public IReadOnlyDictionary<string, string?> Headers { get; } = headers ?? throw new ArgumentNullException(nameof(headers));

    /// <summary>
    /// 从 Broker 原始消息头构造消费上下文。<br/>
    /// 头值支持 UTF-8 字节数组（KafkaBroker/RabbitMqBroker 写侧 <c>CreateHeaders</c> 的编码格式）
    /// 与字符串两种表示，其他类型解码为 null（不猜测编码）。<br/>
    /// <paramref name="correlationId"/> 为兜底值（RabbitMQ 写侧把 correlation 写入 BasicProperties
    /// 而非 header），header 中的 <c>x-correlation-id</c> 优先。<br/>
    /// 无任何头且无兜底 correlation 时返回 null——消费回调收到 null context 表示消息未携带追踪元数据。
    /// </summary>
    /// <param name="headers">Broker 原始消息头（值为字节数组或字符串）；可为 null。接受 KVP 序列以同时兼容 Kafka 的 Dictionary 与 RabbitMQ 的 IDictionary（接口间无 IDictionary→IReadOnlyDictionary 转换）。</param>
    /// <param name="correlationId">header 缺失 x-correlation-id 时的兜底关联 ID。</param>
    public static MessageConsumeContext? FromHeaders(
        IEnumerable<KeyValuePair<string, object?>>? headers,
        string? correlationId = null)
    {
        var decoded = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                decoded[key] = DecodeHeaderValue(value);
        }

        if (decoded.Count == 0 && correlationId is null)
            return null;

        decoded.TryGetValue(HeaderNames.CorrelationId, out var headerCorrelation);
        decoded.TryGetValue(HeaderNames.CausationId, out var causationId);
        decoded.TryGetValue(HeaderNames.TraceParent, out var traceParent);
        decoded.TryGetValue(HeaderNames.TraceState, out var traceState);

        return new MessageConsumeContext(
            headerCorrelation ?? correlationId,
            causationId,
            traceParent,
            traceState,
            decoded);
    }

    /// <summary>已知追踪头键名 — 与 KafkaBroker/RabbitMqBroker 写侧 CreateHeaders 的键完全一致。</summary>
    [SuppressMessage("Design", "CA1034", Justification = "常量分组嵌套是 CA1034 公认例外：键名与上下文类型强内聚，提升为顶层反而弱化归属并扩大 API 面。")]
    public static class HeaderNames
    {
        /// <summary>W3C 追踪父标识键名。</summary>
        public const string TraceParent = "traceparent";

        /// <summary>W3C 追踪状态键名。</summary>
        public const string TraceState = "tracestate";

        /// <summary>关联 ID 键名（Kafka 写侧使用；RabbitMQ 走 BasicProperties.CorrelationId）。</summary>
        public const string CorrelationId = "x-correlation-id";

        /// <summary>因果上游消息 ID 键名。</summary>
        public const string CausationId = "x-causation-id";
    }

    // P2 修复（八轮评审）：头值解码——写侧以 UTF-8 字节写入（见两个 Broker 的 AddHeader），
    // 字符串直传（宽容非本框架写入的头）；其他类型不猜测编码，按缺失处理。
    private static string? DecodeHeaderValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes),
        string text => text,
        _ => null
    };
}
