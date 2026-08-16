// ─────────────────────────────────────────────────────────────
// 🔧 PostgreSQL 增强 DI 注册扩展
// ─────────────────────────────────────────────────────────────
// 使用方式（Program.cs，仅 PostgreSQL 环境）：
//
//   // NpgsqlDataSource（推荐，自动设置应用名）：
//   services.AddPalNpgsqlDataSource(connectionString, "MyApp");
//
//   // 高级配置（自定义类型映射）：
//   services.AddPalNpgsqlDataSource(connectionString, builder => {
//       builder.ConnectionStringBuilder.ApplicationName = "MyApp";
//       builder.UseNodaTime();  // NodaTime 类型映射
//   });
//
//   // LISTEN/NOTIFY 实时通知（可选）：
//   services.AddPalPostgreSqlOutboxNotifier("pal_outbox_notify");
//
// 架构说明：
//   此扩展包不修改任何核心抽象或接口。
//   - NpgsqlDataSource 是 Npgsql 7+ 的新一代连接管理 API，
//     自动管理连接池、负载均衡和故障转移，绕过 ADO.NET 通用抽象。
//   - PGAPPNAME（Npgsql 10.x 新特性）自动注入——PostgreSQL 日志中可追踪来源应用。
//   - AddPalPostgreSqlOutboxNotifier 注册一个独立的 IHostedService，
//     与默认的 OutboxProcessor（PeriodicTimer 轮询）并行运行。
//   - 非 PostgreSQL 环境：此扩展包不被引用，完全不影响行为。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PalDDD.Core.Logging;

using PalDDD.Transactions;
namespace PalDDD.Dapper.PostgreSql;

/// <summary>PostgreSQL 增强 DI 注册扩展</summary>
public static class PostgreSqlServiceCollectionExtensions
{
    // ── NpgsqlDataSource（真正的"绕过 ADO.NET"）──

