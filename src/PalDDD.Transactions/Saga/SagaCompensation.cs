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
/// v40 P3 勘正（v39 执行序勘正的文档残留——原措辞"已注册步骤"暗示补偿全部注册步骤，
/// 与实现不符）：按 <see cref="CompensationPolicy"/> 决定顺序，基于
/// <see cref="SagaState.ExecutedStepKeys"/> 执行序对已执行步骤的补偿动作回放——
/// 未执行步骤不补偿（两个入口均先经 ExecutedStepKeys 过滤）。
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

        // ITM-207 修复（三十一轮）：ReferenceEquals 锁定引用语义——原 List.Contains 依赖
        // 引用相等，若 SagaStep 未来重写 Equals 为值语义则去重误判、失败步骤漏补偿。
        if (failedStep.CompensateAsync is not null && !targets.Any(s => ReferenceEquals(s, failedStep)))
            targets.Add(failedStep);

        await RunAsync(state, targets, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 补偿已执行的步骤。<br/>
    /// 用于 SagaProcessor 超时补偿——仅回滚实际执行过的步骤，不补偿未执行步骤。
    /// </summary>
    /// <remarks>
    /// ⚠️ 多实例下补偿可能与另一实例重入（终态落库乐观锁冲突时，他实例会再次调用）——
    /// 所有补偿动作必须幂等（契约详见 <see cref="SagaStep.CompensateAsync"/>，评审 P1-4）。
    /// </remarks>
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

        await RunAsync(state, targets, ct).ConfigureAwait(false);
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
            // v33 P3 终裁（二难消解——第三选项）：前序两版各有缺陷——删 discard 声称
            // "编译期穷尽"实证不成立（底层 int 未命名值 (CompensationPolicy)3 使无 discard
            // 的枚举 switch 恒触发 CS8524，warnaserror 下编译失败）；保留 discard 静默
            // (0,0,0) 则新增枚举值静默零补偿（原始评审发现）。终裁：discard 分支改抛——
            // 保留 discard 满足 CS8524 穷尽性（可编译），新增枚举值 fail-fast 而非静默零
            // 补偿（防御成立）。None 分支不可达（两个入口均对 None 提前 return）
            CompensationPolicy.Backward => (targets.Count - 1, -1, -1),
            CompensationPolicy.Forward => (0, targets.Count, 1),
            _ => throw new InvalidOperationException(
                $"未知的补偿策略 {_policy}——请在 CompensationPolicy 枚举新增值时同步本 switch（Backward 逆序 / Forward 正序）。")
        };

        for (int i = start; i != end; i += step)
        {
            try
            {
                await targets[i].CompensateAsync!(state, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // P3 修复（二十一轮）：指标语义 doc 声明——SagaCompensationFailed 在补偿动作
                // 抛异常的瞬间计数，度量的是"补偿动作失败发生"（每个失败步骤 +1，一次聚合
                // 可多次计数），不是"失败结果已持久化/已保存"。刻意不与 SagaProcessor 的
                // 状态保存链耦合（不在保存成功后再计数）：本执行器无从感知调用方的持久化
                // 语义，动作发生即记账才能覆盖"保存前进程崩溃"的窗口；如需"已落盘的补偿
                // 失败数"应在保存链路另行计数。
                PalMetrics.SagaCompensationFailed.Add(1);
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException(
                $"Compensation failed for {failures.Count} of {targets.Count} steps.", failures);
    }
}
