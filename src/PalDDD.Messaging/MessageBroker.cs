// ─────────────────────────────────────────────────────────────
// 📡 IMessageBroker + NullMessageBroker — 消息代理抽象
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging;

/// <summary>批量订阅的单条消息（消息体 + 消费上下文）。</summary>
public readonly record struct ConsumedMessage<TMessage>(TMessage Message, MessageConsumeContext? Context);

// ─────────────────────────────────────────────────────────────
// 跨进程消息代理抽象
// ─────────────────────────────────────────────────────────────

/// <summary>跨进程消息代理抽象 — AOT 安全的消息发布与订阅。</summary>
/// <remarks>
/// ⚠️ <b>投递语义声明（ITM-234；三十八轮 P2 修正 RabbitMQ 描述与实现对齐；
/// 2026-11-04 批次 A 补齐 at-least-once 通路）</b>：<br/>
/// — <b>默认（不携带 <see cref="IMessageAck"/> 的重载）</b>：at-most-once。RabbitMQ 为
///   非持久 exclusive 自删队列 + handler 失败 <c>Nack(requeue:false)</c> 弃置；Kafka 为
///   handler 失败后 offset 照常自动提交、消息不重投。订阅者离线窗口内发布的消息不可达。<br/>
/// — <b>需要 at-least-once</b>：使用携带 <see cref="IMessageAck"/> 的
///   <see cref="SubscribeAsync{TMessage}(Func{TMessage, MessageConsumeContext?, IMessageAck, CancellationToken, ValueTask}, CancellationToken)"/>
///   重载，由 handler 显式 <see cref="IMessageAck.AcknowledgeAsync"/> /
///   <see cref="IMessageAck.RejectAsync"/>。Kafka 侧还须把
///   <c>ConsumerConfig.EnableAutoOffsetStore</c> 设为 <c>false</c>——否则
///   <c>AcknowledgeAsync</c> 为无操作（自动存储语义保持不变）。
///   <para>
///   ⚠️ 2026-11-04 勘正：本段的旧表述建议「关闭 EnableAutoOffsetStore 并在 handler 成功后
///   StoreOffset」——但旧订阅契约不向 handler 暴露 <c>IConsumer</c>，<c>StoreOffset</c>
///   无调用点，照做会让 offset 永不前进：重启后 <c>AutoOffsetReset</c> 决定结果
///   （<c>earliest</c> 全量重放 / <c>latest</c> 静默跳过全部未提交消息）。
///   现由 <see cref="IMessageAck"/> 提供唯一正确入口。
///   </para><br/>
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
    /// <summary>
    /// 是否可安全并发发布（2026-10-19 性能实施轮）。Outbox 的自动升窗只在本属性为
    /// <c>true</c> 时启用；默认 <c>false</c> 保证自定义 broker 保持逐条确认语义。
    /// Kafka 实现仅在 producer 开启幂等时返回 true；RabbitMQ 在单发布 channel 的
    /// publisher confirms 下保序，返回 true。
    /// </summary>
    bool SupportsConcurrentPublish => false;

    /// <summary>
    /// 是否支持原始载荷发布（2026-10-19 性能实施轮）。支持时 Outbox 可直接把持久化的
    /// 字节交给 broker，跳过“反序列化成对象 + 再序列化”的往返；默认 false 保证自定义
    /// broker 走既有对象发布语义。
    /// </summary>
    bool SupportsRawPublish => false;

    /// <summary>
    /// 发布已序列化的原始载荷（字节必须与 <paramref name="descriptor"/> 的 ContentType/
    /// SchemaVersion 一致）。仅当 <see cref="SupportsRawPublish"/> 为 true 时由 Outbox 调用。
    /// </summary>
    ValueTask PublishRawAsync(
        ReadOnlyMemory<byte> payload,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            $"Broker '{GetType().Name}' does not implement raw payload publishing.");

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
    /// <para>
    /// ⚠️ <b>2026-11-04 修复（批次 A 的测试暴露的既有缺陷）</b>：转发 lambda 原为三参
    /// <c>(message, _, token) =&gt; ...</c>，重载决议把这次调用重新绑定到<b>本重载自身</b>
    ///（三参 lambda 不能匹配两参委托，只匹配三参），形成无限递归 → 栈溢出。它长期不可见是
    /// 因为两个内置 broker 都覆写了本成员，DIM 从未被执行；第三方只实现两参重载的 broker
    /// 一旦走 context 重载即崩。现改为两参 lambda，正确转发到两参重载。
    /// </para>
    /// </remarks>
    ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        => SubscribeAsync<TMessage>((message, token) => handler(message, null, token), ct);

    /// <summary>
    /// 异步订阅消息（含消费上下文与<b>消费确认器</b>）—— 批次 A（2026-11-04）：at-least-once 的唯一入口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认实现（DIM）把 <see cref="NoOpMessageAck"/> 传给调用方后转发到三参重载，
    /// 因此<b>不覆写本成员的自定义 broker 仍是 at-most-once</b>（与旧行为一致，不静默升级语义）。
    /// 内置两栈（Kafka/RabbitMQ）均覆写：RabbitMQ 本来已走 manual ack；Kafka 覆写后由 handler
    /// 决定是否存储 offset。
    /// </para>
    /// <para>
    /// <b>不结算时的兜底</b>：handler 正常返回且未调用 <see cref="IMessageAck"/> 的任何方法时，
    /// 实现按各自既有规则收尾（RabbitMQ 自动 ack，Kafka 不存储）。handler 抛异常时沿用既有
    /// 失败语义（记录并继续消费，消息弃置）。需要重投必须在 handler 内显式
    /// <c>RejectAsync(requeue: true)</c>——异常不会自动重投，这一点与 at-most-once 默认一致。
    /// </para>
    /// <para><paramref name="ct"/> 契约同无 ack 重载（参与初始化与消费生命周期——取消即终止消费）。</para>
    /// </remarks>
    ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, IMessageAck, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => SubscribeAsync<TMessage>((message, context, token) => handler(message, context, NoOpMessageAck.Instance, token), ct);

    /// <summary>
    /// 批量订阅（2026-10-21）：复用逐条订阅，按 <paramref name="maxBatchSize"/> 或
    /// <paramref name="maxDelay"/> 先到者组批后交给批量 handler。批内消息按到达顺序；
    /// 每批 flush 后列表被清空复用，handler 不得保留列表引用。单个批 handler 抛异常时
    /// 由实现的错误回调记录并继续下一批（<see cref="MessageBrokerBase"/> 经
    /// OnBatchHandlerFailed 记录；默认 DIM 实现静默继续）。
    /// </summary>
    ValueTask<IAsyncDisposable> SubscribeBatchAsync<TMessage>(
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, CancellationToken, ValueTask> handler,
        int maxBatchSize = 64,
        TimeSpan? maxDelay = null,
        CancellationToken ct = default)
        => MessageBatchPump.StartAsync(this, handler, maxBatchSize, maxDelay, static _ => { }, ct);

    /// <summary>
    /// 批量订阅（含<b>批次级</b>消费确认器）—— 批次 A（2026-11-04）：一个
    /// <see cref="IMessageAck"/> 对应整批交付，不支持批内部分确认。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认实现（DIM）把 <see cref="NoOpMessageAck"/> 传给调用方后转发到无 ack 的批量重载，
    /// 不覆写的自定义 broker 保持旧行为。
    /// </para>
    /// <para>
    /// <b>兜底规则</b>：批量 handler 正常返回且未结算时整批 <c>AcknowledgeAsync</c>；抛异常时
    /// 由实现的错误回调记录（<see cref="MessageBrokerBase"/> 覆写为记 Error）后整批
    /// <c>RejectAsync(requeue: false)</c> 弃置，与逐条路径的 at-most-once 默认一致。需要整批
    /// 重投请在 handler 内显式 <c>RejectAsync(requeue: true)</c>（Kafka 逆序 seek 使重投从批内
    /// 最早一条开始，见 <c>MessageBatchAck</c> 说明）。
    /// </para>
    /// <para>
    /// <b>逐条兜底的移交（v4 批 A，2026-11-07）</b>：批量路径下 broker 的单条兜底结算必须让位
    /// 于批次结算，否则 handler 返回入队时刻就会被提前 ACK（pre-ACK），批次 handler 的成败与
    /// 结算脱钩。泵在把确认器写入组批通道<b>之前</b>经 <c>DeferredAckRegistry</c> 打移交标记，
    /// 逐条兜底据此跳过；若入队本身失败（关停/通道关闭），泵看不到该确认器，broker 仍自行
    /// Nack 兜底。该标记是程序集内部机制，不进公共契约、不影响未走批量路径的调用方。
    /// </para>
    /// <para>其余参数语义同无 ack 的批量重载。</para>
    /// </remarks>
    ValueTask<IAsyncDisposable> SubscribeBatchAsync<TMessage>(
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, IMessageAck, CancellationToken, ValueTask> handler,
        int maxBatchSize = 64,
        TimeSpan? maxDelay = null,
        CancellationToken ct = default)
        => MessageBatchPump.StartAsync(this, handler, maxBatchSize, maxDelay, static _ => { }, ct);
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

    // 单节点模式所有发布都是丢弃：声明 raw 能力让 Outbox 跳过“反序列化 + 再序列化”，
    // 声明并发能力让窗口/批量标记正常启用；实现均为 no-op，无顺序/确认语义。
    bool IMessageBroker.SupportsRawPublish => true;

    bool IMessageBroker.SupportsConcurrentPublish => true;

    ValueTask IMessageBroker.PublishRawAsync(
        ReadOnlyMemory<byte> payload,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct)
        => ValueTask.CompletedTask;

    // A14（S1 轮，2026-10-15）：Debug 日志补 IsEnabled 门控——IPalLogger.Debug 收预构造
    // 字符串，插值在调用点已发生；?. 只判 logger 非空不判级别，单节点模式下每次发布/订阅
    // 白建一个字符串。对齐两个真 broker 的 Z-1 模式（KafkaBroker/RabbitMqBroker 均已门控）。
    ValueTask IMessageBroker.PublishAsync<TMessage>(TMessage message, CancellationToken ct)
    {
        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
            _logger.Debug($"NullMessageBroker: publish of '{typeof(TMessage).Name}' discarded (single-node mode)");
        return ValueTask.CompletedTask;
    }

    ValueTask IMessageBroker.PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct)
    {
        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
            _logger.Debug($"NullMessageBroker: publish of '{descriptor.Name}' discarded (single-node mode)");
        return ValueTask.CompletedTask;
    }

    ValueTask IMessageBroker.PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct)
    {
        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
            _logger.Debug($"NullMessageBroker: publish of '{descriptor.Name}' discarded (single-node mode)");
        return ValueTask.CompletedTask;
    }

    ValueTask<IAsyncDisposable> IMessageBroker.SubscribeAsync<TMessage>(Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct)
    {
        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
            _logger.Debug($"NullMessageBroker: subscribe to '{typeof(TMessage).Name}' ignored (single-node mode)");
        return new ValueTask<IAsyncDisposable>(NullAsyncDisposable.Instance);
    }

    ValueTask<IAsyncDisposable> IMessageBroker.SubscribeBatchAsync<TMessage>(
        Func<IReadOnlyList<ConsumedMessage<TMessage>>, CancellationToken, ValueTask> handler,
        int maxBatchSize,
        TimeSpan? maxDelay,
        CancellationToken ct)
    {
        // 2026-10-21 评审：批量订阅与单条订阅同样补 Debug 门控日志——原实现静默返回，
        // 误注册 NullMessageBroker 的批量链路无日志可查。
        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
            _logger.Debug($"NullMessageBroker: batch subscribe to '{typeof(TMessage).Name}' ignored (single-node mode)");
        return new(NullAsyncDisposable.Instance);
    }

    private sealed class NullAsyncDisposable : IAsyncDisposable
    {
        public static readonly NullAsyncDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
