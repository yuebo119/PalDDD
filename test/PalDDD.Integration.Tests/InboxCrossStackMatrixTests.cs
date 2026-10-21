using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Dapper;
using PalDDD.PalORM.Sqlite;
using PalDDD.Transactions;
using PalORM;
using PalORM.Sqlite;

namespace PalDDD.Integration.Tests;

/// <summary>
/// Inbox 四栈契约矩阵（2026-10-21 契约盘点 R2 后续项 P1）：
/// 同一组断言在 InMemory / Dapper SQLite / PalORM SQLite / EF SQLite 上各跑一遍。
/// 锁定的条款：首次认领返回 Processing、重复认领返回 null、完成后返回 null、
/// 失败后可立即重入且 Attempts 递增。改坏任一栈的认领/失败语义即红。
/// </summary>
// R11 F26：与 dapper-global 组串行（Dapper 全局类型映射为进程级状态）。
[TUnit.Core.NotInParallel("dapper-global")]
public sealed class InboxCrossStackMatrixTests
{
    // PalORM 栈与 Dapper 栈的 SQLite schema 刻意分离（TST-205 平行副本声明），
    // 本矩阵作为第三份消费方副本，仅覆盖 inbox_messages 两张索引/表。
    private static readonly string[] InboxDdl =
    [
        "CREATE TABLE inbox_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL, consumer_name TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, received_at TEXT NOT NULL, processing_started_at TEXT, processed_at TEXT, attempts INTEGER NOT NULL DEFAULT 1, last_error TEXT)",
        "CREATE UNIQUE INDEX idx_inbox_unique ON inbox_messages(consumer_name, message_id)",
    ];

    [Test]
    public async Task Inbox_FourStacks_SameClaimAndFailureContract(CancellationToken ct)
    {
        await AssertContractAsync(new InMemoryInboxStore(), ct);

        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            await AssertContractAsync(new DapperInboxStore(conn, DapperDbType.Sqlite), ct);
        }

        await using (var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct))
        {
            var conn = session.GetRawConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);
            await ExecuteAsync(conn, ct);
            await AssertContractAsync(new SqliteInboxStore(session), ct);
        }

        // SQLite 内存库按连接隔离：必须复用同一个打开的连接，否则 EnsureCreated 与
        // 后续保存落在不同数据库（no such table）。
        await using (var conn = new SqliteConnection("Data Source=:memory:"))
        {
            await conn.OpenAsync(ct);
            var options = new DbContextOptionsBuilder<ProbeInboxDbContext>()
                .UseSqlite(conn)
                .Options;
            await using var context = new ProbeInboxDbContext(options);
            await context.Database.EnsureCreatedAsync(ct);
            await AssertContractAsync(context, ct);
        }
    }

    private static async Task AssertContractAsync(IInboxStore store, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryStartProcessingAsync("matrix", "m1", now, TimeSpan.FromMinutes(5), ct);
        await Assert.That(first).IsTypeOf<InboxMessage>();
        await Assert.That(first!.Status).IsEqualTo(InboxStatus.Processing);
        await Assert.That(first.Attempts).IsEqualTo(1);

        var duplicate = await store.TryStartProcessingAsync("matrix", "m1", now.AddSeconds(1), TimeSpan.FromMinutes(5), ct);
        await Assert.That(duplicate).IsNull();

        await store.MarkProcessedAsync(first, now.AddSeconds(2), ct);
        var afterProcessed = await store.TryStartProcessingAsync("matrix", "m1", now.AddSeconds(3), TimeSpan.FromMinutes(5), ct);
        await Assert.That(afterProcessed).IsNull();

        var failing = await store.TryStartProcessingAsync("matrix", "m2", now, TimeSpan.FromMinutes(5), ct);
        await Assert.That(failing).IsTypeOf<InboxMessage>();
        await store.MarkFailedAsync(failing!, "boom", ct);

        var retried = await store.TryStartProcessingAsync("matrix", "m2", now.AddSeconds(2), TimeSpan.FromMinutes(5), ct);
        await Assert.That(retried).IsTypeOf<InboxMessage>();
        await Assert.That(retried!.Attempts).IsEqualTo(2);
    }

    private static async Task ExecuteAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
    {
        foreach (var sql in InboxDdl)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class ProbeInboxDbContext(DbContextOptions options) : InboxDbContext(options);
}
