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

        // P2 定案（failover 参数丢弃）：MySQL 连接串的 User/Password/Database
        // 对主机列表内所有节点统一生效——standby 与 primary 不一致时无法表达，
        // 静默丢弃会导致故障转移后连接失败。此处快速失败并说明约束。
        if (!string.Equals(standbyBuilder.UserID, primaryBuilder.UserID, StringComparison.Ordinal)
            || !string.Equals(standbyBuilder.Password, primaryBuilder.Password, StringComparison.Ordinal)
            || !string.Equals(standbyBuilder.Database, primaryBuilder.Database, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "standby 与 primary 的 User/Password/Database 必须一致：MySQL 连接串的这些参数对主机列表内全部节点统一生效，"
                + "差异无法表达且会被静默丢弃（故障转移后必然连接失败）。请为两节点配置相同账号/库，或使用自定义多主机扩展。");
        }
        // v27 P3（B 片 N8）：Port 一致性校验改内嵌端口感知——MySqlConnector 的 Server 支持
        // "host:port" 内嵌语法（内嵌端口不吸收进 Port 属性，v26 H5 已证），合并后未内嵌端口
        // 的条目统一用 primary 的共享 Port。归一化判定（解析规则与 NormalizeServerEntry 同款，
        // 经 HasHostWithoutEmbeddedPort）：standby 全部条目均内嵌端口 → 其 Port 声明不参与实际
        // 连接，跳过比较（原属性直比会误拒实际无冲突的配置，如 primary "Server=db1;Port=3306"
        // + standby "Server=db2:3306;Port=3307"——实际端口一致仅属性不同）；standby 存在未
        // 内嵌条目（依赖共享 Port）→ 保持原有一致性要求，差异快速失败。
        if (HasHostWithoutEmbeddedPort(standbyBuilder.Server) && standbyBuilder.Port != primaryBuilder.Port)
        {
            throw new ArgumentException(
                "standby 与 primary 的 Port 必须一致：MySQL 连接串的共享 Port 对未内嵌端口的主机列表条目统一生效，"
                + "差异无法表达且会被静默丢弃（合并后 standby 的未内嵌条目被静默改用 primary 端口，故障转移后必然连接失败）。"
                + "请统一端口、改用 Server 内嵌 \"host:port\" 语法逐条目声明，或使用自定义多主机扩展。");
        }

        // 合并主机列表（凭据/端口/库已验证一致，取 primary 的即可）
        // v19 P2-② + v20 F2 机理勘正：standby 串缺 Server= 时 MySqlConnector 返回<b>空串</b>非
        // "localhost"（片 B 探针实证 2.6.2；PG 同构 ITM-262）——空条目并入列表使故障转移静默
        // 失败。fail-fast 对齐 PG 姊妹 EncodeHostEntry。
        if (string.IsNullOrWhiteSpace(standbyBuilder.Server))
            throw new InvalidOperationException(
                "Standby connection string is missing 'Server='. Failover cannot silently include an empty host.");
        // v21 B-2（v27 P3 B 片 N7 勘正行为）：primary 缺 Server 时 Server 属性为空串
        //（v20 F2 自证）——原"空则直接赋 standby"静默把一主一备注册退化为 standby 单机，
        // 配置错误被吞；改 fail-fast（镜像 v26 H4 PostgreSqlMultiHost，见下方合并处）。
        // v26 P3 H5：拼接前 Server 查重 fail-fast（镜像 PG Failover 入口 v25 C9 查重）——
        // primary/standby 同指一机时拼接产生重复 Server 条目（如 "mysql1,mysql1"），
        // FailOver 把同一实例视作两个节点轮试，故障转移语义错乱。归一化经
        // NormalizeServerEntry：MySqlConnector 的 Server 支持 "server:port" 内嵌语法，
        // 属性返回原始串（内嵌端口不吸收进 Port 属性），须拆出 (裸名, port) 再比较；未
        // 内嵌端口时回退共享 Port（MySqlConnector 语义：Port 只对未内嵌端口的主机生效）。
        // primary 列表空条目（primary 缺 Server 的 Split 产物）跳过——该输入随后由下方
        // N7 fail-fast 拦截，此处跳过仅为不误抛"重复"异常。
        var (standbyServer, standbyPort) = NormalizeServerEntry(standbyBuilder.Server, (int)standbyBuilder.Port);
        foreach (var raw in primaryBuilder.Server.Split(','))
        {
            var (primaryServer, primaryPort) = NormalizeServerEntry(raw, (int)primaryBuilder.Port);
            if (primaryServer.Length == 0) continue;
            if (string.Equals(primaryServer, standbyServer, StringComparison.OrdinalIgnoreCase) && primaryPort == standbyPort)
                throw new ArgumentException(
                    $"standby Server '{standbyServer}:{standbyPort}' 与 primary 主机列表中的条目重复："
                    + "多主机拼接将产生重复 Server 条目（如 \"mysql1,mysql1\"），FailOver 把同一实例视作两个节点轮试，"
                    + "故障转移语义错乱。请为 standby 指定不同主机，或使用自定义多主机扩展。");
        }
        // v27 P3（B 片 N7）：primary 缺 Server fail-fast——异常类型与消息风格对齐同文件
        // standby 缺 Server 先例（v20 F2/v21 B-1 的 InvalidOperationException）
        if (string.IsNullOrWhiteSpace(primaryBuilder.Server))
            throw new InvalidOperationException(
                "Primary connection string is missing 'Server='. Failover cannot silently substitute the standby as the only host.");
        primaryBuilder.Server = $"{primaryBuilder.Server},{standbyBuilder.Server}";

        // 故障转移模式：默认先连第一个，失败再试后续
        primaryBuilder.LoadBalance = MySqlLoadBalance.FailOver;

        var dataSource = new MySqlDataSourceBuilder(primaryBuilder.ConnectionString).Build();

        // ITM-113 修复（声明）：同一 dataSource 实例双重注册（MySqlDataSource + DbDataSource）
        // ——容器关闭时 Dispose 两次。MySqlDataSource.Dispose 幂等（对齐 PG 版
        // PostgreSqlReadWriteRouter 探针声明：NpgsqlDataSource 二次/三次释放不抛），
        // 重复释放安全；此模式允许 Store 直接注入 DbDataSource 基类型。
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
            LoadBalance = MySqlLoadBalance.RoundRobin
        };
        // v27 P3（B 片 N9）：Pooling 条件化（对照 W2 MaxAutoPrepare 条件化模式——仅未显式
        // 设置时赋默认）——原无条件 Pooling = true 覆盖用户显式的 "Pooling=false"（调试/
        // 排障禁用连接池被静默重启）。MySqlConnector 默认 Pooling=true，未显式设置时本就
        // 生效；bool 默认值与显式 true 不可区分（无法走 == 默认值 判定式），经 TryGetValue
        // 判定串内是否显式出现 Pooling 关键字——仅补默认、不覆盖显式值。
        if (!builder.TryGetValue("Pooling", out _))
            builder.Pooling = true;

        var dataSource = new MySqlDataSourceBuilder(builder.ConnectionString).Build();

        // ITM-113 修复（声明）：同 AddPalMySqlDataSourceWithFailover——双重注册
        // （MySqlDataSource + DbDataSource）经容器 Dispose 两次，MySqlDataSource.Dispose
        // 幂等，重复释放安全（对齐 PG 版 PostgreSqlReadWriteRouter 探针声明）。
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
            LoadBalance = MySqlLoadBalance.LeastConnections
        };
        // v27 P3（B 片 N9）：同 LoadBalance 入口——Pooling 条件化，不覆盖显式 "Pooling=false"
        if (!builder.TryGetValue("Pooling", out _))
            builder.Pooling = true;

        var dataSource = new MySqlDataSourceBuilder(builder.ConnectionString).Build();

        // ITM-113 修复（声明）：同 AddPalMySqlDataSourceWithFailover——双重注册
        // （MySqlDataSource + DbDataSource）经容器 Dispose 两次，MySqlDataSource.Dispose
        // 幂等，重复释放安全（对齐 PG 版 PostgreSqlReadWriteRouter 探针声明）。
        services.AddSingleton(dataSource);
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource);

        return services;
    }

    /// <summary>
    /// v26 P3（H5）：Server 内嵌端口归一化（MySqlConnector 语义）——解析 "server:port"
    /// 形式为 (裸名, 端口)。MySqlConnector 连接串的 Server 支持 "host:port" 内嵌语法，
    /// 属性返回原始串（内嵌端口不吸收进 Port 属性），与裸名+共享 Port 直接比较恒不等。
    /// 未内嵌端口时返回 (原串, <paramref name="fallbackPort"/>)。
    /// <para>
    /// 仅当冒号为<b>唯一</b>冒号且后缀可解析为整数才拆分：裸 IPv6 字面量内部含冒号，
    /// 误拆会产生畸形对；方括号 IPv6 不拆分，整体作主机名。独立实现（不共享 PG 侧
    /// NormalizeHostEntry）——两驱动的内嵌语法语义各自演化，且 Port 属性类型不同（uint vs int）。
    /// </para>
    /// </summary>
    /// <param name="rawServer">原始 Server 条目（可为多主机列表中的单项，内部 Trim）。</param>
    /// <param name="fallbackPort">未内嵌端口时的回退端口（共享 Port，uint 收窄为 int——端口值域 0-65535 无截断）。</param>
    internal static (string Server, int Port) NormalizeServerEntry(string? rawServer, int fallbackPort)
    {
        var entry = rawServer?.Trim() ?? "";
        var colon = entry.LastIndexOf(':');
        if (colon >= 0 && entry.IndexOf(':') == colon
            && int.TryParse(entry.AsSpan(colon + 1), out var embedded))
        {
            return (entry[..colon], embedded);
        }
        return (entry, fallbackPort);
    }

    /// <summary>
    /// v27 P3（B 片 N8）：判定 Server 多主机列表是否存在<b>未内嵌端口</b>的条目（解析规则与
    /// <see cref="NormalizeServerEntry"/> 同款：唯一冒号且后缀可解析为整数才算内嵌）。
    /// 未内嵌条目依赖连接串共享 Port——合并后统一取 primary 的 Port，两端共享 Port 不一致时
    /// 此类条目被静默改端口。空条目同样返回 true（空条目不携带端口，由缺 Server fail-fast 拦截）。
    /// </summary>
    /// <param name="serverList">Server 属性原始值（可为多主机逗号分隔列表，内部逐项 Trim）。</param>
    internal static bool HasHostWithoutEmbeddedPort(string? serverList)
    {
        foreach (var raw in (serverList ?? "").Split(','))
        {
            var entry = raw.Trim();
            var colon = entry.LastIndexOf(':');
            if (colon < 0 || entry.IndexOf(':') != colon
                || !int.TryParse(entry.AsSpan(colon + 1), out _))
            {
                return true;
            }
        }
        return false;
    }
}
