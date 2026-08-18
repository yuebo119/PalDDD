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
        TimeSpan? pollInterval = null)
        // P3 修复（十七轮）：options 空守卫前移到 base 实参——原实参 pollInterval 为 null 时
        // 先在 options.CurrentValue 解引用 NRE，构造体内的 ThrowIfNull 永不可达（对齐 OutboxProcessor）
        : base(scopeFactory, pollInterval ?? (options ?? throw new ArgumentNullException(nameof(options))).CurrentValue.PollInterval)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    protected override async ValueTask ExecuteTickAsync(CancellationToken ct)
    {
        using var scope = ScopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<SagaTimeoutProcessor<TState>>();
        await processor.CheckTimeoutsAsync(ct);
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
    }

    /// <summary>检查所有活跃 Saga 状态是否超时。</summary>
    public async ValueTask CheckTimeoutsAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var options = _options.CurrentValue;

        var activeSagas = await _store.LeaseActiveSagasAsync(
            options.LeaseOwner,
            options.LeaseDuration,
            options.TimeoutScanBatchSize,
            ct);

        foreach (var sagaState in activeSagas)
        {
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
                        _logger.Warning($"Saga {sagaState.SagaId} timed out at state {sagaState.CurrentState}, step {step.Name}. Compensating...");
                    }

                    try
                    {
                        await _orchestrator.CompensateAsync(sagaState, ct);
                        sagaState.Status = SagaStatus.Compensated;
                        sagaState.CompletedAt = now;
                        sagaState.CurrentState = "Compensated";
                        compensationSucceeded = true;
                    }
                    catch (OperationCanceledException)
                    {
                        // 外部取消：仍释放租约避免该 Saga 对其他实例不可见（ITM-010），
                        // 然后重新抛出让上层感知关停信号。租约释放用独立 CT（不响应本次取消）。
                        // SaveChanges 失败不掩盖原始 OCE —— 关停时 DB 连接可能已断。
                        sagaState.LeasedBy = null;
                        sagaState.LeasedUntil = null;
                        try
                        {
                            await _store.SaveChangesAsync(sagaState, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception releaseEx) when (releaseEx is not OperationCanceledException)
                        {
                            _logger.Error(releaseEx, $"Saga {sagaState.SagaId} lease release failed during cancellation; lease will expire by LeaseDuration");
                        }
                        throw;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Error(ex, $"Saga {sagaState.SagaId} compensation failed");
                        // ITM-200 修复（三十一轮）：截断后再入库——Error 列 HasMaxLength(2048)
                        // （SagaStateDbContext），补偿异常（AggregateException/FanOut 聚合消息）超长
                        // 会让 SaveChangesAsync 抛 DbUpdateException → 被外层 catch 吞 → Saga
                        // 停留 Processing 无限重租重补偿且 CompensationFailed 永不落库。
                        // 对齐 Inbox/Outbox 的 MaxFailureReasonLength=2000 截断族（PD24）。
                        sagaState.Error = ex.Message.Length <= 2000
                            ? ex.Message
                            : ex.Message[..2000];
                        sagaState.CurrentState = "CompensationFailed";
                        sagaState.Status = SagaStatus.CompensationFailed;
                        sagaState.ErrorAt = now;
                    }

                    sagaState.LeasedBy = null;
                    sagaState.LeasedUntil = null;
                    // P2 修复（取消路径对称）：租约释放是终态写入，不响应取消（与上方 OCE 路径
                    // 的 CancellationToken.None 对齐）——此前正常路径用 ct，OCE 传播时租约滞留
                    var compensatedSaved = await _store.SaveChangesAsync(sagaState, CancellationToken.None);
                    if (compensatedSaved == 0)
                    {
                        // P3 修复（八轮）：0 行 = 乐观锁冲突（他实例已写同一 Saga）——
                        // 补偿结果未落库，本实例的内存快照作废，记 Warning 供排查双写
                        _logger.Warning($"Saga {sagaState.SagaId} compensated state save affected 0 rows (optimistic concurrency conflict); another instance may have written");
                    }
                    else if (compensationSucceeded)
                    {
                        PalMetrics.SagaCompensated.Add(1);
                    }
                }
                else
                {
                    // P0-FIX-3: 非超时 Saga 也必须释放租约 —— 否则超时检测平均延迟 = LeaseDuration（2 分钟）
                    // 而非 PollInterval（30 秒），多实例下该 Saga 对其他实例不可见
                    sagaState.LeasedBy = null;
                    sagaState.LeasedUntil = null;
                    // P2 修复（取消路径对称）：同上——终态写入不响应取消
                    var releaseSaved = await _store.SaveChangesAsync(sagaState, CancellationToken.None);
                    if (releaseSaved == 0)
                    {
                        // P3 修复（八轮）：0 行 = 乐观锁冲突——租约释放未落库（LeaseDuration 到期兜底），
                        // 记 Warning 与补偿路径对齐
                        _logger.Warning($"Saga {sagaState.SagaId} lease release save affected 0 rows (optimistic concurrency conflict); lease will expire by LeaseDuration");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw; // 关停信号仍向上传播
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 租约靠 LeaseDuration 兜底过期；记录后继续处理下一条
                _logger.Error(ex, $"Saga {sagaState.SagaId} timeout check failed; lease will expire by LeaseDuration");
            }
        }
    }
}
