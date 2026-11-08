using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
using PalDDD.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Channels;
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
/// 消费循环由专用 <see cref="Thread"/> 承载（Confluent.Kafka 的 Consume 为同步阻塞 API；
/// Task.Run 的 LongRunning 只覆盖首个同步段，续体会回到 ThreadPool——2026-10-21 勘正）。<br/>
/// 后台事件可观测（2026-10-08 审计对齐）：producer/consumer 均接线 ErrorHandler/StatisticsHandler/
/// LogHandler——librdkafka 后台线程的错误/日志/统计此前结构性不可见；致命错误走 Error（携带
/// KafkaException），自愈类错误走 Warning，统计 JSON 需 config 显式设 StatisticsIntervalMs 才回调（Debug 门控）。
/// </remarks>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "Broker 消费循环需记录毒消息失败并继续或优雅关停，需捕获 Exception 基类。")]
public sealed class KafkaBroker : MessageBrokerBase, IAsyncDisposable, IMessageBrokerHealth
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly ConsumerConfig _consumerConfig;
    private readonly IPalLogger<KafkaBroker> _logger;
    private readonly bool _concurrentPublishSafe;
    private readonly List<IAsyncDisposable> _consumers = [];
    // R14：消费线程不可恢复终止的原因（健康检查读取；正常取消/停机不置位）。
    private volatile string? _consumeFaultReason;
    // P2 修复：_consumers 的并发 Add（多线程 SubscribeAsync）与 DisposeAsync 遍历需互斥
    // 优化（二十四轮 OP-2）：object → System.Threading.Lock——全仓最后一个 object 锁，
    // Lock 有 JIT 专用锁内联优化（与其余 8 处模式统一）
    private readonly Lock _consumersLock = new();
    // ITM-217 修复（三十二轮）：DisposeAsync 幂等门（对照 KafkaSubscription._disposed）
    private int _disposed;

    // A13（S1 轮，2026-10-15）：发布路径 TLS body 缓冲——序列化经 IBufferWriter 直写
    // 重载写入本缓冲，只留 Confluent Message.Value 的 byte[] 契约必需的一次 ToArray。
    // 原形态走内存重载 Serialize(object, descriptor)，其在返回前做 WrittenSpan.ToArray
    // （第一次分配+整载荷拷贝），发布处 value.ToArray() 是第二次——直写消除第一次，
    // 每消息省一次全载荷分配 + memcpy。与 RabbitMqBroker 的池化租借形态不同：本处
    // ToArray 产出的独立数组在 await 前已定型（无跨 await 共享缓冲引用），TLS 复用安全。
    // 驻留上限对齐 JsonMessageSerializer 的 MaxResidentBufferBytes 取舍（R9 同族）。
    [ThreadStatic]
    private static System.Buffers.ArrayBufferWriter<byte>? _tlsBodyWriter;

    /// <summary>TLS body 缓冲驻留上限——超阈值弃槽重建（256B 起步），驻留收敛为有界。</summary>
    private const int MaxResidentBodyBytes = 256 * 1024;

    /// <inheritdoc />
    public bool IsConsumingHealthy => _consumeFaultReason is null;

    /// <inheritdoc />
    public string? LastFaultReason => _consumeFaultReason;

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

        // L5（perf-opt-sweep，2026-09-20）：发布超时指引——框架不加超时包装（透传调用方
        // ct），半坏 broker 下 ProduceAsync 的等待时长由本 config 的 librdkafka 属性控制：
        // message.timeout.ms（默认 300s，建议按业务 SLO 显式设置）+ delivery.timeout.ms。
        producerConfig.MessageTimeoutMs ??= 30_000;
        ApplyIdempotenceDefault(producerConfig);
        _logger = logger;
        // Outbox 自动升窗前提：幂等 producer 才保证窗口内重试不乱序。
        // 显式关闭幂等（或显式自定义 acks/retries/in-flight 而未开幂等）时返回 false，
        // 处理器回落逐条确认。
        _concurrentPublishSafe = producerConfig.EnableIdempotence == true;
        // 审计对齐（2026-10-08 全量 API 审计 P2）：官方示例标配三 handler——producer 的
        // 后台事件（librdkafka 线程上的错误/日志/统计）此前结构性不可见
        _producer = new ProducerBuilder<string, byte[]>(producerConfig)
            .SetErrorHandler((_, e) => LogKafkaError("producer", e))
            .SetStatisticsHandler((_, json) =>
            {
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.Debug($"Kafka producer statistics: {json}");
            })
            .SetLogHandler((_, m) => LogLibrdkafkaLog("producer", m))
            .Build();
        _consumerConfig = consumerConfig;
    }

    // EPSL 地毯审计 2026-10-16 A 批 #1：测试 seam——发布路径单测需要可注入的 producer
    //（公开构造的 ProducerBuilder.Build() 产真实客户端，ProduceAsync 会走网络超时）。
    // 仅 Messaging.Integration.Tests 经 IVT 可达（broker 单测归属集成侧工程，架构分层
    // 边界测试约束；B-5 头构造测试同款先例）。
    internal KafkaBroker(
        IProducer<string, byte[]> producer,
        ConsumerConfig consumerConfig,
        IPalLogger<KafkaBroker> logger,
        IMessageSerializer serializer,
        IMessageCatalog messageCatalog)
        : base(serializer, messageCatalog)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(consumerConfig);
        ArgumentNullException.ThrowIfNull(logger);
        _producer = producer;
        _consumerConfig = consumerConfig;
        _logger = logger;
        _concurrentPublishSafe = false; // 注入 producer 无法探测 EnableIdempotence，保守关闭
    }

    /// <summary>
    /// 幂等生产端默认打开（2026-10-21）：仅在调用方未触碰幂等相关四项
    /// （EnableIdempotence/Acks/MaxInFlight/MessageSendMaxRetries）时生效——显式设置
    /// 任一项说明有明确取舍（吞吐/延迟/兼容旧 broker），框架不擅自改写。开启后
    /// librdkafka 按 acks=all、retries=INT_MAX、max.in.flight≤5 运行，重试不产生重复
    /// 也不乱序；这也是 Outbox 自动并发窗口（SupportsConcurrentPublish）的前提。
    /// </summary>
    internal static void ApplyIdempotenceDefault(ProducerConfig config)
    {
        if (config.EnableIdempotence is null
            && config.Acks is null
            && config.MaxInFlight is null
            && config.MessageSendMaxRetries is null)
        {
            config.EnableIdempotence = true;
        }
    }

    /// <inheritdoc />
    public override bool SupportsConcurrentPublish => _concurrentPublishSafe;

    private ushort _consumerDispatchConcurrency = 1;

    /// <summary>
    /// 每订阅的并发 handler 数（默认 1 = 保序逐条）。大于 1 时同一订阅的 handler 可并发
    /// 执行，消息处理顺序不再保证；调用方必须保证 handler 线程安全且幂等。Kafka 自动提交
    /// 与失败消息按 at-most-once 弃置的既有语义不变。必须在 SubscribeAsync 之前设置。
    /// </summary>
    public ushort ConsumerDispatchConcurrency
    {
        get => _consumerDispatchConcurrency;
        set
        {
            if (value == 0)
                throw new ArgumentOutOfRangeException(
                    nameof(value), "ConsumerDispatchConcurrency must be greater than zero.");
            _consumerDispatchConcurrency = value;
        }
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "接口成员必须为实例成员；能力值由 broker 实现类型决定。")]
    public override bool SupportsRawPublish => true;

    /// <summary>批量 handler 异常记录后继续消费后续批次（消息已按 at-most-once 弃置）。</summary>
    protected override void OnBatchHandlerFailed(Exception ex)
        => _logger.Error(ex, "Kafka batch handler failed; subsequent batches continue");

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

        var key = messageId.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        var value = ExtractPayloadArray(payload);
        await PublishSerializedAsync(value, key, descriptor, context, ct).ConfigureAwait(false);
    }

    /// <summary>发布已序列化字节（对象路径与 raw 路径共用）。</summary>
    /// <remarks>
    /// v4 批 D（S3）：发布失败（ProduceException 等）按 <c>paldd.broker.publish_failed</c> 计。
    /// 异常仍原样上抛——Outbox 的重试/死信语义不变，本处只补可观测。
    /// </remarks>
    private async ValueTask PublishSerializedAsync(
        byte[] value,
        string key,
        MessageDescriptor descriptor,
        MessagePublishContext context,
        CancellationToken ct)
    {
        // ADR-039 / 审计 A5：发布跳 span（与消费侧 StartBrokerConsume 配对）
        using var activity = PalActivitySource.StartBrokerPublish(descriptor.Name, descriptor.Name);
        try
        {
            await _producer.ProduceAsync(descriptor.Name, new Message<string, byte[]>
            {
                Key = key,
                Value = value,
                Headers = CreateHeaders(context)
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PalMetrics.BrokerPublishFailed.Add(1);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            throw;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.Debug($"Published {descriptor.ClrType.Name} to Kafka topic {descriptor.Name}, key={key}");
    }

    /// <summary>取整段底层数组（OutboxMessage.Payload 已是独立数组时零拷贝）。</summary>
    private static byte[] ExtractPayloadArray(ReadOnlyMemory<byte> payload)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(payload, out var segment)
            && segment.Array is { } array
            && segment.Offset == 0
            && segment.Count == array.Length)
            return array;
        return payload.ToArray();
    }

    // 审计对齐（2026-10-08）：Kafka 后台事件观测——producer/consumer 共用。
    // librdkafka 的后台错误多为其自愈机制的事件流（临时失联/再平衡中途态），非致命走 Warning；
    // IsFatal 才升 Error（IPalLogger.Error 契约必须携带异常，包装为 KafkaException）。
    private void LogKafkaError(string role, Error error)
    {
        if (error.IsFatal)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.Error(new KafkaException(error), $"Kafka {role} 致命错误 [{error.Code}] {error.Reason}");
        }
        else if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Warning($"Kafka {role} 后台错误 [{error.Code}] {error.Reason}（librdkafka 自愈机制处理，不中断客户端）");
        }
    }

    private void LogLibrdkafkaLog(string role, LogMessage message)
    {
        // SyslogLevel ≤ Warning（syslog 严重级 4）为告警级；Notice/Info/Debug 走 Debug 不刷屏
        if (message.Level <= SyslogLevel.Warning)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.Warning($"Kafka {role} librdkafka [{message.Level}] {message.Message}（{message.Name}）");
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.Debug($"Kafka {role} librdkafka {message.Message}（{message.Name}）");
        }
    }

    /// <summary>发布消息到 Kafka Topic</summary>
    public override async ValueTask PublishAsync(
        object message,
        MessageDescriptor descriptor,
        PalUlid messageId,
        MessagePublishContext context,
        CancellationToken ct = default)
    {
        // v25 P3 守卫族：发布侧 _disposed 守卫（镜像订阅侧 P3-SRC-402 守卫——订阅侧在
        // _consumersLock 临界区 ThrowIf；发布侧不在锁上下文，用 _disposed != 0 直接判定）——
        // Broker 释放后 ProduceAsync 落在已 Dispose 的 producer 上行为未定义，fail-fast。
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentOutOfRangeException.ThrowIfEqual(messageId, default);

        var key = messageId.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        // A13（S1 轮）：IBufferWriter 直写 + 单次 ToArray（Confluent byte[] 契约必需）——
        // 消除内存重载返回前的第一次 WrittenSpan.ToArray 分配+整载荷拷贝。
        // EPSL 地毯审计 2026-10-16 A 批 #1：发布处第二次 ToArray 消除后，单次拷贝落地
        // （旧码双拷净效果为零，分配断言实测 2264B/迭代判红→修复后绿）。
        var bodyWriter = _tlsBodyWriter;
        if (bodyWriter is null || bodyWriter.Capacity > MaxResidentBodyBytes)
        {
            bodyWriter = new System.Buffers.ArrayBufferWriter<byte>(256);
#pragma warning disable S2696 // ThreadStatic 发布缓冲刻意跨实例/线程复用（类头重入声明）
            _tlsBodyWriter = bodyWriter;
#pragma warning restore S2696
        }
        else
        {
            bodyWriter.Clear();
        }
        Serializer.Serialize(message, descriptor, bodyWriter);
        var value = bodyWriter.WrittenMemory.ToArray();

        // Confluent.Kafka 2.16.0 的 Message.Value 需 byte[]；这里是对象路径唯一一次
        // 托管拷贝（raw 路径直接复用 Outbox 的独立数组，无此拷贝）。
        await PublishSerializedAsync(value, key, descriptor, context, ct).ConfigureAwait(false);
    }

    // EPSL 地毯审计 2026-10-11 B-5：默认发布路径（无追踪上下文，PublishAsync 恒传
    // MessagePublishContext.Empty）此前无条件 new Headers()——空对象随每条消息分配
    //（~70B/条）。Confluent.Kafka 的 Message.Headers 默认 null 且 ProduceAsync 接受
    // null（客户端不发头字段，wire 语义等价）——改为按需构造，任一头值存在才建。
    // 消费侧对位：FromKafkaHeaders 无表/空表直返 null，FromHeaders(null) 走空头快路径。
    internal static Headers? CreateHeaders(MessagePublishContext context)
    {
        // P2 修复（八轮评审）：键名与消费端 MessageConsumeContext.FromHeaders 共用常量，锁读写两侧一致
        Headers? headers = null;
        AddHeader(ref headers, MessageConsumeContext.HeaderNames.TraceParent, context.TraceParent);
        AddHeader(ref headers, MessageConsumeContext.HeaderNames.TraceState, context.TraceState);
        AddHeader(ref headers, MessageConsumeContext.HeaderNames.CorrelationId, context.CorrelationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        AddHeader(ref headers, MessageConsumeContext.HeaderNames.CausationId, context.CausationId?.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
        // ADR-039 / 审计 A3：调用方自定义头。框架头优先——先写框架头，业务头只在
        // 不覆盖已知键名时追加（键名冲突静默跳过，不让业务头破坏追踪链）。
        AddExtraHeaders(ref headers, context.ExtraHeaders);
        return headers;
    }

    /// <summary>
    /// ADR-039：追加 <see cref="MessagePublishContext.ExtraHeaders"/>——框架头键名优先，
    /// 冲突项跳过；<c>null</c>/空字典零分配。
    /// </summary>
    private static void AddExtraHeaders(ref Headers? headers, IReadOnlyDictionary<string, string?>? extra)
    {
        if (extra is null || extra.Count == 0)
            return;

        foreach (var (name, value) in extra)
        {
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(value)
                || MessageConsumeContext.HeaderNames.IsKnown(name))
                continue;
            (headers ??= new Headers()).Add(name, Encoding.UTF8.GetBytes(value));
        }
    }

    private static void AddHeader(ref Headers? headers, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        (headers ??= new Headers()).Add(name, Encoding.UTF8.GetBytes(value));
    }

    /// <summary>异步订阅消息 — 适配到带消费上下文的重载（context 恒为 null，零破坏）</summary>
    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        // P2 修复（八轮评审）：旧重载委托适配新重载
        => SubscribeAsync<TMessage>((message, _, token) => handler(message, token), ct);

    /// <summary>异步订阅消息（含消费上下文）— 适配到带消费确认器的重载（确认器被丢弃，零破坏）</summary>
    /// <remarks>
    /// 批次 A（2026-11-04）：传输核心下沉到携带 <see cref="IMessageAck"/> 的四参重载。本重载
    /// 丢弃 broker 提供的确认器——handler 不持有它时 <c>IsSettled</c> 恒 false，而 Kafka 的
    /// 兜底动作是「不做任何存储」（offset 由自动提交推进），与 v5.0.0-preview.1 逐字一致。
    /// </remarks>
    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, CancellationToken, ValueTask> handler, CancellationToken ct = default)
        => SubscribeAsync<TMessage>((message, context, _, token) => handler(message, context, token), ct);

    /// <summary>异步订阅消息（含消费上下文与消费确认器）— 后台线程运行阻塞式消费循环</summary>
    /// <remarks>
    /// Confluent.Kafka 的 Consume 为同步阻塞 API，消费循环必须运行在后台线程。<br/>
    /// 这不是 sync-over-async 反模式——这是与阻塞 IO 库交互的正确方式。<br/>
    /// Task 引用被保存，异常通过日志和 <see cref="KafkaSubscription.ConsumeTask"/> 可观测。<br/>
    /// 消息未携带任何追踪头时 context 为 null。
    /// <para>
    /// <b>确认通路（批次 A，2026-11-04）</b>：Confluent.Kafka 的 <c>IConsumer</c> 非线程安全，
    /// <c>StoreOffset</c>/<c>Seek</c>/<c>Commit</c> 都只能在持有消费者的线程上调用，而 handler
    /// 运行在 worker 线程。因此确认动作经线程内命令队列回送消费线程执行（消费线程每轮
    /// poll 之前排空该队列），<see cref="IMessageAck"/> 的调用在命令被应用后返回。
    /// 消费线程改为带超时的 <c>Consume(100ms)</c> 轮询，以便在两次 poll 之间应用命令；
    /// 消息到达延迟不受影响（有消息时 Consume 立即返回）。
    /// </para>
    /// <para>
    /// <c>EnableAutoOffsetStore=false</c> 时 <c>AcknowledgeAsync</c> 才真正存储 offset；
    /// 该模式下未能确认的消息在重启后按最后提交位点重放（at-least-once）。
    /// <c>EnableAutoCommit=false</c> 时进一步由确认动作显式 <c>Commit</c>（否则存储的 offset
    /// 永不被提交，形成「确认了但仍在重投」的静默失败）。
    /// </para>
    /// <para>
    /// <b>失败路径结算（B3，2026-11-06）</b>：<c>EnableAutoOffsetStore=false</c> 下，handler
    /// 抛异常、反序列化返回 null、或 handler 成功但未显式结算时，框架在 <c>HandleMessageAsync</c>
    /// 的 <c>finally</c> 补一次 <c>RejectAsync(requeue:false)</c> 解冻 offset 水印前缀推进
    ///（与 <c>AcknowledgeAsync</c> 在消费线程走同一分支，等价）。关停窗口（令牌已取消）不补，
    /// 走既有 at-least-once 重启重放；EOF/tombstone 不进 offset 跟踪、不创建确认器。
    /// </para>
    /// </remarks>
    public override ValueTask<IAsyncDisposable> SubscribeAsync<TMessage>(
        Func<TMessage, MessageConsumeContext?, IMessageAck, CancellationToken, ValueTask> handler, CancellationToken ct = default)
    {
        // P3-SRC-402 修复：补 _disposed 守卫（对齐 DisposeAsync 的 ITM-217 幂等门）——
        // Broker 已释放后订阅会登记进已被清空的 _consumers（此后无 DisposeAsync 再遍历），
        // consumer 无人释放且消费循环在已释放 broker 上空转。
        // P3-SRC-602 修复（R45）：守卫移入 _consumersLock 临界区——原锁外读取与 DisposeAsync
        // 的 Exchange+snapshot+Clear 无同步关系，check-then-act 窗口内 Dispose 先行则订阅
        // 仍被登记进已清空列表（守卫缩小了但未消除窗口）；锁内检查与 DisposeAsync 的
        // snapshot 串行化，窗口彻底关闭。
        var descriptor = MessageCatalog.Find(typeof(TMessage))
            ?? throw new InvalidOperationException(
                $"Message type '{typeof(TMessage).FullName}' is not registered in MessageCatalog.");
        var topic = descriptor.Name;
        // v31 P3 修复：cts 创建再前移到 Build 之前——v28 形态中 CreateLinkedTokenSource
        // 位于 Build 之后、try 之外，其抛出（如 ct 源已 Dispose 时 Register 抛 ODE）时已
        // Build 的 consumer（librdkafka native handle 已分配）无人显式 Dispose，仅靠
        // CriticalHandle finalizer 延迟释放。cts 只依赖外部 ct，与 Build 无时序依赖；
        // 创建失败时 consumer 尚未构造，窗口彻底关闭
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // v32 P3 修复：Build 纳入清理域——cts 前移后 Build 抛出时 cts 无人 Dispose（纯托管
        // 对象无泄漏后果，但其 linked 注册滞留至 ct 生命周期）。拆两段 try：Build 失败仅回收
        // cts（consumer 尚未构造）；Subscribe 失败回收两者
        // v33 P3 环境修复：类型声明从具体类 Consumer<,> 换为公开契约 IConsumer<,>——
        // Confluent.Kafka 2.15.0 的 net10.0 资产中具体类 Consumer<,> 为 internal（net11.0
        // 项目按资产选择绑定 net10.0 资产，netstandard2.0 资产才声明 public），显式具体类
        // 声明触发 CS0122。Build() 返回值本就是 IConsumer<,>，接口类型声明零行为变化；
        // 下方 KafkaSubscription 构造参数同为 IConsumer<,>。
        // FIX-002（2026-11-08 综合审计 P1-2）：rebalance 生命周期回调需要 offsetTracker
        // 与 ackCommands——前移到 ConsumerBuilder 之前声明，回调在 Build 时注册、Consume 时
        // 触发，届时两者已初始化。
        var ackCommands = new ConcurrentQueue<AckCommand>();
        var offsetTracker = new OffsetTracker();

        IConsumer<string, byte[]> consumer;
        try
        {
            consumer = new ConsumerBuilder<string, byte[]>(_consumerConfig)
                .SetErrorHandler((_, e) => LogKafkaError("consumer", e))
                .SetStatisticsHandler((_, json) =>
                {
                    if (_logger.IsEnabled(LogLevel.Debug)) _logger.Debug($"Kafka consumer statistics: {json}");
                })
                .SetLogHandler((_, m) => LogLibrdkafkaLog("consumer", m))
                // FIX-002：分区生命周期回调——OffsetTracker 的状态必须与分区所有权同步。
                // 分区被撤销/丢失时作废其水印状态（下次分配以新起点重新初始化），否则
                // rebalance 后新起点 > 旧 Next 时 Done.Remove(Next) 恒失败，位点永久卡住。
                // 未结算的 ack 命令无需显式清理：分区移除后 OffsetTracker.Ack 早退返回 null，
                // 命令自然出队跳过 offset 存储（消息按 at-least-once 重投）。
                .SetPartitionsAssignedHandler((_, partitions) =>
                {
                    foreach (var p in partitions)
                        offsetTracker.Reset(p);
                })
                // Revoked/Lost 的元素是 TopicPartitionOffset（Assigned 是 TopicPartition）——
                // 取 .TopicPartition 归一后重置。
                .SetPartitionsRevokedHandler((_, partitions) =>
                {
                    foreach (var p in partitions)
                        offsetTracker.Reset(p.TopicPartition);
                })
                .SetPartitionsLostHandler((_, partitions) =>
                {
                    foreach (var p in partitions)
                        offsetTracker.Reset(p.TopicPartition);
                })
                .Build();
        }
        catch
        {
            cts.Dispose();
            throw;
        }
        try
        {
            consumer.Subscribe(topic); // 同步订阅（无需网络调用）
        }
        catch
        {
            // ITM-084 修复：Subscribe 抛异常时 consumer 尚未登记进 _consumers（登记在下方
            // 锁登记之后）——无人负责释放，此处显式 Dispose 后重抛，避免连接/组状态泄漏。
            cts.Dispose(); // v28 P3：前移的 cts 一并释放（linked 注册回收，v20 E-P3-2 同款）
            consumer.Dispose();
            throw;
        }

        // P3 修复（十七轮）：登记先行——先创建订阅占位并登记进 _consumers，再启动消费循环。
        // 原顺序 Task.Run 先于登记：循环若在登记前已终止（如订阅后 token 预取消即抛 OCE），
        // DisposeAsync 的 snapshot 不含此订阅 → consumer 无人释放。占位后任何时点的
        // DisposeAsync 都能触达本订阅（DisposeAsync 容忍 _consumeTask 尚未 Set 的窗口）。
        // v30 P3（登记条目移除，镜像 v29 Rabbit 侧 AsyncSubscription 的 TryRemove 形态）：
        // 订阅句柄正常释放后 Broker._consumers 登记条目原先不移除——长驻 Broker 反复
        // 订阅/退订下登记表无界增长。构造注入 removeAction（this 由句柄自身传入，避免
        // 闭包自引用），DisposeAsync 幂等门通过后触发锁内 Remove。
        var subscription = new KafkaSubscription(cts, consumer, RemoveConsumerRegistration);
        try
        {
            lock (_consumersLock)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                _consumers.Add(subscription);
            }
        }
        catch
        {
            // v18 E-1 修复：Dispose 后并发 Subscribe 时守卫抛出——consumer 已 Subscribe 但未登记，
            // 无人负责释放。方法非 async（不能 await DisposeAsync），就地同步释放核心资源：
            // cts.Cancel 终止消费循环，consumer.Dispose 释放连接/组状态；consumeTask 尚未创建
            // （Task.Run 在下方），无 unobserved task 风险。释放后传播 ObjectDisposedException。
            cts.Cancel();
            cts.Dispose(); // v20 E-P3-2：linked 注册即刻回收（原只 Cancel 留至 GC）
            consumer.Dispose();
            throw;
        }

        // v22 E-1：cts.Token 在 lock 后快照一次——Dispose 并发完成时 cts.Token 属性抛 ODE
        // v36 P3：快照创建点（登记后、Task.Run 前）仍裸露于并发 Dispose 窗口——Add 后被
        // 抢占、DisposeAsync 完成 cts.Dispose 后 cts.Token 属性抛原始 ODE，调用方会误判为
        // "对象已释放"的杂散状态而非"订阅无法启动"。转译为 InvalidOperationException（保留
        // ODE 为 InnerException 供诊断）注明并发释放语义；订阅此刻已登记进 _consumers，
        // 其 cts/consumer 资源由 DisposeAsync 统一回收，本异常仅为启动失败的契约表达。
        CancellationToken tokenSnapshot;
        try
        {
            tokenSnapshot = cts.Token;
        }
        catch (ObjectDisposedException ex)
        {
            throw new InvalidOperationException(
                $"Kafka 订阅 {typeof(TMessage).Name} @ {topic} 正在被并发释放（DisposeAsync 已完成），消费循环无法启动。",
                ex);
        }

        // 2026-10-20 性能轮：Consume 是同步阻塞原生调用。原 LongRunning 只覆盖委托的
        // 首个同步段——第一次 await 之后的续体回到 ThreadPool，阻塞 Consume 实际占用
        // 池线程。改为「专用线程只 Consume + 无界 Channel（由 slots 提供背压）+ 异步 worker 处理」：
        // 线程不进入 ThreadPool，slots 提供背压，handler 仍在异步上下文执行。
        // 2026-10-21：worker 支持 maxConcurrent 个在途 handler（默认 1 = 原逐条保序形态）。
        var maxConcurrent = _consumerDispatchConcurrency;

        // 批次 A（2026-11-04）：确认通路状态。
        // manualStore：只有关闭自动存储时 Acknowledge/RejectSkip 才真正存储 offset——否则保持
        // 自动提交语义（Acknowledge 为无操作）。
        var manualStore = _consumerConfig.EnableAutoOffsetStore == false;
        // manualCommit：自动提交也关闭时，存储的 offset 永不被提交 → 必须由确认动作显式 Commit，
        // 否则「确认了却仍在重投」是静默失败（P0 #4 不静默）。仅在 manualStore 成立时有意义。
        var manualCommit = manualStore && _consumerConfig.EnableAutoCommit == false;
        if (manualStore)
        {
            _logger.Information(
                $"Kafka manual offset store enabled: {topic} @ {_consumerConfig.GroupId} — offsets advance only for messages the handler acknowledges"
                + (manualCommit ? " (auto commit disabled: each acknowledge issues an explicit Commit)" : ""));
        }

        // FIX-002：ackCommands/offsetTracker 已前移至 ConsumerBuilder 之前（回调捕获需要）。
        // Seek 退避：批 A 风险项「seek 后立即重新 poll 同一批」的紧循环防线。指数增长到 5s，
        // 有进展（确认/跳过）即复位。本状态只在消费线程读写，无需同步。
        var consecutiveSeeks = 0;
        var backoffUntilTimestamp = 0L;

        // FIX-001（2026-11-08 综合审计 P1-1）：有界 channel 替代 slots——消费线程写入永不
        // 无限阻塞（TryWrite 失败即等待可写或超时后回顶部排空 ack），从根上切断「ack 排空
        // 依赖 slot 释放」的循环等待（原实现 maxConcurrent=1 + handler 结算慢时死锁：
        // handler 等 ack 排空 → 消费线程等 worker ReadAsync → worker 等 handler 完成）。
        // channel 容量 = maxConcurrent，与原 slots 容量一致；worker 侧 inFlight 保留并发
        // 控制，两者不重叠。
        var channel = Channel.CreateBounded<ConsumeResult<string, byte[]>>(
            new BoundedChannelOptions(maxConcurrent)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
        var producerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // F1（2026-11-08）：排空者退出信号。必须在消费线程 finally 的最终排空**之前**置位，
        // 供 KafkaMessageAck.EnqueueAsync 判定「此后入队的确认命令已无人应用」，结算转尽力而为。
        var drainerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerThread = new Thread(() =>
        {
            try
            {
                // FIX-001：pending 保留未写入 channel 的消息，TryWrite 失败时不丢失。
                ConsumeResult<string, byte[]>? pending = null;
                while (!tokenSnapshot.IsCancellationRequested)
                {
                    // 1. 应用 worker 回送的确认命令（只有本线程可以碰 IConsumer）。
                    DrainAckCommands();
                    // 2. Seek 后的指数退避——批 A 风险项：seek 让同一条消息立即重新可读，
                    //    不退避就是无上限紧循环（CPU 打满 + 刷日志）。
                    WaitSeekBackoff();

                    if (pending is null)
                    {
                        ConsumeResult<string, byte[]>? consumed;
                        try
                        {
                            // 批次 A：带超时轮询替换无限期阻塞——两次 poll 之间才有机会应用确认命令
                            // （无限期阻塞时确认要等到下一条消息到达才生效，空闲流上永不生效）。
                            // 有消息时立即返回，消息到达延迟不受影响。
                            consumed = consumer.Consume(TimeSpan.FromMilliseconds(PollIntervalMilliseconds));
                        }
                        catch (OperationCanceledException) when (tokenSnapshot.IsCancellationRequested)
                        {
                            break; // 正常取消
                        }
                        catch (ConsumeException ex)
                        {
                            // 消费错误：记录后继续下一条（ITM-008）。EnableAutoCommit 默认 true——
                            // 失败消息 offset 照常提交，本路径为 at-most-once（与既有语义一致）。
                            _logger.Error(ex, $"Kafka consume error on {topic} @ {_consumerConfig.GroupId}, continuing consumption");
                            // 退避防止持续错误 CPU 空转；专用线程阻塞 1s 不影响线程池。
                            Thread.Sleep(TimeSpan.FromSeconds(1));
                            continue;
                        }

                        if (consumed is null)
                            continue; // poll 超时：回到循环顶部继续应用确认命令

                        // EOF（Message==null）与 tombstone（Value==null）不是可确认的消息——
                        // 不进入 offset 跟踪，否则会把 EOF 的哨兵 offset 记成「下一个待消费位点」。
                        if (manualStore && consumed.Message is not null && consumed.Message.Value is not null)
                            offsetTracker.Consumed(consumed.TopicPartition, consumed.Offset.Value);

                        pending = consumed;
                    }

                    if (!channel.Writer.TryWrite(pending))
                    {
                        // channel 满（inFlight 已达上限）：等待可写或超时。超时回顶部排空 ack
                        // ——这是死锁根治的关键：消费线程永不无限阻塞，ack 应用周期执行。
                        // 用可中断等待而非 WaitToWriteAsync——BCL 的 ChannelWriter 无
                        // (TimeSpan, CancellationToken) 重载，且纯等待不感知 worker 进度；
                        // 100ms 后回顶部排空 ack（与 WaitSeekBackoff 同形态）。
                        tokenSnapshot.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(PollIntervalMilliseconds));
                        continue;
                    }
                    pending = null; // 写入成功，清空待重试
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // R14：消费线程不可恢复终止——置健康故障态（健康检查据此转 Unhealthy），
                // 原实现只记一次 Error，订阅句柄仍有效、/health 恒绿。
                _consumeFaultReason = $"{ex.GetType().Name}: {ex.Message}";
                _logger.Error(ex, $"Kafka consume thread terminated unexpectedly: {topic} @ {_consumerConfig.GroupId}");
            }
            finally
            {
                // 关停/异常退出：仍在队列中的确认命令不会再被应用——以完成收尾而不是让
                // handler 永久阻塞。未提交的位点正是 at-least-once 的预期结果（重启后重放）。
                // F1：退出信号先于最终排空置位——入队晚于此信号的命令无人应用，EnqueueAsync
                // 据其自行收尾；先置位再排空保证「检查未置位 ⇒ 入队早于排空 ⇒ 必被排空」无竞态。
                drainerStopped.TrySetResult();
                while (ackCommands.TryDequeue(out var pending))
                    pending.Completion?.TrySetResult();
                channel.Writer.TryComplete();
                producerCompleted.TrySetResult();
            }

            // ── 确认命令应用（只在消费线程执行）──
            void DrainAckCommands()
            {
                while (ackCommands.TryDequeue(out var command))
                {
                    try
                    {
                        switch (command.Kind)
                        {
                            case AckKind.Acknowledge:
                            case AckKind.RejectSkip:
                                // S1066：合并嵌套 if，manualStore 在前短路——offsetTracker.Ack
                                // 有副作用（推进前缀状态），自动存储模式绝不能调用它。
                                // 乱序确认安全：只有连续前缀推进时才存储（并发 handler 下
                                // 逐条 StoreOffset 会让崩溃后的重启跳过仍在处理中的消息）。
                                if (manualStore && offsetTracker.Ack(command.Offset) is { } advanced)
                                {
                                    consumer.StoreOffset(advanced);
                                    if (manualCommit)
                                        consumer.Commit([advanced]);
                                }
                                // 有进展即复位退避——失败重试的成长只针对「连续被拒」。
                                consecutiveSeeks = 0;
                                backoffUntilTimestamp = 0;
                                break;

                            case AckKind.Seek:
                                // Seek 回到本条 offset；分区跟踪状态作废（其后的确认都不再有效）。
                                offsetTracker.Reset(command.Offset.TopicPartition);
                                consumer.Seek(command.Offset);
                                consecutiveSeeks = Math.Min(consecutiveSeeks + 1, MaxSeekBackoffShift);
                                // 测试缝取值（SeekBackoffInitialMs/MaxMs）须在 SubscribeAsync 之前设置；
                                // 此处做下限钳制防 0/负值把退避变紧循环。
                                var backoffMs = Math.Min(
                                    Math.Max(SeekBackoffInitialMs, 1) << (consecutiveSeeks - 1),
                                    Math.Max(SeekBackoffMaxMs, 1));
                                backoffUntilTimestamp = Stopwatch.GetTimestamp()
                                    + (backoffMs * Stopwatch.Frequency / 1000);
                                _logger.Warning(
                                    $"Kafka message rejected for redelivery, seeking back: {command.Offset} @ {_consumerConfig.GroupId}, backoff {backoffMs}ms (consecutive {consecutiveSeeks})");
                                break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 命令应用失败（分区被撤销、消费者关停中）——记录后继续，绝不让
                        // 消费线程因一次确认失败而终止。
                        _logger.Warning(
                            $"Kafka ack command failed ({command.Kind} {command.Offset}) @ {_consumerConfig.GroupId}: {ex.Message}");
                    }
                    finally
                    {
                        // 无论成功失败都完成等待方：handler 不应因确认落空而挂死。
                        command.Completion?.TrySetResult();
                    }
                }
            }

            void WaitSeekBackoff()
            {
                while (backoffUntilTimestamp > 0)
                {
                    var remainingMs = (backoffUntilTimestamp - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                    if (remainingMs <= 0)
                    {
                        backoffUntilTimestamp = 0;
                        return;
                    }
                    // 等待句柄可中断：取消时立即退出，不必等退避走完。
                    if (tokenSnapshot.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(remainingMs, 100))))
                        return;
                    if (tokenSnapshot.IsCancellationRequested)
                        return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "pal-kafka-consume"
        };
        consumerThread.Start();

        // 保存 Task 引用，用于等待完成和错误观测
        // S8949：worker 刻意不接收 Task.Run 的取消令牌——取消由循环内的 tokenSnapshot 观察，
        // 调度前取消也必须进入 finally（Close/Dispose consumer），传入令牌会让任务根本不运行。
#pragma warning disable S8949 // 见上：取消在循环内观察，调度前取消仍须执行收尾
        var consumeTask = Task.Run(async () =>
#pragma warning restore S8949
        {
            var inFlight = new List<Task>(maxConcurrent);
            try
            {
                while (!tokenSnapshot.IsCancellationRequested)
                {
                    var result = await channel.Reader.ReadAsync(tokenSnapshot).ConfigureAwait(false);
                    // FIX-001：slots.Release() 已移除——channel 容量本身承载并发限流语义。

                    var handlerTask = HandleMessageAsync(result);
                    if (maxConcurrent == 1)
                    {
                        // 默认形态：逐条 await（与 2026-10-20 版本逐位一致，零额外调度）。
                        await handlerTask.ConfigureAwait(false);
                        continue;
                    }

                    inFlight.Add(handlerTask);
                    if (inFlight.Count >= maxConcurrent)
                    {
                        var completed = await Task.WhenAny(inFlight).ConfigureAwait(false);
                        inFlight.Remove(completed);
                        await completed.ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (tokenSnapshot.IsCancellationRequested)
            {
                // 正常取消
            }
            catch (ChannelClosedException) when (tokenSnapshot.IsCancellationRequested)
            {
                // 生产者先于取消完成通道：正常关停，不记错误
            }
            catch (ChannelClosedException ex)
            {
                _logger.Warning($"Kafka consume channel closed unexpectedly: {topic} @ {_consumerConfig.GroupId}: {ex.Message}");
            }
            // v36 勘正：外层非关停 OCE 兜底——handler 的非关停 OCE 已在 HandleMessageAsync
            // 内 catch 后返回；到达本外层 catch 的是 while 条件求值等循环骨架位置的非关停
            // OCE——此处终止是真实语义（骨架异常无法安全 continue），文案如实描述终止。
            catch (OperationCanceledException ex)
            {
                _logger.Error(ex, $"Kafka consume loop terminated by non-shutdown cancellation at loop skeleton: {topic} @ {_consumerConfig.GroupId}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 兜底：记录未被内层捕获的异常
                _logger.Error(ex, $"Kafka consume loop terminated unexpectedly: {topic} @ {_consumerConfig.GroupId}");
            }
            finally
            {
                // worker 因非取消异常退出时终止生产者；以及等待专用线程退出后再 Close/Dispose，
                // 避免 Consume 与 Close/Dispose 并发打在同一个 consumer 上。
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 订阅已释放其 CTS：取消已生效，无需重复。
                }
                await producerCompleted.Task.ConfigureAwait(false);

                if (inFlight.Count > 0)
                {
                    // 已启动的 handler 必须收尾后再释放 consumer；handler 感知 tokenSnapshot
                    // 取消后退出（不响应取消的 handler 由宿主 ShutdownTimeout 兜底）。
                    await Task.WhenAll(inFlight).ConfigureAwait(false);
                }

                // ITM-217 修复（三十二轮）：Close() 抛非 OCE 异常时不得跳过 Dispose——
                // 原 finally 裸调两行，Close 失败即泄漏 consumer（连接/组状态）。
                try
                {
                    consumer.Close();
                }
                catch (ObjectDisposedException)
                {
                    // v43 P3：登记先行窗口（订阅已 Dispose、迟启动委托此刻才被调度执行）下，
                    // KafkaSubscription.DisposeAsync 的兜底 Dispose 已先行释放 consumer——
                    // Close 打在已释放实例上抛 ODE 属该窗口的预期形态，静默跳过（委托侧
                    // 与兜底 Dispose 幂等，资源回收无缺口），不再记 Warning 噪声误导关停诊断
                }
                catch (Exception closeEx) when (closeEx is not OperationCanceledException)
                {
                    _logger.Warning($"Kafka consumer Close failed during shutdown: {closeEx.Message} @ {_consumerConfig.GroupId}");
                }
                finally
                {
                    // Confluent.Kafka Dispose 幂等——与 KafkaSubscription 兜底 Dispose 双重释放安全
                    consumer.Dispose();
                }
            }

            async Task HandleMessageAsync(ConsumeResult<string, byte[]> result)
            {
                // B3（2026-11-06）：确认器提升到 try 外——失败路径（handler 抛异常/反序列化
                // null/非关停 OCE）此前不结算，而 offset 在消费线程 :532-533 已播种进
                // OffsetTracker，前缀推进永久卡死（分区消费停摆 + 重启全量重放）。
                KafkaMessageAck? ack = null;
                try
                {
                    // ITM-628 修复：分区 EOF 空消息守卫（EnablePartitionEof=true 时
                    // Consume 返回 ConsumeResult.Message == null，仅 IsPartitionEOF 置位）——
                    // 原代码直接解引用 result.Message.Value 抛 NRE，落 catch(Exception)
                    // 记假错误日志。EOF 非消息，跳过继续等待（不进 offset 跟踪、不创建 ack）。
                    if (result.Message is null)
                        return;
                    // 三十八轮 P3 修复：tombstone（null value）消息走专门分支——
                    // 原路径 null 经隐式转换成空 span 触发反序列化异常，日志噪声且语义混淆
                    if (result.Message.Value is null)
                    {
                        _logger.Information($"Tombstone (null value) message on {topic}, discarding");
                        return;
                    }
                    // B3：确认器在反序列化之前构造，确保 null 分支也有 ack 可结算。
                    // 批次 A（2026-11-04）：动作经队列回送消费线程执行（IConsumer 非线程安全，
                    // handler 跑在 worker 线程上）。
                    ack = new KafkaMessageAck(
                        ackCommands,
                        new TopicPartitionOffset(result.TopicPartition, result.Offset),
                        manualStore,
                        drainerStopped.Task,
                        _logger);
                    // 2026-10-19 性能实施轮：走泛型反序列化（编译时 JsonTypeInfo<T>/
                    // MemoryPack 泛型 Formatter），免运行时 Type 查找与值类型装箱。
                    TMessage? message = Serializer.Deserialize<TMessage>(result.Message.Value, descriptor);
                    if (message is not null)
                    {
                        // P2 修复（八轮评审）：消费端还原追踪头——写侧 CreateHeaders 的镜像
                        // C-11（EPSL 地毯审计 2026-10-11）：单趟遍历 Confluent Headers 直产
                        // 已解码字典（原 ToHeaderMap 中间字典 + FromHeaders 二次物化消除）
                        var consumeContext = FromKafkaHeaders(result.Message.Headers, result.Message.Key);
                        // ADR-039 / 审计 A5：消费跳 span（与发布侧 StartBrokerPublish 配对）
                        using var consumeActivity = PalActivitySource.StartBrokerConsume(topic, descriptor.Name);
                        await handler(message, consumeContext, ack, tokenSnapshot).ConfigureAwait(false);
                    }
                    else
                    {
                        // 反序列化返回 null — 消息无法处理，不重试（finally 会补 RejectSkip 解冻水印）
                        // v4 批 D（S3）：反序列化失败按消费失败计
                        PalMetrics.BrokerConsumeFailed.Add(1);
                        _logger.Warning($"Deserializing {typeof(TMessage).Name} returned null, discarding message: {topic}");
                    }
                }
                // v37 P3：关停谓词拆双分支（镜像 RabbitMqBroker v31/v33 关停/非关停双 catch
                // 形态）——关停 OCE（tokenSnapshot 已取消）走 Warning 后返回（外层 while 的
                // 取消条件随即退出），不再把关停误标为 Error；非关停 OCE（应用自身取消）
                // 保留 v36 语义 Error 后继续消费。tokenSnapshot 为启动时快照，
                // IsCancellationRequested 在 Dispose 后读取安全（不抛 ODE）
                catch (OperationCanceledException) when (tokenSnapshot.IsCancellationRequested)
                {
                    _logger.Warning($"Handling {typeof(TMessage).Name} message was canceled during shutdown: {topic}");
                }
                catch (OperationCanceledException ex)
                {
                    // v4 批 D（S3）：非关停 OCE = 应用自身取消 → 按失败计（关停窗口不计）
                    PalMetrics.BrokerConsumeFailed.Add(1);
                    _logger.Error(ex, $"Handler for {typeof(TMessage).Name} message threw a non-shutdown OperationCanceledException, discarding: {topic}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // v4 批 D（S3）：handler 异常 / 反序列化失败（null 分支下方同计）
                    PalMetrics.BrokerConsumeFailed.Add(1);
                    _logger.Error(ex, $"Failed to handle {typeof(TMessage).Name} message: {topic}");
                }
                finally
                {
                    // B3（v2 §2.1）：失败路径统一结算，解冻 OffsetTracker 前缀推进。
                    // 五门控：
                    //  · manualStore —— 自动存储模式不结算（RejectSkip 本就早退，但 TryClaim
                    //    会置 _settled；门控后零状态变化，保持既有 at-most-once 语义）；
                    //  · ack is { IsSettled: false } —— handler 已显式结算（Ack/Reject/Seek）
                    //    时不覆盖其决定；EOF/tombstone 的 ack=null 由模式匹配天然跳过；
                    //  · !DeferredAckRegistry.IsDeferred(ack) —— 批量路径已把结算移交批次泵
                    //    （MessageBatchPump 在入队前同步 MarkDeferred），finally 不抢占：
                    //    抢先 RejectAsync 会占用 KafkaMessageAck.TryClaim，批次侧
                    //    AcknowledgeAsync / RejectAsync(requeue:true) 全被拦截，
                    //    EnableAutoOffsetStore=false 下失败无法 Seek 重投 → 消息丢失。
                    //    v4 批 A 遗漏此判据（RabbitMQ 侧四处已查），本轮补（2026-11-07 审计 P1-A）；
                    //  · !tokenSnapshot.IsCancellationRequested —— 关停窗口不结算：
                    //    F1 勘正（2026-11-08）：补结算不再必然异常逃逸——drainerStopped 置位后
                    //    EnqueueAsync 自行收尾并记 Warning（结算被放弃、offset 未存储）；关停
                    //    仍走 at-least-once 重启重放，结论不变而理由随 F1 修复同步；
                    //  · CancellationToken.None —— finally 结算不受外部取消打断，消费线程
                    //    Consume(100ms) 每轮排空命令，100ms 内落地。
                    // 注：handler 成功但未显式结算时补 RejectSkip 与 Acknowledge 等价——两者在
                    // DrainAckCommands（:577-593）走同一分支（前缀推进 + 复位退避），
                    // 因此不会误伤「成功未结算」路径，反而闭合了成功却漏 ack 的推进缺口。
                    if (manualStore && ack is { IsSettled: false } && !DeferredAckRegistry.IsDeferred(ack) && !tokenSnapshot.IsCancellationRequested)
                        await ack.RejectAsync(requeue: false, CancellationToken.None).ConfigureAwait(false);
                }
            }
        });

        // P3 修复（十七轮）：登记先行的回填——循环启动后注入 Task 引用
        // （Set 前若被 Dispose，DisposeAsync 走 null 窗口路径：cts 已取消，
        // 委托侧 finally 释放 consumer + 兜底 Dispose 幂等，无泄漏）
        subscription.SetConsumeTask(consumeTask);
        return new ValueTask<IAsyncDisposable>(subscription);
    }

    // ─────────────────────────────────────────────────────────────
    // 批次 A（2026-11-04）：消费确认通路
    // ─────────────────────────────────────────────────────────────

    /// <summary>消费线程的 poll 超时（毫秒）——决定确认命令的最坏生效延迟，不决定消息到达延迟。</summary>
    private const int PollIntervalMilliseconds = 100;

    /// <summary>首次 Seek 重投的退避下限（毫秒）——默认值，可被 <see cref="SeekBackoffInitialMs"/> 覆写。</summary>
    private const int InitialSeekBackoffMilliseconds = 200;

    /// <summary>Seek 重投退避上限（毫秒）——有进展即复位，避免毒消息把消费打满；默认值，可被 <see cref="SeekBackoffMaxMs"/> 覆写。</summary>
    private const int MaxSeekBackoffMilliseconds = 5000;

    /// <summary>退避指数的位移上限（200ms&lt;&lt;5 = 6400ms 被上限截到 5000ms）。</summary>
    private const int MaxSeekBackoffShift = 6;

    /// <summary>
    /// Seek 退避初始毫秒（内部测试缝，对齐 internal ctor / <c>ApplyIdempotenceDefault</c> 先例）。
    /// </summary>
    /// <remarks>
    /// 仅 <c>PalDDD.Messaging.Integration.Tests</c>（IVT）可触达；<b>必须在 <c>SubscribeAsync</c> 之前设置</b>
    /// （消费线程在循环内读取，订阅后改写不生效）。存在的唯一理由：方案 A6 的验收口径
    /// 「连续 100 次 Reject 不超 10 秒」在默认退避（200ms 起、5s 上限）下算术不可达
    /// （100 次最小 20 秒）——测试用缩短的退避验证有界性与无紧循环，生产行为不变。
    /// </remarks>
    internal int SeekBackoffInitialMs { get; set; } = InitialSeekBackoffMilliseconds;

    /// <summary>Seek 退避上限毫秒（内部测试缝，语义同 <see cref="SeekBackoffInitialMs"/>）。</summary>
    internal int SeekBackoffMaxMs { get; set; } = MaxSeekBackoffMilliseconds;

    /// <summary>确认命令种类——决定消费线程上执行的 broker 调用。</summary>
    private enum AckKind
    {
        /// <summary>处理成功：条件满足时存储（并可能提交）offset。</summary>
        Acknowledge,

        /// <summary>放弃该条：与 <see cref="Acknowledge"/> 同动作（存储即跳过），仅日志语义不同。</summary>
        RejectSkip,

        /// <summary>请求重投：Seek 回本条 offset，随后指数退避。</summary>
        Seek
    }

    /// <summary>worker → 消费线程的确认命令（一次交付一条）。</summary>
    private readonly record struct AckCommand(
        AckKind Kind,
        TopicPartitionOffset Offset,
        TaskCompletionSource? Completion);

    /// <summary>
    /// 单次交付的消费确认器（批次 A）。动作经 <see cref="AckCommand"/> 回送消费线程执行，
    /// 调用方在命令被应用（或关停时被放弃）后继续。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>自动存储模式下 <see cref="AcknowledgeAsync"/> / <see cref="RejectAsync"/>(false) 为无操作</b>：
    /// <c>EnableAutoOffsetStore</c> 未关闭时 offset 由 librdkafka 自动存储，框架不重复存储，
    /// 保持既有 at-most-once 语义。<see cref="RejectAsync"/>(true) 不受此门控——Seek 重投
    /// 在自动模式同样有意义。
    /// </para>
    /// <para>
    /// <b>重复结算</b>：原子标志拦下第二次调用并留 Warning；异常不外抛（确认器在 handler
    /// 内被调用，抛出会打断业务逻辑，而命令失败已由消费线程记录）。
    /// </para>
    /// </remarks>
    private sealed class KafkaMessageAck(
        ConcurrentQueue<AckCommand> queue,
        TopicPartitionOffset offset,
        bool manualStore,
        Task drainerStopped,
        IPalLogger<KafkaBroker> logger) : IMessageAck
    {
        private int _settled;

        /// <summary>本次交付是否已结算。</summary>
        /// <remarks>
        /// S1144：当前无读取方，但 B3 修复（HandleMessageAsync finally 统一结算）将据此判定
        /// handler 是否已显式结算，避免覆盖其决定。保留属性并定点抑制，v1「删死属性」与
        /// 该修法冲突（方案轮红队第 10 条）。
        /// </remarks>
#pragma warning disable S1144 // 由 HandleMessageAsync finally 判定（B3 批 2b 接线）
        public bool IsSettled => Volatile.Read(ref _settled) != 0;
#pragma warning restore S1144

        /// <inheritdoc />
        public async ValueTask AcknowledgeAsync(CancellationToken ct = default)
        {
            if (!TryClaim(nameof(AcknowledgeAsync)))
                return;
            if (!manualStore)
                return; // 自动存储模式：保持自动提交语义

            await EnqueueAsync(AckKind.Acknowledge, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask RejectAsync(bool requeue, CancellationToken ct = default)
        {
            if (!TryClaim($"RejectAsync(requeue: {requeue})"))
                return;

            if (requeue)
            {
                // 重投不受自动存储模式门控：Seek 与 offset 存储是两条独立通路。
                await EnqueueAsync(AckKind.Seek, ct).ConfigureAwait(false);
                return;
            }

            if (!manualStore)
                return;

            await EnqueueAsync(AckKind.RejectSkip, ct).ConfigureAwait(false);
        }

        private async ValueTask EnqueueAsync(AckKind kind, CancellationToken ct)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(new AckCommand(kind, offset, completion));
            // F1 完善（2026-11-08）：排空者（消费线程）已退出时，本命令无人应用——结算转尽力而为，
            // 自行收尾而非死等，并记 Warning 使「静默成功」可观测。修前此处仅 WaitAsync(ct)，
            // 批量泵关停用 ct=None 补结算且 inner 已先释放（排空者已死）时永久挂起孤儿 TCS，
            // DisposeAsync 死锁。信号先于最终排空置位（见消费线程 finally），检查晚于入队，
            // 故「未置位 ⇒ 入队早于排空 ⇒ 必被排空」无竞态。
            // 未存储的 offset 重启重放，是 at-least-once 的预期结果，不推进水印；调用方通过
            // 本 Warning 得知结算未生效，与消费线程意外终止的 broker 级健康态互补。
            if (drainerStopped.IsCompleted)
            {
                logger.Warning(
                    $"Kafka settlement abandoned (consumer thread stopped, offset not stored): " +
                    $"{kind} @ {offset} — redelivery after restart is expected (at-least-once)");
                completion.TrySetResult();
                return;
            }
            // 等待消费线程应用。消费线程 poll 超时 100ms 内必定回到队列。
            await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        private bool TryClaim(string operation)
        {
            if (Interlocked.Exchange(ref _settled, 1) == 0)
                return true;

            logger.Warning(
                $"Duplicate ack operation ignored (delivery already settled): {operation} @ {offset}");
            return false;
        }
    }

    /// <summary>
    /// offset 水印跟踪（批次 A）——把乱序确认折算成「连续前缀已确认到的位点」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要：<see cref="ConsumerDispatchConcurrency"/> &gt; 1 时 handler 并发完成，
    /// 逐条 <c>StoreOffset(offset+1)</c> 会让偏移量越过仍在处理中的消息——崩溃后重启从
    /// 越界位点继续，那些未完成的消息永久丢失（破坏刚建立的 at-least-once 承诺）。
    /// 只有「从 lastStored 起连续的确认链」才允许推进水印。
    /// </para>
    /// <para>
    /// 只在手动存储模式启用（自动模式下确认动作本身是空操作）。
    /// </para>
    /// <para>
    /// FIX-002（2026-11-08）：<see cref="Reset"/> 由分区生命周期回调
    /// （SetPartitionsAssigned/Revoked/Lost）调用——状态与分区所有权同步，rebalance 后
    /// 新起点 &gt; 旧 Next 时不再卡死。internal（非 private）供集成测试直接锁定 Reset 语义。
    /// </para>
    /// </remarks>
    internal sealed class OffsetTracker
    {
        private sealed class Partition(long next)
        {
            public long Next { get; set; } = next;

            public SortedSet<long> Done { get; } = [];
        }

        private readonly Dictionary<TopicPartition, Partition> _partitions = [];

        /// <summary>登记一条已交付的消息——首次见到的分区以本条 offset 作为水印起点。</summary>
        public void Consumed(TopicPartition topicPartition, long offset)
        {
            if (!_partitions.ContainsKey(topicPartition))
                _partitions[topicPartition] = new Partition(offset);
        }

        /// <summary>
        /// 确认一条消息。返回需要存储的位点（"下一个待消费 offset"）；无需推进时返回
        /// <c>null</c>（乱序到达但前缀未连成、或重复确认）。
        /// </summary>
        public TopicPartitionOffset? Ack(TopicPartitionOffset offset)
        {
            if (!_partitions.TryGetValue(offset.TopicPartition, out var partition))
                return null;
            if (offset.Offset.Value < partition.Next)
                return null; // 已被更早的水印覆盖
            if (!partition.Done.Add(offset.Offset.Value))
                return null; // 重复确认

            var advanced = false;
            while (partition.Done.Remove(partition.Next))
            {
                partition.Next++;
                advanced = true;
            }

            return advanced ? new TopicPartitionOffset(offset.TopicPartition, partition.Next) : null;
        }

        /// <summary>作废该分区的水印——Seek 之后既有的连续前缀对新位置无意义。</summary>
        public void Reset(TopicPartition topicPartition) => _partitions.Remove(topicPartition);
    }

    /// <summary>
    /// Confluent.Kafka Headers 与消息 Key → 消费上下文（C-11 单趟形态）——IHeader 枚举直产
    /// 已解码字典，键名/解码语义与 <see cref="MessageConsumeContext.FromHeaders"/> 一致：空字节
    /// 值解码为 null、重复键后者覆盖、未知键保留在 Headers 视图；无头且无 Key 时返回 null。
    /// 消息 Key 由发布侧写入 messageId，作为 <see cref="MessageConsumeContext.MessageId"/> 供
    /// Inbox 幂等使用。Kafka 无 BasicProperties correlation 兜底（恒 null）。
    /// </summary>
    private static MessageConsumeContext? FromKafkaHeaders(Headers? headers, string? messageId)
    {
        if (headers is null || headers.Count == 0)
        {
            return messageId is null
                ? null
                : new MessageConsumeContext(messageId, null, null, null, null, EmptyHeaders);
        }

        string? correlationId = null;
        string? causationId = null;
        string? traceParent = null;
        string? traceState = null;
        foreach (var header in headers)
        {
            var value = DecodeKafkaHeader(header);
            switch (header.Key)
            {
                case MessageConsumeContext.HeaderNames.CorrelationId:
                    correlationId = value;
                    break;
                case MessageConsumeContext.HeaderNames.CausationId:
                    causationId = value;
                    break;
                case MessageConsumeContext.HeaderNames.TraceParent:
                    traceParent = value;
                    break;
                case MessageConsumeContext.HeaderNames.TraceState:
                    traceState = value;
                    break;
                default:
                    break;
            }
        }

        // Headers 只在消费 handler 真正读取时才物化全量字典；已知追踪字段已提前解码。
        // 注：Kafka 的头是 Confluent 的 Headers（非键值序列），走委托形态，每消息一笔闭包；
        // 通用键值序列路径（RabbitMQ 与 FromHeaders）已在 2026-10-22 性能轮去掉该分配。
        return new MessageConsumeContext(
            messageId, correlationId, causationId, traceParent, traceState,
            () => DecodeKafkaHeaders(headers));
    }

    /// <summary>无头消息（仅带 Key）的消费上下文空头视图——只读包装，免每条消息一个空字典。</summary>
    private static readonly IReadOnlyDictionary<string, string?> EmptyHeaders =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>());

    private static Dictionary<string, string?> DecodeKafkaHeaders(Headers headers)
    {
        var decoded = new Dictionary<string, string?>(headers.Count, StringComparer.Ordinal);
        foreach (var header in headers)
            decoded[header.Key] = DecodeKafkaHeader(header);
        return decoded;
    }

    private static string? DecodeKafkaHeader(IHeader header)
    {
        var bytes = header.GetValueBytes();
        return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>释放 Broker：先释放全部订阅，再 Flush 排空在途消息，最后释放 producer。</summary>
    /// <remarks>
    /// ⚠️ 已知竞态（P3 声明，未加固）：<see cref="PublishAsync"/> 的 <c>_disposed</c> 预检无锁，
    /// 与本方法无互斥——释放窗口内并发到达的 <see cref="PublishAsync"/> 可能通过预检后落在
    /// 正在/已被 <c>_producer.Dispose()</c> 的 producer 上，此时 ProduceAsync 行为未定义
    ///（可能抛 ObjectDisposedException，也可能无确认地丢弃）。<br/>
    /// 契约：调用方应在调用本方法<b>之前</b>停止生产者并等待在途发布完成；不保证释放窗口内
    /// 并发发布的投递结果。并发加固（发布/释放互斥）不在当前实现范围。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // ITM-217 修复（三十二轮）：Broker 级幂等门——对照 KafkaSubscription 的 Interlocked
        // _disposed 门；DI 容器与显式 using 双释放时第二次调用不再重复遍历/释放 producer
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        IAsyncDisposable[] snapshot;
        lock (_consumersLock)
        {
            snapshot = [.. _consumers];
            _consumers.Clear();
        }
        foreach (var c in snapshot) await c.DisposeAsync().ConfigureAwait(false);
        // 三十八轮 P2 修复：Dispose 前 Flush 排空 in-flight 消息——关停瞬间仍在飞行中的
        // produce（ct 取消边界/并发发布中）没有排空机会即被 librdkafka destroy 丢弃。
        // 有限超时 5 秒：剩余未确认消息记 Warning（调用方可据此判断是否需要重发）。
        // v38 P3：Flush 异常不阻断 Dispose——Flush 抛非 OCE 异常（handle 失效等）时裸调
        // 会让 _producer.Dispose 被跳过，native handle 只能靠 finalizer 兜底（对齐
        // KafkaSubscription 委托的 try-finally 嵌套防护）
        try
        {
            var remaining = _producer.Flush(TimeSpan.FromSeconds(5));
            if (remaining > 0)
                _logger.Warning($"KafkaBroker disposed with {remaining} unconfirmed message(s) still in-flight (flush timeout 5s)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"KafkaBroker flush during dispose failed, proceeding to dispose producer: {ex.Message}");
        }
        _producer.Dispose();
    }

    /// <summary>
    /// v30 P3：从 _consumers 移除指定订阅的登记条目（订阅句柄正常释放时经 removeAction 触发）。
    /// 镜像 v29 Rabbit 侧 _subscriptions.TryRemove 形态——防长驻 Broker 反复订阅/退订下登记表
    /// 无界增长。与 DisposeAsync 的 snapshot+Clear 在 _consumersLock 临界区互斥：句柄先自行
    /// 移除则 snapshot 不含它（免重复 Dispose）；Broker 先 snapshot 则句柄后续的 removeAction
    /// 对已 Clear 的列表 Remove 为 no-op（幂等安全）。List.Remove 为 O(n)——订阅数与拓扑
    /// 同阶（每消息类型一订阅），非热路径。
    /// </summary>
    private void RemoveConsumerRegistration(KafkaSubscription subscription)
    {
        lock (_consumersLock)
        {
            _consumers.Remove(subscription);
        }
    }

    /// <summary>Kafka 订阅句柄 — 持有后台 Task 引用，支持等待完成和状态观测</summary>
    private sealed class KafkaSubscription : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly IConsumer<string, byte[]> _consumer;
        private readonly Action<KafkaSubscription>? _removeAction;
        // P3 修复（十七轮）：登记先行模式——构造时不持有 Task（循环尚未启动），
        // 由 SetConsumeTask 后置注入；Volatile 读写保证跨线程可见性（引用写原子）
        private Task? _consumeTask;
        private int _disposed;

        public KafkaSubscription(
            CancellationTokenSource cts,
            IConsumer<string, byte[]> consumer,
            Action<KafkaSubscription>? removeAction = null)
        {
            _cts = cts;
            _consumer = consumer;
            _removeAction = removeAction;
        }

        /// <summary>后台消费 Task — 仅供本类 Dispose 路径观察异常与完成。
        /// 类型私有，外部健康检查无法取得（2026-10-21 勘正：原注释承诺的健康检查不可达）。
        /// 登记与循环启动之间存在极小窗口，期间为 null（登记先行的顺序权衡）。</summary>
#pragma warning disable S1144 // 健康检查/异常观测的公开诊断属性（文档承诺）
        public Task? ConsumeTask => Volatile.Read(ref _consumeTask);
#pragma warning restore S1144

        /// <summary>后置注入消费循环 Task（登记先行模式）——登记完成后由 SubscribeAsync 调用一次。</summary>
        public void SetConsumeTask(Task consumeTask)
        {
            ArgumentNullException.ThrowIfNull(consumeTask);
            Volatile.Write(ref _consumeTask, consumeTask);
        }

        // v8 声明：consumeTask 等待无独立超时——handler 不响应取消时靠宿主 HostOptions
        // ShutdownTimeout（默认 30s）兜底强杀
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return; // 幂等

            // v30 P3（镜像 v29 Rabbit 侧 TryRemove 前置形态）：释放流程启动即从 Broker
            // 登记表移除本条目——长驻 Broker 反复订阅/退订下 _consumers 不再无界增长；
            // Broker.DisposeAsync 兜底遍历与此处移除经 _consumersLock 互斥（snapshot
            // 已含本句柄时其 DisposeAsync 调用被幂等门拦截，双路径安全）
            _removeAction?.Invoke(this);

            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                // 等待后台消费循环真正退出（而非盲猜延时）。
                // P3 修复（十七轮）· 登记先行窗口：登记与 SetConsumeTask 之间被 Dispose
                // 时为 null——此时循环未启动或刚启动，cts 已请求取消，委托侧 finally
                // 会释放 consumer，下方 finally 兜底 Dispose 幂等，无泄漏。
                var consumeTask = Volatile.Read(ref _consumeTask);
                if (consumeTask is not null)
                    await consumeTask.ConfigureAwait(false);
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
                // P2 修复 + v43 P3 勘正：本兜底释放 consumer（Confluent.Kafka Dispose 幂等）。
                // 原注释声称"若取消发生在 Task.Run 委托开始执行前，委托内 finally 不会执行"
                // ——仅在 Dispose 后委托从未被调度的窄窗口成立（此时本兜底是唯一释放点）；
                // 委托已被调度时 await consumeTask 保证委托内 finally（Close+Dispose）先行
                // 执行完毕，本行为幂等二次释放（Close 打已释放实例的 ODE 噪声已由委托侧
                // v43 降噪）。
                _consumer.Dispose();
                // ITM-167 声明（登记窗口 cts 释放噪声）：DisposeAsync 可能落在
                // "已登记未 SetConsumeTask" 窗口，此刻释放 _cts 是安全的——
                // v33 P3 勘正：原声明"Token 在源 Dispose 后仍可读取"失实——Dispose 后
                // 访问 cts.Token 属性抛 ObjectDisposedException（仅 IsCancellationRequested
                // 安全）。实际安全机制：① 循环判取消用 tokenSnapshot（Task.Run 启动前预捕获
                // 的 CancellationToken 结构快照），Task.Run 第二实参同用快照（v22 E-1：
                // 规避启动前求值 Token 的 ODE 窗口）——IsCancellationRequested 只读快照
                // 状态，不触碰已 Dispose 源；② CancelAsync 先行请求取消，循环随即退出；
                // ③ await consumeTask 之后才进 finally Dispose——时序保证 Dispose 时循环
                // 已终止，不再读任何 cts 状态。SetConsumeTask 的后置注入仅写 Task 引用，
                // 不触碰 _cts。无资源泄漏，仅存在一个可安全忽略的已取消任务。
                _cts.Dispose();
            }
        }
    }
}
