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
    /// <summary>获取一批活跃（<see cref="SagaStatus.Active"/>）的 Saga 状态。</summary>
    ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct);

    /// <summary>租约获取一批活跃 Saga，避免多实例后台扫描器重复处理同一状态。</summary>
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
    ValueTask<int> SaveChangesAsync(TState state, CancellationToken ct);
}
