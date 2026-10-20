using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalDDD.Dapper;
using PalDDD.Transactions;
using System.Globalization;

namespace PalDDD.Integration.Tests;

/// <summary>历史表保留清理（2026-10-20 性能轮）SQLite 真库测试。</summary>
public sealed class RetentionProcessorTests
{
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

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
