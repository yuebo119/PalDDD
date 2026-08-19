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
/// PalORM PostgreSQL DI 扩展 —— 一键注册 7 Store + UnitOfWork + Scoped DataSession。
/// <para>
/// <b>PG 优势</b>：支持 RETURNING 子句 —— Outbox LeasePending / Inbox TryStart 走单语句原子路径（无两步回读）。
/// BulkInsert 走 Npgsql Binary COPY（性能最优）。
/// </para>
/// </summary>
public static class PostgreSqlPalOrmExtensions
{
    /// <summary>
    /// 注册 PalORM PostgreSQL 适配包。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="connectionString">PostgreSQL 连接串（如 "Host=localhost;Username=user;Password=pass;Database=mydb"）。</param>
    /// <param name="clock">可选时间提供者。</param>
    public static IServiceCollection AddPalOrmPostgreSql(
        this IServiceCollection services,
        string connectionString,
        DbOptions? options = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddScoped(sp =>
        {
            var opts = options ?? DbOptions.Development(connectionString);
            return DataSession<PostgreSqlProvider>.CreateAsync(opts, default).GetAwaiter().GetResult();
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
        // ⚠️ Saga Data 陷阱（四轮评审 P2）：此注册的 jsonTypeInfo 恒为 null——用户自定义 TState
        // 字段不持久化（saga_data 列写 NULL，重启丢业务字段）。
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
