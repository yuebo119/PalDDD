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

    /// <summary>发布消息（泛型）— 查找描述符后转发到传输核心。</summary>
    public ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
    {
        var descriptor = MessageCatalog.Find(typeof(TMessage))
            ?? throw new InvalidOperationException(
                $"Message type '{typeof(TMessage).FullName}' is not registered in MessageCatalog.");

        return PublishAsync(message!, descriptor, PalUlid.New(), MessagePublishContext.Empty, ct);
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

    /// <summary>异步订阅消息 — 子类实现传输核心。</summary>
    public abstract ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default);
}
