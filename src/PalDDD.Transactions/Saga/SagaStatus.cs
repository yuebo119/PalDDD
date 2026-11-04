namespace PalDDD.Transactions;

/// <summary>
/// Saga 生命周期状态。
/// <para>
/// ⚠️ <b>数值是持久化契约</b>：<c>status</c> 列在各栈均为 INT，值即本枚举的显式编号。
/// 删除/新增成员必须沿用显式编号，禁止插入到中间——隐式重排会静默改变已落库行的语义
/// （v5.0.0 批次 B 实例：`DeadLettered` 曾占 4，删它时若走隐式重排，`AwaitingHumanDecision`
/// 会从 5 变 4，库里 status=4 的存量行被读成「等待人工决策」而 status=5 的行变成未定义；
/// SQL 侧 `status IN (0, 5)` 同样失配）。
/// </para>
/// </summary>
public enum SagaStatus
{
    /// <summary>活跃中 — 正常执行</summary>
    Active = 0,

    /// <summary>已完成 — 所有步骤成功</summary>
    Completed = 1,

    /// <summary>已补偿 — 超时或失败后补偿已成功执行</summary>
    Compensated = 2,

    /// <summary>补偿失败 — 补偿执行失败，需人工介入</summary>
    CompensationFailed = 3,

    // 4 是空洞：DeadLettered 在 5.0.0 版本删除（语义位置已被 CompensationFailed 占据——
    // DeadLetter 是消息中间件术语，Saga 域的对应概念是补偿失败；留着只让每个 switch
    // 多一个永不进入的分支）。该编号不得复用：复用它会让历史 status=4 的行被赋予新语义。

    /// <summary>等待人工决策 — HITL 中断后挂起，等待人工输入恢复</summary>
    AwaitingHumanDecision = 5
}
