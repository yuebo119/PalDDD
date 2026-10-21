using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PalDDD.Core.Logging;
using PalDDD.Messaging;

namespace PalDDD.Transactions;

/// <summary>
/// Inbox 批量消费者（2026-10-21）：把 broker 的批量订阅、Inbox 批量认领与业务 handler 串起来。
/// 每批创建一个 DI scope，解析 <see cref="InboxProcessor"/>，调用其
/// <c>TryProcessBatchAsync</c>。消息 ID 默认取
/// <see cref="MessageConsumeContext.MessageId"/>（Kafka Key / RabbitMQ MessageId），
/// 也可用 <c>messageIdSelector</c> 显式提供。
/// </summary>
/// <remarks>
/// 批内 handler 失败由 <see cref="InboxProcessor"/> 逐条标记 Failed 并聚合抛出；broker 的
/// 批量泵记录异常后继续后续批次。Kafka 自动提交与 RabbitMQ <c>requeue:false</c> 仍是
/// at-most-once：失败消息不会由 broker 重投，需要重试的场景应在业务 handler 内处理，
/// 或改用带手动提交的实现。
/// </remarks>
public sealed class InboxBatchConsumer<TMessage>
{
    private readonly IMessageBroker _broker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPalLogger<InboxBatchConsumer<TMessage>>? _logger;
    private readonly string _consumerName;

    /// <summary>创建批量消费者。</summary>
    /// <param name="broker">消息代理（提供批量订阅）。</param>
    /// <param name="scopeFactory">每批创建 scope，scope 内需能解析 <see cref="InboxProcessor"/>。</param>
    /// <param name="options">Inbox 选项（默认消费者名与处理超时）。</param>
    /// <param name="consumerName">显式消费者名；null 时取 <see cref="InboxOptions.DefaultConsumerName"/>。</param>
    /// <param name="logger">可选日志器。</param>
    public InboxBatchConsumer(
        IMessageBroker broker,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<InboxOptions> options,
        string? consumerName = null,
        IPalLogger<InboxBatchConsumer<TMessage>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);

        _broker = broker;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _consumerName = string.IsNullOrWhiteSpace(consumerName)
            ? options.CurrentValue.DefaultConsumerName
            : consumerName;
    }

    /// <summary>
    /// 启动批量消费。返回的句柄释放后停止订阅与组批泵；批内消息最多重放一批
    /// （at-least-once，需业务 handler 幂等）。
    /// </summary>
    /// <param name="handler">业务 handler；每批由 <see cref="InboxProcessor"/> 逐条调用。</param>
    /// <param name="messageIdSelector">消息 ID 选择器；null 时取 <see cref="MessageConsumeContext.MessageId"/>。</param>
    /// <param name="maxBatchSize">单批上限，默认 64。</param>
    /// <param name="maxDelay">组批时间上界，默认 50ms。</param>
    /// <param name="ct">订阅与消费生命周期令牌。</param>
    public ValueTask<IAsyncDisposable> StartAsync(
        Func<TMessage, CancellationToken, ValueTask> handler,
        Func<ConsumedMessage<TMessage>, string>? messageIdSelector = null,
        int maxBatchSize = 64,
        TimeSpan? maxDelay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        return _broker.SubscribeBatchAsync<TMessage>(
            async (batch, token) =>
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<InboxProcessor>();
                var items = new (string MessageId, TMessage Message)[batch.Count];
                for (var i = 0; i < batch.Count; i++)
                {
                    var item = batch[i];
                    var messageId = messageIdSelector is not null
                        ? messageIdSelector(item)
                        : item.Context?.MessageId;
                    if (string.IsNullOrWhiteSpace(messageId))
                    {
                        throw new InvalidOperationException(
                            "InboxBatchConsumer requires MessageConsumeContext.MessageId (Kafka key / RabbitMQ MessageId) or an explicit messageIdSelector.");
                    }
                    items[i] = (messageId, item.Message);
                }

                var processed = await processor.TryProcessBatchAsync(
                    _consumerName, items, handler, token).ConfigureAwait(false);
                if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.Debug(
                        $"Inbox batch consumer '{_consumerName}': processed {processed}/{batch.Count} message(s)");
                }
            },
            maxBatchSize,
            maxDelay,
            ct);
    }
}
