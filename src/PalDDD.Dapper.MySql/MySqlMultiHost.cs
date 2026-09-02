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

        // v35 P3：空白连接串 fail-fast（v34 LoadBalance/LeastConnections 两入口收口的姊妹
        // 漏网，同款口径）——空白串原样放行会延迟到 Build()/建连时才抛异常
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(standbyConnectionString);

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
        // 合并主机列表（凭据/端口/库已验证一致，取 primary 的即可）
        // v19 P2-② + v20 F2 机理勘正：standby 串缺 Server= 时 MySqlConnector 返回<b>空串</b>非
        // "localhost"（片 B 探针实证 2.6.2；PG 同构 ITM-262）——空条目并入列表使故障转移静默
        // 失败。fail-fast 对齐 PG 姊妹 EncodeHostEntry。
        // v28 P3（v27 N8 副作用修复）：本 fail-fast 前置于 Port 一致性校验——空 Server 会被
        // 缺 Server fail-fast 先拦（否则 Port 比较先抛误导性端口消息，真实问题是缺 Server）
        // v29 P3（S5）：异常类型 InvalidOperationException → ArgumentException——缺 Server
        // 是连接串配置参数错误（对齐 PG 侧 v28 裁决与 primary 侧 N7 同步修正），消息不变
        if (string.IsNullOrWhiteSpace(standbyBuilder.Server))
            throw new ArgumentException(
                "Standby connection string is missing 'Server='. Failover cannot silently include an empty host.");
        // v41 P2 勘正：v27 N8 曾称"MySqlConnector 的 Server 支持 host:port 内嵌语法（v26 H5
        // 已证）"——该声明失实：H5 所证仅为"内嵌端口不吸收进 Port 属性"（属性层为真），运行时
        // 建连层不支持（MySqlConnector 2.6.2 源码：ConnectionSettings 仅 Split(',') 且端口恒取
        // 共享 Port；主机名原样传 Dns.GetHostAddresses——"db2:3306" 是非法 DNS 主机名必炸；
        // 维护者 feature request #762 open 至今）。内嵌语法现一律 fail-fast；Port 一致性恢复
        // 无条件校验（原"内嵌感知跳过"建立在不存在的语法上）。
        // v42 勘正：内嵌端口检测收窄为"单冒号+数字后缀"（解析规则同 EnsureNoEmbeddedPort）
        // ——v41 的 Contains(':') 会误拦裸 IPv6（"::1"，MySQL 侧唯一可用 IPv6
        // 形态：IP 字面量可直连 + 共享 Port）
        // v43 P2 修复：删除外层门——v42 把检测开关接在"存在任一无内嵌条目"布尔判定
        //（语义为"存在任一无内嵌条目"，该判定 v43 P2 收口时随死代码一并删除）上，全内嵌
        // 端口列表恰好返回 false 使 fail-fast 整块跳过（v41 P2-2 的核心场景静默复活；
        // 独立探针实证 db2:3306 → gate=False）。内层循环的"单冒号+数字后缀"判定本身已
        // 正确收窄（裸 IPv6 放行、host:port 拦截），直接执行
        // v43 P2 收口：检测提取共享 helper EnsureNoEmbeddedPort——原 standby/primary 两处
        // 内联循环拷贝 + LoadBalance/LeastConnections 两活跃入口零检测，四入口一处收口
        EnsureNoEmbeddedPort(standbyBuilder.Server, "standby Server");
        // v43 P3 顺序校正：primary 侧检测同样前置于 Port 一致性校验——内嵌端口的明确错误
        // 不应被 Port 不一致的误导消息先拦（v43 P2 内联循环误置于 Port 校验之后）
        // v43 P2 历史：primary 侧检测为 v43 P2 新补——v41/v42 检测只作用于 standby，primary
        // "Server=db1:3306" 经归一化查重后原始串原样进连接串（节点永不可连，FailOver 静默
        // 改连备库、写流量落备库拓扑倒挂）
        EnsureNoEmbeddedPort(primaryBuilder.Server, "primary Server");
        if (standbyBuilder.Port != primaryBuilder.Port)
        {
            throw new ArgumentException(
                "standby 与 primary 的 Port 必须一致：MySQL 连接串的共享 Port 对主机列表条目统一生效，"
                + "差异无法表达且会被静默丢弃（合并后 standby 条目被静默改用 primary 端口，故障转移后必然连接失败）。"
                + "请统一端口（MySQL 多主机不支持 per-host 端口声明）。");
        }

        // v21 B-2（v27 P3 B 片 N7 勘正行为）：primary 缺 Server 时 Server 属性为空串
        //（v20 F2 自证）——原"空则直接赋 standby"静默把一主一备注册退化为 standby 单机，
        // 配置错误被吞；改 fail-fast（镜像 v26 H4 PostgreSqlMultiHost，见下方合并处）。
        // v26 P3 H5：拼接前 Server 查重 fail-fast（镜像 PG Failover 入口 v25 C9 查重）——
        // primary/standby 同指一机时拼接产生重复 Server 条目（如 "mysql1,mysql1"），
        // FailOver 把同一实例视作两个节点轮试，故障转移语义错乱。归一化经
        // NormalizeServerEntry：v41 勘正——MySqlConnector 实不支持 "server:port" 内嵌语法，
        // 属性返回原始串（内嵌端口不吸收进 Port 属性），须拆出 (裸名, port) 再比较（doc
        // 口径勘正见 NormalizeServerEntry）；归一化仅用于查重比较口径统一，内嵌形态已被
        // 入口 EnsureNoEmbeddedPort fail-fast 拦截（primary 侧检测 v43 P2 补齐并前置）。
        // primary 列表空条目（primary 缺 Server 的 Split 产物）跳过——该输入随后由下方
        // N7 fail-fast 拦截，此处跳过仅为不误抛"重复"异常。
        // v29 P3（S4，镜像 PG 侧 v28/v29 形态）：standby 侧改复数版 NormalizeServerEntries
        // 展开 + seenServers HashSet 查重——原单值版把 standby 多主机列表整串归一化
        //（"sb1,sb2" 当一个主机名），与 primary 任意单条目恒不等，跨串重复漏检（primary
        // "Server=db1,sb2" + standby "Server=sb1,sb2" 时 sb2 重复漏检）；standby 列表内部
        // 重复（"Server=sb1,sb1"）互不比较同样漏检。primary 条目 + standby 展开条目全部进
        // 集合，Add 失败即抛（一并覆盖两类重复）；大小写归一经 ToUpperInvariant
        //（等价 v26 H5 的 OrdinalIgnoreCase 比较，与 PG 侧 ReadWriteSplit 同款）。
        var seenServers = new HashSet<(string Server, int Port)>();
        foreach (var raw in primaryBuilder.Server.Split(','))
        {
            var (primaryServer, primaryPort) = NormalizeServerEntry(raw, (int)primaryBuilder.Port);
            if (primaryServer.Length == 0) continue;
            seenServers.Add((primaryServer.ToUpperInvariant(), primaryPort));
        }
        foreach (var (standbyServer, standbyPort) in NormalizeServerEntries(standbyBuilder.Server, (int)standbyBuilder.Port))
        {
            if (standbyServer.Length == 0) continue;
            if (!seenServers.Add((standbyServer.ToUpperInvariant(), standbyPort)))
                throw new ArgumentException(
                    $"standby Server '{standbyServer}:{standbyPort}' 与 primary 主机列表或 standby 列表内其他条目重复："
                    + "多主机拼接将产生重复 Server 条目（如 \"mysql1,mysql1\"），FailOver 把同一实例视作两个节点轮试，"
                    + "故障转移语义错乱。请为 standby 指定不同主机，或使用自定义多主机扩展。");
        }
        // v27 P3（B 片 N7）：primary 缺 Server fail-fast
        // v29 P3（S5）：异常类型 InvalidOperationException → ArgumentException——缺 Server
        // 是连接串配置参数错误（对齐 PG 侧 v28 裁决"配置参数错误语义"，与上方 standby 侧
        // 同步修正；原 v27 N7 注释所称"对齐 v20 F2/v21 B-1 的 InvalidOperationException
        // 先例"随该裁决一并废止），消息不变
        if (string.IsNullOrWhiteSpace(primaryBuilder.Server))
            throw new ArgumentException(
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

        // v34 P3：空白连接串 fail-fast（v33 姊妹收口，镜像 MySqlServiceCollectionExtensions
        // AddPalMySqlDataSource 同款口径）——空白串原样放行会延迟到 Build()/建连时才抛异常
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            LoadBalance = MySqlLoadBalance.RoundRobin
        };
        // v43 P2 收口：内嵌端口检测接线——原入口零检测，"Server=db1:3306" 原样进连接串
        //（节点永不可连，轮询/均衡静默失败延迟到建连且无诊断）；对齐 Failover 入口在
        // 共享 Port 语义生效前拦截
        EnsureNoEmbeddedPort(builder.Server, "Server");
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

        // v34 P3：空白连接串 fail-fast（v33 姊妹收口，同 LoadBalance 入口）
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            LoadBalance = MySqlLoadBalance.LeastConnections
        };
        // v43 P2 收口：内嵌端口检测接线——原入口零检测，同 LoadBalance 入口口径
        EnsureNoEmbeddedPort(builder.Server, "Server");
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
    /// 解析 "server:port" 形式为 (裸名, 端口)。v43 P3 勘正 doc 自相矛盾：MySqlConnector
    /// 2.6.2 运行时建连<b>不支持</b> Server 内嵌端口（"host:port" 条目主机名原样传 DNS
    /// 解析，永不可连；入口经 <see cref="EnsureNoEmbeddedPort"/> fail-fast 拦截）——本归一化
    /// 的拆分仅用于查重比较口径统一（属性层 Host 返回原始串、不吸收内嵌端口，与裸名+共享
    /// Port 直接比较恒不等）。
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
    /// v29 P3（S4）：多主机列表归一化（复数版）——按顶层逗号拆分后逐条调
    /// <see cref="NormalizeServerEntry"/>（镜像 PG 侧 NormalizeHostEntries v28 形态）。
    /// 单值版把 "sb1,sb2" 整串当一个主机名归一化，与任何单条目恒不等——多主机 standby
    /// 条目参与查重（Failover standby 侧的 seenServers 收集）必须经本复数版展开，否则
    /// 跨串/内部重复条目漏检、拼接产生双份主机。
    /// </summary>
    /// <param name="rawServer">原始 Server（可为逗号分隔多主机列表，逐项 Trim；空项原样保留由调用方跳过）。</param>
    /// <param name="fallbackPort">未内嵌端口条目的回退端口（共享 Port）。</param>
    internal static IReadOnlyList<(string Server, int Port)> NormalizeServerEntries(string? rawServer, int fallbackPort)
    {
        List<(string Server, int Port)> entries = [];
        foreach (var raw in (rawServer ?? "").Split(','))
        {
            entries.Add(NormalizeServerEntry(raw, fallbackPort));
        }
        return entries;
    }

    /// <summary>
    /// v43 P2（收口）：Server 内嵌端口 fail-fast 共享 helper——遍历逗号分隔条目，对
    /// "单冒号+数字后缀"（host:port 内嵌语法）条目抛 <see cref="ArgumentException"/>，
    /// 消息含命中的条目与 MySqlConnector 2.6.2 不支持说明。
    /// <para>
    /// v41 P2 勘正口径：MySqlConnector 2.6.2 运行时建连不支持内嵌端口——主机名原样传
    /// DNS 解析，含冒号条目是非法主机名，该节点永不可连。判定收窄为"唯一冒号且后缀可
    /// 解析为整数"（v42 勘正）：裸 IPv6（"::1"，多冒号）不命中、放行——MySQL 侧唯一可用
    /// IPv6 形态（IP 字面量可直连 + 共享 Port）；方括号条目（"[::1]"）亦放行。
    /// <see cref="NormalizeServerEntry"/> 的同规则解析仅服务查重口径，本方法才是入口门禁。
    /// </para>
    /// <para>
    /// 四入口统一接线（v43 P2 收口）：Failover standby/primary（原两处内联循环拷贝）+
    /// LoadBalance / LeastConnections（原零检测——"Server=db1:3306" 原样放行，节点永不可
    /// 连且无诊断）。检测前置于共享 Port 语义相关校验，明确错误优先于误导消息。
    /// </para>
    /// </summary>
    /// <param name="serverList">Server 属性原始值（可为多主机逗号分隔列表，内部逐项 Trim）。</param>
    /// <param name="parameterName">来源参数名（拼入异常消息，如 "standby Server" / "primary Server" / "Server"）。</param>
    internal static void EnsureNoEmbeddedPort(string? serverList, string parameterName)
    {
        foreach (var raw in (serverList ?? "").Split(','))
        {
            var entry = raw.Trim();
            var colon = entry.LastIndexOf(':');
            if (colon >= 0 && entry.IndexOf(':') == colon  // v46：口径对齐 NormalizeServerEntry（colon>=0）——":port"（空主机名）同为非法形态 fail-fast
                && int.TryParse(entry.AsSpan(colon + 1), out _))
            {
                throw new ArgumentException(
                    $"{parameterName} 条目 '{entry}' 含内嵌端口语法（\"host:port\"）——MySqlConnector 2.6.2 不支持该语法"
                    + "（主机名原样传 DNS 解析，含冒号条目是非法主机名，该节点永不可连）。"
                    + "请移除内嵌端口、统一使用共享 Port 关键字。");
            }
        }
    }
}
