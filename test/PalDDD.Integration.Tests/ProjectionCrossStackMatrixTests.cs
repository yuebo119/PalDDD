using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.PalORM.Sqlite;
using PalDDD.Projections;
using PalORM;
using PalORM.Sqlite;

namespace PalDDD.Integration.Tests;

/// <summary>
/// Projections 四栈契约矩阵（2026-10-21 契约盘点 R5）：
/// InMemory / Dapper SQLite / PalORM SQLite / EF SQLite 跑同一组断言。
/// 锁定条款：首次认领返回 Processing、活跃租约拒绝并发、完成后同位置拒绝、
/// 失败可重认领、Reset 后同位置可重新开始。
/// </summary>
public sealed class ProjectionCrossStackMatrixTests
{
    private static readonly string[] ProjectionDdl =
    [
        "CREATE TABLE projection_checkpoints (projection_name TEXT NOT NULL, source_name TEXT NOT NULL, position TEXT NOT NULL, status INTEGER NOT NULL, updated_at TEXT NOT NULL, lease_until TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0, error TEXT, PRIMARY KEY (projection_name, source_name, position))",
    ];

    [Test]
    public async Task Projection_FourStacks_SameCheckpointContract(CancellationToken ct)
    {
        await AssertContractAsync(new InMemoryProjectionCheckpointStore(), ct);

        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            await AssertContractAsync(new DapperProjectionCheckpointStore(conn, DapperDbType.Sqlite), ct);
        }

        await using (var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct))
        {
            var conn = session.GetRawConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            await AssertContractAsync(new SqliteProjectionCheckpointStore(session), ct);
        }

        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            var options = new DbContextOptionsBuilder<ProbeProjectionContext>()
                .UseSqlite(conn)
                .Options;
            await using var context = new ProbeProjectionContext(options);
            await context.Database.EnsureCreatedAsync(ct);
            await AssertContractAsync(context, ct);
        }
    }

    private static async Task AssertContractAsync(IProjectionCheckpointStore store, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromMinutes(5);

        var first = await store.TryStartAsync("matrix-projection", "matrix-source", "pos-1", now, timeout, ct);
        await Assert.That(first).IsTypeOf<ProjectionCheckpoint>();
        await Assert.That(first!.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);

        var duplicate = await store.TryStartAsync("matrix-projection", "matrix-source", "pos-1", now.AddSeconds(1), timeout, ct);
        await Assert.That(duplicate).IsNull();

        await store.MarkCompletedAsync(first, now.AddSeconds(2), ct);
        var afterCompleted = await store.TryStartAsync("matrix-projection", "matrix-source", "pos-1", now.AddSeconds(3), timeout, ct);
        await Assert.That(afterCompleted).IsNull();

        var next = await store.TryStartAsync("matrix-projection", "matrix-source", "pos-2", now.AddSeconds(3), timeout, ct);
        await Assert.That(next).IsTypeOf<ProjectionCheckpoint>();
        await store.MarkFailedAsync(next!, "boom", now.AddSeconds(4), ct);

        var reclaim = await store.TryStartAsync("matrix-projection", "matrix-source", "pos-2", now.AddSeconds(5), timeout, ct);
        await Assert.That(reclaim).IsTypeOf<ProjectionCheckpoint>();
        await Assert.That(reclaim!.Status).IsEqualTo(ProjectionCheckpointStatus.Processing);

        await store.ResetAsync("matrix-projection", "matrix-source", ct);
        var afterReset = await store.TryStartAsync("matrix-projection", "matrix-source", "pos-1", now.AddSeconds(6), timeout, ct);
        await Assert.That(afterReset).IsTypeOf<ProjectionCheckpoint>();
    }

    private static async Task ExecuteAsync(DbConnection connection, CancellationToken ct)
    {
        foreach (var sql in ProjectionDdl)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class ProbeProjectionContext(DbContextOptions options) : ProjectionCheckpointDbContext(options);
}
