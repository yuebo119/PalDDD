// 🔄 ProjectionRebuilder — 全量重建/增量回放
// ─────────────────────────────────────────────────────────────

using PalDDD.Core.Diagnostics;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 投影重建器 — 全量重建/增量回放
// ─────────────────────────────────────────────────────────────

[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "投影重建器需在重新抛出前于 Activity 上标记任意回放与投影失败，需捕获 Exception 基类。")]
public sealed class ProjectionRebuilder<TMessage>
{
    private readonly string _projectionName;
    private readonly string _sourceName;
    private readonly IEventReplaySource<TMessage> _replaySource;
    private readonly IProjectionCheckpointStore _checkpointStore;
    private readonly ProjectionProcessor<TMessage> _processor;

    public ProjectionRebuilder(
        string projectionName,
        string sourceName,
        IEventReplaySource<TMessage> replaySource,
        IProjectionCheckpointStore checkpointStore,
        ProjectionProcessor<TMessage> processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(replaySource);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(processor);

        _projectionName = projectionName;
        _sourceName = sourceName;
        _replaySource = replaySource;
        _checkpointStore = checkpointStore;
        _processor = processor;
    }

    public async ValueTask<int> RebuildAsync(CancellationToken ct = default)
    {
        using var activity = PalActivitySource.StartProjectionRebuild(_projectionName, _sourceName);

        try
        {
            await _checkpointStore.ResetAsync(_projectionName, _sourceName, ct);

            return await ReplayCoreAsync(activity, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PalMetrics.ProjectionFailed.Add(1);
            throw;
        }
    }

    /// <summary>
    /// 增量回放 — 不重置检查点。<c>TryStartAsync</c> 会跳过已完成的检查点位置；<br/>
    /// 仅处理缺失或失败的检查点。
    /// </summary>
    /// <remarks>
    /// 这是安全且可重试的模式：若回放在中途失败，<br/>
    /// 已存在的检查点保持完好，可以重试调用而不会丢失数据。<br/>
    /// 如需从零开始完整重建，请使用 <see cref="RebuildAsync"/>（会先重置检查点）。
    /// </remarks>
    public async ValueTask<int> ReplayAsync(CancellationToken ct = default)
    {
        using var activity = PalActivitySource.StartProjectionRebuild(_projectionName, _sourceName);

        try
        {
            return await ReplayCoreAsync(activity, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PalMetrics.ProjectionFailed.Add(1);
            throw;
        }
    }

    private async ValueTask<int> ReplayCoreAsync(
        System.Diagnostics.Activity? activity,
        CancellationToken ct)
    {
        var processed = 0;
        await foreach (var replayEvent in _replaySource.ReadAsync(_sourceName, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var context = new ProjectionContext(
                replayEvent.SourceName,
                replayEvent.Position,
                replayEvent.OccurredAt,
                replayEvent.Audit);

            if (await _processor.ProcessAsync(replayEvent.Message, context, ct))
                checked { processed++; }
        }

        activity?.SetTag("pal.projection.replayed", processed);
        PalMetrics.ProjectionReplayed.Add(processed);
        return processed;
    }
}
