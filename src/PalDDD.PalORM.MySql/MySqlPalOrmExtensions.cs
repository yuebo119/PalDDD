using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PalDDD.Core.Repository;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.PalORM.MySql;
using PalDDD.Projections;
using PalDDD.Transactions;
using PalORM;
using PalORM.MySql;

namespace PalDDD.PalORM.MySql;

/// <summary>
/// PalORM MySQL DI 扩展 —— 一键注册 7 Store + UnitOfWork + Scoped DataSession。
/// <para>
/// <b>MySQL 特性</b>：不支持 RETURNING（走两步 UPDATE+SELECT）；BulkInsert 检测 local_infile 系统变量自动选 BulkCopy 或多值 INSERT。
/// </para>
/// </summary>
public static class MySqlPalOrmExtensions
{
    /// <summary>
    /// 注册 PalORM MySQL 适配包。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="connectionString">MySQL 连接串（如 "Server=localhost;User Id=user;Password=pass;Database=mydb"）。</param>
    /// <param name="clock">可选时间提供者。</param>
    public static IServiceCollection AddPalOrmMySql(
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
            return DataSession<MySqlProvider>.CreateAsync(opts, default).GetAwaiter().GetResult();
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

        services.AddScoped<IPalOutboxStore, MySqlOutboxStore>();
        services.AddScoped<IInboxStore, MySqlInboxStore>();
        services.AddScoped(typeof(ISagaStateStore<>), typeof(MySqlSagaStateStore<>));
        // ⚠️ Saga Data 陷阱（四轮评审 P2）：此注册的 jsonTypeInfo 恒为 null——用户自定义 TState
        // 字段不持久化（saga_data 列写 NULL，重启丢业务字段）。
        // P2/P3 修复（十七轮）：便捷注册 AddPalOrmMySqlSagaSnapshot<TState> 已提供——
        // 以具体泛型覆盖开放泛型并闭包传入 JsonTypeInfo，需 Saga 快照时调用。
        services.AddScoped<IEventLog, MySqlEventLog>();
        services.AddScoped<IProjectionCheckpointStore, MySqlProjectionCheckpointStore>();
        services.AddScoped<IIdempotencyStore, MySqlIdempotencyStore>();
        services.AddScoped<IUnitOfWork, MySqlPalOrmUnitOfWork>();

        return services;
    }

    /// <summary>
    /// P2/P3 修复（十七轮）：Saga 快照持久化（saga_data 列）便捷注册。
    /// <see cref="AddPalOrmMySql"/> 的开放泛型注册 <c>MySqlSagaStateStore&lt;&gt;</c> 无
    /// <c>JsonTypeInfo</c> 传入通道——jsonTypeInfo 恒 null，TState 业务字段不持久化。
    /// 此方法以具体泛型注册覆盖开放泛型（MS DI 具体泛型优先），闭包构造传入
    /// <paramref name="jsonTypeInfo"/>。
    /// <para>⚠️ <b>不调用则 saga_data 不持久化（重启丢业务字段）</b>——须在
    /// <see cref="AddPalOrmMySql"/> 之后调用（依赖其 DataSession 注册）。</para>
    /// </summary>
    public static IServiceCollection AddPalOrmMySqlSagaSnapshot<TState>(
        this IServiceCollection services,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TState> jsonTypeInfo)
        where TState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        services.AddScoped(typeof(ISagaStateStore<TState>), sp =>
            new MySqlSagaStateStore<TState>(
                sp.GetRequiredService<DataSession<MySqlProvider>>(),
                jsonTypeInfo));

        return services;
    }
}
