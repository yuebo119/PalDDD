// ─────────────────────────────────────────────────────────────
// 📤 OutboxBatchProcessor — Scoped 批次发布器
//    由 OutboxProcessor 每 tick 创建一个 scope 实例，租约获取一批待发消息并逐条发布。
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>Scoped 批次发布器 — 由 OutboxProcessor 每 tick 实例化，发布一个租约批次。</summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "Outbox 需在继续处理批次前标记任意 broker 或序列化器失败，需捕获 Exception 基类。")]
public sealed class OutboxBatchProcessor
{
    // P1 修复（二十一轮）：失败原因入库截断上限——Error 列上限 2048（OutboxDbContext），超长
    // ex.Message 让 MarkDead/ReleaseForRetry 本身失败：ReleaseForRetry 路径 ExecuteUpdate 抛
    // 截断异常中止整批（后续消息饿死）；MarkDead 路径毒实体滞留 ChangeTracker 使同批 MarkProcessed
    // 全回滚（无限重发永不死信）。对齐 InboxProcessor 的 FailureReason.Normalize(ex.Message) 收口（十七轮姊妹修复）。

    private readonly IPalOutboxStore _store;
    private readonly Messaging.IMessageBroker _broker;
    private readonly Serialization.IMessageSerializer _serializer;
    private readonly Serialization.IMessageCatalog _messageCatalog;
    private readonly IPalLogger<OutboxBatchProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<OutboxOptions> _options;
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? _scopeFactory;
    // 租约持有者标识 — 从 OutboxOptions.LeaseOwner 读取，支持多实例部署时自定义

