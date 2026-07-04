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

        var messages = await _store.LeasePendingMessagesAsync(
            options.BatchSize,
            options.LeaseOwner,
            options.LeaseDuration,
            options.MaxRetryCount,
            ct);

        using var activity = PalActivitySource.StartOutboxProcess(messages.Count);
        var processed = 0;
        var dead = 0;
        var retried = 0;

        foreach (var msg in messages)
        {
            try
            {
                var descriptor = _messageCatalog.Find(msg.Type);
                if (descriptor is null)
                {
                    _store.MarkDead(msg, $"Type '{msg.Type}' not registered in MessageCatalog", now);
                    checked { dead++; }
                    await PersistSingleAsync(msg.Id, ct);
                    continue;
                }

                var @event = _serializer.Deserialize(msg.Payload, descriptor);
                if (@event is null)
                {
                    _store.MarkDead(msg, "Deserialization returned null", now);
                    checked { dead++; }
                    await PersistSingleAsync(msg.Id, ct);
                    continue;
                }

                var publishContext = new Messaging.MessagePublishContext(
                    msg.CorrelationId,
                    msg.CausationId,
                    msg.TraceParent,
                    msg.TraceState);
                await _broker.PublishAsync(@event, descriptor, msg.Id, publishContext, ct);
                _store.MarkProcessed(msg, now);
                checked { processed++; }
                await PersistSingleAsync(msg.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // RetryCount 由 Store.ReleaseForRetry 在内部递增并与状态一同持久化，
                // 确保计数与状态原子一致（P0 修复：消除增量-持久化窗口）。
                // 退避延迟由 IRetryBackoffPolicy 计算（默认指数 2^n，上限 64s，可选抖动）。
                var nextAttemptAt = now + options.RetryBackoffPolicy.ComputeDelay(msg.RetryCount + 1);
                if (msg.RetryCount + 1 >= options.MaxRetryCount)
                {
                    _store.MarkDead(msg, ex.Message, now);
                    checked { dead++; }
                }
                else
                {
                    _store.ReleaseForRetry(msg, ex.Message, nextAttemptAt);
                    checked { retried++; }
                }
                _logger.Warning($"Outbox: message {msg.Id} processing failed at retry {msg.RetryCount + 1}: {ex.Message}");
                await PersistSingleAsync(msg.Id, ct);
            }
        }

        activity?.SetTag("pal.outbox.processed", processed);
        activity?.SetTag("pal.outbox.dead", dead);
        activity?.SetTag("pal.outbox.retried", retried);
        PalMetrics.OutboxProcessed.Add(processed);
        PalMetrics.OutboxFailed.Add(dead + retried);
    }

    /// <summary>逐条持久化 — 每条消息处理后立即 SaveChanges，避免批次回滚</summary>
    private async ValueTask PersistSingleAsync(PalUlid messageId, CancellationToken ct)
    {
        try
        {
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 单条持久化失败 — 下轮轮询会重试
            // 最坏情况：消息被多处理一次（幂等消费需在 Handler 中保证）
            _logger.Warning($"Outbox: state persistence for {messageId} failed: {ex.Message}. Next poll will retry using last persisted state.");
        }
    }
}
