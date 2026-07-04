// ─────────────────────────────────────────────────────────────
// 👁️ SagaExecutionObserver — Saga 执行观察器
// ─────────────────────────────────────────────────────────────
//
// 💡 什么是 ExecutionObserver？
//   ｜ 贯穿 Saga 执行全生命周期的可观测性钩子。
//   ｜ 通过 ISagaEventSink 将步骤开始/完成/失败、补偿、状态变更事件发射到外部。
//   ｜
// 💡 设计决策：
//   ｜ AsyncLocal 单例模式：每个异步上下文一个 Observer，无需 DI 传播。
//   ｜ 兼容无 Sink 场景（_sink 为 null 时静默跳过）。
//   ｜ 事件类型为 readonly record struct（零分配、值语义）。
// ─────────────────────────────────────────────────────────────

using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>Saga 执行观察器——在 Saga 生命周期各节点发射事件。</summary>
public sealed class SagaExecutionObserver : IDisposable
{
    private readonly ISagaEventSink? _sink;
    private static readonly AsyncLocal<SagaExecutionObserver?> _current = new();

    /// <summary>当前异步上下文中的 Observer。</summary>
    public static SagaExecutionObserver? Current => _current.Value;

    /// <summary>
    /// 创建 Observer 并设为当前上下文的活动实例。
    /// </summary>
    public SagaExecutionObserver(ISagaEventSink? sink = null)
    {
        _sink = sink;
        _current.Value = this;
    }

    /// <summary>步骤开始执行。</summary>
    public async ValueTask OnStepStarted(PalUlid sagaId, string stepKey, CancellationToken ct)
    {
        if (_sink is not null)
            await _sink.EmitAsync(new SagaStepStarted(sagaId, stepKey), ct).ConfigureAwait(false);
    }

    /// <summary>步骤执行成功。</summary>
    public async ValueTask OnStepCompleted(PalUlid sagaId, string stepKey, TimeSpan duration, CancellationToken ct)
    {
        if (_sink is not null)
            await _sink.EmitAsync(new SagaStepCompleted(sagaId, stepKey, duration), ct).ConfigureAwait(false);
    }

    /// <summary>步骤执行失败。</summary>
    public async ValueTask OnStepFailed(PalUlid sagaId, string stepKey, Exception error, CancellationToken ct)
    {
        if (_sink is not null)
            await _sink.EmitAsync(new SagaStepFailed(sagaId, stepKey, error.Message), ct).ConfigureAwait(false);
    }

    /// <summary>补偿开始。</summary>
    public async ValueTask OnCompensationStarted(PalUlid sagaId, string stepKey, CancellationToken ct)
    {
        if (_sink is not null)
            await _sink.EmitAsync(new SagaCompensationStarted(sagaId, stepKey), ct).ConfigureAwait(false);
    }

    /// <summary>Saga 状态变更。</summary>
    public async ValueTask OnStatusChanged(PalUlid sagaId, SagaStatus oldStatus, SagaStatus newStatus, CancellationToken ct)
    {
        if (_sink is not null)
            await _sink.EmitAsync(new SagaStatusChanged(sagaId, oldStatus, newStatus), ct).ConfigureAwait(false);
    }

    /// <summary>释放 Observer，从当前上下文移除。</summary>
    public void Dispose() => _current.Value = null;
}

// ─────────────────────────────────────────────────────────────
// Saga 事件类型（readonly record struct — 零分配）
// ─────────────────────────────────────────────────────────────

/// <summary>步骤开始执行事件。</summary>
public readonly record struct SagaStepStarted(PalUlid SagaId, string StepKey);

/// <summary>步骤执行成功事件。</summary>
public readonly record struct SagaStepCompleted(PalUlid SagaId, string StepKey, TimeSpan Duration);

/// <summary>步骤执行失败事件。</summary>
public readonly record struct SagaStepFailed(PalUlid SagaId, string StepKey, string ErrorMessage);

/// <summary>补偿开始事件。</summary>
public readonly record struct SagaCompensationStarted(PalUlid SagaId, string StepKey);

/// <summary>Saga 状态变更事件。</summary>
public readonly record struct SagaStatusChanged(PalUlid SagaId, SagaStatus OldStatus, SagaStatus NewStatus);
