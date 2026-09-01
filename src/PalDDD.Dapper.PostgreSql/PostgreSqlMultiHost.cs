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

        // v35 P3：空白连接串 fail-fast（v34 五处姊妹收口的延续，同款口径）——空白串原样
        // 放行会延迟到 NpgsqlDataSourceBuilder.Build()/建连时才抛异常
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(standbyConnectionString);

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
        // v21 B-1：原 IsNullOrWhiteSpace==false 时静默跳过 standby——注册成 primary-only
        // 数据源无备机无告警（MySQL v20 F2 fail-fast / ReadWriteRouter ITM-112 均定性为 bug，
        // PG MultiHost 唯二保留跳过语义的入口）。对齐姊妹改 fail-fast。
        // v28 P3：异常类型改 ArgumentException——缺 Host 是连接串配置参数错误（与 primary 侧
        // 缺 Host 的 v26 H4 / ReadWriteRouter ITM-112 的 ArgumentException 语义统一），
        // 原InvalidOperationException 属状态错误语义，不适用。
        if (string.IsNullOrWhiteSpace(standbyBuilder.Host))
            throw new ArgumentException(
                "Standby connection string is missing 'Host='. Failover registration cannot silently degrade to primary-only.");
        {
            // ITM-132 修复：primary Port≠5432 时，未编码的备机 Host 会继承连接串共享 Port
            // （Npgsql 的 Port 只对未内嵌端口的主机生效），导致备机被连到主库端口——
            // 统一经 EncodeHostEntry 编码：primary Port≠5432 时全部 Host 显式 host:port（含显式 5432）。
            var standbyHost = EncodeHostEntry(standbyBuilder, primaryBuilder.Port);
            // ITM-110 修复：拼接规范化——主串无 Host 时原 `Host += ",{standbyHost}"` 产生
            // 前导逗号（",pg2"），Npgsql 解析出空主机条目。
            // v26 P3 H4：primary 缺 Host（Npgsql 返回空串——ITM-262 自证）时原实现静默以
            // standby 充当唯一主机，一主一备注册退化为 standby 单机，配置错误被吞——对齐
            // v19-v21 standby/replica 缺 Host 的 fail-fast 先例改抛异常，原 ITM-110
            // "空则直接赋值"分支随之不可达，删除。
            var primaryHost = builder.ConnectionStringBuilder.Host;
            if (string.IsNullOrWhiteSpace(primaryHost))
                throw new ArgumentException(
                    "Primary connection string is missing 'Host='. Failover cannot silently substitute the standby as the only host.");
            // v25 P3 勘正族 C9：primary/standby Host 重复条目 fail-fast——对照 ReadWriteSplit
            // 零副本分支的 ITM-110 "pg1,pg1" 处置（该分支通过不合并避免重复条目）；failover 入口
            // 两个参数独立传入，primary/standby 同指一机时拼接仍会产生 "pg1,pg1"——驱动视为主备
            // 两份，故障转移/负载语义错乱。比较用归一化 (host, port) 对：primary 条目未内嵌端口时
            // 按共享 Port（primaryBuilder.Port，同 EncodeHostEntry 的 Npgsql 语义）；主机名
            // 大小写不敏感（DNS 大小写不敏感；同文件 ThrowIfCredentialsMismatch 的 Ordinal
            // 适用于凭据精确匹配，主机名语义不同）。
            // v26 P3 H2：两侧比较统一经 NormalizeHostEntry 归一化——Host=pg1:5433 内嵌端口
            // 语法下 standbyBuilder.Host 返回原始串（含端口，Port 属性不吸收内嵌值），直接与
            // primary 侧拆出的裸名+端口比较恒不等（v25 查重失效的根因）。
            // v28 P3：standby 侧改用复数版 NormalizeHostEntries 展开——standby 串自身可为
            // 多主机列表（Host="sb1,sb2"），单值版把整串当一个主机名归一化，与 primary 任意
            // 单条目恒不等，重复检测失效（如 primary "Host=pg1,sb2" + standby "Host=sb1,sb2"
            // 时 sb2 重复漏检，拼接产生双份 sb2 条目）。
            // v29 P3：查重改 seenHosts HashSet 形态（镜像 ReadWriteSplit v26 H1 的 Add 查重）——
            // v28 双层循环只查 primary×standby 交叉重复，standby 多主机列表内部重复
            //（"Host=sb1,sb1"）互不比较漏检，拼接仍产生双份 sb1 条目。primary 条目 + standby
            // 展开条目全部进集合，Add 失败即抛（一并覆盖跨串与 standby 内部两类重复）；
            // 大小写归一经 ToUpperInvariant（与 ReadWriteSplit 同款，等价 C9 的
            // OrdinalIgnoreCase 比较——CA1308 规约的大小写归一方向）。
            var standbyEntries = NormalizeHostEntries(standbyBuilder.Host, standbyBuilder.Port);
            var seenHosts = new HashSet<(string Host, int Port)>();
            foreach (var raw in primaryHost.Split(','))
            {
                var (host, port) = NormalizeHostEntry(raw, primaryBuilder.Port);
                if (host.Length == 0) continue;
                seenHosts.Add((host.ToUpperInvariant(), port));
            }
            foreach (var (standbyNormHost, standbyNormPort) in standbyEntries)
            {
                if (standbyNormHost.Length == 0) continue;
                if (!seenHosts.Add((standbyNormHost.ToUpperInvariant(), standbyNormPort)))
                    throw new ArgumentException(
                        $"standby Host '{standbyNormHost}:{standbyNormPort}' 与 primary 主机列表或 standby 列表内其他条目重复："
                        + "多主机拼接将产生重复 Host 条目（如 \"pg1,pg1\"），驱动视为主备两份，故障转移语义错乱。"
                        + "请为 standby 指定不同主机，或使用 AddPalNpgsqlDataSourceMultiHost 自定义完整连接串。");
            }
            builder.ConnectionStringBuilder.Host = $"{primaryHost},{standbyHost}";
        }

        builder.ConnectionStringBuilder.TargetSessionAttributes = "primary";
        builder.ConnectionStringBuilder.ApplicationName = applicationName;
        // 优化（二十五轮 API 扫描 B-1）：MaxAutoPrepare=20——固定模板 SQL（SqlTemplates 全系）
        // 同一文本执行 ≥5 次后 Npgsql 自动 PREPARE，省去每次执行的 parse/plan 开销（连接级 LRU）。
        // ⚠️ 与 PostgreSqlJsonbExtensions 的动态拼接 SQL 互斥：动态 SQL 文本随参数变化，
        // 反复进出 LRU 会把固定模板逐出（预备失效反而变慢）——启用 Jsonb 动态查询的场景
        // 请改用自行构建 NpgsqlDataSourceBuilder 注册（文档已声明互斥）。
        // 条件化（二十六轮 W2）：仅未设置（读 0）时赋默认——显式非零调优不被覆盖；
        // 显式禁用（写 0）与未设置不可区分，禁用走 configure 回调或自建 DataSource
        // （ITM-194 三十轮：删除旧"赋值覆盖"残留注释，与 W2 条件化行为矛盾）
        if (builder.ConnectionStringBuilder.MaxAutoPrepare == 0)
            builder.ConnectionStringBuilder.MaxAutoPrepare = 20;

        var dataSource0 = builder.Build();
        // v16 P2-2：补 DbDataSource 抽象双注册（对齐 MySQL MultiHost ITM-113 模式与基础入口）——
        // 缺失时 WithStores 连接工厂解析 DbDataSource 抛 InvalidOperationException
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource0);
// v19 P2-①：补具体型注册（Notifier 工厂 GetRequiredService<NpgsqlDataSource>() 依赖）
        services.AddSingleton<NpgsqlDataSource>(dataSource0);
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

        // v35 P3：空白连接串 fail-fast（v34 五处姊妹收口的延续，同款口径）——空白串原样
        // 放行会延迟到 NpgsqlDataSourceBuilder.Build()/建连时才抛异常
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryConnectionString);

        if (replicaConnectionStrings.Length == 0)
        {
            // ITM-110 修复：零副本时直接注册主库单数据源——原实现回退 failover(primary, primary)
            // 产生重复 Host（"pg1,pg1"），驱动层视为主备两份（故障转移/负载语义错乱）
            var soloBuilder = new NpgsqlDataSourceBuilder(primaryConnectionString);
            soloBuilder.ConnectionStringBuilder.ApplicationName = applicationName;
            soloBuilder.ConnectionStringBuilder.TargetSessionAttributes = "primary";
            // 优化（二十五轮 API 扫描 B-1）：MaxAutoPrepare=20（同 AddPalNpgsqlDataSourceWithFailover 注释）
            // 条件化（二十六轮 W2）：仅未设置（读 0）时赋默认——显式非零调优不被覆盖；
            // 显式禁用（写 0）与未设置不可区分，禁用走 configure 回调或自建 DataSource
            if (soloBuilder.ConnectionStringBuilder.MaxAutoPrepare == 0)
                soloBuilder.ConnectionStringBuilder.MaxAutoPrepare = 20;
            var dataSource1 = soloBuilder.Build();
            // v16 P2-2：补 DbDataSource 抽象双注册（对齐 MySQL MultiHost ITM-113 模式与基础入口）——
            // 缺失时 WithStores 连接工厂解析 DbDataSource 抛 InvalidOperationException
            services.AddSingleton<System.Data.Common.DbDataSource>(dataSource1);
            // v19 P2-①：补具体型注册
            services.AddSingleton<NpgsqlDataSource>(dataSource1);
            return services;
        }

        var builder = new NpgsqlDataSourceBuilder(primaryConnectionString);

        // 合并所有主机
        List<string> hosts = [];
        var primaryCsBuilder = new NpgsqlConnectionStringBuilder(primaryConnectionString);
        // v26 P3 H4：primary 缺 Host（Npgsql 返回空串——ITM-262 自证）时原实现静默以副本列表
        // 充当主机列表，主库亲和（ITM-067）注册退化为纯副本，配置错误被吞——对齐 Failover
        // 入口 H4 fail-fast（镜像 v19-v21 standby/replica 缺 Host 先例）。
        var primaryHost = builder.ConnectionStringBuilder.Host;
        if (string.IsNullOrWhiteSpace(primaryHost))
            throw new ArgumentException(
                "Primary connection string is missing 'Host='. ReadWriteSplit cannot silently substitute replicas as the host list.");
        // v26 P3 H1：归一化主机集合查重——副本与 primary 同指一机（或副本互相重复）时拼接
        // 产生重复 Host 条目，LoadBalanceHosts=true 轮询把同一实例计入多份权重（镜像 Failover
        // 入口 v25 C9 查重）。归一化经 NormalizeHostEntry（H2）：内嵌 "pg1:5433" 语法与
        // 裸名+共享 Port 统一为 (裸名, port) 对；主机名 ToUpperInvariant 后以 Ordinal tuple
        // 相等判定（DNS 大小写不敏感，等价 v25 C9 的 OrdinalIgnoreCase 比较；ToUpperInvariant
        // 是 CA1308 规约的大小写归一方向——ToLower 对部分字符会丢失信息）。
        var seenHosts = new HashSet<(string Host, int Port)>();
        foreach (var raw in primaryHost.Split(','))
        {
            var (primaryEntryHost, primaryEntryPort) = NormalizeHostEntry(raw, primaryCsBuilder.Port);
            if (primaryEntryHost.Length == 0) continue;
            seenHosts.Add((primaryEntryHost.ToUpperInvariant(), primaryEntryPort));
        }
        foreach (var cs in replicaConnectionStrings)
        {
            var sb = new NpgsqlConnectionStringBuilder(cs);
            // P2/P3 修复（十七轮 · 镜像 MySQL failover 校验）：副本凭据与主库不一致时快速失败——
            // 合并方式只保留 Host 条目，Username/Password/Database 差异被静默丢弃
            ThrowIfCredentialsMismatch(primaryCsBuilder, sb, "replica");
            // ITM-132 修复：primary Port≠5432 时，未编码的副本 Host 会继承连接串共享 Port
            // （Npgsql 的 Port 只对未内嵌端口的主机生效），读流量/故障转移落到错误实例——
            // 统一经 EncodeHostEntry 编码：primary Port≠5432 时全部 Host 显式 host:port（含显式 5432）。
            // v19 B3 勘正 + v21 B-1：Npgsql 缺 Host 返回空串（ITM-262 实证）。原跳过语义
            // 与 MySQL fail-fast / ITM-112 不对称——对齐姊妹改 fail-fast
            // v28 P3：异常类型改 ArgumentException——缺 Host 是连接串配置参数错误，与同文件
            // standby 侧 v28 / primary 侧 v26 H4 的 ArgumentException 语义统一
            if (string.IsNullOrWhiteSpace(sb.Host))
                throw new ArgumentException(
                    "Read replica connection string is missing 'Host='. ReadWriteSplit cannot silently skip a replica.");
            // v26 P3 H1：hosts.Add 前比对已收集集合（primary 主机条目 + 先前并入的副本）
            // v28 P3：副本侧改用复数版 NormalizeHostEntries 展开——副本串自身可为多主机列表
            //（Host="rb1,rb2"），单值版整串归一化使多主机副本与 primary/其他副本的重复条目
            // 恒不等，查重失效（同 Failover 入口 v28 勘正）
            foreach (var (replicaHost, replicaPort) in NormalizeHostEntries(sb.Host, sb.Port))
            {
                if (replicaHost.Length == 0) continue;
                if (!seenHosts.Add((replicaHost.ToUpperInvariant(), replicaPort)))
                    throw new ArgumentException(
                        $"replica Host '{replicaHost}:{replicaPort}' 与 primary 主机列表或其他副本中的条目重复："
                        + "多主机拼接将产生重复 Host 条目，LoadBalanceHosts 轮询把同一实例计入多份权重，"
                        + "故障转移/负载语义错乱。请为副本指定不同主机，或使用 AddPalNpgsqlDataSourceMultiHost 自定义完整连接串。");
            }
            hosts.Add(EncodeHostEntry(sb, primaryCsBuilder.Port));
        }

        if (hosts.Count > 0)
        {
            // ITM-110 修复：拼接规范化——主串无 Host 时直接赋值，避免前导逗号（同
            // AddPalNpgsqlDataSourceWithFailover 的 ITM-110 修复）。
            // v26 P3 H4：primary Host 已在副本循环前 fail-fast 校验非空，空赋值分支不可达，删除。
            builder.ConnectionStringBuilder.Host = $"{primaryHost},{string.Join(",", hosts)}";
            builder.ConnectionStringBuilder.LoadBalanceHosts = true;
            // ITM-067：必须 primary 亲和——"any" 会把写操作负载均衡到只读副本导致写失败
            builder.ConnectionStringBuilder.TargetSessionAttributes = "primary";
        }

        builder.ConnectionStringBuilder.ApplicationName = applicationName;
        // 优化（二十五轮 API 扫描 B-1）：MaxAutoPrepare=20（同 AddPalNpgsqlDataSourceWithFailover 注释）
        // 条件化（二十六轮 W2）：仅未设置（读 0）时赋默认——显式非零调优不被覆盖；
        // 显式禁用（写 0）与未设置不可区分，禁用走 configure 回调或自建 DataSource
        if (builder.ConnectionStringBuilder.MaxAutoPrepare == 0)
            builder.ConnectionStringBuilder.MaxAutoPrepare = 20;

        var dataSource2 = builder.Build();
        // v16 P2-2：补 DbDataSource 抽象双注册（对齐 MySQL MultiHost ITM-113 模式与基础入口）——
        // 缺失时 WithStores 连接工厂解析 DbDataSource 抛 InvalidOperationException
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource2);
// v19 P2-①：补具体型注册（Notifier 工厂 GetRequiredService<NpgsqlDataSource>() 依赖）
services.AddSingleton<NpgsqlDataSource>(dataSource2);
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

        // v35 P3：空白连接串 fail-fast（v34 五处姊妹收口的延续，同款口径）——空白串原样
        // 放行会延迟到 NpgsqlDataSourceBuilder.Build()/建连时才抛异常
        ArgumentException.ThrowIfNullOrWhiteSpace(multiHostConnectionString);

        var builder = new NpgsqlDataSourceBuilder(multiHostConnectionString);
        builder.ConnectionStringBuilder.ApplicationName = applicationName;
        // 优化（二十五轮 API 扫描 B-1）：MaxAutoPrepare=20（同 AddPalNpgsqlDataSourceWithFailover 注释；
        // 条件化（二十六轮 W2）：仅未设置（读 0）时赋默认——显式非零调优不被覆盖；
        // 显式禁用（写 0）与未设置不可区分，禁用走 configure 回调或自建 DataSource
        if (builder.ConnectionStringBuilder.MaxAutoPrepare == 0)
            builder.ConnectionStringBuilder.MaxAutoPrepare = 20;

        var dataSource3 = builder.Build();
        // v16 P2-2：补 DbDataSource 抽象双注册（对齐 MySQL MultiHost ITM-113 模式与基础入口）——
        // 缺失时 WithStores 连接工厂解析 DbDataSource 抛 InvalidOperationException
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource3);
        // v19 P2-①：补具体型注册（Notifier 工厂 GetRequiredService<NpgsqlDataSource>() 依赖）
        services.AddSingleton<NpgsqlDataSource>(dataSource3);
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
    /// <para>
    /// v37 P3：IPv6 字面量条目不支持自动端口追加——方括号形态（<c>[::1]:5432</c>，
    /// NormalizeHostEntry 整体保留）与裸 IPv6（<c>::1</c>，含多个冒号）追加 <c>:port</c>
    /// 均产出 Npgsql 无法解析的畸形条目（如 <c>[::1]:5432:5433</c>），此类条目 Host 原样
    /// 输出，端口需用内嵌端口语法或依赖默认端口 5432。
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

        // v26 P3 H3：Host 含内嵌端口（如 "pg1:5433"）时原实现直接拼 hostBuilder.Port，
        // Port 属性不吸收内嵌值（缺省 5432）——产出畸形 "pg1:5433:5432"。先经
        // NormalizeHostEntry 拆内嵌端口再编码；无内嵌端口时归一化结果与原值一致，行为不变。
        // v28 P3 声明（混编码，已由 v29 废弃）：Host 为多主机列表（"sb1,sb2"）时端口仅编码在
        // 尾条目（产出 "sb1,sb2:5433"）。
        // v29 P3（S3）：改经 NormalizeHostEntries 复数版逐条拆分归一化、每条独立编码后重新
        // 逗号拼接——原单值版对"已内嵌端口的多主机列表 + primaryPort≠5432"组合整串归一化
        //（"sb1:5433,sb2:5434" 含多个冒号，唯一冒号判定失败回退原串），编码时再追加共享端口
        // 产出三段畸形串 "sb1:5433,sb2:5434:5433"。逐条编码后每条目端口独立挂载
        //（"sb1:5433,sb2:5434"），单条目与未内嵌端口的多主机列表行为不变（后者由"仅尾条目
        // 编码"升级为逐条编码，端口值相同）。空条目原样保留空串（与 v28 整串穿透行为一致，
        // 防产出 ":port" 畸形前缀；缺 Host 由上游 fail-fast 拦截）。
        var entries = NormalizeHostEntries(host, hostBuilder.Port);
        List<string> encoded = [];
        foreach (var (bareHost, effectivePort) in entries)
        {
            if (bareHost.Length == 0)
                encoded.Add("");
            // v40 P2 定稿（v37/v38/v39 三轮该分支各留一缺口，本轮四象限一次闭环）：
            // IPv6 条目按形态三分派——
            // ① "[::1]"（方括号无端口）：端口在 Port 属性，按 ITM-132 契约（primary 非默认
            //    端口时全部条目须显式编码）追加 ":{effectivePort}" 产 "[::1]:5433"（Npgsql
            //    支持方括号 host + 冒号端口语法），primary/effective 均默认时原样；
            // ② "[::1]:5433"（方括号+内嵌端口）：端口已内嵌、Port 属性不参与，自洽原样；
            // ③ 裸 IPv6（"::1"）/畸形（"::1:5433"）：追加 ":port" 必产出畸形（冒号歧义），
            //    一律 fail-fast 指引方括号语法。
            else if (IsIpV6Literal(bareHost))
            {
                if (bareHost.StartsWith('[') && bareHost.EndsWith(']'))
                {
                    // 形态①：方括号定界纯 host
                    if (primaryPort != 5432 || effectivePort != primaryPort)
                        encoded.Add($"{bareHost}:{effectivePort}");
                    else
                        encoded.Add(bareHost);
                }
                else if (bareHost.StartsWith('['))
                {
                    // 形态②：方括号 + 内嵌端口，自洽
                    encoded.Add(bareHost);
                }
                else
                {
                    // 形态③：裸 IPv6 / 畸形混合——无法安全编码
                    throw new ArgumentException(
                        $"IPv6 主机条目 '{bareHost}' 无法安全编码端口（裸 IPv6 追加 ':port' 产出畸形条目）——请改用方括号语法 '[{bareHost}]' 或 '[{bareHost}]:{effectivePort}'。");
                }
            }
            else if (primaryPort != 5432 || effectivePort != 5432)
                encoded.Add($"{bareHost}:{effectivePort}");
            else
                encoded.Add(bareHost);
        }
        return string.Join(",", encoded);
    }

    /// <summary>
    /// v37 P3：判定条目是否为 IPv6 字面量——方括号开头（"[::1]:5432"，Npgsql 内嵌端口语法）
    /// 或含多个冒号（裸 IPv6 "::1"）。单冒号且后缀可解析为整数的普通 host:port 条目不受影响。
    /// </summary>
    private static bool IsIpV6Literal(string host)
    {
        if (host.StartsWith('['))
            return true;
        var colonCount = 0;
        foreach (var c in host)
        {
            if (c == ':' && ++colonCount > 1)
                return true;
        }
        return false;
    }

    /// <summary>
    /// v26 P3（H2/H3）：Host 内嵌端口归一化——解析 "host:port" 形式为 (裸名, 端口)。
    /// <para>
    /// Npgsql 连接串支持 <c>Host=pg1:5433</c> 内嵌端口语法，此时
    /// <see cref="NpgsqlConnectionStringBuilder.Host"/> 返回原始串（含端口），Port 属性不吸收
    /// 内嵌值——直接拿 Host 属性与裸名+Port 比较恒不等（v25 Failover 查重失效的根因）。
    /// 归一化后两侧统一为可比较的 (裸名, port) 对；未内嵌端口时返回 (原串, <paramref name="fallbackPort"/>)。
    /// </para>
    /// <para>
    /// 仅当冒号为<b>唯一</b>冒号且后缀可解析为整数才拆分：裸 IPv6 字面量（如 "::1"）内部
    /// 含冒号，误拆会产生 (":", 1) 畸形对；方括号 IPv6（"[::1]:5432"）不拆分，整体作主机名
    /// （较 v25 Failover 查重内联的 LastIndex 解析收紧了该边界）。
    /// </para>
    /// </summary>
    /// <param name="rawHost">原始 Host 条目（可为多主机列表中的单项，内部 Trim）。</param>
    /// <param name="fallbackPort">未内嵌端口时的回退端口（共享 Port）。</param>
    internal static (string Host, int Port) NormalizeHostEntry(string? rawHost, int fallbackPort)
    {
        var entry = rawHost?.Trim() ?? "";
        var colon = entry.LastIndexOf(':');
        if (colon >= 0 && entry.IndexOf(':') == colon
            && int.TryParse(entry.AsSpan(colon + 1), out var embedded))
        {
            return (entry[..colon], embedded);
        }
        return (entry, fallbackPort);
    }

    /// <summary>
    /// v28 P3：多主机列表归一化（复数版）——按顶层逗号拆分后逐条调
    /// <see cref="NormalizeHostEntry"/>。单值版把 "sb1,sb2" 整串当一个主机名归一化，
    /// 与任何单条目恒不等——多主机条目参与查重（Failover standby 侧 / ReadWriteSplit
    /// replica 侧 / ReadWriteRouter replica 侧的 seenHosts 收集）必须经本复数版展开，
    /// 否则重复条目漏检、拼接产生双份主机。
    /// </summary>
    /// <param name="rawHost">原始 Host（可为逗号分隔多主机列表，逐项 Trim；空项原样保留由调用方跳过）。</param>
    /// <param name="fallbackPort">未内嵌端口条目的回退端口（共享 Port）。</param>
    internal static IReadOnlyList<(string Host, int Port)> NormalizeHostEntries(string? rawHost, int fallbackPort)
    {
        List<(string Host, int Port)> entries = [];
        foreach (var raw in (rawHost ?? "").Split(','))
        {
            entries.Add(NormalizeHostEntry(raw, fallbackPort));
        }
        return entries;
    }
}
