// ─────────────────────────────────────────────────────────────
// 🔁 IdempotencyProcessor — (OperationName,Key) 幂等执行（结果缓存 + 租约）
// ─────────────────────────────────────────────────────────────
using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
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
    // 对齐 OutboxBatchProcessor/InboxProcessor 的 FailureReason.Normalize 收口（PD24 失败标记族，MaxLength=2000）。

    private readonly IIdempotencyStore _store;
    private readonly TimeProvider _timeProvider;
    // v25 P3 行为族 B7：pending-confirmation 信号补日志通道（原仅 Activity 事件，无 Trace
    // listener 时静默）；可选参数默认 null 向后兼容，DI 注册处无需改动
    private readonly IPalLogger<IdempotencyProcessor>? _logger;

    public IdempotencyProcessor(
        IIdempotencyStore store,
        TimeProvider? timeProvider = null,
        IPalLogger<IdempotencyProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
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

        // 阶段 1：执行 handler。失败路径标记 Failed 并传播（副作用未发生，可重试）。
        TResult result;
        // v66 P4 声明（OCE 无观测，对齐 InboxProcessor v17 补文口径）：handler 抛 OCE 时
        // 不标 Failed 直接逃逸——OCE 语义 = "不知道执行到哪一步"，标 Failed 可立即重试
        // = 立即重放可能已完成的副作用；记录残留 Processing 由 LockedUntil 租约到期后
        // 才可重入。代价：① 最长租约期的重试延迟；② 该路径无 SetStatus/无指标/无日志
        //（IdempotencyFailed 不计、Activity 无 result tag）——OCE 由调用方按取消语义
        // 观测（ct.IsCancellationRequested / 调用方自身日志），处理器侧不可见。
        try
        {
            result = await handler(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ITM-175 修复：截断后再入库（对齐 Inbox/Outbox 管线孪生）
            var failureReason = PalDDD.Core.FailureReason.Normalize(ex.Message);
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

        // 阶段 2：序列化 + 完成标记。副作用已发生，任何失败不得按"可重试失败"处理。
        // F1 修复（audit-probe 2026-08-23）：serializeResult 原作为 MarkCompletedAsync
        // 实参求值，其异常落入下方 ITM-191 catch 被静默按 Executed 吞掉——序列化失败是
        // 持久性缺陷（每次必然再抛），记录残留 Processing 导致租约过期后 handler 重放、
        // 副作用无限重复执行且调用方无感知。先序列化再进 try：异常传播给调用方（副作用
        // 已发生、结果未落库）；不标记 Failed（ITM-191 同禁——Failed 可立即重试 = 立即
        // 重放副作用；保持 Processing = 租约窗口内挡重试，at-least-once 状态待确认）。
        ReadOnlyMemory<byte> payload;
        try
        {
            payload = serializeResult(result);
        }
        catch (Exception serializeEx) when (serializeEx is not OperationCanceledException)
        {
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "idempotency.serialize-result-failed",
                tags: new ActivityTagsCollection { ["error"] = serializeEx.Message }));
            // v39 P3：补 Activity Error 状态 + 失败指标（镜像同方法 handler 失败路径形态）——
            // 原仅 AddEvent，Activity 终态呈正常成功且 IdempotencyFailed 指标漏计，
            // 观测端（APM 告警/仪表盘按 SetStatus 与指标过滤）看不见该失败
            activity?.SetTag("pal.idempotency.result", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, serializeEx.Message);
            PalMetrics.IdempotencyFailed.Add(1);
            throw;
        }

        try
        {
            // P2 修复（八轮评审）：副作用已发生后状态标记尽力持久化，不被请求级取消
            // （对齐 MarkFailedAsync 的 None——取消丢失完成标记会让重放重复执行副作用）。
            await _store.MarkCompletedAsync(record, payload, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception markEx)
        {
            // ITM-191 修复（三十轮）：handler 成功但标记失败（DB 故障）——副作用已发生，
            // 不得按通用失败重新标记 Failed 再抛（那会把"已执行"降级为"可重试失败"，
            // 重试时重放副作用）。记区分性错误日志后按 Executed 返回（at-least-once
            // 语义下状态待确认；对齐 InboxProcessor ITM-180 的管线孪生修复）。
            // v25 P3 行为族 B7：补日志通道——原仅 Activity 事件，无 Trace listener 时该
            // 信号完全静默；可选 logger 补 Warning（默认 null 不启用，对称 ProjectionProcessor）。
            // ITM-653（v66 镜像 InboxProcessor）：移除原 `when (markEx is not
            // OperationCanceledException)` 过滤，对齐 ITM-092 口径——MarkCompletedAsync 以
            // CancellationToken.None 调用，其抛 OCE 属存储异常形态（而非请求级取消传播），
            // 原过滤让 OCE 逃逸给调用方，而 handler 实际已成功——调用方按取消处理触发
            // 重试重放路径；捕获后统一按 completed-pending-confirmation 处理（与上方
            // MarkFailedAsync 内层 catch 的不过滤口径对称）。
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "idempotency.completed-pending-confirmation",
                tags: new ActivityTagsCollection { ["error"] = markEx.Message }));
            _logger?.Warning(
                $"Idempotency operation '{operationName}' (key '{key}') handler succeeded but completion could not be persisted (pending confirmation); error: {markEx.Message}");
            return SetActivityResult(activity,
                new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Executed, result));
        }
        return SetActivityResult(activity, new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Executed, result));
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
            // 三十八轮 P3 修复：schema 漂移产生毒载荷时原样抛反序列化异常，该 key 在整个
            // 保留窗口内每次命中都抛——降级为 Skipped（与 Completed 无 payload 同款路径），
            // 调用方按既有"无缓存结果"分支处理；Activity 留痕供诊断。
            try
            {
                return new IdempotencyExecution<TResult>(
                    IdempotencyExecutionStatus.Cached,
                    deserializeResult(record.ResponsePayload.Value));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Activity.Current?.AddEvent(new(
                    "idempotency.cached-payload-deserialize-failed",
                    tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = ex.Message }));
                return new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Skipped, default);
            }
        }

        return new IdempotencyExecution<TResult>(IdempotencyExecutionStatus.Skipped, default);
    }
}
