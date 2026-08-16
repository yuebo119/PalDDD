namespace PalDDD.Integration.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PalDDD.Dapper.Sqlite;
using System.Data;
using System.Data.Common;

// ═══════════════════════════════════════════════════════════════
// 🧪 SqliteServiceCollectionExtensions 集成测试 — DI 注册 + PRAGMA 语义
// ═══════════════════════════════════════════════════════════════
// ITM-135 回归：AddPalSqlite("Data Source=:memory:") 默认 Production
// 启动时不再执行 WAL 确认（:memory: 恒返回 "memory"），而应按 InMemory 级 pragma 注册。
// ═══════════════════════════════════════════════════════════════

public sealed class SqliteServiceCollectionExtensionsTests
{
    [Test]
    public async Task AddPalSqlite_MemoryDataSource_DefaultProduction_RegistersAndAppliesInMemoryPragmas()
    {
        var services = new ServiceCollection();

        // 默认 optimize = Production；修复前此处即抛 InvalidOperationException（WAL 确认失败）
        services.AddPalSqlite("Data Source=:memory:");

        await Assert.That(services.Any(d => d.ServiceType == typeof(SqliteConnection))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(DbConnection))).IsTrue();

        await using var provider = services.BuildServiceProvider();
        var connection = provider.GetRequiredService<SqliteConnection>();
        await Assert.That(connection).IsNotNull();
        await Assert.That(connection.State).IsEqualTo(ConnectionState.Open);

        // 内存源按 InMemory 级 pragma 执行：journal_mode=MEMORY、synchronous=OFF（0）
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        var journalMode = (await command.ExecuteScalarAsync())?.ToString();
        await Assert.That(journalMode).IsEqualTo("memory");

        command.CommandText = "PRAGMA synchronous";
        await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(0L);
    }
}
