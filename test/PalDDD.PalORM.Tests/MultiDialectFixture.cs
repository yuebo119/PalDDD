using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using PalDDD.Testing;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Testcontainers 跨方言 Fixture - 返回 <see cref="TestSession{TProvider}"/> 包装类型。
/// <para>
/// <b>资源清理</b>：TestSession 实现 IAsyncDisposable，dispose 时释放 DataSession 和本次创建的容器。
/// 测试必须用 <c>await using var ts = await MultiDialectFixture.CreateXxxAsync()</c>。
/// </para>
/// <para>
/// <b>外部数据库</b>：只有显式关闭 Testcontainers，并同时满足数据库名前缀和
/// <c>PALDDD_TEST_ALLOW_DESTRUCTIVE_CLEANUP=1</c> 时，才允许清理固定表。
/// </para>
/// </summary>
public static class MultiDialectFixture
{
    public static async Task<TestSession<SqliteProvider>> CreateSqliteAsync(CancellationToken ct = default)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct);
        await ApplySchemaAsync(session, MultiDialectSchema.Sqlite, ct);
        return new TestSession<SqliteProvider>(session);
    }

    public static async Task<TestSession<PostgreSqlProvider>> CreatePostgreSqlAsync(CancellationToken ct = default)
    {
        if (TestEnvironment.UsePostgreSqlTestcontainers)
        {
            var container = new PostgreSqlBuilder(TestEnvironment.PostgreSqlImage).Build();
            try
            {
                await container.StartAsync(ct);
                var session = await DataSession<PostgreSqlProvider>.CreateAsync(
                    DbOptions.Development(container.GetConnectionString()), ct);
                await ApplySchemaAsync(session, MultiDialectSchema.PostgreSql, ct);
                return new TestSession<PostgreSqlProvider>(session, container);
            }
            catch
            {
                await container.DisposeAsync();
                throw;
            }
        }

        var cs = TestEnvironment.PostgreSqlConnectionString;
        EnsureExternalCleanupIsAuthorized(cs, TestEnvironment.PostgreSqlIsolationDatabasePrefix, "PostgreSQL");
        var externalSession = await DataSession<PostgreSqlProvider>.CreateAsync(DbOptions.Development(cs), ct);
        try
        {
            await CleanAllTablesAsync(externalSession, ct);
            await ApplySchemaAsync(externalSession, MultiDialectSchema.PostgreSql, ct);
            return new TestSession<PostgreSqlProvider>(externalSession);
        }
        catch
        {
            await externalSession.DisposeAsync();
            throw;
        }
    }

    public static async Task<TestSession<MySqlProvider>> CreateMySqlAsync(CancellationToken ct = default)
    {
        if (TestEnvironment.UseMySqlTestcontainers)
        {
            var container = new MySqlBuilder(TestEnvironment.MySqlImage).Build();
            try
            {
                await container.StartAsync(ct);
                var session = await DataSession<MySqlProvider>.CreateAsync(
                    DbOptions.Development(container.GetConnectionString()), ct);
                await ApplySchemaAsync(session, MultiDialectSchema.MySql, ct);
                return new TestSession<MySqlProvider>(session, container);
            }
            catch
            {
                await container.DisposeAsync();
                throw;
            }
        }

        var cs = TestEnvironment.MySqlConnectionString;
        EnsureExternalCleanupIsAuthorized(cs, TestEnvironment.MySqlIsolationDatabasePrefix, "MySQL");
        var externalSession = await DataSession<MySqlProvider>.CreateAsync(DbOptions.Development(cs), ct);
        try
        {
            await CleanAllTablesAsync(externalSession, ct);
            await ApplySchemaAsync(externalSession, MultiDialectSchema.MySql, ct);
            return new TestSession<MySqlProvider>(externalSession);
        }
        catch
        {
            await externalSession.DisposeAsync();
            throw;
        }
    }

    private static void EnsureExternalCleanupIsAuthorized(string connectionString, string requiredPrefix, string provider)
    {
        if (!TestEnvironment.CanCleanExternalDatabase(
                connectionString,
                requiredPrefix,
                TestEnvironment.ExternalDatabaseCleanupConfirmed))
        {
            throw new InvalidOperationException(
                $"拒绝清理 {provider} 外部测试目标：数据库名必须以 {requiredPrefix} 开头，且必须设置 PALDDD_TEST_ALLOW_DESTRUCTIVE_CLEANUP=1。" );
        }
    }

    private static async Task ApplySchemaAsync<TProvider>(DataSession<TProvider> session, string[] ddls, CancellationToken ct)
        where TProvider : IDbProvider
    {
        var conn = session.GetRawConnection();
        foreach (var ddl in ddls)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>清理外部专用数据库中的测试表；调用方必须先通过隔离目标双门校验。</summary>
    private static async Task CleanAllTablesAsync<TProvider>(DataSession<TProvider> session, CancellationToken ct)
        where TProvider : IDbProvider
    {
        var conn = session.GetRawConnection();
        var tables = new[] { "outbox_messages", "inbox_messages", "saga_states", "events", "projection_checkpoints", "idempotency_records" };
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            // 只在已通过目标隔离校验的外部库中执行物理删除。
            cmd.CommandText = $"DROP TABLE IF EXISTS {table}";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var indexes = new[] { "idx_inbox_unique", "idx_events_stream", "idx_outbox_status", "idx_projection_checkpoints_status", "idx_idempotency_expires" };
        foreach (var idx in indexes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP INDEX IF EXISTS {idx}";
            try { await cmd.ExecuteNonQueryAsync(ct); } catch { /* 某些方言不支持 IF EXISTS on index：忽略并继续 */ }
        }
    }
}

/// <summary>测试会话包装 - dispose 时释放 DataSession 和可选的 Testcontainers 资源。</summary>
public sealed class TestSession<TProvider> : IAsyncDisposable
    where TProvider : IDbProvider
{
    public DataSession<TProvider> Session { get; }
    private readonly IAsyncDisposable? _resource;

    internal TestSession(DataSession<TProvider> session, IAsyncDisposable? resource = null)
    {
        Session = session;
        _resource = resource;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        if (_resource is not null) await _resource.DisposeAsync();
    }

    public static implicit operator DataSession<TProvider>(TestSession<TProvider> ts) => ts.Session;

    public DataSession<TProvider> ToDataSession() => Session;
}
