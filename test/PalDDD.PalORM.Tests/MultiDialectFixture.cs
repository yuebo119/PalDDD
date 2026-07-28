using System.Data.Common;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Testcontainers 跨方言 Fixture —— 自动启动 PG/MySQL 容器，提供已建表的 DataSession。
/// <para>
/// <b>设计</b>：每个测试方法独立创建容器（无共享状态，零污染）。容器在 DisposeAsync 时自动停止。
/// SQLite 不需容器（:memory:）。
/// </para>
/// <para><b>前置条件</b>：本机运行 Docker（Testcontainers 通过 Docker API 管理容器）。</para>
/// </summary>
public sealed class MultiDialectFixture : IAsyncDisposable
{
    private PostgreSqlContainer? _pg;
    private MySqlContainer? _mySql;

    /// <summary>创建 SQLite :memory: 会话（无需容器）。</summary>
    public async Task<DataSession<SqliteProvider>> CreateSqliteAsync(CancellationToken ct = default)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct);
        await ApplySchemaAsync(session, MultiDialectSchema.Sqlite, ct);
        return session;
    }

    /// <summary>创建 PostgreSQL 会话（启动 Testcontainers 容器）。</summary>
    public async Task<DataSession<PostgreSqlProvider>> CreatePostgreSqlAsync(CancellationToken ct = default)
    {
        _pg = new PostgreSqlBuilder("postgres:latest")
            .WithDatabase("palddd_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
        await _pg.StartAsync(ct);

        var session = await DataSession<PostgreSqlProvider>.CreateAsync(
            DbOptions.Development(_pg.GetConnectionString()), ct);
        await ApplySchemaAsync(session, MultiDialectSchema.PostgreSql, ct);
        return session;
    }

    /// <summary>创建 MySQL 会话（启动 Testcontainers 容器）。</summary>
    public async Task<DataSession<MySqlProvider>> CreateMySqlAsync(CancellationToken ct = default)
    {
        _mySql = new MySqlBuilder("mysql:latest")
            .WithDatabase("palddd_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
        await _mySql.StartAsync(ct);

        var session = await DataSession<MySqlProvider>.CreateAsync(
            DbOptions.Development(_mySql.GetConnectionString()), ct);
        await ApplySchemaAsync(session, MultiDialectSchema.MySql, ct);
        return session;
    }

    /// <summary>逐条执行建表 DDL（DDL 是静态字符串无参数，用 GetRawConnection + ExecuteNonQueryAsync）。</summary>
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
        if (_mySql is not null) await _mySql.DisposeAsync();
    }
}
