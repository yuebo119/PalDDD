using PalDDD.Core.Repository;
using PalDDD.PalORM.Sqlite;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// UnitOfWork 测试 —— 迁移自 DapperUnitOfWorkTests.cs。
/// <para>关键差异：PalORM DataSession 在构造时已 Open（无需 AutoOpen）。</para>
/// </summary>
public class PalOrmUnitOfWorkTests
{
    [Test]
    public async Task BeginTransactionAsync_CreatesTransaction()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        await using var uow = new SqlitePalOrmUnitOfWork(session);
        await uow.BeginTransactionAsync();

        // 写入必须落在事务内：回滚后数据不存在，才证明 BeginTransactionAsync 真正创建了事务。
        await session.ExecuteAsync($"INSERT INTO outbox_messages (id, type, payload, created_at) VALUES ({"begin-tx"}, {"tx.begin"}, {"[]"}, {DateTimeOffset.UtcNow})");
        await uow.RollbackAsync();

        var count = await session.ScalarAsync<long>($"SELECT COUNT(*) FROM outbox_messages");
        await Assert.That(count).IsEqualTo(0L);
    }

    [Test]
    public async Task CommitAsync_WithoutBegin_IsNoOp()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        await using var uow = new SqlitePalOrmUnitOfWork(session);
        // 无 Begin 直接 Commit 不抛
        await Assert.That(async () => await uow.CommitAsync()).ThrowsNothing();
    }

    [Test]
    public async Task RollbackAsync_WithoutBegin_IsNoOp()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        await using var uow = new SqlitePalOrmUnitOfWork(session);
        await Assert.That(async () => await uow.RollbackAsync()).ThrowsNothing();
    }

    [Test]
    public async Task SaveChangesAsync_IsNoOp_ReturnsZero()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        await using var uow = new SqlitePalOrmUnitOfWork(session);
        var result = await uow.SaveChangesAsync();
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeAsync_IsIdempotent()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var uow = new SqlitePalOrmUnitOfWork(session);
        await uow.DisposeAsync();
        // 二次 Dispose 不抛
        await Assert.That(async () => await uow.DisposeAsync()).ThrowsNothing();
    }

    [Test]
    public async Task ExecuteInTransactionAsync_CommitsOnSuccess()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        await using var uow = new SqlitePalOrmUnitOfWork(session);
        var executed = false;
        await uow.ExecuteInTransactionAsync(async ct =>
        {
            executed = true;
            // 委托内写入必须随 Commit 落库——只断言 executed 无法区分"提交"与"未提交"，
            // 这里在事务内插入一行，提交后回读计数证明事务真正提交。
            await session.ExecuteAsync(
                $"INSERT INTO outbox_messages (id, type, payload, created_at) VALUES ({"commit-tx"}, {"tx.commit"}, {"[]"}, {DateTimeOffset.UtcNow})",
                ct);
        }, default);

        await Assert.That(executed).IsTrue();
        var count = await session.ScalarAsync<long>($"SELECT COUNT(*) FROM outbox_messages");
        await Assert.That(count).IsEqualTo(1L);
    }
}
