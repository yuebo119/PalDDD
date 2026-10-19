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

        // v31 P3 修复：名称一致性守卫——projectionName 与 processor 内部 handler 的
        // ProjectionName 错位时（配置错误），ResetAsync 清 A 流检查点而 TryStartAsync 用
        // B 名，旧 Completed 检查点残留使回放被静默跳过（重建丢事件）
        if (!string.Equals(projectionName, processor.ProjectionName, StringComparison.Ordinal))
            throw new ArgumentException(
                $"projectionName '{projectionName}' 与 processor 的投影名 '{processor.ProjectionName}' 不一致——检查点重置与回放将作用于不同流。",
                nameof(projectionName));

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
            await _checkpointStore.ResetAsync(_projectionName, _sourceName, ct).ConfigureAwait(false);

            return await ReplayCoreAsync(activity, ct).ConfigureAwait(false);
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
            return await ReplayCoreAsync(activity, ct).ConfigureAwait(false);
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
        var completed = false;
        try
        {
            // R6/P0-3（ADR-028，2026-10-14 性能实施轮）：增量回放高水位续读——store 与
            // 回放源双双支持续读能力时，取检查点续读位置（最小未完成洞位 / max+1 / null），
            // 从该位置下推读取：已完成前缀零重读、零逐事件 checkpoint 探查（原形态每次
            // 增量回放 O(全流长度) 全量物化）。任一侧不支持或续读位置不可解析 → 全量路径
            //（RebuildAsync 先 Reset 使续读查询返回 null，天然走全量——零特判）。
            IAsyncEnumerable<ReplayEvent<TMessage>> stream;
            if (_checkpointStore is IResumableProjectionCheckpointStore resumableStore
                && _replaySource is IResumableEventReplaySource<TMessage> resumableSource)
            {
                var resume = await resumableStore.GetResumePositionAsync(_projectionName, _sourceName, ct)
                    .ConfigureAwait(false);
                stream = resume is not null
                    && long.TryParse(resume, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var fromPosition)
                    ? resumableSource.ReadAsync(_sourceName, fromPosition, ct)
                    : resumableSource.ReadAsync(_sourceName, ct);
            }
            else
            {
                stream = _replaySource.ReadAsync(_sourceName, ct);
            }

            // M2（2026-10-19）：CheckpointBatchSize > 1 时按组处理；组内只在最后位置
            // 做一次检查点写，崩溃后最多重放一组。返回 0 表示该组已被其他 worker 租用，
            // 停止本轮并在下次 Replay 重试（不得跳过该组）。
            var batchSize = _processor.CheckpointBatchSize;
            if (batchSize <= 1)
            {
                await foreach (var replayEvent in stream.ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();

                    var context = new ProjectionContext(
                        replayEvent.SourceName,
                        replayEvent.Position,
                        replayEvent.OccurredAt,
                        replayEvent.Audit);

                    if (await _processor.ProcessAsync(replayEvent.Message, context, ct).ConfigureAwait(false))
                        checked { processed++; }
                }
            }
            else
            {
                var batch = new List<(TMessage Message, ProjectionContext Context)>(batchSize);
                var stopForLease = false;
                await foreach (var replayEvent in stream.ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    batch.Add((
                        replayEvent.Message,
                        new ProjectionContext(
                            replayEvent.SourceName,
                            replayEvent.Position,
                            replayEvent.OccurredAt,
                            replayEvent.Audit)));

                    if (batch.Count < batchSize)
                        continue;

                    var advanced = await _processor.ProcessBatchAsync(batch, ct).ConfigureAwait(false);
                    if (advanced == 0)
                    {
                        stopForLease = true;
                        break;
                    }
                    checked { processed += advanced; }
                    batch.Clear();
                }

                if (!stopForLease && batch.Count > 0)
                {
                    var advanced = await _processor.ProcessBatchAsync(batch, ct).ConfigureAwait(false);
                    checked { processed += advanced; }
                }
            }

            completed = true;
        }
        finally
        {
            // v62 P3：计数入 finally（对齐二十一轮 metrics-finally 三姊妹——中途失败/取消时
            // 已成功投影数仍进指标，运维可回答"失败前推进到哪里"）
            if (!completed)
            {
                activity?.SetTag("pal.projection.replayed", processed);
                PalMetrics.ProjectionReplayed.Add(processed);
            }
        }
        activity?.SetTag("pal.projection.replayed", processed);
        PalMetrics.ProjectionReplayed.Add(processed);
        return processed;
    }
}
