// ─────────────────────────────────────────────────────────────
// ✋ InterruptStep — HITL（Human-In-The-Loop）中断步骤
// ─────────────────────────────────────────────────────────────
//
// 💡 什么是 HITL 中断？
//   ｜ Saga 执行到需要人工决策的节点时挂起，等待外部输入后恢复。
//   ｜ 例如：风控审核 Saga 中"金额超过阈值 → 等待人工审批"。
//   ｜
// 💡 设计决策：
//   ｜ InterruptStep 挂起 Saga（Status → AwaitingHumanDecision）。
//   ｜ 外部系统通过 ISagaManager.ResumeAsync 恢复执行。
//   ｜ DecisionType 声明期望的决策数据类型，编译时类型安全。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// HITL 中断步骤——挂起 Saga 等待人工决策。
/// </summary>
public sealed class InterruptStep : SagaStep
{
    /// <summary>中断原因（供 UI/日志展示）</summary>
    public string InterruptReason { get; }

    /// <summary>决策数据类型</summary>
    public Type DecisionType { get; }

    /// <inheritdoc/>
    public override StepDispatchKind DispatchKind => StepDispatchKind.Interrupt;

    /// <summary>
    /// 创建中断步骤。
    /// </summary>
    /// <param name="key">步骤 key</param>
    /// <param name="interruptReason">中断原因描述</param>
    /// <param name="decisionType">期望的决策数据类型</param>
    public InterruptStep(string key, string interruptReason, Type decisionType)
        : base(key, execute: null!, compensate: null)
    {
        InterruptReason = interruptReason;
        DecisionType = decisionType;
    }
}