    /// <summary>
    /// 注册 NpgsqlDataSource 作为 Singleton，自动设置 PGAPPNAME。
    /// </summary>
    /// <param name="applicationName">
    /// 应用名，显示在 PostgreSQL pg_stat_activity.application_name。
    /// 用于监控和审计——可在 pgAdmin / Grafana 中按应用名过滤连接。
    /// Npgsql 10.x 新特性：通过 NpgsqlDataSourceBuilder 原生注入。
    /// </param>
    public static IServiceCollection AddPalNpgsqlDataSource(
        this IServiceCollection services,
        string connectionString,
        string applicationName = "Pal.DDD")
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.ApplicationName = applicationName;
        var dataSource = builder.Build();
        services.AddSingleton(dataSource);
        // P2/P3 修复（十七轮）：补 DbDataSource 抽象注册（镜像 MySql 版 AddPalMySqlDataSource）——
        // NpgsqlDataSource 本身是 DbDataSource 子类，但只注册具体类型时
        // GetRequiredService<DbDataSource> 解析失败；WithStores 的连接工厂依赖此抽象注册。
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource);

        return services;
    }

    /// <summary>
    /// 注册 NpgsqlDataSource 作为 Singleton，自动设置 PGAPPNAME。
    /// 支持自定义 TypeHandler、编码等 PostgreSQL 特有配置。
    /// </summary>
    /// <param name="applicationName">PGAPPNAME 应用名（PostgreSQL 日志可见）</param>
    /// <param name="configure">配置回调（如添加 NodaTime 类型映射）</param>
    public static IServiceCollection AddPalNpgsqlDataSource(
        this IServiceCollection services,
        string connectionString,
        string applicationName,
        Action<NpgsqlDataSourceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.ApplicationName = applicationName;
        configure(builder);
        var dataSource = builder.Build();
        services.AddSingleton(dataSource);
        // P2/P3 修复（十七轮）：补 DbDataSource 抽象注册（同上重载，供 WithStores 连接工厂解析）
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource);

        return services;
    }

    /// <summary>
    /// 注册 NpgsqlDataSource + Dapper Store（一键注册：连接 + Outbox + Inbox + Saga）。<br/>
    /// P2/P3 修复（十七轮 · PD17 方言维度）：镜像 MySQL 版 <c>AddPalMySqlDataSourceWithStores</c>——
    /// 此前 PG 无等价入口，用户只能 <c>AddPalNpgsqlDataSource</c> + <c>AddPalDapperTransactions</c>
    /// 组合，而后者注册的 <c>DbConnection</c> 工厂是 <c>new NpgsqlConnection(cs)</c>
    /// （走连接串全局池），完全绕过 NpgsqlDataSource 私有池——PGAPPNAME 应用名、
    /// 自定义 TypeHandler、连接池调优对 Store 全部不生效。此处覆盖 <c>DbConnection</c> 注册
    /// （MS DI 后注册胜出），Store 统一从 DataSource 池取连接。
    /// </summary>
    public static IServiceCollection AddPalNpgsqlDataSourceWithStores(
        this IServiceCollection services,
        string connectionString,
        string applicationName = "Pal.DDD")
    {
        AddPalNpgsqlDataSource(services, connectionString, applicationName);
        DapperServiceCollectionExtensions.AddPalDapperTransactions(services, DapperDbType.PostgreSql, connectionString);

        // 覆盖 AddPalDapperTransactions 的 new NpgsqlConnection(cs) 默认工厂——
        // Store 连接改从注册的 NpgsqlDataSource 取（方言维度对齐 MySQL 版 84-85 行同款修复）
        services.AddScoped<System.Data.Common.DbConnection>(sp =>
            sp.GetRequiredService<System.Data.Common.DbDataSource>().CreateConnection());

        return services;
    }

    // ── LISTEN/NOTIFY 实时通知 ──

    /// <summary>
    /// 注册 PostgreSQL LISTEN/NOTIFY 实时通知服务。
    /// 收到 NOTIFY 后立即触发 <see cref="OutboxBatchProcessor"/> 处理，消除轮询延迟。
    /// </summary>
    /// <remarks>
    /// ⚡ 要求已在 DI 中注册 <c>NpgsqlDataSource</c>（通过 <c>AddPalNpgsqlDataSource</c>）。
    /// 此方法使用 DataSource 而非新连接字符串，确保复用连接池配置和类型映射器。
    /// </remarks>
    /// <param name="channelName">通知通道名（默认 "outbox_channel"）</param>
    public static IServiceCollection AddPalPostgreSqlOutboxNotifier(
        this IServiceCollection services,
        string channelName = "outbox_channel")
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IHostedService>(sp =>
            new PostgreSqlOutboxNotifier(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IPalLogger<PostgreSqlOutboxNotifier>>(),
                sp.GetService<TimeProvider>(),
                channelName));

        return services;
    }

    // ── 软删除 ──

    /// <summary>
    /// 📖 PostgreSQL 软删除已可用：直接调用 <see cref="PostgreSqlSoftDelete"/> 静态方法。<br/>
    /// 💡 Dapper 没有 EF Core 的 QueryFilter，软删除需要显式调用：<br/>
    /// <c>var sql = "SELECT * FROM orders WHERE " + PostgreSqlSoftDelete.ActiveFilter();</c><br/>
    /// 无需 DI 注册——纯 SQL 字符串操作，线程安全。
    /// </summary>
    public static IServiceCollection AddPalPostgreSqlSoftDelete(this IServiceCollection services)
        => services; // 静态工具类，直接调用 PostgreSqlSoftDelete.* 方法即可

    // ── 审计日志 ──

    /// <summary>
    /// 📖 PostgreSQL 审计日志已可用：直接调用 <see cref="PostgreSqlAuditor"/> 静态方法。<br/>
    /// 提供行级变更审计：INSERT/UPDATE/DELETE 自动记录到 audit_log 表。<br/>
    /// 无需 DI 注册——纯 SQL 字符串操作，线程安全。
    /// </summary>
    public static IServiceCollection AddPalPostgreSqlAuditor(this IServiceCollection services)
        => services; // 静态工具类，直接调用 PostgreSqlAuditor.* 方法即可

    // ── 读写分离 ──

    /// <summary>
    /// 注册 PostgreSQL 读写分离路由器。<br/>
    /// 写操作自动路由到主库，读操作负载均衡分发到只读副本。
    /// </summary>
    /// <remarks>
    /// P2/P3 修复（十七轮）：读写分离存在双入口（本方法与
    /// <see cref="PostgreSqlReadWriteRouterExtensions.AddPalReadWriteRouter"/>）——
    /// 后者为推荐入口：Writer/Reader 各自 ApplicationName 后缀（-Writer/-Reader，监控可区分）、
    /// Reader 走 LoadBalanceHosts 负载均衡、并做副本凭据一致性校验（PD17）；本方法三项均缺。
    /// </remarks>
    [System.Obsolete("读写分离请使用 PostgreSqlReadWriteRouterExtensions.AddPalReadWriteRouter（Writer/Reader 应用名后缀区分 + 负载均衡 + 副本凭据校验）。本入口保留仅为既有调用方兼容。")]
    public static IServiceCollection AddPalPostgreSqlReadWriteRouter(
        this IServiceCollection services,
        string writerConnectionString,
        string[] readerConnectionStrings)
    {
        var writer = new NpgsqlDataSourceBuilder(writerConnectionString).Build();
        NpgsqlDataSource? reader = null;
        if (readerConnectionStrings.Length > 0)
        {
            // PD17 姊妹统一：端口编码进 Host 条目（非 5432 副本端口不丢弃）
            var hosts = readerConnectionStrings.Select(cs =>
            {
                var sb = new NpgsqlConnectionStringBuilder(cs);
                return sb.Host is null || sb.Host.Length == 0
                    ? ""
                    : (sb.Port != 5432 ? $"{sb.Host}:{sb.Port}" : sb.Host);
            }).Where(h => h.Length > 0);
            var readerCs = new NpgsqlConnectionStringBuilder(writerConnectionString)
            {
                Host = string.Join(",", hosts),
                LoadBalanceHosts = true,
                TargetSessionAttributes = "any"
            }.ConnectionString;
            reader = new NpgsqlDataSourceBuilder(readerCs).Build();
        }
        services.AddSingleton(new PostgreSqlReadWriteRouter(writer, reader));
        return services;
    }
}