    public OutboxBatchProcessor(
        IPalOutboxStore store,
        Messaging.IMessageBroker broker,
        Serialization.IMessageSerializer serializer,
        Serialization.IMessageCatalog messageCatalog,
        IOptionsMonitor<OutboxOptions> options,
        IPalLogger<OutboxBatchProcessor> logger,
        TimeProvider? timeProvider = null,
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? scopeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(messageCatalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _broker = broker;
        _serializer = serializer;
        _messageCatalog = messageCatalog;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        // C1（perf-opt-sweep）：并行发布需要 per-worker scope（EF DbContext 非线程安全
        // ——各 worker 须独立 store 实例）。默认 DI 注册（AddPalOutbox TryAddScoped）在
        // 容器内激活时自动传入；直构造调用方（测试）传 null → 并行度 >1 时 fail-fast。
        _scopeFactory = scopeFactory;
    }

    /// <summary>处理一个已租约的批次。</summary>
    public async ValueTask ProcessBatchAsync(CancellationToken ct)
        => await ProcessBatchCoreAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// 处理一个已租约的批次并返回积压信号（T1.2，2026-10-11 优化实施轮）：返回 true =
    /// 本轮租到了满批（<c>messages.Count ≥ BatchSize</c>，表内可能仍有积压），调用方
    ///（OutboxProcessor）据此进入自适应排空——立即续批不等轮询间隔；false = 未满批，
    /// 正常按 PollInterval 轮询。与 <see cref="ProcessBatchAsync"/> 共用同一核心，仅多回传信号。
    /// </summary>
    public async ValueTask<bool> ProcessBatchWithBacklogAsync(CancellationToken ct)
        => await ProcessBatchCoreAsync(ct).ConfigureAwait(false);

    private async ValueTask<bool> ProcessBatchCoreAsync(CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var policy = ResolveDeliveryPolicy(options);
        // now 是批次原子时间戳——本批次内所有 MarkProcessed/MarkDead/ReleaseForRetry 共用同一时刻，
        // 保证批次内时间一致性（而非每条消息各取一次 GetUtcNow，避免批次耗时导致的处理时间漂移）。
        var now = _timeProvider.GetUtcNow();

        // v29 P3（观测域补全）：Lease 调用原在观测域外——DB 故障时 LeasePendingMessagesAsync
        // 抛异常直接上抛，零指标零 activity（下方 finally 的 OutboxFailed 不可达），长故障期
        // 监控面板显示"零失败"假象。选最小形态（拆两级）：Lease 外层 catch 计失败指标 +1 后
        // 重抛（OCE 关停信号不计失败，对齐下方 catch 过滤与 tick 循环取消语义）；不建
        // activity——StartOutboxProcess 需要 batchSize 上下文，Lease 失败时无批次可归属，
        // tick 级失败由 OutboxProcessor.OnTickFailed 记日志兜底
        IReadOnlyList<OutboxMessage> messages;
        try
        {
            messages = await _store.LeasePendingMessagesAsync(
                options.BatchSize,
                options.LeaseOwner,
                options.LeaseDuration,
                options.MaxRetryCount,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PalMetrics.OutboxFailed.Add(1);
            throw;
        }

        using var activity = PalActivitySource.StartOutboxProcess(messages.Count);

        // C1（perf-opt-sweep）：并行度 >1 时按分区交由 per-worker scope 并行处理（每
        // worker 独立 store/DbContext 实例——Mark* 走各自 ExecuteUpdate，fencing 互斥
        // 不受影响）；默认 1 = 原串行路径。两种路径共用 ProcessPartitionAsync 的逐条
        // 循环体与指标聚合。
        var dop = options.MaxDegreeOfParallelism;
        PartitionCounters totals;
        if (dop > 1)
        {
            if (_scopeFactory is null)
                throw new InvalidOperationException(
                    "Outbox MaxDegreeOfParallelism > 1 requires IServiceScopeFactory — " +
                    "register OutboxBatchProcessor via DI (AddPalOutbox) or pass scopeFactory to the constructor.");
            // A1（2026-10-15 性能实施轮）：chunkSize 下限 1——空租约（队列排空后每轮常态）
            // 下 Math.Ceiling(0/dop)=0 使 Chunk 首行抛 ArgumentOutOfRangeException，被
            // PeriodicBackgroundProcessor 兜底 catch 吞掉只记 OnTickFailed，排空后持续
            // 噪音且每轮失败信号污染。空批经 Chunk 产生零分区，ForEachAsync 空转。
            var chunkSize = Math.Max(1, (int)Math.Ceiling(messages.Count / (double)dop));
            var partitions = System.Linq.Enumerable.Chunk(messages, chunkSize);
            var aggregate = new PartitionCounters(0, 0, 0, 0);
            await Parallel.ForEachAsync(
                partitions,
                new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                async (partition, token) =>
                {
                    using var childScope = _scopeFactory.CreateScope();
                    var worker = childScope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
                    var c = await worker.ProcessPartitionAsync(partition, options, policy, now, token).ConfigureAwait(false);
                    lock (aggregate) { aggregate.Add(c); }
                }).ConfigureAwait(false);
            totals = aggregate;
        }
        else
        {
            totals = await ProcessPartitionAsync(messages, options, policy, now, ct).ConfigureAwait(false);
        }

        // ITM-097：指标段——单条 OCE 中止整批时计数仍被记录（finally 语义保留在
        // ProcessPartitionAsync 的计数返回中）
        // v28 P3 观测局限声明：IPalOutboxStore.Mark* 三栈均为 void 返回——计数含租约
        // 竞态下标记未落库的条目（persist_failed 独立可见，R2）
        activity?.SetTag("pal.outbox.processed", totals.Processed);
        activity?.SetTag("pal.outbox.dead", totals.Dead);
        activity?.SetTag("pal.outbox.retried", totals.Retried);
        activity?.SetTag("pal.outbox.persist_failed", totals.PersistFailed);
        PalMetrics.OutboxProcessed.Add(totals.Processed);
        PalMetrics.OutboxFailed.Add(totals.Dead + totals.Retried);
        return messages.Count >= options.BatchSize;
    }

    /// <summary>分区内逐条处理（C1 抽取：原串行循环体原样迁移，计数经结构体返回）。</summary>
    private async ValueTask<PartitionCounters> ProcessPartitionAsync(
        IEnumerable<OutboxMessage> messages, OutboxOptions options, DeliveryPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        // T3.1（2026-10-11 优化实施轮）：窗口 >1 时分区内改走 FIFO 流水线形态（发布确认
        // 不再逐条串行相乘）；默认 1 走下方原串行体
        if (policy.PublishWindow > 1)
            return await ProcessPartitionPipelinedAsync(messages, options, policy, now, ct).ConfigureAwait(false);

        var processed = 0;
        var dead = 0;
        var retried = 0;
        var persistFailed = 0;

        // R4/P0-2（ADR-027）：终态标记批量化；2026-10-19 性能实施轮增加时间上界——
        // 满阈值或距上次冲刷满 BatchMarkFlushInterval 即冲刷，重复窗口不再由整批时长界定。
        var batchMark = policy.BatchMarkThreshold > 1 && _store is IBatchMarkOutboxStore batchStore ? batchStore : null;
        List<OutboxMessage>? markPending = batchMark is null ? null : new List<OutboxMessage>(Math.Min(policy.BatchMarkThreshold, 64));
        var lastFlushAt = _timeProvider.GetTimestamp();

        async ValueTask FlushMarksAsync(bool force, CancellationToken flushCt)
        {
            if (markPending is not { Count: > 0 })
                return;
            if (!force
                && markPending.Count < policy.BatchMarkThreshold
                && _timeProvider.GetElapsedTime(lastFlushAt) < policy.BatchMarkFlushInterval)
                return;

            var flushed = markPending.Count;
            if (await TryFlushBatchMarkAsync(batchMark!, markPending, now, options, flushCt).ConfigureAwait(false))
                checked { processed += flushed; }
            else
                checked { retried += flushed; }
            lastFlushAt = _timeProvider.GetTimestamp();
        }

        try
        {
            foreach (var msg in messages)
            {
                // 时间界冲刷：慢 broker 下单条发布可能超过一个冲刷周期，进入下一条前
                // 先把已完成的标记落库，避免未标记窗口被整批时长放大。
                await FlushMarksAsync(force: false, ct).ConfigureAwait(false);
                try
                {
                    // ITM-217 修复：按存储的 SchemaVersion 解析——Find(name) 返回最新版本，
                    // 旧版 payload 被新版 descriptor 解释会产生错误字段/反序列化失败。
                    var descriptor = _messageCatalog.Find(msg.Type, msg.SchemaVersion);
                    if (descriptor is null)
                    {
                        await _store.MarkDeadAsync(msg, Core.FailureReason.Normalize($"Type '{msg.Type}' not registered in MessageCatalog"), now, ct).ConfigureAwait(false);
                        checked { dead++; }
                        PalMetrics.OutboxDead.Add(1); // R1：死信独立计数（不再混入 failed）
                        if (!await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false))
                            checked { persistFailed++; } // R2：未落库，dead 计数含漂移——独立可见
                        continue;
                    }

                    // 2026-10-19 性能实施轮：broker 支持 raw 且内容类型一致时直接发持久化字节，
                    // 跳过“反序列化成对象 + broker 再序列化”两次全载荷工作；否则走原对象路径。
                    var raw = CanPublishRaw(msg, descriptor);
                    object? @event = null;
                    if (!raw)
                    {
                        @event = _serializer.Deserialize(msg.Payload, descriptor);
                        if (@event is null)
                        {
                            await _store.MarkDeadAsync(msg, Core.FailureReason.Normalize("Deserialization returned null"), now, ct).ConfigureAwait(false);
                            checked { dead++; }
                            PalMetrics.OutboxDead.Add(1);
                            if (!await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false))
                                checked { persistFailed++; }
                            continue;
                        }
                    }

                    var publishContext = new Messaging.MessagePublishContext(
                        msg.CorrelationId,
                        msg.CausationId,
                        msg.TraceParent,
                        msg.TraceState);
                    if (raw)
                        await _broker.PublishRawAsync(msg.Payload, descriptor, msg.Id, publishContext, ct).ConfigureAwait(false);
                    else
                        await _broker.PublishAsync(@event!, descriptor, msg.Id, publishContext, ct).ConfigureAwait(false);
                    if (batchMark is null)
                    {
                        await _store.MarkProcessedAsync(msg, now, ct).ConfigureAwait(false);
                        checked { processed++; }
                        // R2：持久化失败时从 processed 退回并计入 persistFailed——指标与 DB 真相一致
                        if (!await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false))
                        {
                            checked { processed--; persistFailed++; }
                            PalMetrics.OutboxPersistFailed.Add(1);
                        }
                    }
                    else
                    {
                        // R4/P0-2：成功路径累积批量标记（标记时点延迟到满阈值/时间界/批末——
                        // at-least-once 契约内的重复窗口由 BatchMarkFlushInterval 界定）
                        markPending!.Add(msg);
                        await FlushMarksAsync(
                            force: markPending.Count >= policy.BatchMarkThreshold, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // v35 P3（EA2）：进入失败路径即捕获 RetryCount 快照——下方 ReleaseForRetry
                    // 在 InMemory 栈就地递增 msg.RetryCount（EF 栈则不动内存对象），末尾日志若
                    // 再读 msg.RetryCount + 1 会跨栈不一致（InMemory 偏大 1）。快照在所有
                    // Mark*/Release 之前取值，日志与 delay 计算（本快照 +1）口径统一
                    var retryAtFailure = msg.RetryCount;
                    // RetryCount 由 Store.ReleaseForRetry 在内部递增并与状态一同持久化，
                    // 确保计数与状态原子一致（P0 修复：消除增量-持久化窗口）。
                    // 退避延迟由 IRetryBackoffPolicy 计算（默认指数 2^n，上限 64s，可选抖动）。
                    // v29 P3：ComputeDelay 抛异常会跳过 MarkDead/ReleaseForRetry（消息状态滞留
                    // 租约直到过期，重试链断）——包 try-catch 降级默认延迟 1s + Warning，保证
                    // 标记路径始终执行（对齐 v9 观察者隔离修复的运行期半面：策略故障不阻断主流程）
                    TimeSpan delay;
                    try
                    {
                        delay = options.RetryBackoffPolicy.ComputeDelay(msg.RetryCount + 1);
                    }
                    catch (Exception delayEx)
                    {
                        delay = TimeSpan.FromSeconds(1);
                        _logger.Warning($"Outbox: retry backoff computation failed for message {msg.Id}, falling back to 1s delay: {delayEx.Message}");
                    }
                    var nextAttemptAt = now + delay;
                    // P1 修复（二十一轮）：入库前截断（日志行保留完整消息）——机理见类头常量注释
                    var failureReason = PalDDD.Core.FailureReason.Normalize(ex.Message);
                    if (msg.RetryCount + 1 >= options.MaxRetryCount)
                    {
                        // v43 P3（ITM-092 管线孪生，镜像 InboxProcessor 同型修复）：标记自身
                        // 失败（DB 栈标记路径故障）不得替换 broker 失败根因、不得中止整批——
                        // 原裸调抛出会使上游把标记错误当业务失败处理且同批后续消息饿死。
                        // 标记错误挂主异常 Data（InboxProcessor 同键形态）+ Warning 留痕
                        //（此处主异常不向上传播，Data 信息需日志兜底可见），主异常优先。
                        try
                        {
                            await _store.MarkDeadAsync(msg, failureReason, now, ct).ConfigureAwait(false);
                        }
                        catch (Exception markEx)
                        {
                            ex.Data["MarkError"] = markEx.Message;
                            _logger.Warning($"Outbox: MarkDead for {msg.Id} failed: {markEx.Message}");
                        }
                        checked { dead++; }
                        PalMetrics.OutboxDead.Add(1); // R1：死信独立计数
                    }
                    else
                    {
                        try
                        {
                            await _store.ReleaseForRetryAsync(msg, failureReason, nextAttemptAt, ct).ConfigureAwait(false);
                        }
                        catch (Exception markEx)
                        {
                            ex.Data["MarkError"] = markEx.Message;
                            _logger.Warning($"Outbox: ReleaseForRetry for {msg.Id} failed: {markEx.Message}");
                        }
                        checked { retried++; }
                    }
                    // v35 P3（EA2）：日志用进入失败路径时的快照 +1（本次为第 N 次失败）——
                    // 原读 msg.RetryCount + 1 在 InMemory 栈 ReleaseForRetry 就地递增后偏大 1
                    _logger.Warning($"Outbox: message {msg.Id} processing failed at retry {retryAtFailure + 1}: {ex.Message}");
                    if (!await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false))
                        checked { persistFailed++; } // R2：Mark 已尝试未落库，独立可见
                }
            }
        }
        finally
        {
            // R4/P0-2：批末冲刷残留累积（含 OCE 关停路径——已发布消息按逐条路径同语义标记；
            // 终态写不响应本次取消，对齐失败路径的 CancellationToken.None 口径）
            await FlushMarksAsync(force: true, CancellationToken.None).ConfigureAwait(false);
        }

        return new PartitionCounters(processed, dead, retried, persistFailed);
    }

