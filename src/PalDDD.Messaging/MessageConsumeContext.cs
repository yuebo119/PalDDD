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
public sealed class MessageConsumeContext
{
    /// <summary>已解码头字典，或按需解码的委托——单字段承载以保持实例尺寸不增。</summary>
    private readonly object _headersSource;

    /// <summary>按需解码的缓存结果（仅委托形态使用）。</summary>
    private IReadOnlyDictionary<string, string?>? _materializedHeaders;

    /// <summary>构造消费上下文（已解码头，不含 broker 消息 ID）。</summary>
    public MessageConsumeContext(
        string? correlationId,
        string? causationId,
        string? traceParent,
        string? traceState,
        IReadOnlyDictionary<string, string?> headers)
        : this(null, correlationId, causationId, traceParent, traceState, headers)
    {
    }

    /// <summary>构造消费上下文（已解码头，含 broker 消息 ID）。</summary>
    public MessageConsumeContext(
        string? messageId,
        string? correlationId,
        string? causationId,
        string? traceParent,
        string? traceState,
        IReadOnlyDictionary<string, string?> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        MessageId = messageId;
        CorrelationId = correlationId;
        CausationId = causationId;
        TraceParent = traceParent;
        TraceState = traceState;
        _headersSource = headers;
    }

    /// <summary>构造消费上下文（头按需解码；已知追踪字段已提前提取）。</summary>
    internal MessageConsumeContext(
        string? messageId,
        string? correlationId,
        string? causationId,
        string? traceParent,
        string? traceState,
        Func<IReadOnlyDictionary<string, string?>> headersFactory)
    {
        ArgumentNullException.ThrowIfNull(headersFactory);
        MessageId = messageId;
        CorrelationId = correlationId;
        CausationId = causationId;
        TraceParent = traceParent;
        TraceState = traceState;
        _headersSource = headersFactory;
    }

    /// <summary>
    /// broker 消息 ID（Kafka 消息 Key / RabbitMQ BasicProperties.MessageId）；消息未携带时为 null。
    /// Inbox 幂等应以本值为键，而不是业务事件 ID。
    /// </summary>
    public string? MessageId { get; }

    /// <summary>关联 ID（header <c>x-correlation-id</c>；RabbitMQ 路径兜底读 BasicProperties.CorrelationId）；消息未携带时为 null。</summary>
    public string? CorrelationId { get; }

    /// <summary>因果上游消息 ID（header <c>x-causation-id</c>）；消息未携带时为 null。</summary>
    public string? CausationId { get; }

    /// <summary>W3C 追踪父标识（header <c>traceparent</c>）；消息未携带时为 null。</summary>
    public string? TraceParent { get; }

    /// <summary>W3C 追踪状态（header <c>tracestate</c>）；消息未携带时为 null。</summary>
    public string? TraceState { get; }

    /// <summary>全部消息头的字符串只读视图（字节头按 UTF-8 解码，未知值类型解码为 null）；无头时为空字典。</summary>
    /// <remarks>
    /// 2026-10-21：惰性物化不再经 <see cref="Lazy{T}"/> 对象，直接缓存解码结果——每条带
    /// 追踪头的消息少一次 Lazy 分配。并发首次读取可能重复解码一次，结果不可变且等价。
    /// </remarks>
    public IReadOnlyDictionary<string, string?> Headers
    {
        get
        {
            if (_headersSource is IReadOnlyDictionary<string, string?> existing)
                return existing;

            var materialized = _materializedHeaders;
            if (materialized is not null)
                return materialized;

            materialized = ((Func<IReadOnlyDictionary<string, string?>>)_headersSource)();
            _materializedHeaders = materialized;
            return materialized;
        }
    }

