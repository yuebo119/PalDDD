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

        services.AddSingleton(clock ?? TimeProvider.System);

        services.AddScoped<IPalOutboxStore, MySqlOutboxStore>();
        services.AddScoped<IInboxStore, MySqlInboxStore>();
        services.AddScoped(typeof(ISagaStateStore<>), typeof(MySqlSagaStateStore<>));
        // ⚠️ Saga Data 陷阱（四轮评审 P2）：此注册的 jsonTypeInfo 恒为 null——用户自定义 TState
        // 字段不持久化（saga_data 列写 NULL）。需要 Saga 快照持久化的应用应手动注册
        // ISagaStateStore<TState> 并传入 JsonTypeInfo<TState>。
        services.AddScoped<IEventLog, MySqlEventLog>();
        services.AddScoped<IProjectionCheckpointStore, MySqlProjectionCheckpointStore>();
        services.AddScoped<IIdempotencyStore, MySqlIdempotencyStore>();
        services.AddScoped<IUnitOfWork, MySqlPalOrmUnitOfWork>();

        return services;
    }
}
