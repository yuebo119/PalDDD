// ─────────────────────────────────────────────────────────────
// 🔄 IUnitOfWork — 工作单元抽象（事务边界 + SaveChanges）
// ─────────────────────────────────────────────────────────────
// 💡 为何放在 PalDDD.Core 项目中但使用 PalDDD.Core.Repository 命名空间？
//   ｜ IUnitOfWork 原属于独立项目 PalDDD.Repository，合并至 Core 后保留
//   ｜ 语义化命名空间以示与 Entity/AggregateRoot 的区别（持久化关注点）。
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Core.Repository;

// ─────────────────────────────────────────────────────────────
// 工作单元接口
// ─────────────────────────────────────────────────────────────

/// <summary>工作单元接口 — 事务管理 + SaveChanges</summary>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>当无活动事务时开启数据库事务。</summary>
    ValueTask BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>提交活动事务。</summary>
    ValueTask CommitAsync(CancellationToken ct = default);

    /// <summary>回滚活动事务。</summary>
    ValueTask RollbackAsync(CancellationToken ct = default);

    /// <summary>将挂起更改持久化到底层存储。</summary>
    ValueTask<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// IUnitOfWork 扩展方法 — 提供基于接口方法的组合操作。
/// ExecuteInTransactionAsync 是 Template Method 模式的标准实现，
/// 作为扩展方法而非接口方法，减少实现者负担，符合 ISP 原则。
/// </summary>
public static class UnitOfWorkExtensions
{
    /// <summary>
    /// 在数据库事务内执行 <paramref name="work"/>。<br/>
    /// 事务在 <paramref name="work"/> 执行前开启，在 <paramref name="work"/> 完成并调用
    /// <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/> 后提交。
    /// 若 <paramref name="work"/> 抛出异常，事务被回滚。
    /// </summary>
    public static async ValueTask ExecuteInTransactionAsync(
        this IUnitOfWork uow,
        Func<CancellationToken, ValueTask> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(work);

        await uow.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await work(ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            await uow.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            try { await uow.RollbackAsync(ct).ConfigureAwait(false); }
            catch (Exception rollbackEx)
            {
                throw new AggregateException(
                    "Transaction rollback failed after a business exception. See inner exceptions for both.",
                    original, rollbackEx);
            }
            throw;
        }
    }
}