    /// <summary>
    /// 从 Broker 原始消息头构造消费上下文。<br/>
    /// 头值支持 UTF-8 字节数组（KafkaBroker/RabbitMqBroker 写侧 <c>CreateHeaders</c> 的编码格式）
    /// 与字符串两种表示，其他类型解码为 null（不猜测编码）。<br/>
    /// <paramref name="correlationId"/> 为兜底值（RabbitMQ 写侧把 correlation 写入 BasicProperties
    /// 而非 header），header 中的 <c>x-correlation-id</c> 优先。<br/>
    /// 无任何头、无兜底 correlation 且无 messageId 时返回 null——消费回调收到 null context
    /// 表示消息未携带追踪元数据与消息 ID。
    /// </summary>
    /// <param name="headers">Broker 原始消息头（值为字节数组或字符串）；可为 null。接受 KVP 序列以同时兼容 Kafka 的 Dictionary 与 RabbitMQ 的 IDictionary（接口间无 IDictionary→IReadOnlyDictionary 转换）。</param>
    /// <param name="correlationId">header 缺失 x-correlation-id 时的兜底关联 ID。</param>
    /// <param name="messageId">broker 消息 ID（Kafka Key / RabbitMQ MessageId）；仅用于 Inbox 幂等键与诊断，不写入 Headers 视图。</param>
    /// <remarks>v9 E5 澄清：头部含<b>未知类型</b>值（非 byte[]/string）时返回非 null 但对应字段为
    /// null 的部分上下文（非全 null——已知类型字段照常填充）；完全无追踪头才返回 null。</remarks>
    public static MessageConsumeContext? FromHeaders(
        IEnumerable<KeyValuePair<string, object?>>? headers,
        string? correlationId = null,
        string? messageId = null)
    {
        if (headers is null && correlationId is null && messageId is null)
            return null;
        if (headers is null)
            return new MessageConsumeContext(messageId, correlationId, null, null, null, EmptyHeaders);

        if (headers is ICollection<KeyValuePair<string, object?>> { Count: 0 })
            return correlationId is null && messageId is null
                ? null
                : new MessageConsumeContext(messageId, correlationId, null, null, null, EmptyHeaders);

        // 闭包只在非空头路径创建；null/空头快路径不承担 display class 分配。
        return FromHeadersCore(headers, correlationId, messageId);
    }

    private static MessageConsumeContext FromHeadersCore(
        IEnumerable<KeyValuePair<string, object?>> headers,
        string? correlationId,
        string? messageId)
    {
        // 只解码四个已知追踪键；其余头在 Headers 被读取时才物化。
        string? headerCorrelation = null;
        string? causationId = null;
        string? traceParent = null;
        string? traceState = null;
        foreach (var (key, value) in headers)
        {
            switch (key)
            {
                case HeaderNames.CorrelationId:
                    headerCorrelation = DecodeHeaderValue(value);
                    break;
                case HeaderNames.CausationId:
                    causationId = DecodeHeaderValue(value);
                    break;
                case HeaderNames.TraceParent:
                    traceParent = DecodeHeaderValue(value);
                    break;
                case HeaderNames.TraceState:
                    traceState = DecodeHeaderValue(value);
                    break;
                default:
                    break;
            }
        }

        return new MessageConsumeContext(
            messageId,
            headerCorrelation ?? correlationId,
            causationId,
            traceParent,
            traceState,
            () => DecodeHeaders(headers));
    }

    private static Dictionary<string, string?> DecodeHeaders(
        IEnumerable<KeyValuePair<string, object?>> headers)
    {
        var decoded = headers is ICollection<KeyValuePair<string, object?>> collection
            ? new Dictionary<string, string?>(collection.Count, StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in headers)
            decoded[key] = DecodeHeaderValue(value);
        return decoded;
    }

    /// <summary>无头形态的空头视图单例——Headers 契约要求非 null，免每条相关消息一个空字典
    ///（EPSL 地毯审计 2026-10-16 A 批 #14）。只读包装，调用方无法经 IReadOnlyDictionary 变异。</summary>
    private static readonly IReadOnlyDictionary<string, string?> EmptyHeaders =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>());

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
