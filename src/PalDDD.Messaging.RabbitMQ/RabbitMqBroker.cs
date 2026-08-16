using PalDDD.Core.Logging;
using PalDDD.Serialization;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging.RabbitMQ;

// ─────────────────────────────────────────────────────────────
// RabbitMQ 消息代理适配器
// ─────────────────────────────────────────────────────────────

/// <summary>RabbitMQ 消息代理适配器 — 实现 <see cref="IMessageBroker"/></summary>
/// <remarks>
/// 使用 RabbitMQ.Client 7.x，支持异步发布和基于 AsyncEventingBasicConsumer 的订阅。<br/>
/// 消息按事件类型名路由到同名 Exchange（Fanout 模式）。<br/>
/// SubscribeAsync 完全异步——零 Task.Run，零 sync-over-async 死锁风险。
/// </remarks>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "Broker 消费回调需记录毒消息失败并执行合理 nack，需捕获 Exception 基类。")]
public sealed class RabbitMqBroker : MessageBrokerBase, IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly IPalLogger<RabbitMqBroker> _logger;
    // P2 修复（八轮评审）：exchange 声明任务缓存——声明幂等但避免每发布一次 AMQP 往返；
    // 任务化后并发发布者 await 同一声明，消除"声明飞行中直接 publish → 404 关 channel"竞态。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _exchangeDeclarations = new();

    public RabbitMqBroker(
        IConnection connection,
        IChannel channel,
        IPalLogger<RabbitMqBroker> logger,
        IMessageSerializer serializer,
        IMessageCatalog messageCatalog)
        : base(serializer, messageCatalog)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _channel = channel;
        _logger = logger;
    }

    /// <summary>发布消息到 RabbitMQ Exchange（Fanout 模式）</summary>
    public override async ValueTask PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentOutOfRangeException.ThrowIfEqual(messageId, default);

        var exchange = descriptor.Name;
        // P2 修复（八轮评审）：声明任务化——首个发布者 GetOrAdd 占位声明 Task，并发发布者
        // await 同一任务，杜绝"声明飞行中他人直接 publish → exchange 不存在 404 → channel 被服务端关闭"。
        // P3 修复（十七轮）：声明任务不连坐——原 lambda 透传首个发布者的 ct，其取消使缓存的
        // 声明任务进入 Canceled，并发 await 同一声明的其他发布者（自身 ct 未取消）被 OCE 连坐。
        // 声明幂等无业务副作用，改用 CancellationToken.None 使声明独立于单个发布者的生命周期；
        // 发布取消语义由下方 BasicPublishAsync(ct) 独立承担。
        var declaration = _exchangeDeclarations.GetOrAdd(
            exchange,
            static (name, channel) => channel.ExchangeDeclareAsync(
                name, ExchangeType.Fanout, durable: true, cancellationToken: CancellationToken.None),
            _channel);
        try
        {
            await declaration;
        }
        catch
        {
            // P2 修复：声明失败回滚占位——仅当字典中仍是本失败任务时移除（不误删他人重试的新任务），下次发布重新声明
            _exchangeDeclarations.TryRemove(new KeyValuePair<string, Task>(exchange, declaration));
            throw;
        }

        var body = Serializer.Serialize(message, descriptor);
        await _channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: "",
            mandatory: false,
            basicProperties: CreateProperties(descriptor, messageId, context),
            body: body,
            cancellationToken: ct);

        _logger.Debug($"Published message {descriptor.ClrType.Name} to exchange {exchange}");
    }

    private static BasicProperties CreateProperties(
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context)
        => new()
        {
            MessageId = messageId.ToString(),
            CorrelationId = context.CorrelationId?.ToString(),
            ContentType = descriptor.ContentType,
            Type = descriptor.Name,
            Persistent = true,
            Headers = CreateHeaders(context)
        };

    private static Dictionary<string, object?> CreateHeaders(MessagePublishContext context)
    {
        // P2 修复（八轮评审）：键名与消费端 MessageConsumeContext.FromHeaders 共用常量，锁读写两侧一致
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddHeader(headers, MessageConsumeContext.HeaderNames.TraceParent, context.TraceParent);
        AddHeader(headers, MessageConsumeContext.HeaderNames.TraceState, context.TraceState);
        AddHeader(headers, MessageConsumeContext.HeaderNames.CausationId, context.CausationId?.ToString());
        return headers;
    }

    private static void AddHeader(Dictionary<string, object?> headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            headers.Add(name, Encoding.UTF8.GetBytes(value));
    }

    /// <summary>异步订阅消息 — 适配到带消费上下文的重载（context 恒为 null，零破坏）</summary>
    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        // P2 修复（八轮评审）：旧重载委托适配新重载
        => SubscribeAsync<TMessage>((message, _, token) => handler(message, token), ct);

    /// <summary>异步订阅消息（含消费上下文）— 完全原生异步，零 Task.Run</summary>
    /// <remarks>
    /// 所有操作（声明 Exchange/Queue、绑定、开始消费）均为原生异步。<br/>
    /// 调用方使用 <c>await using var sub = await broker.SubscribeAsync&lt;T&gt;(handler);</c><br/>
    /// 消息未携带任何追踪头时 context 为 null。
    /// </remarks>
    public override async ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
    {
        var descriptor = MessageCatalog.Find(typeof(TMessage))
            ?? throw new InvalidOperationException(
                $"Message type '{typeof(TMessage).FullName}' is not registered in MessageCatalog.");
        var exchange = descriptor.Name;
        var queueName = $"{exchange}.{PalUlid.New()}";

        await _channel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await _channel.QueueDeclareAsync(queueName, durable: false, exclusive: true, autoDelete: true, cancellationToken: ct);
        await _channel.QueueBindAsync(queueName, exchange, "", cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var message = Serializer.Deserialize(ea.Body.Span, descriptor);
                if (message is not null)
                {
                    // P2 修复（八轮评审）：消费端还原追踪头——写侧 CreateHeaders 的镜像，
                    // correlation 兜底读 BasicProperties.CorrelationId（写侧未写 x-correlation-id 头）
                    var consumeContext = MessageConsumeContext.FromHeaders(
                        ea.BasicProperties.Headers, ea.BasicProperties.CorrelationId);
                    await handler((TMessage)message, consumeContext, ea.CancellationToken);
                    // 手动确认 — 仅在处理成功后 ACK
                    // P3 修复：ACK 与 Nack 同样加保护——channel 已关时异常逃逸进消费者回调
                    await TryAckSafeAsync(ea.DeliveryTag, queueName);
                }
                else
                {
                    // 反序列化返回 null — 消息无法处理，不重试
                    _logger.Warning($"Deserializing {typeof(TMessage).Name} returned null, discarding message: {queueName}");
                    await TryNackSafeAsync(ea.DeliveryTag, requeue: false, queueName);
                }
            }
            catch (OperationCanceledException)
            {
                // P2 定案（匿名队列 requeue 语义）：本 Broker 的队列为 exclusive+autoDelete——
                // 连接关闭即删除，"重连后重新投递"不可能；OCE 多发生在关停路径，队列将随连接消亡。
                // requeue:false 显式弃置并留日志（true 会在存活连接上形成自我热循环）。
                _logger.Warning($"Handling {typeof(TMessage).Name} message was canceled during shutdown, discarding: {queueName}");
                await TryNackSafeAsync(ea.DeliveryTag, requeue: false, queueName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, $"Failed to handle {typeof(TMessage).Name} message, discarding (anonymous queue): {queueName}");
                // P2 定案：exclusive 队列的消费者只有本连接——requeue:true 会立即重投给自己，
                // 持续失败时形成无退避热循环。与 Kafka 路径 ITM-008 对齐：记录后弃置（at-most-once）。
                // 需要失败重试语义的应用应使用持久队列 + DLX，由自身的 Broker 配置承载。
                await TryNackSafeAsync(ea.DeliveryTag, requeue: false, queueName);
            }
        };

        var consumerTag = await _channel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken: ct);

        // P3 修复（八轮评审）：channel 已关/连接断时 BasicCancelAsync 抛 AlreadyClosed 类异常——
        // 订阅释放不应被关停路径异常中断，记 Warning 吞掉（对齐 TryAckSafeAsync 模式）。
        return new AsyncSubscription(async () =>
        {
            try
            {
                await _channel.BasicCancelAsync(consumerTag);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning($"BasicCancel failed during unsubscribe (channel closed?): {queueName}, consumerTag={consumerTag}: {ex.Message}");
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        // P2 修复（所有权契约）：IConnection 由调用方创建并注入——可能被多个 Channel/Broker
        // 共享，本 Broker 无权释放（越权释放会断掉其他使用方）。仅释放本 Broker 独占使用的
        // Channel；连接的生命周期由创建者管理。
        await _channel.DisposeAsync();
    }

    /// <summary>
    /// Nack 的安全包装——channel 已关/连接断时 BasicNackAsync 自身会抛异常，
    /// 若从 ReceivedAsync 事件处理器逃逸会中断消费者（P2 修复）。
    /// </summary>
    private async Task TryNackSafeAsync(ulong deliveryTag, bool requeue, string queueName)
    {
        try
        {
            await _channel.BasicNackAsync(deliveryTag, multiple: false, requeue: requeue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"BasicNack failed (channel closed?): {queueName}, deliveryTag={deliveryTag}: {ex.Message}");
        }
    }

    /// <summary>
    /// ACK 的安全包装——与 TryNackSafeAsync 同型（P3 修复：成功路径 channel 已关时
    /// BasicAckAsync 抛异常同样会逃逸进消费者回调）。
    /// </summary>
    private async Task TryAckSafeAsync(ulong deliveryTag, string queueName)
    {
        try
        {
            await _channel.BasicAckAsync(deliveryTag, multiple: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"BasicAck failed (channel closed?): {queueName}, deliveryTag={deliveryTag}: {ex.Message}");
        }
    }

    private sealed class AsyncSubscription(Func<Task> unsubscribe) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await unsubscribe();
    }
}
