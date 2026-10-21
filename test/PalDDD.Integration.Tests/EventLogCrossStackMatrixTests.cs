using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.EventLog;
using PalDDD.PalORM.Sqlite;
using PalORM;
using PalORM.Sqlite;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Integration.Tests;

/// <summary>
/// EventLog 四栈契约矩阵（2026-10-21 契约盘点 R5）：
/// InMemory / Dapper SQLite / PalORM SQLite / EF SQLite 跑同一组断言。
/// 锁定条款：NoStream 追加从版本 0 开始、流内与全局顺序一致、Exact 版本冲突抛
/// EventStreamConcurrencyException、Any 可在已有流继续追加。
/// </summary>
// R11 F26：Dapper 全局类型映射（MatchNamesWithUnderscores / ResetTypeHandlers）是
// 进程级状态，必须与 dapper-global 组串行，避免与 DapperStoreTests 的 ClassCleanup 竞态。
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class EventLogCrossStackMatrixTests
{
    private static readonly string[] EventLogDdl =
    [
        "CREATE TABLE events (global_position INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL, event_id_bin BLOB, event_name TEXT NOT NULL, stream_name TEXT NOT NULL, stream_version INTEGER NOT NULL, schema_version INTEGER NOT NULL DEFAULT 1, content_type TEXT NOT NULL DEFAULT 'application/json', payload BLOB NOT NULL, metadata BLOB, recorded_at TEXT NOT NULL, actor_id TEXT, reason TEXT, correlation_id TEXT, causation_id TEXT, trace_parent TEXT, trace_state TEXT)",
    ];

    [Test]
    public async Task EventLog_FourStacks_SameAppendAndConcurrencyContract(CancellationToken ct)
    {
        await AssertContractAsync(new InMemoryEventLog(), ct);

        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            await AssertContractAsync(new DapperEventLog(conn, transaction: null, dbType: DapperDbType.Sqlite), ct);
        }

        await using (var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct))
        {
            var conn = session.GetRawConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            await AssertContractAsync(new SqliteEventLog(session), ct);
        }

        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            var options = new DbContextOptionsBuilder<ProbeEventLogContext>()
                .UseSqlite(conn)
                .Options;
            await using var context = new ProbeEventLogContext(options);
            await context.Database.EnsureCreatedAsync(ct);
            await AssertContractAsync(context, ct);
        }
    }

    private static async Task AssertContractAsync(IEventLog log, CancellationToken ct)
    {
        var first = await log.AppendAsync(
            "matrix-stream",
            ExpectedStreamVersion.NoStream,
            [NewEvent("matrix.event.v1"), NewEvent("matrix.event.v2")],
            ct);

        await Assert.That(first.FirstStreamVersion).IsEqualTo(0);
        await Assert.That(first.LastStreamVersion).IsEqualTo(1);
        await Assert.That(first.LastGlobalPosition - first.FirstGlobalPosition).IsEqualTo(1);

        var stream = new List<RecordedEvent>();
        await foreach (var recorded in log.ReadStreamAsync("matrix-stream", 0, 10, ct))
            stream.Add(recorded);
        await Assert.That(stream.Count).IsEqualTo(2);
        await Assert.That(stream[0].StreamVersion).IsEqualTo(0);
        await Assert.That(stream[1].StreamVersion).IsEqualTo(1);
        await Assert.That(stream[0].EventName).IsEqualTo("matrix.event.v1");

        EventStreamConcurrencyException? conflict = null;
        try
        {
            await log.AppendAsync(
                "matrix-stream",
                ExpectedStreamVersion.Exact(0),
                [NewEvent("matrix.event.v3")],
                ct);
        }
        catch (EventStreamConcurrencyException ex)
        {
            conflict = ex;
        }
        await Assert.That(conflict).IsTypeOf<EventStreamConcurrencyException>();

        var appended = await log.AppendAsync(
            "matrix-stream",
            ExpectedStreamVersion.Any,
            [NewEvent("matrix.event.v4")],
            ct);
        await Assert.That(appended.FirstStreamVersion).IsEqualTo(2);
        await Assert.That(appended.LastStreamVersion).IsEqualTo(2);

        var all = new List<RecordedEvent>();
        await foreach (var recorded in log.ReadAllAsync(0, 100, ct))
            all.Add(recorded);
        await Assert.That(all.Count).IsEqualTo(3);
    }

    private static EventData NewEvent(string eventName)
        => new(
            PalUlid.New(),
            eventName,
            1,
            "application/json",
            new ReadOnlyMemory<byte>([1, 2]),
            ReadOnlyMemory<byte>.Empty,
            EventAuditMetadata.Empty);

    private static async Task ExecuteAsync(DbConnection connection, CancellationToken ct)
    {
        foreach (var sql in EventLogDdl)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class ProbeEventLogContext(DbContextOptions options) : EventLogDbContext(options);
}
