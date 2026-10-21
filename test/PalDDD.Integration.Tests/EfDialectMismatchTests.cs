// RELATIONAL-COVERAGE-NONE: 仅断言 provider 与方言错配的可行动异常，不提供 PG 上下文的关系型行为覆盖。
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Transactions;

namespace PalDDD.Integration.Tests;

/// <summary>
/// EF 方言上下文的 provider 不匹配必须给出可行动异常（2026-10-21 契约盘点 R2 后续项）：
/// 修复前 SQLite provider + PostgreSqlOutboxDbContext 会在首次租约查询报 `near "FOR"`。
/// </summary>
public sealed class EfDialectMismatchTests
{
    [Test]
    public async Task PostgreSqlContext_WithSqliteProvider_ThrowsActionableError(CancellationToken ct)
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<ProbePostgreSqlContext>()
            .UseSqlite(conn)
            .Options;
        await using var context = new ProbePostgreSqlContext(options);

        InvalidOperationException? captured = null;
        try
        {
            await context.LeasePendingMessagesAsync(10, "owner", TimeSpan.FromMinutes(1), 10, ct);
        }
        catch (InvalidOperationException ex)
        {
            captured = ex;
        }

        await Assert.That(captured).IsTypeOf<InvalidOperationException>();
        await Assert.That(captured!.Message.Contains("PostgreSql", StringComparison.Ordinal)).IsTrue();
        await Assert.That(captured.Message.Contains("Sqlite", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SqliteContext_WithSqliteProvider_LeasesWithoutError(CancellationToken ct)
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<ProbeSqliteContext>()
            .UseSqlite(conn)
            .Options;
        await using var context = new ProbeSqliteContext(options);
        await context.Database.EnsureCreatedAsync(ct);

        var leased = await context.LeasePendingMessagesAsync(10, "owner", TimeSpan.FromMinutes(1), 10, ct);

        await Assert.That(leased.Count).IsEqualTo(0);
    }

    private sealed class ProbePostgreSqlContext(DbContextOptions options) : PostgreSqlOutboxDbContext(options);

    private sealed class ProbeSqliteContext(DbContextOptions options) : SqliteOutboxDbContext(options);
}
