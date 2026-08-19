// ─────────────────────────────────────────────────────────────
// 🔁 IdempotencyProcessor — (OperationName,Key) 幂等执行（结果缓存 + 租约）
// ─────────────────────────────────────────────────────────────
using PalDDD.Core.Diagnostics;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Idempotency;

// ─────────────────────────────────────────────────────────────
// 幂等执行处理器
// ─────────────────────────────────────────────────────────────

[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "幂等处理器需在重新抛出前持久化任意用户 handler 失败信息，需捕获 Exception 基类。")]
public sealed class IdempotencyProcessor
{
    // ITM-175 修复（二十九轮）：失败原因入库截断上限——error 列 HasMaxLength(2048)
    // （IdempotencyDbContext），超长 ex.Message 让 MarkFailedAsync 自身抛截断异常 →
    // 失败记录残留 Processing → 租约过期重放 → 副作用二次执行。
    // 对齐 OutboxBatchProcessor/InboxProcessor 的 MaxFailureReasonLength=2000（PD24 失败标记族）。
    internal const int MaxFailureReasonLength = 2000;

    private readonly IIdempotencyStore _store;
    private readonly TimeProvider _timeProvider;

    public IdempotencyProcessor(IIdempotencyStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IdempotencyExecution<TResult>> ExecuteAsync<TResult>(
        string operationName,
        string key,
        Func<CancellationToken, ValueTask<TResult>> handler,
        Func<TResult, ReadOnlyMemory<byte>> serializeResult,
        Func<ReadOnlyMemory<byte>, TResult> deserializeResult,
        IdempotencyPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(serializeResult);
        ArgumentNullException.ThrowIfNull(deserializeResult);

        policy ??= IdempotencyPolicy.Default;
        policy.Validate(); // ITM-216：倒挂策略在 Processor 入口快速失败
        using var activity = PalActivitySource.StartIdempotencyExecute(operationName, key);
        var now = _timeProvider.GetUtcNow();
        var existing = await _store.GetAsync(operationName, key, now, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !CanStartNewExecution(existing, now))
            return SetActivityResult(activity, GetExistingResult(existing, deserializeResult));

        var record = await _store.TryStartAsync(operationName, key, now, policy, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            existing = await _store.GetAsync(operationName, key, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return SetActivityResult(activity, existing is null
                ? new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Skipped, default)
                : GetExistingResult(existing, deserializeResult));
        }

        try
        {
            var result = await handler(cancellationToken).ConfigureAwait(false);
            // P2 修复（八轮评审）：副作用已发生后状态标记尽力持久化，不被请求级取消
            // （对齐下方 MarkFailedAsync 的 None——取消丢失完成标记会让重放重复执行副作用）。
            try
            {
                await _store.MarkCompletedAsync(record, serializeResult(result), _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception markEx) when (markEx is not OperationCanceledException)
            {
                // ITM-191 修复（三十轮）：handler 成功但标记失败（DB 故障）——副作用已发生，
                // 不得按通用失败重新标记 Failed 再抛（那会把"已执行"降级为"可重试失败"，
                // 重试时重放副作用）。记区分性错误日志后按 Executed 返回（at-least-once
                // 语义下状态待确认；对齐 InboxProcessor ITM-180 的管线孪生修复）。
                System.Diagnostics.Activity.Current?.AddEvent(new(
                    "idempotency.completed-pending-confirmation",
                    tags: new ActivityTagsCollection { ["error"] = markEx.Message }));
                return SetActivityResult(activity,
                    new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Executed, result));
            }
            return SetActivityResult(activity, new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Executed, result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ITM-175 修复：截断后再入库（对齐 Inbox/Outbox 管线孪生）
            var failureReason = ex.Message.Length <= MaxFailureReasonLength
                ? ex.Message
                : ex.Message[..MaxFailureReasonLength];
            try
            {
                await _store.MarkFailedAsync(record, failureReason, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception markEx)
            {
                // ITM-191 修复（镜像 InboxProcessor ITM-092）：MarkFailedAsync 自身失败
                // （DB 故障）不得掩盖主异常——挂 Data 后仍以主异常优先向上传播。
                ex.Data["MarkFailedError"] = markEx.Message;
            }
            activity?.SetTag("pal.idempotency.result", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PalMetrics.IdempotencyFailed.Add(1);
            throw;
        }
    }

    private static bool CanStartNewExecution(IdempotencyRecord record, DateTimeOffset now)
        => record.Status == IdempotencyRecordStatus.Failed
            || (record.Status == IdempotencyRecordStatus.Processing && record.LockedUntil <= now);

    private static IdempotencyExecution<TResult> SetActivityResult<TResult>(
        System.Diagnostics.Activity? activity,
        IdempotencyExecution<TResult> execution)
    {
        activity?.SetTag("pal.idempotency.result", execution.Status switch
        {
            IdempotencyExecutionStatus.Executed => "executed",
            IdempotencyExecutionStatus.Cached => "cached",
            IdempotencyExecutionStatus.Skipped => "skipped",
            _ => "unknown"
        });
        RecordMetric(execution.Status);

        return execution;
    }

    private static void RecordMetric(IdempotencyExecutionStatus status)
    {
        switch (status)
        {
            case IdempotencyExecutionStatus.Executed:
                PalMetrics.IdempotencyExecuted.Add(1);
                break;

            case IdempotencyExecutionStatus.Cached:
                PalMetrics.IdempotencyCached.Add(1);
                break;

            case IdempotencyExecutionStatus.Skipped:
                PalMetrics.IdempotencySkipped.Add(1);
                break;
        }
    }

    private static IdempotencyExecution<TResult> GetExistingResult<TResult>(
        IdempotencyRecord record,
        Func<ReadOnlyMemory<byte>, TResult> deserializeResult)
    {
        if (record.Status == IdempotencyRecordStatus.Completed && record.ResponsePayload is not null)
        {
            return new IdempotencyExecution<TResult>(
                IdempotencyExecutionStatus.Cached,
                deserializeResult(record.ResponsePayload.Value));
        }

        return new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Skipped, default);
    }
}
