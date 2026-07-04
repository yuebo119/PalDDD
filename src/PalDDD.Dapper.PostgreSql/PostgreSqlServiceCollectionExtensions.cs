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
//   services.AddPalPostgreSqlOutboxNotifier(connectionString);
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
        services.AddSingleton(builder.Build());

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
        services.AddSingleton(builder.Build());

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
    public static IServiceCollection AddPalPostgreSqlReadWriteRouter(
        this IServiceCollection services,
        string writerConnectionString,
        string[] readerConnectionStrings)
    {
        var writer = new NpgsqlDataSourceBuilder(writerConnectionString).Build();
        NpgsqlDataSource? reader = null;
        if (readerConnectionStrings.Length > 0)
        {
            var hosts = readerConnectionStrings.Select(cs =>
                new NpgsqlConnectionStringBuilder(cs).Host ?? "").Where(h => h.Length > 0);
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
