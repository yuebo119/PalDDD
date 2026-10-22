// ─────────────────────────────────────────────────────────────
// ⏱ SagaProcessor — 定时扫描活跃 Saga + 超时检测 + 补偿
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalDDD.Core.Diagnostics;
using PalDDD.Core.Logging;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// Saga 超时后台扫描
// ─────────────────────────────────────────────────────────────

/// <summary>Saga 超时后台轮询服务。</summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "BackgroundService 需记录超时循环失败并继续处理后续 Saga，需捕获 Exception 基类。")]
public sealed class SagaProcessor<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
        | DynamicallyAccessedMemberTypes.NonPublicConstructors
        | DynamicallyAccessedMemberTypes.PublicFields
        | DynamicallyAccessedMemberTypes.NonPublicFields
        | DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.NonPublicProperties
        | DynamicallyAccessedMemberTypes.Interfaces)]
TState> : PeriodicBackgroundProcessor
    where TState : SagaState, new()
{
    private readonly IPalLogger<SagaProcessor<TState>> _logger;

    public SagaProcessor(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SagaProcessorOptions> options,
        IPalLogger<SagaProcessor<TState>> logger,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null)
        // P3 修复（十七轮）：options 空守卫前移到 base 实参——原实参 pollInterval 为 null 时
        // 先在 options.CurrentValue 解引用 NRE，构造体内的 ThrowIfNull 永不可达（对齐 OutboxProcessor）
        // v29 P3：?? 短路勘正——pollInterval 非 null 时 (options ?? throw) 不求值，base 构造
        // 成功创建了 PeriodicTimer 后构造体 ThrowIfNull 才抛（构造中断 Dispose 不可达）→ timer
        // 泄漏。改为三元显式判 options：null 时在任何路径上都先于 base 调用求值抛出，
        // 不创建 timer（对齐 OutboxProcessor v29 同款）
        // v30 P3（logger 轴扩展）：构造体 ThrowIfNull(logger) 同理在 base 成功后抛——timer
        // 已创建、Dispose 不可达 → 泄漏。logger 守卫合并进 base 第一实参（v29 options 轴
        // 三元形态扩展）：实参按出现顺序先于 base 体求值，logger/options 任一为 null 时
        // 均在 PeriodicTimer 创建前抛出；scopeFactory 轴无此问题（基类构造体在创建 timer
        // 之前先 ThrowIfNull(scopeFactory)）（两文件对称）
        : base(
               logger is null
                   ? throw new ArgumentNullException(nameof(logger))
                   : scopeFactory,
               options is null
                   ? throw new ArgumentNullException(nameof(options))
                   : pollInterval ?? options.CurrentValue.PollInterval,
               timeProvider)
    {
        _logger = logger;
    }

    protected override async ValueTask ExecuteTickAsync(CancellationToken ct)
    {
        using var scope = ScopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<SagaTimeoutProcessor<TState>>();
        await processor.CheckTimeoutsAsync(ct).ConfigureAwait(false);
    }

    protected override void OnTickFailed(Exception ex)
        => _logger.Error(ex, "Saga timeout check failed");
}

/// <summary>Scoped Saga 超时处理器。</summary>
[SuppressMessage("Design", "CA1031:Do not catch general exception types",
    Justification = "Saga 编排需记录任意补偿失败并继续，需捕获 Exception 基类。")]
public sealed class SagaTimeoutProcessor<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
        | DynamicallyAccessedMemberTypes.NonPublicConstructors
        | DynamicallyAccessedMemberTypes.PublicFields
        | DynamicallyAccessedMemberTypes.NonPublicFields
        | DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.NonPublicProperties
        | DynamicallyAccessedMemberTypes.Interfaces)]
