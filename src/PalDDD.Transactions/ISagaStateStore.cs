// ─────────────────────────────────────────────────────────────
// 💾 ISagaStateStore<T> — Saga 状态持久化抽象
// ─────────────────────────────────────────────────────────────
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

/// <summary>Saga 状态持久化抽象 — 解耦 Saga 存储与具体数据库实现</summary>
/// <remarks>
/// EF Core implementation is provided by the PalDDD.Transactions.EFCore adapter package.<br/>
/// 其他实现（MongoDB / Redis / DynamoDB）只需实现此接口即可接入 Saga 处理器。
/// </remarks>
/// <typeparam name="TState">Saga 状态类型</typeparam>
public interface ISagaStateStore<TState> where TState : SagaState
{
    /// <summary>
    /// 获取一批活跃的 Saga 状态（<see cref="SagaStatus.Active"/> 与
    /// <see cref="SagaStatus.AwaitingHumanDecision"/>——三十四轮起中断态纳入扫描，
    /// 用于中断步骤超时兜底补偿；未配置步骤 Timeout 的中断态不会被 IsTimedOut 命中）。
    /// </summary>
    ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct);

    /// <summary>
    /// 租约获取一批活跃 Saga，避免多实例后台扫描器重复处理同一状态。<br/>
    /// 扫描集含 <see cref="SagaStatus.Active"/> 与 <see cref="SagaStatus.AwaitingHumanDecision"/>
    ///（三十四轮中断态超时兜底）——是否补偿由 <c>SagaTimeoutProcessor.IsTimedOut</c> 门控：
    /// 中断步骤配置了 Timeout 且超期才触发补偿，否则仅经历租约获取/释放（显式无限等待）。
    /// </summary>
    ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct);

    /// <summary>根据 ID 获取 Saga 状态</summary>
    ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct);

    /// <summary>持久化 Saga 状态更改。</summary>
    /// <param name="state">被修改的状态实例。
    /// Dapper 适配器：必需，无变更跟踪。
    /// EF Core 适配器：内部使用 DbContext 变更跟踪，但建议传入以保持接口一致。</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>受影响行数：<c>1</c> 表示写入生效；<c>0</c> 表示目标行不存在或乐观锁冲突
    /// （他实例已写同一 Saga），调用方据此判定本实例内存快照作废（P3 修复·十七轮：
    /// 统一 EF/Dapper/内存三实现的返回值语义，避免调用方按 0 行误判或漏判冲突）。</returns>
    ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct);
}
