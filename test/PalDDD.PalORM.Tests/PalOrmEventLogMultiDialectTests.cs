using PalDDD.PalORM.Stores;
using PalORM;
using PalDDD.EventLog;

namespace PalDDD.PalORM.Tests;

/// <summary>EventLog Store 跨方言测试 —— 验证 PascalCase 列 + GlobalPosition 自增 + StreamVersion 乐观并发。</summary>
[TUnit.Core.NotInParallel("palorm-multidialect")]
public class PalOrmEventLogMultiDialectTests
{
    [Test]
    public async Task EventLog_Sqlite_AppendNoStream_ThenReadStream()
        => await Test_AppendNoStream_ThenReadStream(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task EventLog_PostgreSql_AppendNoStream_ThenReadStream()
        => await Test_AppendNoStream_ThenReadStream(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task EventLog_MySql_AppendNoStream_ThenReadStream()
        => await Test_AppendNoStream_ThenReadStream(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_AppendNoStream_ThenReadStream<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var log = new PalOrmEventLog<TProvider>(ts.Session);
        var events = new[] { MultiDialectTestData.CreateEventData("event.v1") };

        var result = await log.AppendAsync("stream-1", ExpectedStreamVersion.NoStream, events, default);
        await Assert.That(result.FirstStreamVersion).IsEqualTo(0L);
        await Assert.That(result.LastStreamVersion).IsEqualTo(0L);

        var read = new List<RecordedEvent>();
        await foreach (var e in log.ReadStreamAsync("stream-1", 0, int.MaxValue, default))
            read.Add(e);
        await Assert.That(read.Count).IsEqualTo(1);
        await Assert.That(read[0].EventName).IsEqualTo("event.v1");
    }

    [Test]
    public async Task EventLog_Sqlite_AppendMultiple_SequentialVersions()
        => await Test_AppendMultiple_SequentialVersions(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task EventLog_PostgreSql_AppendMultiple_SequentialVersions()
        => await Test_AppendMultiple_SequentialVersions(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task EventLog_MySql_AppendMultiple_SequentialVersions()
        => await Test_AppendMultiple_SequentialVersions(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_AppendMultiple_SequentialVersions<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var log = new PalOrmEventLog<TProvider>(ts.Session);
        var events = new[]
        {
            MultiDialectTestData.CreateEventData("event.a"),
            MultiDialectTestData.CreateEventData("event.b"),
            MultiDialectTestData.CreateEventData("event.c"),
        };

        var result = await log.AppendAsync("multi-stream", ExpectedStreamVersion.NoStream, events, default);
        await Assert.That(result.FirstStreamVersion).IsEqualTo(0L);
        await Assert.That(result.LastStreamVersion).IsEqualTo(2L);

        var read = new List<RecordedEvent>();
        await foreach (var e in log.ReadStreamAsync("multi-stream", 0, int.MaxValue, default))
            read.Add(e);
        await Assert.That(read.Count).IsEqualTo(3);
        await Assert.That(read[0].EventName).IsEqualTo("event.a");
        await Assert.That(read[2].EventName).IsEqualTo("event.c");
    }

    [Test]
    public async Task EventLog_Sqlite_ReadAll_GlobalOrder()
        => await Test_ReadAll_GlobalOrder(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task EventLog_PostgreSql_ReadAll_GlobalOrder()
        => await Test_ReadAll_GlobalOrder(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task EventLog_MySql_ReadAll_GlobalOrder()
        => await Test_ReadAll_GlobalOrder(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_ReadAll_GlobalOrder<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        var log = new PalOrmEventLog<TProvider>(ts.Session);
        await log.AppendAsync("s1", ExpectedStreamVersion.NoStream, [MultiDialectTestData.CreateEventData("s1.e1")], default);
        await log.AppendAsync("s2", ExpectedStreamVersion.NoStream, [MultiDialectTestData.CreateEventData("s2.e1")], default);

        var all = new List<RecordedEvent>();
        await foreach (var e in log.ReadAllAsync(0, int.MaxValue, default))
            all.Add(e);
        await Assert.That(all.Count).IsEqualTo(2);
        await Assert.That(all[0].GlobalPosition).IsEqualTo(1L);
        await Assert.That(all[1].GlobalPosition).IsEqualTo(2L);
        await Assert.That(all[0].StreamName).IsEqualTo("s1");
        await Assert.That(all[1].StreamName).IsEqualTo("s2");
        await Assert.That(all[0].EventName).IsEqualTo("s1.e1");
        await Assert.That(all[1].EventName).IsEqualTo("s2.e1");
    }
}