    /// <summary>
    /// T3.1（2026-10-11 优化实施轮）：发布确认流水线化——窗口 &gt;1 时的分区内处理形态。
    /// FIFO 窗口保序：发布按分区内顺序启动（RabbitMQ 单 channel 帧序由 _confirmSemaphore
    /// 天然保序；Kafka producer 队列保序，重试乱序须 enable.idempotence，见
    /// <see cref="OutboxOptions.MaxInFlightPublishes"/> 声明），出窗完成处理也按启动序——
    /// 标记顺序与串行路径一致。每消息语义与串行路径逐项对齐（发布/标记同 try、失败走
    /// 退避重试/死信、PersistSingle 计数口径），<b>改动须双侧同步</b>。
    /// </summary>
    private async ValueTask<PartitionCounters> ProcessPartitionPipelinedAsync(
        IEnumerable<OutboxMessage> messages, OutboxOptions options, DeliveryPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        var processed = 0;
        var dead = 0;
        var retried = 0;
        var persistFailed = 0;

        // R4/P0-2（ADR-027）：成功路径批量标记（同串行路径；批序 = 出窗序 = 启动序）
        var batchMark = policy.BatchMarkThreshold > 1 && _store is IBatchMarkOutboxStore batchStore ? batchStore : null;
        List<OutboxMessage>? markPending = batchMark is null ? null : new List<OutboxMessage>(Math.Min(policy.BatchMarkThreshold, 64));
        var lastFlushAt = _timeProvider.GetTimestamp();

        async ValueTask FlushMarksAsync(bool force, CancellationToken flushCt)
        {
            if (markPending is not { Count: > 0 })
                return;
            if (!force
                && markPending.Count < policy.BatchMarkThreshold
                && _timeProvider.GetElapsedTime(lastFlushAt) < policy.BatchMarkFlushInterval)
                return;

            var flushed = markPending.Count;
            if (await TryFlushBatchMarkAsync(batchMark!, markPending, now, options, flushCt).ConfigureAwait(false))
                checked { processed += flushed; }
            else
                checked { retried += flushed; }
            lastFlushAt = _timeProvider.GetTimestamp();
        }

        async ValueTask HandleDeadAsync(OutboxMessage msg, string reason)
        {
            await _store.MarkDeadAsync(msg, Core.FailureReason.Normalize(reason), now, ct).ConfigureAwait(false);
            checked { dead++; }
            PalMetrics.OutboxDead.Add(1);
            if (!await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false))
                checked { persistFailed++; }
        }

