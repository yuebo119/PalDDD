// ─────────────────────────────────────────────────────────────
// 🔄 UnitOfWork<TContext> — EF Core 工作单元实现
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using PalDDD.Core.Repository;

namespace PalDDD.Repository.EFCore;

// ─────────────────────────────────────────────────────────────
// EF Core 工作单元实现
// ─────────────────────────────────────────────────────────────

/// <summary>工作单元 EF Core 默认实现 — 事务管理 + SaveChanges</summary>
/// <typeparam name="TContext">EF Core DbContext 类型</typeparam>
/// <remarks>
/// 与 DbContext 同生命周期（通常为 Scoped）。<br/>
/// 只封装事务边界和 SaveChanges；查询与聚合持久化应由应用层直接使用 DbContext 或显式业务仓储。
/// </remarks>
public sealed class UnitOfWork<TContext> : IUnitOfWork
    where TContext : DbContext
{
    private readonly TContext _context;
    private bool _disposed;

    public UnitOfWork(TContext context) => _context = context;

    /// <inheritdoc/>
    public async ValueTask BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_context.Database.CurrentTransaction is null)
            await _context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        if (_context.Database.CurrentTransaction is not null)
            await _context.Database.CommitTransactionAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask RollbackAsync(CancellationToken ct = default)
    {
        if (_context.Database.CurrentTransaction is not null)
            await _context.Database.RollbackTransactionAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // 三十五轮 C2（ITM-131 姊妹对齐）：停机路径回滚 best-effort——
        // 连接已断/事务悬挂/上下文先释放时 Rollback 或 Database 访问抛出会从 Dispose 逃逸，
        // 在容器 teardown 中覆盖其他 scope 的真实根因异常（对齐 DapperUnitOfWork.DisposeAsync 过滤模式）。
        try
        {
            if (_context.Database.CurrentTransaction is not null)
                await _context.Database.RollbackTransactionAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or ObjectDisposedException
            or System.Data.Common.DbException)
        {
            // 上下文已释放/事务已失效/连接已断——Dispose 路径吞掉，保留真正根因
        }
    }
}
