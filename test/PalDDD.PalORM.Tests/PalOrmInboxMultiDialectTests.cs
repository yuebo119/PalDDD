using PalORM;
using PalDDD.PalORM.Stores;
using PalDDD.Transactions;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Inbox Store 跨方言测试 —— 验证 PG/SQLite ON CONFLICT vs MySQL INSERT IGNORE 方言分叉。
/// </summary>
[TUnit.Core.NotInParallel("palorm-multidialect")]
public class PalOrmInboxMultiDialectTests
{
    [Test]
    public async Task Inbox_Sqlite_TryStartProcessing_FirstAttempt()
        => await Test_TryStartProcessing_FirstAttempt(
            await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Inbox_PostgreSql_TryStartProcessing_FirstAttempt()
        => await Test_TryStartProcessing_FirstAttempt(
            await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Inbox_MySql_TryStartProcessing_FirstAttempt()
        => await Test_TryStartProcessing_FirstAttempt(
            await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_TryStartProcessing_FirstAttempt<TProvider>(
        TestSession<TProvider> ts) where TProvider : IDbProvider
    {
        var store = new PalOrmInboxStore<TProvider>(ts.Session);
        var now = DateTimeOffset.UtcNow;
        var msg = await store.TryStartProcessingAsync("consumer-1", "msg-1", now, TimeSpan.FromMinutes(5), default);
        await Assert.That(msg).IsNotNull();
        await Assert.That(msg!.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(msg.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Inbox_Sqlite_TryStartProcessing_Duplicate()
        => await Test_TryStartProcessing_Duplicate(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Inbox_PostgreSql_TryStartProcessing_Duplicate()
        => await Test_TryStartProcessing_Duplicate(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Inbox_MySql_TryStartProcessing_Duplicate()
        => await Test_TryStartProcessing_Duplicate(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_TryStartProcessing_Duplicate<TProvider>(
        TestSession<TProvider> ts) where TProvider : IDbProvider
    {
        var store = new PalOrmInboxStore<TProvider>(ts.Session);
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryStartProcessingAsync("consumer-1", "msg-1", now, TimeSpan.FromMinutes(5), default);
        await Assert.That(first).IsNotNull();
        await store.MarkProcessedAsync(first!, now, default);

        var second = await store.TryStartProcessingAsync("consumer-1", "msg-1", now.AddSeconds(1), TimeSpan.FromMinutes(5), default);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task Inbox_Sqlite_MarkFailed_ThenRetry()
        => await Test_MarkFailed_ThenRetry(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task Inbox_PostgreSql_MarkFailed_ThenRetry()
        => await Test_MarkFailed_ThenRetry(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task Inbox_MySql_MarkFailed_ThenRetry()
        => await Test_MarkFailed_ThenRetry(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_MarkFailed_ThenRetry<TProvider>(
        TestSession<TProvider> ts) where TProvider : IDbProvider
    {
        var store = new PalOrmInboxStore<TProvider>(ts.Session);
        var now = DateTimeOffset.UtcNow;

        var msg = await store.TryStartProcessingAsync("consumer-1", "msg-1", now, TimeSpan.FromMinutes(5), default);
        await store.MarkFailedAsync(msg!, "transient error", default);

        var retry = await store.TryStartProcessingAsync("consumer-1", "msg-1", now.AddSeconds(1), TimeSpan.FromMinutes(5), default);
        await Assert.That(retry).IsNotNull();
        await Assert.That(retry!.Attempts).IsEqualTo(2);
    }
}
