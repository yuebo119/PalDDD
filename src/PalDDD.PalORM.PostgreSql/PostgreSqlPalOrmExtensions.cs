using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PalDDD.Core.Repository;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.Projections;
using PalDDD.Transactions;
using PalORM;
using PalORM.PostgreSql;

namespace PalDDD.PalORM.PostgreSql;

/// <summary>
/// PalORM PostgreSQL DI 扩展 —— 一键注册 6 Store + UnitOfWork + Scoped DataSession。
///（v34 P3 计数勘正：原"7 Store"不实——抽象层 Store 接口共 6 个
/// Outbox/Inbox/Saga/EventLog/ProjectionCheckpoint/Idempotency，本方法全数注册 + UnitOfWork）。
/// <para>
/// <b>PG 优势</b>：支持 RETURNING 子句 —— Outbox LeasePending / Inbox TryStart 走单语句原子路径（无两步回读）。
/// BulkInsert 走 Npgsql Binary COPY（性能最优）。
/// </para>
/// </summary>
public static class PostgreSqlPalOrmExtensions
{
    /// <summary>
    /// 注册 PalORM PostgreSQL 适配包。
    /// <para>
    /// <b>DI 工厂 sync-over-async（ITM-166 声明，v34 P3 补齐——对齐 MySQL/SQLite 版同款声明）</b>：
    /// <see cref="DataSession{TProvider}"/>.<c>CreateAsync</c> 是异步方法，DI 工厂是同步的——
    /// 用 <c>GetAwaiter().GetResult()</c> 同步阻塞。仅在 Scoped 解析时执行（请求起始），非热路径；
    /// 如未来死锁可改为 <c>Task.Run().Result</c> 或建议 PalORM 提供 IDataSessionFactory。
    /// </para>
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="connectionString">PostgreSQL 连接串（如 "Host=localhost;Username=user;Password=pass;Database=mydb"）。</param>
    /// <param name="options">
    /// 可选 DbOptions。v36 P3 二义性声明：非 null 时 <paramref name="connectionString"/>
    /// 仅用于入口守卫校验（非空白检查），连接串以 options 为准——两参同传且不一致时
    /// 静默采用 options（<c>options ?? DbOptions.Development(connectionString)</c>），无告警；
    /// options 为 null 时以 connectionString 构造 Development 默认 DbOptions。
    /// </param>
    /// <param name="clock">可选时间提供者。</param>
    /// <param name="configureResilience">
    /// 可选弹性配置回调（PalORM 5.4 弹性层）——CreateAsync 后对会话调用，如
    /// <c>s => s.WithRetry(3, i => TimeSpan.FromMilliseconds(50 * i))</c> 与
    /// <c>s => s.WithCircuitBreaker(5, TimeSpan.FromSeconds(30))</c>。作用域为连接建立 +
    /// 只读查询内置管线（SELECT/Get/聚合）；写入路径与事务内查询维持直连（非幂等写不重试）。
    /// null（默认）= 弹性直通，热路径零额外开销。
    /// <para>v53 P3 次序语义：DI 工厂中 CreateAsync 先于回调执行，本会话的<b>首个连接建立</b>
    /// 永远发生在弹性配置之前（连接重试是 CreateAsync 自有循环）——回调配置的连接重试
    /// 仅对后续重连生效，首个连接使用 DbOptions 默认弹性。</para>
    /// </param>
    public static IServiceCollection AddPalOrmPostgreSql(
        this IServiceCollection services,
        string connectionString,
        DbOptions? options = null,
        TimeProvider? clock = null,
        Action<DataSession<PostgreSqlProvider>>? configureResilience = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddScoped(sp =>
        {
            var opts = options ?? DbOptions.Development(connectionString);
            var session = DataSession<PostgreSqlProvider>.CreateAsync(opts, default).GetAwaiter().GetResult();
            // v53 P2：回调异常时释放已打开的会话（三 Provider 同款，见 Sqlite 版注释）
            try
            {
                configureResilience?.Invoke(session);
                return session;
            }
            catch
            {
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        });

        // P2 修复（八轮评审）：改 TryAddSingleton 对齐 Sqlite 版（SqlitePalOrmExtensions）——
        // AddSingleton(clock ?? System) 会覆盖用户先注册的 TimeProvider（如测试注入
        // FakeTimeProvider），时钟覆盖导致租约/审计时间失真；TryAdd 保用户注册优先。
        // P2/P3 修复（十七轮）：clock 显式实参分支改回 AddSingleton 覆盖——显式传参=强意图，
        // TryAdd 会在容器已有 TimeProvider 时静默丢弃显式实参（调用方以为时钟生效实则沿用旧注册）；
        // 仅未传 clock 时保持 TryAdd（不覆盖用户先注册的 TimeProvider，八轮评审语义不变）。
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }
        else
        {
            services.TryAddSingleton(TimeProvider.System);
        }

        services.AddScoped<IPalOutboxStore, PostgreSqlOutboxStore>();
        services.AddScoped<IInboxStore, PostgreSqlInboxStore>();
        services.AddScoped(typeof(ISagaStateStore<>), typeof(PostgreSqlSagaStateStore<>));
        // ⚠️ Saga Data 陷阱（四轮评审 P2；v18 行为变更注释缝合）：本开放泛型注册无
        // jsonTypeInfo 传入通道——调用 AddPalOrmPostgreSqlSagaSnapshot<TState> 前，Save 时基类
        // fail-fast 抛 InvalidOperationException（ITM-228 后行为，v16 前为静默写 NULL）。
        // 需 Saga 快照持久化请用便捷注册方法。
        // P2/P3 修复（十七轮）：便捷注册 AddPalOrmPostgreSqlSagaSnapshot<TState> 已提供——
        // 以具体泛型覆盖开放泛型并闭包传入 JsonTypeInfo，需 Saga 快照时调用。
        services.AddScoped<IEventLog, PostgreSqlEventLog>();
        services.AddScoped<IProjectionCheckpointStore, PostgreSqlProjectionCheckpointStore>();
        services.AddScoped<IIdempotencyStore, PostgreSqlIdempotencyStore>();
        services.AddScoped<IUnitOfWork, PostgreSqlPalOrmUnitOfWork>();

        return services;
    }

