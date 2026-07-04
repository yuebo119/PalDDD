using Microsoft.Data.Sqlite;
using PalDDD.Dapper;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 🔄 DapperUnitOfWork 测试 — 事务边界 + 连接生命周期 + Dispose
// ═══════════════════════════════════════════════════════════════
// DapperUnitOfWork 封装 DbTransaction 生命周期，是 Dapper 适配器的核心。
// 用 SQLite in-memory 验证真实事务语义（BeginTransaction/Commit/Rollback）。
// ═══════════════════════════════════════════════════════════════

public sealed class DapperUnitOfWorkTests
{
    private SqliteConnection _connection = null!;

    [Before(Test)]
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(cancellationToken);
    }

    [After(Test)]
    public async Task CleanupAsync() => await _connection.DisposeAsync();

    [Test]
    public async Task Constructor_NullConnection_Throws()
    {
        await Assert.That(() => new DapperUnitOfWork(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task BeginTransactionAsync_CreatesTransaction(CancellationToken cancellationToken)
    {
        await using var uow = new DapperUnitOfWork(_connection);
        await uow.BeginTransactionAsync(cancellationToken);

        await Assert.That(uow.Transaction).IsNotNull();
    }

    [Test]
    public async Task CommitAsync_CommitsAndClearsTransaction(CancellationToken cancellationToken)
    {
        await using var uow = new DapperUnitOfWork(_connection);
        await uow.BeginTransactionAsync(cancellationToken);
        await Assert.That(uow.Transaction).IsNotNull();

        await uow.CommitAsync(cancellationToken);

        await Assert.That(uow.Transaction).IsNull();
    }

    [Test]
    public async Task CommitAsync_WithoutBegin_IsNoOp(CancellationToken cancellationToken)
    {
        await using var uow = new DapperUnitOfWork(_connection);
        // 未开始事务直接 Commit — 不应抛异常
        await uow.CommitAsync(cancellationToken);
        await Assert.That(uow.Transaction).IsNull();
    }

    [Test]
    public async Task RollbackAsync_RollsBackAndClearsTransaction(CancellationToken cancellationToken)
    {
        await using var uow = new DapperUnitOfWork(_connection);
        await uow.BeginTransactionAsync(cancellationToken);

        await uow.RollbackAsync(cancellationToken);

        await Assert.That(uow.Transaction).IsNull();
    }

    [Test]
    public async Task RollbackAsync_WithoutBegin_IsNoOp(CancellationToken cancellationToken)
    {
        await using var uow = new DapperUnitOfWork(_connection);
        await uow.RollbackAsync(cancellationToken);
        await Assert.That(uow.Transaction).IsNull();
    }

    [Test]
    public async Task SaveChangesAsync_IsNoOp_ReturnsZero(CancellationToken cancellationToken)
    {
        await using var uow = new DapperUnitOfWork(_connection);
        var result = await uow.SaveChangesAsync(cancellationToken);
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeAsync_RollsBackPendingTransaction(CancellationToken cancellationToken)
    {
        var uow = new DapperUnitOfWork(_connection);
        await uow.BeginTransactionAsync(cancellationToken);
        await Assert.That(uow.Transaction).IsNotNull();

        await uow.DisposeAsync();

        // Dispose 后 Transaction 应为 null（已回滚并释放）
        await Assert.That(uow.Transaction).IsNull();
    }

    [Test]
    public async Task DisposeAsync_IsIdempotent()
    {
        var uow = new DapperUnitOfWork(_connection);
        await uow.DisposeAsync();
        // 二次 Dispose 不应抛异常
        await uow.DisposeAsync();
    }

    [Test]
    public async Task BeginTransactionAsync_OpensClosedConnection(CancellationToken cancellationToken)
    {
        // 用新连接验证自动打开逻辑
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await using var uow = new DapperUnitOfWork(conn);

        // 连接初始关闭
        await Assert.That(conn.State).IsEqualTo(System.Data.ConnectionState.Closed);

        await uow.BeginTransactionAsync(cancellationToken);

        // BeginTransactionAsync 应自动打开连接
        await Assert.That(conn.State).IsEqualTo(System.Data.ConnectionState.Open);
        await Assert.That(uow.Transaction).IsNotNull();
    }
}
