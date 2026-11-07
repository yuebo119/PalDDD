// ─────────────────────────────────────────────────────────────
// 📋 SagaStepDescriptor — 已注册步骤的只读投影（ADR-039 / 审计 A4）
// ─────────────────────────────────────────────────────────────
//
// 💡 为什么需要：Saga<TState>.Steps 是 protected，框架此前无公开枚举入口；
//   ｜ DynamicStep.Route 返回未知 key 时零信号——拼写错误与滚动发布窗口期未注册
//   ｜ 新 key 在观测上不可区分。本 DTO 给宿主/运维一个可查询的步骤清单
//   ｜ （启动期自检、诊断端点、健康探针），不暴露 SagaStep 的委托与可变成员。
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// 已注册 Saga 步骤的只读投影（ADR-039）。
/// </summary>
/// <param name="Key">步骤键（<c>State</c> 或 <c>State + EventName</c> 的规范化形式）。</param>
/// <param name="DispatchKind">分发类型（Normal / FanOut / ChildSaga / Interrupt / Dynamic）。</param>
/// <param name="Timeout">
/// 步骤超时（可选）。与 <see cref="SagaStep.Timeout"/> 同义：仅被快照超时检测器消费，
/// 不表示「执行中到点中断」。
/// </param>
public readonly record struct SagaStepDescriptor(
    string Key,
    StepDispatchKind DispatchKind,
    TimeSpan? Timeout);
