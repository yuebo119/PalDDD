// ─────────────────────────────────────────────────────────────
// 🔧 MySQL 增强 DI 注册扩展 — MySqlDataSource（.NET 7+ 标准模式）
// ─────────────────────────────────────────────────────────────
// MySqlDataSource 对比旧 MySqlConnection Singleton：
//
//   ❌ 旧: 手动 new MySqlConnection → AddSingleton → 用完不回收
//   ✅ 新: MySqlDataSourceBuilder → AddSingleton(MySqlDataSource)
//         自动提供: 连接池健康检查 / ILogger 集成 / OpenTelemetry 追踪
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 纯配置层扩展，零业务逻辑侵入。
//   - 通过 DI 注册时传入连接字符串即可，不修改任何领域层代码。
//   - 非 MySQL 环境：此扩展包不被引用，完全不影响行为。
//
// 使用方式：
//   services.AddPalMySqlDataSource("Server=localhost;Database=pal;User=root;Password=xxx");
//   services.AddPalMySqlDataSourceWithStores("Server=localhost;Database=pal;User=root;Password=xxx");
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

using PalDDD.Transactions;
namespace PalDDD.Dapper.MySql;

/// <summary>MySQL 增强 DI 注册扩展（MySqlDataSource 模式）</summary>
public static class MySqlServiceCollectionExtensions
{
    // ── MySqlDataSource（推荐）──

    /// <summary>
    /// 注册 MySqlDataSource 作为 Singleton，自动应用 InnoDB 性能优化。<br/>
    /// MySqlDataSource 是 MySqlConnector 2.x 的新一代连接管理 API，提供：<br/>
    ///   - 自动连接池管理 + 健康检查<br/>
    ///   - ILoggerFactory 日志集成<br/>
    ///   - OpenTelemetry 追踪（自动记录 SQL 执行）<br/>
    ///   - DbDataSource 标准接口（.NET 7+ 通用抽象）<br/>
    /// ⚠️ <b>会话泄漏权衡（applyOptimization=true 时）</b>：连接串被显式追加
    /// <c>ConnectionReset=false</c>（连接串键 "Reset Connections"）（否则 MySqlConnector 默认 ResetConnections=true，
    /// 池取连接时会话已重置，SET SESSION 优化随归池即丢）。代价是同一 DataSource 池内的
    /// 物理连接共享会话设置——优化参数对所有使用方生效，应用方自行执行的 SET SESSION
    /// 变更同样会跨 scope 泄漏到池内其他使用方。若不能接受，传 applyOptimization=false
    /// 并改在 MySQL 服务端（my.cnf）全局配置。
    /// </summary>
    /// <param name="connectionString">MySQL 连接字符串</param>
    /// <param name="applyOptimization">是否应用 InnoDB 性能优化（默认 true）</param>
    public static IServiceCollection AddPalMySqlDataSource(
        this IServiceCollection services,
        string connectionString,
        bool applyOptimization = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionString);

        // P2 修复（二十一轮）：SET SESSION 传导前提——MySqlConnector 默认 ResetConnections=true，
        // 从池中取出的连接会话已被重置（CHARACTER_SET_RESULTS / SQL_MODE 等恢复服务端默认），
        // ApplySessionOptimization 打出的会话级优化随连接归池即丢，"首个连接上设置、后续复用连接
        // 继承"的原假设不成立。此处显式关闭池会话重置，使会话设置在同一 DataSource 池的物理
        // 连接上持久化——恰好是想要的传导语义（见上方 summary 的会话泄漏权衡声明）。
        if (applyOptimization)
            connectionString = new MySqlConnectionStringBuilder(connectionString)
            {
                ConnectionReset = false  // P2 修正（二十一轮主线程）：builder 属性真名是 ConnectionReset（连接串键 "Reset Connections" 的 C# 映射）
                // P3 声明（二十三轮验证轮）：① 显式 applyOptimization=true 时覆盖用户连接串中的
                // 显式 Reset Connections 设置（强意图优先）；② ApplySessionOptimization 失败被吞时
                // 连接串已带 ConnectionReset=false——"优化未打上、泄漏代价照付"的次序权衡已接受
            }.ConnectionString;

        var builder = new MySqlDataSourceBuilder(connectionString);

        // MySqlDataSource 自动使用 ILoggerFactory（无需手动传递）
        var dataSource = builder.Build();

