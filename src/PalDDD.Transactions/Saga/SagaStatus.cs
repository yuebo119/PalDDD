namespace PalDDD.Transactions;

/// <summary>Saga 生命周期状态</summary>
public enum SagaStatus
{
    /// <summary>活跃中 — 正常执行</summary>
    Active,

    /// <summary>已完成 — 所有步骤成功</summary>
    Completed,

    /// <summary>已补偿 — 超时或失败后补偿已成功执行</summary>
    Compensated,

    /// <summary>补偿失败 — 补偿执行失败，需人工介入</summary>
    CompensationFailed,

    /// <summary>死信 — 自动补偿无法继续安全重试</summary>
    DeadLettered,

    /// <summary>等待人工决策 — HITL 中断后挂起，等待人工输入恢复</summary>
    AwaitingHumanDecision
}
