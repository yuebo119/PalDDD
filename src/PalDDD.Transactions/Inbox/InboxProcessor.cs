// ─────────────────────────────────────────────────────────────
// 📥 InboxProcessor — (ConsumerName,MessageId) 幂等消费
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// 收件箱幂等消费处理器
// ─────────────────────────────────────────────────────────────

/// <summary>收件箱处理器 — 基于存储唯一约束的幂等消费</summary>
/// <remarks>
/// 核心思路：消息处理前先写入收件箱状态，处理成功后标记为 Processed。<br/>
/// 失败消息会保留 Failed 状态，由消息代理重投递或 DLQ 策略负责重试。<br/>
/// MessageId 保持唯一，避免重复消费。
/// </remarks>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "Inbox 需在重新抛出前标记任意用户 handler 失败，需捕获 Exception 基类。")]
public sealed class InboxProcessor
{
    // P3 修复（十七轮）：失败原因入库截断上限——LastError 列上限 2048，调用方传入
    // 超长 ex.Message 会让终态保存本身失败（对齐 InboxDbContext.MarkFailedAsync 防御）

    private readonly IInboxStore _store;
    private readonly IPalLogger<InboxProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<InboxOptions> _options;

    // 2026-10-21 契约盘点 R6：IBatchInboxStore 缺失时的回落只提示一次（每 processor 实例），
    // 避免每批 50ms 一条的日志放大；置位在读 IsEnabled 之后，级别临时关闭时不吞掉后续开启。
    private int _batchFallbackLogged;

