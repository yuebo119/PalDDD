using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PalORM.Sqlite;
using PalDDD.PalORM.Sqlite;
using PalDDD.PalORM.Stores;
using PalDDD.Transactions;
using System.Data.Common;

namespace PalDDD.PalORM.Tests;

/// <summary>保留清理注册契约（2026-10-21 审计 P0）：PalORM 入口默认只提供 IRetentionStore，
/// 不启动后台删除；清理必须显式 AddPalRetention。</summary>
public sealed class PalOrmRetentionRegistrationTests
{
    [Test]
    public async Task AddPalOrmSqlite_DoesNotStartRetentionByDefault()
    {
        var services = new ServiceCollection();
        services.AddPalOrmSqlite("Data Source=:memory:");

        var retentionHosted = services.Count(static d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(RetentionProcessorService));

        await Assert.That(retentionHosted).IsEqualTo(0);
    }

    [Test]
    public async Task AddPalRetention_AfterAddPalOrmSqlite_EnablesRetentionExplicitly()
    {
        var services = new ServiceCollection();
        services.AddPalOrmSqlite("Data Source=:memory:");
        services.AddPalRetention();

        var retentionHosted = services.Count(static d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(RetentionProcessorService));

        await Assert.That(retentionHosted).IsEqualTo(1);
    }

    [Test]
    public async Task PalOrmRetention_Sqlite_UsesProviderNativeTimeEncoding()
    {
        await using var session = await PalOrmStoreFixture.CreateAsync();
        var conn = session.GetRawConnection();
        var cutoff = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        await InsertOutboxAsync(conn, "same-day", cutoff.AddHours(23));
        await InsertOutboxAsync(conn, "older", cutoff.AddDays(-1));

        var store = new PalOrmRetentionStore<SqliteProvider>(session);
        var deleted = await store.DeleteExpiredOutboxAsync(cutoff, batchSize: 10, includeDead: false);

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await CountAsync(conn, "outbox_messages")).IsEqualTo(1);
        await Assert.That(await CountAsync(conn, "outbox_messages WHERE id = 'same-day'")).IsEqualTo(1);
    }

    private static async Task InsertOutboxAsync(DbConnection conn, string id, DateTimeOffset processedAt)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = "INSERT INTO outbox_messages (id, type, payload, status, created_at, processed_at) VALUES ($id, 't', $payload, 1, $at, $at)";
        Add(command, "$id", id);
        Add(command, "$payload", new byte[] { 1 });
        Add(command, "$at", processedAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(DbConnection conn, string fromAndWhere)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + fromAndWhere;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
