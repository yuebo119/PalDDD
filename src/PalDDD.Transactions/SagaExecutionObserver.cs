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
/// <remarks>
/// <para>
/// ⚠️ <b>接线说明（P3·八轮评审）</b>：Observer 通过 <see cref="AsyncLocal{T}"/> 沿异步调用链向下传播，
/// <b>不是 DI 组件</b>——框架不会自动创建它，须在 Saga 执行入口手动包裹
/// （<see cref="Saga{TState}.ProcessEventAsync"/> 读取 <see cref="Current"/>）。
/// 因此本框架不提供 <c>TryAddSingleton</c> 便捷注册——注册一个 <see cref="ISagaEventSink"/>
/// 到 DI 并不会让事件自动流向它。标准用法：
/// <code>
///   // 1. 实现 Sink（示例：转发到日志/指标）
///   sealed class MetricsSink(TimeProvider clock) : ISagaEventSink
///   {
///       public ValueTask EmitAsync&lt;T&gt;(T sagaEvent, CancellationToken ct)
///       {
///           _logger.Information($"saga event: {sagaEvent}"); // 转发到注入的 IPalLogger
///           return ValueTask.CompletedTask;
///       }
///   }
///
///   // 2. 在 Saga 执行作用域入口创建 Observer（AsyncLocal 自动传播到所有异步子步骤）
///   using var _ = new SagaExecutionObserver(new MetricsSink(TimeProvider.System));
///   await saga.ProcessEventAsync(state, evt, ct);
/// </code>
/// </para>
/// <para>
/// 兼容无 Sink 场景（构造参数为 null 时所有事件静默跳过）；
/// 事件类型为 readonly record struct（零分配、值语义）。
/// </para>
/// </remarks>
public sealed class SagaExecutionObserver : IDisposable
{
    private readonly ISagaEventSink? _sink;
    // P2 修复：嵌套 Observer 场景——保存外层实例，Dispose 时恢复而非清空
    // （此前内层 Dispose 把 _current 置 null，外层注册随之丢失）
    private readonly SagaExecutionObserver? _previous;
    private static readonly AsyncLocal<SagaExecutionObserver?> _current = new();

    /// <summary>当前异步上下文中的 Observer。</summary>
    public static SagaExecutionObserver? Current => _current.Value;

    /// <summary>
    /// 创建 Observer 并设为当前上下文的活动实例。
    /// </summary>
    public SagaExecutionObserver(ISagaEventSink? sink = null)
    {
        _sink = sink;
        _previous = _current.Value;
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
    /// <remarks>
    /// P3 修复（十七轮）：<see cref="SagaStepFailed.ErrorMessage"/> 递归下钻
    /// <see cref="Exception.InnerException"/> 取最内层真实消息——重试链的
    /// AggregateException、反射派发的 TargetInvocationException（如 DefaultSagaManager
    /// 的子 Saga 非泛型反射 Invoke）外层 Message 是 "One or more errors occurred"
    /// 之类聚合文案，观测端无从定位根因。
    /// </remarks>
    public async ValueTask OnStepFailed(PalUlid sagaId, string stepKey, Exception error, CancellationToken ct)
    {
        if (_sink is not null)
            await _sink.EmitAsync(new SagaStepFailed(sagaId, stepKey, GetRootMessage(error)), ct).ConfigureAwait(false);
    }

    /// <summary>取最内层异常的 Message（无 InnerException 时返回自身 Message）。</summary>
    private static string GetRootMessage(Exception error)
    {
        var current = error;
        while (current.InnerException is not null)
            current = current.InnerException;
        return current.Message;
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

    /// <summary>释放 Observer，恢复外层 Observer（如有）。</summary>
    public void Dispose() => _current.Value = _previous;
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
