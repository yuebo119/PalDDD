using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PalDDD.Dapper;
using PalDDD.Transactions;
using System.Globalization;

namespace PalDDD.Integration.Tests;

/// <summary>历史表保留清理（2026-10-20 性能轮）SQLite 真库测试。</summary>
public sealed class RetentionProcessorTests
{
    [Test]
    public async Task StoreDirectCall_InvalidBatchSize_ThrowsAtEntry()
    {
        // R11 F18 回归锁：SQLite LIMIT -1 = 无上限，直调 Store 时 batchSize<1 必须在
        // 入口拒绝，不能让一次调用删光积压；EF 同款入口守卫一并锁定。
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var dapper = new DapperRetentionStore(conn, DapperDbType.Sqlite);
        var ef = new EfCoreRetentionStore(conn, RetentionDialect.Sqlite);

        await Assert.That(async () => await dapper.DeleteExpiredOutboxAsync(DateTimeOffset.UtcNow, -1, false))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await dapper.DeleteExpiredInboxAsync(DateTimeOffset.UtcNow, 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await ef.DeleteExpiredIdempotencyAsync(DateTimeOffset.UtcNow, -5))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RunOnce_DeletesOnlyExpiredTerminalRows()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await CreateSchemaAsync(conn);

        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-10);
        var veryOld = now.AddDays(-40);
        var recent = now.AddDays(-1);
        var future = now.AddDays(1);

        for (var i = 0; i < 3; i++)
            await InsertOutboxAsync(conn, $"processed-old-{i}", status: 1, old);
        await InsertOutboxAsync(conn, "processed-new", status: 1, recent);
        await InsertOutboxAsync(conn, "dead-old", status: 2, old);
        await InsertOutboxAsync(conn, "pending-old", status: 0, old);

        await InsertInboxAsync(conn, status: 2, veryOld);
        await InsertInboxAsync(conn, status: 2, veryOld);
        await InsertInboxAsync(conn, status: 2, recent);
        await InsertInboxAsync(conn, status: 1, veryOld);

        await InsertIdempotencyAsync(conn, "expired-1", old.AddDays(-1));
        await InsertIdempotencyAsync(conn, "expired-2", old.AddDays(-1));
        await InsertIdempotencyAsync(conn, "future", future);

        var options = new RetentionOptions
        {
            BatchSize = 2,
            MaxBatchesPerTick = 10,
            OutboxRetention = TimeSpan.FromDays(7),
            InboxRetention = TimeSpan.FromDays(30),
        };
        var processor = new RetentionProcessor(
            new DapperRetentionStore(conn, DapperDbType.Sqlite),
            new StaticOptionsMonitor<RetentionOptions>(options));

        var result = await processor.RunOnceAsync();
        await Assert.That(result.OutboxDeleted).IsEqualTo(3);
        await Assert.That(result.InboxDeleted).IsEqualTo(2);
        await Assert.That(result.IdempotencyDeleted).IsEqualTo(2);

        await Assert.That(await CountAsync(conn, "outbox_messages")).IsEqualTo(3);
        await Assert.That(await CountAsync(conn, "inbox_messages")).IsEqualTo(2);
        await Assert.That(await CountAsync(conn, "idempotency_records")).IsEqualTo(1);

