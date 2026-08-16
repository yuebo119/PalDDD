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

        // 优化（二十五轮 B3）：foreign_keys 移入连接串——驱动层每物理连接首开自动发送
        // （比 PRAGMA 批每 scope 重跑可靠；PRAGMA 批已移除该行）
        var csb = new SqliteConnectionStringBuilder(connectionString) { ForeignKeys = true };
        connectionString = csb.ConnectionString;
        var isMemory = IsMemoryDataSource(new SqliteConnectionStringBuilder(connectionString).DataSource);
        if (isMemory)
        {
            // ⚠️ 线程安全契约（八轮评审 P3 补强声明，不加 SemaphoreSlim 串行化——包装连接改动面大）：
            // :memory: 必须 Singleton（连接关闭数据即销毁），但 SqliteConnection 非线程安全——
            // 并发 scope 共享此 Singleton 连接属未定义行为（SQLite Error 5 "database is locked" /
            // 交叉读写损坏）。契约：调用方必须串行访问（单线程应用 / 逐个 await 的测试）；
            // 需要并发时改用文件模式（Scoped 连接隔离）或 AddPalSqliteInMemory(sharedCache: true)。
            var connection = new SqliteConnection(connectionString);
            ApplyOptimization(connection, optimize, isMemory);
            services.AddSingleton(connection);
            services.AddSingleton<System.Data.Common.DbConnection>(sp => sp.GetRequiredService<SqliteConnection>());
        }
        else
        {
            // P2 修复：文件模式改 Scoped——每 scope 独立连接（PRAGMA 逐连接生效，工厂内应用）
            services.AddScoped<SqliteConnection>(_ =>
            {
                var c = new SqliteConnection(connectionString);
                ApplyOptimization(c, optimize, isMemory);
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

    private static void ApplyOptimization(SqliteConnection connection, SqliteOptimizeLevel level, bool isMemory)
    {
        connection.Open();

        // ITM-135 修复：内存数据源跳过 WAL 确认并降级为 InMemory 级 pragma——
        // :memory: 上 PRAGMA journal_mode=WAL 恒返回 "memory"（无法切换 WAL），
        // 默认 Production 的 WAL 确认会在注册启动时误抛 InvalidOperationException。
        // None 级别保持原语义（明确要求不优化时不注入任何 pragma）。
        var effectiveLevel = isMemory && level is (SqliteOptimizeLevel.Production or SqliteOptimizeLevel.Light)
            ? SqliteOptimizeLevel.InMemory
            : level;

        var sql = SqlitePerformanceOptimizer.GetPragma(effectiveLevel);
        if (sql.Length == 0) return;

        // WAL 模式需单独执行确认切换成功，其余 PRAGMA 批量执行
        // P2/P3 修复（十七轮）：改消费 SqlitePerformanceOptimizer 常量——消除魔法切片偏移
        // （原 sql["PRAGMA journal_mode=WAL;\n".Length..] 与 GetPragma 输出硬耦合，
        // 与 Optimizer.ApplyAsync 同款改造，双消费方单一来源）
        // P3 修复（二十一轮）：WAL 确认声明落地——读 PRAGMA 返回值比对 "wal"，
        // 失配抛异常（与 Optimizer.ApplyAsync 同款语义，同步路径版本）。
        if (!isMemory && effectiveLevel is (SqliteOptimizeLevel.Production or SqliteOptimizeLevel.Light))
        {
            using var walCmd = connection.CreateCommand();
            walCmd.CommandText = SqlitePerformanceOptimizer.WalPragma;
            var walResult = walCmd.ExecuteScalar();
            if (!string.Equals(walResult?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"PRAGMA journal_mode=WAL 未生效（返回 {walResult?.ToString() ?? "<null>"}）——"
                    + "目标卷可能不支持 WAL（网络盘/只读卷）；WAL 依赖的并发读写语义不会成立。");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = level == SqliteOptimizeLevel.Production
                ? SqlitePerformanceOptimizer.ProductionRestPragma
                : SqlitePerformanceOptimizer.LightRestPragma;
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// ITM-111 修复：精确判定 SQLite 内存数据源——原 <c>Contains(":memory:")</c> 子串匹配
    /// 会把文件名恰含 ":memory:" 子串的文件库误判为内存库（误配 Singleton 生命周期 +
    /// 连接关闭数据即销毁，误判直接丢数据）。判定规则对齐 SQLite 语义：
    /// DataSource 为 ":memory:" 字面量、<c>file::memory:</c> URI 形式（含 shared cache
    /// 变体 <c>file::memory:?cache=shared</c>）、或 <c>file:</c> URI 查询参数含
    /// <c>mode=memory</c>（命名内存库形式）。
    /// </summary>
    private static bool IsMemoryDataSource(string dataSource)
    {
        if (dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return false;

        var queryIndex = dataSource.IndexOf('?');
        return queryIndex >= 0
            && dataSource.AsSpan(queryIndex + 1).Contains("mode=memory", StringComparison.OrdinalIgnoreCase);
    }
}
