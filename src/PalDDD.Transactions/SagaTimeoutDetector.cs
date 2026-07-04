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
            if (step.Timeout.HasValue
                && state.CurrentState == SagaKey.ExtractState(key)
                && state.StepStartedAt.TryGetValue(key, out var startedAt)
                && (now - startedAt) > step.Timeout.Value)
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
/// 💡 <b>分隔符约束：</b>key 使用 <c>|</c> 作为 state 与 eventType 的分隔符。
/// <para>
/// ⚠️ <b>运行时校验：</b><see cref="Make"/> 在 state 包含 <c>|</c> 时抛出 <see cref="ArgumentException"/>，
/// 因为含 <c>|</c> 的状态名会破坏 <see cref="ExtractState"/> 的状态名还原逻辑。
/// 实际场景中状态名通常为 PascalCase 或 kebab-case，不会出现此字符。
/// <see cref="SagaState.CurrentState"/> 的 setter 同样执行此校验。
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
