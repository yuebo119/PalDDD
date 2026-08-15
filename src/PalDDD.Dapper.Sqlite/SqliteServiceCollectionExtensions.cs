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
    /// 注册 SQLite 连接并应用性能优化（生命周期按连接串自动选择，P2 修复后）：
    /// :memory: → Singleton（连接关闭数据即销毁）；文件模式 → Scoped（非线程安全）。
    /// </summary>
    /// <param name="connectionString">SQLite 连接字符串</param>
    /// <param name="optimize">优化级别（默认 Production — WAL + 性能 PRAGMA）</param>
    /// <remarks>
    /// 生命周期按连接串自动选择（P2 修复）：<br/>
    /// · <c>:memory:</c> → <b>Singleton</b>（连接关闭后数据即销毁，必须保持单连接）；<br/>
    /// · 文件模式 → <b>Scoped</b>（SqliteConnection 非线程安全，Singleton 在并发请求下属未定义行为）。<br/>
    /// 不建议同时调用 <c>AddPalSqlite</c> 和 <c>AddPalDapperTransactions</c>——两者都注册 <c>DbConnection</c>。
    /// </remarks>
    public static IServiceCollection AddPalSqlite(
        this IServiceCollection services,
        string connectionString,
        SqliteOptimizeLevel optimize = SqliteOptimizeLevel.Production)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            // ⚠️ 线程安全契约（八轮评审 P3 补强声明，不加 SemaphoreSlim 串行化——包装连接改动面大）：
            // :memory: 必须 Singleton（连接关闭数据即销毁），但 SqliteConnection 非线程安全——
            // 并发 scope 共享此 Singleton 连接属未定义行为（SQLite Error 5 "database is locked" /
            // 交叉读写损坏）。契约：调用方必须串行访问（单线程应用 / 逐个 await 的测试）；
            // 需要并发时改用文件模式（Scoped 连接隔离）或 AddPalSqliteInMemory(sharedCache: true)。
            var connection = new SqliteConnection(connectionString);
            ApplyOptimization(connection, optimize);
            services.AddSingleton(connection);
            services.AddSingleton<System.Data.Common.DbConnection>(sp => sp.GetRequiredService<SqliteConnection>());
        }
        else
        {
            // P2 修复：文件模式改 Scoped——每 scope 独立连接（PRAGMA 逐连接生效，工厂内应用）
            services.AddScoped<SqliteConnection>(_ =>
            {
                var c = new SqliteConnection(connectionString);
                ApplyOptimization(c, optimize);
                return c;
            });
            services.AddScoped<System.Data.Common.DbConnection>(sp => sp.GetRequiredService<SqliteConnection>());
        }

        // ✅ SQLite TypeHandler 已通过 [ModuleInitializer] + [module:DapperAot] 在 DapperAotInitializer.cs 注册
        // 不再需要运行时 RegisterTypeHandlers() 调用

        return services;
    }

    /// <summary>
    /// 注册 SQLite 内存数据库（测试用）。
    /// Data Source=:memory: — 连接关闭后数据销毁。
    /// </summary>
    /// <param name="sharedCache">
    /// 是否使用共享缓存（跨连接保持数据）。P2 探针实证修复：共享形式必须用
    /// <c>file::memory:?cache=shared</c> URI 语法——裸形式 <c>:memory:?cache=shared</c>
    /// 会被当普通文件名（Windows 上 ? 非法），Open 直接抛 SQLite Error 14。
    /// </param>
    public static IServiceCollection AddPalSqliteInMemory(
        this IServiceCollection services,
        bool sharedCache = false)
    {
        var cs = sharedCache
            ? "Data Source=file::memory:?cache=shared"
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
