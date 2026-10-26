using System.Runtime.CompilerServices;
using PalDDD.PalORM.Stores;
using PalORM;
using PalORM.Sqlite;
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
    public async Task EventLog_Sqlite_AppendLargeBatch_ChunksAboveParameterLimit()
    {
        await using var ts = await MultiDialectFixture.CreateSqliteAsync();
        var log = new PalOrmEventLog<SqliteProvider>(ts.Session);
        var events = Enumerable.Range(0, 2500)
            .Select(i => MultiDialectTestData.CreateEventData($"large.event.{i}"))
            .ToArray();

        var result = await log.AppendAsync("large-stream", ExpectedStreamVersion.NoStream, events, default);

        await Assert.That(result.FirstStreamVersion).IsEqualTo(0L);
        await Assert.That(result.LastStreamVersion).IsEqualTo(2499L);
        var read = new List<RecordedEvent>();
        await foreach (var e in log.ReadStreamAsync("large-stream", 0, int.MaxValue, default))
            read.Add(e);
        await Assert.That(read.Count).IsEqualTo(2500);
        await Assert.That(read[^1].EventName).IsEqualTo("large.event.2499");
    }

    [Test]
    public async Task EventLog_Sqlite_BinaryMode_ReadsBinAndRejectsMissingBackfill()
    {
        await using var ts = await MultiDialectFixture.CreateSqliteAsync();
        var log = new PalOrmEventLog<SqliteProvider>(ts.Session);
        var eventData = MultiDialectTestData.CreateEventData("binary.event.v1");
        await log.AppendAsync("binary-stream", ExpectedStreamVersion.NoStream, [eventData], default);

        log.UseBinaryEventId = true;
        var read = new List<RecordedEvent>();
        await foreach (var e in log.ReadStreamAsync("binary-stream", 0, 10, default))
            read.Add(e);
        await Assert.That(read).HasSingleItem();
        await Assert.That(read[0].EventId).IsEqualTo(eventData.EventId);

        await ts.Session.ExecuteAsync(FormattableStringFactory.Create("UPDATE events SET event_id_bin = NULL"), ct: default);
        await Assert.That(async () => await ReadAllAsync(log.ReadAllAsync(0, 10, default)))
            .Throws<InvalidOperationException>();
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

    [Test]
    public async Task EventLog_Sqlite_AppendSingleAny_DuplicateEventId_ThrowsOriginal()
        => await Test_AppendSingleAny_DuplicateEventId_ThrowsOriginal(await MultiDialectFixture.CreateSqliteAsync());

    [Test]
    public async Task EventLog_MySql_AppendSingleAny_DuplicateEventId_ThrowsOriginal()
        => await Test_AppendSingleAny_DuplicateEventId_ThrowsOriginal(await MultiDialectFixture.CreateMySqlAsync());

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
            // 同 EventId 重投：唯一约束冲突 → 分类为 EventId 重复原样上抛（不误译并发异常）。
            // v5.0 ITM-075 对齐（2026-10-27）起断言升级为类型锁定：数据错误不得转
            // EventStreamConcurrencyException（转了会诱导盲目重试的调用方无限循环）
            var ex = await Assert.ThrowsAsync(async () =>
                await log.AppendAsync("dup-stream", ExpectedStreamVersion.Any, [duplicate], default));
            await Assert.That(ex is not PalDDD.EventLog.EventStreamConcurrencyException).IsTrue();
        }
    }

    // ─── v5.0 ITM-075 对齐锁定（2026-10-27）：批路径两因分离 ───
    // 撞库（批内重复 / 撞表中既有 ID）必须原样上抛，不得误译为并发异常。
    // 「撞库 + 并发推进同窗叠加」的行为差异面本地不可构造（SQLite 批 INSERT 单语句
    // 原子失败，MAX 不会被部分推进；MySQL 逐行部分成功叠加归 CI dialect-probe 覆盖）。

    [Test]
    public async Task EventLog_Sqlite_AppendBatch_DuplicateEventIdInBatch_ThrowsOriginalNotConcurrency()
    {
        await using var ts = await MultiDialectFixture.CreateSqliteAsync();
        var log = new PalOrmEventLog<SqliteProvider>(ts.Session);
        var e1 = MultiDialectTestData.CreateEventData("batchdup.e1");
        var duplicate = new PalDDD.EventLog.EventData(
            e1.EventId, e1.EventName, e1.SchemaVersion, e1.ContentType,
            e1.Payload, e1.Metadata, e1.Audit);
        var ex = await Assert.ThrowsAsync(async () =>
            await log.AppendAsync("batch-dup-stream", ExpectedStreamVersion.Any, [e1, duplicate], default));
        await Assert.That(ex is not PalDDD.EventLog.EventStreamConcurrencyException).IsTrue();
    }

    [Test]
    public async Task EventLog_Sqlite_AppendBatch_EventIdAlreadyStoredInOtherStream_ThrowsOriginalNotConcurrency()
    {
        await using var ts = await MultiDialectFixture.CreateSqliteAsync();
        var log = new PalOrmEventLog<SqliteProvider>(ts.Session);
        var stored = MultiDialectTestData.CreateEventData("crossdup.e1");
        await log.AppendAsync("stream-a", ExpectedStreamVersion.Any, [stored], default);
        var fresh = MultiDialectTestData.CreateEventData("crossdup.e2");
        var clash = new PalDDD.EventLog.EventData(
            stored.EventId, stored.EventName, stored.SchemaVersion, stored.ContentType,
            stored.Payload, stored.Metadata, stored.Audit);
        var ex = await Assert.ThrowsAsync(async () =>
            await log.AppendAsync("stream-b", ExpectedStreamVersion.Any, [fresh, clash], default));
        await Assert.That(ex is not PalDDD.EventLog.EventStreamConcurrencyException).IsTrue();
    }

    private static async Task<List<RecordedEvent>> ReadAllAsync(IAsyncEnumerable<RecordedEvent> events)
    {
        var result = new List<RecordedEvent>();
        await foreach (var e in events)
            result.Add(e);
        return result;
    }
}