        async ValueTask HandleOutcomeAsync(OutboxMessage msg, Task publish)
        {
            try
            {
                await publish.ConfigureAwait(false);
                if (batchMark is null)
                {
                    await _store.MarkProcessedAsync(msg, now, ct).ConfigureAwait(false);
                    checked { processed++; }
                    if (!await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false))
                    {
                        checked { processed--; persistFailed++; }
                        PalMetrics.OutboxPersistFailed.Add(1);
                    }
                }
                else
                {
                    // R4/P0-2：批量标记累积（成功路径；失败路径落下方 catch 逐条处置不变）
                    markPending!.Add(msg);
                    await FlushMarksAsync(
                        force: markPending.Count >= policy.BatchMarkThreshold, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 失败语义逐项镜像串行路径（快照/退避降级/标记错误挂 Data/PersistSingle）——
                // 口径注释见串行路径同型段，改动双侧同步
                var retryAtFailure = msg.RetryCount;
                TimeSpan delay;
                try
                {
                    delay = options.RetryBackoffPolicy.ComputeDelay(msg.RetryCount + 1);
                }
                catch (Exception delayEx)
                {
                    delay = TimeSpan.FromSeconds(1);
                    _logger.Warning($"Outbox: retry backoff computation failed for message {msg.Id}, falling back to 1s delay: {delayEx.Message}");
                }
                var nextAttemptAt = now + delay;
                var failureReason = PalDDD.Core.FailureReason.Normalize(ex.Message);
                if (msg.RetryCount + 1 >= options.MaxRetryCount)
                {
                    try
                    {
                        await _store.MarkDeadAsync(msg, failureReason, now, ct).ConfigureAwait(false);
                    }
                    catch (Exception markEx)
                    {
                        ex.Data["MarkError"] = markEx.Message;
                        _logger.Warning($"Outbox: MarkDead for {msg.Id} failed: {markEx.Message}");
                    }
                    checked { dead++; }
                    PalMetrics.OutboxDead.Add(1);
                }
                else
                {
                    try
                    {
                        await _store.ReleaseForRetryAsync(msg, failureReason, nextAttemptAt, ct).ConfigureAwait(false);
                    }
                    catch (Exception markEx)
                    {
                        ex.Data["MarkError"] = markEx.Message;
                        _logger.Warning($"Outbox: ReleaseForRetry for {msg.Id} failed: {markEx.Message}");
                    }
                    checked { retried++; }
                }
                _logger.Warning($"Outbox: message {msg.Id} processing failed at retry {retryAtFailure + 1}: {ex.Message}");
                if (!await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false))
                    checked { persistFailed++; }
            }
        }

