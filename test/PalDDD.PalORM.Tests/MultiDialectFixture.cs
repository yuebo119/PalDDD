using System.Runtime.ExceptionServices;
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
/// PG/MySQL 只允许每次测试创建的隔离容器，不清理外部固定数据库。
/// </summary>
public static class MultiDialectFixture
{
    public static async Task<TestSession<SqliteProvider>> CreateSqliteAsync(CancellationToken ct = default)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct);
        try
        {
            await ApplySchemaAsync(session, MultiDialectSchema.Sqlite, ct);
            return new TestSession<SqliteProvider>(session);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public static async Task<TestSession<PostgreSqlProvider>> CreatePostgreSqlAsync(CancellationToken ct = default)
    {
        EnsureTestcontainersRequired(TestEnvironment.UsePostgreSqlTestcontainers, "PostgreSQL");
        var database = $"palddd_test_{Guid.NewGuid():N}";
        var marker = Guid.NewGuid().ToString("N");
        var container = new PostgreSqlBuilder(TestEnvironment.PostgreSqlImage)
            .WithDatabase(database)
            .Build();
        DataSession<PostgreSqlProvider>? session = null;

        try
        {
            await container.StartAsync(ct);
            var connectionString = container.GetConnectionString();
            EnsureSingleDatabaseAlias(connectionString, database, "PostgreSQL");
            session = await DataSession<PostgreSqlProvider>.CreateAsync(DbOptions.Development(connectionString), ct);
            await VerifyConnectedDatabaseAsync(session.GetRawConnection(), database, verifyMySqlDatabase: false, ct);
            await CreateAndVerifyOwnershipMarkerAsync(session.GetRawConnection(), marker, ct);
            await ApplySchemaAsync(session, MultiDialectSchema.PostgreSql, ct);
            return new TestSession<PostgreSqlProvider>(session, container);
        }
        catch (Exception initializationException)
        {
            var cleanupExceptions = await AsyncResourceDisposer.DisposeCollectingAsync(session, container);
            if (cleanupExceptions.Count == 0) throw;
            throw new AggregateException("PostgreSQL Fixture 初始化和资源释放均失败。", [initializationException, .. cleanupExceptions]);
        }
    }

    public static async Task<TestSession<MySqlProvider>> CreateMySqlAsync(CancellationToken ct = default)
    {
        EnsureTestcontainersRequired(TestEnvironment.UseMySqlTestcontainers, "MySQL");
        var database = $"palddd_test_{Guid.NewGuid():N}";
        var marker = Guid.NewGuid().ToString("N");
        var container = new MySqlBuilder(TestEnvironment.MySqlImage)
            .WithDatabase(database)
            .Build();
        DataSession<MySqlProvider>? session = null;

        try
        {
            await container.StartAsync(ct);
            var connectionString = container.GetConnectionString();
            EnsureSingleDatabaseAlias(connectionString, database, "MySQL");
            session = await DataSession<MySqlProvider>.CreateAsync(DbOptions.Development(connectionString), ct);
            await VerifyConnectedDatabaseAsync(session.GetRawConnection(), database, verifyMySqlDatabase: true, ct);
            await CreateAndVerifyOwnershipMarkerAsync(session.GetRawConnection(), marker, ct);
            await ApplySchemaAsync(session, MultiDialectSchema.MySql, ct);
            return new TestSession<MySqlProvider>(session, container);
        }
        catch (Exception initializationException)
        {
            var cleanupExceptions = await AsyncResourceDisposer.DisposeCollectingAsync(session, container);
            if (cleanupExceptions.Count == 0) throw;
            throw new AggregateException("MySQL Fixture 初始化和资源释放均失败。", [initializationException, .. cleanupExceptions]);
        }
    }

    internal static void EnsureTestcontainersRequired(bool useTestcontainers, string provider)
    {
        if (!useTestcontainers)
        {
            throw new InvalidOperationException(
                $"{provider} 多方言 Fixture 禁止连接或清理外部数据库；必须启用 Testcontainers。" );
        }
    }

    private static void EnsureSingleDatabaseAlias(string connectionString, string expectedDatabase, string provider)
    {
        if (!TestEnvironment.TryGetUniqueDatabaseName(connectionString, out var configuredDatabase)
            || !string.Equals(configuredDatabase, expectedDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{provider} 容器连接串的数据库目标不唯一或与生成目标不一致。");
        }
    }

    private static async Task VerifyConnectedDatabaseAsync(
        System.Data.Common.DbConnection connection,
        string expectedDatabase,
        bool verifyMySqlDatabase,
        CancellationToken ct)
    {
        if (!string.Equals(connection.Database, expectedDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException("已打开连接的 Database 与生成测试目标不一致。");

        if (!verifyMySqlDatabase) return;

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DATABASE()";
        var actualDatabase = Convert.ToString(await command.ExecuteScalarAsync(ct));
        if (!string.Equals(actualDatabase, expectedDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException("MySQL SELECT DATABASE() 与生成测试目标不一致。");
    }

    private static async Task CreateAndVerifyOwnershipMarkerAsync(
        System.Data.Common.DbConnection connection,
        string marker,
        CancellationToken ct)
    {
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE palddd_test_ownership (marker VARCHAR(64) PRIMARY KEY)";
            await create.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO palddd_test_ownership(marker) VALUES (@marker)";
            var parameter = insert.CreateParameter();
            parameter.ParameterName = "@marker";
            parameter.Value = marker;
            insert.Parameters.Add(parameter);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT marker FROM palddd_test_ownership";
        var actualMarker = Convert.ToString(await read.ExecuteScalarAsync(ct));
        if (!string.Equals(actualMarker, marker, StringComparison.Ordinal))
            throw new InvalidOperationException("测试数据库 ownership marker 校验失败。");
    }

    private static async Task ApplySchemaAsync<TProvider>(DataSession<TProvider> session, string[] ddls, CancellationToken ct)
        where TProvider : IDbProvider
    {
        var connection = session.GetRawConnection();
        foreach (var ddl in ddls)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ddl;
            await command.ExecuteNonQueryAsync(ct);
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
        await AsyncResourceDisposer.DisposeAsync(Session, _resource);
    }

    public static implicit operator DataSession<TProvider>(TestSession<TProvider> ts) => ts.Session;

    public DataSession<TProvider> ToDataSession() => Session;
}

internal static class AsyncResourceDisposer
{
    internal static async ValueTask DisposeAsync(IAsyncDisposable primary, IAsyncDisposable? secondary)
    {
        var exceptions = await DisposeCollectingAsync(primary, secondary);
        if (exceptions.Count == 1) ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
        if (exceptions.Count > 1) throw new AggregateException("多个异步资源释放失败。", exceptions);
    }

    internal static async ValueTask<List<Exception>> DisposeCollectingAsync(
        IAsyncDisposable? primary,
        IAsyncDisposable? secondary)
    {
        var exceptions = new List<Exception>(2);
        try
        {
            if (primary is not null) await primary.DisposeAsync();
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
        finally
        {
            try
            {
                if (secondary is not null) await secondary.DisposeAsync();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        return exceptions;
    }
}
