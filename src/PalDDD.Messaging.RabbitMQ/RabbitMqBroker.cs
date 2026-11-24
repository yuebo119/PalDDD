using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
using PalDDD.Serialization;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
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
/// <remarks>
/// <b>内置 tracing 与本仓手工 traceparent 头的共存语义</b>（2026-09-25 特性审计，源码级结论
/// ——反编译 RabbitMQ.Client 7.2.2 RabbitMQActivitySource；<b>7.3.0 勘正 2026-11-23</b>：
/// <c>RabbitMQActivitySource.ContextInjector</c> / <c>ContextExtractor</c> /
/// <c>UseRoutingKeyAsOperationName</c> / <c>TracingOptions</c> 自 7.3.0 起标记
/// <c>[Obsolete]</c>，传播配置迁移到 <c>ConnectionFactory.TracingOptions.Propagator</c>
/// （<c>DistributedContextPropagator</c>）与同级的 <c>ConnectionTracingOptions</c>；本仓两条
/// 共存规则的语义不变——本仓不注册 ActivityListener、不配置 propagator，默认传播器仍是
/// 进程级 <c>DistributedContextPropagator.Current</c>，故 header 同键与短路行为逐位一致。
/// 上文与下文仅在语义层保留旧成员名，新增代码请改用 <c>ConnectionTracingOptions</c>）：
/// <para>客户端内建 tracing（<c>RabbitMQActivitySource.ContextInjector/ContextExtractor</c>）
/// 经 <see cref="System.Diagnostics.DistributedContextPropagator.Current"/>（W3C）注入/提取，
/// header 键即 <c>"traceparent"</c>——与本仓 <see cref="MessageConsumeContext.HeaderNames.TraceParent"/>
/// <b>同键</b>。两条共存规则（均有源码短路保证）：</para>
/// <para>① <b>宿主未注册 ActivityListener 时客户端 tracing 完全不激活</b>
/// （<c>BasicPublish/Deliver/BasicGet</c> 均以 <c>HasListeners()</c> 短路返回 null，注入与提取
/// 不会执行）——本仓手工头是唯一传播事实源，行为与本仓 3.1.0 语义逐位一致；</para>
/// <para>② <b>宿主注册 listener 后</b>，消费侧 <c>ContextExtractor</c> 在建链时先读到的是
/// 消息到达时头部里的本仓值（出站时序：本仓 <c>CreateHeaders</c> 先写、客户端 Injector 后写
/// 同键覆盖）——W3C 链条衔接正确；发布侧 OutboxProcessor 的 Activity.Current 链会覆盖本仓
/// 还原的原始发布上下文头。若业务需保留 outbox 原始上下文而非 outbox 投递 activity 链，
/// 请设置自定义 <c>ConnectionTracingOptions.Propagator</c>（7.3.0+；旧版本为
/// <c>RabbitMQActivitySource.ContextInjector</c>）先取本仓头再透传。</para>
/// </remarks>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "Broker 消费回调需记录毒消息失败并执行合理 nack，需捕获 Exception 基类。")]
public sealed class RabbitMqBroker : MessageBrokerBase, IAsyncDisposable, IMessageBrokerHealth
{
    private int _disposed;
    // v4 批 C（S1）：channel 级不可恢复故障原因（健康检查读取；普通 handler 失败不置位）。
    // Rabbit 无 Kafka 式「消费线程终止」观测点——per-delivery ReceivedAsync 回调模型下，
    // 可观测的故障面是 channel 关闭。R18 缺陷 1 勘正：修前注释称「只有结算抛异常且
    // IsOpen=false」才置位，漏了「channel 关闭但无结算动作」的空闲断连面；现由
    // consumer.ShutdownAsync 主动通知补齐（Initiator=Application 的正常关停不置位）。
    private volatile string? _consumeFaultReason;
    private readonly IChannel _channel;
    private readonly IConnection _connection;
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

    /// <summary>初始化 RabbitMQ Broker。</summary>
    /// <param name="prefetchCount">每消费者 unacked 消息上限（BasicQos prefetch，默认 10）。</param>
    /// <remarks>
    /// ⚠️ <b>ITM-639 前置条件——<paramref name="channel"/> 必须启用 publisher confirms</b>：
    /// <see cref="PublishAsync"/> 使用 <c>mandatory:true</c>，只有 channel 同时启用
    /// <c>publisherConfirmationsEnabled</c> 与 <c>publisherConfirmationTrackingEnabled</c> 时，
    /// broker 的 <c>basic.return</c> 才会被客户端转成 <c>RabbitMQ.Client.Exceptions.PublishException</c>；
    /// 未启用时发布到无绑定队列的 exchange 会静默"成功"，Outbox 随即标记 Processed →
    /// 消息实际丢失。<br/>
    /// RabbitMQ.Client 7.x 的 <see cref="IChannel"/> <b>不提供</b>该启用状态的公开探测成员
    /// （<c>GetNextPublishSequenceNumberAsync</c> 在未启用时同样返回而不抛），框架无法在
    /// 构造函数内可靠校验——请改用 <see cref="CreateAsync"/> 工厂构造本类型，或自行以
    /// <see cref="CreateChannelOptions"/> 显式启用后注入本构造函数。
    /// </remarks>
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

        _channel = channel;
        _connection = connection;
        _logger = logger;
        _prefetchCount = prefetchCount;
        // 发布 channel 的 publisher confirms 保证同一 channel 内的帧序；并发发布
        // 由 channel 内部确认跟踪串行化，Outbox 窗口可安全启用。
        SupportsConcurrentPublish = true;

