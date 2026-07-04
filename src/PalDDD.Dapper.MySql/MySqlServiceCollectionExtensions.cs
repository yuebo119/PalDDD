// ─────────────────────────────────────────────────────────────
// 🔧 MySQL 增强 DI 注册扩展 — MySqlDataSource（.NET 7+ 标准模式）
// ─────────────────────────────────────────────────────────────
// MySqlDataSource 对比旧 MySqlConnection Singleton：
//
//   ❌ 旧: 手动 new MySqlConnection → AddSingleton → 用完不回收
//   ✅ 新: MySqlDataSourceBuilder → AddSingleton(MySqlDataSource)
//         自动提供: 连接池健康检查 / ILogger 集成 / OpenTelemetry 追踪
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 纯配置层扩展，零业务逻辑侵入。
//   - 通过 DI 注册时传入连接字符串即可，不修改任何领域层代码。
//   - 非 MySQL 环境：此扩展包不被引用，完全不影响行为。
//
// 使用方式：
//   services.AddPalMySqlDataSource("Server=localhost;Database=pal;User=root;Password=xxx");
//   services.AddPalMySqlDataSourceWithStores("Server=localhost;Database=pal;User=root;Password=xxx");
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

using PalDDD.Transactions;
namespace PalDDD.Dapper.MySql;

/// <summary>MySQL 增强 DI 注册扩展（MySqlDataSource 模式）</summary>
public static class MySqlServiceCollectionExtensions
{
    // ── MySqlDataSource（推荐）──

    /// <summary>
    /// 注册 MySqlDataSource 作为 Singleton，自动应用 InnoDB 性能优化。<br/>
    /// MySqlDataSource 是 MySqlConnector 2.x 的新一代连接管理 API，提供：<br/>
    ///   - 自动连接池管理 + 健康检查<br/>
    ///   - ILoggerFactory 日志集成<br/>
    ///   - OpenTelemetry 追踪（自动记录 SQL 执行）<br/>
    ///   - DbDataSource 标准接口（.NET 7+ 通用抽象）
    /// </summary>
    /// <param name="connectionString">MySQL 连接字符串</param>
    /// <param name="applyOptimization">是否应用 InnoDB 性能优化（默认 true）</param>
    public static IServiceCollection AddPalMySqlDataSource(
        this IServiceCollection services,
        string connectionString,
        bool applyOptimization = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new MySqlDataSourceBuilder(connectionString);

        // MySqlDataSource 自动使用 ILoggerFactory（无需手动传递）
        var dataSource = builder.Build();

        if (applyOptimization)
        {
            // 在第一个从池中创建的连接上应用会话级性能优化
            // SET SESSION 设置会随连接返回池而持久化（后续复用的连接也继承这些设置）
            ApplySessionOptimization(dataSource);
        }

        // 注册为 Singleton — MySqlDataSource 内部管理连接池
        services.AddSingleton(dataSource);
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource);

        return services;
    }

    /// <summary>注册 MySqlDataSource + Dapper Store（一键注册：连接 + Outbox + Inbox）</summary>
    /// <remarks>
    /// 委托核心 <c>AddPalDapperTransactions</c> 注册 Dapper Store（Outbox/Inbox/Saga），
    /// 避免手动构造（旧实现手动 new DapperOutboxStore/DapperInboxStore，重复核心逻辑）。
    /// </remarks>
    public static IServiceCollection AddPalMySqlDataSourceWithStores(
        this IServiceCollection services,
        string connectionString)
    {
        AddPalMySqlDataSource(services, connectionString);
        return DapperServiceCollectionExtensions.AddPalDapperTransactions(services, DapperDbType.MySql, connectionString);
    }

    // ── Legacy（旧 API 兼容）──

    /// <summary>注册 MySQL 连接（Singleton）并应用性能优化</summary>
    [System.Obsolete("请使用 AddPalMySqlDataSource 以获得自动连接池管理、健康检查和 OpenTelemetry 追踪。")]
    public static IServiceCollection AddPalMySql(
        this IServiceCollection services,
        string connectionString,
        bool applyOptimization = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        var connection = new MySqlConnection(connectionString);
        if (applyOptimization)
            MySqlPerformanceOptimizer.Optimize(connection);

        services.AddSingleton(connection);
        services.AddSingleton<System.Data.Common.DbConnection>(sp => sp.GetRequiredService<MySqlConnection>());

        return services;
    }

    /// <summary>注册 MySQL 连接 + Dapper Store（一键注册）</summary>
    [System.Obsolete("请使用 AddPalMySqlDataSourceWithStores 以获得自动连接池管理、健康检查和 OpenTelemetry 追踪。")]
    public static IServiceCollection AddPalMySqlWithStores(
        this IServiceCollection services,
        string connectionString)
    {
        AddPalMySql(services, connectionString);

        services.AddScoped<IPalOutboxStore>(sp =>
        {
            var conn = sp.GetRequiredService<MySqlConnection>();
            return new DapperOutboxStore(conn, DapperDbType.MySql);
        });

        services.AddScoped<IInboxStore>(sp =>
        {
            var conn = sp.GetRequiredService<MySqlConnection>();
            return new DapperInboxStore(conn, DapperDbType.MySql);
        });

        return services;
    }

    // ── 内部辅助 ──

    /// <summary>
    /// 在从池中获取的第一个连接上应用会话级 InnoDB 性能优化。<br/>
    /// 注意：SET SESSION 设置会随连接返回池而持久化，后续从同一物理连接派生的会话继承这些设置。<br/>
    /// 对于生产环境多连接场景，建议在 MySQL 服务端配置（my.cnf）中全局设置这些参数。
    /// </summary>
    private static void ApplySessionOptimization(MySqlDataSource dataSource)
    {
        try
        {
            using var conn = dataSource.CreateConnection();
            conn.Open();
            MySqlPerformanceOptimizer.Optimize(conn);
        }
        catch (MySqlException)
        {
            // 连接失败时跳过优化——应用仍可启动，使用 MySQL 默认设置
        }
    }
}
