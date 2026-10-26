// ─────────────────────────────────────────────────────────────
// 📽️ ProjectionProcessor — Checkpoint 幂等投影处理
// ─────────────────────────────────────────────────────────────
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Projections;

// ─────────────────────────────────────────────────────────────
// 投影处理器 — 逐事件处理并记录检查点
// ─────────────────────────────────────────────────────────────

[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "投影处理器需在重新抛出前持久化任意用户投影失败信息，需捕获 Exception 基类。")]
public sealed class ProjectionProcessor<TMessage>
{
    // ITM-167 修复：失败原因入库截断（Core.FailureReason.Normalize，2000 上限——与
    // InboxProcessor/OutboxBatchProcessor 同口径）——checkpoint.error
    // 列上限 2048，超长 ex.Message 会让 MarkFailedAsync 的持久化本身失败，掩盖原始投影失败。

    private readonly IProjectionHandler<TMessage> _handler;

    /// <summary>投影显示名称 — handler 契约成员的透传（Rebuilder 名称一致性守卫的比对源）。</summary>
    public string ProjectionName => _handler.ProjectionName;
    private readonly IProjectionCheckpointStore _checkpointStore;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _processingTimeout;
    // v25 P3 行为族 B7：pending-confirmation 信号补日志通道（原仅 Activity 事件，无 Trace
    // listener 时静默）；可选参数默认 null 向后兼容，DI 注册处无需改动
    private readonly IPalLogger<ProjectionProcessor<TMessage>>? _logger;

    private int _checkpointBatchSize = 1000;

