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
        await _channel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);

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
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddHeader(headers, "traceparent", context.TraceParent);
        AddHeader(headers, "tracestate", context.TraceState);
        AddHeader(headers, "x-causation-id", context.CausationId?.ToString());
        return headers;
    }

    private static void AddHeader(Dictionary<string, object?> headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            headers.Add(name, Encoding.UTF8.GetBytes(value));
    }

    /// <summary>异步订阅消息 — 完全原生异步，零 Task.Run</summary>
    /// <remarks>
    /// 所有操作（声明 Exchange/Queue、绑定、开始消费）均为原生异步。<br/>
    /// 调用方使用 <c>await using var sub = await broker.SubscribeAsync&lt;T&gt;(handler);</c>
    /// </remarks>
    public override async ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
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
                    await handler((TMessage)message, ea.CancellationToken);
                    // 手动确认 — 仅在处理成功后 ACK
                    await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                }
                else
                {
                    // 反序列化返回 null — 消息无法处理，不重试
                    _logger.Warning($"Deserializing {typeof(TMessage).Name} returned null, discarding message: {queueName}");
                    await _channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
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

        return new AsyncSubscription(() => _channel.BasicCancelAsync(consumerTag));
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

    private sealed class AsyncSubscription(Func<Task> unsubscribe) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await unsubscribe();
    }
}
