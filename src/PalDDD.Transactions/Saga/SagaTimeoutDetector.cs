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
    private readonly IReadOnlyList<(string Key, SagaStep Step)> _stepsInOrder;

    public SagaTimeoutDetector(IReadOnlyList<(string Key, SagaStep Step)> stepsInOrder)
    {
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

        List<SagaStep> list = [];
        foreach (var (key, step) in _stepsInOrder)
        {
            // v43 P2 修复（判据三条件合取，三个既有测试语义矩阵一次保全）：
            // ① E1-P2 场景（AWD + 普通步骤已成功 + 残留时间戳超期）→ 排除：StepStartedAt
            //    只写不清（v17 声明），三十四轮 AWD 纳入扫描后同状态混注册（带 Timeout 普通
            //    步骤 A 成功执行 + 无 Timeout InterruptStep 中断）时，A 的残留时间戳让
            //    IsTimedOut 命中"早已成功"的 A，违背中断态"显式无限等待"契约；
            // ② 三十四轮场景（InterruptStep 自带 Timeout 超期）→ 保留触发："等待人工决策
            //    超时"是 InterruptStep 的设计能力（人工决策失踪由 Timeout 兜底回滚）；
            // ③ Active 态普通步骤已成功 + 超期 → 保留触发：saga 卡死兜底
            //    （RecordsCompensationFailedStatus 语义）。
            // 排除条件 = AWD 态 ∧ 步骤已成功完成 ∧ 非 InterruptStep
            var isAwdCompletedNonInterruptStep = state.Status == SagaStatus.AwaitingHumanDecision
                && state.ExecutedStepKeys.Contains(key)
                && step.DispatchKind != StepDispatchKind.Interrupt;
            if (step.Timeout.HasValue
                && state.CurrentState == SagaKey.ExtractState(key)
                && state.StepStartedAt.TryGetValue(key, out var startedAt)
                && (now - startedAt) > step.Timeout.Value
                && !isAwdCompletedNonInterruptStep)
            {
                list.Add(step);
            }
        }

        var hasTimeout = list.Count > 0;
        timedOutSteps = hasTimeout ? list : [];
        return hasTimeout;
    }
}

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
