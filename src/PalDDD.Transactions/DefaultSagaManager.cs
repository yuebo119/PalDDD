// ─────────────────────────────────────────────────────────────
// 🔧 DefaultSagaManager — ISagaManager 的默认实现
// ─────────────────────────────────────────────────────────────
//
// 💡 职责：提供 ISagaManager 的最小可行实现。
//   ｜ 用户可注入自定义实现替换默认行为。
//   ｜ 中断恢复：暂存决策 + 恢复派发闭包于内存字典，ResumeAsync 以决策为事件
//   ｜ 重新进入 Saga.ProcessEventAsync 执行管线（P2 修复·八轮——此前仅暂存决策，
//   ｜ 无人消费，恢复链路断裂）。
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
/// 中断恢复使用内存字典暂存决策与恢复派发闭包。<see cref="ResumeAsync{TDecision}"/>
/// 以决策为事件重新进入 Saga 的 <c>ProcessEventAsync</c> 管线，成功后移除条目；
/// 未知 SagaId 或二次恢复抛 <see cref="InvalidOperationException"/>（不再静默丢弃决策）。
/// 生产环境应替换为持久化实现（重启后内存条目丢失）。<br/>
/// 子 Saga 执行直接委托给子编排器的 ProcessEventAsync。
/// </remarks>
public sealed class DefaultSagaManager : ISagaManager
{
    /// <summary>暂存的中断状态 — keyed by sagaId</summary>
    private readonly ConcurrentDictionary<PalUlid, InterruptedSagaEntry> _interrupted = [];

    /// <inheritdoc/>
    /// <remarks>
    /// 默认实现：以决策为事件重新派发到中断时的 Saga 执行管线
    /// （<c>ProcessEventAsync</c>，含重试/补偿）。派发成功后移除中断条目；
    /// 派发抛异常时保留条目，可再次调用恢复。未注册的 SagaId 抛
    /// <see cref="InvalidOperationException"/>——决策要么被投递、要么可见地失败，
    /// 不静默丢弃（P2 修复·八轮：此前仅暂存决策且条目只增不减）。<br/>
    /// 📐 <b>并发语义（P3 声明·二十一轮）</b>：本实现非线程安全——同一 sagaId 的多次
    /// 决策/恢复调用之间无互斥（ResumeDispatch 直接进入 Saga 管线，条目移除按 KVP 身份
    /// 仅防误删，不防并发重入）。同一 sagaId 的决策必须由调用方串行投递（先等上一次
    /// ResumeAsync 完成再投递下一决策）；跨 sagaId 并发恢复安全。
    /// </remarks>
    public async ValueTask ResumeAsync<TDecision>(
        PalUlid sagaId, TDecision decision, CancellationToken ct)
        where TDecision : notnull
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (!_interrupted.TryGetValue(sagaId, out var entry))
            throw new InvalidOperationException(
                $"Saga {sagaId} 无已注册的中断条目——可能从未中断、已恢复成功，或进程重启丢失内存状态。");

        // P3 修复（十七轮）：删除 entry.SetDecision(decision) 调用与 Decision 属性——
        // ResumeDispatch 委托直接以参数接收决策，Decision 只写不读（死状态）

        // P2 修复（八轮）：重新派发——把中断恢复接回执行管线
        var resumedState = await entry.ResumeDispatch(decision, ct).ConfigureAwait(false);

        // P3 修复（九轮→十轮修正）：状态 Alone 无法区分"路由缺失"与"合法二次中断"
        // （多阶段 HITL 的决策触发下一个 InterruptStep 同样返回 AwaitingHumanDecision）——
        // 用条目身份判别：二次中断会 RegisterInterrupted 以新条目对象替换字典项，
        // 路由缺失则条目原样未动。路由缺失时可见失败并保留条目，兑现"要么投递
        // 要么可见失败"契约；二次中断则保留新条目供下一次决策到达。
        if (resumedState.Status == SagaStatus.AwaitingHumanDecision)
        {
            if (_interrupted.TryGetValue(sagaId, out var currentEntry) && ReferenceEquals(currentEntry, entry))
                throw new InvalidOperationException(
                    $"Saga {sagaId} 未注册决策类型 {decision.GetType().Name} 的处理路由（缺少对应 When 注册）——决策未被消费。");
            return; // 合法二次中断：新条目已就位，本次恢复视为成功
        }

