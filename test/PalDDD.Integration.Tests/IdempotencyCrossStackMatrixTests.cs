using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.Idempotency;
using PalDDD.PalORM.Sqlite;
using PalORM;
using PalORM.Sqlite;

namespace PalDDD.Integration.Tests;

/// <summary>
/// Idempotency 四栈契约矩阵（2026-10-21 契约盘点 R5）：
/// InMemory / Dapper SQLite / PalORM SQLite / EF SQLite 跑同一组断言。
/// 锁定条款：首次执行返回 Processing、活跃租约拒绝并发、完成后重放拒绝、
/// 终态可读且载荷保留、租约过期后可回收重建。
/// </summary>
public sealed class IdempotencyCrossStackMatrixTests
{
    private static readonly string[] IdempotencyDdl =
    [
        "CREATE TABLE idempotency_records (operation_name TEXT NOT NULL, idempotency_key TEXT NOT NULL, status INTEGER NOT NULL, locked_until TEXT NOT NULL, expires_at TEXT NOT NULL, updated_at TEXT NOT NULL, response_payload BLOB, error TEXT, revision INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (operation_name, idempotency_key))",
    ];

    [Test]
    public async Task Idempotency_FourStacks_SameLeaseAndReplayContract(CancellationToken ct)
    {
        await AssertContractAsync(new InMemoryIdempotencyStore(), ct);

        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            await AssertContractAsync(new DapperIdempotencyStore(conn, DapperDbType.Sqlite), ct);
        }

        await using (var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct))
        {
            var conn = session.GetRawConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            await AssertContractAsync(new SqliteIdempotencyStore(session), ct);
        }

        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            var options = new DbContextOptionsBuilder<ProbeIdempotencyContext>()
                .UseSqlite(conn)
                .Options;
            await using var context = new ProbeIdempotencyContext(options);
            await context.Database.EnsureCreatedAsync(ct);
            await AssertContractAsync(context, ct);
        }
    }

    private static async Task AssertContractAsync(IIdempotencyStore store, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var policy = IdempotencyPolicy.Default;

        var started = await store.TryStartAsync("matrix-op", "k1", now, policy, ct);
        await Assert.That(started).IsTypeOf<IdempotencyRecord>();
        await Assert.That(started!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);

        var concurrent = await store.TryStartAsync("matrix-op", "k1", now.AddSeconds(1), policy, ct);
        await Assert.That(concurrent).IsNull();

        await store.MarkCompletedAsync(started, "done"u8.ToArray(), now.AddSeconds(2), ct);

        var replay = await store.TryStartAsync("matrix-op", "k1", now.AddSeconds(3), policy, ct);
        await Assert.That(replay).IsNull();

        var loaded = await store.GetAsync("matrix-op", "k1", now.AddSeconds(4), ct);
        await Assert.That(loaded).IsTypeOf<IdempotencyRecord>();
        await Assert.That(loaded!.Status).IsEqualTo(IdempotencyRecordStatus.Completed);
        await Assert.That(loaded.ResponsePayload.HasValue).IsTrue();
        await Assert.That(loaded.ResponsePayload!.Value.Length).IsEqualTo(4);

        // 租约过期后可回收重建（与终态无关，过期即回收）。
        var shortPolicy = new IdempotencyPolicy
        {
            ProcessingTimeout = TimeSpan.FromSeconds(1),
            Retention = TimeSpan.FromHours(1)
        };
        var leased = await store.TryStartAsync("matrix-op", "k2", now, shortPolicy, ct);
        await Assert.That(leased).IsTypeOf<IdempotencyRecord>();
        var reclaimed = await store.TryStartAsync("matrix-op", "k2", now.AddSeconds(2), policy, ct);
        await Assert.That(reclaimed).IsTypeOf<IdempotencyRecord>();
        await Assert.That(reclaimed!.Status).IsEqualTo(IdempotencyRecordStatus.Processing);
    }

    private static async Task ExecuteAsync(DbConnection connection, CancellationToken ct)
    {
        foreach (var sql in IdempotencyDdl)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class ProbeIdempotencyContext(DbContextOptions options) : IdempotencyDbContext(options);
}
