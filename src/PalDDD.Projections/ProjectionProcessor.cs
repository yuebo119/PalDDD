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
            ct);

        if (checkpoint is null)
            return false;

        try
        {
            await _handler.ProjectAsync(message, context, ct);
            await _checkpointStore.MarkCompletedAsync(checkpoint, _timeProvider.GetUtcNow(), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _checkpointStore.MarkFailedAsync(checkpoint, ex.Message, _timeProvider.GetUtcNow(), ct);
            throw;
        }
    }
}
