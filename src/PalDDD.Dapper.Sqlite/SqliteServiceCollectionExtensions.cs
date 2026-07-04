// ─────────────────────────────────────────────────────────────
// 🔧 SQLite 增强 DI 注册扩展
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ Microsoft.Data.Sqlite — 纯托管 + native interop，零反射。
//   ✅ PRAGMA SQL — 纯字符串执行，运行时零类型推断。
//
// 使用方式（Program.cs）：
//
//   // 文件数据库（生产）
//   services.AddPalSqlite("Data Source=pal.db");
//
//   // 内存数据库（测试）
//   services.AddPalSqliteInMemory();
//
//   // 配合 Dapper Store
//   services.AddPalSqlite("Data Source=pal.db");
//   services.AddPalDapperTransactions(DapperDbType.Sqlite, "Data Source=pal.db");
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 纯基础设施扩展包，在 Dapper 适配器层之上。
//   - 不修改任何核心抽象或接口。
//   - 非 SQLite 环境不引用此包。
// ─────────────────────────────────────────────────────────────

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
namespace PalDDD.Dapper.Sqlite;

/// <summary>SQLite 优化级别</summary>
public enum SqliteOptimizeLevel
{ None, Light, Production, InMemory }

/// <summary>SQLite 增强 DI 注册扩展</summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// 注册 SQLite 连接（Singleton）并应用性能优化。
    /// 需要配合 AddPalDapperTransactions 注册 Dapper Store。
    /// <summary>
    /// 注册 SQLite 连接和优化配置。
    /// </summary>
    /// <param name="connectionString">SQLite 连接字符串</param>
    /// <param name="optimize">优化级别（默认 Production — WAL + 性能 PRAGMA）</param>
    /// <remarks>
    /// ⚠️ SQLite 连接注册为 <b>Singleton</b>（与核心 Dapper 层的 Scoped 策略不同）。<br/>
    /// 原因：SQLite :memory: 数据库连接关闭后数据即销毁，因此必须在应用生命周期内保持同一连接。<br/>
    /// 若使用文件模式且需要 Scoped 生命周期，请直接使用 <c>AddPalDapperTransactions(DapperDbType.Sqlite, connStr)</c>。<br/>
    /// 不建议同时调用 <c>AddPalSqlite</c> 和 <c>AddPalDapperTransactions</c>——两者都注册 <c>DbConnection</c>。
    /// </remarks>
    public static IServiceCollection AddPalSqlite(
        this IServiceCollection services,
        string connectionString,
        SqliteOptimizeLevel optimize = SqliteOptimizeLevel.Production)
    {
        ArgumentNullException.ThrowIfNull(services);

        var connection = new SqliteConnection(connectionString);
        ApplyOptimization(connection, optimize);

        services.AddSingleton(connection);
        services.AddSingleton<System.Data.Common.DbConnection>(sp => sp.GetRequiredService<SqliteConnection>());

        // 🔧 注册 SQLite TypeHandler — 运行时 Dapper TEXT→Guid/DateTimeOffset 映射
        SqliteRowFactory.RegisterTypeHandlers();

        return services;
    }

    /// <summary>
    /// 注册 SQLite 内存数据库（测试用）。
    /// Data Source=:memory: — 连接关闭后数据销毁。
    /// </summary>
    /// <param name="sharedCache">是否使用共享缓存（跨连接保持数据）</param>
    public static IServiceCollection AddPalSqliteInMemory(
        this IServiceCollection services,
        bool sharedCache = false)
    {
        var cs = sharedCache
            ? "Data Source=:memory:?cache=shared"
            : "Data Source=:memory:";

        return AddPalSqlite(services, cs, SqliteOptimizeLevel.InMemory);
    }

    private static void ApplyOptimization(SqliteConnection connection, SqliteOptimizeLevel level)
    {
        connection.Open();

        var sql = SqlitePerformanceOptimizer.GetPragma(level);
        if (sql.Length == 0) return;

        // WAL 模式需单独执行确认切换成功，其余 PRAGMA 批量执行
        if (level is SqliteOptimizeLevel.Production or SqliteOptimizeLevel.Light)
        {
            using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL";
            walCmd.ExecuteNonQuery();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql["PRAGMA journal_mode=WAL;\n".Length..];
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
