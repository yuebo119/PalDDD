using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PalDDD.Core.Repository;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.Projections;
using PalDDD.Transactions;
using PalORM;
using PalORM.Sqlite;

namespace PalDDD.PalORM.Sqlite;

/// <summary>
/// PalORM SQLite DI 扩展 —— 一键注册 7 Store + UnitOfWork + Scoped DataSession。
/// </summary>
public static class SqlitePalOrmExtensions
{
    /// <summary>
    /// 注册 PalORM SQLite 适配包（7 Store + UnitOfWork + DataSession）。
    /// <para>
    /// <b>事务自动传播</b>：DataSession 注册为 Scoped —— 同一请求作用域内所有 Store 注入同一实例，
    /// UnitOfWork.BeginTransactionAsync 后 CreateCommand 自动附加 GetActiveTransaction。
    /// </para>
    /// <para>
    /// ⚠️ <b>与 Dapper 适配器的 outbox status 编码互斥（P2 定案声明）</b>：
    /// PalORM 版 outbox_messages.status 存 <b>int 枚举值</b>，Dapper 版存<b>字符串字面量</b>
    /// （'Pending' 等）——同一物理表两者编码互斥，<b>禁止对同一数据库同时注册
    /// PalORM 与 Dapper 的 Outbox 实现</b>（互相读不到对方状态）。选型后全程使用同一适配器族。
    /// </para>
    /// <para>
    /// <b>DI 工厂 sync-over-async</b>：<see cref="DataSession{TProvider}"/>.<c>CreateAsync</c> 是异步方法，
    /// DI 工厂是同步的 —— 用 <c>GetAwaiter().GetResult()</c> 同步阻塞。仅在 Scoped 解析时执行（请求起始），
    /// 非热路径；如未来死锁可改为 <c>Task.Run().Result</c> 或建议 PalORM 提供 IDataSessionFactory。
    /// </para>
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="connectionString">SQLite 连接串（如 "Data Source=:memory:" 或 "Data Source=app.db"）。</param>
    /// <param name="clock">可选时间提供者（用于 created_at/processed_at 应用层赋值）。</param>
    public static IServiceCollection AddPalOrmSqlite(
        this IServiceCollection services,
        string connectionString,
        DbOptions? options = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Scoped DataSession（同步阻塞创建，仅在请求起始）
        services.AddScoped(sp =>
        {
            var opts = options ?? DbOptions.Development(connectionString);
            return DataSession<SqliteProvider>.CreateAsync(opts, default).GetAwaiter().GetResult();
        });

        // 时间提供者（默认 System）
        // P2/P3 修复（十七轮）：clock 显式实参分支改 AddSingleton 覆盖——显式传参=强意图，
        // TryAdd 会在容器已有 TimeProvider 时静默丢弃显式实参（调用方以为时钟生效实则沿用旧注册）；
        // 仅未传 clock 时保持 TryAdd（不覆盖用户先注册的 TimeProvider，八轮评审 P2 语义不变）。
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }
        else
        {
            services.TryAddSingleton(TimeProvider.System); // TryAdd——用户先注册的 TimeProvider 不被覆盖
        }

        // 7 Store + UnitOfWork（全部 Scoped，共享同一 DataSession）
        services.AddScoped<IPalOutboxStore, SqliteOutboxStore>();
        services.AddScoped<IInboxStore, SqliteInboxStore>();
        services.AddScoped(typeof(ISagaStateStore<>), typeof(SqliteSagaStateStore<>));
        // ⚠️ Saga Data 陷阱（四轮评审 P2）：此注册的 jsonTypeInfo 恒为 null——用户自定义 TState
        // 字段不持久化（saga_data 列写 NULL，重启丢业务字段）。
        // P2/P3 修复（十七轮）：便捷注册 AddPalOrmSqliteSagaSnapshot<TState> 已提供——
        // 以具体泛型覆盖开放泛型并闭包传入 JsonTypeInfo，需 Saga 快照时调用。
        services.AddScoped<IEventLog, SqliteEventLog>();
        services.AddScoped<IProjectionCheckpointStore, SqliteProjectionCheckpointStore>();
        services.AddScoped<IIdempotencyStore, SqliteIdempotencyStore>();
        services.AddScoped<IUnitOfWork, SqlitePalOrmUnitOfWork>();

        return services;
    }

    /// <summary>
    /// P2/P3 修复（十七轮）：Saga 快照持久化（saga_data 列）便捷注册。
    /// <see cref="AddPalOrmSqlite"/> 的开放泛型注册 <c>SqliteSagaStateStore&lt;&gt;</c> 无
    /// <c>JsonTypeInfo</c> 传入通道——jsonTypeInfo 恒 null，TState 业务字段不持久化。
    /// 此方法以具体泛型注册覆盖开放泛型（MS DI 具体泛型优先），闭包构造传入
    /// <paramref name="jsonTypeInfo"/>。
    /// <para>⚠️ <b>不调用则 saga_data 不持久化（重启丢业务字段）</b>——须在
    /// <see cref="AddPalOrmSqlite"/> 之后调用（依赖其 DataSession 注册）。</para>
    /// </summary>
    public static IServiceCollection AddPalOrmSqliteSagaSnapshot<TState>(
        this IServiceCollection services,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TState> jsonTypeInfo)
        where TState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        services.AddScoped(typeof(ISagaStateStore<TState>), sp =>
            // P3 修复（十八轮验证轮 F1）：解析容器 TimeProvider——开放泛型 DI 路径会注入容器注册的
            // TimeProvider（如 FakeTimeProvider），便捷注册此前回落 System 使租约/时间断言漂移
            new SqliteSagaStateStore<TState>(
                sp.GetRequiredService<DataSession<SqliteProvider>>(),
                jsonTypeInfo,
                sp.GetService<TimeProvider>()));

        return services;
    }
}
