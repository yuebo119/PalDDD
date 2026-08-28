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

    /// <summary>已失效（超时补偿回滚）的 sagaId 集合——拒绝幽灵条目再注册（v27 P2）</summary>
    private readonly ConcurrentDictionary<PalUlid, byte> _invalidated = [];

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
    /// ResumeAsync 完成再投递下一决策）；跨 sagaId 并发恢复安全。<br/>
    /// 📐 <b>已知残余窗口（v37 P2 声明）</b>：决策派发与超时补偿线程并发交错时（决策恰落在
    /// <c>CompensateAsync</c> 执行期间），决策副作用可能在正被回滚的 Saga 上产生且假成功——
    /// 失效集在补偿保存成功后才写入，窗口内不可检测。v26/v27 修复族只关闭了"补偿完成后"
    /// 半边（迟到决策经失效集/终态检查可见失败），"补偿进行中"半边仍敞开。框架级根治需
    /// manager 侧租约 fencing（v3.0 接口窗口）；调用方对配 Timeout 的 HITL 步骤应保证
    /// 决策与扫描周期错开。
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

        // P2 修复（二十五轮）：补偿/死信终态的决策必然未被消费——超时兜底补偿
        // （SagaTimeoutProcessor）把中断 Saga 置 Compensated 后全程不触碰本 Manager
        // （条目滞留），迟到决策对终态状态无路由命中而宽容返回；若走 TryRemove+正常
        // 返回即"静默吞弃+假报成功"，违反本方法"要么投递、要么可见失败"契约。三类
        // 补偿/死信终态时先清理条目（终态不可恢复）再可见失败。
        // Completed 不在此列：那是决策被消费后的正常完成路径（保持成功返回）；
        // 已完成 Saga 的重复决策宽容处理（幂等语义）。
        if (resumedState.Status is SagaStatus.Compensated or SagaStatus.CompensationFailed
            or SagaStatus.DeadLettered)
        {
            _interrupted.TryRemove(new KeyValuePair<PalUlid, InterruptedSagaEntry>(sagaId, entry));
            throw new InvalidOperationException(
                $"Saga {sagaId} 已处于终态 {resumedState.Status}（可能已被超时补偿回滚），" +
                $"决策 {decision.GetType().Name} 未能被消费——按契约可见失败而非静默丢弃。");
        }

        // P3 修复（九轮→十轮修正）：状态 Alone 无法区分"路由缺失"与"合法二次中断"
        // （多阶段 HITL 的决策触发下一个 InterruptStep 同样返回 AwaitingHumanDecision）——
        // 用条目身份判别：二次中断会 RegisterInterrupted 以新条目对象替换字典项，
        // 路由缺失则条目原样未动。路由缺失时可见失败并保留条目，兑现"要么投递
        // 要么可见失败"契约；二次中断则保留新条目供下一次决策到达。
        // v28 P2 修复：十轮判别设计时条目只有"原样/被替换"两个预期态——v26 的
        // InvalidateInterrupted（TryRemove）引入第三态"被移除"：TryGetValue false 时
        // 原逻辑短路落到 return，飞行中决策（ResumeDispatch 返回 AWD 且路由缺失）被
        // 静默吞弃+假成功，与 v26 修复动机同款违约。三分支判别补全第三态。
        if (resumedState.Status == SagaStatus.AwaitingHumanDecision)
        {
            if (!_interrupted.TryGetValue(sagaId, out var currentEntry))
                throw new InvalidOperationException(
                    $"Saga {sagaId} 的中断条目在决策派发期间被移除（可能已被超时补偿失效）——决策 {decision.GetType().Name} 未能被消费。");
            if (ReferenceEquals(currentEntry, entry))
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

    /// <summary>
    /// 失效指定 Saga 的中断条目——超时兜底补偿（SagaTimeoutProcessor）把 Saga 置
    /// Compensated/CompensationFailed 后调用，使迟到的决策走"无已注册的中断条目"
    /// 可见失败而非静默处理。
    /// <para>
    /// v26 P1 修复背景：ResumeAsync 内的终态分支（Compensated/CompensationFailed/
    /// DeadLettered 检查）依赖 resumedState 终态，但条目闭包捕获的是<b>中断时实例</b>，
    /// 补偿写的是 store 租约 successor（CloneForLease 后继实例）——旧实例永远停留在
    /// AwaitingHumanDecision，终态分支不可达，迟到决策会在已回滚 Saga 上执行副作用
    /// 并假成功。本方法把失效责任移到补偿侧（补偿持有 successor 真实状态），
    /// 终态分支保留作双保险。
    /// </para>
    /// <para>
    /// v27 P2 修复：失效同时登记失效集——失效后仍飞行中的 ResumeDispatch 在旧实例上
    /// 触发下一个 InterruptStep（多阶段 HITL）时 RegisterInterrupted 会注册"幽灵条目"，
    /// 使后续决策经幽灵条目在已回滚 Saga 上继续执行并假成功（v26 失效被绕过）。
    /// 失效集按 sagaId 记录（16 字节/项，量级=超时补偿的 HITL Saga 数，只增不减——
    /// 与 _interrupted 的历史泄漏教训权衡：本集拒绝注册后条目永不重建，幂等防御所需）。
    /// 按 key TryRemove 与 ResumeAsync 的 KVP 身份式语义差异由此失效集兜底（再注册被拒）。
    /// </para>
    /// </summary>
    public void InvalidateInterrupted(PalUlid sagaId)
    {
        // v29 P2 修复：先写失效集再移除条目——两步换序消除竞态窗口。原序
        // （TryRemove → _invalidated 写入）的窗口内并发 RegisterInterrupted 读
        // ContainsKey=false 照常注册幽灵条目（v27 防护经窗口回归）；换序后窗口内
        // 注册被失效集拒绝、迟到决策走"无条目"IOE，两向闭环
        _invalidated[sagaId] = 1;
        _interrupted.TryRemove(sagaId, out _);
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
    [UnconditionalSuppressMessage("Aot", "IL2075:RequiresDynamicallyAccessedMembers",
        Justification = "整个方法已声明 RequiresDynamicCode 边界——GetProperty 位于该边界内，消费方已获得编译期 AOT 警告。")]
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
    // v34 P3：再删 reason 参数——InterruptedSagaEntry.Reason 同样只写不读（死状态，
    // 与 Decision/DecisionType 残留同款）；中断原因仍由 SagaState.InterruptReason 承载，
    // 条目无需副本。
    internal void RegisterInterrupted(
        PalUlid sagaId,
        Func<object, CancellationToken, ValueTask<SagaState>> resumeDispatch)
    {
        ArgumentNullException.ThrowIfNull(resumeDispatch);
        // v27 P2 修复：失效 Saga 拒绝再注册——失效（超时补偿）后仍飞行中的
        // ResumeDispatch 在旧实例上触发下一个 InterruptStep（多阶段 HITL）时，
        // 若照常注册会形成"幽灵条目"，后续决策经幽灵条目在已回滚 Saga 上继续
        // 执行并假成功（v26 Invalidate 的一次性 TryRemove 被再注册绕过）
        if (_invalidated.ContainsKey(sagaId))
            throw new InvalidOperationException(
                $"Saga {sagaId} 已被超时兜底补偿失效，拒绝注册新的中断条目——决策不应再投向已回滚的 Saga。");
        var entry = new InterruptedSagaEntry(sagaId, resumeDispatch);
        _interrupted[sagaId] = entry;
        // v34 P2 修复：写入后二次检查（double-check）收口 TOCTOU 残余——ContainsKey 检查
        // 与字典写入是两个独立操作，"检查 false → Invalidate 落地（失效集写入+TryRemove
        // 旧条目）→ 本方法写入新条目"的交错会让幽灵条目逃过前置检查。失效集单调为真
        // （Invalidate 先写失效集再删条目，v29 换序），写入后重读必然看到并发失效 →
        // 撤销刚写入的条目（KVP 身份式防误删并发新注册），任何交错下幽灵条目均不可留存
        if (_invalidated.ContainsKey(sagaId))
        {
            _interrupted.TryRemove(new KeyValuePair<PalUlid, InterruptedSagaEntry>(sagaId, entry));
            throw new InvalidOperationException(
                $"Saga {sagaId} 在中断注册期间被超时兜底补偿失效——拒绝注册（并发失效检查）。");
        }
    }

    private sealed class InterruptedSagaEntry
    {
        public PalUlid SagaId { get; }

        /// <summary>恢复派发委托——以决策为事件重新进入 ProcessEventAsync（见 RegisterInterrupted）。</summary>
        public Func<object, CancellationToken, ValueTask<SagaState>> ResumeDispatch { get; }

        // P3 修复（十七轮）：删除 Decision 属性与 SetDecision 方法——决策经 ResumeDispatch
        // 参数直接传递，属性只写不读（死状态）
        // P3 修复（二十一轮）：再删 DecisionType 属性——同理只写不读（见 RegisterInterrupted 注释）
        // v34 P3：再删 Reason 属性——同理只写不读（见 RegisterInterrupted 注释）

        public InterruptedSagaEntry(
            PalUlid sagaId,
            Func<object, CancellationToken, ValueTask<SagaState>> resumeDispatch)
        {
            SagaId = sagaId;
            ResumeDispatch = resumeDispatch;
        }
    }
}