        var window = new Queue<(OutboxMessage Message, Task Publish)>();
        try
        {
            foreach (var msg in messages)
            {
                await FlushMarksAsync(force: false, ct).ConfigureAwait(false);
                // 预处理（目录/反序列化失败即死信，不入窗）——镜像串行路径前置段
                var descriptor = _messageCatalog.Find(msg.Type, msg.SchemaVersion);
                if (descriptor is null)
                {
                    await HandleDeadAsync(msg, $"Type '{msg.Type}' not registered in MessageCatalog").ConfigureAwait(false);
                    continue;
                }
                // 2026-10-19：raw 路径不再反序列化；仅对象路径做 null 死信检查。
                var raw = CanPublishRaw(msg, descriptor);
                object? @event = null;
                if (!raw)
                {
                    @event = _serializer.Deserialize(msg.Payload, descriptor);
                    if (@event is null)
                    {
                        await HandleDeadAsync(msg, "Deserialization returned null").ConfigureAwait(false);
                        continue;
                    }
                }
                var publishContext = new Messaging.MessagePublishContext(
                    msg.CorrelationId, msg.CausationId, msg.TraceParent, msg.TraceState);

                // 窗口满先收队头（维持上限——队头出窗保标记启动序）
                while (window.Count >= policy.PublishWindow)
                {
                    var oldest = window.Dequeue();
                    await HandleOutcomeAsync(oldest.Message, oldest.Publish).ConfigureAwait(false);
                }

                // 发布任务热启动（异常流入 Task，出窗侧统一处理——发布与标记同 try 的语义
                // 由 HandleOutcomeAsync 承载）；任务体仅转发 broker 调用
                window.Enqueue((msg, PublishCoreAsync()));

                async Task PublishCoreAsync()
                {
                    if (raw)
                        await _broker.PublishRawAsync(msg.Payload, descriptor, msg.Id, publishContext, ct).ConfigureAwait(false);
                    else
                        await _broker.PublishAsync(@event!, descriptor, msg.Id, publishContext, ct).ConfigureAwait(false);
                }
            }
            while (window.Count > 0)
            {
                var entry = window.Dequeue();
                await HandleOutcomeAsync(entry.Message, entry.Publish).ConfigureAwait(false);
            }
        }
        finally
        {
            // R4/P0-2：批末冲刷残留累积（含 OCE 关停路径——终态写不响应本次取消）
            await FlushMarksAsync(force: true, CancellationToken.None).ConfigureAwait(false);
        }