    public InboxProcessor(
        IInboxStore store,
        IOptionsMonitor<InboxOptions> options,
        IPalLogger<InboxProcessor> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>尝试处理消息 — 已处理则返回 false（幂等保证）</summary>
    /// <remarks>
    /// 🔴 P1 性能修复 (2026-07-28): 此重载只是转发到带 consumerName 的重载。
    /// 原实现用 <c>async + await</c> 会让编译器为这个零逻辑的转发方法生成一个
    /// 完整的异步状态机（分配一个 <c>AsyncTaskMethodBuilder&lt;bool&gt;</c> + 状态字段），
    /// 在高频消费路径上产生无谓的分配压力。改为直接返回底层 ValueTask 即可跳过该状态机。
    /// </remarks>
    public ValueTask<bool> TryProcessAsync<TMessage>(
        string messageId,
        Func<TMessage, CancellationToken, ValueTask> handler,
        TMessage message,
        CancellationToken ct = default)
        => TryProcessAsync(_options.CurrentValue.DefaultConsumerName, messageId, handler, message, ct);

    /// <summary>尝试处理消息 — 以消费者名称隔离幂等记录。</summary>
    public async ValueTask<bool> TryProcessAsync<TMessage>(
        string consumerName,
        string messageId,
        Func<TMessage, CancellationToken, ValueTask> handler,
        TMessage message,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(handler);
        // ITM-167 修复：补 message null 守卫——引用类型 TMessage 传 null 时 handler
        // 内部才解引用（或经序列化路径），失败点远离入口且与 value type TMessage 的
        // 行为不对称；入口显式守卫（值类型 TMessage 装箱检查，恒非 null 零开销）。
        ArgumentNullException.ThrowIfNull(message);

        var options = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow();
        using var activity = PalActivitySource.StartInboxProcess(consumerName, messageId);

        var record = await _store.TryStartProcessingAsync(
            consumerName,
            messageId,
            now,
            options.ProcessingTimeout,
            ct).ConfigureAwait(false);

        if (record is null)
        {
            activity?.SetTag("pal.inbox.result", "skipped");
            PalMetrics.InboxSkipped.Add(1);
            // EPSL 地毯审计 2026-10-11 A-2：插值字符串在调用前求值，Information 关闭时守卫
            // 免每消息的字符串分配（IsEnabled 的文档化用途，见 IPalLogger）
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.Information(PalLogEventIds.InboxMessageSkipped,
                    "Inbox: message {MessageId} is already processed or processing, skipping", messageId);
            return false;
        }

        try
        {
            await handler(message, ct).ConfigureAwait(false);
            WarnIfLeaseExceeded(consumerName, messageId, options.ProcessingTimeout, now);
            // P2 修复（八轮评审）：副作用（handler）已发生后，完成标记不应随请求级 ct 取消——
            // 取消会导致 Processing 记录滞留，租约/超时到期后同一消息被双重执行
            // （对齐下方 MarkFailedAsync 的 CancellationToken.None）
            //
            // ⚠️ v17 补文（OCE 滞留语义）：handler 本身抛 OCE（如 ct 取消传播）时同样不标
            // Failed——记录滞留 Processing 至 ProcessingTimeout 才可重入。此为设计取舍：
            // OCE 语义 = "不知道执行到哪一步"，标 Failed 会重放副作用而实际可能已完成。
            // 代价 = 最长 Timeout 的重试延迟（默认 5 分钟）。
            try
            {
                await _store.MarkProcessedAsync(record, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception markEx)
            {
                // ITM-180 修复（二十九轮）：handler 成功但标记失败（DB 故障）——副作用已发生，
                // 不得按通用失败重新标记 Failed 再抛（那会把"已执行"降级为"可重试失败"，
                // 重试时重放副作用）。记录区分性错误日志后按成功返回（at-least-once 语义下
                // 状态待观察者确认；Inbox 的 Processed 状态由下一轮循环/监控补正）。
                // v66 P3：移除原 `when (markEx is not OperationCanceledException)` 过滤，对齐
                // ITM-092 口径——MarkProcessedAsync 以 CancellationToken.None 调用，其抛 OCE
                // 属存储异常形态（而非请求级取消传播），原过滤让 OCE 逃逸给调用方，而
                // handler 实际已成功——调用方按取消处理触发重试重放路径；捕获后统一按
                // processed-pending-confirmation 处理（与下方 MarkFailedAsync 的不过滤口径对称）。
                _logger.Error(markEx, PalLogEventIds.InboxMessageProcessedMarkFailed,
                    "Inbox: message {MessageId} handler SUCCEEDED but MarkProcessed failed; state pending confirmation (at-least-once)",
                    messageId);
                activity?.SetTag("pal.inbox.result", "processed-pending-confirmation");
                // ITM-197 修复（三十轮）：该路径补指标——修复前监控盲区（handler 成功但状态
                // 待确认在 InboxProcessed/InboxFailed 均不可见）。
                PalMetrics.InboxProcessed.Add(1);
                return true;
            }
            activity?.SetTag("pal.inbox.result", "processed");
            PalMetrics.InboxProcessed.Add(1);
            // A-2 同款守卫（成功路径与跳过路径同为每消息热路径）
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.Information(PalLogEventIds.InboxMessageProcessed,
                    "Inbox: message {MessageId} processed successfully", messageId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WarnIfLeaseExceeded(consumerName, messageId, options.ProcessingTimeout, now);
            // P3 修复（十七轮）：ex.Message 截断到 2000 再入库——长异常消息（含大 payload
            // 的序列化错误等）超出 LastError 列上限会让终态保存本身失败，掩盖原始 handler 失败
            var failureReason = PalDDD.Core.FailureReason.Normalize(ex.Message);
            // ITM-092 修复：MarkFailedAsync 本身失败（DB 故障）不得掩盖主异常——内层捕获把清理
            // 错误挂到主异常 Data 上，仍以主异常优先向上传播（对齐 SagaProcessor OCE 释放路径）。
            // 验证轮返工：内层 catch 不加 OCE 过滤——MarkFailedAsync 以 CancellationToken.None
            // 调用，其抛 OCE 属异常形态（而非外层取消传播），同样不得覆盖主异常。
            try
            {
                await _store.MarkFailedAsync(record, failureReason, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception markEx)
            {
                ex.Data["MarkFailedError"] = markEx.Message;
            }
            activity?.SetTag("pal.inbox.result", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PalMetrics.InboxFailed.Add(1);
            _logger.Error(ex, PalLogEventIds.InboxMessageFailed,
                "Inbox: message {MessageId} handler failed", messageId);
            throw;
        }
    }

    /// <summary>
    /// 租约超限告警（2026-10-21 契约盘点）：handler 时长达到 <see cref="InboxOptions.ProcessingTimeout"/>
    /// 时，记录可能已被第二个消费者重新认领并并发执行。不改变行为，只把默认配置下的静默风险
    /// 变成可观测事件（审计 2026-09-20 登记的「配置契约无机械化校验或启动告警」缺口）。
    /// </summary>
    private void WarnIfLeaseExceeded(string consumerName, string messageId, TimeSpan processingTimeout, DateTimeOffset claimedAt)
    {
        var elapsed = _timeProvider.GetUtcNow() - claimedAt;
        if (elapsed < processingTimeout)
            return;

        _logger.Warning(PalLogEventIds.InboxHandlerLeaseExceeded,
            "Inbox: handler for message {MessageId} (consumer {ConsumerName}) ran {ElapsedMs}ms, reaching ProcessingTimeout {TimeoutMs}ms; the record may have been re-claimed and executed concurrently. Configure InboxOptions.ProcessingTimeout above the longest handler duration.",
            messageId, consumerName, (long)elapsed.TotalMilliseconds, (long)processingTimeout.TotalMilliseconds);
    }

    /// <summary>
    /// 批量优化不可用的一次性提示（2026-10-21 契约盘点 R6）：store 未实现
    /// <see cref="IBatchInboxStore"/> 时每消息仍是 2 次往返，此前文档说明回落但运行期
    /// 无任何信号，PalORM/EF 消费者按吞吐选型时无从发现。每 processor 实例只记一次，
    /// 避免每批一条的日志放大。
    /// </summary>
    private void LogBatchFallbackOnce(string consumerName)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
            return;
        if (Interlocked.Exchange(ref _batchFallbackLogged, 1) != 0)
            return;

        _logger.Information(
            $"Inbox: store {_store.GetType().Name} does not implement IBatchInboxStore; batch consumption for consumer {consumerName} falls back to per-item TryProcessAsync (two round trips per message).");
    }

    /// <summary>
    /// 批量处理消息（2026-10-21）：一次认领、逐条执行、一次批量完成标记。存储未实现
    /// <see cref="IBatchInboxStore"/> 时回落逐条路径。单个 handler 失败不中断其余；
    /// 成功项批量标记，失败项逐条标记后聚合抛出。返回成功处理的消息数。
    /// </summary>
    public async ValueTask<int> TryProcessBatchAsync<TMessage>(
        string consumerName,
        IReadOnlyList<(string MessageId, TMessage Message)> batch,
        Func<TMessage, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(handler);
        if (batch.Count == 0)
            return 0;

        if (_store is not IBatchInboxStore batchStore)
        {
            // 2026-10-21 评审 F8：回落路径必须与批量路径同语义——逐条 try/catch，单条失败
            // 由 TryProcessAsync 标 Failed 后继续处理其余，最后聚合抛出。原实现让首个 handler
            // 异常中止整批，第 2..N 条既不认领也不标记；投递侧是 at-most-once（Kafka 自动提交 /
            // RabbitMQ requeue:false），这些消息不会重投。
            LogBatchFallbackOnce(consumerName);
            var processedFallback = 0;
            List<Exception>? fallbackFailures = null;
            foreach (var (messageId, message) in batch)
            {
                try
                {
                    if (await TryProcessAsync(consumerName, messageId, handler, message, ct).ConfigureAwait(false))
                        processedFallback++;
                }
                catch (OperationCanceledException)
                {
                    // 取消：已认领记录滞留 Processing 至租约到期（与批量路径同语义）。
                    throw;
                }
                catch (Exception ex)
                {
                    (fallbackFailures ??= []).Add(ex);
                }
            }
            if (fallbackFailures is not null)
                throw new AggregateException($"{fallbackFailures.Count} inbox handler(s) failed.", fallbackFailures);
            return processedFallback;
        }

        var options = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow();
        var byId = new Dictionary<string, TMessage>(batch.Count, StringComparer.Ordinal);
        var ids = new List<string>(batch.Count);
        foreach (var (messageId, message) in batch)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            ArgumentNullException.ThrowIfNull(message, nameof(batch));
            if (byId.TryAdd(messageId, message))
                ids.Add(messageId);
        }

        using var activity = PalActivitySource.StartInboxProcess(consumerName, "batch");
        var claimed = await batchStore.TryStartProcessingManyAsync(
            consumerName, ids, now, options.ProcessingTimeout, ct).ConfigureAwait(false);
        // R9：按去重后的 ID 数计算。batch.Count 含批内重复 MessageId，那些条目在认领前已去重，
        // 计为 skipped 会让 InboxSkipped 指标与 span 标签虚高（实际没有消息被跳过）。
        var skipped = ids.Count - claimed.Count;
        if (skipped > 0)
        {
            PalMetrics.InboxSkipped.Add(skipped);
            activity?.SetTag("pal.inbox.skipped", skipped);
        }

        if (claimed.Count == 0)
        {
            activity?.SetTag("pal.inbox.result", "skipped");
            return 0;
        }

        var succeeded = new List<InboxMessage>(claimed.Count);
        List<Exception>? failures = null;
        // R9：完成标记是否全部落库。false 时本批处于 processed-pending-confirmation，
        // 不得输出 1005 "processed"（否则与 1004 自相矛盾）。
        var marksConfirmed = true;
        // 2026-10-21 契约盘点 R6：批量路径补齐 1006 告警——此前 WarnIfLeaseExceeded 只在
        // 逐条路径调用，批量消费（usage.md 推荐的吞吐路径）在 handler 超过 ProcessingTimeout
        // 时没有任何信号。按批聚合为一条（超限条数 + 最大耗时），避免每批 N 条日志放大；
        // 计时起点与逐条路径一致取认领时刻 now（processing_started_at 同源），批内后段消息
        // 等待前序 handler 的时长同样计入其租约老化，属真实风险而非噪声。
        var leaseExceeded = 0;
        long maxLeaseElapsedMs = 0;
        void ObserveLease()
        {
            var elapsed = _timeProvider.GetUtcNow() - now;
            if (elapsed < options.ProcessingTimeout)
                return;
            leaseExceeded++;
            var elapsedMs = (long)elapsed.TotalMilliseconds;
            if (elapsedMs > maxLeaseElapsedMs)
                maxLeaseElapsedMs = elapsedMs;
        }

        void FlushLeaseWarning()
        {
            if (leaseExceeded == 0)
                return;
            _logger.Warning(PalLogEventIds.InboxHandlerLeaseExceeded,
                "Inbox: {Count} handler(s) for consumer {ConsumerName} in one batch reached ProcessingTimeout {TimeoutMs}ms (max {MaxElapsedMs}ms); the records may have been re-claimed and executed concurrently. Configure InboxOptions.ProcessingTimeout above the longest handler duration.",
                leaseExceeded, consumerName, (long)options.ProcessingTimeout.TotalMilliseconds, maxLeaseElapsedMs);
        }

        foreach (var record in claimed)
        {
            try
            {
                await handler(byId[record.MessageId], ct).ConfigureAwait(false);
                succeeded.Add(record);
                PalMetrics.InboxProcessed.Add(1);
                ObserveLease();
            }
            catch (OperationCanceledException)
            {
                // 取消：不标 Failed，已认领记录滞留 Processing 至租约到期（与逐条路径同语义）。
                // 超限告警在抛出前冲刷——取消不能成为静默窗口。
                ObserveLease();
                FlushLeaseWarning();
                throw;
            }
            catch (Exception ex)
            {
                ObserveLease();
                (failures ??= []).Add(ex);
                PalMetrics.InboxFailed.Add(1);
                try
                {
                    await _store.MarkFailedAsync(
                        record,
                        PalDDD.Core.FailureReason.Normalize(ex.Message),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception markEx)
                {
                    ex.Data["MarkFailedError"] = markEx.Message;
                }
            }
        }

        // 批末冲刷：成功/失败/混合批次都可见（取消路径已在 catch 内提前冲刷）。
        FlushLeaseWarning();

        if (succeeded.Count > 0)
        {
            try
            {
                var marked = await batchStore.MarkProcessedManyAsync(
                    consumerName, succeeded, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                if (marked < succeeded.Count)
                {
                    // R9：部分未命中（租约被抢/令牌不匹配）与整批失败同属"handler 成功但完成标记
                    // 未全部落库"，与逐条路径 1004 同口径。原实现是无 EventId 的 Warning，
                    // 按 EventId 过滤的告警规则命中不到。
                    marksConfirmed = false;
                    _logger.Warning(PalLogEventIds.InboxMessageProcessedMarkFailed,
                        "Inbox: batch completion marked {MarkedCount}/{SucceededCount} message(s) for consumer {ConsumerName}; unmarked rows stay Processing until lease expiry (at-least-once).",
                        marked, succeeded.Count, consumerName);
                }
            }
            catch (Exception markEx)
            {
                // handler 已成功：按 at-least-once 返回成功，状态待确认（与逐条路径同语义）。
                marksConfirmed = false;
                _logger.Error(markEx, PalLogEventIds.InboxMessageProcessedMarkFailed,
                    "Inbox: batch handler succeeded but MarkProcessedMany failed for {MessageCount} message(s) for consumer {ConsumerName}; state pending confirmation (at-least-once).",
                    succeeded.Count, consumerName);
            }
        }

        if (failures is not null)
        {
            var aggregate = new AggregateException($"{failures.Count} inbox handler(s) failed.", failures);
            // R9：批量路径此前不发 1003，只有单条路径发；无 EventId 的 broker 侧字符串无法被
            // 告警规则命中。按批聚合一条（逐条 MarkFailed 已各自落库），与 1006 的批量口径一致。
            _logger.Error(aggregate, PalLogEventIds.InboxMessageFailed,
                "Inbox: {FailedCount} handler(s) failed for consumer {ConsumerName} in one batch; failed records were marked Failed.",
                failures.Count, consumerName);
            activity?.SetTag("pal.inbox.result", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, $"{failures.Count} handler(s) failed");
            throw aggregate;
        }

        activity?.SetTag("pal.inbox.result", marksConfirmed ? "processed" : "processed-pending-confirmation");
        if (marksConfirmed && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.Information(PalLogEventIds.InboxBatchProcessed,
                "Inbox: batch of {MessageCount} message(s) processed for consumer {ConsumerName}",
                claimed.Count, consumerName);
        }
        return succeeded.Count;
    }

    /// <summary>批量处理消息（使用默认消费者名）。</summary>
    public ValueTask<int> TryProcessBatchAsync<TMessage>(
        IReadOnlyList<(string MessageId, TMessage Message)> batch,
        Func<TMessage, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
        => TryProcessBatchAsync(_options.CurrentValue.DefaultConsumerName, batch, handler, ct);
}
