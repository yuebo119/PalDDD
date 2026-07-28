using PalDDD.PalORM.Stores;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalDDD.PalORM.MySql;
using PalDDD.PalORM.PostgreSql;
using PalDDD.PalORM.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace PalDDD.PalORM.Tests;

/// <summary>UnitOfWork 跨方言测试 —— 验证三方言的事务 Commit/Rollback。</summary>
[TUnit.Core.NotInParallel("palorm-multidialect")]
public class PalOrmUnitOfWorkMultiDialectTests
{
    [Test]
    public async Task UoW_Sqlite_Commit_PersistsChanges()
        => await Test_Commit_PersistsChanges(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task UoW_PostgreSql_Commit_PersistsChanges()
        => await Test_Commit_PersistsChanges(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task UoW_MySql_Commit_PersistsChanges()
        => await Test_Commit_PersistsChanges(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_Commit_PersistsChanges<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        await using var uow = new PalOrmUnitOfWork<TProvider>(ts.Session);
        await uow.BeginTransactionAsync();
        await ts.Session.ExecuteAsync($"INSERT INTO outbox_messages (id, type, payload, content_type, schema_version, status, retry_count, created_at) VALUES ({ByteAether.Ulid.Ulid.New().ToString()}, {"tx.commit"}, {"[]"}, {"application/json"}, {1}, {0}, {0}, {DateTimeOffset.UtcNow})", default);
        await uow.CommitAsync();

        var count = await ts.Session.ScalarAsync<long>($"SELECT COUNT(*) FROM outbox_messages");
        await Assert.That(count).IsEqualTo(1L);
    }

    [Test]
    public async Task UoW_Sqlite_Rollback_DiscardsChanges()
        => await Test_Rollback_DiscardsChanges(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task UoW_PostgreSql_Rollback_DiscardsChanges()
        => await Test_Rollback_DiscardsChanges(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task UoW_MySql_Rollback_DiscardsChanges()
        => await Test_Rollback_DiscardsChanges(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_Rollback_DiscardsChanges<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        await using var uow = new PalOrmUnitOfWork<TProvider>(ts.Session);
        await uow.BeginTransactionAsync();
        await ts.Session.ExecuteAsync($"INSERT INTO outbox_messages (id, type, payload, content_type, schema_version, status, retry_count, created_at) VALUES ({ByteAether.Ulid.Ulid.New().ToString()}, {"tx.rollback"}, {"[]"}, {"application/json"}, {1}, {0}, {0}, {DateTimeOffset.UtcNow})", default);
        await uow.RollbackAsync();

        var count = await ts.Session.ScalarAsync<long>($"SELECT COUNT(*) FROM outbox_messages");
        await Assert.That(count).IsEqualTo(0L);
    }
}
