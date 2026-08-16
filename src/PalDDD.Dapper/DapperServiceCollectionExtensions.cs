// ─────────────────────────────────────────────────────────────
// 🔧 Dapper DI 注册扩展 — 全 AOT 安全
// ─────────────────────────────────────────────────────────────
//
// 💡 这是什么？
//   ｜ Dapper 适配器层的 DI 注册入口——将 Dapper 实现的 Store 注册到 DI 容器。
//   ｜ 类似于 EF Core 的 services.AddDbContext<...>()，但针对 Dapper。
//   ｜
//   ｜ 注册的内容：
//   ｜   1. DbConnection（Scoped）— 每个请求/作用域一个数据库连接
//   ｜   2. DapperDbType（Singleton）— 数据库类型枚举值，供 Store 选择 SQL 方言
//   ｜   3. DapperOutboxStore / DapperInboxStore / DapperSagaStateStore（Scoped）
//   ｜
//   ｜ 为什么 DbConnection 是 Scoped 而不是 Singleton？
//   ｜   ADO.NET 连接不是线程安全的，不能在多线程间共享。
//   ｜   Scoped 生命周期确保每个请求/事务使用独立连接。
//   ｜   实际的连接池由数据库驱动（Npgsql/MySqlConnector）在底层管理。
//
// ✅ AOT 分析：
//   ✅ Dapper.DefaultTypeMap.MatchNamesWithUnderscores — 纯字符串转换（PascalCase→snake_case），零反射
//   ✅ DapperDbType 枚举（Singleton）— 编译时已知值，零运行时开销
//   ✅ DbConnection（Scoped）— ADO.NET 原生连接，非托管资源，AOT 安全
//   ❌ Dapper.FluentMap（已移除）— 反射 + Expression 编译，AOT 不兼容
//
// 📐 DDD 位置：基础设施层 — DI 注册是组合根的一部分，不涉及领域逻辑。
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using PalDDD.Transactions;
using System.Data.Common;

namespace PalDDD.Dapper;

