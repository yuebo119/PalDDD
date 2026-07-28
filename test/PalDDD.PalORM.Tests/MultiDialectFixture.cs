using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace PalDDD.PalORM.Tests;

/// <summary>
/// Testcontainers 跨方言 Fixture —— 返回 <see cref="TestSession{TProvider}"/> 包装类型。
/// <para>
/// <b>资源清理</b>：TestSession 实现 IAsyncDisposable，dispose 时自动停止容器。
/// 测试必须用 <c>await using var ts = await MultiDialectFixture.CreateXxxAsync()</c>。
/// </para>
/// <para><b>前置条件</b>：本机运行 Docker。</para>
/// </summary>
public static class MultiDialectFixture
{
    public static async Task<TestSession<SqliteProvider>> CreateSqliteAsync(CancellationToken ct = default)
    {
        var session = await DataSession<SqliteProvider>.CreateAsync(
            DbOptions.Development("Data Source=:memory:"), ct);
        await ApplySchemaAsync(session, MultiDialectSchema.Sqlite, ct);
        return new TestSession<SqliteProvider>(session, null, null);
    }

    public static async Task<TestSession<PostgreSqlProvider>> CreatePostgreSqlAsync(CancellationToken ct = default)
    {
        var pg = new PostgreSqlBuilder("postgres:latest")
            .WithDatabase("palddd_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
        await pg.StartAsync(ct);

        var session = await DataSession<PostgreSqlProvider>.CreateAsync(
            DbOptions.Development(pg.GetConnectionString()), ct);
        await ApplySchemaAsync(session, MultiDialectSchema.PostgreSql, ct);
        return new TestSession<PostgreSqlProvider>(session, pg, null);
    }

    public static async Task<TestSession<MySqlProvider>> CreateMySqlAsync(CancellationToken ct = default)
    {
        var mysql = new MySqlBuilder("mysql:8.4.10")
            .WithDatabase("palddd_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
        await mysql.StartAsync(ct);

        var session = await DataSession<MySqlProvider>.CreateAsync(
            DbOptions.Development(mysql.GetConnectionString()), ct);
        await ApplySchemaAsync(session, MultiDialectSchema.MySql, ct);
        return new TestSession<MySqlProvider>(session, null, mysql);
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
}

/// <summary>测试会话包装 —— dispose 时自动停止容器 + 释放 DataSession。</summary>
public sealed class TestSession<TProvider> : IAsyncDisposable
    where TProvider : IDbProvider
{
    public DataSession<TProvider> Session { get; }
    private readonly PostgreSqlContainer? _pg;
    private readonly MySqlContainer? _mySql;

    internal TestSession(DataSession<TProvider> session, PostgreSqlContainer? pg, MySqlContainer? mySql)
    {
        Session = session;
        _pg = pg;
        _mySql = mySql;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
        if (_mySql is not null) await _mySql.DisposeAsync();
    }

    public static implicit operator DataSession<TProvider>(TestSession<TProvider> ts) => ts.Session;

    [SuppressMessage("Design", "CA2225", Justification = "测试基础设施，隐式转换足够。")]
    public DataSession<TProvider> ToDataSession() => Session;
}
