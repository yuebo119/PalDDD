using PalORM;

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
        await using (ts)
        {
            await using var uow = new PalOrmUnitOfWork<TProvider>(ts.Session);
            await uow.BeginTransactionAsync();
            var before = DateTimeOffset.UtcNow;
            await ts.Session.ExecuteAsync($"INSERT INTO outbox_messages (id, type, payload, content_type, schema_version, status, retry_count, created_at) VALUES ({ByteAether.Ulid.Ulid.New().ToString()}, {"tx.commit"}, {System.Text.Encoding.UTF8.GetBytes("[]")}, {"application/json"}, {1}, {0}, {0}, {before})", default);
            await uow.CommitAsync();

            var count = await ts.Session.ScalarAsync<long>($"SELECT COUNT(*) FROM outbox_messages");
            await Assert.That(count).IsEqualTo(1L);

            // ITM-245：时间戳往返守护网——Commit 后读回 created_at 物化值与写入时刻绝对差值须在窗口内。
            // ScalarAsync 底层物化按方言不同（SQLite=string / PG=DateTimeOffset / MySQL=DateTime），
            // 统一转 DateTimeOffset 后断言。
            var raw = await ts.Session.ScalarAsync<object>($"SELECT created_at FROM outbox_messages");
            var roundTripped = raw switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
                string s => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"created_at 物化类型未预期：{raw?.GetType().Name ?? "null"}"),
            };
            await MultiDialectFixture.AssertTimestampRoundTrip(roundTripped, before, "created_at");
        }
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
        await using (ts)
        {
            await using var uow = new PalOrmUnitOfWork<TProvider>(ts.Session);
            await uow.BeginTransactionAsync();
            await ts.Session.ExecuteAsync($"INSERT INTO outbox_messages (id, type, payload, content_type, schema_version, status, retry_count, created_at) VALUES ({ByteAether.Ulid.Ulid.New().ToString()}, {"tx.rollback"}, {System.Text.Encoding.UTF8.GetBytes("[]")}, {"application/json"}, {1}, {0}, {0}, {DateTimeOffset.UtcNow})", default);
            await uow.RollbackAsync();

            var count = await ts.Session.ScalarAsync<long>($"SELECT COUNT(*) FROM outbox_messages");
            await Assert.That(count).IsEqualTo(0L);
        }
    }
}
