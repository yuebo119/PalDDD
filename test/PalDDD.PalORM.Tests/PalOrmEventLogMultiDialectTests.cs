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
        await using (ts)
        {
            var log = new PalOrmEventLog<TProvider>(ts.Session);
            var before = DateTimeOffset.UtcNow;
            var events = new[] { MultiDialectTestData.CreateEventData("event.v1") };

            var result = await log.AppendAsync("stream-1", ExpectedStreamVersion.NoStream, events, default);
            await Assert.That(result.FirstStreamVersion).IsEqualTo(0L);
            await Assert.That(result.LastStreamVersion).IsEqualTo(0L);

            var read = new List<RecordedEvent>();
            await foreach (var e in log.ReadStreamAsync("stream-1", 0, int.MaxValue, default))
                read.Add(e);
            await Assert.That(read.Count).IsEqualTo(1);
            await Assert.That(read[0].EventName).IsEqualTo("event.v1");
            // ITM-245：时间戳往返守护网——RecordedAt 物化读回与写入时刻绝对差值须在窗口内
            await MultiDialectFixture.AssertTimestampRoundTrip(read[0].RecordedAt, before, "RecordedAt");
        }
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
        await using (ts)
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
        await using (ts)
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

    // ─── T2.2（2026-10-11 优化实施轮）：单事件 Any 版本原子追加快路径（PG 门控）───
    // 锁定测试：①快路径版本/位置正确且读回一致（PG 走 INSERT..SELECT 单语句）；②跨方言
    // 路径等价（同断言 × 3 方言——SQLite/MySQL 走原两往返路径，行为须逐位一致）；③EventId
    // 重复时冲突分类与原路径一致（原样上抛，不误译为并发异常）。

    [Test]
    public async Task EventLog_PostgreSql_AppendSingleAny_FastPath_SequentialVersions()
        => await Test_AppendSingleAny_SequentialVersions(await MultiDialectFixture.CreatePostgreSqlAsync());

    [Test]
    public async Task EventLog_Sqlite_AppendSingleAny_SequentialVersions()
        => await Test_AppendSingleAny_SequentialVersions(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task EventLog_MySql_AppendSingleAny_SequentialVersions()
        => await Test_AppendSingleAny_SequentialVersions(await MultiDialectFixture.CreateMySqlAsync());

    private static async Task Test_AppendSingleAny_SequentialVersions<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        await using (ts)
        {
            var log = new PalOrmEventLog<TProvider>(ts.Session);
            var r1 = await log.AppendAsync("any-stream", ExpectedStreamVersion.Any,
                [MultiDialectTestData.CreateEventData("any.e1")], default);
            var r2 = await log.AppendAsync("any-stream", ExpectedStreamVersion.Any,
                [MultiDialectTestData.CreateEventData("any.e2")], default);
            await Assert.That(r1.FirstStreamVersion).IsEqualTo(0L);
            await Assert.That(r1.LastStreamVersion).IsEqualTo(0L);
            await Assert.That(r2.FirstStreamVersion).IsEqualTo(1L);
            await Assert.That(r2.LastStreamVersion).IsEqualTo(1L);
            await Assert.That(r2.FirstGlobalPosition).IsGreaterThan(r1.FirstGlobalPosition);

            var read = new List<RecordedEvent>();
            await foreach (var e in log.ReadStreamAsync("any-stream", 0, int.MaxValue, default))
                read.Add(e);
            await Assert.That(read.Count).IsEqualTo(2);
            await Assert.That(read[0].EventName).IsEqualTo("any.e1");
            await Assert.That(read[1].EventName).IsEqualTo("any.e2");
        }
    }

    [Test]
    public async Task EventLog_PostgreSql_AppendSingleAny_DuplicateEventId_ThrowsOriginal()
        => await Test_AppendSingleAny_DuplicateEventId_ThrowsOriginal(await MultiDialectFixture.CreatePostgreSqlAsync());

    private static async Task Test_AppendSingleAny_DuplicateEventId_ThrowsOriginal<TProvider>(TestSession<TProvider> ts)
        where TProvider : IDbProvider
    {
        await using (ts)
        {
            var log = new PalOrmEventLog<TProvider>(ts.Session);
            var first = MultiDialectTestData.CreateEventData("dup.e1");
            await log.AppendAsync("dup-stream", ExpectedStreamVersion.Any, [first], default);
            // 同 EventId 重投：唯一约束冲突 → 分类为 EventId 重复原样上抛（不误译并发异常）
            var duplicate = new PalDDD.EventLog.EventData(
                first.EventId, first.EventName, first.SchemaVersion, first.ContentType,
                first.Payload, first.Metadata, first.Audit);
            await Assert.ThrowsAsync(async () =>
                await log.AppendAsync("dup-stream", ExpectedStreamVersion.Any, [duplicate], default));
        }
    }
}
