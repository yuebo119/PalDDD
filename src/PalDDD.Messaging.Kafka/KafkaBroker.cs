using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.Serialization;
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
/// 消费循环在后台线程运行（Confluent.Kafka 的 Consume 为同步阻塞 API，必须用 Task.Run）。<br/>
/// 后台事件可观测（2026-10-08 审计对齐）：producer/consumer 均接线 ErrorHandler/StatisticsHandler/
/// LogHandler——librdkafka 后台线程的错误/日志/统计此前结构性不可见；致命错误走 Error（携带
/// KafkaException），自愈类错误走 Warning，统计 JSON 需 config 显式设 StatisticsIntervalMs 才回调（Debug 门控）。
/// </remarks>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "Broker 消费循环需记录毒消息失败并继续或优雅关停，需捕获 Exception 基类。")]
public sealed class KafkaBroker : MessageBrokerBase, IAsyncDisposable
{
    private readonly IProducer<string, byte[]> _producer;
    private readonly ConsumerConfig _consumerConfig;
    private readonly IPalLogger<KafkaBroker> _logger;
    private readonly bool _concurrentPublishSafe;
    private readonly List<IAsyncDisposable> _consumers = [];
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
        _logger = logger;
        // Outbox 自动升窗前提：幂等 producer 才保证窗口内重试不乱序。
        // 显式关闭或未设置时保守返回 false，处理器回落逐条确认。
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
    private async ValueTask PublishSerializedAsync(
        byte[] value,
        string key,
        MessageDescriptor descriptor,
        MessagePublishContext context,
        CancellationToken ct)
    {
        await _producer.ProduceAsync(descriptor.Name, new Message<string, byte[]>
        {
            Key = key,
            Value = value,
            Headers = CreateHeaders(context)
        }, ct).ConfigureAwait(false);

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
        return headers;
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
        // 池线程。改为「专用线程只 Consume + 有界 Channel + 异步 worker 处理」：
        // 线程不进入 ThreadPool，slots 提供背压，handler 仍在异步上下文执行。
        // 2026-10-21：worker 支持 maxConcurrent 个在途 handler（默认 1 = 原逐条保序形态）。
        var maxConcurrent = _consumerDispatchConcurrency;
        var channel = Channel.CreateUnbounded<ConsumeResult<string, byte[]>>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
        // 专用线程上的同步背压：Wait(token) 阻塞本线程而不占用线程池；worker 读到消息即释放
        // 一个槽位。槽位数 = 并发 handler 上限（默认 1 = 最多 1 处理中 + 1 排队）。
        var slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        var producerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerThread = new Thread(() =>
        {
            try
            {
                while (!tokenSnapshot.IsCancellationRequested)
                {
                    ConsumeResult<string, byte[]> consumed;
                    try
                    {
                        consumed = consumer.Consume(tokenSnapshot);
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

                    try
                    {
                        slots.Wait(tokenSnapshot);
                    }
                    catch (OperationCanceledException) when (tokenSnapshot.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!channel.Writer.TryWrite(consumed))
                    {
                        slots.Release();
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, $"Kafka consume thread terminated unexpectedly: {topic} @ {_consumerConfig.GroupId}");
            }
            finally
            {
                channel.Writer.TryComplete();
                producerCompleted.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "pal-kafka-consume"
        };
        consumerThread.Start();

        // 保存 Task 引用，用于等待完成和错误观测
        var consumeTask = Task.Run(async () =>
        {
            var inFlight = new List<Task>(maxConcurrent);
            try
            {
                while (!tokenSnapshot.IsCancellationRequested)
                {
                    var result = await channel.Reader.ReadAsync(tokenSnapshot).ConfigureAwait(false);
                    slots.Release();

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
                try
                {
                    // ITM-628 修复：分区 EOF 空消息守卫（EnablePartitionEof=true 时
                    // Consume 返回 ConsumeResult.Message == null，仅 IsPartitionEOF 置位）——
                    // 原代码直接解引用 result.Message.Value 抛 NRE，落 catch(Exception)
                    // 记假错误日志。EOF 非消息，跳过继续等待。
                    if (result.Message is null)
                        return;
                    // 三十八轮 P3 修复：tombstone（null value）消息走专门分支——
                    // 原路径 null 经隐式转换成空 span 触发反序列化异常，日志噪声且语义混淆
                    if (result.Message.Value is null)
                    {
                        _logger.Information($"Tombstone (null value) message on {topic}, discarding");
                        return;
                    }
                    // 2026-10-19 性能实施轮：走泛型反序列化（编译时 JsonTypeInfo<T>/
                    // MemoryPack 泛型 Formatter），免运行时 Type 查找与值类型装箱。
                    TMessage? message = Serializer.Deserialize<TMessage>(result.Message.Value, descriptor);
                    if (message is not null)
                    {
                        // P2 修复（八轮评审）：消费端还原追踪头——写侧 CreateHeaders 的镜像
                        // C-11（EPSL 地毯审计 2026-10-11）：单趟遍历 Confluent Headers 直产
                        // 已解码字典（原 ToHeaderMap 中间字典 + FromHeaders 二次物化消除）
                        var consumeContext = FromKafkaHeaders(result.Message.Headers);
                        await handler(message, consumeContext, tokenSnapshot).ConfigureAwait(false);
                    }
                    else
                    {
                        // 反序列化返回 null — 消息无法处理，不重试
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
                    _logger.Error(ex, $"Handler for {typeof(TMessage).Name} threw a non-shutdown OperationCanceledException, discarding: {topic}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error(ex, $"Failed to handle {typeof(TMessage).Name} message: {topic}");
                }
            }
        });

        // P3 修复（十七轮）：登记先行的回填——循环启动后注入 Task 引用
        // （Set 前若被 Dispose，DisposeAsync 走 null 窗口路径：cts 已取消，
        // 委托侧 finally 释放 consumer + 兜底 Dispose 幂等，无泄漏）
        subscription.SetConsumeTask(consumeTask);
        return new ValueTask<IAsyncDisposable>(subscription);
    }

    /// <summary>
    /// Confluent.Kafka Headers → 消费上下文（C-11 单趟形态）——IHeader 枚举直产已解码字典，
    /// 键名/解码语义与 <see cref="MessageConsumeContext.FromHeaders"/> 一致：空字节值解码为
    /// null、重复键后者覆盖、未知键保留在 Headers 视图；无头返回 null（消费回调收到 null
    /// context 表示消息未携带追踪元数据）。Kafka 无 BasicProperties correlation 兜底（恒 null）。
    /// </summary>
    private static MessageConsumeContext? FromKafkaHeaders(Headers? headers)
    {
        if (headers is null || headers.Count == 0)
            return null;

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
        return new MessageConsumeContext(
            correlationId, causationId, traceParent, traceState,
            () => DecodeKafkaHeaders(headers));
    }

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

        /// <summary>后台消费 Task — 可用于健康检查和异常观测。
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