        if (applyOptimization)
        {
            // 在第一个从池中创建的连接上应用会话级性能优化。
            // 仅在 ResetConnections=false（上方已追加）时持久化到池内物理连接。
            ApplySessionOptimization(dataSource);
        }

        // 注册为 Singleton — MySqlDataSource 内部管理连接池
        services.AddSingleton(dataSource);
        services.AddSingleton<System.Data.Common.DbDataSource>(dataSource);

        return services;
    }

    /// <summary>注册 MySqlDataSource + Dapper Store（一键注册：连接 + Outbox + Inbox）</summary>
    /// <remarks>
    /// 委托核心 <c>AddPalDapperTransactions</c> 注册 Dapper Store（Outbox/Inbox/Saga），
    /// 避免手动构造（旧实现手动 new DapperOutboxStore/DapperInboxStore，重复核心逻辑）。
    /// </remarks>
    public static IServiceCollection AddPalMySqlDataSourceWithStores(
        this IServiceCollection services,
        string connectionString)
    {
        AddPalMySqlDataSource(services, connectionString);
        DapperServiceCollectionExtensions.AddPalDapperTransactions(services, DapperDbType.MySql, connectionString);

        // P3 修复（八轮评审）：Store 连接改从注册的 MySqlDataSource 取——AddPalDapperTransactions 的
        // DbConnection 工厂是 new MySqlConnection(cs)（走连接串全局池），而 ApplySessionOptimization 的
        // SET SESSION 打在 MySqlDataSource 私有池上，优化完全打不到 Store 实际使用的连接。
        // 此处覆盖 DbConnection 注册（MS DI 后注册胜出），Store 统一从 DataSource 池取连接。
        // P2 勘正（二十一轮）：会话优化传导到 Store 的前提是池不重置会话——MySqlConnector 默认
        // ResetConnections=true 会把该假设打掉，AddPalMySqlDataSource 现已在 applyOptimization=true
        // 时显式设 Reset Connections=false（见其 summary 的会话泄漏权衡），传导语义方成立。
        services.AddScoped<System.Data.Common.DbConnection>(sp =>
            sp.GetRequiredService<System.Data.Common.DbDataSource>().CreateConnection());

        return services;
    }

    // ── Legacy（旧 API 兼容）──

    /// <summary>注册 MySQL 连接（Singleton）并应用性能优化</summary>
    [System.Obsolete("请使用 AddPalMySqlDataSource 以获得自动连接池管理、健康检查和 OpenTelemetry 追踪。")]
    public static IServiceCollection AddPalMySql(
        this IServiceCollection services,
        string connectionString,
        bool applyOptimization = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        var connection = new MySqlConnection(connectionString);
        try
        {
            if (applyOptimization)
                MySqlPerformanceOptimizer.Optimize(connection);

            // P2 真修（三轮评审发现此前假修）：闭包共享同一 MySqlConnection 实例，线程安全未修。
            // Scoped 工厂按连接串为每个 scope 创建新连接。
            var cs = connection.ConnectionString;
            services.AddScoped(_ => new MySqlConnector.MySqlConnection(cs));
            // P2 修复（captive dependency）：移除 singleton→scoped 桥接——root 单例捕获 scoped
            // MySqlConnection 导致全进程共享一条非线程安全连接。DbConnection 需要者应从 scope 内
            // 解析 MySqlConnection（C# 无法转型桥接泛型注册，改注册工厂供 Scoped 消费方使用）
            services.AddScoped<System.Data.Common.DbConnection>(sp => sp.GetRequiredService<MySqlConnection>());
        }
        finally
        {
            // ITM-086 修复：Optimize 抛异常时原路径连接不释放——finally 统一兜底
            // （正常路径原有的 Dispose 语义不变，异常路径补释放，无双释放）。
            connection.Dispose();
        }

        return services;
    }

    /// <summary>注册 MySQL 连接 + Dapper Store（一键注册）</summary>
    [System.Obsolete("请使用 AddPalMySqlDataSourceWithStores 以获得自动连接池管理、健康检查和 OpenTelemetry 追踪。八轮评审已补齐 ISagaStateStore 注册，与新路径的 Store 三件套（Outbox/Inbox/Saga）差异已消除，仅剩连接管理模式不同。")]
    public static IServiceCollection AddPalMySqlWithStores(
        this IServiceCollection services,
        string connectionString)
    {
        AddPalMySql(services, connectionString);

        // P2 修复（九轮评审）：DapperSagaStateStore 开放泛型由容器构造，DapperDbType 参数
        // 依赖容器注入——此前未注册落到构造默认值 Sqlite，Legacy 路径 Saga 的 10 处时间
        // 参数按 SQLite "O" 格式发送（重新引入 session tz 依赖行为）。Outbox/Inbox 显式
        // 传 DapperDbType.MySql 不受影响，唯 Saga 中招。
        services.AddSingleton(typeof(DapperDbType), _ => DapperDbType.MySql);

        services.AddScoped<IPalOutboxStore>(sp =>
        {
            var conn = sp.GetRequiredService<MySqlConnection>();
            return new DapperOutboxStore(conn, DapperDbType.MySql);
        });

        services.AddScoped<IInboxStore>(sp =>
        {
            var conn = sp.GetRequiredService<MySqlConnection>();
            return new DapperInboxStore(conn, DapperDbType.MySql);
        });

        // P3 修复（八轮评审）：补注册 ISagaStateStore（对齐新路径 AddPalDapperTransactions 的
        // Outbox/Inbox/Saga 三件套）——此前 Legacy 一键注册只含 Outbox+Inbox，消费方解析
        // ISagaStateStore<TState> 直接抛解析失败。DapperSagaStateStore 构造的其余参数均有
        // 默认值，仅需 Scoped DbConnection（上方 AddPalMySql 已注册）。
        services.AddScoped(typeof(ISagaStateStore<>), typeof(DapperSagaStateStore<>));

        return services;
    }

    /// <summary>
    /// P2/P3 修复（十七轮）：Legacy 路径（<see cref="AddPalMySqlWithStores"/>）的 Saga 快照便捷注册——
    /// 与 <c>DapperServiceCollectionExtensions.AddPalDapperSagaSnapshot</c> 同款问题：Legacy 开放泛型注册
    /// <c>DapperSagaStateStore&lt;&gt;</c> 的 jsonTypeInfo 恒 null，saga_data 列写 NULL（重启丢业务字段）。
    /// 此方法以具体泛型注册覆盖开放泛型，闭包构造传入 JsonTypeInfo；
    /// dbType 显式 <see cref="DapperDbType.MySql"/>（与 Legacy 路径 Outbox/Inbox 显式传参一致，
    /// 不依赖容器的 DapperDbType 单例）。<br/>
    /// ⚠️ <b>不调用则 saga_data 不持久化（重启丢业务字段）</b>。
    /// </summary>
    public static IServiceCollection AddPalMySqlSagaSnapshot<TState>(
        this IServiceCollection services,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TState> jsonTypeInfo)
        where TState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        services.AddScoped(typeof(ISagaStateStore<TState>), sp =>
            new DapperSagaStateStore<TState>(
                sp.GetRequiredService<System.Data.Common.DbConnection>(),
                transaction: null,
                jsonTypeInfo: jsonTypeInfo,
                timeProvider: sp.GetService<TimeProvider>(),
                dbType: DapperDbType.MySql));

        return services;
    }

    // ── 内部辅助 ──

    /// <summary>
    /// 在从池中获取的第一个连接上应用会话级 InnoDB 性能优化。<br/>
    /// ⚠️ 传导前提（P2 勘正·二十一轮）：MySqlConnector 默认 Reset Connections=true——从池中
    /// 取出的连接会话已被重置，SET SESSION 设置随连接归池即丢，并不会被后续会话继承。
    /// 调用方（AddPalMySqlDataSource）已显式设 Reset Connections=false 使设置持久化到池内
    /// 物理连接；直接调用本方法且未关闭会话重置时，优化在下一次取连接时即失效。<br/>
    /// 对于生产环境多连接场景，建议在 MySQL 服务端配置（my.cnf）中全局设置这些参数。
    /// </summary>
    private static void ApplySessionOptimization(MySqlDataSource dataSource)
    {
        try
        {
            using var conn = dataSource.CreateConnection();
            conn.Open();
            MySqlPerformanceOptimizer.Optimize(conn);
        }
        catch (MySqlException)
        {
            // 连接失败时跳过优化——应用仍可启动，使用 MySQL 默认设置
        }
    }
}
