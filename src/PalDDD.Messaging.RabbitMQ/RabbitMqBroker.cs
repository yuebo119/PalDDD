using PalDDD.Core.Logging;
using PalDDD.Serialization;
using Microsoft.Extensions.Logging;
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
    private int _disposed;
    private readonly IChannel _channel;
    private readonly IPalLogger<RabbitMqBroker> _logger;
    // P2 修复（八轮评审）：exchange 声明任务缓存——声明幂等但避免每发布一次 AMQP 往返；
    // 任务化后并发发布者 await 同一声明，消除"声明飞行中直接 publish → 404 关 channel"竞态。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _exchangeDeclarations = new();
    // 优化（二十五轮 R-1）：exchange 名的 CachedString 缓存（UTF-8 字节表示，免每发布编码分配）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedString> _cachedExchanges = new();
    // 三十八轮 P2 修复：消费 prefetch 上限——manual-ack 下无 BasicQos 时 broker 无界推送，
    // 慢 handler 会无限堆积 unacked 消息（内存膨胀/服务端告警）。默认 10，可按吞吐调整。
    private readonly ushort _prefetchCount;
    // v29 P3：订阅句柄登记（consumerTag → 句柄）——DisposeAsync 兜底释放（镜像 KafkaBroker
    // _consumers 持有列表）；多线程订阅/释放并发安全用 ConcurrentDictionary
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IAsyncDisposable> _subscriptions = new();

    /// <param name="prefetchCount">每消费者 unacked 消息上限（BasicQos prefetch，默认 10）。</param>
    public RabbitMqBroker(
        IConnection connection,
        IChannel channel,
        IPalLogger<RabbitMqBroker> logger,
        IMessageSerializer serializer,
        IMessageCatalog messageCatalog,
        ushort prefetchCount = 10)
        : base(serializer, messageCatalog)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _channel = channel;
        _logger = logger;
        _prefetchCount = prefetchCount;
    }

    /// <summary>发布消息到 RabbitMQ Exchange（Fanout 模式）</summary>
    public override async ValueTask PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct = default)
    {
        // v25 P3 守卫族：发布侧 _disposed 守卫（对齐 KafkaBroker 守卫族）——Broker 释放后
        // 发布落在已 DisposeAsync 的 channel 上抛 provider 异常（或声明缓存回滚竞态），
        // fail-fast 更早更明确（CA1513：用 ThrowIf 替代显式 throw new）
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
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
            await declaration.ConfigureAwait(false);
        }
        catch
        {
            // P2 修复：声明失败回滚占位——仅当字典中仍是本失败任务时移除（不误删他人重试的新任务），下次发布重新声明
            _exchangeDeclarations.TryRemove(new KeyValuePair<string, Task>(exchange, declaration));
            throw;
        }

        var body = Serializer.Serialize(message, descriptor);
        // 优化（二十五轮 R-1）：CachedString 重载——缓存 exchange 名的 UTF-8 字节表示
        // （免每发布 string→bytes 编码分配；XML 证实 BasicPublishAsync(CachedString,...) 存在）
        var cachedExchange = _cachedExchanges.GetOrAdd(exchange, static name => new CachedString(name));
        // ITM-213 修复（三十二轮）：mandatory:true——无绑定队列时发布抛 PublishException，
        // 不再静默丢弃（原 mandatory:false 使 Outbox 标记 Processed 但消息实际未路由）。
        // 注意：publisher confirms 需由调用方在注入的 IChannel 上启用
        // （channel.EnablePublisherConfirmation() 或 CreateChannelAsync 时配置）。
        await _channel.BasicPublishAsync(
            exchange: cachedExchange,
            routingKey: CachedString.Empty,
            mandatory: true,
            basicProperties: CreateProperties(descriptor, messageId, context),
            body: body,
            cancellationToken: ct).ConfigureAwait(false);

        // 优化（二十五轮 Z-1）：同 KafkaBroker——Debug 级门控免白做插值
        if (_logger.IsEnabled(LogLevel.Debug))
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
        // v25 P3 守卫族：订阅侧 _disposed 守卫（descriptor 解析前，对齐 KafkaBroker 订阅侧
        // P3-SRC-402 守卫）——Broker 释放后订阅会在已 DisposeAsync 的 channel 上声明
        // exchange/queue 并登记无人管理的消费句柄（CA1513：用 ThrowIf 替代显式 throw new）
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var descriptor = MessageCatalog.Find(typeof(TMessage))
            ?? throw new InvalidOperationException(
                $"Message type '{typeof(TMessage).FullName}' is not registered in MessageCatalog.");
        var exchange = descriptor.Name;
        var queueName = $"{exchange}.{PalUlid.New()}";

        await _channel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true, cancellationToken: ct).ConfigureAwait(false);
        await _channel.QueueDeclareAsync(queueName, durable: false, exclusive: true, autoDelete: true, cancellationToken: ct).ConfigureAwait(false);

        // v33 P3：队列删除清理的共享收口——v31 初始化失败 catch 与本轮扩围后的新 catch
        // 共用（避免两处 catch 各自内联同一段尽力删除逻辑）
        async Task CleanupDeclaredQueueAsync()
        {
            // v31 P3 修复：尽力删除已声明的匿名队列——autoDelete 仅在"有过消费者后全部
            // 断开"才删除，从未有消费者的 exclusive 队列随连接关闭才消亡；连接长驻时每次
            // 初始化失败会累积一个空壳队列。失败吞掉（channel 已关等，对齐安全包装模式）
            try
            {
                await _channel.QueueDeleteAsync(queueName).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning($"Queue cleanup after failed subscribe was skipped (channel closed?): {queueName}: {ex.Message}");
            }
        }

        var consumer = new AsyncEventingBasicConsumer(_channel);
        // v33 P3 清理域扩围：v31 的清理 catch 只覆盖 BasicQos 起的失败——QueueBindAsync
        // 失败、CreateLinkedTokenSource 抛出时已声明队列无清理（连接长驻时每次失败累积
        // 一个空壳匿名队列）。两失败点纳入下方 try-catch，尽力删除已声明队列后重抛；
        // consumer 创建（纯本地对象，BasicConsume 前 broker 无消费状态）不入域。
        // v25 P2-5：linked-CTS——外部 ct 参与消费生命周期（对齐 KafkaBroker 的
        // CreateLinkedTokenSource 契约，修复前 ct 仅中断初始化、取消后静默继续消费）：
        // ct 取消 → 解绑消费者终止消费；飞行中的 handler 经组合 token 感知取消
        CancellationTokenSource linkedCts;
        try
        {
            await _channel.QueueBindAsync(queueName, exchange, "", cancellationToken: ct).ConfigureAwait(false);
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        catch
        {
            // QueueBind 失败时 consumer 尚未注册消费（无 consumerTag），清理只需队列删除；
            // CreateLinkedTokenSource 抛出时无 CTS 对象产生，同样只需队列删除
            await CleanupDeclaredQueueAsync().ConfigureAwait(false);
            throw;
        }
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
                    // v25 P2-5：per-delivery token 与订阅生命周期 token 组合（每消息一个小 CTS
                    // 分配，消费路径本有反序列化分配；订阅取消/释放时 handler 感知取消→走
                    // OCE 分支 nack 弃置，与关停语义一致）
                    // v28 P3 修复：cts 创建提取到独立 try-catch(ODE)——v27 的 ODE catch 覆盖
                    // 整个 try 块，应用 handler 内部的 ODE（应用自身对象已释放）被误分类为
                    // 关停 Warning。收窄后仅框架 token（linkedCts，随订阅句柄释放）访问的
                    // ODE 走关停 Warning；应用层 ODE 落回下方通用 Error catch（真实故障语义）。
                    // 未采用 `when (_disposed != 0)` 谓词：_disposed 是 broker 级字段，v27 的
                    // 主场景是订阅级释放（sub 先于 broker 释放，此时 _disposed==0），谓词
                    // 会使框架 ODE 落回 Error catch，回退 v27 修复本身
                    CancellationTokenSource deliveryCts;
                    try
                    {
                        deliveryCts = CancellationTokenSource.CreateLinkedTokenSource(
                            ea.CancellationToken, linkedCts.Token);
                    }
                    catch (ObjectDisposedException)
                    {
                        // v27 P3 修复（自外层 catch 移入，场景源收窄）：句柄释放
                        // （AsyncSubscription.DisposeAsync → linkedCts.Dispose()）后
                        // in-flight 投递访问 linkedCts.Token 抛 ODE——关停竞态是预期路径，
                        // Warning 语义（subscription disposed, in-flight delivery discarded），
                        // 不 requeue
                        _logger.Warning($"Subscription disposed while handling {typeof(TMessage).Name} message, in-flight delivery discarded: {queueName}");
                        await TryNackSafeAsync(ea.DeliveryTag, requeue: false, queueName).ConfigureAwait(false);
                        return;
                    }
                    using (deliveryCts)
                    {
                        await handler((TMessage)message, consumeContext, deliveryCts.Token).ConfigureAwait(false);
                        // 手动确认 — 仅在处理成功后 ACK
                        // P3 修复：ACK 与 Nack 同样加保护——channel 已关时异常逃逸进消费者回调
                        await TryAckSafeAsync(ea.DeliveryTag, queueName).ConfigureAwait(false);
                    }
                }
                else
                {
                    // 反序列化返回 null — 消息无法处理，不重试
                    _logger.Warning($"Deserializing {typeof(TMessage).Name} returned null, discarding message: {queueName}");
                    await TryNackSafeAsync(ea.DeliveryTag, requeue: false, queueName).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                // P2 定案（匿名队列 requeue 语义）：本 Broker 的队列为 exclusive+autoDelete——
                // 连接关闭即删除，"重连后重新投递"不可能；OCE 多发生在关停路径，队列将随连接消亡。
                // requeue:false 显式弃置并留日志（true 会在存活连接上形成自我热循环）。
                // v31 P3 修复：补订阅级取消谓词（对齐 KafkaBroker when(cts.Token.IsCancellationRequested)
                // 的关停/非关停区分）——应用 handler 内部抛出的 OCE（应用自身超时/取消）不再被
                // 误标为关停 Warning；CTS.IsCancellationRequested 属性在 Dispose 后读取安全（不抛），
                // 句柄释放（linkedCts.Dispose）后的飞行中 OCE 同样落回通用 catch 记 Error
                _logger.Warning($"Handling {typeof(TMessage).Name} message was canceled during shutdown, discarding: {queueName}");
                await TryNackSafeAsync(ea.DeliveryTag, requeue: false, queueName).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException)
            {
                // v33 P2 修复：补齐 v31 谓词化引入的 filter 互斥缝——非关停 OCE（应用 handler
                // 自身超时/取消抛出，linkedCts 未取消）不匹配上方关停谓词、也不匹配下方
                // is-not-OCE 通用 catch，异常从 async 回调逃逸且 Ack/Nack 均未执行 → delivery
                // 恒 unacked 占用 prefetch 名额（默认 10），连续后 broker 停推，超
                // consumer_timeout（默认 30 分钟）触发 PRECONDITION_FAILED 关闭 channel，
                // 共用 _channel 的全部订阅与发布一并瘫痪。本分支记 Error + nack 弃置
                //（与 ITM-008 at-most-once 对齐），消费继续
                _logger.Error(ex, $"Handler for {typeof(TMessage).Name} threw a non-shutdown OperationCanceledException, discarding: {queueName}");
                await TryNackSafeAsync(ea.DeliveryTag, requeue: false, queueName).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, $"Failed to handle {typeof(TMessage).Name} message, discarding (anonymous queue): {queueName}");
                // P2 定案：exclusive 队列的消费者只有本连接——requeue:true 会立即重投给自己，
                // 持续失败时形成无退避热循环。与 Kafka 路径 ITM-008 对齐：记录后弃置（at-most-once）。
                // 需要失败重试语义的应用应使用持久队列 + DLX，由自身的 Broker 配置承载。
                await TryNackSafeAsync(ea.DeliveryTag, requeue: false, queueName).ConfigureAwait(false);
            }
        };

        // 三十八轮 P2 修复：consume 前设置 prefetch 上限——manual-ack 下防 broker 无界推送
        // v26 P3（镜像 KafkaBroker v18 E-1）：初始化段失败清理——linkedCts 创建后至订阅句柄
        // 构造前（BasicQosAsync/BasicConsumeAsync/ct.Register）任一抛出时，linkedCts（含其
        // 对 ct 的 linked 注册）与 tokenReg 无人负责释放（句柄尚未创建），就地 Dispose 后重抛；
        // BasicConsumeAsync 已成功时同步取消消费，防消费者悬挂在已声明队列上无人管理
        string? consumerTag = null;
        CancellationTokenRegistration tokenReg = default;
        try
        {
            await _channel.BasicQosAsync(0, _prefetchCount, false, ct).ConfigureAwait(false);
            consumerTag = await _channel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken: ct).ConfigureAwait(false);

            // v25 P2-5：ct 取消 → 异步解绑消费者终止消费；channel 已关等异常由安全包装吞成
            // Warning。句柄释放时 Dispose registration 防泄漏（释放与取消并发时，双
            // BasicCancelAsync 的后者同样被安全包装吞掉）
            tokenReg = ct.Register(() => { _ = CancelConsumeSafeAsync(consumerTag!, queueName); });
        }
        catch
        {
            tokenReg.Dispose();
            if (consumerTag is not null)
                await CancelConsumeSafeAsync(consumerTag, queueName).ConfigureAwait(false);
            linkedCts.Dispose();
            // v33 P3：队列删除逻辑上移至 CleanupDeclaredQueueAsync 共享收口（QueueBind/
            // linkedCts 失败的扩围 catch 同用；v31 原内联 try-catch 注释见局部函数内）
            await CleanupDeclaredQueueAsync().ConfigureAwait(false);
            throw;
        }

        // P3 修复（八轮评审）：channel 已关/连接断时 BasicCancelAsync 抛 AlreadyClosed 类异常——
        // 订阅释放不应被关停路径异常中断，记 Warning 吞掉（对齐 TryAckSafeAsync 模式）。
        // v25 P2-5：释放时先取消组合 token（通知飞行中 handler）再解绑，最后释放 linked cts。
        // v29 P3 修复（兜底释放，镜像 KafkaBroker _consumers）：此前 Broker 不追踪订阅句柄——
        // 调用方（如容器 teardown 顺序异常）未 Dispose 句柄时 tokenReg 滞留于 ct 生命周期、
        // 消费者悬挂至 channel 关闭。现以 consumerTag 为键登记进 _subscriptions（v26 初始化
        // 清理 try 成功后、返回前）；DisposeAsync 在 channel 释放前遍历 Dispose 全部句柄
        //（句柄级幂等门已防双释放）；句柄闭包正常释放时同步移除登记，防长驻 Broker 反复
        // 订阅/退订下登记表无界增长。残留窗口声明：DisposeAsync 遍历后并发完成登记的句柄
        //（SubscribeAsync 入口的 _disposed 守卫之后的飞行中订阅）不在兜底范围——句柄随
        // channel 释放终结，仅 tokenReg 沿调用方 ct 生命周期滞留（修复前常态，收敛不改恶）
        var subscription = new AsyncSubscription(async () =>
        {
            _subscriptions.TryRemove(consumerTag!, out _);
            tokenReg.Dispose();
            linkedCts.Cancel();
            await CancelConsumeSafeAsync(consumerTag!, queueName).ConfigureAwait(false);
            linkedCts.Dispose();
        });
        _subscriptions[consumerTag] = subscription;
        return subscription;
    }

    public async ValueTask DisposeAsync()
    {
        // P2 修复（所有权契约）：IConnection 由调用方创建并注入——可能被多个 Channel/Broker
        // 共享，本 Broker 无权释放（越权释放会断掉其他使用方）。仅释放本 Broker 独占使用的
        // Channel；连接的生命周期由创建者管理。
        // v9 E3：幂等门（对齐 KafkaBroker ITM-217）——双 Dispose 时 Channel 自身幂等，
        // 显式门消除对下游幂等性的依赖假设
        // v29 P3：订阅句柄兜底释放——channel 释放前遍历 Dispose 全部登记句柄（调用方
        // 已自行释放时句柄幂等门使再释放为 no-op）
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var subscription in _subscriptions.Values)
            await subscription.DisposeAsync().ConfigureAwait(false);
        _subscriptions.Clear();
        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Nack 的安全包装——channel 已关/连接断时 BasicNackAsync 自身会抛异常，
    /// 若从 ReceivedAsync 事件处理器逃逸会中断消费者（P2 修复）。
    /// </summary>
    private async Task TryNackSafeAsync(ulong deliveryTag, bool requeue, string queueName)
    {
        try
        {
            await _channel.BasicNackAsync(deliveryTag, multiple: false, requeue: requeue).ConfigureAwait(false);
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
            await _channel.BasicAckAsync(deliveryTag, multiple: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"BasicAck failed (channel closed?): {queueName}, deliveryTag={deliveryTag}: {ex.Message}");
        }
    }

    /// <summary>
    /// 取消订阅的安全包装——channel 已关/连接断时 BasicCancelAsync 抛 AlreadyClosed 类异常，
    /// 吞成 Warning（v25 P2-5：ct 取消路径与句柄释放路径共用，对齐 TryNackSafeAsync 模式）。
    /// </summary>
    private async Task CancelConsumeSafeAsync(string consumerTag, string queueName)
    {
        try
        {
            await _channel.BasicCancelAsync(consumerTag).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"BasicCancel failed (channel closed?): {queueName}, consumerTag={consumerTag}: {ex.Message}");
        }
    }

    private sealed class AsyncSubscription(Func<Task> unsubscribe) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            // v13 姊妹对称：句柄级幂等门（对齐 KafkaSubscription ITM-217）——双 Dispose 时
            // 第二次 BasicCancelAsync 异常被 Broker 侧 catch 吞成 Warning，显式门消除噪音
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await unsubscribe().ConfigureAwait(false);
        }
    }
}
