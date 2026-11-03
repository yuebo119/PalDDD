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
    /// <summary>开启数据库事务。<b>事务已活动时抛 <see cref="InvalidOperationException"/></b>
    /// （ADR-023：三栈统一为 fail-fast，不支持嵌套）。</summary>
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
    // 两个重载（无返回值 / 泛型返回值）共用回滚失败消息——单一出处防措辞漂移。
    private const string RollbackFailureMessage =
        "Transaction rollback failed after a business exception. See inner exceptions for both.";

    /// <summary>
    /// 在数据库事务内执行 <paramref name="work"/>。<br/>
    /// 事务在 <paramref name="work"/> 执行前开启，在 <paramref name="work"/> 完成并调用
    /// <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/> 后提交。
    /// 若 <paramref name="work"/> 抛出异常，事务被回滚。
    /// <para>
    /// ⚠️ <b>不支持嵌套（ADR-023）</b>：本方法无条件「开启 → 提交」，故在已活动事务内调用会经
    /// <see cref="IUnitOfWork.BeginTransactionAsync(CancellationToken)"/> 抛出
    /// <see cref="InvalidOperationException"/>（三栈一致）。需要在既有事务内执行工作请不要用本方法——
    /// 直接执行 <paramref name="work"/>，或由调用方自行编排提交边界。
    /// </para>
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
            try { await uow.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackEx)
            {
                throw new AggregateException(RollbackFailureMessage, original, rollbackEx);
            }
            throw;
        }
    }

    /// <summary>
    /// 在数据库事务内执行 <paramref name="work"/> 并返回其结果。<br/>
    /// 事务在 <paramref name="work"/> 执行前开启，在 <paramref name="work"/> 完成并调用
    /// <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/> 后提交；异常则回滚。
    /// </summary>
    /// <typeparam name="T"><paramref name="work"/> 的返回值类型（命令结果、新建聚合 ID 等）。</typeparam>
    /// <remarks>
    /// 本重载只消除「先声明结果变量、再在委托内赋值」的样板，事务语义、回滚失败聚合规则与
    /// 无返回值重载完全相同，两者改动须双侧同步。
    /// <para>⚠️ <b>不支持嵌套（ADR-023）</b>：与无返回值重载一致——已活动事务内调用会经
    /// <see cref="IUnitOfWork.BeginTransactionAsync(CancellationToken)"/> 抛出
    /// <see cref="InvalidOperationException"/>（三栈一致）。</para>
    /// </remarks>
    public static async ValueTask<T> ExecuteInTransactionAsync<T>(
        this IUnitOfWork uow,
        Func<CancellationToken, ValueTask<T>> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(work);

        await uow.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await work(ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            await uow.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception original)
        {
            try { await uow.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackEx)
            {
                throw new AggregateException(RollbackFailureMessage, original, rollbackEx);
            }
            throw;
        }
    }
}
