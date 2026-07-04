// ─────────────────────────────────────────────────────────────
// 🏭 MySqlMultiHost — 多主机/故障转移/读写分离配置
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ 纯连接字符串配置 — 零反射，仅设置 MySqlDataSourceBuilder 参数。
//   ✅ 所有逻辑在 MySqlConnector 驱动层完成，框架只传配置，不干预运行时。
//
// MySQL 多主机模式（MySqlConnector 2.x 内置）：
//   连接字符串中指定多个 Host，MySqlConnector 自动：
//     - 故障转移：primary 不可用时切换到 standby
//     - 负载均衡：RoundRobin / Random / LeastConnections
//     - 读写分离：配合 ProxySQL / MySQL Router 或应用层路由
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 纯配置层扩展，零业务逻辑侵入。
//   - 通过 DI 注册时传入连接字符串即可，不修改任何领域层代码。
//   - 非多主机环境：使用默认 AddPalMySqlDataSource(connectionString)。
//
// 使用方式：
//   // 故障转移（一主一备）
//   services.AddPalMySqlDataSourceWithFailover(
//       primary: "Server=mysql-master;Database=pal",
//       standby: "Server=mysql-standby;Database=pal");
//
//   // 负载均衡（多主机轮询）
//   services.AddPalMySqlDataSourceWithLoadBalance(
//       "Server=mysql-1,mysql-2,mysql-3;Database=pal");
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace PalDDD.Dapper.MySql;

/// <summary>MySQL 多主机/故障转移配置扩展</summary>
public static class MySqlMultiHost
{
    /// <summary>
    /// 注册支持故障转移的 MySqlDataSource（一主一备）。<br/>
    /// 当 primary 不可达时自动切换到 standby。
    /// </summary>
    /// <param name="primaryConnectionString">主库连接字符串</param>
    /// <param name="standbyConnectionString">备库连接字符串</param>
    public static IServiceCollection AddPalMySqlDataSourceWithFailover(
        this IServiceCollection services,
        string primaryConnectionString,
        string standbyConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        var primaryBuilder = new MySqlConnectionStringBuilder(primaryConnectionString);
        var standbyBuilder = new MySqlConnectionStringBuilder(standbyConnectionString);

        // 合并主机列表
        primaryBuilder.Server = $"{primaryBuilder.Server},{standbyBuilder.Server}";

        // 故障转移模式：默认先连第一个，失败再试后续
        primaryBuilder.LoadBalance = MySqlLoadBalance.FailOver;

        var dataSource = new MySqlDataSourceBuilder(primaryBuilder.ConnectionString).Build();

        services.AddSingleton(dataSource);
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource);

        return services;
    }

    /// <summary>
    /// 注册支持负载均衡的 MySqlDataSource（多主机轮询）。<br/>
    /// 使用 RoundRobin 策略在多台 MySQL 实例之间分发连接。
    /// </summary>
    /// <param name="connectionString">
    /// 包含多个 Server 的连接字符串，例如：<br/>
    /// "Server=mysql-1,mysql-2,mysql-3;Database=pal;LoadBalance=RoundRobin;MaximumPoolSize=50"
    /// </param>
    public static IServiceCollection AddPalMySqlDataSourceWithLoadBalance(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            LoadBalance = MySqlLoadBalance.RoundRobin,
            Pooling = true
        };

        var dataSource = new MySqlDataSourceBuilder(builder.ConnectionString).Build();

        services.AddSingleton(dataSource);
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource);

        return services;
    }

    /// <summary>
    /// 注册支持最小连接数策略的 MySqlDataSource。<br/>
    /// 新连接优先选择当前连接数最少的服务器，适合负载不均的场景。
    /// </summary>
    public static IServiceCollection AddPalMySqlDataSourceWithLeastConnections(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            LoadBalance = MySqlLoadBalance.LeastConnections,
            Pooling = true
        };

        var dataSource = new MySqlDataSourceBuilder(builder.ConnectionString).Build();

        services.AddSingleton(dataSource);
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource);

        return services;
    }
}