        // L5（perf-opt-sweep，2026-09-20）：发布超时指引——框架不包装 PublishAsync 超时
        //（透传调用方 ct）；半坏 broker 下的等待时长由注入的 IConnection/ChannelOptions
        // 控制（publisher confirmations + continuation timeout，见 CreateChannelOptions
        // 使用方配置）。此处仅声明，不改写使用方的连接配置。
    }

    /// <inheritdoc />
    public override bool SupportsConcurrentPublish { get; }

    /// <summary>
    /// 为 true 时所有订阅复用发布的单 channel（4.x 旧行为，兼容/受限拓扑）；
    /// 默认 false：每个订阅经 <see cref="IConnection"/> 创建独立 consumer channel，
    /// 发布 channel 只承担发布与确认，订阅之间不再共享 QoS/ack/消费串行面。
    /// </summary>
    public bool UseSharedConsumerChannel { get; init; }

    private ushort? _consumerDispatchConcurrency;

    /// <summary>
    /// 每订阅 consumer channel 的消费分发并发度；null = 继承连接配置（RabbitMQ.Client 默认 1）。
    /// 大于 1 时同一订阅的 handler 可并发执行，调用方必须保证 handler 线程安全与幂等；
    /// 建议同时把 prefetch 设为不小于该值。共享单 channel 模式下本选项不生效。
    /// 必须在 <c>SubscribeAsync</c> 之前设置。
    /// </summary>
    public ushort? ConsumerDispatchConcurrency
    {
        get => _consumerDispatchConcurrency;
        set
        {
            if (value is 0)
                throw new ArgumentOutOfRangeException(nameof(value), "ConsumerDispatchConcurrency must be greater than zero.");
            _consumerDispatchConcurrency = value;
        }
    }

    // ITM-639：outstanding publisher confirms 上限——对齐 RabbitMQ.Client 文档所述默认
    // 并发阈值 128，防确认跟踪下未确认消息无界堆积（内存膨胀）。
    private const int MaxOutstandingConfirms = 128;

    /// <summary>
    /// ITM-639 推荐构造入口：在 <paramref name="connection"/> 上创建一个已启用 publisher
    /// confirms（含 confirmation tracking）的 channel，并构造 <see cref="RabbitMqBroker"/>。
    /// </summary>
    /// <remarks>
    /// 使用本工厂可确保 <see cref="PublishAsync"/> 的 <c>mandatory:true</c> 语义生效——
    /// 发布到无绑定队列的 exchange 会抛 <c>RabbitMQ.Client.Exceptions.PublishException</c>
    /// 而非静默丢弃。若调用方自行创建 channel，必须同样以
    /// <see cref="CreateChannelOptions"/>（<c>publisherConfirmationsEnabled:true</c> +
    /// <c>publisherConfirmationTrackingEnabled:true</c>）创建，否则消息可能丢失。<br/>
    /// 连接（<paramref name="connection"/>）生命周期仍由调用方管理；本工厂仅创建并（在
    /// 构造失败时）释放临时 channel，成功构造后 channel 交由 <see cref="RabbitMqBroker.DisposeAsync"/> 释放。
    /// </remarks>
    public static async ValueTask<RabbitMqBroker> CreateAsync(
        IConnection connection,
        IPalLogger<RabbitMqBroker> logger,
        IMessageSerializer serializer,
        IMessageCatalog messageCatalog,
        ushort prefetchCount = 10,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // ITM-639：显式启用发布者确认 + 确认跟踪（mandatory 路由失败方可转为 PublishException）。
        // consumerDispatchConcurrency 传 null——继承连接配置，等价于无参 CreateChannelAsync()
        // 的消费分发并发度（本 Broker 同一 channel 亦用于订阅，避免隐式改为 1）。
        var options = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true,
            outstandingPublisherConfirmationsRateLimiter: new ThrottlingRateLimiter(MaxOutstandingConfirms),
            consumerDispatchConcurrency: null);
        var channel = await connection.CreateChannelAsync(options, ct).ConfigureAwait(false);
        try
        {
            return new RabbitMqBroker(connection, channel, logger, serializer, messageCatalog, prefetchCount);
        }
        catch
        {
            // 构造失败（logger/serializer/catalog 为 null 等）——已创建的 channel 无人负责，
            // 就地释放后重抛，避免连接内 channel 泄漏。
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // A2（2026-10-15 性能实施轮）：发布路径的 body 缓冲——每发布从 ArrayPool 租借，
    // 序列化经 IBufferWriter 直写重载写入，BasicPublishAsync(body: WrittenMemory)
    // 直传，await 完成后归还（using 保证异常路径同样归还）。
    //
    // 租借形态取代 R8 的 ThreadStatic 直传，原因是后者存在静默数据损坏风险（官方源码
    // 实证，RabbitMQ.Client 7.2.2）：BasicPublishCoreAsync（Channel.BasicPublish.cs）
    // 在 MaybeAcquirePublisherConfirmationLockAsync（outstanding confirms 超过
    // ThrottlingRateLimiter 上限即挂起，CreateAsync 默认上限 128）与
    // MaybeEnforceFlowControlAsync（broker connection.blocked 时挂起）两个 await
    // 之后，才经 SessionBase.TransmitAsync → SerializeToFrames 同步拷贝 body。
    // 窗口内 body 仍指向调用方缓冲：同线程流水线发布（Outbox MaxInFlightPublishes>1）
    // 的下一次入口 Clear 会抹掉尚待拷贝的内容，帧照常组装、confirm 照常成功，消息静默
    // 错内容。原注释「BasicPublishAsync 完成前已消费 body」只覆盖拷贝发生的事实，
    // 不覆盖拷贝时点晚于两个可挂起点的事实。租借把缓冲生命周期收敛到单次发布调用内，
    // 共享可变状态消除（rent/return 约 40ns，对比帧损坏可接受）。

    /// <summary>body 缓冲初始租借容量——覆盖常见消息尺寸，减少倍增成长次数。</summary>
    private const int InitialBodyBufferBytes = 4096;

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
        // FIX-016（2026-11-08）：发布跳 span 提升到 try 外——catch 分支补 SetStatus(Error)，
        // 让发布失败在 Activity 上可见（修前失败仅记日志/计数器，Activity 恒 Unset）。
        using var publishActivity = PalActivitySource.StartBrokerPublish(descriptor.Name, descriptor.Name);
        // FIX-011（2026-11-08）：EnsureExchangeAsync 与 Serialize 移入 try——broker 不可达
        // （exchange 声明失败）与序列化失败同计 BrokerPublishFailed（修前两者逃逸出 try，
        // 成为不计数的静默失败）。
        // 7.3.0 bodyOwner（2026-11-23）：writer 的所有权在发布成功后转移给客户端，
        // 因此不能在 try 内 using；失败/未转移路径由 finally 兜底释放（Dispose 幂等）。
        PooledBodyWriter? bodyWriter = null;
        try
        {
            var cachedExchange = await EnsureExchangeAsync(exchange).ConfigureAwait(false);

            // A2 + 7.3.0 bodyOwner：IBufferWriter 直写 + WrittenMemory 直传，并把 writer 交给
            // 客户端作 bodyOwner——客户端在发送完成或发布失败后释放一次，7.2.x 无 owner 时
            // 客户端必须把 body 复制进内部缓冲，本形态省掉该整载荷拷贝。
            bodyWriter = new PooledBodyWriter(InitialBodyBufferBytes);
            Serializer.Serialize(message, descriptor, bodyWriter);
            // ITM-213 修复（三十二轮）：mandatory:true——无绑定队列时发布抛 PublishException，
            // 不再静默丢弃（原 mandatory:false 使 Outbox 标记 Processed 但消息实际未路由）。
            // ITM-639：该语义仅在 channel 启用 publisher confirms（含 confirmation tracking）时
            // 成立——只有 broker 的 basic.return 被客户端转成 PublishException，mandatory 才真正
            // 生效。推荐用 RabbitMqBroker.CreateAsync 构造（自动启用）；注入自建 channel 时必须
            // 自行以 CreateChannelOptions(publisherConfirmationsEnabled:true,
            // publisherConfirmationTrackingEnabled:true) 创建，否则本 await 会静默"成功"。
            await _channel.BasicPublishAsync(
                exchange: cachedExchange,
                routingKey: CachedString.Empty,
                mandatory: true,
                basicProperties: CreateProperties(descriptor, messageId, context),
                body: bodyWriter.WrittenMemory,
                bodyOwner: bodyWriter,
                cancellationToken: ct).ConfigureAwait(false);
            bodyWriter = null; // 所有权已移交客户端
        }
        catch (PublishReturnException ex)
        {
            // 路由失败诊断（7.2.0 的 basic.return 专用异常）：基类 PublishException 只有
            // 泛化消息，缺 exchange/routingKey/replyCode/replyText——NO_ROUTE 排障原本要回
            // broker 端查。此处仅补日志后原样重抛，重试与 Outbox 语义不变（ITM-213 的
            // mandatory:true 契约不受影响）。
            _logger.Warning(
                $"Message not routed (NO_ROUTE) to exchange {ex.Exchange}, routingKey={ex.RoutingKey}, " +
                $"reply={ex.ReplyCode} {ex.ReplyText}: {descriptor.ClrType.Name}");
            // v4 批 D（S3）：发布失败计数器（NO_ROUTE 是丢消息的直接原因，必须可见）
            publishActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            PalMetrics.BrokerPublishFailed.Add(1);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // F2（2026-11-08）：非 NO_ROUTE 的发布失败（连接断/确认失败等）同计——对齐
            // PalDiagnostics.BrokerPublishFailed description「路由失败/连接断/确认失败」与 Kafka
            // 发布 catch 口径。修前仅 catch PublishReturnException，其余发布异常逃逸不计数，
            // 按 description 配「连接断」告警会假阴性。throw 保持调用方重试/Outbox 语义不变。
            publishActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            PalMetrics.BrokerPublishFailed.Add(1);
            throw;
        }
        finally
        {
            // 覆盖「序列化或 exchange 声明阶段抛出，所有权未转移」路径；发布失败时客户端已释放
            // bodyOwner，PooledBodyWriter.Dispose 的 Interlocked 门保证池缓冲只归还一次。
            bodyWriter?.Dispose();
        }

        // 优化（二十五轮 Z-1）：同 KafkaBroker——Debug 级门控免白做插值
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.Debug($"Published message {descriptor.ClrType.Name} to exchange {exchange}");
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "接口成员必须为实例成员；能力值由 broker 实现类型决定。")]
    public override bool SupportsRawPublish => true;

    /// <summary>批量 handler 异常记录后继续消费后续批次（消息已按 at-most-once 弃置）。</summary>
    protected override void OnBatchHandlerFailed(Exception ex)
        => _logger.Error(ex, "RabbitMQ batch handler failed; subsequent batches continue");

    /// <inheritdoc />
    public override async ValueTask PublishRawAsync(
        ReadOnlyMemory<byte> payload,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentOutOfRangeException.ThrowIfEqual(messageId, default);

        var exchange = descriptor.Name;
        // R17 D9：raw 路径补 publish span（对齐对象路径 :229）——修前整条路径无 span，
        // 链路追踪缺发布跳。FIX-016 的教训：span 必须在 try 外建，catch 分支才能 SetStatus(Error)。
        using var publishActivity = PalActivitySource.StartBrokerPublish(descriptor.Name, descriptor.Name);
        try
        {
            // R17 D9：exchange 声明移入 try（对齐对象路径 FIX-011）——修前位于 try 外，
            // broker 不可达时声明失败逃逸出 BrokerPublishFailed 计数与 span Error。
            var cachedExchange = await EnsureExchangeAsync(exchange).ConfigureAwait(false);
            await _channel.BasicPublishAsync(
                exchange: cachedExchange,
                routingKey: CachedString.Empty,
                mandatory: true,
                basicProperties: CreateProperties(descriptor, messageId, context),
                body: payload,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (PublishReturnException ex)
        {
            _logger.Warning(
                $"Message not routed (NO_ROUTE) to exchange {ex.Exchange}, routingKey={ex.RoutingKey}, " +
                $"reply={ex.ReplyCode} {ex.ReplyText}: {descriptor.ClrType.Name}");
            // v4 批 D（S3）：raw 发布路径同计
            publishActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            PalMetrics.BrokerPublishFailed.Add(1);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // F2（2026-11-08）：raw 路径非 NO_ROUTE 的发布失败同计（对象路径同形勘正）。
            publishActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            PalMetrics.BrokerPublishFailed.Add(1);
            throw;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.Debug($"Published message {descriptor.ClrType.Name} to exchange {exchange}");
    }

    /// <summary>
    /// 声明 exchange 并返回 CachedString（对象发布与 raw 发布共用单一出处）。
    /// 声明任务化：并发发布者 await 同一任务，杜绝“声明飞行中直接 publish → 404 关 channel”；
    /// 声明任务不连坐（CancellationToken.None），发布取消由各发布调用独立承担。
    /// </summary>
    private async ValueTask<CachedString> EnsureExchangeAsync(string exchange)
    {
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
            // 声明失败回滚占位——仅当字典中仍是本失败任务时移除，下次发布重新声明
            _exchangeDeclarations.TryRemove(new KeyValuePair<string, Task>(exchange, declaration));
            throw;
        }

        // CachedString 重载缓存 exchange 名的 UTF-8 字节表示，免每发布 string→bytes 编码分配
        return _cachedExchanges.GetOrAdd(exchange, static name => new CachedString(name));
    }

    private static BasicProperties CreateProperties(
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context)
        => new()
        {
            MessageId = messageId.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId = context.CorrelationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            ContentType = descriptor.ContentType,
            Type = descriptor.Name,
            Persistent = true,
            Headers = CreateHeaders(context)
        };

    // EPSL 地毯审计 2026-10-11 B-5：同 KafkaBroker——默认发布路径（Empty 上下文）此前
    // 无条件 new Dictionary（~48B/条随消息分配）；RabbitMQ.Client 的
    // BasicProperties.Headers 为 null 时客户端不写头字段（wire 语义等价）——按需构造。
    // 消费侧对位：FromHeaders(null 头, correlation 兜底) 走空头快路径。
    internal static Dictionary<string, object?>? CreateHeaders(MessagePublishContext context)
    {
        // P2 修复（八轮评审）：键名与消费端 MessageConsumeContext.FromHeaders 共用常量，锁读写两侧一致
        Dictionary<string, object?>? headers = null;
        AddHeader(ref headers, MessageConsumeContext.HeaderNames.TraceParent, context.TraceParent);
        AddHeader(ref headers, MessageConsumeContext.HeaderNames.TraceState, context.TraceState);
        AddHeader(ref headers, MessageConsumeContext.HeaderNames.CausationId, context.CausationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        // ADR-039 / 审计 A3：调用方自定义头；框架头优先（已知键名冲突静默跳过）
        AddExtraHeaders(ref headers, context.ExtraHeaders);
        return headers;
    }

    /// <summary>ADR-039：追加 <see cref="MessagePublishContext.ExtraHeaders"/>（null/空零分配）。</summary>
    private static void AddExtraHeaders(ref Dictionary<string, object?>? headers, IReadOnlyDictionary<string, string?>? extra)
    {
        if (extra is null || extra.Count == 0)
            return;

        foreach (var (name, value) in extra)
        {
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(value)
                || MessageConsumeContext.HeaderNames.IsKnown(name))
                continue;
            (headers ??= new Dictionary<string, object?>(StringComparer.Ordinal)).Add(name, Encoding.UTF8.GetBytes(value));
        }
    }

    private static void AddHeader(ref Dictionary<string, object?>? headers, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        (headers ??= new Dictionary<string, object?>(StringComparer.Ordinal)).Add(name, Encoding.UTF8.GetBytes(value));
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
    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        // 批次 A（2026-11-04）：本重载不再承载传输核心——核心下沉到携带 IMessageAck 的四参
        // 重载，本重载把 broker 提供的确认器丢弃。丢弃是安全的：handler 不持有确认器时
        // 其 IsSettled 恒 false，兜底规则（成功 BasicAck / 异常 Nack(false)）与 v5.0.0-preview.1
        // 逐字一致，旧调用方零行为变化。
        => SubscribeAsync<TMessage>((message, context, _, token) => handler(message, context, token), ct);

    /// <summary>
    /// 异步订阅消息（含消费上下文与消费确认器）— 传输核心。
    /// </summary>
    /// <remarks>
    /// 本 Broker 的队列本就是 manual-ack（成功 <c>BasicAck</c>、失败 <c>Nack(false)</c>），
    /// 确认器只是把已有的确认动作交给 handler 决策：<c>AcknowledgeAsync</c> → <c>BasicAck</c>、
    /// <c>RejectAsync(true)</c> → <c>Nack(requeue:true)</c>、<c>RejectAsync(false)</c> →
    /// <c>Nack(requeue:false)</c>。每次交付一个确认器，重复结算被原子标志拦下并留 Warning
    /// （重复 <c>BasicAck</c> 会以 channel 关闭的形式波及同 channel 的其它订阅与发布）。
    /// </remarks>
    public override async ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, IMessageAck, CancellationToken, ValueTask> handler, CancellationToken ct = default)
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

        // M3（2026-10-19）：默认每订阅独立 consumer channel；发布 channel 只承担发布/确认。
        // 共享单 channel 旧行为可通过 UseSharedConsumerChannel=true 复现。
        var ownsConsumerChannel = !UseSharedConsumerChannel;
        var consumerOptions = _consumerDispatchConcurrency is { } concurrency
            ? new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: concurrency)
            : null;
        var consumerChannel = ownsConsumerChannel
            ? await _connection.CreateChannelAsync(consumerOptions, ct).ConfigureAwait(false)
            : _channel;

        // 2026-11-23 性能轮：批量 multiple-ack 聚合授权（判据见 ReceivedAsync 处注释）。
        // 共享 channel（ownsConsumerChannel=false）恒关；并发 dispatch >1 恒关。
        var batchAggregateEnabled = CanAggregateBatchAck(ownsConsumerChannel, _consumerDispatchConcurrency);

        try
        {
            await consumerChannel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true, cancellationToken: ct).ConfigureAwait(false);
            await consumerChannel.QueueDeclareAsync(queueName, durable: false, exclusive: true, autoDelete: true, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            // F14（2026-10-21）：原实现在此无 catch——ct 在 channel 创建后取消、或声明失败时，
            // 每订阅独立 channel 模式会残留 1 个 channel 到连接关闭。
            await DisposeConsumerChannelSafeAsync(consumerChannel, ownsConsumerChannel, queueName).ConfigureAwait(false);
            throw;
        }

        // v33 P3：队列删除清理的共享收口——v31 初始化失败 catch 与本轮扩围后的新 catch
        // 共用（避免两处 catch 各自内联同一段尽力删除逻辑）
        async Task CleanupDeclaredQueueAsync()
        {
            // v31 P3 修复：尽力删除已声明的匿名队列——autoDelete 仅在"有过消费者后全部
            // 断开"才删除，从未有消费者的 exclusive 队列随连接关闭才消亡；连接长驻时每次
            // 初始化失败会累积一个空壳队列。失败吞掉（channel 已关等，对齐安全包装模式）
            try
            {
                // R14：清理不随订阅取消——ct 已取消时 QueueDelete 抛 OCE 会逃出下方过滤，
                // 跳过调用方的 channel 释放与 linkedCts.Dispose。
                await consumerChannel.QueueDeleteAsync(queueName, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 清理链路不响应取消；OCE 已由 None 消除，此处仅防御驱动层意外取消。
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning($"Queue cleanup after failed subscribe was skipped (channel closed?): {queueName}: {ex.Message}");
            }
        }

        var consumer = new AsyncEventingBasicConsumer(consumerChannel);
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
            await consumerChannel.QueueBindAsync(queueName, exchange, "", cancellationToken: ct).ConfigureAwait(false);
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        catch
        {
            // QueueBind 失败时 consumer 尚未注册消费（无 consumerTag），清理只需队列删除；
            // CreateLinkedTokenSource 抛出时无 CTS 对象产生，同样只需队列删除
            await CleanupDeclaredQueueAsync().ConfigureAwait(false);
            await DisposeConsumerChannelSafeAsync(consumerChannel, ownsConsumerChannel, queueName).ConfigureAwait(false);
            throw;
        }
        // R18 审计缺陷 1：订阅 channel shutdown 事件，覆盖「空闲断连无结算动作」的故障面。
        // 修前 _consumeFaultReason 只在五个结算/取消安全包装 catch 置位；服务端重启、
        // 网络分区等导致 channel 关闭而消费空闲时，无任何包装被调用 → 故障位保持 null →
        // IsConsumingHealthy 恒 true → /health 恒绿，实例停止消费却不被摘流量。
        // 上游 AsyncEventingBasicConsumer.ShutdownAsync 会主动通知（client/server initiated），
        // 本订阅补齐该观测点。判据只用 linkedCts.IsCancellationRequested：本仓正常关停
        // （订阅句柄释放/DisposeAsync）先 Cancel 再关 channel，故 shutdown 事件到达时
        // linkedCts 必已取消（不置位）；外部断连或测试/他方主动关闭连接时 linkedCts 未取消
        // （置位）。不能用 Initiator=Application 判「本仓关停」——实测客户端主动 CloseAsync(200)
        // 也标成 Application，无法区分，会漏掉空闲断连（CI 真 broker 实证）。
        Task OnConsumerShutdownAsync(object _, ShutdownEventArgs args)
        {
            if (!(linkedCts?.IsCancellationRequested ?? true))
            {
                var reason = args.Exception?.Message ?? args.ReplyText;
                // ShutdownAsync 事件本身即 channel 关闭的权威信号——不走
                // MarkChannelFaultIfClosed 的 IsOpen 二次检查：事件回调早于 IsOpen
                // 翻转时被挡掉会漏置（CI 真 broker 实证）。
                // initiator 仅作原因文本的一部分（Application 也记录，便于诊断谁关的连接）；
                // 置位判据不靠它——客户端 CloseAsync(200) 同样标 Application，
                // 与「本仓正常关停」不可区分，唯一可靠信号是 linkedCts 是否已取消。
                var initiatorText = args.Initiator == ShutdownInitiator.Application
                    ? "application-initiated"
                    : args.Initiator.ToString();
                var fault = new InvalidOperationException($"consumer channel shutdown ({initiatorText}): {reason}");
                _consumeFaultReason ??= $"{fault.GetType().Name}: {fault.Message} ({queueName})";
            }
            return Task.CompletedTask;
        }
        consumer.ShutdownAsync += OnConsumerShutdownAsync;

        consumer.ReceivedAsync += async (_, ea) =>
        {
            // 批次 A（2026-11-04）：每次交付一个确认器——handler 显式结算时由它调 broker API，
            // 未结算时下方兜底分支按既有规则收尾（成功 BasicAck / 异常 Nack(false)）。
            // 2026-11-23 性能轮：批量聚合授权在订阅时算好传入——独立 consumer channel
            // （默认）+ 串行 dispatch（ConsumerDispatchConcurrency null 或 ≤1，客户端默认 1）
            // 时 deliveryTag 严格单调入批，multiple:true 单帧等价逐条；两种语义前提任一
            // 不满足（共享 channel 多订阅共 tag 空间 / 并发 dispatch 打乱单调性）则关闭。
            var ack = new RabbitMqMessageAck(
                this, consumerChannel, ea.DeliveryTag, queueName, batchAggregateEnabled);
            // v4 批 A（B2）：入队确认——只有泵的四参 handler 正常返回才置 true。
            // 批量路径的 handler 就是 MessageBatchPump.EnqueueAsync：它先同步打 deferred
            // 标记，再 WriteAsync 入通道。WriteAsync 在关停/通道关闭时抛 OCE 或
            // ChannelClosedException，item 未入通道，泵永远看不到这条确认器、不会结算，
            // broker 必须自行 Nack——否则 delivery 恒 unacked 占用 prefetch 名额。
            var enqueueConfirmed = false;
            try
            {
                // 2026-10-19 性能实施轮：泛型反序列化，免运行时 Type 查找与值类型装箱。
                TMessage? message = Serializer.Deserialize<TMessage>(ea.Body.Span, descriptor);
                if (message is not null)
                {
                    // P2 修复（八轮评审）：消费端还原追踪头——写侧 CreateHeaders 的镜像，
                    // correlation 兜底读 BasicProperties.CorrelationId（写侧未写 x-correlation-id 头）
                    var consumeContext = MessageConsumeContext.FromHeaders(
                        ea.BasicProperties.Headers, ea.BasicProperties.CorrelationId, ea.BasicProperties.MessageId);
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
                        await TryNackSafeAsync(consumerChannel, ea.DeliveryTag, requeue: false, queueName).ConfigureAwait(false);
                        return;
                    }
                    using (deliveryCts)
                    {
                        // ADR-039 / 审计 A5：消费跳 span（与发布侧 StartBrokerPublish 配对）
                        using var consumeActivity = PalActivitySource.StartBrokerConsume(exchange, typeof(TMessage).Name);
                        await handler(message, consumeContext, ack, deliveryCts.Token).ConfigureAwait(false);
                        // v4 批 A（B2）：handler 正常返回 = 泵已把 item 写入通道并入批。
                        enqueueConfirmed = true;
                        // 兜底确认 — handler 未显式结算时按处理成功 ACK（旧行为）
                        // P3 修复：ACK 与 Nack 同样加保护——channel 已关时异常逃逸进消费者回调
                        // v4 批 A（B2）：批量泵在 WriteAsync 之前把结算权移交批次，此处必须显式
                        // 查 IsDeferred——CWT 标记不置 ack.IsSettled，只查 IsSettled 会提前 ACK，
                        // 与批次收尾结算冲突（pre-ACK 主缺陷就在此分支）。
                        if (!DeferredAckRegistry.IsDeferred(ack) && !ack.IsSettled)
                            await TryAckSafeAsync(consumerChannel, ea.DeliveryTag, queueName).ConfigureAwait(false);
                    }
                }
                else
                {
                    // 反序列化返回 null — 消息无法处理，不重试
                    // FIX-016（2026-11-08）：反序列化 null 按消费失败计（修前漏计）。
                    PalMetrics.BrokerConsumeFailed.Add(1);
                    _logger.Warning($"Deserializing {typeof(TMessage).Name} returned null, discarding message: {queueName}");
                    await TryNackSafeAsync(consumerChannel, ea.DeliveryTag, requeue: false, queueName).ConfigureAwait(false);
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
                // v4 批 A（B2）：批量路径下若确认器已移交批次**且** item 已确认入通道
                // （enqueueConfirmed），结算权归泵的批次收尾，此处跳过以免二次 Nack。
                // WriteAsync 抛 OCE 时 item 未入通道、泵看不到这条 ack，必须由 broker 兜底
                // Nack，否则 delivery 恒 unacked 占用 prefetch 名额（红队 A-2 实证）。
                // FIX-003：判据经 ShouldFallbackSettle 单一来源（含 IsSettled 判据）。
                await TrySettleFallbackAsync(
                    consumerChannel, ea.DeliveryTag, requeue: false, queueName, ack, enqueueConfirmed)
                    .ConfigureAwait(false);
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
                // v4 批 A（B2）：判据同上方的关停分支——已移交且入队确认才交给批次结算。
                // v4 批 D（S3）：非关停 OCE 按消费失败计（关停窗口不计）
                // FIX-003：判据经 ShouldFallbackSettle 单一来源（含 IsSettled 判据）。
                PalMetrics.BrokerConsumeFailed.Add(1);
                _logger.Error(ex, $"Handler for {typeof(TMessage).Name} message threw a non-shutdown OperationCanceledException, discarding: {queueName}");
                await TrySettleFallbackAsync(
                    consumerChannel, ea.DeliveryTag, requeue: false, queueName, ack, enqueueConfirmed)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // v4 批 D（S3）：handler 异常按消费失败计
                PalMetrics.BrokerConsumeFailed.Add(1);
                _logger.Error(ex, $"Failed to handle {typeof(TMessage).Name} message, discarding (anonymous queue): {queueName}");
                // P2 定案：exclusive 队列的消费者只有本连接——requeue:true 会立即重投给自己，
                // 持续失败时形成无退避热循环。与 Kafka 路径 ITM-008 对齐：记录后弃置（at-most-once）。
                // 需要失败重试语义的应用应使用持久队列 + DLX，由自身的 Broker 配置承载。
                // 批次 A：handler 已显式结算（含 RejectAsync(true)）时不覆盖其决定。
                // v4 批 A（B2）：已移交且入队确认的确认器由泵的批次收尾结算；WriteAsync 抛
                // ChannelClosedException 等异常时 item 未入通道，必须由 broker 兜底 Nack——
                // 只查 IsDeferred 会跳过而悬空（v4 §2.1 表 :564 判据的实施轮补强）。
                // FIX-003：判据经 ShouldFallbackSettle 单一来源。
                await TrySettleFallbackAsync(
                    consumerChannel, ea.DeliveryTag, requeue: false, queueName, ack, enqueueConfirmed)
                    .ConfigureAwait(false);
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
            await consumerChannel.BasicQosAsync(0, _prefetchCount, false, ct).ConfigureAwait(false);
            consumerTag = await consumerChannel.BasicConsumeAsync(queueName, autoAck: false, consumer, cancellationToken: ct).ConfigureAwait(false);

            // FIX-020（2026-11-08）：健康态恢复——新订阅成功即清除粘滞的故障标志。
            // channel 关闭在连接存活时可重建，修前只置位不清除使 /health 永久 Unhealthy。
            // 局限（broker 级标志）：一个订阅恢复会清掉另一订阅的故障——按 channel 跟踪
            // 是更严谨的后续加固（ConditionalWeakTable<IChannel, string>）。
            if (consumerChannel.IsOpen)
                _consumeFaultReason = null;

            // v25 P2-5：ct 取消 → 异步解绑消费者终止消费；channel 已关等异常由安全包装吞成
            // Warning。句柄释放时 Dispose registration 防泄漏（释放与取消并发时，双
            // BasicCancelAsync 的后者同样被安全包装吞掉）
            // CA2025：ct 回调为同步签名，无法 await；取消任务与 consumer channel 释放
            // 由订阅句柄的有序清理（Cancel → Dispose channel）兜底。
#pragma warning disable CA2025
            // F21（2026-10-21）：丢弃的 Task 改为内部 catch-all 观察——原 CancelConsumeSafeAsync
            // 的过滤排除 OCE，逃逸路径无日志且成为未观察任务。
            tokenReg = ct.Register(static state =>
            {
                var (broker, channel, tag, queue) = ((RabbitMqBroker, IChannel, string, string))state!;
                _ = broker.CancelConsumeObservedAsync(channel, tag, queue);
            }, (this, consumerChannel, consumerTag, queueName));
#pragma warning restore CA2025
        }
        catch
        {
            // R18 缺陷 1：初始化失败同样要退订 shutdown 回调（订阅在 ReceivedAsync 前已建立），
            // 否则委托随 channel 存活到连接关闭，关闭时误触健康位置位。
            consumer.ShutdownAsync -= OnConsumerShutdownAsync;
            await tokenReg.DisposeAsync().ConfigureAwait(false);
            if (consumerTag is not null)
                await CancelConsumeSafeAsync(consumerChannel, consumerTag, queueName).ConfigureAwait(false);
            // F14（2026-10-21）：顺序修正——队列删除必须先于 channel 释放。原实现先
            // DisposeConsumerChannelSafeAsync 再 QueueDeleteAsync，删除打在已释放 channel 上
            // 必失败并被吞，exclusive 空队列残留到连接关闭。
            await CleanupDeclaredQueueAsync().ConfigureAwait(false);
            await DisposeConsumerChannelSafeAsync(consumerChannel, ownsConsumerChannel, queueName).ConfigureAwait(false);
            linkedCts.Dispose();
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
            _subscriptions.TryRemove(consumerTag, out _);
            // R18 缺陷 1：先退订 shutdown 回调，再取消 linkedCts、释放 channel、Dispose CTS——
            // 避免正常关停时回调被触发后访问已释放资源（成对 API + 顺序保证）。
            consumer.ShutdownAsync -= OnConsumerShutdownAsync;
            await tokenReg.DisposeAsync().ConfigureAwait(false);
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await CancelConsumeSafeAsync(consumerChannel, consumerTag, queueName).ConfigureAwait(false);
            await DisposeConsumerChannelSafeAsync(consumerChannel, ownsConsumerChannel, queueName).ConfigureAwait(false);
            linkedCts.Dispose();
        });
        _subscriptions[consumerTag] = subscription;
        return subscription;
    }

    /// <inheritdoc />
    /// <remarks>
    /// v4 批 C（S1）：Rabbit 的健康态定义为「channel 级不可恢复故障」——per-delivery
    /// <c>ReceivedAsync</c> 回调模型没有 Kafka 式「消费线程终止」观测点，可观测的故障面是
    /// channel 关闭。R18 缺陷 1 勘正：置位点有二，一是结算/取消安全包装抛异常且
    /// <c>IChannel.IsOpen == false</c>，二是 <c>consumer.ShutdownAsync</c> 外部断连事件
    /// （修前只有前者，空闲断连无结算动作时不置位，/health 恒绿）。
    /// 普通 handler 失败（毒消息弃置）<b>不置位</b>——那是 at-most-once 弃置，不是链路终止。
    /// </remarks>
    public bool IsConsumingHealthy => _consumeFaultReason is null;

    /// <inheritdoc />
    public string? LastFaultReason => _consumeFaultReason;

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
    private async Task TryNackSafeAsync(IChannel channel, ulong deliveryTag, bool requeue, string queueName)
    {
        try
        {
            await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: requeue).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"BasicNack failed (channel closed?): {queueName}, deliveryTag={deliveryTag}: {ex.Message}");
            MarkChannelFaultIfClosed(channel, ex, queueName);
        }
    }

    /// <summary>
    /// FIX-003（2026-11-08 综合审计 P1-3）：broker 兜底结算判据的单一来源——三个 catch
    /// 分支共用，结构上消除判据分散的遗漏可能。修前关停/非关停 OCE 两 catch 缺
    /// <c>IsSettled</c> 判据（通用 catch 有），对已结算的 deliveryTag 二次 Nack 会触发
    /// <c>PRECONDITION_FAILED</c> 关闭 channel，共用该 channel 的全部订阅与发布一并瘫痪。
    /// </summary>
    private static bool ShouldFallbackSettle(RabbitMqMessageAck? ack, bool enqueueConfirmed)
        => ack is not null
           && !ack.IsSettled
           && (!DeferredAckRegistry.IsDeferred(ack) || !enqueueConfirmed);

    /// <summary>
    /// FIX-003：broker 兜底结算的包装——判据经 <see cref="ShouldFallbackSettle"/> 单一来源，
    /// 检查下沉到本层避免调用点各自重复判据（双保险）。
    /// </summary>
    private Task TrySettleFallbackAsync(
        IChannel channel, ulong deliveryTag, bool requeue, string queueName,
        RabbitMqMessageAck? ack, bool enqueueConfirmed)
    {
        if (!ShouldFallbackSettle(ack, enqueueConfirmed))
            return Task.CompletedTask;
        return TryNackSafeAsync(channel, deliveryTag, requeue, queueName);
    }

    /// <summary>
    /// 批量聚合授权判定（2026-11-23 性能轮）——提为纯函数供白盒测试锁定：
    /// 独立 consumer channel（每订阅独占 tag 空间）且消费分发串行
    /// （<paramref name="consumerDispatchConcurrency"/> null = 客户端默认 1，或显式 ≤1）
    /// 时，deliveryTag 严格单调入批，单帧 multiple ack/nack 语义等价逐条；任一前提
    /// 不满足（共享 channel 多订阅共 tag 空间 / 并发 dispatch 打乱单调性）返回 false。
    /// </summary>
    internal static bool CanAggregateBatchAck(bool ownsConsumerChannel, ushort? consumerDispatchConcurrency)
        => ownsConsumerChannel && consumerDispatchConcurrency is not > 1;

    /// <summary>
    /// ACK 的安全包装——与 TryNackSafeAsync 同型（P3 修复：成功路径 channel 已关时
    /// BasicAckAsync 抛异常同样会逃逸进消费者回调）。
    /// </summary>
    private async Task TryAckSafeAsync(IChannel channel, ulong deliveryTag, string queueName)
    {
        try
        {
            await channel.BasicAckAsync(deliveryTag, multiple: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"BasicAck failed (channel closed?): {queueName}, deliveryTag={deliveryTag}: {ex.Message}");
            MarkChannelFaultIfClosed(channel, ex, queueName);
        }
    }

    /// <summary>
    /// 批量聚合 ACK 的安全包装（2026-11-23 性能轮）——单帧 <c>multiple:true</c> 结算整批，
    /// 与 <see cref="TryAckSafeAsync"/> 同型的 channel 已关防护。成员占位已在聚合器内完成，
    /// 帧失败只记 Warning（语义对齐逐条路径第 i 帧失败后跳过剩余成员）。
    /// </summary>
    private async Task TryAckMultipleSafeAsync(IChannel channel, ulong maxDeliveryTag, string queueName)
    {
        try
        {
            await channel.BasicAckAsync(maxDeliveryTag, multiple: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"BasicAck(multiple) failed (channel closed?): {queueName}, deliveryTag={maxDeliveryTag}: {ex.Message}");
            MarkChannelFaultIfClosed(channel, ex, queueName);
        }
    }

    /// <summary>
    /// 批量聚合 Nack 的安全包装（2026-11-23 性能轮）——单帧 <c>multiple:true</c> 弃置/重投整批，
    /// 与 <see cref="TryNackSafeAsync"/> 同型的 channel 已关防护。
    /// </summary>
    private async Task TryNackMultipleSafeAsync(IChannel channel, ulong maxDeliveryTag, bool requeue, string queueName)
    {
        try
        {
            await channel.BasicNackAsync(maxDeliveryTag, multiple: true, requeue: requeue).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"BasicNack(multiple) failed (channel closed?): {queueName}, deliveryTag={maxDeliveryTag}: {ex.Message}");
            MarkChannelFaultIfClosed(channel, ex, queueName);
        }
    }

    /// <summary>
    /// v4 批 C（S1）：channel 关闭即置健康故障态。判据用 <c>IChannel.IsOpen</c>（RabbitMQ.Client
    /// 7.x 公开属性）而非具体异常类型——全仓零 <c>AlreadyClosedException</c> 引用，无现成依据；
    /// channel 仍 open 时的失败（如瞬时协议错误）不置位，避免把可恢复故障误报成链路终止。
    /// </summary>
    private void MarkChannelFaultIfClosed(IChannel channel, Exception ex, string queueName)
    {
        // IsOpen 在 channel 已释放后读取安全（返回 false，不抛）
        if (channel.IsOpen)
            return;

        // 记录原因不含凭据：异常类型 + 消息 + 队列名即可定位
        _consumeFaultReason ??= $"{ex.GetType().Name}: {ex.Message} ({queueName})";
    }

    /// <summary>
    /// 单次投递的消费确认器（批次 A，2026-11-04）——把既有 manual-ack 动作交给 handler 决策。
    /// 2026-11-23 性能轮：实现 <c>IBatchAckAggregator</c>（internal），批量泵的批次级
    /// 结算可聚合成单帧 <c>multiple:true</c> ack/nack。
    /// </summary>
    /// <remarks>
    /// 嵌套在 Broker 内以复用 <see cref="TryAckSafeAsync"/>/<see cref="TryNackSafeAsync"/> 的
    /// 安全包装（channel 已关时抛异常会中断消费者），因此不作为公共 API 暴露——调用方只能
    /// 通过订阅重载拿到实例。
    /// <para>
    /// <b>重复结算防护</b>：<see cref="TryClaim"/> 是原子占位，只有胜者真正调用 broker API；
    /// 败者留 Warning 后静默返回。需要这层防护的原因是 RabbitMQ 对同一 deliveryTag 二次
    /// <c>BasicAck</c> 会以 <c>PRECONDITION_FAILED</c> 关闭整个 channel——共用该 channel 的
    /// 其它订阅与发布一起瘫痪。异常不外抛同样是刻意的：确认器在 handler 内被调用，
    /// 抛出会打断业务逻辑，而结算失败本身已由安全包装记 Warning。
    /// </para>
    /// <para>
    /// <b>批量聚合（2026-11-23）</b>：授权由订阅时判定（独立 channel + 串行 dispatch，
    /// 见四参 <c>SubscribeAsync</c> 重载内 <c>batchAggregateEnabled</c>）。聚合校验：① 授权在场；② 全员同 channel；③ 批内
    /// deliveryTag 严格连续递增（tag 洞只可能来自 broker 兜底已弃置的交付——不在服务端
    /// unacked map，multiple 帧语义上安全，但保守回退逐条，把「正确性依赖服务端 map 状态」
    /// 收窄到可静态判定的形态）。任一不满足返回 false（零副作用），调用方逐条结算。
    /// </para>
    /// <para>
    /// <c>ct</c> 不参与结算：AMQP 的 ack/nack 是通道级单帧写，无法在中途取消，且结算
    /// 必须尽可能完成（取消语义交由调用方在调用前判断）。
    /// </para>
    /// </remarks>
    private sealed class RabbitMqMessageAck(
        RabbitMqBroker broker,
        IChannel channel,
        ulong deliveryTag,
        string queueName,
        bool batchAggregateEnabled) : IMessageAck, IBatchAckAggregator
    {
        private int _settled;

        /// <summary>本次投递是否已结算（兜底分支据此避免二次 ack/nack）。</summary>
        public bool IsSettled => Volatile.Read(ref _settled) != 0;

        /// <summary>原子占位：首次返回 <c>true</c>。</summary>
        public bool TryClaim() => Interlocked.Exchange(ref _settled, 1) == 0;

        /// <summary>本确认器的 deliveryTag（聚合校验用；批量路径外不暴露）。</summary>
        public ulong DeliveryTag => deliveryTag;

        /// <summary>本确认器所属 channel（同批成员归属校验用）。</summary>
        public IChannel Channel => channel;

        /// <inheritdoc />
        public async ValueTask AcknowledgeAsync(CancellationToken ct = default)
        {
            if (!TryClaim())
            {
                broker._logger.Warning(
                    $"Duplicate acknowledge ignored (already settled): {queueName}, deliveryTag={deliveryTag}");
                return;
            }

            await broker.TryAckSafeAsync(channel, deliveryTag, queueName).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask RejectAsync(bool requeue, CancellationToken ct = default)
        {
            if (!TryClaim())
            {
                broker._logger.Warning(
                    $"Duplicate reject ignored (already settled): {queueName}, deliveryTag={deliveryTag}, requeue={requeue}");
                return;
            }

            await broker.TryNackSafeAsync(channel, deliveryTag, requeue, queueName).ConfigureAwait(false);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 聚合路径：全员 TryClaim 占位后，以最大 deliveryTag 发一帧 <c>multiple:true</c>
        /// ack——服务端确认该 channel unacked map 中 ≤ tag 的全部条目。占位失败（成员已被
        /// 单独结算，防御分支——批量泵移交后 broker 不兜底，正常不可达）的成员 tag 不在
        /// 服务端 unacked map，multiple 帧不受影响，仅跳过其 TryClaim。帧发送失败由
        /// <see cref="RabbitMqBroker.TryAckMultipleSafeAsync"/> 安全包装吞掉并记 Warning——语义与
        /// 逐条路径第 i 帧失败后跳过剩余成员一致（后续交付随连接/ channel 恢复重投，
        /// at-least-once 由 unacked 状态兜底）。
        /// </remarks>
        public async ValueTask<bool> TryAcknowledgeBatchAsync(IMessageAck[] batch, CancellationToken ct)
        {
            if (!TryPrepareAggregate(batch, out var maxTag))
                return false;

            await broker.TryAckMultipleSafeAsync(channel, maxTag, queueName).ConfigureAwait(false);
            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 聚合路径与 ack 同型（multiple:true + requeue 单帧）。Kafka 的逆序委派理由
        /// （Seek 最后一次生效）对本实现不适用：单帧无顺序语义。
        /// </remarks>
        public async ValueTask<bool> TryRejectBatchAsync(IMessageAck[] batch, bool requeue, CancellationToken ct)
        {
            if (!TryPrepareAggregate(batch, out var maxTag))
                return false;

            await broker.TryNackMultipleSafeAsync(channel, maxTag, requeue, queueName).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// 聚合前置校验与占位：授权在场、首成员即本确认器（<c>MessageBatchAck</c> 以首成员
        /// 路由聚合器）、全员同 channel、deliveryTag 严格连续递增（洞 = broker 兜底已弃置的
        /// 交付——不在服务端 unacked map，语义上安全，但保守回退逐条，把「正确性依赖服务端
        /// map 状态」收窄到可静态判定的形态）。校验失败零占位零副作用，调用方逐条结算。
        /// </summary>
        private bool TryPrepareAggregate(IMessageAck[] batch, out ulong maxTag)
        {
            maxTag = 0;
            if (!batchAggregateEnabled || batch.Length == 0)
                return false;

            var previous = deliveryTag;
            for (var i = 0; i < batch.Length; i++)
            {
                if (batch[i] is not RabbitMqMessageAck rabbitAck
                    || !ReferenceEquals(rabbitAck.Channel, channel))
                    return false;

                if (i == 0)
                {
                    // 首成员即本聚合器（路由协议）；防御非首成员路由的未来变化。
                    if (!ReferenceEquals(rabbitAck, this))
                        return false;
                }
                else if (rabbitAck.DeliveryTag != previous + 1)
                {
                    // 乱序（并发 dispatch 已被授权判定排除，防御保留）或洞 → 拒绝聚合。
                    return false;
                }

                previous = rabbitAck.DeliveryTag;
            }

            // 校验全过才占位（失败路径零副作用）；已结算成员（防御，正常不可达）跳过——
            // 其 tag 不在服务端 unacked map，multiple 帧不波及。
            foreach (var candidate in batch)
                if (candidate is RabbitMqMessageAck rabbitAck)
                    _ = rabbitAck.TryClaim();

            maxTag = previous;
            return true;
        }
    }

    /// <summary>
    /// 取消订阅的安全包装——channel 已关/连接断时 BasicCancelAsync 抛 AlreadyClosed 类异常，
    /// 吞成 Warning（v25 P2-5：ct 取消路径与句柄释放路径共用，对齐 TryNackSafeAsync 模式）。
    /// </summary>
    private async Task CancelConsumeSafeAsync(IChannel channel, string consumerTag, string queueName)
    {
        try
        {
            await channel.BasicCancelAsync(consumerTag).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"BasicCancel failed (channel closed?): {queueName}, consumerTag={consumerTag}: {ex.Message}");
            MarkChannelFaultIfClosed(channel, ex, queueName);
        }
    }

    /// <summary>
    /// F21（2026-10-21）：取消回调的观察包装——取消回调不能 async，原 `_ = CancelConsumeSafeAsync`
    /// 丢弃的 Task 在 OCE 路径无日志且可能成为未观察异常。本方法捕获全部异常并记录。
    /// </summary>
    private async Task CancelConsumeObservedAsync(IChannel channel, string consumerTag, string queueName)
    {
        try
        {
            await CancelConsumeSafeAsync(channel, consumerTag, queueName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"CancelConsume callback failed: {queueName}, consumerTag={consumerTag}");
        }
    }

    /// <summary>释放本订阅自有的 consumer channel；共享 channel 模式不释放。</summary>
    private async Task DisposeConsumerChannelSafeAsync(IChannel channel, bool ownsChannel, string queueName)
    {
        if (!ownsChannel)
            return;
        try
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"Consumer channel dispose failed: {queueName}: {ex.Message}");
        }
    }

    /// <summary>
    /// A2（2026-10-15）：按发布租借的 body 写入器——序列化直写进 ArrayPool 租用的数组，
    /// 超越容量时按倍增继续租借（旧数组就地归还），Dispose 归还当前数组。<br/>
    /// 替代 ThreadStatic 直传形态的共享可变状态（损坏机理见 <see cref="InitialBodyBufferBytes"/>
    /// 处注释）：每次发布独占缓冲至 await 完成，同线程流水线发布互不干扰。
    /// </summary>
    internal sealed class PooledBodyWriter : System.Buffers.IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer;
        private int _written;
        private int _disposed;

        public PooledBodyWriter(int initialCapacity)
            => _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));

        /// <summary>已写入内容（不复制，随下次 Advance/Dispose 失效——调用方须在 Dispose 前消费完毕）。</summary>
        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint <= 0) sizeHint = 1;
            var required = _written + sizeHint;
            if (required <= _buffer.Length) return;
            var next = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(required, _buffer.Length * 2));
            _buffer.AsSpan(0, _written).CopyTo(next);
            System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        /// <summary>
        /// 归还池缓冲。<b>幂等</b>：RabbitMQ.Client 7.3.0 起 <c>BasicPublishAsync(bodyOwner:)</c>
        /// 由客户端在发送完成或发布失败后释放一次，发布路径的 <c>finally</c> 也会释放一次。
        /// 双重释放会把同一数组重复 Return 给 <see cref="System.Buffers.ArrayPool{T}"/>，
        /// 使同一缓冲被两个租用者同时持有。Interlocked 门保证只归还一次。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
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
