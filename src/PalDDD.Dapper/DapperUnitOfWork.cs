// ─────────────────────────────────────────────────────────────
// 🔄 DapperUnitOfWork — Dapper 工作单元（✅ AOT 安全）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ 仅依赖 DbConnection/DbTransaction — ADO.NET 原生类型，零反射。
//   ✅ SaveChanges 是 no-op — Dapper 即时执行，不需要 ChangeTracker。
//   ✅ 无 ORM 映射 — 纯手写 SQL，Dapper 只做参数绑定 + 物化。
// ─────────────────────────────────────────────────────────────

using PalDDD.Core.Repository;
using System.Data.Common;

namespace PalDDD.Dapper;

/// <summary>Dapper 工作单元 — 封装 DbTransaction 生命周期</summary>
public sealed class DapperUnitOfWork : IUnitOfWork
{
    private readonly DbConnection _connection;
    private DbTransaction? _transaction;
    private bool _disposed;

    /// <summary>构造 Dapper UnitOfWork</summary>
    /// <remarks>
    /// 同一 DbConnection 可以被多个 Dapper Store 共享（OutboxStore/InboxStore/SagaStore）。
    /// 通过构造函数注入同一 DbTransaction，所有 Store 的操作在同一事务中。
    /// </remarks>
    public DapperUnitOfWork(DbConnection connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>获取当前事务——可传递给各个 Dapper Store 的构造函数</summary>
    public DbTransaction? Transaction => _transaction;

    public async ValueTask BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
            await _connection.OpenAsync(ct).ConfigureAwait(false);
        _transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(ct).ConfigureAwait(false);
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }

    /// <summary>Dapper 即时执行——SaveChanges 是幂等 no-op</summary>
    public ValueTask<int> SaveChangesAsync(CancellationToken ct = default) => ValueTask.FromResult(0);

    public async ValueTask RollbackAsync(CancellationToken ct = default)
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync(ct).ConfigureAwait(false);
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (_transaction is not null)
            {
                await _transaction.RollbackAsync().ConfigureAwait(false);
                await _transaction.DisposeAsync().ConfigureAwait(false);
                _transaction = null;
            }
            _disposed = true;
        }
    }
}
