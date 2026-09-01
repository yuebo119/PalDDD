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
//   ｜ DecisionType 为公共 DSL 元数据：声明期望的决策类型供 UI/文档读取；
//   ｜ 框架不做类型校验（v39 P3 勘正：原"编译时类型安全"失实——ResumeAsync 泛型
//   ｜ 参数由调用方自由指定，框架零消费该属性、无任何编译时/运行时绑定）。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// HITL 中断步骤——挂起 Saga 等待人工决策。
/// </summary>
public sealed class InterruptStep : SagaStep
{
    /// <summary>中断原因（供 UI/日志展示）</summary>
    public string InterruptReason { get; }

    /// <summary>决策数据类型（公共 DSL 元数据——声明期望的决策类型供 UI/文档读取；
    /// 框架不做类型校验，ResumeAsync 泛型参数由调用方保证）</summary>
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
        // P3 修复：入参校验（此前 null 延迟到使用点 NRE）
        ArgumentException.ThrowIfNullOrWhiteSpace(interruptReason);
        ArgumentNullException.ThrowIfNull(decisionType);
        InterruptReason = interruptReason;
        DecisionType = decisionType;
    }
}