        return new PartitionCounters(processed, dead, retried, persistFailed);
    }

    /// <summary>
    /// R4/P0-2（ADR-027）：批量标记冲刷——成功返回 true（调用方 processed += 批大小）；
    /// 失败（DB 故障等）镜像逐条路径 MarkProcessed 抛出的处置（逐条退避释放重投）后返回
    /// false（调用方 retried += 批大小）。部分 fencing 拒绝（affected &lt; 批大小，无异常）
    /// 不视为失败——processed 计数沿用逐条路径的漂移口径（R2 独立可见）。调用返回时
    /// <paramref name="pending"/> 已清空。
    /// </summary>
    private async ValueTask<bool> TryFlushBatchMarkAsync(
        IBatchMarkOutboxStore batchStore, List<OutboxMessage> pending, DateTimeOffset now, OutboxOptions options, CancellationToken ct)
    {
        try
        {
            await batchStore.MarkProcessedBatchAsync(pending, now, ct).ConfigureAwait(false);
            pending.Clear();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning($"Outbox: batch mark failed for {pending.Count} published message(s), releasing each for retry: {ex.Message}");
            foreach (var msg in pending)
            {
                TimeSpan delay;
                try
                {
                    delay = options.RetryBackoffPolicy.ComputeDelay(msg.RetryCount + 1);
                }
                catch (Exception delayEx)
                {
                    delay = TimeSpan.FromSeconds(1);
                    _logger.Warning($"Outbox: retry backoff computation failed for message {msg.Id}, falling back to 1s delay: {delayEx.Message}");
                }
                var failureReason = PalDDD.Core.FailureReason.Normalize(ex.Message);
                try
                {
                    await _store.ReleaseForRetryAsync(msg, failureReason, now + delay, ct).ConfigureAwait(false);
                }
                catch (Exception markEx)
                {
                    ex.Data["MarkError"] = markEx.Message;
                    _logger.Warning($"Outbox: ReleaseForRetry for {msg.Id} failed: {markEx.Message}");
                }
            }
            pending.Clear();
            return false;
        }
    }

    /// <summary>分区处理计数（C1 并行聚合单元——class 使 lock 聚合合法；量级 4 int）。</summary>
    private sealed class PartitionCounters
    {
        public int Processed;
        public int Dead;
        public int Retried;
        public int PersistFailed;

        public PartitionCounters(int processed, int dead, int retried, int persistFailed)
        {
            Processed = processed;
            Dead = dead;
            Retried = retried;
            PersistFailed = persistFailed;
        }

        public void Add(PartitionCounters other)
        {
            Processed += other.Processed;
            Dead += other.Dead;
            Retried += other.Retried;
            PersistFailed += other.PersistFailed;
        }
    }

    /// <summary>
    /// 是否可直发持久化字节：broker 支持 raw，且消息与描述符的 ContentType 都与当前
    /// 序列化器一致。SchemaVersion 已由 <see cref="Serialization.IMessageCatalog.Find(string,int)"/>
    /// 按存储版本解析，payload 与该描述符逐字节对应，无需再反序列化/重序列化。
    /// </summary>
    private bool CanPublishRaw(OutboxMessage msg, Serialization.MessageDescriptor descriptor)
        => _broker.SupportsRawPublish
           && string.Equals(msg.ContentType, _serializer.ContentType, StringComparison.OrdinalIgnoreCase)
           && string.Equals(descriptor.ContentType, _serializer.ContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析本批次的投递策略（2026-10-19 性能实施轮）。
    /// <para>
    /// Auto：broker 声明可并发发布时把窗口提升到至少 16；store 实现批量标记时把阈值
    /// 提升到至少 16，并由 <see cref="OutboxOptions.BatchMarkFlushInterval"/> 加时间上界。
    /// 能力不足时保留用户显式值（默认 1），不会替不支持并发的 broker 擅自升窗。
    /// </para>
    /// </summary>
    private DeliveryPolicy ResolveDeliveryPolicy(OutboxOptions options)
    {
        if (options.DeliveryMode == OutboxDeliveryMode.Serial)
            return new DeliveryPolicy(1, options.BatchMarkThreshold, TimeSpan.MaxValue);

        var window = options.MaxInFlightPublishes;
        if (_broker.SupportsConcurrentPublish)
            window = Math.Max(window, 16);

        var threshold = options.BatchMarkThreshold;
        if (_store is IBatchMarkOutboxStore)
            threshold = Math.Max(threshold, 16);

        return new DeliveryPolicy(
            window,
            threshold,
            threshold > 1 ? options.BatchMarkFlushInterval : TimeSpan.MaxValue);
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct DeliveryPolicy(
        int PublishWindow,
        int BatchMarkThreshold,
        TimeSpan BatchMarkFlushInterval);

    /// <summary>逐条持久化 — 每条消息处理后立即 SaveChanges，避免批次回滚。
    /// R2 修正（2026-09-20）：失败时调用方以 persistFailed 计数（不再混入 processed——
    /// 指标与 DB 真相一致：processed 仅含落库成功的条目）。
    /// 2026-10-19：内置四栈的 Mark* 已即时写库（<see cref="IPalOutboxStore.RequiresSaveAfterMark"/>
    /// 为 false），跳过纯空转的 SaveChanges；自定义延迟写 store 保持旧行为。</summary>
    private async ValueTask<bool> PersistSingleAsync(PalUlid messageId, CancellationToken ct)
    {
        if (!_store.RequiresSaveAfterMark)
            return true;
        try
        {
            await _store.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 单条持久化失败 — 下轮轮询会重试
            // 最坏情况：消息被多处理一次（幂等消费需在 Handler 中保证）
            _logger.Warning($"Outbox: state persistence for {messageId} failed: {ex.Message}. Next poll will retry using last persisted state.");
            return false;
        }
    }
}
