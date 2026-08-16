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
        var primaryBuilder = new NpgsqlConnectionStringBuilder(primaryConnectionString);

        // 追加备机到 Host 列表
        var standbyBuilder = new NpgsqlConnectionStringBuilder(standbyConnectionString);
        // P2/P3 修复（十七轮 · 镜像 MySqlMultiHost.AddPalMySqlDataSourceWithFailover）：快速失败校验——
        // Npgsql 连接串的 Username/Password/Database 对主机列表内所有节点统一生效，
        // 备机串与主库不一致时差异无法表达且被静默丢弃（故障转移后必然连接失败/连错库）。
        // Port 不校验：已编码进 Host 条目（host:port 语法，见 EncodeHostEntry 注释）。
        ThrowIfCredentialsMismatch(primaryBuilder, standbyBuilder, "standby");
        if (standbyBuilder.Host is not null)
        {
            // ITM-132 修复：primary Port≠5432 时，未编码的备机 Host 会继承连接串共享 Port
            // （Npgsql 的 Port 只对未内嵌端口的主机生效），导致备机被连到主库端口——
            // 统一经 EncodeHostEntry 编码：primary Port≠5432 时全部 Host 显式 host:port（含显式 5432）。
            var standbyHost = EncodeHostEntry(standbyBuilder, primaryBuilder.Port);
            // ITM-110 修复：拼接规范化——主串无 Host 时原 `Host += ",{standbyHost}"` 产生
            // 前导逗号（",pg2"），Npgsql 解析出空主机条目；改为空则直接赋值
            var primaryHost = builder.ConnectionStringBuilder.Host;
            builder.ConnectionStringBuilder.Host = string.IsNullOrWhiteSpace(primaryHost)
                ? standbyHost
                : $"{primaryHost},{standbyHost}";
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
        {
            // ITM-110 修复：零副本时直接注册主库单数据源——原实现回退 failover(primary, primary)
            // 产生重复 Host（"pg1,pg1"），驱动层视为主备两份（故障转移/负载语义错乱）
            var soloBuilder = new NpgsqlDataSourceBuilder(primaryConnectionString);
            soloBuilder.ConnectionStringBuilder.ApplicationName = applicationName;
            soloBuilder.ConnectionStringBuilder.TargetSessionAttributes = "primary";
            services.AddSingleton(soloBuilder.Build());
            return services;
        }

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
            // ITM-132 修复：primary Port≠5432 时，未编码的副本 Host 会继承连接串共享 Port
            // （Npgsql 的 Port 只对未内嵌端口的主机生效），读流量/故障转移落到错误实例——
            // 统一经 EncodeHostEntry 编码：primary Port≠5432 时全部 Host 显式 host:port（含显式 5432）。
            if (sb.Host is not null)
                hosts.Add(EncodeHostEntry(sb, primaryCsBuilder.Port));
        }

        if (hosts.Count > 0)
        {
            // ITM-110 修复：拼接规范化——主串无 Host 时直接赋值，避免前导逗号（同
            // AddPalNpgsqlDataSourceWithFailover 的 ITM-110 修复）
            var primaryHost = builder.ConnectionStringBuilder.Host;
            builder.ConnectionStringBuilder.Host = string.IsNullOrWhiteSpace(primaryHost)
                ? string.Join(",", hosts)
                : $"{primaryHost},{string.Join(",", hosts)}";
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

    /// <summary>
    /// ITM-132 修复（端口编码纯函数）：将副本/备机 Host 条目编码为 host:port。
    /// <para>
    /// Npgsql 的共享 Port 只对<b>未内嵌端口</b>的主机生效：当主库 Port 非 5432 时，
    /// 未编码的副本/备机 Host 会错误继承主库 Port（读流量/故障转移落到错误实例）。
    /// 因此 <paramref name="primaryPort"/> != 5432 时必须对全部 Host 显式编码（含显式 5432）；
    /// <paramref name="primaryPort"/> == 5432 时仅对非 5432 端口的 Host 编码
    /// （未编码 Host 继承 5432 语义正确）。
    /// </para>
    /// </summary>
    /// <param name="hostBuilder">副本/备机连接串（读取其 Host/Port）。</param>
    /// <param name="primaryPort">主库连接串 Port（决定是否强制全部显式编码）。</param>
    /// <returns>可直接拼接进多主机 Host 列表的条目（如 <c>pg2:5433</c>、<c>pg2:5432</c> 或 <c>pg2</c>）。</returns>
    internal static string EncodeHostEntry(NpgsqlConnectionStringBuilder hostBuilder, int primaryPort)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);

        var host = hostBuilder.Host;
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("多主机合并的每个节点都必须显式指定 Host。", nameof(hostBuilder));

        return primaryPort != 5432 || hostBuilder.Port != 5432
            ? $"{host}:{hostBuilder.Port}"
            : host;
    }
}