        options.DeleteDeadOutboxMessages = true;
        var second = await processor.RunOnceAsync();
        await Assert.That(second.OutboxDeleted).IsEqualTo(1);
        await Assert.That(await CountAsync(conn, "outbox_messages")).IsEqualTo(2);
    }

    [Test]
    public async Task RetentionQueries_UseProcessedIndexes()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await CreateSchemaAsync(conn);

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 100; i++)
        {
            await InsertOutboxAsync(conn, $"p-{i}", status: 1, now.AddDays(-10 - i));
            await InsertInboxAsync(conn, status: 2, now.AddDays(-40 - i));
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "ANALYZE";
            await cmd.ExecuteNonQueryAsync();
        }

        var outboxPlan = await ExplainAsync(conn,
            "SELECT id FROM outbox_messages WHERE status = 1 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT 10");
        var inboxPlan = await ExplainAsync(conn,
            "SELECT id FROM inbox_messages WHERE status = 2 AND processed_at IS NOT NULL AND processed_at < @cutoff ORDER BY processed_at LIMIT 10");

        await Assert.That(outboxPlan).Contains("idx_outbox_processed");
        await Assert.That(inboxPlan).Contains("idx_inbox_processed");
    }

    [Test]
    public async Task EfCoreRetention_Sqlite_DoesNotDeleteSameDayProviderFormattedRow()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RetentionOutboxContext>().UseSqlite(conn).Options;
        await using var context = new RetentionOutboxContext(options);
        await context.Database.EnsureCreatedAsync();

        // EF/PalORM 经 Microsoft.Data.Sqlite 写入原生 DateTimeOffset，落库为空格分隔；
        // Dapper 写 "O"（T 分隔）。SQLite 分支必须按栈选择编码。
        var cutoff = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        await InsertEfOutboxNativeAsync(conn, "same-day", status: 1, cutoff.AddHours(23));
        await InsertEfOutboxNativeAsync(conn, "older", status: 1, cutoff.AddDays(-1));

        var store = new EfCoreRetentionStore(context, RetentionDialect.Sqlite);
        var deleted = await store.DeleteExpiredOutboxAsync(cutoff, batchSize: 10, includeDead: false);

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await CountAsync(conn, "\"OutboxMessages\"")).IsEqualTo(1);
        await Assert.That(await CountAsync(conn, "\"OutboxMessages\" WHERE \"Id\" = 'same-day'")).IsEqualTo(1);
    }

    private static async Task InsertEfOutboxNativeAsync(
        SqliteConnection conn, string id, int status, DateTimeOffset at)
    {
        await using var command = conn.CreateCommand();
        command.CommandText =
            "INSERT INTO \"OutboxMessages\" (\"Id\", \"Type\", \"Payload\", \"ContentType\", \"SchemaVersion\", \"Status\", \"RetryCount\", \"CreatedAt\", \"ProcessedAt\") "
            + "VALUES ($id, 'test.event.v1', X'01', 'application/json', 1, $status, 0, $created, $at)";
        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        idParam.Value = id;
        var statusParam = command.CreateParameter();
        statusParam.ParameterName = "$status";
        statusParam.Value = status;
        var atParam = command.CreateParameter();
        atParam.ParameterName = "$at";
        atParam.Value = at;
        var createdParam = command.CreateParameter();
        createdParam.ParameterName = "$created";
        createdParam.Value = at.AddDays(-1);
        command.Parameters.Add(idParam);
        command.Parameters.Add(statusParam);
        command.Parameters.Add(atParam);
        command.Parameters.Add(createdParam);
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task EfCoreRetention_DefaultTableNames_MatchEfCreatedSchema()
    {
        // F5：EF Core 默认映射是 DbSet 名（OutboxMessages），清理 SQL 必须用同一物理名；
        // 原实现硬编码 outbox_messages，PG/MySQL 在 Linux 下首次清理即报 relation 不存在。
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RetentionOutboxContext>().UseSqlite(conn).Options;
        var cutoff = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        await using (var context = new RetentionOutboxContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            context.OutboxMessages.Add(new OutboxMessage
            {
                Type = "test.event.v1",
                Payload = [1, 2, 3],
                Status = OutboxStatus.Processed,
                ProcessedAt = cutoff.AddDays(-1),
                CreatedAt = cutoff.AddDays(-10),
            });
            await context.SaveChangesAsync();
        }

        var store = new EfCoreRetentionStore(conn, RetentionDialect.Sqlite);
        var deleted = await store.DeleteExpiredOutboxAsync(cutoff, batchSize: 10, includeDead: false);

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await CountAsync(conn, "\"OutboxMessages\"")).IsEqualTo(0);
    }

    private static async Task InsertOutboxNativeAsync(
        SqliteConnection conn, string id, int status, DateTimeOffset at)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = "INSERT INTO outbox_messages (id, status, processed_at) VALUES ($id, $status, $at)";
        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        idParam.Value = id;
        var statusParam = command.CreateParameter();
        statusParam.ParameterName = "$status";
        statusParam.Value = status;
        var atParam = command.CreateParameter();
        atParam.ParameterName = "$at";
        atParam.Value = at;
        command.Parameters.Add(idParam);
        command.Parameters.Add(statusParam);
        command.Parameters.Add(atParam);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ExplainAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var cutoff = cmd.CreateParameter();
        cutoff.ParameterName = "@cutoff";
        cutoff.Value = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        cmd.Parameters.Add(cutoff);
        await using var reader = await cmd.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync())
            details.Add(reader.GetString(3));
        return string.Join("\n", details);
    }

    private static async Task CreateSchemaAsync(SqliteConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE outbox_messages (
                id TEXT PRIMARY KEY,
                status INTEGER NOT NULL,
                processed_at TEXT
            );
            CREATE TABLE inbox_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                status INTEGER NOT NULL,
                processed_at TEXT
            );
            CREATE TABLE idempotency_records (
                operation_name TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                PRIMARY KEY (operation_name, idempotency_key)
            );
            CREATE INDEX idx_outbox_processed ON outbox_messages(status, processed_at);
            CREATE INDEX idx_inbox_processed ON inbox_messages(status, processed_at);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<int> InsertOutboxAsync(SqliteConnection conn, string id, int status, DateTimeOffset at)
        => conn.ExecuteAsync(
            "INSERT INTO outbox_messages (id, status, processed_at) VALUES (@id, @status, @at)",
            new { id, status, at = at.ToString("O", CultureInfo.InvariantCulture) });

    private static Task<int> InsertInboxAsync(SqliteConnection conn, int status, DateTimeOffset at)
        => conn.ExecuteAsync(
            "INSERT INTO inbox_messages (status, processed_at) VALUES (@status, @at)",
            new { status, at = at.ToString("O", CultureInfo.InvariantCulture) });

    private static Task<int> InsertIdempotencyAsync(SqliteConnection conn, string key, DateTimeOffset expiresAt)
        => conn.ExecuteAsync(
            "INSERT INTO idempotency_records (operation_name, idempotency_key, expires_at) VALUES ('probe', @key, @expiresAt)",
            new { key, expiresAt = expiresAt.ToString("O", CultureInfo.InvariantCulture) });

    private static async Task<long> CountAsync(SqliteConnection conn, string table)
        => await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}");

    private sealed class RetentionOutboxContext(DbContextOptions<RetentionOutboxContext> options)
        : SqliteOutboxDbContext(options);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