    /// <summary>
    /// 检查点批量大小：默认 1000（2026-10-22 性能轮由 100 提升）表示回放按 1000 条一组提交
    /// （在线逐消息路径不受影响）；设为 1 恢复逐事件检查点。N&gt;1 时由
    /// <see cref="ProjectionRebuilder{TMessage}"/> 按 N 条一组调用 <see cref="ProcessBatchAsync"/>，
    /// 组内只在组首/组末做一次租约/完成写。
    /// <para>
    /// <b>为什么提默认值</b>：回放吞吐上限 = 批宽 ÷（每批往返数 × RTT）。每批固定 4 次
    /// 检查点往返，批宽 100 时 10ms RTT 下每事件要付约 600μs 的往返延迟，把 handler 成本
    /// 完全淹没；批宽 1000 后同一 RTT 下摊到约 60μs，回放吞吐提升一个数量级。
    /// </para>
    /// <para>
    /// 崩溃后最多重放 N 条（因此 N 也是 at-least-once 的重复窗口；投影 handler 必须幂等，
    /// 既有契约不变）。组内处理时长由租约心跳（1/3 租约周期续期）兜底，不受批宽影响。
    /// </para>
    /// </summary>
    public int CheckpointBatchSize
    {
        get => _checkpointBatchSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _checkpointBatchSize = value;
        }
    }

    /// <summary>投影租约时长（R12 C-31：供 Rebuilder 校验冲刷间隔上界）。</summary>
    internal TimeSpan ProcessingTimeout => _processingTimeout;

    public ProjectionProcessor(
        IProjectionHandler<TMessage> handler,
        IProjectionCheckpointStore checkpointStore,
        TimeProvider? timeProvider = null,
        TimeSpan processingTimeout = default,
        IPalLogger<ProjectionProcessor<TMessage>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        // ITM-166 修复：checkpoint 租约时长（processingTimeout）负值集中校验——负值使
        // LeaseUntil = startedAt + timeout < startedAt（租约即刻过期，僵尸抢占语义失效）。
        // Projection 无独立 Options 类，构造函数是进程内配置入口（Options 层等价物）
        // （对齐 DapperProjectionCheckpointStore.TryStartAsync 同款非负约束）。
        // v27 P3 勘正：原注释"允许 TimeSpan.Zero：租约即刻过期"与实现矛盾——
        // default(TimeSpan) == Zero，下方 `processingTimeout == default ? 5min : ...`
        // 把显式 Zero 替换为 5 分钟默认，"即刻过期"语义当前不可达；如需支持须改用
        // nullable 参数（TimeSpan?，破坏性变更留 v4.0）。仅勘正注释，不改行为。
        if (processingTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processingTimeout), "processingTimeout must not be negative.");

        _handler = handler;
        _checkpointStore = checkpointStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _processingTimeout = processingTimeout == default ? TimeSpan.FromMinutes(5) : processingTimeout;
        _logger = logger;
    }

    public async ValueTask<bool> ProcessAsync(
        TMessage message,
        ProjectionContext context,
        CancellationToken ct = default)
    {
        // v33 P3 补落 v31 承诺的入口 null 校验（仅 message 半边成立）：引用类型消息
        // 实例化可为 null，原实现直通 ProjectAsync，失败点远离入口。不校验 context——
        // ProjectionContext 是 readonly record struct（值类型不可能为 null，除非调用方
        // 传 Nullable<T>，而那在非 nullable 形参处过不了 NRT 警告，本项目 warnaserror
        // 编译不过），ThrowIfNull(context) 是死代码且触发 CA2264（v31 的"入口 null
        // 校验"承诺里 context 半边本就无法成立）。
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        var checkpoint = await _checkpointStore.TryStartAsync(
            _handler.ProjectionName,
            context.SourceName,
            context.Position,
            _timeProvider.GetUtcNow(),
            _processingTimeout,
            ct).ConfigureAwait(false);

        if (checkpoint is null)
            return false;

        // R12 C-02：handler 执行期间心跳续租，避免慢 handler 超过 processingTimeout 后
        // 被另一 worker 重租并并发投影。
        var heartbeat = StartLeaseHeartbeat(checkpoint);
        try
        {
            await _handler.ProjectAsync(message, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopLeaseHeartbeatAsync(heartbeat).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await StopLeaseHeartbeatAsync(heartbeat).ConfigureAwait(false);
            // ITM-092 修复：MarkFailedAsync 本身失败不得掩盖主异常——内层捕获挂 Data 后仍以主异常优先。
            try
            {
                // ITM-167 修复：ex.Message 截断再入库（FailureReason.Normalize，2000 上限）
                var failureReason = PalDDD.Core.FailureReason.Normalize(ex.Message);
                await _checkpointStore.MarkFailedAsync(checkpoint, failureReason, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception markEx)
            {
                ex.Data["MarkFailedError"] = markEx.Message;
            }
            throw;
        }

        await StopLeaseHeartbeatAsync(heartbeat).ConfigureAwait(false);

        // ITM-211 修复：handler 已成功——MarkCompletedAsync 失败不得进入 MarkFailedAsync
        // （那会把"已成功投影"降级为"可重试失败"→同一位置重放副作用）。
        // 镜像 ITM-191（IdempotencyProcessor）/ ITM-180（InboxProcessor）的管线孪生修复。
        try
        {
            await _checkpointStore.MarkCompletedAsync(checkpoint, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception markEx)
        {
            // 副作用已发生，按 at-least-once 语义返回成功；区分性事件供运维介入。
            // v25 P3 行为族 B7：补日志通道——原仅 Activity.Current?.AddEvent，无 Trace
            // listener 时该信号完全静默；可选 logger 补 Warning（默认 null 不启用）。
            // ITM-653（v66 镜像 InboxProcessor）：移除原 `when (markEx is not
            // OperationCanceledException)` 过滤，对齐 ITM-092 口径——MarkCompletedAsync 以
            // CancellationToken.None 调用，其抛 OCE 属存储异常形态（而非请求级取消传播），
            // 原过滤让 OCE 逃逸给调用方，而投影 handler 实际已成功——调用方按取消处理
            // 触发重试重放路径；捕获后统一按 completed-pending-confirmation 处理（与上方
            // MarkFailedAsync 内层 catch 的不过滤口径对称）。
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "projection.completed-pending-confirmation",
                tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = markEx.Message }));
            _logger?.Warning(
                $"Projection '{_handler.ProjectionName}' handler succeeded but checkpoint completion could not be persisted (pending confirmation); error: {markEx.Message}");
        }
        return true;
    }

    /// <summary>
    /// 批量处理一组事件（M2）：组首位置在处理器执行<b>之前</b>取得租约并作为续读锚点，
    /// 组末位置记录组完成；组内事件交给 handler。handler 实现
    /// <see cref="IProjectionBatchHandler{TMessage}"/> 时整组一次调用 <c>ProjectBatchAsync</c>
    ///（可批量写），否则逐条 <c>ProjectAsync</c>。
    /// </summary>
    /// <remarks>
    /// 2026-10-21 评审 F7：原实现在组末 TryStart 之后、处理失败时才补写组首 Failed——
    /// 组首写入失败时只剩组末 Failed，续读从组末之后开始，组内前 N-1 条永久跳过。
    /// 现改为「先锚点、后处理」：组首行（Processing/Failed）在任何事件处理前就存在，
    /// 因此处理器崩溃或组首标记失败时续读位置最小也为组首；代价是每组多一次
    /// TryStart + MarkCompleted（默认 100 条一组，约 2% 检查点调用增量）。
    /// <para>
    /// 返回 0 表示该组已被其他 worker 租用，调用方应停止本轮回放并在下次调用重试
    ///（不得跳过该组）。崩溃后最多重放一整组（<paramref name="batch"/>.Count 条）。
    /// </para>
    /// </remarks>
    public async ValueTask<int> ProcessBatchAsync(
        IReadOnlyList<(TMessage Message, ProjectionContext Context)> batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return 0;
        foreach (var (message, _) in batch)
        {
            if (message is null)
                throw new ArgumentNullException(nameof(batch));
        }

        var first = batch[0];
        var last = batch[^1];
        var isSinglePosition = string.Equals(first.Context.Position, last.Context.Position, StringComparison.Ordinal);

        // F7 修复：组首锚点在组末租约与 handler 执行之前取得。顺序不可交换——
        // 先取组末会创建「推进续读位置」的行；若随后组首获取失败，组内前面的事件从未处理，
        // 续读却会从组末继续（原缺陷的窄窗口）。组首先取失败时没有任何行被创建，
        // 续读位置保持在上一个已完成组，安全。
        ProjectionCheckpoint? anchor = null;
        if (!isSinglePosition)
        {
            anchor = await _checkpointStore.TryStartAsync(
                _handler.ProjectionName,
                first.Context.SourceName,
                first.Context.Position,
                _timeProvider.GetUtcNow(),
                _processingTimeout,
                ct).ConfigureAwait(false);

            if (anchor is null)
                return 0; // 组首已被其他 worker 持有/完成：本组不处理，下一轮重试
        }

        ProjectionCheckpoint? checkpoint;
        try
        {
            checkpoint = await _checkpointStore.TryStartAsync(
                _handler.ProjectionName,
                last.Context.SourceName,
                last.Context.Position,
                _timeProvider.GetUtcNow(),
                _processingTimeout,
                ct).ConfigureAwait(false);
        }
        catch (Exception leaseEx) when (leaseEx is not OperationCanceledException)
        {
            // 尚未处理任何事件：锚点标 Failed 把续读固定在组首（不标则滞留 Processing，
            // 租约到期后同样从组首重试），再抛原始异常。
            if (anchor is not null)
            {
                await TryMarkFailedAsync(
                    anchor,
                    PalDDD.Core.FailureReason.Normalize(leaseEx.Message),
                    "MarkFirstError",
                    leaseEx).ConfigureAwait(false);
            }
            throw;
        }

        if (checkpoint is null)
        {
            // 组末已被其他 worker 持有/完成：本组不处理；锚点标 Failed 让下一轮从组首重试。
            if (anchor is not null)
            {
                await TryMarkFailedAsync(
                    anchor,
                    "Projection batch end position is held by another worker; batch was not processed.",
                    "MarkFirstError",
                    primary: null).ConfigureAwait(false);
            }
            return 0;
        }

        // R12 C-02：整组 handler 执行期间续租组首/组末租约；慢 handler 不再被重租并发投影。
        var heartbeat = StartLeaseHeartbeat(anchor, checkpoint);
        try
        {
            if (_handler is IProjectionBatchHandler<TMessage> batchHandler)
            {
                await batchHandler.ProjectBatchAsync(batch, ct).ConfigureAwait(false);
            }
            else
            {
                for (var i = 0; i < batch.Count; i++)
                    await _handler.ProjectAsync(batch[i].Message, batch[i].Context, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await StopLeaseHeartbeatAsync(heartbeat).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await StopLeaseHeartbeatAsync(heartbeat).ConfigureAwait(false);
            var failureReason = PalDDD.Core.FailureReason.Normalize(ex.Message);
            // 组首锚点先标 Failed（它是续读位置的唯一保证），组末 Failed 仅作诊断。
            if (anchor is not null)
                await TryMarkFailedAsync(anchor, failureReason, "MarkFirstError", ex).ConfigureAwait(false);
            await TryMarkFailedAsync(checkpoint, failureReason, "MarkFailedError", ex).ConfigureAwait(false);
            throw;
        }

        await StopLeaseHeartbeatAsync(heartbeat).ConfigureAwait(false);

        // 成功：组首与组末都标 Completed。组首完成写失败时保持 Processing → 续读回到组首
        // （重放整组，at-least-once）；组末完成写失败时最多重放最后一条。
        if (anchor is not null)
        {
            try
            {
                await _checkpointStore.MarkCompletedAsync(
                    anchor, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception markEx)
            {
                System.Diagnostics.Activity.Current?.AddEvent(new(
                    "projection.anchor-completion-pending-confirmation",
                    tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = markEx.Message }));
                _logger?.Warning(
                    $"Projection '{_handler.ProjectionName}' group anchor completion could not be persisted; the group may be replayed (at-least-once); error: {markEx.Message}");
            }
        }

        try
        {
            await _checkpointStore.MarkCompletedAsync(
                checkpoint, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception markEx)
        {
            // 与逐事件路径同语义：handler 已成功，检查点未确认按 at-least-once 返回成功
            System.Diagnostics.Activity.Current?.AddEvent(new(
                "projection.completed-pending-confirmation",
                tags: new System.Diagnostics.ActivityTagsCollection { ["error"] = markEx.Message }));
            _logger?.Warning(
                $"Projection '{_handler.ProjectionName}' batch handler succeeded but checkpoint completion could not be persisted (pending confirmation); error: {markEx.Message}");
        }
        return batch.Count;
    }

    /// <summary>失败标记 best-effort：标记失败不得替换主异常，挂 Data 或日志供诊断。</summary>
    private async ValueTask TryMarkFailedAsync(
        ProjectionCheckpoint checkpoint,
        string failureReason,
        string dataKey,
        Exception? primary)
    {
        try
        {
            await _checkpointStore.MarkFailedAsync(
                checkpoint, failureReason, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception markEx)
        {
            if (primary is not null)
                primary.Data[dataKey] = markEx.Message;
            else
                _logger?.Warning(
                    $"Projection '{_handler.ProjectionName}' checkpoint failure mark could not be persisted; error: {markEx.Message}");
        }
    }

    /// <summary>R12 C-02：handler 期间的租约心跳（store 未实现续期能力时返回 null）。</summary>
    private LeaseHeartbeat? StartLeaseHeartbeat(params ProjectionCheckpoint?[] checkpoints)
    {
        if (_checkpointStore is not IProjectionLeaseRenewalStore renewal)
            return null;

        var distinct = checkpoints
            .OfType<ProjectionCheckpoint>()
            .DistinctBy(static checkpoint => (checkpoint.ProjectionName, checkpoint.SourceName, checkpoint.Position))
            .ToArray();
        if (distinct.Length == 0)
            return null;

        var cts = new CancellationTokenSource();
        var task = RunLeaseHeartbeatAsync(renewal, distinct, cts.Token);
        return new LeaseHeartbeat(cts, task);
    }

    private async Task RunLeaseHeartbeatAsync(
        IProjectionLeaseRenewalStore store,
        ProjectionCheckpoint[] checkpoints,
        CancellationToken ct)
    {
        // 租约的 1/3 作为心跳周期：任一时刻至少还有 2/3 租约余量，容忍一次调度抖动。
        var intervalTicks = Math.Max(_processingTimeout.Ticks / 3, TimeSpan.TicksPerMillisecond * 50);
        using var timer = new PeriodicTimer(TimeSpan.FromTicks(intervalTicks), _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                foreach (var checkpoint in checkpoints)
                {
                    var renewed = await store.RenewLeaseAsync(
                        checkpoint,
                        _timeProvider.GetUtcNow(),
                        _processingTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!renewed)
                    {
                        _logger?.Warning(
                            $"Projection '{_handler.ProjectionName}' lease renewal failed for position {checkpoint.Position}; "
                            + "another worker may take over and project concurrently.");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止（handler 完成/取消）
        }
    }

    private async Task StopLeaseHeartbeatAsync(LeaseHeartbeat? heartbeat)
    {
        if (heartbeat is null)
            return;

        await heartbeat.Cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await heartbeat.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            // R13 F13：心跳故障（连接闪断/超时）不得覆盖 handler 的结果——handler 已成功
            // 时旧实现会把整组判失败、检查点不标 Completed，调用方重放整组（重复副作用）。
            // 续租失败只降低租约保障，租约仍由 LeaseDuration 兜底过期。
            _logger?.Warning(
                $"Projection '{_handler.ProjectionName}' lease heartbeat failed; handler result is authoritative: {ex.Message}");
        }
        finally
        {
            heartbeat.Cts.Dispose();
        }
    }

    private sealed record LeaseHeartbeat(CancellationTokenSource Cts, Task Task);
}
