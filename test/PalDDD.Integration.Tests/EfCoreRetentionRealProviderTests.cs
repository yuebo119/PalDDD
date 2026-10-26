using MySqlConnector;
using Npgsql;
using PalDDD.Transactions;
using System.Data.Common;

// Justification: 探针跳过说明是测试控制台输出，不是本地化 UI 文案。
#pragma warning disable CA1303

namespace PalDDD.Integration.Tests;

/// <summary>
/// T2（基础库优化清单）：EfCoreRetentionStore 在真实 PG/MySQL 上的端到端删除。
/// 通过 PALDDD_TEST_PG / PALDDD_TEST_MYSQL 环境变量启用；未设置时打印说明并返回，
/// 不写 Skip 标注（new-skip-guard 口径）。临时表随会话结束消失，不污染库。
/// </summary>
public sealed class EfCoreRetentionRealProviderTests
{
    private static readonly DateTimeOffset Cutoff =
        new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task PostgreSql_DeletesByEfPhysicalNames(CancellationToken ct)
    {
        var connectionString = Environment.GetEnvironmentVariable("PALDDD_TEST_PG");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("[skip] PALDDD_TEST_PG 未设置；PG 真库保留清理未执行。");
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = """
                CREATE TEMP TABLE "OutboxMessages" (
                    "Id" TEXT PRIMARY KEY,
                    "Status" INT NOT NULL,
                    "ProcessedAt" TIMESTAMPTZ NULL
                )
                """;
            await ddl.ExecuteNonQueryAsync(ct);
        }

        await InsertAsync(connection,
            """INSERT INTO "OutboxMessages" ("Id", "Status", "ProcessedAt") VALUES (@id, @status, @at)""",
            "same-day", 1, Cutoff.AddHours(23), ct);
        await InsertAsync(connection,
            """INSERT INTO "OutboxMessages" ("Id", "Status", "ProcessedAt") VALUES (@id, @status, @at)""",
            "older", 1, Cutoff.AddDays(-1), ct);

        var store = new EfCoreRetentionStore(connection, RetentionDialect.PostgreSql);
        var deleted = await store.DeleteExpiredOutboxAsync(Cutoff, batchSize: 10, includeDead: false, ct);

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await CountAsync(connection, """SELECT COUNT(*) FROM "OutboxMessages" """, ct)).IsEqualTo(1);
        await Assert.That(await CountAsync(connection, """SELECT COUNT(*) FROM "OutboxMessages" WHERE "Id" = 'same-day' """, ct)).IsEqualTo(1);
    }

    [Test]
    public async Task MySql_DeletesByEfPhysicalNames(CancellationToken ct)
    {
        var connectionString = Environment.GetEnvironmentVariable("PALDDD_TEST_MYSQL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("[skip] PALDDD_TEST_MYSQL 未设置；MySQL 真库保留清理未执行。");
            return;
        }

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = """
                CREATE TEMPORARY TABLE OutboxMessages (
                    Id VARCHAR(64) PRIMARY KEY,
                    Status INT NOT NULL,
                    ProcessedAt DATETIME(6) NULL
                )
                """;
            await ddl.ExecuteNonQueryAsync(ct);
        }

        await InsertAsync(connection,
            "INSERT INTO OutboxMessages (Id, Status, ProcessedAt) VALUES (@id, @status, @at)",
            "same-day", 1, Cutoff.AddHours(23), ct);
        await InsertAsync(connection,
            "INSERT INTO OutboxMessages (Id, Status, ProcessedAt) VALUES (@id, @status, @at)",
            "older", 1, Cutoff.AddDays(-1), ct);

        var store = new EfCoreRetentionStore(connection, RetentionDialect.MySql);
        var deleted = await store.DeleteExpiredOutboxAsync(Cutoff, batchSize: 10, includeDead: false, ct);

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await CountAsync(connection, "SELECT COUNT(*) FROM OutboxMessages", ct)).IsEqualTo(1);
        await Assert.That(await CountAsync(connection, "SELECT COUNT(*) FROM OutboxMessages WHERE Id = 'same-day'", ct)).IsEqualTo(1);
    }

    private static async Task InsertAsync(
        DbConnection connection,
        string sql,
        string id,
        int status,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", id);
        AddParameter(command, "@status", status);
        AddParameter(command, "@at", connection is MySqlConnection ? at.UtcDateTime : at);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
