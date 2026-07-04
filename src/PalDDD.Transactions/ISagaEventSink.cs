// ─────────────────────────────────────────────────────────────
// 📡 ISagaEventSink — Saga 事件发射器接口
// ─────────────────────────────────────────────────────────────

namespace PalDDD.Transactions;

/// <summary>
/// Saga 事件接收器——接收 Saga 生命周期事件（步骤开始/完成/失败、补偿、状态变更）。
/// 实现此接口以将 Saga 事件推送到外部系统（日志、监控、审计等）。
/// </summary>
/// <remarks>
/// ⚠️ 事件通知为<b>尽力（best-effort）语义</b>——Saga 编排器不保证每个事件都被可靠投递。
/// 例如 InterruptStep 的状态变更事件以 fire-and-forget 方式发射，若接收端需可靠审计，
/// 应在业务步骤内通过 <c>IOutboxStore</c> 显式写入领域事件而非依赖此接口。<br/>
/// 框架使用 <see cref="SagaExecutionObserver"/> 作为默认实现，无 Sink 时静默跳过。
/// </remarks>
public interface ISagaEventSink
{
    /// <summary>发射一个 Saga 事件。</summary>
    ValueTask EmitAsync<T>(T sagaEvent, CancellationToken ct) where T : notnull;
}
