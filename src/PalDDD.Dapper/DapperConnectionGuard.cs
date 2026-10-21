using System.Data.Common;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace PalDDD.Dapper;

/// <summary>
/// 连接与方言一致性守卫（2026-10-21 契约盘点）。
/// <para>
/// 背景：Dapper store 的方言由 <see cref="DapperDbType"/> 决定，连接由调用方提供；二者不一致时
/// 现有实现会在首次查询抛 provider 原生语法错误（如 SQLite 上出现 <c>near "FOR": syntax error</c>），
/// 缺连接串则在 SQLite 上表现为 <c>no such table</c>，都不指向真正的问题。
/// 本守卫在构造期做两项可行动校验：连接类型 ↔ 方言、连接串非空。
/// </para>
/// <para>
/// 只在能识别连接类型时判定方言（自定义包装/装饰连接放行），避免误伤代理形态。
/// 连接串必须在构造 store 前配置——store 不拥有连接，也不延迟绑定连接串。
/// </para>
/// </summary>
internal static class DapperConnectionGuard
{
    /// <summary>校验连接与方言一致且连接串已配置。</summary>
    /// <exception cref="ArgumentException">方言与连接类型不匹配，或连接串为空。</exception>
    internal static void Validate(DbConnection connection, DapperDbType dbType)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var detected = connection switch
        {
            SqliteConnection => (DapperDbType?)DapperDbType.Sqlite,
            NpgsqlConnection => DapperDbType.PostgreSql,
            MySqlConnection => DapperDbType.MySql,
            _ => null,
        };

        if (detected is { } actual && actual != dbType)
        {
            throw new ArgumentException(
                $"DapperDbType.{dbType} 与连接类型 {connection.GetType().FullName} 不匹配（该连接对应 {actual}）。"
                + "请修正 store 构造调用的 dbType 参数，或改用与方言一致的 DbConnection；"
                + "否则 SQL 方言错配会以 provider 语法错误的形式出现在首次查询。",
                nameof(dbType));
        }

        if (string.IsNullOrWhiteSpace(connection.ConnectionString))
        {
            throw new ArgumentException(
                $"{connection.GetType().Name} 未配置连接串。请在构造 store 前设置 ConnectionString："
                + "PostgreSQL 形如 Host=127.0.0.1;Database=app;Username=u;Password=p；"
                + "MySQL 形如 Server=127.0.0.1;Database=app;User ID=u;Password=p；"
                + "SQLite 形如 Data Source=app.db 或 Data Source=:memory:。",
                nameof(connection));
        }
    }
}
