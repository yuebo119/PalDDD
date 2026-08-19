// ─────────────────────────────────────────────────────────────
// 📽️ ProjectionProcessor — Checkpoint 幂等投影处理
// ─────────────────────────────────────────────────────────────
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 投影处理器 — 逐事件处理并记录检查点
// ─────────────────────────────────────────────────────────────

[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "投影处理器需在重新抛出前持久化任意用户投影失败信息，需捕获 Exception 基类。")]
public sealed class ProjectionProcessor<TMessage>
{
    // ITM-167 修复：失败原因入库截断上限（对齐 InboxProcessor/OutboxBatchProcessor 的
    // MaxFailureReasonLength=2000）——checkpoint.error 列上限 2048，超长 ex.Message 会让
    // MarkFailedAsync 的持久化本身失败，掩盖原始投影失败。
    internal const int MaxFailureReasonLength = 2000;

    private readonly IProjectionHandler<TMessage> _handler;
    private readonly IProjectionCheckpointStore _checkpointStore;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _processingTimeout;

    public ProjectionProcessor(
        IProjectionHandler<TMessage> handler,
        IProjectionCheckpointStore checkpointStore,
        TimeProvider? timeProvider = null,
        TimeSpan processingTimeout = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        // ITM-166 修复：checkpoint 租约时长（processingTimeout）负值集中校验——负值使
        // LeaseUntil = startedAt + timeout < startedAt（租约即刻过期，僵尸抢占语义失效）。
        // Projection 无独立 Options 类，构造函数是进程内配置入口（Options 层等价物）；
        // 允许 TimeSpan.Zero：租约即刻过期是测试"超时接管可重入"的合法语义
        // （对齐 DapperProjectionCheckpointStore.TryStartAsync 同款非负约束）。
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");

        _handler = handler;
        _checkpointStore = checkpointStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _processingTimeout = processingTimeout == default ? TimeSpan.FromMinutes(5) : processingTimeout;
    }

    public async ValueTask<bool> ProcessAsync(
        TMessage message,
        ProjectionContext context,
        CancellationToken ct = default)
    {
        var checkpoint = await _checkpointStore.TryStartAsync(
            _handler.ProjectionName,
            context.SourceName,
            context.Position,
            _timeProvider.GetUtcNow(),
            _processingTimeout,
            ct).ConfigureAwait(false);

        if (checkpoint is null)
            return false;

        try
        {
            await _handler.ProjectAsync(message, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ITM-092 修复：MarkFailedAsync 本身失败不得掩盖主异常——内层捕获挂 Data 后仍以主异常优先。
            try
            {
                // ITM-167 修复：ex.Message 截断到 MaxFailureReasonLength 再入库
                var failureReason = ex.Message.Length <= MaxFailureReasonLength
                    ? ex.Message
                    : ex.Message[..MaxFailureReasonLength];
                await _checkpointStore.MarkFailedAsync(checkpoint, failureReason, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception markEx)
            {
                ex.Data["MarkFailedError"] = markEx.Message;
            }
            throw;
        }

        // ITM-211 修复：handler 已成功——MarkCompletedAsync 失败不得进入 MarkFailedAsync
        // （那会把"已成功投影"降级为"可重试失败"→同一位置重放副作用）。
        // 镜像 ITM-191（IdempotencyProcessor）/ ITM-180（InboxProcessor）的管线孪生修复。
        try
        {
            await _checkpointStore.MarkCompletedAsync(checkpoint, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception markEx) when (markEx is not OperationCanceledException)
        {
            // 副作用已发生，按 at-least-once 语义返回成功；区分性事件供运维介入。
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "projection.completed-pending-confirmation",
                tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = markEx.Message }));
        }
        return true;
    }
}