        // 恢复成功后移除条目（此前 _interrupted 只增不减，内存泄漏）
        // P3 修复（十七轮）：KVP 身份形式 TryRemove——按 key 移除在并发场景会误删：
        // resume 期间同一 sagaId 发生新中断注册（RegisterInterrupted 以新条目替换字典项）
        // 时，按 key 移除删掉的是新条目；KVP 重载仅当字典中仍是本条目时才移除
        _interrupted.TryRemove(new KeyValuePair<PalUlid, InterruptedSagaEntry>(sagaId, entry));
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
        // P1 修复（七轮评审）：ProcessEventAsync 是非泛型方法（Saga.cs:181 签名
        // ProcessEventAsync(TState, object, CancellationToken)）——MakeGenericMethod 对非泛型
        // MethodInfo 必抛 ArgumentException。直接调用即可。
        var method = sagaType.GetMethod(
            "ProcessEventAsync",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Saga type '{sagaType.Name}' does not have a public ProcessEventAsync method. Ensure it inherits Saga<TState>.");

        // Invoke: returns boxed ValueTask<TState>（非泛型方法直接 Invoke）
        var boxedValueTask = method.Invoke(childSaga, [childState, triggerEvent, ct])!;

        return await UnboxAndAwaitValueTaskAsync(boxedValueTask, method.ReturnType, stateType).ConfigureAwait(false);
    }

    /// <summary>拆箱并 await 反射调用的 ValueTask&lt;TState&gt;，返回 SagaState 结果。</summary>
    [RequiresDynamicCode("Uses MakeGenericType to construct ValueTask<TState> when reflection return type is not pre-constructed; not compatible with native AOT.")]
    private static async ValueTask<SagaState> UnboxAndAwaitValueTaskAsync(
        object boxedValueTask, Type? methodReturnType, Type stateType)
    {
        // P3 修复（八轮评审）：优先直接使用 method.ReturnType（已是构造完的 ValueTask<T>）——
        // MakeGenericType(stateType) 在方法实际声明类型与运行时 stateType 不一致时
        // （如 saga 类型继承链上的隐藏/协变场景）会构造出错误类型导致 AsTask 绑定失败
        var valueTaskType = methodReturnType is { IsConstructedGenericType: true } constructed
            && constructed.GetGenericTypeDefinition() == typeof(ValueTask<>)
                ? constructed
                : typeof(ValueTask<>).MakeGenericType(stateType);
        var asTaskMethod = valueTaskType.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance)!;
        var task = (Task)asTaskMethod.Invoke(boxedValueTask, null)!;
        await task.ConfigureAwait(false);

        // Extract Result property (Task<T>.Result)
        var taskType = task.GetType();
        var resultProp = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)!;
        return (SagaState)resultProp.GetValue(task)!;
    }

    /// <summary>注册一个中断的 Saga——由 InterruptStep 执行时调用。</summary>
    /// <param name="resumeDispatch">
    /// 恢复派发委托——以人工决策为事件重新进入该 Saga 的 ProcessEventAsync 管线，
    /// 由 <see cref="ResumeAsync{TDecision}"/> 在决策到达时调用。
    /// </param>
    // P3 修复（二十一轮）：删除 decisionType 参数与 InterruptedSagaEntry.DecisionType 字段——
    // 字段只写不读（死状态）。原拟"由 GetInterruptedSagasAsync 消费"，但接口契约返回
    // IReadOnlyList<SagaState>，无法携带 DecisionType（改公共接口超出 P3 范围）；
    // 期望的决策类型仍可由调用方经 InterruptStep.DecisionType（公共 DSL 元数据）获知。
    internal void RegisterInterrupted(
        PalUlid sagaId,
        string reason,
        Func<object, CancellationToken, ValueTask<SagaState>> resumeDispatch)
    {
        ArgumentNullException.ThrowIfNull(resumeDispatch);
        _interrupted[sagaId] = new InterruptedSagaEntry(sagaId, reason, resumeDispatch);
    }

    private sealed class InterruptedSagaEntry
    {
        public PalUlid SagaId { get; }
        public string Reason { get; }

        /// <summary>恢复派发委托——以决策为事件重新进入 ProcessEventAsync（见 RegisterInterrupted）。</summary>
        public Func<object, CancellationToken, ValueTask<SagaState>> ResumeDispatch { get; }

        // P3 修复（十七轮）：删除 Decision 属性与 SetDecision 方法——决策经 ResumeDispatch
        // 参数直接传递，属性只写不读（死状态）
        // P3 修复（二十一轮）：再删 DecisionType 属性——同理只写不读（见 RegisterInterrupted 注释）

        public InterruptedSagaEntry(
            PalUlid sagaId,
            string reason,
            Func<object, CancellationToken, ValueTask<SagaState>> resumeDispatch)
        {
            SagaId = sagaId;
            Reason = reason;
            ResumeDispatch = resumeDispatch;
        }
    }
}
