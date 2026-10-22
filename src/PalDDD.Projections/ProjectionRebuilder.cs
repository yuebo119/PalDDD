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
    private TimeSpan _checkpointFlushInterval = TimeSpan.FromSeconds(1);
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>
    /// 批量检查点的时间上界：即使未攒满 <see cref="ProjectionProcessor{TMessage}.CheckpointBatchSize"/>，
    /// 距本组首项满本间隔也提交一次。默认 1 秒，必须大于零且小于投影租约时长。
    /// </summary>
    public TimeSpan CheckpointFlushInterval
    {
        get => _checkpointFlushInterval;
        set
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), "CheckpointFlushInterval must be greater than zero.");
            _checkpointFlushInterval = value;
        }
    }

    /// <summary>回放计时源；测试可注入 FakeTimeProvider 驱动时间界冲刷。</summary>
    public TimeProvider TimeProvider
    {
        get => _timeProvider;
        set => _timeProvider = value ?? throw new ArgumentNullException(nameof(value));
    }

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

            var (rebuilt, _) = await ReplayCoreAsync(activity, ct).ConfigureAwait(false);
            return rebuilt;
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
        => (await ReplayDetailedAsync(ct).ConfigureAwait(false)).Processed;

    /// <summary>
    /// 增量回放并返回是否因租约冲突提前停止。
    /// </summary>
    /// <remarks>
    /// R14：原 <see cref="ReplayAsync"/> 只返回处理条数，调用方无法区分「已追平」与
    /// 「组首/组末被其他 worker 持有而提前结束」——后者需要立即重试，前者代表完成。
    /// 本方法在相同语义上额外返回 <see cref="ProjectionReplayResult.StoppedForLease"/>；
    /// 旧方法行为逐字不变。
    /// </remarks>
    public async ValueTask<ProjectionReplayResult> ReplayDetailedAsync(CancellationToken ct = default)
    {
        using var activity = PalActivitySource.StartProjectionRebuild(_projectionName, _sourceName);

        try
        {
            var (processed, stoppedForLease) = await ReplayCoreAsync(activity, ct).ConfigureAwait(false);
            return new ProjectionReplayResult(processed, stoppedForLease);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PalMetrics.ProjectionFailed.Add(1);
            throw;
        }
    }

    /// <summary>
    /// R11 F1：批量处理前裁掉已完成的首尾边界位置。返回 -1 表示整组均已完成（跳过），
    /// 0 表示组首/组末被其他 worker 租用（调用方应停止本轮），&gt;0 为实际处理条数。
    /// <para>
    /// 为什么需要边界裁剪：组首/组末的 <c>TryStartAsync</c> 对 Completed 行返回 null，
    /// 原实现把 null 一律当租用冲突。全量路径（非续读源）重放到已完成前缀时整轮空转；
    /// 组首 Failed 洞 + 组末 Completed 时洞永远无法重放（组末抢占失败 → 组首标 Failed）。
    /// 续读位置能处理数值位置的前缀，但非数值位置与「洞后已完成」形态仍需逐位置判定。
    /// </para>
    /// </summary>
    private async ValueTask<int> ProcessBatchWithEdgeTrimAsync(
        List<(TMessage Message, ProjectionContext Context)> batch,
        CancellationToken ct)
    {
        while (batch.Count > 0)
        {
            var first = batch[0].Context;
            var checkpoint = await _checkpointStore.GetAsync(
                _projectionName, first.SourceName, first.Position, ct).ConfigureAwait(false);
            if (checkpoint?.Status != ProjectionCheckpointStatus.Completed)
                break;
            batch.RemoveAt(0);
        }

        while (batch.Count > 0)
        {
            var last = batch[^1].Context;
            var checkpoint = await _checkpointStore.GetAsync(
                _projectionName, last.SourceName, last.Position, ct).ConfigureAwait(false);
            if (checkpoint?.Status != ProjectionCheckpointStatus.Completed)
                break;
            batch.RemoveAt(batch.Count - 1);
        }

        if (batch.Count == 0)
            return -1;

        return await _processor.ProcessBatchAsync(batch, ct).ConfigureAwait(false);
    }

    /// <summary>R11 F1：位置为数值且低于续读水位的事件直接丢弃（非续读源的已完成前缀）。</summary>
    private static async IAsyncEnumerable<ReplayEvent<TMessage>> ReadAfterPositionAsync(
        IAsyncEnumerable<ReplayEvent<TMessage>> source,
        long fromPosition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var replayEvent in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (!long.TryParse(replayEvent.Position, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var position)
                || position >= fromPosition)
                yield return replayEvent;
        }
    }

    private async ValueTask<(int Processed, bool StoppedForLease)> ReplayCoreAsync(
        System.Diagnostics.Activity? activity,
        CancellationToken ct)
    {
        var processed = 0;
        var stoppedForLease = false;
        var completed = false;
        try
        {
            // R12 C-31：批模式的组内时间界必须落在租约内。间隔 >= processingTimeout 时
            // 组首租约会在提交前过期，另一 worker 可重租同组并并发投影（重复副作用）。
            if (_processor.CheckpointBatchSize > 1
                && _checkpointFlushInterval >= _processor.ProcessingTimeout)
            {
                throw new InvalidOperationException(
                    $"CheckpointFlushInterval ({_checkpointFlushInterval}) must be less than the projection "
                    + $"processing timeout ({_processor.ProcessingTimeout}); otherwise the group lease can expire "
                    + "before the batch checkpoint is committed.");
            }
            // R6/P0-3（ADR-028，2026-10-14 性能实施轮）：增量回放高水位续读——store 与
            // 回放源双双支持续读能力时，取检查点续读位置（最小未完成洞位 / max+1 / null），
            // 从该位置下推读取：已完成前缀零重读、零逐事件 checkpoint 探查（原形态每次
            // 增量回放 O(全流长度) 全量物化）。任一侧不支持或续读位置不可解析 → 全量路径
            //（RebuildAsync 先 Reset 使续读查询返回 null，天然走全量——零特判）。
            //
            // R11 F1 修复：续读位置只依赖 checkpoint store，不依赖回放源能力。原实现要求
            // store 与 source 双双可续读，非续读源（接口明示支持的回落形态）直接走全量，
            // 批模式下组首遇到已完成前缀会返回 null，被误判为租用冲突并终止整轮。
            // 现在非续读源也按续读位置过滤事件（位置为数值时），前端已完成前缀零处理。
            long? resumeFrom = null;
            if (_checkpointStore is IResumableProjectionCheckpointStore resumableStore)
            {
                var resume = await resumableStore.GetResumePositionAsync(_projectionName, _sourceName, ct)
                    .ConfigureAwait(false);
                if (resume is not null
                    && long.TryParse(resume, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var fromPosition))
                    resumeFrom = fromPosition;
            }

            IAsyncEnumerable<ReplayEvent<TMessage>> stream;
            if (_replaySource is IResumableEventReplaySource<TMessage> resumableSource
                && resumeFrom is { } resumableFrom)
            {
                stream = resumableSource.ReadAsync(_sourceName, resumableFrom, ct);
            }
            else if (resumeFrom is { } skipBefore)
            {
                stream = ReadAfterPositionAsync(_replaySource.ReadAsync(_sourceName, ct), skipBefore, ct);
            }
            else
            {
                stream = _replaySource.ReadAsync(_sourceName, ct);
            }

            // M2（2026-10-19）：CheckpointBatchSize > 1 时按组处理；组首在处理器执行前
            // 取租约作为续读锚点（F7，2026-10-21），组末记录完成，崩溃后最多重放一组。
            // 返回 0 表示该组已被其他 worker 租用，停止本轮并在下次 Replay 重试（不得跳过该组）。
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
                var batchStarted = _timeProvider.GetTimestamp();
                await foreach (var replayEvent in stream.ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    if (batch.Count == 0)
                        batchStarted = _timeProvider.GetTimestamp();
                    batch.Add((
                        replayEvent.Message,
                        new ProjectionContext(
                            replayEvent.SourceName,
                            replayEvent.Position,
                            replayEvent.OccurredAt,
                            replayEvent.Audit)));

                    // 数量界或时间界任一到达即提交：慢回放不会攒满批而让租约过期，
                    // 快回放也不会每秒只提交一次。
                    if (batch.Count < batchSize
                        && _timeProvider.GetElapsedTime(batchStarted) < _checkpointFlushInterval)
                        continue;

                    var advanced = await ProcessBatchWithEdgeTrimAsync(batch, ct).ConfigureAwait(false);
                    if (advanced < 0)
                    {
                        // 整组均已完成（边界裁剪后为空）：跳过该组，继续下一组。
                        batch.Clear();
                        continue;
                    }
                    if (advanced == 0)
                    {
                        stoppedForLease = true;
                        break;
                    }
                    checked { processed += advanced; }
                    batch.Clear();
                }

                if (!stoppedForLease && batch.Count > 0)
                {
                    var advanced = await ProcessBatchWithEdgeTrimAsync(batch, ct).ConfigureAwait(false);
                    if (advanced > 0)
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
        return (processed, stoppedForLease);
    }
}