TState>
    where TState : SagaState, new()
{
    private readonly ISagaStateStore<TState> _store;
    private readonly Saga<TState> _orchestrator;
    private readonly IPalLogger<SagaTimeoutProcessor<TState>> _logger;
    private readonly IOptionsMonitor<SagaProcessorOptions> _options;
    private readonly TimeProvider _timeProvider;
    private static int s_cursorWarningLogged;

    public SagaTimeoutProcessor(
        ISagaStateStore<TState> store,
        Saga<TState> orchestrator,
        IPalLogger<SagaTimeoutProcessor<TState>> logger,
        IOptionsMonitor<SagaProcessorOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _orchestrator = orchestrator;
        _logger = logger;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // 自定义 store 未实现轮转游标时，释放会清空 leased_until，扫描始终取前 N 条——
        // 活跃 Saga 超过批大小时第 N+1 条起永远不被扫描。只在进程内首次命中时告警。
        if (store is not ISagaScanCursorStore<TState>
            && Interlocked.Exchange(ref s_cursorWarningLogged, 1) == 0)
        {
            _logger.Warning(
                $"Saga store {store.GetType().FullName} does not implement ISagaScanCursorStore<{typeof(TState).Name}>; "
                + "released leases clear leased_until and the scanner may starve rows beyond LeaseBatchSize. "
                + "Implement the interface (or use a built-in store) to enable scan rotation.");
        }
    }

    /// <summary>检查所有活跃 Saga 状态是否超时。</summary>
    public async ValueTask CheckTimeoutsAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var options = _options.CurrentValue;

        // v70 P3：租约获取失败观测（对齐 OutboxBatchProcessor v29——此前 DB 故障仅 OnTickFailed
        // 记日志，长故障期监控面板呈"零失败"假象）
        System.Collections.Generic.IReadOnlyList<TState> activeSagas;
        try
        {
            // M1（2026-10-19）：EnableDueTimeFilter=true 且 store 支持时只租到期行；
            // 否则走旧全量路径，并在下方惰性回填 NextCheckAt。
            activeSagas = options.EnableDueTimeFilter && _store is IDueTimeSagaStore<TState> dueStore
                ? await dueStore.LeaseDueSagasAsync(
                    options.LeaseOwner,
                    options.LeaseDuration,
                    options.TimeoutScanBatchSize,
                    now,
                    ct).ConfigureAwait(false)
                : await _store.LeaseActiveSagasAsync(
                    options.LeaseOwner,
                    options.LeaseDuration,
                    options.TimeoutScanBatchSize,
                    ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // v71：改独立 SagaScanFailed（v70 复用 SagaCompensationFailed 与其
            // "补偿动作失败"语义声明冲突——SagaCompensation.cs:133）
            PalMetrics.SagaScanFailed.Add(1);
            throw;
        }

        // EPSL 地毯审计 2026-10-11 B-1：非超时 Saga 的租约释放收集到环后单语句批量执行
        //（常态路径原为逐 saga SaveChangesAsync——每条一往返，Dapper 栈叠加 B-3 修复前的
        // 存在性全行预查，EF 栈叠加全跟踪实体 DetectChanges）；存储未实现
        // ISagaLeaseBatchStore 时保持逐条路径逐字不变（自定义存储向后兼容）。
        List<TState>? pendingRelease = null;
        // 2026-10-20 性能轮：回填/到期推进/补偿终态收集到环后批量提交，替代逐 Saga
        // SaveChangesAsync（EF 栈逐行保存还会重复扫描整个 ChangeTracker）。
        List<TState>? pendingBackfill = null;
        List<TState>? pendingNextCheck = null;
        List<(TState State, bool CompensationSucceeded)>? pendingTerminal = null;

        foreach (var sagaState in activeSagas)
        {
            // M1 惰性回填（2026-10-21）：只有到期过滤真正生效时才需要——迁移前存量行
            // NextCheckAt 为 NULL，到期谓词把 NULL 视为到期（否则存量行永不扫描）。
            // 首次扫描按当前状态计算，无待检查超时写哨兵；收集到环后由批量保存冲刷。
            // 全量扫描（过滤关闭或 store 无到期能力）不需要该值，保持旧路径零额外写。
            if (options.EnableDueTimeFilter
                && _store is IDueTimeSagaStore<TState>
                && sagaState.NextCheckAt is null)
            {
                sagaState.NextCheckAt =
                    _orchestrator.ComputeNextCheckAt(sagaState) ?? SagaState.NoPendingTimeout;
                (pendingBackfill ??= []).Add(sagaState);
            }

            // P3 修复：单条 Saga 处理失败不中断剩余——否则后续 Saga 的租约需等
            // LeaseDuration 自然过期才对其他实例可见（与批处理整体失败同害）
            // P3 修复（十七轮）：try 块体整体补一层缩进（原块体与 try 关键字同列，
            // 纯格式修复，无行为变更）
            try
            {
                // P3 修复（八轮）：补偿指标移到持久化成功后再计——提前计数会在
                // 保存失败/0 行时重复累加（下一 tick 重做补偿再 +1）
                var compensationSucceeded = false;
                if (_orchestrator.IsTimedOut(sagaState, now, out var timedOutSteps))
                {
                    foreach (var step in timedOutSteps)
                    {
                        _logger.Warning(PalLogEventIds.SagaTimedOut,
                            "Saga {SagaId} timed out at state {State}, step {Step}. Compensating...",
                            sagaState.SagaId, sagaState.CurrentState, step.Name);
                    }

                    try
                    {
                        await _orchestrator.CompensateAsync(sagaState, ct).ConfigureAwait(false);
                        sagaState.Status = SagaStatus.Compensated;
                        sagaState.CompletedAt = now;
                        sagaState.CurrentState = SagaState.CompensatedStateName;
                        sagaState.NextCheckAt = null; // M1：终态不再参与超时扫描
                        compensationSucceeded = true;
                    }
                    catch (OperationCanceledException)
                    {
                        // 外部取消：仍释放租约避免该 Saga 对其他实例不可见（ITM-010），
                        // 然后重新抛出让上层感知关停信号。租约释放用独立 CT（不响应本次取消）。
                        // SaveChanges 失败不掩盖原始 OCE —— 关停时 DB 连接可能已断。
                        sagaState.LeasedBy = null;
                        // 扫描轮转：写回本 tick 时刻而不是 NULL，legacy 扫描按 leased_until
                        // NULLS FIRST 排序时该行退到未扫描行之后，避免同一批每 tick 重租。
                        sagaState.LeasedUntil = now;
                        try
                        {
                            var releaseSaved = await _store.SaveChangesAsync(sagaState, CancellationToken.None).ConfigureAwait(false);
                            if (releaseSaved == 0)
                            {
                                // v26 P3：OCE 关停路径的租约释放 0 行——乐观锁冲突（LeaseDuration 兜底）。
                                // 与下方补偿路径/非超时路径的 0 行 Warning 对齐（排障三处都看），
                                // 原实现忽略返回值使关停期冲突静默
                                _logger.Warning($"Saga {sagaState.SagaId} lease release save affected 0 rows during cancellation (optimistic concurrency conflict); lease will expire by LeaseDuration");
                            }
                        }
                        catch (Exception releaseEx) when (releaseEx is not OperationCanceledException)
                        {
                            _logger.Error(releaseEx, PalLogEventIds.SagaLeaseReleaseFailed,
                                "Saga {SagaId} lease release failed during cancellation; lease will expire by LeaseDuration",
                                sagaState.SagaId);
                        }
                        throw;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Error(ex, PalLogEventIds.SagaCompensationFailed,
                            "Saga {SagaId} compensation failed", sagaState.SagaId);
                        // ITM-200 修复（三十一轮）：截断后再入库——Error 列 HasMaxLength(2048)
                        // （SagaStateDbContext），补偿异常（AggregateException/FanOut 聚合消息）超长
                        // 会让 SaveChangesAsync 抛 DbUpdateException → 被外层 catch 吞 → Saga
                        // 停留 Processing 无限重租重补偿且 CompensationFailed 永不落库。
                        // 二轮评审 T6：改用共享 FailureReason.Normalize（截断 + F2 空白归一——
                        // 后者此前为 Saga 漏网姊妹，ex.Message 为 null 时旧内联版直接 NRE）。
                        sagaState.Error = Core.FailureReason.Normalize(ex.Message);
                        sagaState.CurrentState = SagaState.CompensationFailedStateName;
                        sagaState.Status = SagaStatus.CompensationFailed;
                        sagaState.ErrorAt = now;
                        sagaState.NextCheckAt = null; // M1：终态不再参与超时扫描
                    }

                    sagaState.LeasedBy = null;
                    sagaState.LeasedUntil = null;
                    // v26 P1 修复：补偿终态同步失效 Manager 中断条目——迟到决策走"无条目"
                    // IOE 可见失败。此前仅靠 DefaultSagaManager.ResumeAsync 的终态分支，
                    // 但条目闭包捕获中断时旧实例、补偿写的是 store successor（CloneForLease
                    // 后继），旧实例永远停留 AwaitingHumanDecision——终态分支不可达，迟到
                    // 决策会在已回滚 Saga 上执行副作用并假成功
                    if ((sagaState.Status == SagaStatus.Compensated || sagaState.Status == SagaStatus.CompensationFailed)
                        && _orchestrator.SagaManager is DefaultSagaManager defaultManager)
                    {
                        defaultManager.InvalidateInterrupted(sagaState.SagaId);
                    }
                    // P2 修复（取消路径对称）：租约释放是终态写入，不响应取消——收集到环后
                    // 由批量冲刷以 CancellationToken.None 保存（与上方 OCE 路径语义对齐）
                    (pendingTerminal ??= []).Add((sagaState, compensationSucceeded));
                }
                else
                {
                    // 到期过滤下，已到期但未超时的行（时钟偏差/步骤重入等）必须推进
                    // NextCheckAt，否则同一批行每 tick 重复被租，饿死后续到期 Saga。
                    if (options.EnableDueTimeFilter && sagaState.NextCheckAt is not null)
                    {
                        // 与 Saga.UpdateNextCheckAt 同口径：无待检查超时写哨兵而非 null，
                        // 避免下一 tick 又以「NULL = 存量行」被租一次。
                        var nextCheck =
                            _orchestrator.ComputeNextCheckAt(sagaState) ?? SagaState.NoPendingTimeout;
                        if (nextCheck != sagaState.NextCheckAt)
                        {
                            sagaState.NextCheckAt = nextCheck;
                            (pendingNextCheck ??= []).Add(sagaState);
                        }
                    }

                    // P0-FIX-3: 非超时 Saga 也必须释放租约 —— 否则超时检测平均延迟 = LeaseDuration（2 分钟）
                    // 而非 PollInterval（30 秒），多实例下该 Saga 对其他实例不可见
                    if (_store is ISagaLeaseBatchStore<TState>)
                    {
                        // B-1 批量路径：环后单语句释放（leased_by 守卫 + 换代在存储侧完成，
                        // 此处不变异内存对象——InMemory 栈由存储变异字典条目保持一致性）
                        (pendingRelease ??= new List<TState>(activeSagas.Count)).Add(sagaState);
                    }
                    else
                    {
                        sagaState.LeasedBy = null;
                        sagaState.LeasedUntil = now;
                        // P2 修复（取消路径对称）：同上——终态写入不响应取消
                        var releaseSaved = await _store.SaveChangesAsync(sagaState, CancellationToken.None).ConfigureAwait(false);
                        if (releaseSaved == 0)
                        {
                            // P3 修复（八轮）：0 行 = 乐观锁冲突——租约释放未落库（LeaseDuration 到期兜底），
                            // 记 Warning 与补偿路径对齐
                            _logger.Warning($"Saga {sagaState.SagaId} lease release save affected 0 rows (optimistic concurrency conflict); lease will expire by LeaseDuration");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // R11 F7：取消不得丢掉已收集的终态/租约——原实现直接 throw，循环后的
                // flush 不可达，已补偿 saga 停留 Active 且持租，LeaseDuration 后被重租
                // 并再次补偿。与正常收口共用同一冲刷定义（全部 CancellationToken.None）。
                await FlushTerminalAndReleaseAsync(
                    pendingTerminal, pendingNextCheck, pendingRelease, now, options.LeaseOwner)
                    .ConfigureAwait(false);
                throw; // 关停信号仍向上传播
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 租约靠 LeaseDuration 兜底过期；记录后继续处理下一条
                // R11 F24：使用已定义的 SagaTimeoutCheckFailed（1205）——原字符串重载
                // 让按 EventId 过滤的告警对单条检查失败零命中。
                _logger.Error(ex, PalLogEventIds.SagaTimeoutCheckFailed,
                    "Saga {SagaId} timeout check failed; lease will expire by LeaseDuration",
                    sagaState.SagaId);
            }
        }

        // 2026-10-20 性能轮收口：回填/补偿终态/到期推进各一次批量提交（无批量能力的
        // 自定义存储回落逐条，语义与旧路径一致）；随后才做租约批量释放。
        // R13 F3：回填与后置冲刷同用 CancellationToken.None——它是"终态写入前的收尾"，
        // 若用调用方 ct，OCE 会从 FlushBackfillAsync 逃逸并跳过补偿终态/租约释放
        // （下一 tick 重租同一 Saga 再次补偿）。
        if (pendingBackfill is not null)
            await FlushBackfillAsync(pendingBackfill, CancellationToken.None).ConfigureAwait(false);
        await FlushTerminalAndReleaseAsync(
            pendingTerminal, pendingNextCheck, pendingRelease, now, options.LeaseOwner)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// R11 F7：终态/到期推进/租约释放的统一收口（取消路径与正常路径共用）。
    /// 三者都以 <see cref="CancellationToken.None"/> 落库——它们是终态写入，不响应取消。
    /// </summary>
    private async ValueTask FlushTerminalAndReleaseAsync(
        List<(TState State, bool CompensationSucceeded)>? pendingTerminal,
        List<TState>? pendingNextCheck,
        List<TState>? pendingRelease,
        DateTimeOffset now,
        string owner)
    {
        if (pendingTerminal is not null)
            await FlushTerminalAsync(pendingTerminal).ConfigureAwait(false);
        if (pendingNextCheck is not null)
            await FlushNextCheckAsync(pendingNextCheck, owner).ConfigureAwait(false);
        if (pendingRelease is not null)
            await FlushReleaseAsync(pendingRelease, now, owner).ConfigureAwait(false);
    }

    private async ValueTask FlushReleaseAsync(List<TState> pendingRelease, DateTimeOffset now, string owner)
    {
        try
        {
            var batchStore = (ISagaLeaseBatchStore<TState>)_store;
            var released = _store is ISagaScanCursorStore<TState> cursorStore
                ? await cursorStore.ReleaseLeasesAsync(owner, pendingRelease, now, CancellationToken.None).ConfigureAwait(false)
                : await batchStore.ReleaseLeasesAsync(owner, pendingRelease, CancellationToken.None).ConfigureAwait(false);
            if (released < pendingRelease.Count)
            {
                // 冲突信号粒度由逐 saga 收粗为批计数（B-1 已裁决取舍，见 ISagaLeaseBatchStore remarks）
                _logger.Warning(PalLogEventIds.SagaLeaseBatchPartial,
                    "Saga lease batch release affected {Released}/{PendingCount} rows (optimistic concurrency conflicts); unreleased leases will expire by LeaseDuration",
                    released, pendingRelease.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, PalLogEventIds.SagaLeaseBatchReleaseFailed,
                "Saga lease batch release failed ({PendingCount} sagas); leases will expire by LeaseDuration",
                pendingRelease.Count);
        }
    }

    /// <summary>回填 NextCheckAt 的批量落库；非批量存储回落逐条路径（与旧行为一致）。</summary>
    /// <remarks>
    /// R13 F3：回填失败（含 store 自身抛出的 OCE，如 DB 超时映射）一律吞掉并记日志——
    /// 它是收尾步骤，失败只能让本 tick 的 NextCheckAt 留空（下一 tick 重算），绝不能让
    /// 异常跳过后续的补偿终态与租约释放冲刷。调用方传入 CancellationToken.None。
    /// </remarks>
    private async ValueTask FlushBackfillAsync(List<TState> states, CancellationToken ct)
    {
        if (_store is ISagaBatchStateStore<TState> batchStore)
        {
            try
            {
                var results = await batchStore.SaveManyAsync(states, ct).ConfigureAwait(false);
                for (var i = 0; i < results.Count; i++)
                {
                    if (results[i] == 0)
                        _logger.Warning($"Saga {states[i].SagaId} NextCheckAt backfill save affected 0 rows (optimistic concurrency conflict); will retry on next scan");
                }
            }
            catch (Exception ex)
            {
                // R13 F3：不过滤 OCE——store 以 CancellationToken.None 调用时抛 OCE 属
                // "DB 超时映射"，与其它失败同义；逃逸会跳过后置终态冲刷。
                _logger.Error(ex, $"Saga NextCheckAt backfill batch save failed ({states.Count} sagas); will retry on next scan");
            }
            return;
        }

        foreach (var state in states)
        {
            try
            {
                var saved = await _store.SaveChangesAsync(state, ct).ConfigureAwait(false);
                if (saved == 0)
                    _logger.Warning($"Saga {state.SagaId} NextCheckAt backfill save affected 0 rows (optimistic concurrency conflict); will retry on next scan");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Saga {state.SagaId} NextCheckAt backfill save failed; will retry on next scan");
            }
        }
    }

    /// <summary>补偿终态的批量落库；逐行结果决定告警与补偿指标。</summary>
    private async ValueTask FlushTerminalAsync(List<(TState State, bool CompensationSucceeded)> pending)
    {
        var states = new TState[pending.Count];
        for (var i = 0; i < pending.Count; i++)
            states[i] = pending[i].State;

        if (_store is ISagaBatchStateStore<TState> batchStore)
        {
            try
            {
                var results = await batchStore.SaveManyAsync(states, CancellationToken.None).ConfigureAwait(false);
                for (var i = 0; i < results.Count; i++)
                    LogTerminalResult(pending[i].State, pending[i].CompensationSucceeded, results[i]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, $"Saga compensated terminal batch save failed ({states.Length} sagas); leases will expire by LeaseDuration");
            }
            return;
        }

        foreach (var (state, compensationSucceeded) in pending)
        {
            try
            {
                var saved = await _store.SaveChangesAsync(state, CancellationToken.None).ConfigureAwait(false);
                LogTerminalResult(state, compensationSucceeded, saved);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, $"Saga {state.SagaId} compensated terminal save failed; lease will expire by LeaseDuration");
            }
        }
    }

    private void LogTerminalResult(TState sagaState, bool compensationSucceeded, int saved)
    {
        if (saved == 0)
        {
            // 评审 P1-4 增强（八轮 P3 基础上）：0 行 = 乐观锁冲突（他实例已写同一
            // Saga）——补偿副作用已在本实例执行，他实例可能再次补偿同一 Saga
            // （双重副作用风险）。框架无法回滚外部副作用，只能依赖补偿动作幂等
            // （契约见 SagaStep.CompensateAsync remarks）——本日志是重入发生时
            // 的常规信号（0 行路径）；SaveChangesAsync 抛异常路径落入批量 catch——
            // 同为重入风险但措辞通用，排障两处都看（v8 声明）。
            _logger.Warning($"Saga {sagaState.SagaId} compensated state save affected 0 rows (optimistic concurrency conflict); another instance may have compensated it too — double compensation detected, ensure SagaStep.CompensateAsync actions are idempotent");
        }
        else if (compensationSucceeded)
        {
            PalMetrics.SagaCompensated.Add(1);
        }
    }

    /// <summary>到期推进 NextCheckAt 的批量落库；无批量能力的存储回落逐条保存。</summary>
    private async ValueTask FlushNextCheckAsync(List<TState> states, string owner)
    {
        if (_store is ISagaBatchStateStore<TState> batchStore)
        {
            try
            {
                var affected = await batchStore.AdvanceNextCheckAtAsync(owner, states, CancellationToken.None).ConfigureAwait(false);
                if (affected < states.Count)
                    _logger.Warning($"Saga NextCheckAt batch advance affected {affected}/{states.Count} rows (optimistic concurrency conflicts); unadvanced rows will be re-leased next tick");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, $"Saga NextCheckAt batch advance failed ({states.Count} sagas); rows will be re-leased next tick");
            }
            return;
        }

        foreach (var state in states)
        {
            try
            {
                var saved = await _store.SaveChangesAsync(state, CancellationToken.None).ConfigureAwait(false);
                if (saved == 0)
                    _logger.Warning($"Saga {state.SagaId} NextCheckAt advance affected 0 rows (optimistic concurrency conflict); will be re-leased next tick");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, $"Saga {state.SagaId} NextCheckAt advance failed; will be re-leased next tick");
            }
        }
    }
}
