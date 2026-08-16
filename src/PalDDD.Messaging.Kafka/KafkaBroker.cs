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
    // P2 修复：_consumers 的并发 Add（多线程 SubscribeAsync）与 DisposeAsync 遍历需互斥
    private readonly object _consumersLock = new();

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
        // P2 修复（八轮评审）：键名与消费端 MessageConsumeContext.FromHeaders 共用常量，锁读写两侧一致
        var headers = new Headers();
        AddHeader(headers, MessageConsumeContext.HeaderNames.TraceParent, context.TraceParent);
        AddHeader(headers, MessageConsumeContext.HeaderNames.TraceState, context.TraceState);
        AddHeader(headers, MessageConsumeContext.HeaderNames.CorrelationId, context.CorrelationId?.ToString());
        AddHeader(headers, MessageConsumeContext.HeaderNames.CausationId, context.CausationId?.ToString());
        return headers;
    }

    private static void AddHeader(Headers headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            headers.Add(name, Encoding.UTF8.GetBytes(value));
    }

    /// <summary>异步订阅消息 — 适配到带消费上下文的重载（context 恒为 null，零破坏）</summary>
    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        // P2 修复（八轮评审）：旧重载委托适配新重载
        => SubscribeAsync<TMessage>((message, _, token) => handler(message, token), ct);

    /// <summary>异步订阅消息（含消费上下文）— 后台线程运行阻塞式消费循环</summary>
    /// <remarks>
    /// Confluent.Kafka 的 Consume 为同步阻塞 API，消费循环必须运行在后台线程。<br/>
    /// 这不是 sync-over-async 反模式——这是与阻塞 IO 库交互的正确方式。<br/>
    /// Task 引用被保存，异常通过日志和 <see cref="KafkaSubscription.ConsumeTask"/> 可观测。<br/>
    /// 消息未携带任何追踪头时 context 为 null。
    /// </remarks>
    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
    {
        var descriptor = MessageCatalog.Find(typeof(TMessage))
            ?? throw new InvalidOperationException(
                $"Message type '{typeof(TMessage).FullName}' is not registered in MessageCatalog.");
        var topic = descriptor.Name;
        var consumer = new ConsumerBuilder<string, byte[]>(_consumerConfig).Build();
        consumer.Subscribe(topic); // 同步订阅（无需网络调用）

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // P3 修复（十七轮）：登记先行——先创建订阅占位并登记进 _consumers，再启动消费循环。
        // 原顺序 Task.Run 先于登记：循环若在登记前已终止（如订阅后 token 预取消即抛 OCE），
        // DisposeAsync 的 snapshot 不含此订阅 → consumer 无人释放。占位后任何时点的
        // DisposeAsync 都能触达本订阅（DisposeAsync 容忍 _consumeTask 尚未 Set 的窗口）。
        var subscription = new KafkaSubscription(cts, consumer);
        lock (_consumersLock)
        {
            _consumers.Add(subscription);
        }

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
                        // 消费错误：记录后继续下一条（ITM-008）。
                        // ⚠️ 投递语义声明（三轮评审纠偏）：EnableAutoCommit 默认 true——失败消息
                        // 的 offset 照常自动提交，本路径为 at-most-once（消息丢失），不是重投。
                        // 与 RabbitMQ 的 nack 路径语义不同（那条是 requeue:false 显式弃置）。
                        // 若为分区末尾/短暂网络抖动，Consume 会自动重试或等待新消息。
                        _logger.Error(ex, $"Kafka consume error on {topic} @ {_consumerConfig.GroupId}, continuing consumption");
                        // 退避防止边缘场景（如 topic 不存在）的 CPU 空转。
                        // Consume 本身通常阻塞等待，但某些持续错误会立即返回。
                        await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        var message = Serializer.Deserialize(result.Message.Value, descriptor);
                        if (message is not null)
                        {
                            // P2 修复（八轮评审）：消费端还原追踪头——写侧 CreateHeaders 的镜像
                            var consumeContext = MessageConsumeContext.FromHeaders(ToHeaderMap(result.Message.Headers));
                            await handler((TMessage)message, consumeContext, cts.Token);
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
            // P3 修复（十七轮）：非关停 OCE 空洞——外层 token 未取消却收到 OCE（语义不明，
            // 如链路 token 深层触发）时，此前 when 分支与 not-OCE 分支均不匹配，
            // 异常逃逸致 Task 静默 fault。终止而非 continue：OCE 语义不明时继续消费
            // 可能违背取消意图，终止更安全（终止状态经 ConsumeTask 可观测，运维可介入）。
            catch (OperationCanceledException ex)
            {
                _logger.Error(ex, $"Kafka consume loop terminated by non-shutdown cancellation: {topic} @ {_consumerConfig.GroupId}");
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

        // P3 修复（十七轮）：登记先行的回填——循环启动后注入 Task 引用
        // （Set 前若被 Dispose，DisposeAsync 走 null 窗口路径：cts 已取消，
        // 委托侧 finally 释放 consumer + 兜底 Dispose 幂等，无泄漏）
        subscription.SetConsumeTask(consumeTask);
        return new ValueTask<IAsyncDisposable>(subscription);
    }

    /// <summary>
    /// Confluent.Kafka Headers（Key/Value 结构集合）转字典视图——
    /// 供 <see cref="MessageConsumeContext.FromHeaders"/> 统一提取（重复键后者覆盖）。
    /// </summary>
    private static Dictionary<string, object?>? ToHeaderMap(Headers? headers)
    {
        if (headers is null || headers.Count == 0)
            return null;

        Dictionary<string, object?> map = new(StringComparer.Ordinal);
        foreach (var header in headers)
            map[header.Key] = header.GetValueBytes(); // IHeader API：Key + GetValueBytes()（无 Value 属性）
        return map;
    }

    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] snapshot;
        lock (_consumersLock)
        {
            snapshot = [.. _consumers];
            _consumers.Clear();
        }
        foreach (var c in snapshot) await c.DisposeAsync();
        _producer.Dispose();
    }

    /// <summary>Kafka 订阅句柄 — 持有后台 Task 引用，支持等待完成和状态观测</summary>
    private sealed class KafkaSubscription : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly IConsumer<string, byte[]> _consumer;
        // P3 修复（十七轮）：登记先行模式——构造时不持有 Task（循环尚未启动），
        // 由 SetConsumeTask 后置注入；Volatile 读写保证跨线程可见性（引用写原子）
        private Task? _consumeTask;
        private int _disposed;

        public KafkaSubscription(CancellationTokenSource cts, IConsumer<string, byte[]> consumer)
        {
            _cts = cts;
            _consumer = consumer;
        }

        /// <summary>后台消费 Task — 可用于健康检查和异常观测。
        /// 登记与循环启动之间存在极小窗口，期间为 null（登记先行的顺序权衡）。</summary>
        public Task? ConsumeTask => Volatile.Read(ref _consumeTask);

        /// <summary>后置注入消费循环 Task（登记先行模式）——登记完成后由 SubscribeAsync 调用一次。</summary>
        public void SetConsumeTask(Task consumeTask)
        {
            ArgumentNullException.ThrowIfNull(consumeTask);
            Volatile.Write(ref _consumeTask, consumeTask);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return; // 幂等

            await _cts.CancelAsync();
            try
            {
                // 等待后台消费循环真正退出（而非盲猜延时）。
                // P3 修复（十七轮）· 登记先行窗口：登记与 SetConsumeTask 之间被 Dispose
                // 时为 null——此时循环未启动或刚启动，cts 已请求取消，委托侧 finally
                // 会释放 consumer，下方 finally 兜底 Dispose 幂等，无泄漏。
                var consumeTask = Volatile.Read(ref _consumeTask);
                if (consumeTask is not null)
                    await consumeTask;
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
                // P2 修复：若取消发生在 Task.Run 委托开始执行前，委托内 finally
                // （consumer.Close + Dispose）不会执行——此处兜底释放 consumer
                // （Confluent.Kafka Dispose 幂等，与委托内 finally 双重释放安全）。
                _consumer.Dispose();
                _cts.Dispose();
            }
        }
    }
}
