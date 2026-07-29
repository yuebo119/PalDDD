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
        : base(scopeFactory, pollInterval ?? options.CurrentValue.PollInterval)
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
                    PalMetrics.SagaCompensated.Add(1);
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
                    sagaState.Error = ex.Message;
                    sagaState.CurrentState = "CompensationFailed";
                    sagaState.Status = SagaStatus.CompensationFailed;
                    sagaState.ErrorAt = now;
                }

                sagaState.LeasedBy = null;
                sagaState.LeasedUntil = null;
                await _store.SaveChangesAsync(sagaState, ct);
            }
            else
            {
                // P0-FIX-3: 非超时 Saga 也必须释放租约 —— 否则超时检测平均延迟 = LeaseDuration（2 分钟）
                // 而非 PollInterval（30 秒），多实例下该 Saga 对其他实例不可见
                sagaState.LeasedBy = null;
                sagaState.LeasedUntil = null;
                await _store.SaveChangesAsync(sagaState, ct);
            }
        }
    }
}
