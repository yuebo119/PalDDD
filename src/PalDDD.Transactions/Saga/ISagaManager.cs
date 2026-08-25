// ─────────────────────────────────────────────────────────────
// 🎛️ ISagaManager — Saga 管理器接口（恢复中断的 Saga + 子 Saga 执行）
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>
/// Saga 管理器——用于恢复中断的 Saga、查询待决策 Saga、以及执行子 Saga。
/// </summary>
public interface ISagaManager
{
    /// <summary>
    /// 恢复一个中断的 Saga——传入人工决策后继续执行。
    /// </summary>
    /// <typeparam name="TDecision">决策数据类型</typeparam>
    /// <param name="sagaId">Saga ID</param>
    /// <param name="decision">人工决策数据</param>
    /// <param name="ct">取消令牌</param>
    ValueTask ResumeAsync<TDecision>(PalUlid sagaId, TDecision decision, CancellationToken ct)
        where TDecision : notnull;

    /// <summary>
    /// 获取所有处于 <see cref="SagaStatus.AwaitingHumanDecision"/> 状态的 Saga。
    /// </summary>
    ValueTask<IReadOnlyList<SagaState>> GetInterruptedSagasAsync(CancellationToken ct);

    /// <summary>
    /// 执行子 Saga——将子 Saga 作为父 Saga 的一个步骤执行。
    /// </summary>
    /// <typeparam name="TChildState">子 Saga 状态类型</typeparam>
    /// <param name="childSaga">子 Saga 编排器实例</param>
    /// <param name="childState">子 Saga 初始状态</param>
    /// <param name="triggerEvent">触发子 Saga 的事件</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>执行后的子 Saga 状态</returns>
    ValueTask<TChildState> ExecuteChildSagaAsync<TChildState>(
        Saga<TChildState> childSaga,
        TChildState childState,
        object triggerEvent,
        CancellationToken ct)
        where TChildState : SagaState, new();

    /// <summary>
    /// 非泛型子 Saga 执行——用于 ChildSagaStep 从非泛型上下文调用。<br/>
    /// 默认实现在 <see cref="DefaultSagaManager"/> 中通过反射委托给泛型版本。
    /// </summary>
    /// <param name="childSaga">子 Saga 编排器实例（object 类型）</param>
    /// <param name="childState">子 Saga 初始状态</param>
    /// <param name="triggerEvent">触发子 Saga 的事件</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>执行后的子 Saga 状态</returns>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Non-generic child saga dispatch relies on reflection; not compatible with trimming.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Uses MakeGenericMethod/MakeGenericType for non-generic dispatch; not compatible with native AOT.")]
    internal ValueTask<SagaState> ExecuteChildSagaNonGenericAsync(
        object childSaga,
        SagaState childState,
        object triggerEvent,
        CancellationToken ct);
}
