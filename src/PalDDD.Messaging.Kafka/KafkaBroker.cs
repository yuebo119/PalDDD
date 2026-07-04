using Confluent.Kafka;
using PalDDD.Core.Logging;
using PalDDD.Serialization;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Messaging.Kafka;

// ─────────────────────────────────────────────────────────────
// Kafka 消息代理适配器
// ─────────────────────────────────────────────────────────────

/// <summary>Kafka 消息代理适配器 — 实现 <see cref="IMessageBroker"/></summary>
/// <remarks>
/// 使用 Confluent.Kafka 2.x。<br/>
/// 消息按类型名路由到同名 Topic。<br/>
/// 使用显式消息 ID 作为消息 Key 保证可追踪性。<br/>
/// 消费循环在后台线程运行（Confluent.Kafka 的 Consume 为同步阻塞 API，必须用 Task.Run）。
/// </remarks>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "Broker 消费循环需记录毒消息失败并继续或优雅关停，需捕获 Exception 基类。")]
public sealed class KafkaBroker : MessageBrokerBase, IAsyncDisposable
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly ConsumerConfig _consumerConfig;
    private readonly IPalLogger<KafkaBroker> _logger;
    private readonly List<IAsyncDisposable> _consumers = [];

    public KafkaBroker(
        ProducerConfig producerConfig,
        ConsumerConfig consumerConfig,
        IPalLogger<KafkaBroker> logger,
        IMessageSerializer serializer,
        IMessageCatalog messageCatalog)
        : base(serializer, messageCatalog)
    {
        ArgumentNullException.ThrowIfNull(producerConfig);
        ArgumentNullException.ThrowIfNull(consumerConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();
        _consumerConfig = consumerConfig;
        _logger = logger;
    }

    /// <summary>发布消息到 Kafka Topic</summary>
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

        var key = messageId.ToString();
        var value = Serializer.Serialize(message, descriptor);

        await _producer.ProduceAsync(descriptor.Name, new Message<string, byte[]>
        {
            Key = key,
            Value = value.ToArray(),
            Headers = CreateHeaders(context)
        }, ct);

        _logger.Debug($"Published {descriptor.ClrType.Name} to Kafka topic {descriptor.Name}, key={key}");
    }

    private static Headers CreateHeaders(MessagePublishContext context)
    {
        var headers = new Headers();
        AddHeader(headers, "traceparent", context.TraceParent);
        AddHeader(headers, "tracestate", context.TraceState);
        AddHeader(headers, "x-correlation-id", context.CorrelationId?.ToString());
        AddHeader(headers, "x-causation-id", context.CausationId?.ToString());
        return headers;
    }

    private static void AddHeader(Headers headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            headers.Add(name, Encoding.UTF8.GetBytes(value));
    }

    /// <summary>异步订阅消息 — 后台线程运行阻塞式消费循环</summary>
    /// <remarks>
    /// Confluent.Kafka 的 Consume 为同步阻塞 API，消费循环必须运行在后台线程。<br/>
    /// 这不是 sync-over-async 反模式——这是与阻塞 IO 库交互的正确方式。<br/>
    /// Task 引用被保存，异常通过日志和 <see cref="KafkaSubscription.ConsumeTask"/> 可观测。
    /// </remarks>
    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
    {
        var descriptor = MessageCatalog.Find(typeof(TMessage))
            ?? throw new InvalidOperationException(
                $"Message type '{typeof(TMessage).FullName}' is not registered in MessageCatalog.");
        var topic = descriptor.Name;
        var consumer = new ConsumerBuilder<string, byte[]>(_consumerConfig).Build();
        consumer.Subscribe(topic); // 同步订阅（无需网络调用）

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 保存 Task 引用，用于等待完成和错误观测
        var consumeTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    ConsumeResult<string, byte[]> result;
                    try
                    {
                        result = consumer.Consume(cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
                    {
                        break; // 正常取消
                    }
                    catch (ConsumeException ex)
                    {
                        _logger.Error(ex, $"Kafka consume failed: {topic} @ {_consumerConfig.GroupId}");
                        break; // 不可恢复的消费错误
                    }

                    try
                    {
                        var message = Serializer.Deserialize(result.Message.Value, descriptor);
                        if (message is not null)
                        {
                            await handler((TMessage)message, cts.Token);
                        }
                        else
                        {
                            // 反序列化返回 null — 消息无法处理，不重试
                            _logger.Warning($"Deserializing {typeof(TMessage).Name} returned null, discarding message: {topic}");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Error(ex, $"Failed to handle {typeof(TMessage).Name} message: {topic}");
                    }
                }
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
                // 正常取消
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 兜底：记录未被内层捕获的异常
                _logger.Error(ex, $"Kafka consume loop terminated unexpectedly: {topic} @ {_consumerConfig.GroupId}");
            }
            finally
            {
                consumer.Close();
                consumer.Dispose();
            }
        }, cts.Token);

        var subscription = new KafkaSubscription(cts, consumeTask);
        _consumers.Add(subscription);
        return new ValueTask<IAsyncDisposable>(subscription);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _consumers) await c.DisposeAsync();
        _producer.Dispose();
    }

    /// <summary>Kafka 订阅句柄 — 持有后台 Task 引用，支持等待完成和状态观测</summary>
    private sealed class KafkaSubscription : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _consumeTask;
        private int _disposed;

        public KafkaSubscription(CancellationTokenSource cts, Task consumeTask)
        {
            _cts = cts;
            _consumeTask = consumeTask;
        }

        /// <summary>后台消费 Task — 可用于健康检查和异常观测</summary>
        public Task ConsumeTask => _consumeTask;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return; // 幂等

            await _cts.CancelAsync();
            try
            {
                // 等待后台消费循环真正退出（而非盲猜延时）
                await _consumeTask;
            }
            catch (OperationCanceledException)
            {
                // 预期行为：取消导致 Task 取消
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 异常已在 Task 内部记录日志，此处防止二次传播
                System.Diagnostics.Debug.Fail($"Kafka 订阅关闭异常: {ex}");
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}
