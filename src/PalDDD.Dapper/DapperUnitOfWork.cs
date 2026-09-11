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
        // ITM-165 修复：Dispose 后 Begin 应抛 ObjectDisposedException（对齐 PalOrmUnitOfWork），
        // 避免在已释放的 UnitOfWork 上开启新事务。
        ObjectDisposedException.ThrowIf(_disposed, this);

        // ITM-088 修复：事务已激活时再次 Begin 会覆盖旧 _transaction 引用（旧事务未处置即丢失）——
        // 前置判活明确抛错，调用方应先 CommitAsync/RollbackAsync 结束当前事务。
        if (_transaction is not null)
            throw new InvalidOperationException(
                "BeginTransactionAsync 在事务已激活时被再次调用——请先 CommitAsync/RollbackAsync 结束当前事务。");

        if (_connection.State != System.Data.ConnectionState.Open)
            await _connection.OpenAsync(ct).ConfigureAwait(false);
        _transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // 二轮评审 T5：活动事务登记到 ambient 通道——同连接的 DI 解析 Store
        // （Outbox/Inbox/SagaState/Checkpoint/EventLog）执行时自动挂接，
        // 消除"DI Store 恒无事务"的构造时序断链（见 DapperAmbientTransaction）。
        DapperAmbientTransaction.Set(_connection, _transaction);
    }

    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        // ITM-165 姊妹收口：Dispose 后 Commit 抛 ObjectDisposedException（对齐 PalOrmUnitOfWork.
        // CommitAsync 首行守卫）——原静默 return 把"已释放后提交"伪装成成功落库；
        // 未 Begin 事务的幂等 no-op 语义不受影响（下方 null 判定保留，测试
        // CommitAsync_WithoutBegin_IsNoOp 锁定）。全仓调用方（IUnitOfWork 扩展
        // ExecuteInTransactionAsync 等）均在 Dispose 前提交，无静默语义依赖。
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transaction is null)
            return;

        var transaction = _transaction;
        try
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // ITM-131 修复：Commit 成功或失败都清理事务引用并 Dispose——原失败路径遗留
            // _transaction 非 null 且不 Dispose，作用域释放时 DisposeAsync 对失效事务再 Rollback
            // 会以新异常覆盖原始异常。Dispose 的幂等异常按 PalOrmUnitOfWork 惯例吞掉，保留根因。
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* 事务已失效/已释放 */ }
            _transaction = null;
            DapperAmbientTransaction.Set(_connection, null);
        }
    }

    /// <summary>Dapper 即时执行——SaveChanges 是幂等 no-op</summary>
    public ValueTask<int> SaveChangesAsync(CancellationToken ct = default) => ValueTask.FromResult(0);

    public async ValueTask RollbackAsync(CancellationToken ct = default)
    {
        // ITM-165 姊妹收口（第三轮残余）：Dispose 后 Rollback 同样抛 ObjectDisposedException
        // （对齐 PalOrmUnitOfWork.RollbackAsync:88 与本类 CommitAsync/BEGIN 守卫）——原静默
        // return 使"已释放后回滚"不可见；调用方持残留引用误以为已回滚（实际 DisposeAsync
        // 路径已回滚过或事务根本未激活）。no-op 契约仅保留给"未 Begin"路径（_transaction null）。
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transaction is null)
            return;

        var transaction = _transaction;
        try
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // ITM-131 修复：Rollback 失败同样清理事务引用并 Dispose，防止 DisposeAsync 二次回滚覆盖根因。
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* 事务已失效/已释放 */ }
            _transaction = null;
            DapperAmbientTransaction.Set(_connection, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // ITM-131 修复：_disposed 先置位——原实现回滚/释放失败时 _disposed 仍为 false，
        // 二次 Dispose 会对失效事务再次 Rollback；先置位保证 Dispose 可重入且根因不被覆盖。
        _disposed = true;

        if (_transaction is not null)
        {
            var transaction = _transaction;
            try { await transaction.RollbackAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* 事务已提交/回滚 */ }
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* 事务已失效/已释放 */ }
            _transaction = null;
            DapperAmbientTransaction.Set(_connection, null);
        }
    }
}
