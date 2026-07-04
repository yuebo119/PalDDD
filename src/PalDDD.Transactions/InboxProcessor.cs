// ─────────────────────────────────────────────────────────────
// 📥 InboxProcessor — (ConsumerName,MessageId) 幂等消费
// ─────────────────────────────────────────────────────────────
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
    private readonly IInboxStore _store;
    private readonly IPalLogger<InboxProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<InboxOptions> _options;

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
    public async ValueTask<bool> TryProcessAsync<TMessage>(
        string messageId,
        Func<TMessage, CancellationToken, ValueTask> handler,
        TMessage message,
        CancellationToken ct = default)
        => await TryProcessAsync(_options.CurrentValue.DefaultConsumerName, messageId, handler, message, ct);

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

        var options = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow();
        using var activity = PalActivitySource.StartInboxProcess(consumerName, messageId);

        var record = await _store.TryStartProcessingAsync(
            consumerName,
            messageId,
            now,
            options.ProcessingTimeout,
            ct);

        if (record is null)
        {
            activity?.SetTag("pal.inbox.result", "skipped");
            PalMetrics.InboxSkipped.Add(1);
            _logger.Information($"Inbox: message {messageId} is already processed or processing, skipping");
            return false;
        }

        try
        {
            await handler(message, ct);
            await _store.MarkProcessedAsync(record, _timeProvider.GetUtcNow(), ct);
            activity?.SetTag("pal.inbox.result", "processed");
            PalMetrics.InboxProcessed.Add(1);
            _logger.Information($"Inbox: message {messageId} processed successfully");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _store.MarkFailedAsync(record, ex.Message, ct);
            activity?.SetTag("pal.inbox.result", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PalMetrics.InboxFailed.Add(1);
            _logger.Error(ex, $"Inbox: message {messageId} handler failed");
            throw;
        }
    }
}
