// ─────────────────────────────────────────────────────────────
// 📡 IMessageBroker + NullMessageBroker — 消息代理抽象
// ─────────────────────────────────────────────────────────────

using PalDDD.Core.Logging;
using PalDDD.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging;

// ─────────────────────────────────────────────────────────────
// 跨进程消息代理抽象
// ─────────────────────────────────────────────────────────────

/// <summary>跨进程消息代理抽象 — AOT 安全的消息发布与订阅。</summary>
/// <remarks>
/// ⚠️ <b>投递语义声明（ITM-234；三十八轮 P2 修正 RabbitMQ 描述与实现对齐）</b>：<br/>
/// — <b>RabbitMQ</b>：at-most-once（非持久 exclusive 自删队列 + handler 失败 nack 弃置；
///   订阅者离线窗口内发布的消息不可达）。需持久化/at-least-once 请自行实现 IMessageBroker
///   （durable 队列 + DLX + requeue 策略）。<br/>
/// — <b>Kafka</b>：at-most-once（handler 失败的 offset 照常自动提交，消息不重投）。
///   若需 at-least-once，请关闭 EnableAutoOffsetStore 并在 handler 成功后 StoreOffset。<br/>
/// — <b>Null</b>：不发送任何消息（单节点测试用）。<br/>
/// ⚠️ <b>顺序性声明（三十八轮 P2）</b>：本抽象不保证消息顺序——Kafka 分区键为每消息唯一
/// Ulid（均匀散列到随机分区），RabbitMQ fanout 同样无序。需要分区内有序的场景须自行实现
/// IMessageBroker 并以聚合 Id 作分区键。<br/>
/// ⚠️ <b>多订阅者语义声明（三十八轮 P2）</b>：同一消息类型的多个订阅者，RabbitMQ 为广播
/// （每订阅独立匿名队列各收全量）；Kafka 同一 GroupId 下为负载均衡（各订阅分得部分分区，
/// 各收部分消息）——需要 Kafka 广播语义请为每个订阅使用独立 GroupId。<br/>
/// 消费方幂等性由应用层保证（Inbox/Idempotency Store）。
/// </remarks>
public interface IMessageBroker
{
    /// <summary>发布消息（泛型，编译时已知类型）。⚠️ 不保证顺序——见接口 remarks 顺序性声明。</summary>
    ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default);

    /// <summary>发布消息（非泛型，使用预注册消息描述符和外部消息 ID）</summary>
    ValueTask PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct = default);

    /// <summary>发布消息（非泛型，使用预注册消息描述符、外部消息 ID 和跨上下文追踪元数据）</summary>
    ValueTask PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct = default)
        => PublishAsync(message, descriptor, messageId, ct);

    /// <summary>异步订阅消息 — 完全异步，零 Task.Run，零死锁风险</summary>
    /// <remarks>
    /// 📐 <b>ct 契约（二十五轮 P2-5 统一）</b>：<paramref name="ct"/> 参与订阅初始化与
    /// <b>消费生命周期</b>——取消即终止消费（解绑消费者，飞行中的 handler 收到取消信号）；
    /// 不是"仅初始化 token"。订阅的完整释放仍以返回的句柄 Dispose 为准。
    /// </remarks>
    ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default);

    /// <summary>异步订阅消息（含消费上下文）— 从消息头还原 correlation/causation/traceparent/tracestate。</summary>
    /// <remarks>
    /// 默认实现（DIM）适配到无 context 的重载，context 恒为 null；KafkaBroker/RabbitMqBroker
    /// 覆写此成员以提供从消息头提取的真实 <see cref="MessageConsumeContext"/>（八轮评审：补追踪头消费端断链）。
    /// <paramref name="ct"/> 契约同无 context 重载（参与初始化与消费生命周期——取消即终止消费）。
    /// </remarks>
    ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        => SubscribeAsync<TMessage>((message, _, token) => handler(message, null, token), ct);
}

/// <summary>
/// 空实现 — 单节点部署时使用，不发送任何消息。<br/>
/// Debug 级日志记录发布/订阅调用，便于诊断"消息未发送"问题（如误注册 NullMessageBroker）。
/// </summary>
public sealed class NullMessageBroker : IMessageBroker
{
    private readonly IPalLogger<NullMessageBroker>? _logger;

    /// <summary>创建 NullMessageBroker，可选注入日志器。</summary>
    public NullMessageBroker(IPalLogger<NullMessageBroker>? logger = null)
        => _logger = logger;

    ValueTask IMessageBroker.PublishAsync<TMessage>(TMessage message, CancellationToken ct)
    {
        _logger?.Debug($"NullMessageBroker: publish of '{typeof(TMessage).Name}' discarded (single-node mode)");
        return ValueTask.CompletedTask;
    }

    ValueTask IMessageBroker.PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct)
    {
        _logger?.Debug($"NullMessageBroker: publish of '{descriptor.Name}' discarded (single-node mode)");
        return ValueTask.CompletedTask;
    }

    ValueTask IMessageBroker.PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct)
    {
        _logger?.Debug($"NullMessageBroker: publish of '{descriptor.Name}' discarded (single-node mode)");
        return ValueTask.CompletedTask;
    }

    ValueTask<IAsyncDisposable> IMessageBroker.SubscribeAsync<TMessage>(Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct)
    {
        _logger?.Debug($"NullMessageBroker: subscribe to '{typeof(TMessage).Name}' ignored (single-node mode)");
        return new ValueTask<IAsyncDisposable>(NullAsyncDisposable.Instance);
    }

    private sealed class NullAsyncDisposable : IAsyncDisposable
    {
        public static readonly NullAsyncDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