/// <summary>
/// Dapper 适配器的 DI 注册扩展方法。<br/>
/// 一键注册 DbConnection + DapperDbType + 所有 Dapper Store 实现。
/// </summary>
/// <remarks>
/// 使用示例：
/// <code>
///   // PostgreSQL
///   services.AddPalDapperTransactions(DapperDbType.PostgreSql, connectionString);
///
///   // MySQL（推荐使用 MySqlDataSource）
///   services.AddPalMySqlDataSourceWithStores(connectionString);
///
///   // SQLite（适合测试）
///   services.AddPalDapperTransactions(DapperDbType.Sqlite, "Data Source=:memory:");
/// </code>
/// </remarks>
public static class DapperServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Dapper 适配器层所有服务。<br/>
    /// 包括：数据库连接（Scoped）、数据库类型枚举（Singleton）、
    /// Outbox/Inbox/Saga 的 Dapper 实现（Scoped）。
    /// <para>
    /// ⚠️ <b>与 PalORM 适配器的 outbox status 编码互斥（P2 定案声明）</b>：
    /// Dapper 版 outbox_messages.status 存<b>字符串字面量</b>（'Pending' 等），
    /// PalORM 版存 <b>int 枚举值</b>——同一物理表两者编码互斥，<b>禁止对同一数据库
    /// 同时注册 Dapper 与 PalORM 的 Outbox 实现</b>（互相读不到对方状态）。
    /// 选型后应全程使用同一适配器族。
    /// </para>
    /// </summary>
    /// <param name="services">DI 服务集合</param>
    /// <param name="type">数据库类型（Sqlite / PostgreSql / MySql）</param>
    /// <param name="connectionString">数据库连接字符串</param>
    /// <returns>DI 服务集合（支持链式调用）</returns>
    public static IServiceCollection AddPalDapperTransactions(
        this IServiceCollection services,
        DapperDbType type,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ⚡ 启用 snake_case → PascalCase 自动映射
        // 数据库列名是 snake_case（如 created_at），C# 属性是 PascalCase（如 CreatedAt）
        // MatchNamesWithUnderscores 是纯字符串转换（Split('_') + 拼接），零反射，AOT 安全
        // 替代已移除的 Dapper.FluentMap（依赖 Expression.Compile，AOT 不兼容）
        //
        // ⚠️ 注意：此设置为全局（AppDomain 级别）。如果同一进程中其他库使用 Dapper
        // 且不期望 snake_case 映射，应在各自 Registry 中覆盖 DefaultTypeMap。
        // 此设计选择是因为：库默认使用 snake_case 命名约定，且 Dapper 不提供
        // .NET 中 AppDomain 级别的 local scope 机制——全局设置是当前最佳选择。
        global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        // 📦 注册数据库连接（Scoped — 每个请求一个独立连接）
        // 实际连接池由驱动层管理（Npgsql Pooling=true / MySqlConnector Pooling=true）
        services.AddScoped<DbConnection>(_ =>
            DapperConfiguration.Create(type, connectionString));

        // 📦 注册 DapperDbType（Singleton — 枚举值，不可变）
        // Store 类通过此值在运行时选择对应数据库的 SQL 方言
        services.AddSingleton(typeof(DapperDbType), _ => type);

        // 📦 注册 Dapper Store 实现
        // Scoped 生命周期 — 与 DbConnection 保持一致
        services.AddScoped<IPalOutboxStore, DapperOutboxStore>();
        services.AddScoped<IInboxStore, DapperInboxStore>();

        // ISagaStateStore<TState> 是开放泛型 — 运行时由 DI 自动关闭
        services.AddScoped(typeof(ISagaStateStore<>), typeof(DapperSagaStateStore<>));

        return services;
    }

    /// <summary>
    /// P2/P3 修复（十七轮）：为 Saga 快照持久化（saga_data 列）提供便捷注册。<br/>
    /// <see cref="AddPalDapperTransactions"/> 的开放泛型注册 <c>DapperSagaStateStore&lt;&gt;</c>
    /// 无 <c>JsonTypeInfo</c> 传入通道——jsonTypeInfo 恒 null，用户自定义 TState 的业务字段
    /// 不持久化（saga_data 列写 NULL，重启即丢，仅恢复 SagaId/状态等元数据列）。<br/>
    /// 此方法以<b>具体泛型</b>注册覆盖开放泛型（MS DI 具体泛型优先于开放泛型解析），
    /// 闭包构造传入 <paramref name="jsonTypeInfo"/>（STJ source-generated）。
    /// <para>
    /// ⚠️ <b>不调用此方法则 saga_data 不持久化（重启丢业务字段）</b>——开放泛型默认注册无通道传入
    /// JsonTypeInfo，这是 PalDDD.Dapper/PalORM 双适配器的共同已知限制（PD17 修复为便捷方法暴露）。
    /// </para>
    /// </summary>
    /// <typeparam name="TState">Saga 状态类型（须有无参构造）。</typeparam>
    /// <param name="services">DI 服务集合。</param>
    /// <param name="jsonTypeInfo">STJ source-generated 序列化元数据（<c>[JsonSerializable(typeof(TState))]</c> 产出）。</param>
    public static IServiceCollection AddPalDapperSagaSnapshot<TState>(
        this IServiceCollection services,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TState> jsonTypeInfo)
        where TState : SagaState, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        services.AddScoped(typeof(ISagaStateStore<TState>), sp =>
            new DapperSagaStateStore<TState>(
                sp.GetRequiredService<DbConnection>(),
                transaction: null,
                jsonTypeInfo: jsonTypeInfo,
                timeProvider: sp.GetService<TimeProvider>(),
                dbType: sp.GetRequiredService<DapperDbType>()));

        return services;
    }
}
