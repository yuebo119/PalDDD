using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core.Repository;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.PalORM.PostgreSql;
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

        services.AddSingleton(clock ?? TimeProvider.System);

        services.AddScoped<IPalOutboxStore, PostgreSqlOutboxStore>();
        services.AddScoped<IInboxStore, PostgreSqlInboxStore>();
        services.AddScoped(typeof(ISagaStateStore<>), typeof(PostgreSqlSagaStateStore<>));
        services.AddScoped<IEventLog, PostgreSqlEventLog>();
        services.AddScoped<IProjectionCheckpointStore, PostgreSqlProjectionCheckpointStore>();
        services.AddScoped<IIdempotencyStore, PostgreSqlIdempotencyStore>();
        services.AddScoped<IUnitOfWork, PostgreSqlPalOrmUnitOfWork>();

        return services;
    }
}
