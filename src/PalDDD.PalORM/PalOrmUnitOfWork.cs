using System.Data;
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
        _transaction = await _session.BeginTransactionAsync(ct: ct);
    }

    /// <inheritdoc />
    public ValueTask<int> SaveChangesAsync(CancellationToken ct = default)
        => new(0);  // 即时执行模式 —— 无 ChangeTracker（与 Dapper 实现语义一致）

    /// <inheritdoc />
    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transaction is null) return;  // 无活动事务，幂等 no-op

        await _transaction.CommitAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    /// <inheritdoc />
    public async ValueTask RollbackAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transaction is null) return;  // 无活动事务，幂等 no-op

        await _transaction.RollbackAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);  // CA1816: IAsyncDisposable 模式要求

        // 未提交的事务自动回滚（与 DapperUnitOfWork.DisposeAsync 一致）
        if (_transaction is not null)
        {
            try { await _transaction.RollbackAsync(); }
            catch (InvalidOperationException) { /* 事务已提交/回滚 */ }
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }
}
