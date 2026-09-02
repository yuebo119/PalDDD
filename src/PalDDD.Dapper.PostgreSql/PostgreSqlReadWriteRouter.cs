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

using System.Diagnostics.CodeAnalysis;
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
        // ITM-249 修复（F9，对齐同包姊妹 PostgreSqlSharding.ShardedDataSourceManager 三十七轮）：
        // 逐数据源异常隔离——原实现 Writer.DisposeAsync 抛出时 Reader 永不释放（读库连接池
        // 泄漏）。现挂起首异常继续释放 Reader，最后重抛（姊妹同款 OperationCanceledException
        // 不吞过滤）。
        // 传感器说明：Writer/Reader 为 NpgsqlDataSource 具体类型，构造经 NpgsqlDataSourceBuilder
        // 收口（无可注入替换点，fake 子类不可行），"Writer 抛异常后 Reader 仍被释放"无法单测
        // 隔离验证——验证方式为与姊妹逐 shard 隔离实现逐行形态对照（本包内同型已生效模式）。
        Exception? firstError = null;
        try
        {
            await Writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            firstError = ex;
        }
        if (Reader is not null)
        {
            try
            {
                await Reader.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstError ??= ex;  // 保首个异常，继续释放其余
            }
        }
        if (firstError is not null) throw firstError;
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
    [SuppressMessage("Design", "CA1031",
        Justification = "Reader build failure path must dispose the not-yet-registered writer DataSource for ANY exception before rethrowing (ITM-262); narrowing the catch would leak on unexpected Npgsql build failures.")]
    public static IServiceCollection AddPalReadWriteRouter(
        this IServiceCollection services,
        string primaryConnectionString,
        string[]? replicaConnectionStrings = null,
        string applicationName = "Pal.DDD")
    {
        ArgumentNullException.ThrowIfNull(services);

        // v35 P3：空白连接串 fail-fast（v34 五处姊妹收口的延续，同款口径）——空白串原样
        // 放行会延迟到 NpgsqlDataSourceBuilder.Build()/建连时才抛异常
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryConnectionString);

        // 主库（写）
        var writerBuilder = new NpgsqlDataSourceBuilder(primaryConnectionString);
        writerBuilder.ConnectionStringBuilder.ApplicationName = applicationName + "-Writer";
        // 优化（二十五轮 API 扫描 B-1）：MaxAutoPrepare=20——固定模板 SQL（SqlTemplates 全系）
        // 同一文本执行 ≥5 次后 Npgsql 自动 PREPARE，省去每次执行的 parse/plan 开销（连接级 LRU）。
        // ⚠️ 与 PostgreSqlJsonbExtensions 的动态拼接 SQL 互斥（动态 SQL 反复进出 LRU 会把
        // 固定模板逐出）——启用 Jsonb 动态查询的场景请自行构建 NpgsqlDataSourceBuilder 注册（文档已声明互斥）。
        // 条件化（二十六轮 W2）：仅未设置（读 0）时赋默认——显式非零调优不被覆盖；
        // 显式禁用（写 0）与未设置不可区分，禁用走 configure 回调或自建 DataSource
        if (writerBuilder.ConnectionStringBuilder.MaxAutoPrepare == 0)
            writerBuilder.ConnectionStringBuilder.MaxAutoPrepare = 20;
        var writer = writerBuilder.Build();

        // 读库
        NpgsqlDataSource? reader = null;
        if (replicaConnectionStrings is { Length: > 0 })
        {
            // P3（R40 ITM-262 顺带）：reader 构建路径（凭据失配/缺 Host/端口编码/reader Build）抛出时
            // writer 已 Build 尚未注册进 DI——失败路径同步释放，防宿主重试场景 NpgsqlDataSource 累积
            //（NpgsqlDataSource.Dispose 同步且未连接时无 IO）。
            try
            {
                List<string> hosts = [];
                var primaryCsBuilder = new NpgsqlConnectionStringBuilder(primaryConnectionString);
                // v27 P3（B 片 N4-①）：primary 缺 Host（Npgsql 返回空串——ITM-262 自证）时原
                // 拼接三元分支静默以副本列表充当主机列表，读库合并串缺主库 Host，读写分离
                // 退化为纯副本——fail-fast（镜像 v26 H4 PostgreSqlMultiHost.ReadWriteSplit 同款）
                var primaryHost = primaryCsBuilder.Host;
                if (string.IsNullOrWhiteSpace(primaryHost))
                    throw new ArgumentException(
                        "Primary connection string is missing 'Host='. Read-write split cannot silently substitute replicas as the host list.",
                        nameof(primaryConnectionString));
                // v27 P3（B 片 N4-②）：归一化主机集合查重——副本与 primary 同指一机（或副本互相
                // 重复）时拼接产生重复 Host 条目，LoadBalanceHosts=true 轮询把同一实例计入多份
                // 权重（镜像 MultiHost v26 H1；归一化经 NormalizeHostEntry 消除内嵌 host:port
                // 语法差异，主机名 ToUpperInvariant 后以 tuple 判等——DNS 大小写不敏感）
                var seenHosts = new HashSet<(string Host, int Port)>();
                foreach (var raw in primaryHost.Split(','))
                {
                    var (primaryEntryHost, primaryEntryPort) = PostgreSqlMultiHost.NormalizeHostEntry(raw, primaryCsBuilder.Port);
                    // v54 P3（B-P3-2）：primary 裸 IPv6 拦截（同 MultiHost 口径）
                    if (primaryEntryHost.StartsWith('[') && !primaryEntryHost.Contains(']'))
                        throw new ArgumentException(
                            $"primary Host 条目 '{primaryEntryHost}' 方括号未闭合——请改用 '[host]' 或 '[host]:port' 语法。");
                    if (primaryEntryHost.Count(c => c == ':') > 1 && !primaryEntryHost.StartsWith('['))
                        throw new ArgumentException(
                            $"primary Host 条目 '{primaryEntryHost}' 是裸 IPv6——未加方括号的多冒号条目在 Npgsql 多主机 Host 列表中歧义。请改用 '[host]' 或 '[host]:port' 语法。");
                    // v53 P2：空条目 fail-fast（镜像 MySQL v49 姊妹）——列表空段是死节点
                    if (primaryEntryHost.Length == 0)
                        throw new ArgumentException(
                            "primary Host 列表存在空条目（如 \"Host=pg1,,pg2\"）：空条目并入主机列表后"
                            + "成为参与轮询的死节点，故障转移静默失败。请清理 Host 列表中的空条目。");
                    // v52 P2：primary 内部重复也抛（对齐 MultiHost Failover/standby 侧）
                    if (!seenHosts.Add((primaryEntryHost.ToUpperInvariant(), primaryEntryPort)))
                        throw new ArgumentException(
                            $"primary Host 列表存在重复条目 '{primaryEntryHost}:{primaryEntryPort}'："
                            + "多主机拼接将产生重复 Host 条目，故障转移语义错乱。请去重 primary 主机列表。");
                }
                foreach (var (cs, index) in replicaConnectionStrings.Select((c, i) => (c, i)))
                {
                    var sb = new NpgsqlConnectionStringBuilder(cs);
                    // P2/P3 修复（十七轮 · 镜像 MySQL failover 校验）：reader 连接串以主库串为基线仅追加 Host——
                    // 副本凭据/库名与主库不一致时被静默丢弃，此处快速失败（复用 PostgreSqlMultiHost 校验）
                    PostgreSqlMultiHost.ThrowIfCredentialsMismatch(primaryCsBuilder, sb, "replica");
                    // PD17 姊妹统一：端口编码进 Host 条目
                    // ITM-112 修复（验证轮返工）：副本连接串缺 Host 是配置错误——原实现静默跳过，
                    // 副本被无声丢弃（读写分离静默退化为纯主库、流量全走写库，无任何提示）；显式抛
                    // ArgumentException 暴露配置问题（框架库语义：配置错误快速失败）。
                    // P0 修复（R40 ITM-262）：消息只报副本索引，不内嵌连接串原文——连接串含 Password
                    // 等敏感段，异常进宿主日志即凭据泄漏（探针实锤；对齐 ThrowIfCredentialsMismatch
                    // 姊妹"只含角色名"形态）。注意触发条件：须凭据与主库一致才到达此处（前置校验拦截差异凭据）。
                    // 注意：Npgsql 的 NpgsqlConnectionStringBuilder.Host 在连接串未指定时
                    // 返回空字符串而非 null（Npgsql 10.0.3 实证）——必须用 IsNullOrWhiteSpace 判定，
                    // 仅判 null 永不触发（假修）。
                    if (string.IsNullOrWhiteSpace(sb.Host))
                        throw new ArgumentException(
                            $"Replica connection string at index {index} has no Host. Each replica must specify a Host.",
                            nameof(replicaConnectionStrings));
                    // v27 P3（B 片 N4-②）：hosts.Add 前比对已收集集合（primary 主机条目 + 先前
                    // 并入的副本）——归一化后重复即 fail-fast，不把重复条目拼进 reader 主机列表
                    // v28 P3：副本侧改用复数版 NormalizeHostEntries 展开——副本串自身可为多主机
                    // 列表（Host="rb1,rb2"），单值版整串归一化使重复条目漏检（同 MultiHost
                    // Failover/ReadWriteSplit 两入口的 v28 勘正）
                    foreach (var (replicaHost, replicaPort) in PostgreSqlMultiHost.NormalizeHostEntries(sb.Host, sb.Port))
                    {
                        // v53 P2：副本列表空段 fail-fast（镜像 MySQL v49 姊妹）
                        if (replicaHost.Length == 0)
                            throw new ArgumentException(
                                "replica Host 列表存在空条目：空条目并入读写分离后成为参与轮询的死节点。"
                                + "请清理副本 Host 列表中的空条目。");
                        if (!seenHosts.Add((replicaHost.ToUpperInvariant(), replicaPort)))
                            throw new ArgumentException(
                                $"Replica connection string at index {index} Host '{replicaHost}:{replicaPort}' duplicates the primary host list or another replica: "
                                + "multi-host concatenation would produce duplicate Host entries (e.g. \"pg1,pg1\"), LoadBalanceHosts counting the same instance multiple times. "
                                + "Specify a distinct host for each replica.",
                                nameof(replicaConnectionStrings));
                    }
                    // ITM-132 修复：primary Port≠5432 时，未编码的副本 Host 会继承 reader 连接串共享 Port
                    // （Npgsql 的 Port 只对未内嵌端口的主机生效），读流量/故障转移落到错误实例——
                    // 统一经 EncodeHostEntry 编码：primary Port≠5432 时全部 Host 显式 host:port（含显式 5432）。
                    hosts.Add(PostgreSqlMultiHost.EncodeHostEntry(sb, primaryCsBuilder.Port));
                }

                if (hosts.Count > 0)
                {
                    var readerCs = primaryConnectionString;
                    var psb = new NpgsqlConnectionStringBuilder(readerCs);
                    // ITM-110 姊妹路径修复（验证轮返工）：主库串无 Host 时原 `psb.Host += ",..."`
                    // 产生前导逗号（",replica"），Npgsql 解析出空主机条目
                    // v27 P3（B 片 N4-①）：primary Host 已在副本循环前 fail-fast 校验非空
                    //（primaryHost 与 psb.Host 同源 primaryConnectionString）——空赋值分支
                    // 不可达，删除三元改直拼（对齐 MultiHost v26 H4 后形态）
                    psb.Host = primaryHost + "," + string.Join(",", hosts);
                    psb.LoadBalanceHosts = true;
                    // ITM-181 修复（二十九轮）：any → read-only——修复前 any 对列表内主机
                    // 轮询（含写主库），读流量负载均衡到 write master，读写分离稀释、主库
                    // 连接池承压。read-only 意指"会话默认不接受读写事务"（Npgsql 10.0.3
                    // 实证：hot standby 副本满足，主库不满足）——读流量优先副本，
                    // 全部副本不可达时连接失败（Npgsql read-only target_session_attrs 为过滤语义：不满足 read-only 的主库被跳过、不参与 fallback——P3-SRC-604 勘正原"回退主库"失实描述）。
                    // ⚠️ 连接串值必须是连字符 "read-only"（NpgsqlConnectionStringBuilder
                    // 实证：readonly/read_only 抛 ArgumentException）。
                    psb.TargetSessionAttributes = "read-only";
                    psb.ApplicationName = applicationName + "-Reader";

                    var readerBuilder = new NpgsqlDataSourceBuilder(psb.ConnectionString);
                    // 优化（二十五轮 API 扫描 B-1）：读副本同 writer 启用自动预备（见上方 writer 注释）
                    // 条件化（二十六轮 W2）：仅未设置（读 0）时赋默认——显式非零调优不被覆盖；
                    // 显式禁用（写 0）与未设置不可区分，禁用走 configure 回调或自建 DataSource
                    if (readerBuilder.ConnectionStringBuilder.MaxAutoPrepare == 0)
                        readerBuilder.ConnectionStringBuilder.MaxAutoPrepare = 20;
                    reader = readerBuilder.Build();
                }
            }
            catch
            {
                try { writer.Dispose(); } catch { /* 释放失败不掩盖根因 */ }
                throw;
            }
        }

        var router = new PostgreSqlReadWriteRouter(writer, reader);
        services.AddSingleton(router);
        // 双重注册（router 持有 + 独立注入）经实测无害：NpgsqlDataSource.DisposeAsync 幂等
        // （2026-08-15 file-based app 探针：二次/三次释放均不抛），容器重复释放安全。
        services.AddSingleton(writer); // 主库可直接注入

        return services;
    }
}
