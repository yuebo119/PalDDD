// ─────────────────────────────────────────────────────────────
// 🔀 PostgreSqlReadWriteRouter — 读写分离路由（应用层显式路由）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ 纯连接管理 — 零反射。
//
// 模式说明：
//   方案 A（推荐）：MultiHost 连接串 — 已在 PostgreSqlMultiHost 实现
//     "Host=primary,replica1,replica2;TargetSessionAttributes=..."
//     Npgsql 驱动层自动路由，应用程序零感知。
//
//   方案 B（显式路由）：本文件实现 — 手动指定读/写数据源
//     适用于需要更精细控制的场景：
//     - 写后立即读（read-your-writes）
//     - 特定查询强制走主库
//     - 跨数据中心延迟敏感查询
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 纯基础设施层路由决策，领域层无感知。
//   - 通过 IServiceProvider 注入，Dapper Store 取对应数据源。
//
// 使用方式：
//   services.AddPalNpgsqlDataSourceWithReadWriteSplit(
//       primary, [replica1, replica2]);
//
//   // 显式路由
//   var readConn  = router.GetReader().CreateConnection();
//   var writeConn = router.GetWriter().CreateConnection();
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace PalDDD.Dapper.PostgreSql;

/// <summary>读写分离路由器 — 封装主库和读库数据源</summary>
public sealed class PostgreSqlReadWriteRouter : IAsyncDisposable
{
    /// <summary>主库数据源（写操作）</summary>
    public NpgsqlDataSource Writer { get; }

    /// <summary>读库数据源（读操作，负载均衡）</summary>
    public NpgsqlDataSource? Reader { get; }

    public PostgreSqlReadWriteRouter(NpgsqlDataSource writer, NpgsqlDataSource? reader = null)
    {
        Writer = writer ?? throw new ArgumentNullException(nameof(writer));
        Reader = reader;
    }

    /// <summary>获取读连接。无读库时返回主库连接。</summary>
    public NpgsqlConnection GetReader()
        => (Reader ?? Writer).CreateConnection();

    /// <summary>获取写连接</summary>
    public NpgsqlConnection GetWriter()
        => Writer.CreateConnection();

    /// <summary>
    /// 释放主库和读库数据源持有的连接池。
    /// DI 注册为 Singleton 时容器自动调用此方法；NpgsqlDataSource 未释放会导致连接泄漏。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await Writer.DisposeAsync().ConfigureAwait(false);
        if (Reader is not null)
            await Reader.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>读写分离 DI 注册扩展</summary>
public static class PostgreSqlReadWriteRouterExtensions
{
    /// <summary>
    /// 注册显式读写分离路由器。
    /// 同时注册 Router 和 Writer NpgsqlDataSource，方便 Store 直接注入。
    /// </summary>
    /// <param name="primaryConnectionString">主库连接串</param>
    /// <param name="replicaConnectionStrings">只读副本连接串（可选）</param>
    /// <param name="applicationName">PGAPPNAME</param>
    public static IServiceCollection AddPalReadWriteRouter(
        this IServiceCollection services,
        string primaryConnectionString,
        string[]? replicaConnectionStrings = null,
        string applicationName = "Pal.DDD")
    {
        ArgumentNullException.ThrowIfNull(services);

        // 主库（写）
        var writerBuilder = new NpgsqlDataSourceBuilder(primaryConnectionString);
        writerBuilder.ConnectionStringBuilder.ApplicationName = applicationName + "-Writer";
        var writer = writerBuilder.Build();

        // 读库
        NpgsqlDataSource? reader = null;
        if (replicaConnectionStrings is { Length: > 0 })
        {
            List<string> hosts = [];
            foreach (var cs in replicaConnectionStrings)
            {
                var sb = new NpgsqlConnectionStringBuilder(cs);
                if (sb.Host is not null) hosts.Add(sb.Host);
            }

            if (hosts.Count > 0)
            {
                var readerCs = primaryConnectionString;
                var psb = new NpgsqlConnectionStringBuilder(readerCs);
                psb.Host += "," + string.Join(",", hosts);
                psb.LoadBalanceHosts = true;
                psb.TargetSessionAttributes = "any";
                psb.ApplicationName = applicationName + "-Reader";

                var readerBuilder = new NpgsqlDataSourceBuilder(psb.ConnectionString);
                reader = readerBuilder.Build();
            }
        }

        var router = new PostgreSqlReadWriteRouter(writer, reader);
        services.AddSingleton(router);
        services.AddSingleton(writer); // 主库可直接注入

        return services;
    }
}
