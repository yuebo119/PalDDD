// ─────────────────────────────────────────────────────────────
// ⏰ SagaTimeoutDetector — Saga 步骤级超时检测
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么提取为独立类？
//   ｜ Saga<TState> 的超时检测逻辑（~30 行）与编排/补偿独立。
//   ｜ 提取后可以单独测试各种超时场景。
//   ｜
// 💡 超时如何计算？
//   ｜ 从步骤开始时间（SagaState.StepStartedAt）起算，而非 Saga 创建时间。
//   ｜ 避免前置步骤耗时挤占后续步骤的超时窗口。
//   ｜ 返回所有超时步骤（而非仅第一个），确保全部被处理。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// Saga 步骤级超时检测器。<br/>
/// 检查指定 Saga 状态中是否有步骤已超时。
/// </summary>
internal sealed class SagaTimeoutDetector<TState>
    where TState : SagaState
{
    private readonly IReadOnlyList<SagaStepRegistration> _stepsInOrder;

    public SagaTimeoutDetector(IReadOnlyList<SagaStepRegistration> stepsInOrder)
    {
        // P3 守卫族对齐（SRC-108）：null stepsInOrder 原延迟到 IsTimedOut 的 foreach 才 NRE，
        // 失败点远离构造入口（对齐 PalOrmUnitOfWork ITM-281 三栈守卫同款口径）
        ArgumentNullException.ThrowIfNull(stepsInOrder);
        _stepsInOrder = stepsInOrder;
    }

    /// <summary>
    /// 检查 Saga 是否超时——收集所有超时步骤。
    /// </summary>
    /// <remarks>
    /// v36 P3 边界声明：超时命中的"执行中滞留步骤"（StepStartedAt 已记录但步骤未返回）
    /// 自身不在补偿范围——CompensateAllAsync 按 ExecutedStepKeys（仅成功步骤）执行；
    /// 若滞留步骤最终完成产生副作用而 Saga 已被补偿至终态，该副作用泄漏。
    /// 补偿"已成功执行的步骤"是既定语义（SagaStep 类头整步重放+幂等契约），
    /// 滞留窗口（超时命中→步骤完成）极窄，接受为边界。
    /// </remarks>
    /// <param name="state">当前 Saga 状态</param>
    /// <param name="now">当前时间</param>
    /// <param name="timedOutSteps">超时的步骤列表</param>
    /// <returns>true 表示至少有一个步骤超时</returns>
    public bool IsTimedOut(TState state, DateTimeOffset now, out IReadOnlyList<SagaStep> timedOutSteps)
    {
        ArgumentNullException.ThrowIfNull(state);

        // 常态（无超时）路径零分配：下标循环 + 首个命中才建 List + Array.Empty 空视图。
        List<SagaStep>? list = null;
        for (var i = 0; i < _stepsInOrder.Count; i++)
        {
            var registration = _stepsInOrder[i];
            if (IsTimedOutStep(state, registration, now))
                (list ??= []).Add(registration.Step);
        }

        timedOutSteps = (IReadOnlyList<SagaStep>?)list ?? Array.Empty<SagaStep>();
        return list is not null;
    }

    /// <summary>
    /// 计算下一次需要检查的时间（2026-10-19 M1）：当前状态中带 Timeout、已开始执行且
    /// 未被 AWD 已成功步骤排除的步骤，取 <c>StepStartedAt + Timeout</c> 的最小值。
    /// 无待检查步骤返回 null（Saga 在等待事件，不需要超时扫描）。
    /// </summary>
    public DateTimeOffset? ComputeNextCheckAt(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        DateTimeOffset? next = null;
        for (var i = 0; i < _stepsInOrder.Count; i++)
        {
            var registration = _stepsInOrder[i];
            if (!MatchesStep(state, registration)
                || !state.StepStartedAt.TryGetValue(registration.Key, out var startedAt))
                continue;

            var due = startedAt + registration.Step.Timeout!.Value;
            if (next is null || due < next.Value)
                next = due;
        }
        return next;
    }

    /// <summary>
    /// v43 P2 判据三条件合取：
    /// ① AWD 态 + 普通步骤已成功 + 残留时间戳超期 → 排除（中断态显式无限等待契约）；
    /// ② InterruptStep 自带 Timeout 超期 → 保留（人工决策失踪兜底）；
    /// ③ Active 态普通步骤已成功 + 超期 → 保留（Saga 卡死兜底）。
    /// 2026-10-21：状态名在注册期固化，扫描不再从 key 切分（原 ExtractState 每步一次分配）。
    /// </summary>
    private static bool MatchesStep(TState state, in SagaStepRegistration registration)
    {
        var isAwdCompletedNonInterruptStep = state.Status == SagaStatus.AwaitingHumanDecision
            && state.ExecutedStepKeys.Contains(registration.Key)
            && registration.Step.DispatchKind != StepDispatchKind.Interrupt;
        return registration.Step.Timeout.HasValue
            && state.CurrentState == registration.State
            && !isAwdCompletedNonInterruptStep;
    }

    private static bool IsTimedOutStep(TState state, in SagaStepRegistration registration, DateTimeOffset now)
        => MatchesStep(state, registration)
           && state.StepStartedAt.TryGetValue(registration.Key, out var startedAt)
           && (now - startedAt) > registration.Step.Timeout!.Value;
}

