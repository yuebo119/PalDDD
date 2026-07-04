// ─────────────────────────────────────────────────────────────
// 💔 SagaCompensation — Saga 补偿策略（逆序/正序/无补偿）
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么提取为独立类？
//   ｜ Saga<TState> 的补偿逻辑（~65 行）与编排逻辑独立。
//   ｜ 提取后：Saga 专注编排，Compensation 专注回滚策略。
//   ｜ 单一职责：补偿逻辑变更不影响编排逻辑。
//   ｜
// 💡 什么是补偿？
//   ｜ Saga 步骤失败后，需要"撤销"已成功执行的步骤。
//   ｜ 例如：下单→扣库存→扣款 中扣款失败 → 补偿恢复库存。
//   ｜
// 💡 三种策略：
//   ｜ Backward（逆序）— 最后执行的先补偿（默认）
//   ｜ Forward（正序）— 先执行的先补偿
//   ｜ None — 不补偿，失败后停止
// ─────────────────────────────────────────────────────────────

using PalDDD.Core.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace PalDDD.Transactions;

/// <summary>
/// Saga 补偿策略执行器。<br/>
/// 按 <see cref="CompensationPolicy"/> 决定顺序执行已注册步骤的补偿动作。
/// </summary>
internal sealed class SagaCompensation<TState>
    where TState : SagaState
{
    private readonly CompensationPolicy _policy;
    private readonly IReadOnlyDictionary<string, SagaStep> _stepsByKey;

    public SagaCompensation(
        CompensationPolicy policy,
        IReadOnlyDictionary<string, SagaStep> stepsByKey)
    {
        _policy = policy;
        _stepsByKey = stepsByKey;
    }

    /// <summary>
    /// 补偿已执行的步骤（含当前失败步骤）。<br/>
    /// 用于 ProcessEventAsync 中步骤执行失败后的补偿。
    /// </summary>
    public async ValueTask CompensateExecutedStepsAsync(
        TState state, string failedStepKey, SagaStep failedStep, CancellationToken ct)
    {
        if (_policy == CompensationPolicy.None) return;

        // 构建补偿步骤列表：已执行的步骤 + 当前失败步骤（如果有补偿）。
        // 补偿顺序：失败步骤优先回滚（追加到列表末尾 → Backward 策略首个补偿），
        // 然后按实际已执行步骤的逆序执行。这确保可能部分执行的失败步骤最先被回滚。
        List<SagaStep> targets = [];
        foreach (var key in state.ExecutedStepKeys)
        {
            if (_stepsByKey.TryGetValue(key, out var step) && step.CompensateAsync is not null)
                targets.Add(step);
        }

        if (failedStep.CompensateAsync is not null && !targets.Contains(failedStep))
            targets.Add(failedStep);

        await RunAsync(state, targets, ct);
    }

    /// <summary>
    /// 补偿已执行的步骤。<br/>
    /// 用于 SagaProcessor 超时补偿——仅回滚实际执行过的步骤，不补偿未执行步骤。
    /// </summary>
    public async ValueTask CompensateAllAsync(TState state, CancellationToken ct)
    {
        if (_policy == CompensationPolicy.None) return;

        // 基于 ExecutedStepKeys 补偿：只补偿实际已执行的步骤，避免补偿未执行步骤。
        List<SagaStep> targets = [];
        foreach (var key in state.ExecutedStepKeys)
        {
            if (_stepsByKey.TryGetValue(key, out var step) && step.CompensateAsync is not null)
                targets.Add(step);
        }

        await RunAsync(state, targets, ct);
    }

    /// <summary>
    /// 按策略顺序执行补偿步骤。<br/>
    /// 💡 单个补偿失败不中断后续补偿——仅失败时分配 List&lt;Exception&gt;。
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception",
        Justification = "补偿循环必须收集所有步骤的失败异常并聚合抛出，不能因单个补偿失败中断后续补偿；OperationCanceledException 已由前一 catch 分支处理。")]
    private async ValueTask RunAsync(TState state, List<SagaStep> targets, CancellationToken ct)
    {
        if (targets.Count == 0) return;

        List<Exception>? failures = null;
        var (start, end, step) = _policy switch
        {
            CompensationPolicy.Backward => (targets.Count - 1, -1, -1),
            CompensationPolicy.Forward => (0, targets.Count, 1),
            _ => (0, 0, 0)
        };

        for (int i = start; i != end; i += step)
        {
            try
            {
                await targets[i].CompensateAsync!(state, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PalMetrics.SagaCompensationFailed.Add(1);
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException(
                $"Compensation failed for {failures.Count} of {targets.Count} steps.", failures);
    }
}
