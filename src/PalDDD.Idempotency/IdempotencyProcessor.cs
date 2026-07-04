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
        using var activity = PalActivitySource.StartIdempotencyExecute(operationName, key);
        var now = _timeProvider.GetUtcNow();
        var existing = await _store.GetAsync(operationName, key, now, cancellationToken);
        if (existing is not null && !CanStartNewExecution(existing, now))
            return SetActivityResult(activity, GetExistingResult(existing, deserializeResult));

        var record = await _store.TryStartAsync(operationName, key, now, policy, cancellationToken);
        if (record is null)
        {
            existing = await _store.GetAsync(operationName, key, _timeProvider.GetUtcNow(), cancellationToken);
            return SetActivityResult(activity, existing is null
                ? new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Skipped, default)
                : GetExistingResult(existing, deserializeResult));
        }

        try
        {
            var result = await handler(cancellationToken);
            await _store.MarkCompletedAsync(record, serializeResult(result), _timeProvider.GetUtcNow(), cancellationToken);
            return SetActivityResult(activity, new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Executed, result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _store.MarkFailedAsync(record, ex.Message, _timeProvider.GetUtcNow(), cancellationToken);
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
