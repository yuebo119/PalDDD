using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using PalORM;
using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Sqlite;

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
        return new TestSession<SqliteProvider>(session);
    }

    public static async Task<TestSession<PostgreSqlProvider>> CreatePostgreSqlAsync(CancellationToken ct = default)
    {
        var cs = Environment.GetEnvironmentVariable("PALORM_PG_CONNECTION")
            ?? "ENV_PG_NOT_SET";
        var session = await DataSession<PostgreSqlProvider>.CreateAsync(DbOptions.Development(cs), ct);
        await CleanAllTablesAsync(session, ct);
        await ApplySchemaAsync(session, MultiDialectSchema.PostgreSql, ct);
        return new TestSession<PostgreSqlProvider>(session);
    }

    public static async Task<TestSession<MySqlProvider>> CreateMySqlAsync(CancellationToken ct = default)
    {
        var cs = Environment.GetEnvironmentVariable("PALORM_MYSQL_CONNECTION")
            ?? "ENV_MYSQL_NOT_SET";
        var session = await DataSession<MySqlProvider>.CreateAsync(DbOptions.Development(cs), ct);
        await CleanAllTablesAsync(session, ct);
        await ApplySchemaAsync(session, MultiDialectSchema.MySql, ct);
        return new TestSession<MySqlProvider>(session);
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

    /// <summary>清理所有表（DROP TABLE IF EXISTS）—— 远程共享数据库每测试前必须清理。</summary>
    private static async Task CleanAllTablesAsync<TProvider>(DataSession<TProvider> session, CancellationToken ct)
        where TProvider : IDbProvider
    {
        var conn = session.GetRawConnection();
        var tables = new[] { "outbox_messages", "inbox_messages", "saga_states", "events", "projection_checkpoints", "idempotency_records" };
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            // 先删 INDEX（部分方言不支持 IF EXISTS on index），再删表
            cmd.CommandText = $"DROP TABLE IF EXISTS {table}";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // 删除可能残留的索引
        var indexes = new[] { "idx_inbox_unique", "idx_events_stream", "idx_outbox_status", "idx_projection_checkpoints_status", "idx_idempotency_expires" };
        foreach (var idx in indexes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP INDEX IF EXISTS {idx}";
            try { await cmd.ExecuteNonQueryAsync(ct); } catch { /* 某些方言不支持 IF EXISTS on index */ }
        }
    }
}

/// <summary>测试会话包装 —— dispose 时释放 DataSession（远程数据库无需停止容器）。</summary>
public sealed class TestSession<TProvider> : IAsyncDisposable
    where TProvider : IDbProvider
{
    public DataSession<TProvider> Session { get; }

    internal TestSession(DataSession<TProvider> session) => Session = session;

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await Session.DisposeAsync();

    [SuppressMessage("Design", "CA2225", Justification = "测试基础设施，隐式转换足够。")]
    public static implicit operator DataSession<TProvider>(TestSession<TProvider> ts) => ts.Session;

    [SuppressMessage("Design", "CA2225", Justification = "测试基础设施，隐式转换足够。")]
    public DataSession<TProvider> ToDataSession() => Session;
}
