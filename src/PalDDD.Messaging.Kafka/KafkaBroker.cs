using Confluent.Kafka;
using Microsoft.Extensions.Logging;
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
    // 优化（二十四轮 OP-2）：object → System.Threading.Lock——全仓最后一个 object 锁，
    // Lock 有 JIT 专用锁内联优化（与其余 8 处模式统一）
    private readonly Lock _consumersLock = new();
    // ITM-217 修复（三十二轮）：DisposeAsync 幂等门（对照 KafkaSubscription._disposed）
    private int _disposed;

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
        // v25 P3 守卫族：发布侧 _disposed 守卫（镜像订阅侧 P3-SRC-402 守卫——订阅侧在
        // _consumersLock 临界区 ThrowIf；发布侧不在锁上下文，用 _disposed != 0 直接判定）——
        // Broker 释放后 ProduceAsync 落在已 Dispose 的 producer 上行为未定义，fail-fast。
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentOutOfRangeException.ThrowIfEqual(messageId, default);

        var key = messageId.ToString();
        var value = Serializer.Serialize(message, descriptor);

        await _producer.ProduceAsync(descriptor.Name, new Message<string, byte[]>
        {
            Key = key,
            // Serialize 契约返回 ReadOnlyMemory<byte>，Message.Value 需 byte[]——此处 ToArray
            // 是必要转换非冗余拷贝（ReadOnlyMemory 底层即单次 ToArray 产物，无双重拷贝）
            Value = value.ToArray(),
            Headers = CreateHeaders(context)
        }, ct).ConfigureAwait(false);

        // 优化（二十五轮 Z-1）：Debug 级门控——AddPalLogging 最低 Information，无门控时
        // 每消息的字符串插值（类型名+topic+key 的 Ulid.ToString）全部白做
        if (_logger.IsEnabled(LogLevel.Debug))
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
            consumer = new ConsumerBuilder<string, byte[]>(_consumerConfig).Build();
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

        // 保存 Task 引用，用于等待完成和错误观测
        var consumeTask = Task.Run(async () =>
        {
            try
            {
                while (!tokenSnapshot.IsCancellationRequested)
                {
                    ConsumeResult<string, byte[]> result;
                    try
                    {
                        result = consumer.Consume(tokenSnapshot);
                    }
                    catch (OperationCanceledException) when (tokenSnapshot.IsCancellationRequested)
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
                        await Task.Delay(TimeSpan.FromSeconds(1), tokenSnapshot).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        // 三十八轮 P3 修复：tombstone（null value）消息走专门分支——
                        // 原路径 null 经隐式转换成空 span 触发反序列化异常，日志噪声且语义混淆
                        if (result.Message.Value is null)
                        {
                            _logger.Information($"Tombstone (null value) message on {topic}, discarding");
                            continue;
                        }
                        var message = Serializer.Deserialize(result.Message.Value, descriptor);
                        if (message is not null)
                        {
                            // P2 修复（八轮评审）：消费端还原追踪头——写侧 CreateHeaders 的镜像
                            var consumeContext = MessageConsumeContext.FromHeaders(ToHeaderMap(result.Message.Headers));
                            await handler((TMessage)message, consumeContext, tokenSnapshot).ConfigureAwait(false);
                        }
                        else
                        {
                            // 反序列化返回 null — 消息无法处理，不重试
                            _logger.Warning($"Deserializing {typeof(TMessage).Name} returned null, discarding message: {topic}");
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        // v36 P1 真修（v34 假修勘正）：非关停 OCE = 应用自身取消 = 单消息事件
                        //（对齐 RabbitBroker v33 判定）——本 catch 必须位于 while 体内：v34 的
                        // 对齐只改了外层 catch 的日志文本，catch 锚定在 while 之外物理上不可
                        // 能继续循环（捕获后直落 finally Close+Dispose → 订阅静默死亡），
                        // "continuing consumption" 文案与实际终止行为相反。移入后消费继续
                        _logger.Error(ex, $"Handler for {typeof(TMessage).Name} threw a non-shutdown OperationCanceledException, discarding: {topic}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Error(ex, $"Failed to handle {typeof(TMessage).Name} message: {topic}");
                    }
                }
            }
            catch (OperationCanceledException) when (tokenSnapshot.IsCancellationRequested)
            {
                // 正常取消
            }
            // v36 勘正：外层非关停 OCE 兜底——v34 假修勘正后，handler 的非关停 OCE 已在
            // while 体内 catch+继续（真 continue）；到达本外层 catch 的是 while 条件求值等
            // 循环骨架位置的非关停 OCE——此处终止是真实语义（骨架异常无法安全 continue），
            // 文案如实描述终止
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
                // ITM-217 修复（三十二轮）：Close() 抛非 OCE 异常时不得跳过 Dispose——
                // 原 finally 裸调两行，Close 失败即泄漏 consumer（连接/组状态）。
                try
                {
                    consumer.Close();
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
        }, tokenSnapshot); // v22 E-1：Task.Run 第二实参在启动前求值=ODE 窗口，用快照

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
        var remaining = _producer.Flush(TimeSpan.FromSeconds(5));
        if (remaining > 0)
            _logger.Warning($"KafkaBroker disposed with {remaining} unconfirmed message(s) still in-flight (flush timeout 5s)");
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
        public Task? ConsumeTask => Volatile.Read(ref _consumeTask);

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
                // P2 修复：若取消发生在 Task.Run 委托开始执行前，委托内 finally
                // （consumer.Close + Dispose）不会执行——此处兜底释放 consumer
                // （Confluent.Kafka Dispose 幂等，与委托内 finally 双重释放安全）。
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