    /// <summary>
    /// P2/P3 修复（十七轮）：Saga 快照持久化（saga_data 列）便捷注册。
    /// <see cref="AddPalOrmPostgreSql"/> 的开放泛型注册 <c>PostgreSqlSagaStateStore&lt;&gt;</c> 无
    /// <c>JsonTypeInfo</c> 传入通道——jsonTypeInfo 恒 null，TState 业务字段不持久化。
    /// 此方法以具体泛型注册覆盖开放泛型（MS DI 具体泛型优先），闭包构造传入
    /// <paramref name="jsonTypeInfo"/>。
    /// <para>⚠️ <b>不调用则 saga_data 不持久化（重启丢业务字段）</b>——须在
    /// <see cref="AddPalOrmPostgreSql"/> 之后调用（依赖其 DataSession 注册）。</para>
    /// </summary>
    public static IServiceCollection AddPalOrmPostgreSqlSagaSnapshot<TState>(
        this IServiceCollection services,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TState> jsonTypeInfo)
        where TState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        services.AddScoped(typeof(ISagaStateStore<TState>), sp =>
            // P3 修复（十八轮验证轮 F1）：解析容器 TimeProvider——开放泛型 DI 路径会注入容器注册的
            // TimeProvider（如 FakeTimeProvider），便捷注册此前回落 System 使租约/时间断言漂移
            new PostgreSqlSagaStateStore<TState>(
                sp.GetRequiredService<DataSession<PostgreSqlProvider>>(),
                jsonTypeInfo,
                sp.GetService<TimeProvider>()));

        return services;
    }
}
