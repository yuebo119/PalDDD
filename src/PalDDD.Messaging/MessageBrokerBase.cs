// ─────────────────────────────────────────────────────────────
// 📡 MessageBrokerBase — Broker 适配器共享基类
// ─────────────────────────────────────────────────────────────
//
// 💡 KafkaBroker 与 RabbitMqBroker 共享相同的泛型 Publish 转发逻辑
//   ｜ 和 Find-or-throw 契约。提取基类保证两个 Broker 行为一致，
//   ｜ 子类只实现传输相关的 PublishCoreAsync / SubscribeCoreAsync。
//
// ✅ AOT 安全：零反射。
// ─────────────────────────────────────────────────────────────

using PalDDD.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging;

/// <summary>
/// Broker 适配器共享基类。<br/>
/// 封装泛型 Publish 转发 + 消息目录查找契约，子类只实现传输核心。
/// </summary>
public abstract class MessageBrokerBase : IMessageBroker
{
    /// <summary>序列化器（子类共享）。</summary>
    protected IMessageSerializer Serializer { get; }

    /// <summary>消息目录（子类共享）。</summary>
    protected IMessageCatalog MessageCatalog { get; }

    protected MessageBrokerBase(IMessageSerializer serializer, IMessageCatalog messageCatalog)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(messageCatalog);
        Serializer = serializer;
        MessageCatalog = messageCatalog;
    }

    /// <inheritdoc />
    /// <remarks>默认 false；Kafka/RabbitMQ 等实现按能力覆写。</remarks>
    public virtual bool SupportsConcurrentPublish => false;

    /// <inheritdoc />
    /// <remarks>默认 false；支持直发持久化字节的实现覆写。</remarks>
    public virtual bool SupportsRawPublish => false;

    /// <inheritdoc />
    public virtual ValueTask PublishRawAsync(
        ReadOnlyMemory<byte> payload,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            $"Broker '{GetType().Name}' does not implement raw payload publishing.");

    /// <summary>发布消息（泛型）— 查找描述符后转发到传输核心。</summary>
    public ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
    {
        // ITM-179 修复（二十九轮）：泛型入口统一 null 校验——修复前引用类型 null 仅
        // `message!` 空断言放行，null 被序列化为 "null" 负载发布到 broker，消费端
        // 反序列化为 null 后 handler NRE 远离入口。非泛型核心重载（KafkaBroker/
        // RabbitMqBroker 的 PublishAsync 实现）已有 ThrowIfNull，唯此泛型入口缺。
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        var descriptor = MessageCatalog.Find(typeof(TMessage))
            ?? throw new InvalidOperationException(
                $"Message type '{typeof(TMessage).FullName}' is not registered in MessageCatalog.");

        return PublishAsync(message, descriptor, PalUlid.New(), MessagePublishContext.Empty, ct);
    }

    /// <summary>发布消息（非泛型重载）— 转发到带 context 的重载。</summary>
    public ValueTask PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        CancellationToken ct = default)
        => PublishAsync(message, descriptor, messageId, MessagePublishContext.Empty, ct);

    /// <summary>发布消息（非泛型，含跨上下文追踪元数据）— 子类实现传输核心。</summary>
    public abstract ValueTask PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct = default);

    /// <summary>
    /// 批量订阅（2026-10-21）：默认组批泵，复用逐条订阅，按数量或时间界组批。
    /// handler 异常交由 <see cref="OnBatchHandlerFailed"/> 处理（默认实现无操作，不记录日志），
    /// 然后继续下一批。
    /// </summary>
    public virtual ValueTask<IAsyncDisposable> SubscribeBatchAsync<TMessage>(
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, CancellationToken, ValueTask> handler,
        int maxBatchSize = 64,
        TimeSpan? maxDelay = null,
        CancellationToken ct = default)
        => MessageBatchPump.StartAsync(this, handler, maxBatchSize, maxDelay, OnBatchHandlerFailed, ct);

    /// <summary>批量 handler 抛异常时的回调（默认无操作；broker 实现覆写记录日志）。</summary>
    protected virtual void OnBatchHandlerFailed(Exception ex)
    {
    }

    /// <summary>异步订阅消息 — 子类实现传输核心。</summary>
    public abstract ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default);

    /// <summary>
    /// 异步订阅消息（含消费上下文）— 子类实现传输核心。<br/>
    /// 消息未携带任何追踪头时 context 为 null；无 context 的旧重载默认适配到此重载（零破坏）。
    /// </summary>
    public abstract ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default);
}
