using System.Data.Common;
using PalDDD.Core.Repository;
using PalORM;

namespace PalDDD.PalORM;

/// <summary>
/// UnitOfWork 的 PalORM 实现 —— 双泛型核心基类（包装 <see cref="DataSession{TProvider}"/> 事务）。
/// <para>
/// <b>事务自动传播</b>：BeginTransactionAsync 调 <see cref="DataSession{TProvider}"/>.<c>BeginTransactionAsync</c>
/// 后，<c>DataSession</c> 内部 <c>OperationState.PublishTransaction</c>；
/// 后续所有 Store 的 ExecuteAsync/InsertAsync 经 <c>CreateCommand</c> 自动附加 <c>GetActiveTransaction()</c>，
/// 无需 Store 显式接收 transaction 参数。
/// </para>
/// <para>
/// <b>单 Scoped 共享</b>：所有 Store 注入同一 Scoped <c>DataSession&lt;TProvider&gt;</c>，
/// UnitOfWork 与 Store 共享此实例 —— 跨 Store 事务天然生效。
/// </para>
/// <para>
/// <b>注意</b>：PalORM <c>DataSession</c> 在构造时已 Open 连接（CreateAsync 同步打开），
/// 故本实现无需 AutoOpen 逻辑（与 Dapper 实现的 "BeginTransaction 时 Open 连接" 不同）。
/// </para>
/// </summary>
public class PalOrmUnitOfWork<TProvider> : IUnitOfWork
    where TProvider : IDbProvider
{
    private readonly DataSession<TProvider> _session;
    private DbTransaction? _transaction;
    private bool _disposed;

    /// <summary>构造 UnitOfWork。</summary>
    public PalOrmUnitOfWork(DataSession<TProvider> session) => _session = session;

    /// <inheritdoc />
    public async ValueTask BeginTransactionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transaction is not null) return;  // 幂等：已有活动事务

        // PalORM DataSession 已 Open 连接（构造时打开），BeginTransactionAsync 直接 begin
        _transaction = await _session.BeginTransactionAsync(ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<int> SaveChangesAsync(CancellationToken ct = default)
        => new(0);  // 即时执行模式 —— 无 ChangeTracker（与 Dapper 实现语义一致）

    /// <inheritdoc />
    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transaction is null) return;

        try
        {
            await _transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // ITM-087 修复：Commit 成功或失败都清理事务引用并解绑 DataSession——
            // 原失败路径遗留 _transaction 与非 null 的 UseTransaction 绑定，后续 RollbackAsync
            // 会二次操作已失效事务、以新异常掩盖根因。finally 保证任何路径都清理；
            // DisposeAsync 的幂等异常（已失效/已释放）按文件既有惯例吞掉，保留根因异常。
            try { await _transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* 事务已失效/已释放 */ }
            _transaction = null;
            // 清除 DataSession 内部的事务引用（PalORM 要求 Commit/Rollback 后显式清空）
            _session.UseTransaction(null);
        }
    }

    /// <inheritdoc />
    public async ValueTask RollbackAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transaction is null) return;

        try
        {
            await _transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // 三十五轮 C1（ITM-131 姊妹对齐）：回滚失败也必须清理引用并解绑 DataSession——
            // 原实现回滚抛出（连接故障/已取消）时悬挂 _transaction 与 UseTransaction 绑定，
            // 后续 DisposeAsync 二次操作已失效事务以新异常掩盖根因（对齐 CommitAsync 的 ITM-087 finally 模式）。
            try { await _transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* 事务已失效/已释放 */ }
            _transaction = null;
            _session.UseTransaction(null);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);

        if (_transaction is not null)
        {
            try { await _transaction.RollbackAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* 事务已提交/回滚 */ }
            // 三十七轮 A4：对齐 CommitAsync/RollbackAsync 的 DisposeAsync 幂等 catch——三处同类两处有防护的不对称
            try { await _transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* 已释放/连接断 */ }
            _transaction = null;
            _session.UseTransaction(null);
        }
    }
}
