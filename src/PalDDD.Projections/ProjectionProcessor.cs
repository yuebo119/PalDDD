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
            // P2 修复（八轮评审）：副作用已发生后 checkpoint 标记尽力持久化，不被请求级取消
            // （对齐下方 MarkFailedAsync 的 None——取消丢失完成标记会导致同一位置重放）。
            await _checkpointStore.MarkCompletedAsync(checkpoint, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ITM-092 修复：MarkFailedAsync 本身失败不得掩盖主异常——内层捕获挂 Data 后仍以主异常优先。
            // 验证轮返工：内层 catch 不加 OCE 过滤（同 InboxProcessor——None 令牌下 OCE 属异常形态）。
            try
            {
                // ITM-167 修复：ex.Message 截断到 MaxFailureReasonLength 再入库——
                // 长异常消息（含大 payload 的序列化错误等）超出 checkpoint.error 列上限
                // 会让失败标记持久化本身失败，掩盖原始投影失败（对齐 InboxProcessor 十七轮
                // 与 OutboxBatchProcessor 二十一轮姊妹修复）。
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
    }
}
