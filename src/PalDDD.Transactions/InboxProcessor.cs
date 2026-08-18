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
    // P3 修复（十七轮）：失败原因入库截断上限——LastError 列上限 2048，调用方传入
    // 超长 ex.Message 会让终态保存本身失败（对齐 InboxDbContext.MarkFailedAsync 防御）
    internal const int MaxFailureReasonLength = 2000;

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
            // P2 修复（八轮评审）：副作用（handler）已发生后，完成标记不应随请求级 ct 取消——
            // 取消会导致 Processing 记录滞留，租约/超时到期后同一消息被双重执行
            // （对齐下方 MarkFailedAsync 的 CancellationToken.None）
            try
            {
                await _store.MarkProcessedAsync(record, _timeProvider.GetUtcNow(), CancellationToken.None);
            }
            catch (Exception markEx) when (markEx is not OperationCanceledException)
            {
                // ITM-180 修复（二十九轮）：handler 成功但标记失败（DB 故障）——副作用已发生，
                // 不得按通用失败重新标记 Failed 再抛（那会把"已执行"降级为"可重试失败"，
                // 重试时重放副作用）。记录区分性错误日志后按成功返回（at-least-once 语义下
                // 状态待观察者确认；Inbox 的 Processed 状态由下一轮循环/监控补正）。
                _logger.Error(markEx, $"Inbox: message {messageId} handler SUCCEEDED but MarkProcessed failed; state pending confirmation (at-least-once)");
                activity?.SetTag("pal.inbox.result", "processed-pending-confirmation");
                // ITM-197 修复（三十轮）：该路径补指标——修复前监控盲区（handler 成功但状态
                // 待确认在 InboxProcessed/InboxFailed 均不可见）。
                PalMetrics.InboxProcessed.Add(1);
                return true;
            }
            activity?.SetTag("pal.inbox.result", "processed");
            PalMetrics.InboxProcessed.Add(1);
            _logger.Information($"Inbox: message {messageId} processed successfully");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // P3 修复（十七轮）：ex.Message 截断到 2000 再入库——长异常消息（含大 payload
            // 的序列化错误等）超出 LastError 列上限会让终态保存本身失败，掩盖原始 handler 失败
            var failureReason = ex.Message.Length <= MaxFailureReasonLength
                ? ex.Message
                : ex.Message[..MaxFailureReasonLength];
            // ITM-092 修复：MarkFailedAsync 本身失败（DB 故障）不得掩盖主异常——内层捕获把清理
            // 错误挂到主异常 Data 上，仍以主异常优先向上传播（对齐 SagaProcessor OCE 释放路径）。
            // 验证轮返工：内层 catch 不加 OCE 过滤——MarkFailedAsync 以 CancellationToken.None
            // 调用，其抛 OCE 属异常形态（而非外层取消传播），同样不得覆盖主异常。
            try
            {
                await _store.MarkFailedAsync(record, failureReason, CancellationToken.None);
            }
            catch (Exception markEx)
            {
                ex.Data["MarkFailedError"] = markEx.Message;
            }
            activity?.SetTag("pal.inbox.result", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PalMetrics.InboxFailed.Add(1);
            _logger.Error(ex, $"Inbox: message {messageId} handler failed");
            throw;
        }
    }
}
