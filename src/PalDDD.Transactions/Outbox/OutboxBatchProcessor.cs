// ─────────────────────────────────────────────────────────────
// 📤 OutboxBatchProcessor — Scoped 批次发布器
//    由 OutboxProcessor 每 tick 创建一个 scope 实例，租约获取一批待发消息并逐条发布。
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.Options;
using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;
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
    // 租约持有者标识 — 从 OutboxOptions.LeaseOwner 读取，支持多实例部署时自定义

    public OutboxBatchProcessor(
        IPalOutboxStore store,
        Messaging.IMessageBroker broker,
        Serialization.IMessageSerializer serializer,
        Serialization.IMessageCatalog messageCatalog,
        IOptionsMonitor<OutboxOptions> options,
        IPalLogger<OutboxBatchProcessor> logger,
        TimeProvider? timeProvider = null)
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
    }

    /// <summary>处理一个已租约的批次。</summary>
    public async ValueTask ProcessBatchAsync(CancellationToken ct)
    {
        var options = _options.CurrentValue;
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
        var processed = 0;
        var dead = 0;
        var retried = 0;

        try
        {
            foreach (var msg in messages)
            {
                try
                {
                    // ITM-217 修复：按存储的 SchemaVersion 解析——Find(name) 返回最新版本，
                    // 旧版 payload 被新版 descriptor 解释会产生错误字段/反序列化失败。
                    var descriptor = _messageCatalog.Find(msg.Type, msg.SchemaVersion);
                    if (descriptor is null)
                    {
                        _store.MarkDead(msg, Core.FailureReason.Normalize($"Type '{msg.Type}' not registered in MessageCatalog"), now);
                        checked { dead++; }
                        await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false);
                        continue;
                    }

                    var @event = _serializer.Deserialize(msg.Payload, descriptor);
                    if (@event is null)
                    {
                        _store.MarkDead(msg, Core.FailureReason.Normalize("Deserialization returned null"), now);
                        checked { dead++; }
                        await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false);
                        continue;
                    }

                    var publishContext = new Messaging.MessagePublishContext(
                        msg.CorrelationId,
                        msg.CausationId,
                        msg.TraceParent,
                        msg.TraceState);
                    await _broker.PublishAsync(@event, descriptor, msg.Id, publishContext, ct).ConfigureAwait(false);
                    _store.MarkProcessed(msg, now);
                    checked { processed++; }
                    await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false);
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
                        // （此处主异常不向上传播，Data 信息需日志兜底可见），主异常优先。
                        try
                        {
                            _store.MarkDead(msg, failureReason, now);
                        }
                        catch (Exception markEx)
                        {
                            ex.Data["MarkError"] = markEx.Message;
                            _logger.Warning($"Outbox: MarkDead for {msg.Id} failed: {markEx.Message}");
                        }
                        checked { dead++; }
                    }
                    else
                    {
                        try
                        {
                            _store.ReleaseForRetry(msg, failureReason, nextAttemptAt);
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
                    await PersistSingleAsync(msg.Id, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // ITM-097 修复：指标段移入 finally——单条消息 OCE 直接中止整批时（如关停），
            // 已处理/死亡/重试计数仍被记录，批次指标不再随 OCE 丢失（finally 中不再抛）
            // v28 P3 观测局限声明：IPalOutboxStore.Mark*（MarkProcessed/MarkDead/
            // ReleaseForRetry）三栈均为 void 返回——InMemory 栈 IsCurrentLeaseHolder 守卫
            // 失败时静默 no-op、EF 栈 fencing token 拒绝时提前 return、PersistSingleAsync
            // 持久化失败时吞成 Warning，processed/dead/retried 计数含租约竞态下标记未落库
            // 的条目。指标为尽力语义（计数=已尝试标记数），精确计数需 v3.0 接口返回
            // affected 行数（接口签名变更超出 P3 范围）
            activity?.SetTag("pal.outbox.processed", processed);
            activity?.SetTag("pal.outbox.dead", dead);
            activity?.SetTag("pal.outbox.retried", retried);
            PalMetrics.OutboxProcessed.Add(processed);
            PalMetrics.OutboxFailed.Add(dead + retried);
        }
    }

    /// <summary>逐条持久化 — 每条消息处理后立即 SaveChanges，避免批次回滚</summary>
    private async ValueTask PersistSingleAsync(PalUlid messageId, CancellationToken ct)
    {
        try
        {
            await _store.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 单条持久化失败 — 下轮轮询会重试
            // 最坏情况：消息被多处理一次（幂等消费需在 Handler 中保证）
            _logger.Warning($"Outbox: state persistence for {messageId} failed: {ex.Message}. Next poll will retry using last persisted state.");
        }
    }
}
