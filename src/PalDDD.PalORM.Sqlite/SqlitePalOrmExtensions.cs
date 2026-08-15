using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core.Repository;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.PalORM.Sqlite;
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
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Scoped DataSession（同步阻塞创建，仅在请求起始）
        services.AddScoped(sp =>
        {
            var opts = DbOptions.Development(connectionString);
            return DataSession<SqliteProvider>.CreateAsync(opts, default).GetAwaiter().GetResult();
        });

        // 时间提供者（默认 System）
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }
        else
        {
            services.AddSingleton(TimeProvider.System);
        }

        // 7 Store + UnitOfWork（全部 Scoped，共享同一 DataSession）
        services.AddScoped<IPalOutboxStore, SqliteOutboxStore>();
        services.AddScoped<IInboxStore, SqliteInboxStore>();
        services.AddScoped(typeof(ISagaStateStore<>), typeof(SqliteSagaStateStore<>));
        services.AddScoped<IEventLog, SqliteEventLog>();
        services.AddScoped<IProjectionCheckpointStore, SqliteProjectionCheckpointStore>();
        services.AddScoped<IIdempotencyStore, SqliteIdempotencyStore>();
        services.AddScoped<IUnitOfWork, SqlitePalOrmUnitOfWork>();

        return services;
    }
}
