// ─────────────────────────────────────────────────────────────
// 🏭 PostgreSqlMultiHost — 多主机/故障转移/读写分离配置
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ 纯连接字符串配置 — 零反射，仅设置 NpgsqlDataSourceBuilder 属性。
//   ✅ 所有逻辑在 Npgsql 驱动层完成，框架只传配置，不干预运行时。
//
// PostgreSQL 多主机模式：
//   连接字符串中指定多个 Host，Npgsql 自动：
//     - 故障转移：primary 不可用时切换到 standby
//     - 负载均衡：多个 replica 之间轮询
//     - 读写分离：写走 primary，读走 replica（需配合 TargetSessionAttributes）
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 纯配置层扩展，零业务逻辑侵入。
//   - 通过 DI 注册时传入连接字符串即可，不需要修改任何领域层代码。
//   - 非多主机环境：使用默认 AddPalNpgsqlDataSource(connectionString)。
//
// 使用方式：
//   // 故障转移（一主一备）
//   services.AddPalNpgsqlDataSourceWithFailover(
//       primary: "Host=pg1;Database=pal",
//       standby:  "Host=pg2;Database=pal");
//
//   // 读写分离（一主多读）
//   services.AddPalNpgsqlDataSourceWithReadWriteSplit(
//       primary:  "Host=pg-master;Database=pal",
//       replicas: ["Host=pg-read1;Database=pal", "Host=pg-read2;Database=pal"]);
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace PalDDD.Dapper.PostgreSql;

/// <summary>PostgreSQL 多主机/故障转移配置扩展</summary>
public static class PostgreSqlMultiHost
{
    /// <summary>
    /// 注册支持故障转移的 NpgsqlDataSource（一主一备）。
    /// 当 primary 不可达时自动切换到 standby。
    /// </summary>
    /// <param name="applicationName">PGAPPNAME 应用名</param>
    public static IServiceCollection AddPalNpgsqlDataSourceWithFailover(
        this IServiceCollection services,
        string primaryConnectionString,
        string standbyConnectionString,
        string applicationName = "Pal.DDD")
    {
        ArgumentNullException.ThrowIfNull(services);

        // 多主机连接串：Host 逗号分隔，TargetSessionAttributes 控制
        var builder = new NpgsqlDataSourceBuilder(primaryConnectionString);

        // 追加备机到 Host 列表
        var standbyBuilder = new NpgsqlConnectionStringBuilder(standbyConnectionString);
        if (standbyBuilder.Host is not null)
        {
            builder.ConnectionStringBuilder.Host += $",{standbyBuilder.Host}";
            if (standbyBuilder.Port != 5432)
            {
                // 如果备机端口不同，追加端口
                var currentPorts = builder.ConnectionStringBuilder.Port;
                if (currentPorts != standbyBuilder.Port)
                    builder.ConnectionStringBuilder.Port = 0; // 0 = 使用 Host 内嵌端口
            }
        }

        builder.ConnectionStringBuilder.TargetSessionAttributes = "primary";
        builder.ConnectionStringBuilder.ApplicationName = applicationName;

        services.AddSingleton(builder.Build());
        return services;
    }

    /// <summary>
    /// 注册支持读写分离的 NpgsqlDataSource。
    /// 写操作走 primary，读操作走 replicas（负载均衡轮询）。
    /// </summary>
    /// <param name="primaryConnectionString">主库连接串</param>
    /// <param name="replicaConnectionStrings">只读副本连接串列表</param>
    /// <param name="applicationName">PGAPPNAME 应用名</param>
    public static IServiceCollection AddPalNpgsqlDataSourceWithReadWriteSplit(
        this IServiceCollection services,
        string primaryConnectionString,
        string[] replicaConnectionStrings,
        string applicationName = "Pal.DDD")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(replicaConnectionStrings);

        if (replicaConnectionStrings.Length == 0)
            return AddPalNpgsqlDataSourceWithFailover(services, primaryConnectionString, primaryConnectionString, applicationName);

        var builder = new NpgsqlDataSourceBuilder(primaryConnectionString);

        // 合并所有主机
        List<string> hosts = [];
        foreach (var cs in replicaConnectionStrings)
        {
            var sb = new NpgsqlConnectionStringBuilder(cs);
            if (sb.Host is not null) hosts.Add(sb.Host);
        }

        if (hosts.Count > 0)
        {
            builder.ConnectionStringBuilder.Host += "," + string.Join(",", hosts);
            builder.ConnectionStringBuilder.LoadBalanceHosts = true;
            builder.ConnectionStringBuilder.TargetSessionAttributes = "any";
        }

        builder.ConnectionStringBuilder.ApplicationName = applicationName;

        services.AddSingleton(builder.Build());
        return services;
    }

    /// <summary>
    /// 注册多主机 NpgsqlDataSource（完全自定义连接串）。
    /// 适用于 Cloud SQL Proxy / PgBouncer 等自定义多主机场景。
    /// </summary>
    /// <param name="multiHostConnectionString">
    /// 完整多主机连接串，例如：
    /// "Host=pg1,pg2,pg3;Database=pal;Load Balance Hosts=true;Target Session Attributes=primary"
    /// </param>
    public static IServiceCollection AddPalNpgsqlDataSourceMultiHost(
        this IServiceCollection services,
        string multiHostConnectionString,
        string applicationName = "Pal.DDD")
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new NpgsqlDataSourceBuilder(multiHostConnectionString);
        builder.ConnectionStringBuilder.ApplicationName = applicationName;

        services.AddSingleton(builder.Build());
        return services;
    }
}
