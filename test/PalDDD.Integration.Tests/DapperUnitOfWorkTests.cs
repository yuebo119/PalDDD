using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
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

    // ───────────────────────────────────────────────────────────
    // ITM-131/ITM-165 回归测试 — 失败路径事务清理 + Dispose 后 Begin 守卫
    // 用轻量 fake DbTransaction 子类模拟 Commit/Rollback 抛异常（不引入 Moq）。
    // ───────────────────────────────────────────────────────────

    [Test]
    public async Task BeginTransactionAsync_AfterDispose_Throws()
    {
        var uow = new DapperUnitOfWork(_connection);
        await uow.DisposeAsync();

        await Assert.That(async () => await uow.BeginTransactionAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task CommitAsync_WhenCommitThrows_DisposesAndClearsTransaction()
    {
        var tx = new ThrowingDbTransaction();
        await using var conn = new ThrowingDbConnection(tx);
        var uow = new DapperUnitOfWork(conn);
        await uow.BeginTransactionAsync();

        await Assert.That(async () => await uow.CommitAsync()).Throws<InvalidOperationException>();

        // ITM-131：Commit 失败后事务引用必须清空且已 Dispose，否则 DisposeAsync 会二次回滚覆盖根因
        await Assert.That(uow.Transaction).IsNull();
        await Assert.That(tx.DisposeAsyncCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task RollbackAsync_WhenRollbackThrows_DisposesAndClearsTransaction()
    {
        var tx = new ThrowingDbTransaction();
        await using var conn = new ThrowingDbConnection(tx);
        var uow = new DapperUnitOfWork(conn);
        await uow.BeginTransactionAsync();

        await Assert.That(async () => await uow.RollbackAsync()).Throws<InvalidOperationException>();

        // ITM-131：Rollback 失败后事务引用必须清空且已 Dispose
        await Assert.That(uow.Transaction).IsNull();
        await Assert.That(tx.DisposeAsyncCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_WhenRollbackThrows_IsIdempotentAndDisposes()
    {
        var tx = new ThrowingDbTransaction();
        await using var conn = new ThrowingDbConnection(tx);
        var uow = new DapperUnitOfWork(conn);
        await uow.BeginTransactionAsync();

        // ITM-131：DisposeAsync 过滤已失效事务的 Rollback 异常，不应外抛
        await uow.DisposeAsync();

        await Assert.That(uow.Transaction).IsNull();
        await Assert.That(tx.RollbackAsyncCallCount).IsEqualTo(1);
        await Assert.That(tx.DisposeAsyncCallCount).IsEqualTo(1);

        // _disposed 已置位：二次 Dispose 不再触碰事务（可重入）
        await uow.DisposeAsync();
        await Assert.That(tx.RollbackAsyncCallCount).IsEqualTo(1);
        await Assert.That(tx.DisposeAsyncCallCount).IsEqualTo(1);
    }

    private sealed class ThrowingDbConnection : DbConnection
    {
        private readonly DbTransaction _transaction;

        public ThrowingDbConnection(DbTransaction transaction) => _transaction = transaction;

        [AllowNull]
        public override string ConnectionString
        {
            get => "";
            set => _ = value;
        }
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "0";
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => _transaction;
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class ThrowingDbTransaction : DbTransaction
    {
        public int RollbackAsyncCallCount { get; private set; }
        public int DisposeAsyncCallCount { get; private set; }

        public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
        protected override DbConnection DbConnection => null!;

        public override void Commit() => throw new InvalidOperationException("Commit failed");
        public override void Rollback() => throw new InvalidOperationException("Rollback failed");

        public override Task CommitAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Commit failed");

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            RollbackAsyncCallCount++;
            throw new InvalidOperationException("Rollback failed");
        }

        public override ValueTask DisposeAsync()
        {
            DisposeAsyncCallCount++;
            return base.DisposeAsync();
        }
    }
}
