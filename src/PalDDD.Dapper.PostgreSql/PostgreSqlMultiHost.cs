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
        // P2/P3 修复（十七轮 · 镜像 MySqlMultiHost.AddPalMySqlDataSourceWithFailover）：快速失败校验——
        // Npgsql 连接串的 Username/Password/Database 对主机列表内所有节点统一生效，
        // 备机串与主库不一致时差异无法表达且被静默丢弃（故障转移后必然连接失败/连错库）。
        // Port 不校验：已编码进 Host 条目（host:port 语法，见下方实证修正注释）。
        ThrowIfCredentialsMismatch(new NpgsqlConnectionStringBuilder(primaryConnectionString), standbyBuilder, "standby");
        if (standbyBuilder.Host is not null)
        {
            // 实证修正（Npgsql 10.0.3 实测）：Port=0 的赋值直接抛 ArgumentOutOfRangeException，
            // 旧注释"0 = 使用 Host 内嵌端口"不成立。Npgsql 的共享 Port 只对未内嵌端口的主机生效，
            // 备机端口不同时正确做法是把端口编码进该主机的 Host 条目（Host 条目支持 host:port 语法）。
            var standbyHost = standbyBuilder.Port != 5432
                ? $"{standbyBuilder.Host}:{standbyBuilder.Port}"
                : standbyBuilder.Host;
            builder.ConnectionStringBuilder.Host += $",{standbyHost}";
        }

        builder.ConnectionStringBuilder.TargetSessionAttributes = "primary";
        builder.ConnectionStringBuilder.ApplicationName = applicationName;

        services.AddSingleton(builder.Build());
        return services;
    }

    /// <summary>
    /// 注册多主机、主库亲和的 NpgsqlDataSource（ITM-067 语义修正）。
    /// <para>
    /// 全部流量（含读）走 primary：多主机合并仅用于主库发现与故障转移（TargetSessionAttributes=primary）。
    /// ⚠️ 此方法<b>不做读写分离</b>——写操作路由到只读副本会导致失败，故数据源必须主库亲和。
    /// 真正的读写分离（写走 primary、读负载均衡 replicas）请用
    /// <see cref="PostgreSqlReadWriteRouterExtensions.AddPalReadWriteRouter"/>（双 DataSource 方案）。
    /// </para>
    /// </summary>
    /// <param name="primaryConnectionString">主库连接串</param>
    /// <param name="replicaConnectionStrings">只读副本连接串列表（用于主库发现/故障转移合并）</param>
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
        var primaryCsBuilder = new NpgsqlConnectionStringBuilder(primaryConnectionString);
        foreach (var cs in replicaConnectionStrings)
        {
            var sb = new NpgsqlConnectionStringBuilder(cs);
            // P2/P3 修复（十七轮 · 镜像 MySQL failover 校验）：副本凭据与主库不一致时快速失败——
            // 合并方式只保留 Host 条目，Username/Password/Database 差异被静默丢弃
            ThrowIfCredentialsMismatch(primaryCsBuilder, sb, "replica");
            // PD17 姊妹统一：端口编码进 Host 条目（与 Failover 方法 62-64 行实证修正对齐）
            if (sb.Host is not null)
                hosts.Add(sb.Port != 5432 ? $"{sb.Host}:{sb.Port}" : sb.Host);
        }

        if (hosts.Count > 0)
        {
            builder.ConnectionStringBuilder.Host += "," + string.Join(",", hosts);
            builder.ConnectionStringBuilder.LoadBalanceHosts = true;
            // ITM-067：必须 primary 亲和——"any" 会把写操作负载均衡到只读副本导致写失败
            builder.ConnectionStringBuilder.TargetSessionAttributes = "primary";
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

    /// <summary>
    /// P2/P3 修复（十七轮）：副本连接串的 Username/Password/Database 与主库不一致时抛明确异常——
    /// Npgsql 连接串的凭据/库名对主机列表内全部节点统一生效，多主机合并只保留 Host 条目，
    /// 差异被静默丢弃（故障转移后必然连接失败/连错库）。镜像 MySqlMultiHost 快速失败模式。
    /// </summary>
    /// <param name="primary">主库连接串（凭据基准）。</param>
    /// <param name="replica">副本连接串（仅 Host/Port 应与主库不同）。</param>
    /// <param name="replicaRole">角色名（用于异常消息，如 "standby" / "replica"）。</param>
    internal static void ThrowIfCredentialsMismatch(
        NpgsqlConnectionStringBuilder primary, NpgsqlConnectionStringBuilder replica, string replicaRole)
    {
        if (!string.Equals(replica.Username, primary.Username, StringComparison.Ordinal)
            || !string.Equals(replica.Password, primary.Password, StringComparison.Ordinal)
            || !string.Equals(replica.Database, primary.Database, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{replicaRole} 与主库的 Username/Password/Database 必须一致：Npgsql 连接串的这些参数对主机列表内全部节点统一生效，"
                + "多主机合并只保留 Host 条目（端口经 host:port 内嵌），凭据/库名差异无法表达且会被静默丢弃"
                + "（故障转移后必然连接失败或连错库）。请为节点配置相同账号与库，或使用 AddPalNpgsqlDataSourceMultiHost 自定义完整连接串。");
        }
    }
}
