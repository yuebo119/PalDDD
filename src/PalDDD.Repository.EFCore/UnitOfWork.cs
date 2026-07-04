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
            await _context.Database.BeginTransactionAsync(ct);
    }

    /// <inheritdoc/>
    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        if (_context.Database.CurrentTransaction is not null)
            await _context.Database.CommitTransactionAsync(ct);
    }

    /// <inheritdoc/>
    public async ValueTask RollbackAsync(CancellationToken ct = default)
    {
        if (_context.Database.CurrentTransaction is not null)
            await _context.Database.RollbackTransactionAsync(ct);
    }

    /// <inheritdoc/>
    public async ValueTask<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_context.Database.CurrentTransaction is not null)
            await _context.Database.RollbackTransactionAsync();
    }
}