/// <summary>
/// 步骤注册项——注册期同时固化状态名、事件名与持久化 key。运行时查找与超时扫描只读
/// 这三个值，不再按事件重建 "State|EventName" 字符串，也不从 key 切分状态名。
/// </summary>
internal readonly record struct SagaStepRegistration(
    string State,
    string? EventName,
    string Key,
    SagaStep Step);

/// <summary>Saga key 工具——"state|EventType" 格式的解析。Saga 与 SagaTimeoutDetector 共用。</summary>
/// <remarks>
/// 💡 <b>key 格式（P3 声明·十七轮）</b>：事件精确匹配 key 为 <c>"State|EventName"</c>（如
/// <c>"Approved|OrderCreated"</c>）；通配步骤 key 仅为 <c>"State"</c>（无事件段）。
/// <para>
/// ⚠️ <b>运行时校验：</b><see cref="Make"/> 在 state 包含 <c>|</c> 时抛出 <see cref="ArgumentException"/>，
/// 因为含 <c>|</c> 的状态名会破坏 <see cref="ExtractState"/> 的状态名还原逻辑。
/// 实际场景中状态名通常为 PascalCase 或 kebab-case，不会出现此字符。
/// <see cref="SagaState.CurrentState"/> 的 setter 同样执行此校验。
/// </para>
/// <para>
/// ⚠️ <b>同名碰撞限制（P3 声明·十七轮）</b>：key 的事件段只取
/// <see cref="Type"/> 的 <c>Name</c>（不含命名空间）——同状态、不同命名空间的同名事件类型
/// （如 <c>V1.OrderCreated</c> 与 <c>V2.OrderCreated</c>）生成相同 key，后注册者
/// 覆盖前者（<see cref="Saga{TState}"/> 的 When 重复 key 覆盖语义）；同理通配 key
/// <c>"State"</c> 与名为 <c>"State"</c> 的另一状态通配注册天然同 key。避免在不同
/// 命名空间使用同名事件类型驱动同一状态转换。
/// </para>
/// </remarks>
internal static class SagaKey
{
    /// <summary>
    /// 构造 Saga 步骤 key。
    /// </summary>
    /// <param name="state">Saga 状态名（不含 <c>|</c> 字符）</param>
    /// <param name="eventType">触发事件类型；null 表示通配步骤</param>
    /// <exception cref="ArgumentException"><paramref name="state"/> 包含 <c>|</c> 字符时抛出</exception>
    public static string Make(string state, Type? eventType)
    {
        if (state.Contains('|'))
            throw new ArgumentException(
                $"Saga 状态名不能包含 '|' 字符（当前值：\"{state}\"），因为 '|' 用作 key 分隔符。请使用 PascalCase 或 kebab-case。",
                nameof(state));

        return eventType is null ? state : $"{state}|{eventType.Name}";
    }

    /// <summary>
    /// 从 Saga key 中提取状态名（<c>|</c> 之前的部分）。
    /// </summary>
    public static string ExtractState(string key)
    {
        var idx = key.IndexOf('|', StringComparison.Ordinal);
        return idx < 0 ? key : key[..idx];
    }
}
