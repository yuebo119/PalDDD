// ─────────────────────────────────────────────────────────────
// 🔧 DefaultSagaManager — ISagaManager 的默认实现
// ─────────────────────────────────────────────────────────────
//
// 💡 职责：提供 ISagaManager 的最小可行实现。
//   ｜ 用户可注入自定义实现替换默认行为。
//   ｜ 中断恢复：暂存决策信息于内存字典，供 ISagaManager.ResumeAsync 使用。
//   ｜ 子 Saga 执行：直接调用 childSaga.ProcessEventAsync。
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>
/// ISagaManager 默认实现——提供中断恢复和子 Saga 执行的最小可行实现。
/// </summary>
/// <remarks>
/// 中断恢复使用内存字典暂存状态。生产环境应替换为持久化实现。
/// 子 Saga 执行直接委托给子编排器的 ProcessEventAsync。
/// </remarks>
public sealed class DefaultSagaManager : ISagaManager
{
    /// <summary>暂存的中断状态 — keyed by sagaId</summary>
    private readonly ConcurrentDictionary<PalUlid, InterruptedSagaEntry> _interrupted = [];

    /// <inheritdoc/>
    public ValueTask ResumeAsync<TDecision>(
        PalUlid sagaId, TDecision decision, CancellationToken ct)
        where TDecision : notnull
    {
        if (_interrupted.TryGetValue(sagaId, out var entry))
        {
            entry.SetDecision(decision);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SagaState>> GetInterruptedSagasAsync(CancellationToken ct)
    {
        // 默认实现无法持久化查询——返回空列表。
        // 生产环境应替换为数据库查询实现。
        return new([]);
    }

    /// <inheritdoc/>
    public async ValueTask<TChildState> ExecuteChildSagaAsync<TChildState>(
        Saga<TChildState> childSaga,
        TChildState childState,
        object triggerEvent,
        CancellationToken ct)
        where TChildState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(childSaga);
        ArgumentNullException.ThrowIfNull(childState);
        ArgumentNullException.ThrowIfNull(triggerEvent);

        return await childSaga.ProcessEventAsync(childState, triggerEvent, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode("Non-generic child saga dispatch relies on reflection to call ProcessEventAsync; child saga types must be preserved for AOT.")]
    [RequiresDynamicCode("Uses MakeGenericMethod/MakeGenericType which requires dynamic code generation for AOT compatibility.")]
    async ValueTask<SagaState> ISagaManager.ExecuteChildSagaNonGenericAsync(
        object childSaga, SagaState childState, object triggerEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(childSaga);
        ArgumentNullException.ThrowIfNull(childState);
        ArgumentNullException.ThrowIfNull(triggerEvent);

        var stateType = childState.GetType();
        var sagaType = childSaga.GetType();

        // Resolve ProcessEventAsync(TState, object, CancellationToken) on the saga type
        // Use open generic typeof(Saga<>) to avoid CS0310 constraint violation
        var method = sagaType.GetMethod(
            "ProcessEventAsync",
            BindingFlags.Public | BindingFlags.Instance);
        var genericMethod = method!.MakeGenericMethod(stateType);

        // Invoke: returns boxed ValueTask<TState>
        var boxedValueTask = genericMethod.Invoke(childSaga, [childState, triggerEvent, ct])!;

        // Use AsTask() to get Task<TState>, then await and upcast
        var valueTaskType = typeof(ValueTask<>).MakeGenericType(stateType);
        var asTaskMethod = valueTaskType.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance)!;
        var task = (Task)asTaskMethod.Invoke(boxedValueTask, null)!;
        await task.ConfigureAwait(false);

        // Extract Result property (Task<T>.Result)
        var taskType = task.GetType();
        var resultProp = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)!;
        return (SagaState)resultProp.GetValue(task)!;
    }

    /// <summary>注册一个中断的 Saga——由 InterruptStep 执行时调用。</summary>
    internal void RegisterInterrupted(PalUlid sagaId, string reason, Type decisionType)
    {
        _interrupted[sagaId] = new InterruptedSagaEntry(sagaId, reason, decisionType);
    }

    /// <summary>尝试获取中断条目的决策——由 Saga 恢复流程使用。</summary>
    internal bool TryGetDecision<TDecision>(PalUlid sagaId, out TDecision? decision)
        where TDecision : notnull
    {
        if (_interrupted.TryGetValue(sagaId, out var entry)
            && entry.Decision is TDecision d)
        {
            decision = d;
            _interrupted.TryRemove(sagaId, out _);
            return true;
        }
        decision = default;
        return false;
    }

    private sealed class InterruptedSagaEntry
    {
        public PalUlid SagaId { get; }
        public string Reason { get; }
        public Type DecisionType { get; }
        public object? Decision { get; private set; }

        public InterruptedSagaEntry(PalUlid sagaId, string reason, Type decisionType)
        {
            SagaId = sagaId;
            Reason = reason;
            DecisionType = decisionType;
        }

        public void SetDecision(object decision) => Decision = decision;
    }
}
